using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Elio.Connections;
using Elio.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Elio.UI;

/// <summary>What the user asked for in the keychain window.</summary>
public enum KeychainAction
{
    Close,
    Add,
    Generate,
    Edit,
    Delete,
    ShowPublicKey,
    Install,
}

/// <summary>What was chosen in the keychain window, and for which identity when it is about one.</summary>
public sealed record KeychainChoice(KeychainAction Action, Identity? Identity = null);

/// <summary>An identity as the user filled it in, with the secret that goes into the vault beside it.</summary>
public sealed class IdentityEdit(Identity identity, IdentitySecret secret)
{
    public Identity Identity { get; } = identity;
    public IdentitySecret Secret { get; } = secret;
}

/// <summary>What the user asked a new key to be.</summary>
public sealed class KeyRequest(string name, string user, SshKeyType type, string? passphrase)
{
    public string Name { get; } = name;
    public string User { get; } = user;
    public SshKeyType Type { get; } = type;
    public string? Passphrase { get; } = passphrase;
}

/// <summary>How putting a key on one server went.</summary>
/// <param name="Installed">The key is in the server's authorized_keys now, whether it was added or was there already.</param>
/// <param name="Message">One line for the user.</param>
/// <param name="CanSwitch">Whether the server can be pointed at the identity and still log in as the account the key was put under.</param>
public sealed record KeyInstallResult(bool Installed, string Message, bool CanSwitch);

/// <content>The keychain: the identities servers share, making a key, and putting one on servers.</content>
public sealed partial class DialogHost
{
    private static Button SmallButton(string text, string tag) =>
        new() { Content = text, FontSize = ElioStyle.SmallFontSize, Padding = new Thickness(9, 2), CornerRadius = new CornerRadius(6), Tag = tag };

    /// <summary>The first word of a public key line, which is the key's type. Null where there is no line.</summary>
    private static string? KeyTypeOf(Identity identity) => identity.PublicKey?.Split(' ', 2)[0];

    /// <summary>Lists the identities and says what the user wants done. The work itself is the caller's, since only one dialog shows at a time.</summary>
    /// <param name="usedBy">How many servers and groups point at an identity.</param>
    public Task<KeychainChoice> ShowKeychainAsync(IReadOnlyList<Identity> identities, Func<Identity, int> usedBy) => Enqueue(new KeychainChoice(KeychainAction.Close), result =>
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (Identity identity in identities.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var details = new List<string>
            {
                identity.User.Length > 0 ? identity.User : L.T("the server's own user name"),
                identity.Kind == IdentityKind.Key ? KeyTypeOf(identity) ?? L.T("Private key") : L.T("Password"),
            };
            if (usedBy(identity) is > 0 and int count) details.Add(L.T("used in {0} places", count));

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 5, 0, 0) };
            void Offer(string text, string tag, KeychainAction action)
            {
                Button button = SmallButton(text, tag);
                button.Click += (_, _) => result.TrySetResult(new KeychainChoice(action, identity));
                actions.Children.Add(button);
            }

            if (identity.Kind == IdentityKind.Key && identity.PublicKey is not null)
            {
                Offer(L.T("Public key"), "identity-public", KeychainAction.ShowPublicKey);
                Offer(L.T("Install on a server…"), "identity-install", KeychainAction.Install);
            }

            Offer(L.T("Edit"), "identity-edit", KeychainAction.Edit);
            Offer(L.T("Delete"), "identity-delete", KeychainAction.Delete);

            rows.Children.Add(new StackPanel
            {
                Tag = "identity",
                Children =
                {
                    new TextBlock { Text = identity.Name, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = string.Join("  ·  ", details), Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                    actions,
                },
            });
        }

        TextBlock intro = Paragraph(L.T("An identity is a user name with a password or a private key. Servers and groups that point to one all log in with it, so it is changed in one place."));
        intro.Foreground = ElioColors.TextMuted;

        Button close = MakeButton(L.T("Close"), primary: false);
        Button generate = MakeButton(L.T("Generate key"), primary: false);
        Button add = MakeButton(L.T("Add identity"), primary: true);
        generate.Tag = "keychain-generate";
        add.Tag = "keychain-add";
        close.Click += (_, _) => result.TrySetResult(new KeychainChoice(KeychainAction.Close));
        generate.Click += (_, _) => result.TrySetResult(new KeychainChoice(KeychainAction.Generate));
        add.Click += (_, _) => result.TrySetResult(new KeychainChoice(KeychainAction.Add));

        return Card(
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Title(L.T("Keychain")),
                    intro,
                    new ScrollViewer { MaxHeight = 360, Margin = ScrollOutset, Content = new Border { Padding = ScrollInset, Child = rows } },
                    Actions(close, generate, add),
                },
            },
            560);
    });

    /// <summary>The form for one identity.</summary>
    /// <param name="secret">What the vault holds for it now. Nothing of it is shown; a field left empty keeps what is there.</param>
    /// <returns>The identity as edited with its secret, or null if cancelled.</returns>
    public Task<IdentityEdit?> EditIdentityAsync(Identity? existing, IdentitySecret? secret) => Enqueue<IdentityEdit?>(null, result =>
    {
        bool hadKey = !string.IsNullOrEmpty(secret?.PrivateKey);
        bool hadPassword = !string.IsNullOrEmpty(secret?.Password);

        TextBox name = Input(existing?.Name, tag: "identity-name");
        TextBox user = Input(existing?.User, tag: "identity-user");
        var kind = new ComboBox { ItemsSource = new[] { L.T("Password"), L.T("Private key") }, SelectedIndex = (int)(existing?.Kind ?? IdentityKind.Password), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "identity-kind" };
        TextBox password = Input(secret: true, tag: "identity-password");
        TextBox key = MultiLine(null, "identity-key");
        key.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        key.FontSize = 12;
        TextBox passphrase = Input(secret: true, tag: "identity-passphrase");
        if (hadPassword) password.PlaceholderText = L.T("Saved. Leave empty to keep it.");
        if (hadKey) key.PlaceholderText = L.T("A key is saved. Paste another one here to replace it.");
        if (hadKey && !string.IsNullOrEmpty(secret?.Passphrase)) passphrase.PlaceholderText = L.T("Saved. Leave empty to keep it.");

        Button load = SmallButton(L.T("Load from a file…"), "identity-load");
        load.HorizontalAlignment = HorizontalAlignment.Left;
        load.Margin = new Thickness(0, 6, 0, 0);

        var error = new TextBlock { Foreground = ElioColors.Danger, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        void Fail(string message)
        {
            error.Text = message;
            error.IsVisible = true;
        }

        load.Click += async (_, _) =>
        {
            // A private key is a few kilobytes at most. Anything much larger was picked by mistake.
            const int MaxKeyFile = 64 * 1024;

            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("Private key"), AllowMultiple = false });
            if (files.Count == 0) return;

            try
            {
                await using Stream stream = await files[0].OpenReadAsync();
                var buffer = new byte[MaxKeyFile + 1];
                int read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                if (read > MaxKeyFile) Fail(L.T("This is not a private key Elio can read."));
                else key.Text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex.Message);
            }
        };

        Control passwordField = Field(L.T("Password"), password);
        var keyFields = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel { Children = { Label(L.T("Private key (the text of a key file)")), key, load } },
                Field(L.T("Passphrase of the key (optional)"), passphrase),
            },
        };

        void ShowKind() => (passwordField.IsVisible, keyFields.IsVisible) = (kind.SelectedIndex == 0, kind.SelectedIndex == 1);
        kind.SelectionChanged += (_, _) => ShowKind();
        ShowKind();

        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            string label = name.Text?.Trim() ?? string.Empty;
            if (label.Length == 0)
            {
                Fail(L.T("A name is required."));
                return;
            }

            var identity = new Identity { Id = existing?.Id ?? Guid.NewGuid(), Name = label, User = user.Text?.Trim() ?? string.Empty, Kind = (IdentityKind)kind.SelectedIndex };
            var stored = new IdentitySecret();

            if (identity.Kind == IdentityKind.Password)
            {
                // No password at all is fine too: the identity then only names the user, and each server keeps its own password.
                stored.Password = !string.IsNullOrEmpty(password.Text) ? password.Text : secret?.Password;
            }
            else
            {
                bool replaced = !string.IsNullOrWhiteSpace(key.Text);

                // One form for every device: plain line feeds, and without the mark some editors put at the start of a file.
                stored.PrivateKey = replaced ? key.Text!.Replace("\r\n", "\n").Trim('﻿', ' ', '\t', '\r', '\n') + "\n" : secret?.PrivateKey;
                stored.Passphrase = !string.IsNullOrEmpty(passphrase.Text) ? passphrase.Text : replaced ? null : secret?.Passphrase;
                if (string.IsNullOrEmpty(stored.PrivateKey))
                {
                    Fail(L.T("Paste a private key, or load one from a file."));
                    return;
                }

                // Reading the key here is what tells a wrong passphrase or a damaged paste apart from a good key,
                // before a server does. It also gives the public half, which is kept to show and to install.
                try
                {
                    identity.PublicKey = SshKeys.PublicLine(stored.PrivateKey, stored.Passphrase, label);
                }
                catch (SshPassPhraseNullOrEmptyException)
                {
                    Fail(L.T("This key is protected by a passphrase. Enter it below."));
                    return;
                }
                catch (SecurityKeyFileException)
                {
                    Fail(L.T("This key lives on a security key, which the keychain cannot hold. Give the server the key file instead."));
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Fail(string.IsNullOrEmpty(stored.Passphrase) ? L.T("This is not a private key Elio can read.") : L.T("The key could not be read. Check the passphrase."));
                    return;
                }
            }

            result.TrySetResult(new IdentityEdit(identity, stored));
        }

        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        foreach (TextBox box in new[] { name, user, password, passphrase }) SubmitOnEnter(box, Submit);
        FocusWhenShown(name);

        return Frame(
            existing is null ? L.T("Add identity") : L.T("Edit identity"),
            new ScrollViewer
            {
                MaxHeight = 480,
                Margin = ScrollOutset,
                Content = new StackPanel
                {
                    Margin = ScrollInset,
                    Spacing = 12,
                    Children =
                    {
                        Field(L.T("Name"), name),
                        Field(L.T("User name (empty uses the server's own)"), user),
                        Field(L.T("Logs in with"), kind),
                        passwordField,
                        keyFields,
                        error,
                    },
                },
            },
            cancel,
            save);
    });

    /// <summary>Asks what kind of key to make and what to call it.</summary>
    public Task<KeyRequest?> AskNewKeyAsync() => Enqueue<KeyRequest?>(null, result =>
    {
        TextBox name = Input(tag: "key-name");
        TextBox user = Input(tag: "key-user");
        var type = new ComboBox { ItemsSource = new[] { L.T("Ed25519 (recommended)"), "ECDSA P-256", "RSA 4096" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "key-type" };
        TextBox passphrase = Input(secret: true, tag: "key-passphrase");
        var error = new TextBlock { Foreground = ElioColors.Danger, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button make = MakeButton(L.T("Generate key"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                error.Text = L.T("A name is required.");
                error.IsVisible = true;
                return;
            }

            result.TrySetResult(new KeyRequest(name.Text.Trim(), user.Text?.Trim() ?? string.Empty, (SshKeyType)type.SelectedIndex, string.IsNullOrEmpty(passphrase.Text) ? null : passphrase.Text));
        }

        make.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        foreach (TextBox box in new[] { name, user, passphrase }) SubmitOnEnter(box, Submit);
        FocusWhenShown(name);

        TextBlock note = Paragraph(L.T("The private key is made on this device and kept in the encrypted vault. Only the public key ever goes to a server."));
        note.Foreground = ElioColors.TextMuted;

        return Frame(
            L.T("Generate key"),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    note,
                    Field(L.T("Name"), name),
                    Field(L.T("User name (empty uses the server's own)"), user),
                    Field(L.T("Key type"), type),
                    Field(L.T("Passphrase of the key (optional)"), passphrase),
                    error,
                },
            },
            cancel,
            make);
    });

    /// <summary>Shows an identity's public key line, to copy or to have installed.</summary>
    /// <returns>True if the user wants it installed on servers.</returns>
    public Task<bool> ShowPublicKeyAsync(Identity identity, Func<string, Task> copy) => Enqueue(false, result =>
    {
        string line = identity.PublicKey ?? string.Empty;
        Button close = MakeButton(L.T("Close"), primary: false);
        Button again = MakeButton(L.T("Copy"), primary: false);
        Button install = MakeButton(L.T("Install on a server…"), primary: true);
        again.Tag = "public-key-copy";
        install.Tag = "public-key-install";
        close.Click += (_, _) => result.TrySetResult(false);
        install.Click += (_, _) => result.TrySetResult(true);
        again.Click += async (_, _) =>
        {
            await copy(line);

            // A mark in place of the word for a moment, so a press is seen to have done something.
            again.Content = "✓";
            await Task.Delay(1200);
            again.Content = L.T("Copy");
        };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Paragraph(L.T("This is the public key. A server lets this identity in once the line is in ~/.ssh/authorized_keys there. Elio can put it there for you.")),
                new Border
                {
                    Background = ElioColors.Surface,
                    BorderBrush = ElioColors.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
                    Padding = new Thickness(10, 8),
                    Child = new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Foreground = ElioColors.Text, Tag = "public-key" },
                },
            },
        };
        return Frame(L.T("Public key of \"{0}\"", identity.Name), body, close, again, install);
    });

    private sealed class InstallRow(HostEntry host, CheckBox pick, TextBlock status)
    {
        public HostEntry Host { get; } = host;
        public CheckBox Pick { get; } = pick;
        public TextBlock Status { get; } = status;
        public bool CanSwitch { get; set; }
    }

    /// <summary>
    /// Puts an identity's public key on the servers the user ticks, and says for each what happened. Afterwards it
    /// offers to point those servers at the identity.
    /// </summary>
    /// <param name="install">Adds the key on one server, logging in the way that server does today.</param>
    /// <param name="switchTo">Points servers at the identity.</param>
    public Task InstallKeyAsync(Identity identity, IReadOnlyList<HostEntry> hosts, Func<HostEntry, CancellationToken, Task<KeyInstallResult>> install, Action<IReadOnlyList<HostEntry>> switchTo) => Enqueue(false, result =>
    {
        var rows = new StackPanel { Spacing = 2 };
        var all = new List<InstallRow>();
        foreach (HostEntry host in hosts)
        {
            var pick = new CheckBox { Content = host.Name, Foreground = ElioColors.Text, Tag = "install-pick" };
            var status = new TextBlock { Foreground = ElioColors.TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 4), IsVisible = false, Tag = "install-status" };
            all.Add(new InstallRow(host, pick, status));
            rows.Children.Add(new StackPanel { Children = { pick, status } });
        }

        Button close = MakeButton(L.T("Close"), primary: false);
        Button run = MakeButton(L.T("Install"), primary: true);
        Button use = MakeButton(L.T("Use this identity on them"), primary: false);
        use.IsVisible = false;
        use.Tag = "install-switch";
        run.Tag = "install-run";
        CancellationTokenSource? running = null;

        void Show(InstallRow row, string text, IBrush color)
        {
            row.Status.Text = text;
            row.Status.Foreground = color;
            row.Status.IsVisible = true;
        }

        async Task Start()
        {
            List<InstallRow> chosen = all.Where(r => r.Pick.IsChecked == true).ToList();
            if (running is not null || chosen.Count == 0) return;

            using var cancel = new CancellationTokenSource();
            running = cancel;
            run.Content = L.T("Stop");
            use.IsVisible = false;
            foreach (InstallRow row in chosen)
            {
                row.CanSwitch = false;
                Show(row, L.T("running..."), ElioColors.TextMuted);
            }

            using var slots = new SemaphoreSlim(MaxParallelRuns);
            await Task.WhenAll(chosen.Select(async row =>
            {
                try
                {
                    await slots.WaitAsync(cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    Show(row, L.T("stopped"), ElioColors.Danger);
                    return;
                }

                KeyInstallResult outcome;
                try
                {
                    outcome = await install(row.Host, cancel.Token);
                }
                finally
                {
                    slots.Release();
                }

                row.CanSwitch = outcome.Installed && outcome.CanSwitch;
                Show(row, (outcome.Installed ? "✓ " : "✕ ") + outcome.Message, outcome.Installed ? ElioColors.Accent : ElioColors.Danger);
            }));

            running = null;
            run.Content = L.T("Install");
            use.IsVisible = all.Any(r => r.CanSwitch);
        }

        run.Click += (_, _) =>
        {
            if (running is not null) running.Cancel();
            else _ = Start();
        };
        use.Click += (_, _) =>
        {
            List<InstallRow> ready = all.Where(r => r.CanSwitch).ToList();
            switchTo(ready.Select(r => r.Host).ToList());
            foreach (InstallRow row in ready)
            {
                row.CanSwitch = false;
                Show(row, "✓ " + L.T("Now logs in with \"{0}\".", identity.Name), ElioColors.Accent);
            }

            use.IsVisible = false;
        };
        close.Click += (_, _) =>
        {
            running?.Cancel();
            result.TrySetResult(true);
        };

        TextBlock intro = Paragraph(L.T("Elio logs in to each server the way it does today and adds the public key to ~/.ssh/authorized_keys. A server whose host key is not known yet is left alone: connect to it once first."));
        intro.Foreground = ElioColors.TextMuted;

        return Card(
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Title(L.T("Install \"{0}\" on servers", identity.Name)),
                    intro,
                    hosts.Count == 0 ? Paragraph(L.T("No servers yet. Add one with +.")) : new ScrollViewer { MaxHeight = 320, Margin = ScrollOutset, Content = new Border { Padding = ScrollInset, Child = rows } },
                    Actions(close, use, run),
                },
            },
            560);
    });

    /// <summary>
    /// The fields for a proxy, shared by the server form and the group form. What they hold is read with
    /// <see cref="TryRead"/>, and the password goes to the vault with <see cref="Store"/> once the owner's id is known.
    /// </summary>
    private sealed class ProxyFields
    {
        private const string SocksLabel = "SOCKS5";
        private const string HttpLabel = "HTTP CONNECT";

        private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "proxy-kind" };
        private readonly TextBox _host;
        private readonly TextBox _port;
        private readonly TextBox _user;
        private readonly TextBox _password = Input(secret: true, tag: "proxy-password");
        private readonly StackPanel _details;
        private readonly bool _forServer;

        // What each entry of the list means: a kind, or null for "leave it to the group".
        private List<ProxyKind?> _choices = [];

        public StackPanel View { get; }

        /// <param name="forServer">A server can leave the choice to its group or overrule it; a group can only name a proxy or none.</param>
        public ProxyFields(ProxySettings? current, bool forServer, bool passwordSaved)
        {
            _forServer = forServer;
            _host = Input(current?.Host, tag: "proxy-host");
            _port = Input(current is { Port: > 0 } ? current.Port.ToString() : null, tag: "proxy-port");
            _user = Input(current?.User, tag: "proxy-user");
            if (passwordSaved) _password.PlaceholderText = L.T("Saved. Leave empty to keep it.");

            var address = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,90") };
            Control portField = Field(L.T("Port"), _port);
            Grid.SetColumn(portField, 2);
            address.Children.Add(Field(L.T("Proxy address"), _host));
            address.Children.Add(portField);

            _details = new StackPanel
            {
                Spacing = 12,
                Children = { address, Field(L.T("Proxy user name (optional)"), _user), Field(L.T("Proxy password (optional)"), _password) },
            };

            View = new StackPanel { Spacing = 12, Children = { Field(L.T("Connect through"), _kind), _details } };
            _kind.SelectionChanged += (_, _) =>
            {
                if (!_rebuilding) _wanted = Chosen;
                ShowDetails();
            };
            _wanted = current?.Kind;
            SetInherited(null);
        }

        // What the user picked, kept apart from the list: "direct, whatever the group says" has no entry while the
        // group says nothing, and must not be forgotten when another group is chosen a moment later.
        private ProxyKind? _wanted;
        private bool _rebuilding;

        private ProxyKind? Chosen => _kind.SelectedIndex >= 0 && _kind.SelectedIndex < _choices.Count ? _choices[_kind.SelectedIndex] : null;

        private void ShowDetails()
        {
            _details.IsVisible = Chosen is ProxyKind.Socks5 or ProxyKind.Http;

            // The usual port of each kind is offered, but only shown, never filled in: it has to be typed to count.
            _port.PlaceholderText = Chosen == ProxyKind.Http ? "8080" : "1080";
        }

        /// <summary>Says what a server would get from its group, and rebuilds the list around that, keeping what was chosen.</summary>
        public void SetInherited(string? inherited)
        {
            var labels = new List<string>();
            _choices = [];

            void Add(string label, ProxyKind? kind)
            {
                labels.Add(label);
                _choices.Add(kind);
            }

            if (_forServer && inherited is not null)
            {
                // Only when the group names a proxy is there a difference between following it and going direct.
                Add(inherited, null);
                Add(L.T("None (direct connection)"), ProxyKind.None);
            }
            else
            {
                Add(L.T("None"), null);
            }

            Add(SocksLabel, ProxyKind.Socks5);
            Add(HttpLabel, ProxyKind.Http);

            _rebuilding = true;
            _kind.ItemsSource = labels;
            int index = _choices.IndexOf(_wanted);
            _kind.SelectedIndex = index >= 0 ? index : 0;
            _rebuilding = false;
            ShowDetails();
        }

        /// <summary>Whether a proxy is set, for deciding if the section it is in starts out open.</summary>
        public bool IsSet => Chosen is not null;

        /// <summary>Reads the fields. False with a message when they do not add up to a proxy.</summary>
        public bool TryRead(out ProxySettings? proxy, out string error)
        {
            (proxy, error) = (null, string.Empty);
            if (Chosen is not ProxyKind kind) return true;

            if (kind == ProxyKind.None)
            {
                proxy = new ProxySettings();
                return true;
            }

            string host = _host.Text?.Trim() ?? string.Empty;
            if (host.Length == 0 || !int.TryParse(_port.Text, out int port) || port is < 1 or > 65535)
            {
                error = L.T("The proxy needs an address and a valid port.");
                return false;
            }

            proxy = new ProxySettings { Kind = kind, Host = host, Port = port, User = _user.Text?.Trim() ?? string.Empty };
            return true;
        }

        /// <summary>Files the proxy password under its owner, or takes it away when the proxy no longer has a login.</summary>
        public void Store(Guid owner, ProxySettings? proxy)
        {
            Guid id = ProxySettings.SecretId(owner);
            Vault vault = AppServices.Vault;

            if (proxy is { Kind: not ProxyKind.None, User.Length: > 0 })
            {
                if (!string.IsNullOrEmpty(_password.Text)) vault.Set(id, _password.Text);
            }
            else if (!vault.IsLocked)
            {
                // Tidying up is no reason to raise a Windows Hello prompt on every save. A password left behind
                // in a locked vault is filed under an id nothing asks for any more.
                vault.Remove(id);
            }
        }
    }

    /// <summary>
    /// A tick box for something a group can turn on for its servers. While the group does, and the server has not
    /// ticked it itself, the box is shown ticked and cannot be changed, with the group's name beside it: unticking
    /// would change nothing, since a server can only add to what its group says.
    /// </summary>
    private sealed class InheritableCheck
    {
        private readonly string _text;
        private bool _showing;

        public CheckBox Box { get; }

        /// <summary>What the server itself says, whatever the box shows on the group's behalf.</summary>
        public bool Own { get; private set; }

        public InheritableCheck(string text, bool own, string tag)
        {
            (_text, Own) = (text, own);
            Box = Check(text, own, tag);
            Box.IsCheckedChanged += (_, _) =>
            {
                if (!_showing && Box.IsEnabled) Own = Box.IsChecked == true;
            };
        }

        /// <param name="from">The group that turns it on. Null when none does.</param>
        public void ShowInherited(HostGroup? from)
        {
            _showing = true;
            bool forced = from is not null && !Own;
            Box.Content = forced ? FromGroup(_text, from) : _text;
            Box.IsChecked = forced || Own;
            Box.IsEnabled = !forced;
            _showing = false;
        }
    }

    /// <summary>Reads a list of saved servers by name, in order, as the jump host fields take it.</summary>
    /// <returns>Null with the name that was not found in <paramref name="missing"/>, or the servers' ids.</returns>
    private static List<Guid>? ReadJumpHosts(string? text, IReadOnlyList<HostEntry> hosts, Guid? self, out string missing)
    {
        missing = string.Empty;
        var ids = new List<Guid>();
        foreach (string name in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            HostEntry? hop = hosts.FirstOrDefault(h => h.Id != self && h.Kind == HostKind.Ssh && h.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (hop is null)
            {
                missing = name;
                return null;
            }

            ids.Add(hop.Id);
        }

        return ids;
    }

    private static string JumpHostNames(IEnumerable<Guid> ids, IReadOnlyList<HostEntry> hosts) =>
        string.Join(", ", ids.Select(id => hosts.FirstOrDefault(h => h.Id == id)?.Name).OfType<string>());

    /// <summary>"value (from group "name")", for showing where an inherited value comes from.</summary>
    private static string FromGroup(string value, HostGroup? group) =>
        group is null ? value : L.T("{0} (from group \"{1}\")", value, group.Name);

    /// <summary>
    /// The form for a group: its name, and what its servers connect with unless they say otherwise themselves.
    /// </summary>
    /// <param name="parentId">The group it is in, whose own defaults are shown as what applies when a field is left empty.</param>
    /// <returns>The group as edited, or null if cancelled.</returns>
    public Task<HostGroup?> EditGroupAsync(string title, HostGroup? existing, Guid? parentId, HostLibrary library) => Enqueue<HostGroup?>(null, result =>
    {
        // What a server would get from the groups above this one, shown where this group says nothing.
        ResolvedHost above = ConnectionResolver.Resolve(new HostEntry { Kind = HostKind.Ssh, Port = 0, GroupId = parentId }, library);
        List<Identity> identities = library.Identities.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        TextBox name = Input(existing?.Name, tag: "group-name");
        var identity = new ComboBox
        {
            ItemsSource = identities.Select(i => i.Name).Prepend(above.Identity is null ? L.T("None") : FromGroup(above.Identity.Name, above.IdentityFrom)).ToList(),
            SelectedIndex = 1 + identities.FindIndex(i => i.Id == existing?.IdentityId),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = "group-identity",
        };
        TextBox user = Input(existing?.User, tag: "group-user");
        if (above.User.Length > 0) user.PlaceholderText = FromGroup(above.User, above.UserFrom);
        TextBox port = Input(existing?.Port?.ToString(), tag: "group-port");
        port.PlaceholderText = FromGroup(above.Port.ToString(), above.PortFrom);
        TextBox jumps = Input(existing is null ? null : JumpHostNames(existing.JumpHosts, library.Hosts), tag: "group-jumps");
        if (above.JumpHosts.Count > 0) jumps.PlaceholderText = FromGroup(JumpHostNames(above.JumpHosts, library.Hosts), above.JumpHostsFrom);
        CheckBox agent = Check(L.T("Use the keys in the SSH agent"), existing?.UseAgent == true, "group-agent");
        CheckBox mosh = Check(L.T("Use Mosh, which survives sleep and changing networks (the server needs mosh-server)"), existing?.UseMosh == true, "group-mosh");
        var proxy = new ProxyFields(existing?.Proxy, forServer: false, existing is not null && !AppServices.Vault.IsLocked && AppServices.Vault.Get(ProxySettings.SecretId(existing.Id)) is not null);

        bool hasDefaults = existing is not null && (existing.IdentityId is not null || !string.IsNullOrEmpty(existing.User) || existing.Port is not null || existing.JumpHosts.Count > 0 || existing.UseAgent == true || existing.UseMosh == true || existing.Proxy is not null);
        TextBlock hint = Paragraph(L.T("Servers in this group, and in groups inside it, connect with these unless they set their own."));
        hint.Foreground = ElioColors.TextMuted;
        var defaults = new StackPanel
        {
            Spacing = 12,
            IsVisible = hasDefaults,
            Children =
            {
                hint,
                Field(L.T("Identity"), identity),
                Field(L.T("User name"), user),
                Field(L.T("Port"), port),
                Field(L.T("Jump hosts (in order, by saved name, separated by commas)"), jumps),
                agent,
                mosh,
                proxy.View,
            },
        };
        var toggle = new Button { Content = L.T("Defaults for its servers"), Padding = new Thickness(12, 4), HorizontalAlignment = HorizontalAlignment.Left, Tag = "group-defaults" };
        toggle.Click += (_, _) => defaults.IsVisible = !defaults.IsVisible;

        var error = new TextBlock { Foreground = ElioColors.Danger, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        void Fail(string message)
        {
            error.Text = message;
            error.IsVisible = true;
            defaults.IsVisible = true;
        }

        Button save = MakeButton(existing is null ? L.T("Add") : L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;

            int? number = null;
            if (!string.IsNullOrWhiteSpace(port.Text))
            {
                if (!int.TryParse(port.Text, out int parsed) || parsed is < 1 or > 65535)
                {
                    Fail(L.T("The port must be a number from 1 to 65535."));
                    return;
                }

                number = parsed;
            }

            if (ReadJumpHosts(jumps.Text, library.Hosts, null, out string missing) is not List<Guid> jumpIds)
            {
                Fail(L.T("Jump host not found: {0}", missing));
                return;
            }

            if (!proxy.TryRead(out ProxySettings? through, out string problem))
            {
                Fail(problem);
                return;
            }

            var group = new HostGroup
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = name.Text.Trim(),
                ParentId = existing?.ParentId ?? parentId,
                Collapsed = existing?.Collapsed ?? false,
                IdentityId = identity.SelectedIndex > 0 ? identities[identity.SelectedIndex - 1].Id : null,
                User = string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim(),
                Port = number,
                JumpHosts = jumpIds,

                // Unticked says nothing, which lets a group further up decide.
                UseAgent = agent.IsChecked == true ? true : null,
                UseMosh = mosh.IsChecked == true ? true : null,
                Proxy = through,
            };
            proxy.Store(group.Id, through);
            result.TrySetResult(group);
        }

        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        foreach (TextBox box in new[] { name, user, port, jumps }) SubmitOnEnter(box, Submit);
        FocusWhenShown(name);

        return Frame(
            title,
            new ScrollViewer
            {
                MaxHeight = 500,
                Margin = ScrollOutset,
                Content = new StackPanel { Margin = ScrollInset, Spacing = 12, Children = { Field(L.T("Group name"), name), toggle, defaults, error } },
            },
            cancel,
            save);
    });
}
