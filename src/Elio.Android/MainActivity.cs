using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia.Android;
using Elio.UI;

namespace Elio.Android;

// One window for the whole app, so that a link opened while Elio is running comes to it and does not start a second one.
[Activity(
    Label = "Elio",
    Icon = "@mipmap/icon",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]

// The link in the QR code a device that already syncs shows: elio://sync/..., as the phone's own camera hands it over.
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "elio",
    DataHost = "sync")]
public class MainActivity : AvaloniaMainActivity
{
    private const int ScanRequest = 4711;

    private TaskCompletionSource<string?>? _scan;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        AppServices.ScanQrCode = ScanAsync;
        AppServices.SettingsChanged += KeepOutOfPictures;
        KeepOutOfPictures();
        TakeLink(Intent);
    }

    /// <summary>The app's one window while it exists, for what has to be shown from it: the device's own check.</summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnDestroy()
    {
        AppServices.SettingsChanged -= KeepOutOfPictures;
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    // Leaving and coming back are what the app lock counts its time by.
    protected override void OnPause()
    {
        base.OnPause();
        AppServices.AppWentToBackground();
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppServices.AppCameToForeground();
    }

    // With the app lock on, Android is told to keep Elio out of screenshots and to show it blank in the list of
    // open apps, where a terminal would otherwise be readable by anyone who picks the phone up. Off again with the lock.
    private void KeepOutOfPictures() => RunOnUiThread(() =>
    {
        if (AppServices.Settings.LockApp) Window?.AddFlags(WindowManagerFlags.Secure);
        else Window?.ClearFlags(WindowManagerFlags.Secure);
    });

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        TakeLink(intent);
    }

    private static void TakeLink(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.DataString is { Length: > 0 } link) AppServices.OpenLink(link);
    }

    /// <summary>Opens the camera on a screen of its own and waits for a QR code. Null if the user came back without one.</summary>
    private Task<string?> ScanAsync()
    {
        _scan?.TrySetResult(null);
        _scan = new TaskCompletionSource<string?>();
        StartActivityForResult(new Intent(this, typeof(ScanActivity)), ScanRequest);
        return _scan.Task;
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == DeviceCheck.CredentialRequest) DeviceCheck.CredentialAnswered(resultCode == Result.Ok);
        if (requestCode != ScanRequest) return;

        _scan?.TrySetResult(resultCode == Result.Ok ? data?.GetStringExtra(ScanActivity.ResultText) : null);
        _scan = null;
    }
}
