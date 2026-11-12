using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using Elio.Core;
using Foundation;
using UIKit;

namespace Elio.iOS;

/// <summary>Reads a QR code with the camera, on a screen of its own over the app.</summary>
/// <remarks>
/// iOS finds the code in the picture itself and hands over only its text; no picture is kept or sent anywhere.
/// </remarks>
internal static class QrScanner
{
    /// <summary>The text of the first QR code the camera sees, or null if the user gave up or refused the camera.</summary>
    public static Task<string?> ScanAsync()
    {
        var done = new TaskCompletionSource<string?>();
        Console.Error.WriteLine("Elio scan: asked for.");
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            UIViewController? top = KeyWindow()?.RootViewController;
            while (top?.PresentedViewController is { } above) top = above;
            if (top is null)
            {
                Console.Error.WriteLine("Elio scan: no window to show the camera over.");
                done.TrySetResult(null);
                return;
            }

            top.PresentViewController(new ScanViewController(done) { ModalPresentationStyle = UIModalPresentationStyle.FullScreen }, true, null);
        });
        return done.Task;
    }

    // The window the app is shown in. An app of today keeps its windows in scenes; the delegate's own window
    // is only there on the oldest systems.
    private static UIWindow? KeyWindow()
    {
        foreach (UIScene scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene { Windows: { Length: > 0 } windows }) continue;
            return windows.FirstOrDefault(w => w.IsKeyWindow) ?? windows[0];
        }

        return (UIApplication.SharedApplication.Delegate as AppDelegate)?.Window;
    }

    private sealed class ScanViewController(TaskCompletionSource<string?> done) : UIViewController, IAVCaptureMetadataOutputObjectsDelegate
    {
        private AVCaptureSession? _session;
        private AVCaptureVideoPreviewLayer? _preview;
        private bool _finished;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;

            var hint = new UILabel
            {
                Text = L.T("Point the camera at the QR code Elio shows on your other device."),
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Lines = 0,
                BackgroundColor = UIColor.FromWhiteAlpha(0, 0.65f),
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var cancel = UIButton.FromType(UIButtonType.System);
            cancel.SetTitle(L.T("Cancel"), UIControlState.Normal);
            cancel.SetTitleColor(UIColor.White, UIControlState.Normal);
            cancel.TitleLabel!.Font = UIFont.SystemFontOfSize(18)!;
            cancel.TranslatesAutoresizingMaskIntoConstraints = false;
            cancel.TouchUpInside += (_, _) => Finish(null);

            View.AddSubview(hint);
            View.AddSubview(cancel);
            UILayoutGuide safe = View.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                hint.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                hint.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                hint.BottomAnchor.ConstraintEqualTo(cancel.TopAnchor, -16),
                hint.HeightAnchor.ConstraintGreaterThanOrEqualTo(64),
                cancel.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
                cancel.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor, -20),
            ]);

            // The system asks the user the first time, and remembers the answer after that.
            AVCaptureDevice.RequestAccessForMediaType(AVAuthorizationMediaType.Video, granted => InvokeOnMainThread(() =>
            {
                if (granted) Start();
                else
                {
                    Console.Error.WriteLine("Elio scan: the camera was not allowed.");
                    Finish(null);
                }
            }));
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview is not null) _preview.Frame = View!.Bounds;
        }

        private void Start()
        {
            if (_finished || AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video) is not { } camera
                || AVCaptureDeviceInput.FromDevice(camera, out NSError? error) is not { } input || error is not null)
            {
                Console.Error.WriteLine("Elio scan: no camera could be opened.");
                Finish(null);
                return;
            }

            var session = new AVCaptureSession();
            var output = new AVCaptureMetadataOutput();
            if (!session.CanAddInput(input) || !session.CanAddOutput(output))
            {
                Console.Error.WriteLine("Elio scan: the camera could not be set up for reading codes.");
                Finish(null);
                return;
            }

            session.AddInput(input);
            session.AddOutput(output);
            output.SetDelegate(this, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = AVMetadataObjectType.QRCode;

            _preview = new AVCaptureVideoPreviewLayer(session) { Frame = View!.Bounds, VideoGravity = AVLayerVideoGravity.ResizeAspectFill };
            View.Layer.InsertSublayer(_preview, 0);
            _session = session;

            // Starting the camera takes a moment and must not hold up the screen.
            _ = Task.Run(session.StartRunning);
        }

        [Export("captureOutput:didOutputMetadataObjects:fromConnection:")]
        public void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            foreach (AVMetadataObject found in metadataObjects)
            {
                if (found is AVMetadataMachineReadableCodeObject { StringValue: { Length: > 0 } text })
                {
                    Finish(text);
                    return;
                }
            }
        }

        private void Finish(string? text)
        {
            if (_finished) return;

            _finished = true;
            AVCaptureSession? session = _session;
            _session = null;
            if (session is not null) _ = Task.Run(session.StopRunning);
            DismissViewController(true, () => done.TrySetResult(text));
        }
    }
}
