using System.Diagnostics;
using System.Text.RegularExpressions;
using NinjaLauncher.Application.Security;

namespace NinjaLauncher.Minecraft.Java.Launch;

public enum GameLogLevel { Info, Warn, Error, Debug }

public sealed record GameLogLine(GameLogLevel Level, string Text);

/// <summary>Runs the game with an argv array, streams its output (log4j XML is unwrapped) and reports the exit code.</summary>
public static partial class GameProcess
{
    public static async Task<int> RunAsync(string javaPath, IReadOnlyList<string> arguments, string workingDirectory,
        Action<GameLogLine> onLog, Action<int>? onStarted, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var parser = new Log4jStreamParser(onLog);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) parser.Feed(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLog(new GameLogLine(GameLogLevel.Error, SecretRedactor.Redact(e.Data))); };
        if (!process.Start()) throw new InvalidOperationException("The Java process failed to start.");
        onStarted?.Invoke(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit(); // flush remaining output events
        return process.ExitCode;
    }

    /// <summary>Formats the command for diagnostics with the access token removed.</summary>
    public static string DescribeCommand(string javaPath, IReadOnlyList<string> arguments)
    {
        var redacted = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var hide = i > 0 && arguments[i - 1] is "--accessToken" or "--session";
            redacted.Add(hide ? "[REDACTED]" : SecretRedactor.Redact(arguments[i]));
        }
        return javaPath + " " + string.Join(' ', redacted.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    /// <summary>Minecraft's log4j config prints XML events; this turns them back into plain lines.</summary>
    internal sealed partial class Log4jStreamParser(Action<GameLogLine> onLog)
    {
        private GameLogLevel _level = GameLogLevel.Info;
        private System.Text.StringBuilder? _message;

        public void Feed(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<log4j:Event", StringComparison.Ordinal))
            {
                _level = LevelAttr().Match(trimmed) is { Success: true } m ? ParseLevel(m.Groups[1].Value) : GameLogLevel.Info;
                return;
            }
            if (trimmed.StartsWith("<log4j:Message>", StringComparison.Ordinal))
            {
                _message = new System.Text.StringBuilder(StripCdata(trimmed["<log4j:Message>".Length..]));
                if (trimmed.EndsWith("</log4j:Message>", StringComparison.Ordinal)) Flush();
                return;
            }
            if (_message is not null)
            {
                _message.Append('\n').Append(StripCdata(trimmed));
                if (trimmed.EndsWith("</log4j:Message>", StringComparison.Ordinal)) Flush();
                return;
            }
            if (trimmed.StartsWith("</log4j:Event", StringComparison.Ordinal) || trimmed.StartsWith("<log4j:Throwable", StringComparison.Ordinal) || trimmed.Length == 0) return;
            onLog(new GameLogLine(GuessLevel(line), SecretRedactor.Redact(line)));
        }

        private void Flush()
        {
            onLog(new GameLogLine(_level, SecretRedactor.Redact(_message!.ToString())));
            _message = null;
        }

        private static string StripCdata(string s) =>
            s.Replace("<![CDATA[", "").Replace("]]>", "").Replace("</log4j:Message>", "");

        private static GameLogLevel ParseLevel(string s) => s switch
        {
            "WARN" => GameLogLevel.Warn,
            "ERROR" or "FATAL" => GameLogLevel.Error,
            "DEBUG" or "TRACE" => GameLogLevel.Debug,
            _ => GameLogLevel.Info,
        };

        private static GameLogLevel GuessLevel(string line) =>
            line.Contains("/ERROR]") || line.Contains("Exception") ? GameLogLevel.Error : line.Contains("/WARN]") ? GameLogLevel.Warn : GameLogLevel.Info;

        [GeneratedRegex("level=\"([A-Z]+)\"")]
        private static partial Regex LevelAttr();
    }
}
