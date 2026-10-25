using Microsoft.Extensions.Logging;
using NinjaLauncher.Application.Security;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace NinjaLauncher.Infrastructure.Logging;

public static class LoggingSetup
{
    /// <summary>Rolling file logs (7 days) with token redaction applied to every rendered line.</summary>
    public static ILoggerFactory Create(string logsDirectory, bool verbose)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(new RedactingFormatter(), Path.Combine(logsDirectory, "launcher-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 20_000_000)
            .CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: true));
    }
}

/// <summary>Renders an event, then strips anything that looks like a credential.</summary>
public sealed class RedactingFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner =
        new("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(SecretRedactor.Redact(buffer.ToString()));
    }
}
