using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Elio.Desktop;

/// <summary>Makes the window's taskbar button flash until the user comes back to it.</summary>
[SupportedOSPlatform("windows")]
internal static partial class TaskbarAttention
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    public static void Ask(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not nint handle) return;

        var request = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
        };
        FlashWindowEx(ref request);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
