using Elio.UI;
using Foundation;
using LocalAuthentication;
using UIKit;

namespace Elio.iOS;

/// <summary>
/// Asks iOS to check who is holding the device: Face ID or Touch ID, with the passcode to fall back on. Elio
/// never sees any of those, only whether the check succeeded.
/// </summary>
internal static class DeviceCheck
{
    /// <summary>True only if the device confirmed its owner. False when refused or cancelled.</summary>
    public static async Task<bool> AuthenticateAsync(string reason)
    {
        // Kept until the answer is in: the prompt belongs to this object and goes away with it.
        using var context = new LAContext();
        if (!context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out NSError? unable))
        {
            // A device with no passcode has nothing to check against, and anyone holding it already has everything
            // on it. Saying no here would shut the owner out of Elio for good the day they remove their passcode.
            return unable?.Code == (long)LAStatus.PasscodeNotSet;
        }

        try
        {
            (bool confirmed, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthentication, reason);
            return confirmed;
        }
        catch (NSErrorException)
        {
            // Cancelled, or refused too often. Either way it was not confirmed.
            return false;
        }
    }

    /// <summary>
    /// Tells the app lock when Elio leaves the screen and when it is back. Leaving is taken at the moment iOS
    /// says the app is about to stop being the one in use, which is early enough for the content to be covered
    /// in the picture iOS keeps for its list of open apps.
    /// </summary>
    public static void WatchForLeaving()
    {
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => AppServices.AppWentToBackground());
        NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification, _ => AppServices.AppCameToForeground());
    }
}
