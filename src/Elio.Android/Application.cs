using Android.App;
using Android.Runtime;
using Android.Util;
using Avalonia.Android;
using Elio.Core;
using Elio.UI;

namespace Elio.Android;

[Application]
public class Application(nint javaReference, JniHandleOwnership transfer) : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    private const string LogTag = "Elio";

    // Before anything asks for the vault, so its key is kept by the device from the first run. Here and not in
    // OnCreate: the runtime may set up AppServices, and the vault with it, on the way into a method that
    // mentions it, which is before that method's first line has run. The vault then found no key store.
    static Application() => VaultKeys.Platform = new AndroidKeyProtector();

    public override void OnCreate()
    {
        // Before the app itself starts, which is when it decides whether it starts locked. Not in the static
        // constructor above, for the reason given there.
        AppServices.Authenticate = DeviceCheck.AuthenticateAsync;
        base.OnCreate();
        Log.Info(LogTag, AppServices.Vault.IsAvailable ? "The vault is kept by the device's key store." : "The vault could not be set up; secrets last for this run only.");
    }
}
