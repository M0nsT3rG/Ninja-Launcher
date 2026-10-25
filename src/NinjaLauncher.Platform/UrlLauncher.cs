using System.Diagnostics;
using NinjaLauncher.Application.Abstractions;

namespace NinjaLauncher.Platform;

/// <summary>Opens http(s) URLs in the user's default browser. Other schemes are refused.</summary>
public sealed class UrlLauncher : IUrlLauncher
{
    public void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException($"Refusing to open non-web URL scheme '{uri.Scheme}'.");
        var url = uri.AbsoluteUri;
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else Start(url);
    }

    public void OpenDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(path) }, UseShellExecute = false });
        else Start(Path.GetFullPath(path));
    }

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { target }, UseShellExecute = false });
}
