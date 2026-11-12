using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI;

// The lock screen: what is shown instead of everything else while AppServices.Lock says Elio is locked or out of sight.
public partial class MainView
{
    private Border? _lockScreen;
    private StackPanel? _lockedNote;
    private Button? _unlock;

    /// <summary>
    /// Puts the lock screen over everything in the view. It is opaque and takes every touch, and what is under
    /// it is neither drawn nor able to take the keyboard while it is up.
    /// </summary>
    private void AddLockScreen()
    {
        var mark = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = ElioColors.Accent,
            Child = ElioStyle.Mark(64, ElioColors.AccentText, ElioColors.AccentText),
        };

        _unlock = new Button
        {
            Content = L.T("Unlock"),
            Padding = new Thickness(24, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
            Background = ElioColors.Accent,
            Foreground = ElioColors.AccentText,
            Tag = "unlock",
        };
        _unlock.Classes.Add("primary");
        _unlock.Click += (_, _) => _ = AppServices.Lock.UnlockAsync();

        // Not shown while Elio is only out of sight: there is nothing to unlock then, just nothing to see.
        _lockedNote = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = L.T("Elio is locked"), Foreground = ElioColors.Text, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                _unlock,
            },
        };

        _lockScreen = new Border
        {
            Background = ElioColors.Window,
            IsVisible = false,
            Tag = "lock-screen",
            Child = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { mark, _lockedNote } },
        };
        Root.Children.Add(_lockScreen);

        // A keyboard does not go through the screen the way a finger does, so it is stopped on its way down
        // to whatever still has it. The Unlock button keeps Enter and Space.
        AddHandler(KeyDownEvent, StopWhileLocked, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, StopWhileLocked, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, StopWhileLocked, RoutingStrategies.Tunnel);

        // In place before the view is first drawn, so a locked Elio starts covered.
        ShowLockState();
        AttachedToVisualTree += (_, _) =>
        {
            AppServices.Lock.Changed += OnLockChanged;
            ShowLockState();

            // The device's prompt comes up by itself once the lock screen is really there to return to.
            Dispatcher.UIThread.Post(AppServices.Lock.ShownToUser, DispatcherPriority.Background);
        };
        DetachedFromVisualTree += (_, _) => AppServices.Lock.Changed -= OnLockChanged;
    }

    private void StopWhileLocked(object? sender, RoutedEventArgs e)
    {
        if (AppServices.Lock.HidesContent && !ReferenceEquals(e.Source, _unlock)) e.Handled = true;
    }

    // Straight away when it can be: going out of sight, the cover has to be up before the system takes its picture of the app.
    private void OnLockChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) ShowLockState();
        else Dispatcher.UIThread.Post(ShowLockState);
    }

    private void ShowLockState()
    {
        if (_lockScreen is null || _lockedNote is null || _unlock is null) return;

        AppLock appLock = AppServices.Lock;
        bool hidden = appLock.HidesContent;
        bool locked = appLock.IsLocked;
        _lockScreen.IsVisible = hidden;
        _lockedNote.IsVisible = locked && !appLock.IsCovered;

        foreach (Control child in Root.Children)
        {
            if (ReferenceEquals(child, _lockScreen)) continue;

            // Not drawn at all, rather than drawn and painted over. Its size and place stay as they are, so no terminal is resized.
            child.Opacity = hidden ? 0 : 1;

            // Only while really locked: this takes the keyboard away from the terminal, which a moment out of sight should not.
            child.IsEnabled = !locked;
        }

        // Whatever had the keyboard gives it up, and the phone's own keyboard goes down with it.
        if (locked && _lockedNote.IsVisible) _unlock.Focus();
    }
}
