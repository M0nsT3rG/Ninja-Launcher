using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Minecraft.Java.Install;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.Services;

public delegate Task JobWork(IProgress<InstallProgress> progress, CancellationToken ct);

/// <summary>One long-running operation shown on the Downloads page.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    private readonly JobQueue _queue;
    private readonly JobWork _work;
    private CancellationTokenSource _cts = new();

    private readonly bool _notifySuccess;

    internal JobViewModel(JobQueue queue, string title, JobWork work, bool notifySuccess)
    {
        _queue = queue;
        Title = title;
        _work = work;
        _notifySuccess = notifySuccess;
    }

    public string Title { get; }
    [ObservableProperty] public partial string Status { get; set; } = L.T("Queued");
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Speed { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(RetryCommand))] public partial bool IsRunning { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetryCommand))] public partial bool CanRetry { get; set; }

    internal Task Completion { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts.Cancel();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => Start();

    internal Task Start()
    {
        _cts = new CancellationTokenSource();
        Error = null;
        CanRetry = false;
        IsRunning = true;
        Status = L.T("Starting");
        var progress = new Progress<InstallProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Status = L.T(p.Stage);
            Percent = Math.Clamp(p.Percent, 0, 100);
            Speed = p.BytesPerSecond > 0 ? $"{p.BytesPerSecond / 1024 / 1024:0.0} MB/s" : "";
        }));
        Completion = Run(progress);
        return Completion;
    }

    private async Task Run(IProgress<InstallProgress> progress)
    {
        try
        {
            await Task.Run(() => _work(progress, _cts.Token));
            Status = L.T("Completed");
            Percent = 100;
            if (_notifySuccess) _queue.Notifications.Notify(L.T("Finished"), Title);
        }
        catch (OperationCanceledException)
        {
            Status = L.T("Cancelled");
            CanRetry = true;
        }
        catch (Exception e)
        {
            Status = L.T("Failed");
            Error = L.T(SecretRedactor.Redact(e.Message));
            CanRetry = true;
            _queue.Notifications.Notify(L.F("Failed: {0}", Title), Error);
            _queue.Logger.LogError(e, "Job '{Title}' failed", Title);
            throw;
        }
        finally
        {
            IsRunning = false;
            Speed = "";
        }
    }
}

public sealed class JobQueue(ILogger<JobQueue> logger, NotificationService notifications)
{
    internal ILogger Logger => logger;
    internal NotificationService Notifications => notifications;
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>Starts a job on a background thread; the returned task faults if the job fails.</summary>
    /// <param name="notifySuccess">Send a desktop notification when it succeeds; failures always notify (only while the launcher is in the background).</param>
    public Task Run(string title, JobWork work, bool notifySuccess = true)
    {
        var job = new JobViewModel(this, title, work, notifySuccess);
        Jobs.Insert(0, job);
        while (Jobs.Count > 50 && Jobs[^1] is { IsRunning: false } old) Jobs.Remove(old);
        return job.Start();
    }

    public void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => !j.IsRunning).ToList()) Jobs.Remove(j);
    }
}
