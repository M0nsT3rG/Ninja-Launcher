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
        lock (_running)
            if (!_running.Add(instance.Id)) throw new InvalidOperationException($"'{instance.Name}' is already running.");
        RunningChanged?.Invoke();
        Dispatcher.UIThread.Post(Log.Clear);
        try
        {
            await jobs.Run($"Play {instance.Name}", async (progress, ct) =>
            {
                var session = await accounts.GetSessionAsync(account, ct);
                var gameDir = instances.GetGameDirectory(instance);
                await instances.MarkPlayedAsync(instance.Id, ct);
                await activity.AddAsync($"Launched '{instance.Name}' as {session.Username}", ct);
                var exit = await launcher.LaunchAsync(instance, gameDir,
                    new LaunchSession(session.Username, session.ProfileId, session.MinecraftAccessToken, session.Xuid),
                    progress, AppendLog, ct);
                await activity.AddAsync($"'{instance.Name}' exited with status {exit}", ct);
                if (exit != 0) throw new InvalidOperationException($"Minecraft exited with status {exit}. Check the game log for the crash reason.");
            });
        }
        finally
        {
            lock (_running) _running.Remove(instance.Id);
            RunningChanged?.Invoke();
        }
    }

    public Task RepairAsync(GameInstance instance) => jobs.Run($"Repair {instance.Name}", async (progress, ct) =>
    {
        // shortcut: full verification is a downloader-wide flag; a concurrent launch also re-hashes. Make it per-call if that hurts.
        downloader.FullVerification = true;
        try { await launcher.PrepareAsync(instance, instances.GetGameDirectory(instance), progress, ct); }
        finally { downloader.FullVerification = false; }
        await activity.AddAsync($"Repaired '{instance.Name}'", ct);
    });

    private void AppendLog(GameLogLine line) => Dispatcher.UIThread.Post(() =>
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    });
}
