using Elio.Core;
using Elio.UI;
using UIKit;

namespace Elio.iOS;

public static class Program
{
    private static void Main(string[] args)
    {
        // Before anything asks for the vault, so its key is kept by the Keychain from the first run.
        VaultKeys.Platform = new IosKeyProtector();
        AppServices.ScanQrCode = QrScanner.ScanAsync;
        AppServices.Authenticate = DeviceCheck.AuthenticateAsync;
        DeviceCheck.WatchForLeaving();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
