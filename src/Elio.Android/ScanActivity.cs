using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Elio.Core;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

// The camera interface every Android version has. Its successors do more than a QR reader needs, at many
// times the code, and this one is marked as old rather than taken away.
#pragma warning disable CA1422, CS0618
using Camera = Android.Hardware.Camera;
using Result = Android.App.Result;

namespace Elio.Android;

/// <summary>
/// Shows what the camera sees and looks for a QR code in it. Ends with the code's text as soon as one is read,
/// or with nothing if the user goes back.
/// </summary>
/// <remarks>
/// The picture never leaves this screen: each frame is searched in memory and dropped, nothing is saved or sent.
/// </remarks>
[Activity(Theme = "@style/Theme.AppCompat.NoActionBar", ScreenOrientation = ScreenOrientation.Portrait, Exported = false)]
public class ScanActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    /// <summary>The name the text that was read is returned under.</summary>
    public const string ResultText = "sh.elio.app.scan.text";

    private const int PermissionRequest = 1;

    private SurfaceView? _surface;
    private Camera? _camera;
    private bool _surfaceReady;
    private bool _found;
    private int _reading;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _surface = new SurfaceView(this);
        _surface.Holder!.AddCallback(this);

        var hint = new TextView(this)
        {
            Text = L.T("Point the camera at the QR code Elio shows on your other device."),
            Gravity = GravityFlags.Center,
        };
        hint.SetTextColor(Color.White);
        hint.SetBackgroundColor(Color.Argb(170, 0, 0, 0));
        hint.SetPadding(48, 40, 48, 40);
        hint.TextSize = 16;

        var layout = new FrameLayout(this);
        layout.SetBackgroundColor(Color.Black);
        layout.AddView(_surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        layout.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom) { BottomMargin = 96 });
        SetContentView(layout);

        if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted) RequestPermissions([Manifest.Permission.Camera], PermissionRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != PermissionRequest) return;

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartCamera();
            return;
        }

        Toast.MakeText(this, L.T("Elio needs the camera to read the code. You can type the code instead."), ToastLength.Long)?.Show();
        SetResult(Result.Canceled);
        Finish();
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        _surfaceReady = true;
        StartCamera();
    }

    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Format format, int width, int height)
    {
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceReady = false;
        StopCamera();
    }

    protected override void OnPause()
    {
        base.OnPause();
        StopCamera();
    }

    protected override void OnResume()
    {
        base.OnResume();
        StartCamera();
    }

    private void StartCamera()
    {
        if (_camera is not null || !_surfaceReady || _found || CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted) return;

        try
        {
            Camera camera = Camera.Open() ?? throw new Java.Lang.RuntimeException("There is no camera on the back of this device.");
            Camera.Parameters? settings = camera.GetParameters();
            if (settings?.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true)
            {
                settings.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
                camera.SetParameters(settings);
            }

            // The sensor lies sideways in a phone held upright.
            camera.SetDisplayOrientation(90);
            camera.SetPreviewDisplay(_surface!.Holder);
            camera.SetPreviewCallback(this);
            camera.StartPreview();
            _camera = camera;
        }
        catch (Exception ex) when (ex is Java.Lang.RuntimeException or Java.IO.IOException)
        {
            // In use by another app, or there is none.
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            SetResult(Result.Canceled);
            Finish();
        }
    }

    private void StopCamera()
    {
        if (_camera is not { } camera) return;

        _camera = null;
        try
        {
            camera.SetPreviewCallback(null);
            camera.StopPreview();
        }
        catch (Java.Lang.RuntimeException)
        {
            // Already stopped by the system.
        }

        camera.Release();
    }

    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        // One frame at a time: the next ones are let go by while this one is searched.
        if (data is null || camera is null || _found || Interlocked.Exchange(ref _reading, 1) == 1) return;

        Camera.Size? size;
        try
        {
            size = camera.GetParameters()?.PreviewSize;
        }
        catch (Java.Lang.RuntimeException)
        {
            size = null;
        }

        if (size is null)
        {
            Volatile.Write(ref _reading, 0);
            return;
        }

        int width = size.Width, height = size.Height;
        _ = Task.Run(() =>
        {
            try
            {
                // The frame starts with one byte of brightness for each pixel, which is all a QR code needs.
                var picture = new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(data, width, height, 0, 0, width, height, false)));
                if (new QRCodeReader().decode(picture) is { Text: { Length: > 0 } text }) RunOnUiThread(() => Found(text));
            }
            catch (Exception ex) when (ex is ReaderException or ArgumentException or IndexOutOfRangeException)
            {
                // Nothing readable in this frame. The next one is tried.
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        });
    }

    private void Found(string text)
    {
        if (_found) return;

        _found = true;
        StopCamera();
        SetResult(Result.Ok, new Intent().PutExtra(ResultText, text));
        Finish();
    }
}
