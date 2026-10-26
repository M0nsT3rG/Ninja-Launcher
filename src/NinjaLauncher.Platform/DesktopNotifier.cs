using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NinjaLauncher.Platform;

/// <summary>
/// Operating-system notifications without extra packages: a notification-area balloon on Windows (shown as a toast
/// on Windows 10/11), <c>notify-send</c> on Linux and <c>osascript</c> on macOS. Best effort: failures are swallowed
/// because a missed notification must never break the launcher.
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    private WindowsBalloon? _balloon;

    /// <param name="windowHandle">Windows only: a window of this process that owns the notification-area icon.</param>
    /// <returns>Whether the operating system accepted the notification (it may still hide it, e.g. in Do Not Disturb).</returns>
    public bool Show(string title, string body, nint windowHandle)
    {
        title = Trim(title, 63);
        body = Trim(body, 255);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (windowHandle == 0) return false;
                return (_balloon ??= new WindowsBalloon(windowHandle)).Show(title, body);
            }
            if (OperatingSystem.IsMacOS())
                // Text goes in as argv, never spliced into the script.
                return Run("osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, body);
            return Run("notify-send", "--app-name=Ninja Launcher", "--icon=io.github.m0nst3rg.NinjaLauncher", title, body);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ExternalException) { return false; }
    }

    internal static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static bool Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        return p is not null;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) _balloon?.Dispose();
    }

    /// <summary>One hidden-until-needed notification-area icon; Windows turns its balloons into toasts.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsBalloon : IDisposable
    {
        private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
        private const uint NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
        private const uint NiifUser = 0x4, NiifLargeIcon = 0x20;
        private NotifyIconData _data;
        private bool _added;

        public WindowsBalloon(nint hwnd)
        {
            ExtractIconEx(Environment.ProcessPath ?? "", 0, out var large, out var small, 1);
            if (small != 0) DestroyIcon(small);
            _data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1, uFlags = NifIcon | NifTip,
                hIcon = large, szTip = "Ninja Launcher", szInfo = "", szInfoTitle = "", hBalloonIcon = large,
            };
        }

        public bool Show(string title, string body)
        {
            if (!_added)
            {
                _added = ShellNotifyIcon(NimAdd, ref _data);
                _data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
                ShellNotifyIcon(NimSetVersion, ref _data);
            }
            _data.uFlags = NifIcon | NifTip | NifInfo;
            _data.szInfoTitle = title;
            _data.szInfo = body;
            _data.dwInfoFlags = _data.hIcon == 0 ? 0x1 /* NIIF_INFO */ : NiifUser | NiifLargeIcon;
            return _added && ShellNotifyIcon(NimModify, ref _data);
        }

        public void Dispose()
        {
            if (_added) ShellNotifyIcon(NimDelete, ref _data);
            if (_data.hIcon != 0) DestroyIcon(_data.hIcon);
            _added = false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(nint icon);
    }
}
