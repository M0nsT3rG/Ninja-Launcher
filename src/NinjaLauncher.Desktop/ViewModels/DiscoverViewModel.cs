using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.CurseForge;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.Desktop.ViewModels;

public enum ContentSource { Modrinth, CurseForge }

/// <summary>One search result from either catalogue.</summary>
public sealed record ContentHit(string Title, string Description, string Author, long Downloads, string PageUrl, string? IconUrl,
    ModrinthHit? Modrinth, CurseForgeMod? CurseForge)
{
    public static ContentHit From(ModrinthHit h) =>
        new(h.Title, h.Description, h.Author, h.Downloads, $"https://modrinth.com/{h.ProjectType}/{Uri.EscapeDataString(h.Slug)}", h.IconUrl, h, null);
    public static ContentHit From(CurseForgeMod m) =>
        new(m.Name, m.Summary, string.Join(", ", m.Authors.Select(a => a.Name)), m.DownloadCount, m.PageUrl, m.Logo?.ThumbnailUrl ?? m.Logo?.Url, null, m);
}

public sealed partial class DiscoverViewModel(UiContext ui, ModrinthClient modrinth, ModpackInstaller modpacks, CurseForgeClient curseForge,
    CurseForgeModpackInstaller curseForgePacks, InstanceService instances, JobQueue jobs, IUrlLauncher shell) : ViewModelBase
{
    private const int PageSize = 20;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<ContentHit> Results { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public IReadOnlyList<ProjectKind> Kinds { get; } = [ProjectKind.Mod, ProjectKind.Modpack, ProjectKind.Shader, ProjectKind.ResourcePack];
    public IReadOnlyList<ContentSource> Sources { get; } = [ContentSource.Modrinth, ContentSource.CurseForge];
    public bool NeedsTarget => Kind != ProjectKind.Modpack;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial ProjectKind Kind { get; set; } = ProjectKind.Mod;
    [ObservableProperty] public partial ContentSource Source { get; set; } = ContentSource.Modrinth;
    [ObservableProperty] public partial GameInstance? Target { get; set; }
    [ObservableProperty] public partial bool FilterByInstance { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))] public partial int Offset { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextPageCommand))] public partial int Total { get; set; }

    public string Warning => "Mods are programs that run with your user's permissions. Only install content you trust, whichever site it comes from.";

    public override async Task OnShownAsync()
    {
        var keep = Target?.Id;
        Instances.Clear();
        foreach (var i in await instances.ListAsync()) Instances.Add(i);
        Target = Instances.FirstOrDefault(i => i.Id == keep) ?? Instances.FirstOrDefault(i => i.Loader != ModLoaderKind.None) ?? Instances.FirstOrDefault();
        // CurseForge data may not be kept around (API terms §3.1), so its results are always re-queried.
        if (Results.Count == 0 || Source == ContentSource.CurseForge) await SearchAsync();
    }

    partial void OnKindChanged(ProjectKind value)
    {
        OnPropertyChanged(nameof(NeedsTarget));
        Restart();
    }
    partial void OnSourceChanged(ContentSource value) => Restart();
    partial void OnTargetChanged(GameInstance? value) { if (FilterByInstance) Restart(); }
    partial void OnFilterByInstanceChanged(bool value) => Restart();

    private void Restart() => _ = ui.RunAsync(() => { Offset = 0; return SearchAsync(); });

    [RelayCommand]
    private Task Search() => ui.RunAsync(() => { Offset = 0; return SearchAsync(); });

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPage() => ui.RunAsync(() => { Offset = Math.Max(0, Offset - PageSize); return SearchAsync(); });
    private bool CanGoBack() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPage() => ui.RunAsync(() => { Offset += PageSize; return SearchAsync(); });
    private bool CanGoNext() => Offset + PageSize < Total;

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var (source, kind, query, offset) = (Source, Kind, Query, Offset);
        IsBusy = true;
        try
        {
            var filter = FilterByInstance && NeedsTarget ? Target : null;
            List<ContentHit> hits;
            int total;
            if (source == ContentSource.CurseForge)
            {
                if (!await curseForge.HasKeyAsync(ct))
                {
                    Results.Clear();
                    Total = 0;
                    Summary = "CurseForge needs your own API key: add it in Settings → CurseForge.";
                    return;
                }
                var r = await curseForge.SearchAsync(query, kind, filter?.MinecraftVersion, filter?.Loader ?? ModLoaderKind.None, offset, PageSize, ct);
                (hits, total) = (r.Mods.Select(ContentHit.From).ToList(), r.TotalCount);
            }
            else
            {
                var r = await modrinth.SearchAsync(query, kind, filter?.MinecraftVersion, filter?.Loader ?? ModLoaderKind.None, offset, PageSize, ct);
                (hits, total) = (r.Hits.Select(ContentHit.From).ToList(), r.TotalHits);
            }
            if (ct.IsCancellationRequested) return;
            Results.Clear();
            foreach (var hit in hits) Results.Add(hit);
            Total = total;
            Summary = Total == 0 ? "No results." : $"{offset + 1}–{offset + Results.Count} of {Total:N0} on {source}" +
                (filter is null ? "" : $", compatible with {filter.MinecraftVersion} {(kind == ProjectKind.Mod ? filter.Loader.ToString() : "")}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task Install(ContentHit hit) => ui.RunAsync(async () =>
    {
        var kind = Kind;
        if (kind == ProjectKind.Modpack)
        {
            if (!await ui.ConfirmAsync($"Create a new instance from the modpack '{hit.Title}'? Its mods, loader and settings are downloaded from {(hit.CurseForge is null ? "Modrinth" : "CurseForge")}.\n\n{Warning}")) return;
            GameInstance? created = null;
            IReadOnlyList<BlockedFile> blocked = [];
            await jobs.Run($"Install modpack {hit.Title}", async (progress, ct) =>
            {
                if (hit.CurseForge is { } pack) (created, blocked) = await curseForgePacks.InstallFromCurseForgeAsync(pack, progress, ct);
                else created = await modpacks.InstallFromModrinthAsync(hit.Modrinth!, progress, ct);
            });
            ui.Info($"Created instance '{created!.Name}'. Find it in the Library.");
            await OfferManualDownloadsAsync(ui, shell, blocked, instances.GetGameDirectory(created));
            return;
        }

        var target = Target ?? throw new InvalidOperationException("Create or select an instance to install into.");
        if (!await ui.ConfirmAsync($"Install '{hit.Title}' and its required dependencies into '{target.Name}'?\n\n{Warning}")) return;
        IReadOnlyList<InstalledContent> installed = [];
        IReadOnlyList<BlockedFile> manual = [];
        await jobs.Run($"Install {hit.Title} → {target.Name}", async (progress, ct) =>
        {
            progress.Report(new($"Resolving {hit.Title}", 10, 0));
            var gameDir = instances.GetGameDirectory(target);
            if (hit.CurseForge is { } mod) (installed, manual) = await curseForge.InstallAsync(mod, kind, target, gameDir, ct);
            else installed = await modrinth.InstallAsync(hit.Modrinth!, kind, target, gameDir, ct);
        });
        if (installed.Count > 0) ui.Info($"Installed {string.Join(", ", installed.Select(i => i.FileName))}");
        await OfferManualDownloadsAsync(ui, shell, manual, instances.GetGameDirectory(target));
    });

    /// <summary>
    /// Authors can forbid third-party downloads on CurseForge. Those files are never fetched behind their back:
    /// the user downloads them on curseforge.com and drops them into the folder we open.
    /// </summary>
    internal static async Task OfferManualDownloadsAsync(UiContext ui, IUrlLauncher shell, IReadOnlyList<BlockedFile> blocked, string gameDirectory)
    {
        if (blocked.Count == 0) return;
        // The full list is always saved, so nothing is lost when only the first pages are opened automatically.
        var report = Path.Combine(gameDirectory, "manual-downloads.txt");
        await File.WriteAllLinesAsync(report, ["Download each file from its page and put it in the folder shown:", "",
            .. blocked.Select(b => $"{b.ModName}: {b.FileName}\n  page:   {b.PageUrl}\n  folder: {b.TargetFolder}/\n")]);
        var list = string.Join("\n", blocked.Select(b => $"• {b.ModName} → {b.FileName} (into {b.TargetFolder}/)"));
        if (!await ui.ConfirmAsync($"{blocked.Count} file(s) can only be downloaded on the CurseForge website because their authors don't allow other apps to download them:\n\n{list}\n\nThe full list with links is saved in manual-downloads.txt in the instance folder. Open the first {Math.Min(8, blocked.Count)} download page(s) and the instance folder now?")) return;
        foreach (var b in blocked.Take(8)) shell.Open(new Uri(b.PageUrl));
        Directory.CreateDirectory(gameDirectory);
        shell.OpenDirectory(gameDirectory);
    }

    [RelayCommand]
    private void OpenPage(ContentHit hit) => shell.Open(new Uri(hit.PageUrl));
}
