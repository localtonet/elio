using Avalonia.iOS;
using Foundation;
using Elio.UI;

namespace Elio.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711 // UIKit expects this name.
public partial class AppDelegate : AvaloniaAppDelegate<App>;
#pragma warning restore CA1711
