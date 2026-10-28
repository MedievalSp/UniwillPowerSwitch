using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UwPower.Core.Idle;

internal static partial class NativeIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    private const int QunsBusy = 2;               // a fullscreen app is running
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    /// <summary>Time since the last keyboard/mouse/touch input in this session.</summary>
    public static TimeSpan InputIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    public static string? FullscreenAppState()
    {
        if (SHQueryUserNotificationState(out var state) != 0)
            return null;
        return state switch
        {
            QunsBusy => "a fullscreen app is in the foreground",
            QunsRunningD3DFullScreen => "a fullscreen game (Direct3D) is running",
            QunsPresentationMode => "presentation mode is on",
            _ => null,
        };
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
