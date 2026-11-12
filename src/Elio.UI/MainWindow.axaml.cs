using Avalonia.Controls;

namespace Elio.UI;

public partial class MainWindow : Window
{
    public MainView View { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // Where the system can blur what is behind the window, the sidebar lets it show through. The rest
        // of the window paints itself, so nothing depends on the blur being there.
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];
        TransparencyBackgroundFallback = ElioColors.Window;
        ShowBackdrop();
        Content = View;
        View.Workspace.DetachTab = MoveToNewWindow;
        Activated += (_, _) => AppServices.Sync.OnActivated();

        // Otherwise the search box takes the focus, and its ring is the first thing anyone sees.
        Opened += (_, _) => FocusManager?.Focus(null);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty) ShowBackdrop();
    }

    // Without a backdrop from the system the window has to be its own, or the sidebar's tint would lie on nothing.
    // Mica is already a calm, even surface. A plain blur is whatever is behind the window, so it gets a layer
    // of the window's color over it first: the sidebar's text must stay readable over a bright wallpaper.
    private void ShowBackdrop()
    {
        WindowTransparencyLevel level = ActualTransparencyLevel;
        Background = level == WindowTransparencyLevel.None ? ElioColors.Window
            : level == WindowTransparencyLevel.Mica ? ElioColors.Transparent
            : new Avalonia.Media.SolidColorBrush(ElioColors.ColorOf(ElioColors.Window), 0.6);
    }

    private void MoveToNewWindow(WorkspaceTab tab)
    {
        var window = new MainWindow { Width = Width, Height = Height };
        App.AnnounceWindow(window);
        window.View.Workspace.Adopt(View.Workspace.Release(tab));
        window.Show();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // Only the last window's tabs are worth bringing back. Closing a secondary window means closing its tabs.
        if (WorkspaceView.All.Count == 1) AppServices.SaveLayout();
        _ = CloseAndRecordAsync();
    }

    private async Task CloseAndRecordAsync()
    {
        bool others = WorkspaceView.All.Count > 1;
        await View.Workspace.CloseAllAsync();
        if (others) AppServices.SaveLayout();
    }
}
