using System.Collections.ObjectModel;
using Avalonia.Threading;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Downloads;
using NinjaLauncher.Minecraft.Java;
using NinjaLauncher.Minecraft.Java.Launch;

namespace NinjaLauncher.Desktop.Services;

/// <summary>Play/repair workflows shared by Home and Library. Owns the live game log.</summary>
public sealed class GameSessionService(
    JavaLaunchService launcher, InstanceService instances, AccountService accounts, JobQueue jobs,
    HttpDownloadService downloader, IActivityLog activity)
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
        var account = await accounts.GetActiveAsync() ?? throw new InvalidOperationException("Sign in with a Microsoft account on the Accounts page before playing.");
        if (IsRunning(instance.Id)) throw new InvalidOperationException($"'{instance.Name}' is already running.");
        // The guard lives inside the job so a Retry from the Downloads page is protected too.
        await jobs.Run($"Play {instance.Name}", async (progress, ct) =>
        {
            lock (_running)
                if (!_running.Add(instance.Id)) throw new InvalidOperationException($"'{instance.Name}' is already running.");
            RunningChanged?.Invoke();
            try
            {
                Dispatcher.UIThread.Post(Log.Clear);
                var session = await accounts.GetSessionAsync(account, ct);
                var current = await instances.GetAsync(instance.Id, ct) ?? throw new InvalidOperationException($"'{instance.Name}' no longer exists.");
                await instances.MarkPlayedAsync(current.Id, ct);
                await activity.AddAsync($"Launched '{current.Name}' as {session.Username}", ct);
                var exit = await launcher.LaunchAsync(current, instances.GetGameDirectory(current),
                    new LaunchSession(session.Username, session.ProfileId, session.MinecraftAccessToken, session.Xuid),
                    progress, AppendLog, ct);
                await activity.AddAsync($"'{current.Name}' exited with status {exit}", ct);
                if (exit != 0) throw new InvalidOperationException($"Minecraft exited with status {exit}. Check the game log for the crash reason.");
            }
            finally
            {
                lock (_running) _running.Remove(instance.Id);
                RunningChanged?.Invoke();
            }
        });
    }

    /// <summary>Throws when an action would touch files a running game holds open.</summary>
    public void EnsureNotRunning(GameInstance instance, string action)
    {
        if (IsRunning(instance.Id)) throw new InvalidOperationException($"Close '{instance.Name}' before you {action}.");
    }

    public Task RepairAsync(GameInstance instance) => jobs.Run($"Repair {instance.Name}", async (progress, ct) =>
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
