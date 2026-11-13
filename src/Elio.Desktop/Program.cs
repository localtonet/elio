using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;
using Elio.UI;

namespace Elio.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // No window in this mode: an AI tool started Elio to talk to it over its input and output.
        if (args.Contains("--mcp"))
        {
            Environment.ExitCode = McpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).GetAwaiter().GetResult();
            return;
        }

        // No window in this mode either: this copy keeps local shells running while windows come and go.
        if (args.Contains(ShellHost.Argument))
        {
            Environment.ExitCode = ShellHost.Run();
            return;
        }

        // A copy started by an update waits for the old one to finish closing, so that it does not take that one for a
        // copy to hand its launch to, and clears away what the update left.
        UpdateInstaller.AfterUpdate(args);

        // A copy that is already running takes this launch as a request for a new tab.
        if (SingleInstance.TryHandOver()) return;

        Window? main = null;
        App.WindowOpened += window =>
        {
            window.Icon = AppIcon.Create();
            if (main is not null) return;

            main = window;
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(() => ComeForward(window, openTab: true)));
            if (AppServices.Settings.SummonKey is not { Length: > 0 } keys) return;

            // The window can be sent away, so there has to be another way back to it than the key.
            AddTrayIcon(window);
            if (OperatingSystem.IsWindows()) SummonKey.Attach(window, keys, AppServices.Settings.SummonFromTop);
        };

        App.AttentionWanted += window =>
        {
            if (OperatingSystem.IsWindows()) TaskbarAttention.Ask(window);
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ComeForward(Window window, bool openTab)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (openTab && window is MainWindow elio) elio.View.OpenLocalShell();
    }

    private static void AddTrayIcon(Window window)
    {
        if (Application.Current is not Application app) return;

        var show = new NativeMenuItem(L.T("Show or hide Elio"));
        show.Click += (_, _) =>
        {
            if (window.IsVisible && window.IsActive) window.Hide();
            else ComeForward(window, openTab: false);
        };
        var quit = new NativeMenuItem(L.T("Quit"));
        quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        var tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "Elio", Menu = new NativeMenu { Items = { show, quit } } };
        tray.Clicked += (_, _) => ComeForward(window, openTab: false);
        TrayIcon.SetIcons(app, [tray]);
    }

    // Also called by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
