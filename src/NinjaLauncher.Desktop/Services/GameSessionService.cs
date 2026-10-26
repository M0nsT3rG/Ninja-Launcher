using System.Collections.ObjectModel;
using Avalonia.Threading;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Downloads;
using NinjaLauncher.Minecraft.Java;
using NinjaLauncher.Minecraft.Java.Diagnostics;
using NinjaLauncher.Minecraft.Java.Launch;
using NinjaLauncher.Mods.Modrinth;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.Services;

/// <summary>Play/repair workflows shared by Home and Library. Owns the live game log.</summary>
public sealed class GameSessionService(
    JavaLaunchService launcher, InstanceService instances, AccountService accounts, JobQueue jobs,
    HttpDownloadService downloader, IActivityLog activity, ModCompatibilityChecker checker, NotificationService notifications)
{
    private const int MaxLogLines = 3000;
    private readonly HashSet<Guid> _running = [];

    public ObservableCollection<GameLogLine> Log { get; } = [];
    public event Action? RunningChanged;

    public bool IsRunning(Guid instanceId)
    {
        lock (_running) return _running.Contains(instanceId);
    }

    public async Task PlayAsync(GameInstance instance)
    {
        var account = await accounts.GetActiveAsync() ?? throw new InvalidOperationException(L.T("Sign in with a Microsoft account on the Accounts page before playing."));
        if (IsRunning(instance.Id)) throw new InvalidOperationException(L.F("'{0}' is already running.", instance.Name));
        // The guard lives inside the job so a Retry from the Downloads page is protected too.
        await jobs.Run(L.F("Play {0}", instance.Name), async (progress, ct) =>
        {
            lock (_running)
                if (!_running.Add(instance.Id)) throw new InvalidOperationException(L.F("'{0}' is already running.", instance.Name));
            RunningChanged?.Invoke();
            try
            {
                Dispatcher.UIThread.Post(Log.Clear);
                var session = await accounts.GetSessionAsync(account, ct);
                var current = await instances.GetAsync(instance.Id, ct) ?? throw new InvalidOperationException(L.F("'{0}' no longer exists.", instance.Name));
                await instances.MarkPlayedAsync(current.Id, ct);
                await activity.AddAsync($"Launched '{current.Name}' as {session.Username}", ct);
                var started = DateTime.UtcNow.AddSeconds(-5);
                var exit = await launcher.LaunchAsync(current, instances.GetGameDirectory(current),
                    new LaunchSession(session.Username, session.ProfileId, session.MinecraftAccessToken, session.Xuid, session.Offline ? "legacy" : "msa"),
                    progress, AppendLog, ct);
                await activity.AddAsync($"'{current.Name}' exited with status {exit}", ct);
                if (exit != 0)
                {
                    var reason = DescribeCrash(instances.GetGameDirectory(current), started, exit);
                    notifications.Notify(L.F("'{0}' crashed", current.Name), reason);
                    throw new InvalidOperationException(reason);
                }
                notifications.Notify(L.T("Minecraft closed"), L.F("'{0}' closed normally.", current.Name));
            }
            finally
            {
                lock (_running) _running.Remove(instance.Id);
                RunningChanged?.Invoke();
            }
        }, notify: false); // the game reports closing or crashing itself
    }

    /// <summary>
    /// Pre-launch mod check: returns blocking (error) issues, or none when the instance has no mods, the check fails
    /// or Modrinth is unreachable within a few seconds. It never stops a launch on its own; callers ask the user.
    /// </summary>
    public async Task<IReadOnlyList<ModIssue>> CheckModsBeforeLaunchAsync(GameInstance instance)
    {
        if (instance.Loader == ModLoaderKind.None) return [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var issues = await checker.AnalyzeAsync(instance, instances.GetGameDirectory(instance), timeout.Token);
            return issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return []; // offline or slow: don't block playing
        }
    }

    /// <summary>Turns a failed exit into a readable reason using the crash report the game just wrote, if any.</summary>
    private static string DescribeCrash(string gameDirectory, DateTime startedUtc, int exit)
    {
        var fallback = L.F("Minecraft exited with status {0}. Check the game output on Home for the reason.", exit);
        try
        {
            var crash = CrashReports.List(gameDirectory).FirstOrDefault(f => f.WrittenUtc >= startedUtc);
            if (crash is null) return fallback;
            var summary = CrashReports.Summarize(CrashReports.ReadText(crash), crash.IsJvmCrash);
            var cause = summary.RootCause ?? summary.Exception;
            return $"{L.T(summary.Title)}{(cause is null ? "" : $" ({cause})")}. {L.T(summary.Hint ?? "See Library → Crash reports for details.")}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback; // reading the report is best effort; never hide the real failure
        }
    }

    /// <summary>Throws when an action would touch files a running game holds open.</summary>
    public void EnsureNotRunning(GameInstance instance, string action)
    {
        if (IsRunning(instance.Id)) throw new InvalidOperationException(L.F("Close '{0}' before you {1}.", instance.Name, L.T(action)));
    }

    public Task RepairAsync(GameInstance instance) => jobs.Run(L.F("Repair {0}", instance.Name), async (progress, ct) =>
    {
        EnsureNotRunning(instance, "repair it");
        // shortcut: full verification is a downloader-wide flag; a concurrent launch also re-hashes. Make it per-call if that hurts.
        downloader.FullVerification = true;
        var current = await instances.GetAsync(instance.Id, ct) ?? instance;
        try { await launcher.PrepareAsync(current, instances.GetGameDirectory(current), progress, ct); }
        finally { downloader.FullVerification = false; }
        await activity.AddAsync($"Repaired '{instance.Name}'", ct);
    });

    private void AppendLog(GameLogLine line) => Dispatcher.UIThread.Post(() =>
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    });
}
