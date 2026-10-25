using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Auth;
using NinjaLauncher.Infrastructure.Downloads;
using NinjaLauncher.Infrastructure.Logging;
using NinjaLauncher.Infrastructure.Persistence;
using NinjaLauncher.Minecraft.Bedrock;
using NinjaLauncher.Minecraft.Java;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Minecraft.Java.Runtime;
using NinjaLauncher.Mods.Loaders;
using NinjaLauncher.Mods.Modrinth;
using NinjaLauncher.Platform;

namespace NinjaLauncher.Desktop.Services;

/// <summary>Composition root. Settings are loaded first because they choose paths and logging verbosity.</summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var bootstrapPaths = new AppPaths();
        var db = new NinjaDatabase(bootstrapPaths.DatabasePath);
        var settingsStore = new SqliteSettingsStore(db);
        var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
        var paths = string.IsNullOrWhiteSpace(settings.CustomInstancesDirectory) ? bootstrapPaths : new AppPaths(null, settings.CustomInstancesDirectory);
        var settingsHolder = new SettingsHolder(settingsStore, settings);

        var s = new ServiceCollection();
        s.AddSingleton(LoggingSetup.Create(paths.LogsDirectory, settings.VerboseLogging));
        s.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        s.AddSingleton<IAppPaths>(paths);
        s.AddSingleton(db);
        s.AddSingleton<ISettingsStore>(settingsStore);
        s.AddSingleton(settingsHolder);
        s.AddSingleton<IInstanceRepository, SqliteInstanceRepository>();
        s.AddSingleton<IAccountRepository, SqliteAccountRepository>();
        s.AddSingleton<IActivityLog, SqliteActivityLog>();
        s.AddSingleton(_ => HttpDownloadService.CreateClient());
        s.AddSingleton(sp => new HttpDownloadService(sp.GetRequiredService<HttpClient>(), () => settingsHolder.Current.DownloadConcurrency, sp.GetRequiredService<ILogger<HttpDownloadService>>()));
        s.AddSingleton<IDownloadService>(sp => sp.GetRequiredService<HttpDownloadService>());
        s.AddSingleton<IUrlLauncher, UrlLauncher>();
        s.AddSingleton<IJavaDiscovery, JavaDiscovery>();
        s.AddSingleton(sp => SecretStoreFactory.Create(sp.GetRequiredService<IAppPaths>()));
        s.AddSingleton<IMinecraftAuthenticator>(sp => new MicrosoftAuthenticator(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IUrlLauncher>(),
            () => Environment.GetEnvironmentVariable("NINJA_MSA_CLIENT_ID") is { Length: > 0 } env ? env : settingsHolder.Current.MicrosoftClientId,
            sp.GetRequiredService<ILogger<MicrosoftAuthenticator>>()));
        s.AddSingleton<InstanceService>();
        s.AddSingleton<AccountService>();
        s.AddSingleton<VersionCatalog>();
        s.AddSingleton<GameInstaller>();
        s.AddSingleton<JavaRuntimeManager>();
        s.AddSingleton<JavaLaunchService>();
        s.AddSingleton<BedrockProvider>();
        s.AddSingleton<ModrinthClient>();
        s.AddSingleton<MrpackImporter>();
        s.AddSingleton<ModpackInstaller>();
        s.AddSingleton<ModCompatibilityChecker>();
        s.AddSingleton(sp => new NinjaLauncher.Mods.CurseForge.CurseForgeClient(sp.GetRequiredService<HttpClient>(),
            ct => sp.GetRequiredService<ISecretStore>().GetAsync(NinjaLauncher.Desktop.ViewModels.SettingsViewModel.CurseForgeSecretKey, ct),
            sp.GetRequiredService<IDownloadService>()));
        s.AddSingleton<NinjaLauncher.Mods.CurseForge.CurseForgeModpackInstaller>();
        s.AddSingleton<ILoaderProvider>(sp => MetaProfileLoaderProvider.Fabric(sp.GetRequiredService<IDownloadService>(), sp.GetRequiredService<VersionCatalog>()));
        s.AddSingleton<ILoaderProvider>(sp => MetaProfileLoaderProvider.Quilt(sp.GetRequiredService<IDownloadService>(), sp.GetRequiredService<VersionCatalog>()));
        foreach (var kind in new[] { ModLoaderKind.Forge, ModLoaderKind.NeoForge })
            s.AddSingleton<ILoaderProvider>(sp => new InstallerLoaderProvider(kind, sp.GetRequiredService<IDownloadService>(), sp.GetRequiredService<IAppPaths>(),
                sp.GetRequiredService<VersionCatalog>(), sp.GetRequiredService<GameInstaller>(), sp.GetRequiredService<JavaRuntimeManager>(),
                sp.GetRequiredService<ILogger<InstallerLoaderProvider>>()));
        s.AddSingleton<JobQueue>();
        s.AddSingleton<GameSessionService>();
        s.AddSingleton<UpdateService>();
        return s.BuildServiceProvider();
    }
}

/// <summary>Current settings plus persistence. Updates are serialised and applied to the latest value so concurrent edits don't clobber each other.</summary>
public sealed class SettingsHolder(ISettingsStore store, LauncherSettings initial)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    public LauncherSettings Current { get; private set; } = initial;

    public async Task UpdateAsync(Func<LauncherSettings, LauncherSettings> change)
    {
        await _lock.WaitAsync();
        try
        {
            var next = change(Current);
            await store.SaveAsync(next);
            Current = next;
        }
        finally { _lock.Release(); }
    }
}
