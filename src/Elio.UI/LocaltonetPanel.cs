using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Core;
using Elio.Localtonet;

namespace Elio.UI;

/// <summary>The sidebar's Localtonet section: sign in once, and the account's computers show up here.</summary>
public sealed class LocaltonetPanel : UserControl
{
    private const double ListHeight = 220;

    private readonly DialogHost _dialogs;
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly TextBlock _account = new() { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, Margin = new Thickness(6, 0, 6, 4), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "localtonet-account" };
    private readonly Button _refresh;

    public event Action<HostEntry>? ConnectRequested;

    public event Action<HostEntry>? FilesRequested;

    /// <summary>The user wants this device's files beside those of another server, yet to be picked.</summary>
    public event Action<HostEntry>? FilesWithAnotherRequested;

    public LocaltonetPanel(DialogHost dialogs)
    {
        _dialogs = dialogs;

        _refresh = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconRefresh), L.T("Refresh device list"), 26);
        _refresh.Tag = "localtonet-refresh";
        _refresh.Click += async (_, _) => await AppServices.Localtonet.RefreshAsync();

        // As tall with the refresh button hidden as with it, so signing in does not move the title.
        var header = new DockPanel { Margin = new Thickness(6, 10, 0, 2), MinHeight = 26 };
        DockPanel.SetDock(_refresh, Dock.Right);
        header.Children.Add(_refresh);
        header.Children.Add(ElioStyle.SectionTitle("Localtonet"));

        var signOut = new MenuItem { Header = L.T("Sign out of Localtonet") };
        signOut.Click += (_, _) => AppServices.Localtonet.SignOut();
        var importKeys = new MenuItem { Header = L.T("Get access keys for all devices…") };
        importKeys.Click += (_, _) => _ = ImportKeysAsync();
        var another = new MenuItem { Header = L.T("Add another Localtonet account") };
        another.Click += (_, _) => _ = SignInAsync();
        header.ContextMenu = new ContextMenu { ItemsSource = new[] { another, importKeys, signOut } };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_account, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_account);
        layout.Children.Add(new ScrollViewer { MaxHeight = ListHeight, Content = _rows });
        Content = layout;

        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppServices.Localtonet.Changed += Show;
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AppServices.Localtonet.Changed -= Show;
    }

    /// <summary>Opens the window that turns sync on. Set by whoever owns that window.</summary>
    public Func<Task>? OpenSync { get; set; }

    public Task SignInAsync() => SignInAsync(offerSync: true);

    /// <param name="offerSync">False when the sign-in was started from the sync window, which is already open on that very question.</param>
    public async Task SignInAsync(bool offerSync)
    {
        if (!await AppServices.Localtonet.SignInAsync(_dialogs, url => AppServices.OpenUrlAsync(this, url))) return;
        Show();

        // Signing in brings the account's devices and nothing else. Someone who expected their own servers to
        // follow them here would otherwise be left wondering where they are.
        if (offerSync && OpenSync is { } openSync && !AppServices.Sync.IsOn && await _dialogs.ConfirmAsync(
            L.T("Sync your servers too?"),
            L.T("Signing in lists your Localtonet devices. The servers you saved yourself stay on this device unless sync is on. Sync is encrypted with a password only you know, and is turned on once on each device."),
            L.T("Turn on sync"),
            cancelText: L.T("Not now")))
        {
            await openSync();
        }

        // Right after signing in is when the question is most useful: before the first connection asks for an approval.
        LocaltonetLogin? signedIn = AppServices.Localtonet.Latest;
        bool anyWithoutKey = signedIn is not null && AppServices.Localtonet.DevicesOf(signedIn).Any(d => AppServices.Vault.Get(d.Id) is null);
        if (anyWithoutKey && await _dialogs.ConfirmAsync(
            L.T("Connect without approving each time?"),
            L.T("Without an access key, every connection to one of your devices has to be approved in the browser. Elio can ask Localtonet for an access key for each device and keep them in its vault. You approve this once."),
            L.T("Get the keys")))
        {
            await ImportKeysAsync(signedIn);
        }
    }

    /// <summary>Fetches an access key for every device, after asking how long they should last.</summary>
    public async Task ImportKeysAsync(LocaltonetLogin? account = null)
    {
        if (await AppServices.Localtonet.ImportKeysAsync(_dialogs, url => AppServices.OpenUrlAsync(this, url), account) is not null) Show();
    }

    private void Show()
    {
        LocaltonetAccount account = AppServices.Localtonet;
        _rows.Children.Clear();
        _refresh.IsVisible = account.SignedIn;
        // With one account its name sits under the title. With several, each has its own line above its devices.
        List<LocaltonetLogin> accounts = account.Accounts.Where(a => account.KeyOf(a) is not null).ToList();
        _account.Text = accounts.Count == 1 ? accounts[0].Email ?? string.Empty : string.Empty;
        _account.IsVisible = accounts.Count == 1 && !string.IsNullOrEmpty(accounts[0].Email);

        if (!account.SignedIn)
        {
            var signIn = new Button { Content = L.T("Sign in with Localtonet"), Padding = new Thickness(12, 5), Margin = new Thickness(6, 2, 6, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Tag = "localtonet-signin" };
            signIn.Click += async (_, _) => await SignInAsync();
            _rows.Children.Add(Hint(account.Problem ?? L.T("See your computers here without opening a port.")));
            _rows.Children.Add(signIn);
            return;
        }

        if (account.Problem is { } problem) _rows.Children.Add(Hint(problem));
        if (accounts.Count > 1)
        {
            foreach (LocaltonetLogin each in accounts)
            {
                _rows.Children.Add(AccountLine(each));
                foreach (HostEntry device in account.DevicesOf(each)) _rows.Children.Add(Row(device));
            }
        }
        else
        {
            foreach (HostEntry device in account.Devices) _rows.Children.Add(Row(device));
        }

        if (account.Devices.Count == 0 && account.Problem is null)
            _rows.Children.Add(Hint(L.T("No devices yet. Run localtonet --terminal enable on a computer.")));
    }

    /// <summary>The name of one account above its devices, with what can be done to that account alone.</summary>
    private Control AccountLine(LocaltonetLogin account)
    {
        var keys = new MenuItem { Header = L.T("Get access keys for all devices…") };
        keys.Click += (_, _) => _ = ImportKeysAsync(account);
        var signOut = new MenuItem { Header = L.T("Sign out of {0}", account.Email ?? "Localtonet") };
        signOut.Click += (_, _) => AppServices.Localtonet.SignOut(account);

        return new TextBlock
        {
            Text = account.Email ?? "Localtonet",
            Foreground = ElioColors.TextMuted,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 8, 6, 2),
            Background = ElioColors.Transparent,
            ContextMenu = new ContextMenu { ItemsSource = new[] { keys, signOut } },
            Tag = "localtonet-account-line",
        };
    }

    private static TextBlock Hint(string text) => new() { Text = text, Foreground = ElioColors.TextMuted, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 6) };

    private Control Row(HostEntry device)
    {
        Control built = BuildRow(device);
        HostListView.EnableDrag(built, device);
        return built;
    }

    private Control BuildRow(HostEntry device)
    {
        LocaltonetDevice? details = AppServices.Localtonet.Details(device.Id);
        bool online = details?.Online == true;
        bool keyed = AppServices.Vault.Get(device.Id) is not null;
        string about = string.Join("  ·  ", new[] { online ? L.T("online") : L.T("offline"), details?.Os, keyed ? L.T("key saved") : null }.Where(s => !string.IsNullOrEmpty(s)));

        var row = new Border
        {
            Background = ElioColors.Transparent,
            CornerRadius = new CornerRadius(ElioStyle.RowRadius),
            Padding = new Thickness(8, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = device.Id,
            Child = new DockPanel
            {
                Children =
                {
                    Docked(new Panel
                    {
                        Opacity = online ? 1 : 0.55,
                        Children =
                        {
                            ElioStyle.Avatar(device.Name),

                            // Whether the computer can be reached right now, in the corner of its picture.
                            new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = online ? ElioColors.Online : ElioColors.TextFaint, Margin = new Thickness(0, 0, -2, -2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom },
                        },
                    }),
                    new StackPanel
                    {
                        Margin = new Thickness(9, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = device.Name, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = about, Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "localtonet-about" },
                        },
                    },
                },
            },
        };
        ElioStyle.HoverFill(row, () => ElioColors.Transparent);
        HostListView.ConnectOnActivate(row, () => ConnectRequested?.Invoke(device));

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        row.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item(L.T("Connect"), () => ConnectRequested?.Invoke(device)),
                Item(L.T("Open files"), () => FilesRequested?.Invoke(device)),
                Item(L.T("Open files with another server…"), () => FilesWithAnotherRequested?.Invoke(device)),
                Item(L.T("Enter access key…"), () => _ = EnterKeyAsync(device)),
                Item(L.T("Delete access key"), () =>
                {
                    AppServices.Vault.Remove(device.Id);
                    Show();
                }),
            },
        };
        return row;
    }

    private static Control Docked(Control control)
    {
        DockPanel.SetDock(control, Dock.Left);
        return control;
    }

    private async Task EnterKeyAsync(HostEntry device)
    {
        SecretAnswer? answer = await _dialogs.AskSecretAsync(
            L.T("Access key for {0}", device.Name),
            L.T("Paste the key you created under My Devices > Keys in the Localtonet panel. With a key, connections do not need approval in the browser."),
            offerRemember: false);
        if (answer is null || string.IsNullOrWhiteSpace(answer.Secret)) return;

        AppServices.Vault.Set(device.Id, answer.Secret.Trim());
        device.AutoReconnect = true;
        Show();
    }
}
