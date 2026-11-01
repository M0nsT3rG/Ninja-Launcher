using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

/// <param name="Id">Null for "no cape".</param>
/// <param name="Preview">The part of the cape seen from behind.</param>
public sealed record CapeChoice(string? Id, string Name, IImage? Preview, Bitmap? Texture);

/// <summary>Changes the signed-in account's Java Edition skin and cape through Minecraft Services.</summary>
public sealed partial class WardrobeViewModel(UiContext ui, AccountService accounts, SkinService skins) : ViewModelBase
{
    private MinecraftAccount? _account;
    private PlayerLook? _look;
    private byte[]? _pending;
    private bool _applying;
    private int _generation; // bumped on every (re)load; results for an older account or load are dropped

    public ObservableCollection<CapeChoice> Capes { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract))] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial string AccountName { get; set; } = "";
    [ObservableProperty] public partial Bitmap? Skin { get; set; }
    [ObservableProperty] public partial Bitmap? Cape { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplySkinCommand))] public partial bool Slim { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplySkinCommand))] public partial string? PendingName { get; set; }
    [ObservableProperty] public partial CapeChoice? SelectedCape { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract))] public partial bool IsBusy { get; set; }

    /// <summary>One request at a time: the controls are off while anything is loading or saving.</summary>
    public bool CanInteract => CanEdit && !IsBusy;
    [ObservableProperty] public partial double PreviewYaw { get; set; } = 25;

    [RelayCommand] private void TurnAround() => PreviewYaw = (PreviewYaw + 180) % 360;

    public bool Classic { get => !Slim; set => Slim = !value; }
    partial void OnSlimChanged(bool value) => OnPropertyChanged(nameof(Classic));

    public override async Task OnShownAsync()
    {
        var generation = ++_generation;
        CanEdit = false;
        Show(null); // never show one account's look while another loads
        var account = _account = await accounts.GetActiveAsync();
        AccountName = account?.Username ?? "";
        var microsoft = account is { Kind: AccountKind.Microsoft };
        Message = account is null ? L.T("Sign in with a Microsoft account on the Accounts page to change your skin and cape.")
            : microsoft ? "" : L.T("Offline profiles have no Minecraft profile, so their skin and cape can’t be changed. Switch to a Microsoft account.");
        if (!microsoft) return;
        await RunAsync(account!, generation, () => skins.GetAsync(account!, refresh: true)!, keepSkinEdits: false);
        if (generation == _generation && _look is not null) CanEdit = true;
    }

    /// <summary>Runs one load or change and shows its result, unless the page moved on to another load meanwhile.</summary>
    private async Task RunAsync(MinecraftAccount account, int generation, Func<Task<PlayerLook>> request, bool keepSkinEdits)
    {
        IsBusy = true;
        try
        {
            var look = await request();
            if (generation == _generation && _account == account) Show(look, keepSkinEdits);
        }
        finally { if (generation == _generation) IsBusy = false; }
    }

    private void Show(PlayerLook? look, bool keepSkinEdits = false)
    {
        _applying = true;
        try
        {
            _look = look;
            if (!keepSkinEdits || _pending is null && look is not null && Slim == look.Slim)
                (_pending, PendingName, Skin, Slim) = (null, null, look?.Skin, look?.Slim ?? false);
            ApplySkinCommand.NotifyCanExecuteChanged();
            Capes.Clear();
            Capes.Add(new CapeChoice(null, L.T("No cape"), null, null));
            if (look is not null)
                foreach (var c in look.Profile.Capes)
                    if (look.Capes.TryGetValue(c.Id, out var texture))
                    {
                        var s = texture.PixelSize.Width / 64; // capes are 64×32, or a multiple of it
                        Capes.Add(new CapeChoice(c.Id, c.Alias, new CroppedBitmap(texture, new PixelRect(s, s, 10 * s, 16 * s)), texture));
                    }
            SelectedCape = Capes.FirstOrDefault(c => c.Id == look?.Profile.ActiveCape?.Id) ?? Capes[0];
            Cape = SelectedCape.Texture;
        }
        finally { _applying = false; }
    }

    [RelayCommand]
    private Task ChooseSkin() => ui.RunAsync(async () =>
    {
        var file = await ui.PickFileAsync("Skin", "*.png");
        if (file is null) return;
        var bytes = new FileInfo(file).Length <= NinjaLauncher.Infrastructure.Auth.MinecraftProfileClient.MaxSkinBytes ? await File.ReadAllBytesAsync(file) : [];
        Skin = SkinService.DecodeSkin(bytes); // throws with the reason when Minecraft would reject it
        (_pending, PendingName) = (bytes, Path.GetFileName(file));
    });

    private bool CanApply() => PendingName is not null || (_look is not null && Slim != _look.Slim);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplySkin() => ui.RunAsync(async () =>
    {
        if (_account is not { } account || (_pending ?? _look?.SkinPng) is not { } png) return;
        var slim = Slim;
        await RunAsync(account, _generation, () => skins.UploadSkinAsync(account, png, slim), keepSkinEdits: false);
        ui.Info(L.T("Skin saved. It shows in game the next time you join a world or server."));
    });

    [RelayCommand]
    private void DiscardSkin() => Show(_look);

    [RelayCommand]
    private Task ResetSkin() => ui.RunAsync(async () =>
    {
        if (_account is not { } account || !await ui.ConfirmAsync(L.T("Go back to the default skin Minecraft gives your account? Your current skin is not saved anywhere else."))) return;
        await RunAsync(account, _generation, () => skins.ResetSkinAsync(account), keepSkinEdits: false);
    });

    partial void OnSelectedCapeChanged(CapeChoice? value)
    {
        if (_applying || value is null || _account is not { } account || value.Id == _look?.Profile.ActiveCape?.Id) return;
        Cape = value.Texture;
        if (value.Id is not null) PreviewYaw = 155; // show the back, where the cape is
        var generation = _generation;
        _ = ui.RunAsync(async () =>
        {
            // An unsaved skin stays in the preview: changing the cape doesn't touch it.
            try { await RunAsync(account, generation, () => skins.SetCapeAsync(account, value.Id), keepSkinEdits: true); }
            catch { if (generation == _generation) Show(_look, keepSkinEdits: true); throw; } // put the selection back, then report it
        });
    }
}
