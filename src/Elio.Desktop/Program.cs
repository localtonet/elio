using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;
using Elio.UI;

namespace Elio.Desktop;

internal static partial class Program
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

        // What the command line asks for: a server, a workspace, a link, or only Elio itself.
        LaunchRequest request = LaunchRequest.Parse(args);
        switch (request.Kind)
        {
            case LaunchKind.Help:
                Print(LaunchRequest.Usage);
                return;
            case LaunchKind.Version:
                Print("Elio " + (UpdateCheck.Running is { } version ? version.ToString(3) : "?") + "\n");
                return;
            case LaunchKind.Invalid:
                Print("elio: " + request.Error + "\n\n" + LaunchRequest.Usage, error: true);
                Environment.ExitCode = 2;
                return;
        }

        // A copy started by an update waits for the old one to finish closing, so that it does not take that one for a
        // copy to hand its launch to, and clears away what the update left.
        UpdateInstaller.AfterUpdate(args);

        // A copy that is already running takes this launch: a new tab, or whatever the command line asked for.
        if (SingleInstance.TryHandOver(request)) return;

        Window? main = null;
        App.WindowOpened += window =>
        {
            window.Icon = AppIcon.Create();
            if (main is not null) return;

            main = window;

            // Set once the app is up: AppServices is not touched before Avalonia is, since some of what it holds needs it.
            AppServices.Links = LinkRegistration.ForThisPlatform();
            SingleInstance.Listen(later => Dispatcher.UIThread.Post(() =>
            {
                ComeForward(window);
                AppServices.Launch(later);
            }));

            // The window is up and listening, so what this launch asked for can be done now. A first launch with
            // nothing asked for shows the window as it is.
            if (request.Kind != LaunchKind.NewTab) AppServices.Launch(request);

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

    private static void ComeForward(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Writes to the console Elio was started from. On Windows Elio is a windowed program, which has no console of its
    /// own, so it borrows the one it was started from, if any.
    /// </summary>
    private static void Print(string text, bool error = false)
    {
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);
        (error ? Console.Error : Console.Out).Write(text);
    }

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    private static void AddTrayIcon(Window window)
    {
        if (Application.Current is not Application app) return;

        var show = new NativeMenuItem(L.T("Show or hide Elio"));
        show.Click += (_, _) =>
        {
            if (window.IsVisible && window.IsActive) window.Hide();
            else ComeForward(window);
        };
        var quit = new NativeMenuItem(L.T("Quit"));
        quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        var tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "Elio", Menu = new NativeMenu { Items = { show, quit } } };
        tray.Clicked += (_, _) => ComeForward(window);
        TrayIcon.SetIcons(app, [tray]);
    }

    // Also called by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
