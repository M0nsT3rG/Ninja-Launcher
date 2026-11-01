using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.CurseForge;
using NinjaLauncher.Mods.Modrinth;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public enum ContentSource { Modrinth, CurseForge }

/// <summary>One search result from either catalogue.</summary>
public sealed record ContentHit(string Title, string Description, string Author, long Downloads, string PageUrl, string? IconUrl,
    ModrinthHit? Modrinth, CurseForgeMod? CurseForge, long? Follows = null, DateTimeOffset? Updated = null)
{
    public static ContentHit From(ModrinthHit h) =>
        new(h.Title, h.Description, h.Author, h.Downloads, $"https://modrinth.com/{h.ProjectType}/{Uri.EscapeDataString(h.Slug)}", h.IconUrl, h, null, h.Follows, h.DateModified);
    public static ContentHit From(CurseForgeMod m) =>
        new(m.Name, m.Summary, string.Join(", ", m.Authors.Select(a => a.Name)), m.DownloadCount, m.PageUrl, m.Logo?.ThumbnailUrl ?? m.Logo?.Url, null, m, null, m.DateModified);
}

/// <summary>A category the user can tick in the filters; every ticked one must match.</summary>
public sealed partial class CategoryFilter(CategoryOption option) : ObservableObject
{
    public CategoryOption Option { get; } = option;
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <param name="Version">Null means any version.</param>
public sealed record VersionChoice(string? Version, string Name)
{
    public override string ToString() => Name;
}

public sealed record LoaderChoice(ModLoaderKind Loader, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class DiscoverViewModel(UiContext ui, ModrinthClient modrinth, ModpackInstaller modpacks, CurseForgeClient curseForge,
    CurseForgeModpackInstaller curseForgePacks, InstanceService instances, JobQueue jobs, IUrlLauncher shell, GameSessionService game,
    NinjaLauncher.Minecraft.Java.Metadata.VersionCatalog catalog) : ViewModelBase
{
    private CancellationTokenSource? _searchCts;
    private bool _rebuildingFilters; // filter lists being refilled must not each start a search

    public ObservableCollection<ContentHit> Results { get; } = [];
    public ObservableCollection<ContentHit> Popular { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public IReadOnlyList<ProjectKind> Kinds { get; } = [ProjectKind.Mod, ProjectKind.Modpack, ProjectKind.ResourcePack, ProjectKind.DataPack, ProjectKind.Shader];
    public ObservableCollection<string> Worlds { get; } = [];
    public ObservableCollection<VersionChoice> GameVersions { get; } = [new(null, L.T("Any version"))];
    public ObservableCollection<CategoryFilter> Categories { get; } = [];
    public LoaderChoice[] Loaders { get; } =
    [
        new(ModLoaderKind.None, L.T("Any loader")), new(ModLoaderKind.Fabric, "Fabric"), new(ModLoaderKind.Forge, "Forge"),
        new(ModLoaderKind.NeoForge, "NeoForge"), new(ModLoaderKind.Quilt, "Quilt"),
    ];
    /// <summary>CurseForge has no follower ranking, so it doesn't offer that order.</summary>
    public IReadOnlyList<SearchSort> Sorts => Source == ContentSource.CurseForge ? Enum.GetValues<SearchSort>().Where(s => s != SearchSort.Follows).ToList() : Enum.GetValues<SearchSort>();
    public string PopularTitle => Source == ContentSource.CurseForge ? L.T("Popular on CurseForge") : L.T("Most followed");
    /// <summary>CurseForge only filters by loader together with a game version.</summary>
    public bool CanPickLoader => ManualFilters && !(Source == ContentSource.CurseForge && GameVersion?.Version is null);
    public IReadOnlyList<int> PageSizes { get; } = [10, 20, 50];
    public bool IsDataPack => Kind == ProjectKind.DataPack;
    public bool HasNoWorlds => IsDataPack && Target is not null && Worlds.Count == 0;
    [ObservableProperty] public partial string? TargetWorld { get; set; }
    public IReadOnlyList<ContentSource> Sources { get; } = [ContentSource.Modrinth, ContentSource.CurseForge];
    public bool NeedsTarget => Kind != ProjectKind.Modpack;
    public bool HasLoaderFilter => Kind is ProjectKind.Mod or ProjectKind.Modpack;
    /// <summary>Version and loader can be picked by hand unless results follow the chosen profile.</summary>
    public bool ManualFilters => !(FilterByInstance && NeedsTarget);
    public bool ShowPopular => Popular.Count > 0;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial ProjectKind Kind { get; set; } = ProjectKind.Mod;
    [ObservableProperty] public partial ContentSource Source { get; set; } = ContentSource.Modrinth;
    [ObservableProperty] public partial GameInstance? Target { get; set; }
    [ObservableProperty] public partial bool FilterByInstance { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial SearchSort Sort { get; set; } = SearchSort.Relevance;
    [ObservableProperty] public partial int PageSize { get; set; } = 20;
    [ObservableProperty] public partial VersionChoice? GameVersion { get; set; }
    [ObservableProperty] public partial LoaderChoice? Loader { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText)), NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(PreviousPageCommand), nameof(NextPageCommand), nameof(LastPageCommand))]
    public partial int Offset { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText)), NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(LastPageCommand))]
    public partial int Total { get; set; }

    private int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public string PageText => $"{Offset / PageSize + 1} / {Pages}";

    public string Warning => L.T("Mods are programs that run with your user’s permissions. Only install content you trust, whichever site it comes from.");

    public override async Task OnShownAsync()
    {
        var keep = Target?.Id;
        Instances.Clear();
        foreach (var i in await instances.ListAsync()) Instances.Add(i);
        Target = Instances.FirstOrDefault(i => i.Id == keep) ?? Instances.FirstOrDefault(i => i.Loader != ModLoaderKind.None) ?? Instances.FirstOrDefault();
        RefreshWorlds();
        GameVersion ??= GameVersions[0];
        Loader ??= Loaders[0];
        if (GameVersions.Count == 1) _ = ui.RunAsync(LoadGameVersionsAsync);
        if (Categories.Count == 0) await LoadCategoriesAsync();
        // CurseForge data may not be kept around (API terms §3.1), so its results are always re-queried.
        if (Results.Count == 0 || Source == ContentSource.CurseForge) await SearchAsync();
    }

    private async Task LoadGameVersionsAsync()
    {
        var releases = (await catalog.GetManifestAsync()).Versions.Where(v => v.Type == "release").Take(40);
        foreach (var v in releases) GameVersions.Add(new VersionChoice(v.Id, v.Id));
    }

    private async Task LoadCategoriesAsync()
    {
        var (source, kind) = (Source, Kind);
        IReadOnlyList<CategoryOption> options = [];
        try
        {
            options = source == ContentSource.CurseForge
                ? await curseForge.HasKeyAsync(CancellationToken.None) ? await curseForge.GetCategoriesAsync(kind, CancellationToken.None) : []
                : await modrinth.GetCategoriesAsync(kind, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException) { } // filters are optional
        if ((source, kind) != (Source, Kind)) return; // switched meanwhile; that switch loads its own
        _rebuildingFilters = true;
        Categories.Clear();
        foreach (var o in options)
        {
            var filter = new CategoryFilter(o);
            filter.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CategoryFilter.IsChecked) && !_rebuildingFilters) Restart(); };
            Categories.Add(filter);
        }
        _rebuildingFilters = false;
    }

    partial void OnKindChanged(ProjectKind value)
    {
        OnPropertyChanged(nameof(NeedsTarget));
        OnPropertyChanged(nameof(IsDataPack));
        OnPropertyChanged(nameof(HasLoaderFilter));
        OnPropertyChanged(nameof(ManualFilters));
        RefreshWorlds();
        ReloadForNewListing();
    }
    partial void OnSourceChanged(ContentSource value)
    {
        OnPropertyChanged(nameof(Sorts));
        OnPropertyChanged(nameof(PopularTitle));
        OnPropertyChanged(nameof(CanPickLoader));
        if (value == ContentSource.CurseForge && Sort == SearchSort.Follows)
        {
            _rebuildingFilters = true; // the reload below searches once
            Sort = SearchSort.Relevance;
            _rebuildingFilters = false;
        }
        ReloadForNewListing();
    }

    /// <summary>
    /// Categories belong to one source and kind: they are dropped and the running search is cancelled straight away,
    /// so nothing searches with the old ones (a Modrinth category is not a CurseForge id) while the new list loads.
    /// </summary>
    private void ReloadForNewListing()
    {
        _searchCts?.Cancel();
        _rebuildingFilters = true;
        Categories.Clear();
        _rebuildingFilters = false;
        _ = ui.RunAsync(async () => { await LoadCategoriesAsync(); Offset = 0; await SearchAsync(); });
    }
    partial void OnTargetChanged(GameInstance? value)
    {
        RefreshWorlds();
        if (FilterByInstance) Restart();
    }

    /// <summary>Data packs belong to a world, so their install target is a world of the selected instance.</summary>
    private void RefreshWorlds()
    {
        var keep = TargetWorld;
        Worlds.Clear();
        if (Target is { } t) foreach (var w in instances.ListWorlds(t)) Worlds.Add(w);
        TargetWorld = Worlds.Contains(keep ?? "") ? keep : Worlds.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoWorlds));
    }
    partial void OnFilterByInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(ManualFilters));
        OnPropertyChanged(nameof(CanPickLoader));
        Restart();
    }
    partial void OnSortChanged(SearchSort value) => Restart();
    partial void OnPageSizeChanged(int value) => Restart();
    partial void OnGameVersionChanged(VersionChoice? value)
    {
        OnPropertyChanged(nameof(CanPickLoader));
        if (ManualFilters) Restart();
    }
    partial void OnLoaderChanged(LoaderChoice? value) { if (ManualFilters && HasLoaderFilter) Restart(); }

    private void Restart()
    {
        if (_rebuildingFilters) return;
        _ = ui.RunAsync(() => { Offset = 0; return SearchAsync(); });
    }

    [RelayCommand]
    private Task Search() => ui.RunAsync(() => { Offset = 0; return SearchAsync(); });

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task FirstPage() => ui.RunAsync(() => { Offset = 0; return SearchAsync(); });

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPage() => ui.RunAsync(() => { Offset = Math.Max(0, Offset - PageSize); return SearchAsync(); });
    private bool CanGoBack() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPage() => ui.RunAsync(() => { Offset += PageSize; return SearchAsync(); });
    private bool CanGoNext() => Offset + PageSize < Total;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task LastPage() => ui.RunAsync(() => { Offset = (Pages - 1) * PageSize; return SearchAsync(); });

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var (source, kind, query, offset, sort, size) = (Source, Kind, Query, Offset, Sort, PageSize);
        IsBusy = true;
        try
        {
            var profile = ManualFilters ? null : Target;
            var version = profile is not null ? profile.MinecraftVersion : GameVersion?.Version;
            var loader = profile?.Loader ?? (HasLoaderFilter ? Loader?.Loader : null) ?? ModLoaderKind.None;
            var categories = Categories.Where(c => c.IsChecked).Select(c => c.Option.Id).ToList();
            async Task<(List<ContentHit> Hits, int Total)> Fetch(SearchSort order, int skip, int take)
            {
                if (source == ContentSource.CurseForge)
                {
                    var c = await curseForge.SearchAsync(query, kind, version, loader, skip, take, ct, order, categories);
                    return (c.Mods.Select(ContentHit.From).ToList(), c.TotalCount);
                }
                var m = await modrinth.SearchAsync(query, kind, version, loader, skip, take, ct, order, categories);
                return (m.Hits.Select(ContentHit.From).ToList(), m.TotalHits);
            }

            if (source == ContentSource.CurseForge && !await curseForge.HasKeyAsync(ct))
            {
                Results.Clear();
                Popular.Clear();
                OnPropertyChanged(nameof(ShowPopular));
                Total = 0;
                Summary = L.T("CurseForge needs your own API key: add it in Settings → CurseForge.");
                return;
            }
            var (hits, total) = await Fetch(sort, offset, size);
            // The "most followed" row only on the first page of an unfiltered-by-text search, where it helps browsing.
            var popular = query.Length == 0 && offset == 0 ? (await Fetch(SearchSort.Follows, 0, 10)).Hits : [];
            if (ct.IsCancellationRequested) return;
            Results.Clear();
            foreach (var hit in hits) Results.Add(hit);
            Popular.Clear();
            foreach (var hit in popular) Popular.Add(hit);
            OnPropertyChanged(nameof(ShowPopular));
            Total = total;
            OnPropertyChanged(nameof(PageText)); // also when only the page size changed
            NextPageCommand.NotifyCanExecuteChanged();
            LastPageCommand.NotifyCanExecuteChanged();
            Summary = Total == 0 ? L.T("No results.")
                : profile is null ? L.F("{0:N0} results on {1}", Total, source)
                : L.F("{0:N0} results for {1} (Minecraft {2} · {3})", Total, profile.Name, profile.MinecraftVersion,
                    profile.Loader == ModLoaderKind.None ? L.T("Vanilla") : profile.Loader.ToString());
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task Install(ContentHit hit) => ui.RunAsync(async () =>
    {
        var kind = Kind;
        if (kind == ProjectKind.Modpack)
        {
            if (!await ui.ConfirmAsync(L.F("Create a new profile from the modpack '{0}'? Its mods, loader and settings are downloaded from {1}.", hit.Title, hit.CurseForge is null ? "Modrinth" : "CurseForge") + "\n\n" + Warning)) return;
            GameInstance? created = null;
            IReadOnlyList<BlockedFile> blocked = [];
            await jobs.Run(L.F("Install modpack {0}", hit.Title), async (progress, ct) =>
            {
                if (hit.CurseForge is { } pack) (created, blocked) = await curseForgePacks.InstallFromCurseForgeAsync(pack, progress, ct);
                else created = await modpacks.InstallFromModrinthAsync(hit.Modrinth!, progress, ct);
            });
            ui.Info(L.F("Created profile '{0}'. Find it in Profiles.", created!.Name));
            await OfferManualDownloadsAsync(ui, shell, blocked, instances.GetGameDirectory(created));
            return;
        }

        var target = Target ?? throw new InvalidOperationException(L.T("Create or select a profile to install into."));
        game.EnsureNotRunning(target, "install mods");
        var world = kind == ProjectKind.DataPack
            ? TargetWorld ?? throw new InvalidOperationException(L.T("Data packs are added to a world. Play this profile once and create a world, then pick it here."))
            : null;
        var into = world is null ? target.Name : L.F("the world '{0}' of '{1}'", world, target.Name);
        if (!await ui.ConfirmAsync(L.F("Install '{0}' and its required dependencies into '{1}'?", hit.Title, into) + "\n\n" + Warning)) return;
        IReadOnlyList<InstalledContent> installed = [];
        IReadOnlyList<BlockedFile> manual = [];
        await jobs.Run(L.F("Install {0} → {1}", hit.Title, target.Name), async (progress, ct) =>
        {
            progress.Report(new(L.F("Resolving {0}", hit.Title), 10, 0));
            var gameDir = instances.GetGameDirectory(target);
            if (hit.CurseForge is { } mod) (installed, manual) = await curseForge.InstallAsync(mod, kind, target, gameDir, ct, world);
            else installed = await modrinth.InstallAsync(hit.Modrinth!, kind, target, gameDir, ct, world);
        });
        if (installed.Count > 0) ui.Info(L.F("Installed {0}", string.Join(", ", installed.Select(i => i.FileName))));
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
        if (!await ui.ConfirmAsync(L.F("{0} file(s) can only be downloaded on the CurseForge website because their authors don’t allow other apps to download them:", blocked.Count) + "\n\n" + list + "\n\n" +
            L.F("The full list with links is saved in manual-downloads.txt in the profile folder. Open the first {0} download page(s) and the profile folder now?", Math.Min(8, blocked.Count)))) return;
        foreach (var b in blocked.Take(8)) shell.Open(new Uri(b.PageUrl));
        Directory.CreateDirectory(gameDirectory);
        shell.OpenDirectory(gameDirectory);
    }

    [RelayCommand]
    private void OpenPage(ContentHit hit) => shell.Open(new Uri(hit.PageUrl));
}
