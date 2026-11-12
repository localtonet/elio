using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Elio.Core;
using Java.Lang;

namespace Elio.Android;

/// <summary>
/// Asks Android to check who is holding the phone: fingerprint or face where the phone has them, and its PIN,
/// pattern or password otherwise. Elio never sees any of those, only whether the check succeeded.
/// </summary>
/// <remarks>
/// Android 9 and later show their own prompt for this. Before Android 11 that prompt could not always fall back
/// to the PIN by itself, and before Android 9 it did not exist, so there the older screen that confirms the
/// PIN, pattern or password is used.
/// </remarks>
internal static class DeviceCheck
{
    /// <summary>The number the PIN screen's answer comes back to the activity under.</summary>
    public const int CredentialRequest = 4712;

    private static TaskCompletionSource<bool>? _credential;

    /// <summary>True only if the phone confirmed its owner. False when refused, cancelled, or there is no window to ask from.</summary>
    public static Task<bool> AuthenticateAsync(string reason)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (MainActivity.Current is not { } activity)
        {
            done.TrySetResult(false);
            return done.Task;
        }

        activity.RunOnUiThread(() =>
        {
            try
            {
                Ask(activity, reason, done);
            }
            catch (System.Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
            {
                // Something the phone could not do counts as not confirmed. Nothing opens on a failure.
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private static void Ask(MainActivity activity, string reason, TaskCompletionSource<bool> done)
    {
        // A phone with no screen lock has nothing to check against, and anyone holding it already has everything
        // on it. Saying no here would shut the owner out of Elio for good the day they remove their PIN.
        if (activity.GetSystemService(Context.KeyguardService) is not KeyguardManager keyguard || !keyguard.IsDeviceSecure)
        {
            done.TrySetResult(true);
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(28)) AskWithPrompt(activity, keyguard, reason, done);
        else AskForCredential(activity, keyguard, reason, done);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private static void AskWithPrompt(MainActivity activity, KeyguardManager keyguard, string reason, TaskCompletionSource<bool> done)
    {
        var builder = new BiometricPrompt.Builder(activity);
        builder.SetTitle(reason);

        // Whether the prompt itself can take the PIN. Where it cannot, its second button leads to the PIN screen.
        bool promptTakesCredential = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            builder.SetAllowedAuthenticators((int)(BiometricManagerAuthenticators.BiometricWeak | BiometricManagerAuthenticators.DeviceCredential));
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
#pragma warning disable CA1422 // Replaced in Android 11, which takes the branch above.
            builder.SetDeviceCredentialAllowed(true);
#pragma warning restore CA1422
        }
        else
        {
            promptTakesCredential = false;
            builder.SetNegativeButton(
                L.T("Use passcode"),
                activity.MainExecutor!,
                new Clicked(() => AskForCredential(activity, keyguard, reason, done)));
        }

        var callback = new Callback(
            () => done.TrySetResult(true),
            cancelled =>
            {
                // No fingerprint enrolled, no sensor, too many wrong tries: the PIN still proves who it is. Only
                // the user turning the prompt away is a plain no.
                if (!promptTakesCredential && !cancelled) AskForCredential(activity, keyguard, reason, done);
                else done.TrySetResult(false);
            });
        builder.Build().Authenticate(new CancellationSignal(), activity.MainExecutor!, callback);
    }

    // The screen Android itself shows to confirm the PIN, pattern or password. Its answer comes to the activity.
    private static void AskForCredential(MainActivity activity, KeyguardManager keyguard, string reason, TaskCompletionSource<bool> done)
    {
#pragma warning disable CA1422 // Replaced by the prompt in Android 10 and later, which do not come here.
        Intent? confirm = keyguard.CreateConfirmDeviceCredentialIntent(reason, null);
#pragma warning restore CA1422
        if (confirm is null)
        {
            done.TrySetResult(false);
            return;
        }

        _credential?.TrySetResult(false);
        _credential = done;
        activity.StartActivityForResult(confirm, CredentialRequest);
    }

    /// <summary>Takes the PIN screen's answer from the activity.</summary>
    public static void CredentialAnswered(bool confirmed)
    {
        _credential?.TrySetResult(confirmed);
        _credential = null;
    }

    private sealed class Clicked(Action clicked) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => clicked();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private sealed class Callback(Action succeeded, Action<bool> failed) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => succeeded();

        // A finger that was not recognised is not reported here: the prompt stays up for another try. This is the end of it.
        public override void OnAuthenticationError(BiometricErrorCode errorCode, ICharSequence? errString) =>
            failed(errorCode is BiometricErrorCode.UserCanceled or BiometricErrorCode.Canceled);
    }
}
