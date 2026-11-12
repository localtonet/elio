using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Win32.Input;

namespace Elio.Desktop;

/// <summary>
/// A key that brings the window up from anywhere and sends it away again, the way a drop-down terminal works.
/// </summary>
/// <remarks>Windows only for now: each desktop has its own way of handing out keys that work everywhere.</remarks>
[SupportedOSPlatform("windows")]
internal static partial class SummonKey
{
    private const uint HotkeyMessage = 0x0312;
    private const int HotkeyId = 0x454C;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWindows = 0x8;
    private const uint NoRepeat = 0x4000;

    // How much of the screen's height the window takes when it drops down from the top.
    private const double DropDownShare = 0.45;

    /// <summary>Makes the key show and hide the window. Does nothing if the key cannot be read or is already taken.</summary>
    /// <param name="keys">One key with its modifiers, such as "Ctrl+Oem3".</param>
    /// <param name="fromTop">Whether the window is placed along the top of the screen each time it is shown.</param>
    public static void Attach(Window window, string keys, bool fromTop)
    {
        KeyGesture gesture;
        try
        {
            gesture = KeyGesture.Parse(keys);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A typo in the setting costs the key, not the app.
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (virtualKey == 0) return;

        uint modifiers = NoRepeat;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ModAlt;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ModControl;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ModShift;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ModWindows;

        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is not nint handle) return;

            // Another program may hold the key already. Then it stays theirs.
            if (!RegisterHotKey(handle, HotkeyId, modifiers, (uint)virtualKey)) return;

            Win32Properties.AddWndProcHookCallback(window, (nint _, uint message, nint wParam, nint _, ref bool handled) =>
            {
                if (message != HotkeyMessage || wParam != HotkeyId) return 0;

                Toggle(window, fromTop);
                handled = true;
                return 0;
            });
            window.Closed += (_, _) => UnregisterHotKey(handle, HotkeyId);
        };
    }

    private static void Toggle(Window window, bool fromTop)
    {
        if (window.IsVisible && window.IsActive && window.WindowState != WindowState.Minimized)
        {
            window.Hide();
            return;
        }

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        if (fromTop && (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is Screen screen)
        {
            PixelRect area = screen.WorkingArea;
            window.WindowState = WindowState.Normal;
            window.Position = area.Position;
            window.Width = area.Width / screen.Scaling;
            window.Height = area.Height * DropDownShare / screen.Scaling;
        }

        window.Show();
        window.Activate();
    }

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);
}
