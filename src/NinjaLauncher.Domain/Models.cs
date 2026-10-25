namespace NinjaLauncher.Domain;

public enum GameEdition { Java, Bedrock }

public enum ModLoaderKind { None, Fabric, Quilt, Forge, NeoForge }

public enum ThemeMode { Dark, Light, System }

/// <summary>An isolated Minecraft Java installation profile. Mutable game data lives in its own directory.</summary>
public sealed record GameInstance
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string MinecraftVersion { get; init; }
    public ModLoaderKind Loader { get; init; } = ModLoaderKind.None;
    public string? LoaderVersion { get; init; }
    /// <summary>Version JSON id to launch (a loader profile inheriting the vanilla version); null means <see cref="MinecraftVersion"/>.</summary>
    public string? LaunchVersionId { get; init; }
    /// <summary>Explicit java executable; null means "managed runtime chosen from version metadata".</summary>
    public string? JavaPath { get; init; }
    public int MinMemoryMb { get; init; } = 512;
    public int MaxMemoryMb { get; init; } = 4096;
    public string ExtraJvmArguments { get; init; } = "";
    public string? IconKey { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayedUtc { get; init; }
}

public enum AccountKind { Microsoft, Offline }

/// <summary>A signed-in Minecraft profile. Secrets are never stored on this record.</summary>
public sealed record MinecraftAccount
{
    /// <summary>Offline profiles exist only after a Microsoft account that owns the game has signed in on this launcher.</summary>
    public AccountKind Kind { get; init; } = AccountKind.Microsoft;
    /// <summary>Minecraft profile UUID without dashes.</summary>
    public required string ProfileId { get; init; }
    public required string Username { get; init; }
    public string? Xuid { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LauncherSettings
{
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;
    public string AccentColor { get; init; } = "#B44CFF";
    public string Language { get; init; } = "en";
    public int DefaultMaxMemoryMb { get; init; } = 4096;
    public int DownloadConcurrency { get; init; } = 8;
    public string? CustomInstancesDirectory { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public bool VerboseLogging { get; init; }
    public GameEdition LastEdition { get; init; } = GameEdition.Java;
    public Guid? SelectedInstanceId { get; init; }
    /// <summary>Azure application (client) ID approved by Mojang for Minecraft Services. Empty disables sign-in.</summary>
    public string MicrosoftClientId { get; init; } = "";
}

public sealed record ActivityEntry(DateTimeOffset TimestampUtc, string Message);
