using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Elio.UI;

public partial class App : Application
{
    /// <summary>Raised for each window the app opens, so a platform head can attach what only it can do.</summary>
    public static event Action<Avalonia.Controls.Window>? WindowOpened;

    internal static void AnnounceWindow(Avalonia.Controls.Window window) => WindowOpened?.Invoke(window);

    /// <summary>Something in a window wants the user's eye while they are in another program.</summary>
    public static event Action<Avalonia.Controls.Window>? AttentionWanted;

    internal static void WantAttention(Avalonia.Controls.Window window) => AttentionWanted?.Invoke(window);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The built-in controls follow the same choice as the app's own colors.
        RequestedThemeVariant = AppServices.Settings.LightInterface ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        ElioStyle.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (PlatformSettings is { } platform) platform.ColorValuesChanged += (_, _) => AppServices.SystemAppearanceChanged();

        // The terminal engine leaves reading PNG files to the app, which has Skia for it.
        Elio.Terminal.TerminalImages.DecodePng = DecodePng;

        // A vault behind Windows Hello is opened once, before any window needs a password from it. Saying no
        // leaves it shut for this run, and servers ask for their passwords instead.
        if (AppServices.Vault.IsLocked) AppServices.Vault.TryUnlock();

        // Before any view is made, so that a locked Elio never shows its content, not even for one frame.
        AppServices.Lock.Start();

        // On an iPhone a link opened from outside, such as the sync QR code read by the phone's own camera, comes
        // in this way. Android hands its links to the activity instead.
        if (OperatingSystem.IsIOS() && this.TryGetFeature<Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime>() is { } activation)
        {
            activation.Activated += (_, e) =>
            {
                if (e is Avalonia.Controls.ApplicationLifetimes.ProtocolActivatedEventArgs { Uri: { } link }) AppServices.OpenLink(link.OriginalString);
            };
        }

        // Before any view is built, since each one asks for its text as it is made.
        Elio.Core.L.Use(AppServices.Settings.Language);

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var window = new MainWindow();
                desktop.MainWindow = window;
                AnnounceWindow(window);
                window.View.RestoreSession();
                UpdateCheck.Start(window.View.Workspace);
                break;
            case IActivityApplicationLifetime activity:
                activity.MainViewFactory = () => new MainView();
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView();
                break;
        }

        base.OnFrameworkInitializationCompleted();
        StartBackgroundWork();
    }

    private static Elio.Terminal.ImagePixels? DecodePng(ReadOnlyMemory<byte> file)
    {
        using SkiaSharp.SKData data = SkiaSharp.SKData.CreateCopy(file.Span);
        using SkiaSharp.SKCodec? codec = SkiaSharp.SKCodec.Create(data);
        if (codec is null) return null;

        // The size is whatever the file's first bytes claim, and a few hundred bytes can claim gigabytes of pixels.
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > Elio.Terminal.TerminalImages.MaxPixels) return null;

        // Plain red, green, blue and opacity, which is the form the engine keeps pictures in.
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using SkiaSharp.SKBitmap? plain = SkiaSharp.SKBitmap.Decode(codec, info);
        return plain is null ? null : new Elio.Terminal.ImagePixels(info.Width, info.Height, plain.Bytes);
    }

    /// <summary>What runs once per app, whatever the platform: the device list and sync.</summary>
    public static void StartBackgroundWork()
    {
        _ = AppServices.Localtonet.RefreshAsync();
        AppServices.Sync.Start();
    }
}
