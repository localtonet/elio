using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Core;
using Elio.Sync;

namespace Elio.UI;

public sealed partial class DialogHost
{
    private const int MinPassphraseLength = 10;

    /// <summary>Turns sync on, adds this device to a sync that exists, or shows how it is doing.</summary>
    /// <remarks>
    /// One window that walks through it a step at a time, each step saying what it is for and what to do next:
    /// sync has a password, sometimes a code and a recovery key, and shown all at once nobody could tell which
    /// of them this device needed.
    /// </remarks>
    /// <param name="link">A link read from a QR code, to add this device with. The window then opens on that step.</param>
    public Task ShowSyncAsync(SyncCoordinator sync, Func<Task> signIn, string? link = null) => Enqueue(false, result =>
    {
        var stage = new Border();
        var flow = new SyncFlow(this, sync, signIn, result, stage);
        stage.Child = sync.IsOn ? flow.Status() : link is null ? flow.Welcome() : flow.Join(link);
        return Card(stage, 460);
    });

    private async Task ChangeSyncPassphraseAsync(SyncCoordinator sync)
    {
        if (await AskSecretAsync(L.T("New sync password"), L.T("At least {0} characters. Your recovery key does not change.", MinPassphraseLength), offerRemember: false) is not { } first) return;
        if (await AskSecretAsync(L.T("New sync password"), L.T("Type the same password again."), offerRemember: false) is not { } second) return;

        if (first.Secret != second.Secret || first.Secret.Length < MinPassphraseLength)
        {
            await ShowMessageAsync(L.T("Password not changed"), L.T("The two passwords must match and be at least {0} characters.", MinPassphraseLength));
            return;
        }

        SyncEngine engine = sync.EngineFor(sync.Server!);
        try
        {
            if (await WaitAsync(L.T("Changing password"), L.T("Preparing the new key..."), null, null, cancellation => engine.ChangePassphraseAsync(first.Secret, cancellation)))
                await ShowMessageAsync(L.T("Password changed"), L.T("Your other devices keep syncing. Use the new password on a new device."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await ShowMessageAsync(L.T("Password not changed"), ex.Message);
        }
    }

    /// <summary>The steps of the sync window, and what they share: which server, and the window they are shown in.</summary>
    private sealed class SyncFlow(DialogHost host, SyncCoordinator sync, Func<Task> signIn, TaskCompletionSource<bool> result, Border stage)
    {
        private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

        // A key already fetched with a one-time code, while its device signs in to the account the sync belongs to.
        private static (string Code, SyncPairing.DeviceLink Link, DateTime Until)? _fetched;

        private string _server = AppServices.Settings.SyncServer is { Length: > 0 } chosen ? chosen : SyncCoordinator.DefaultServer;

        private void Show(Control page) => stage.Child = page;

        private void Finish()
        {
            result.TrySetResult(true);
            _ = sync.SyncNowAsync();
        }

        // ---- What every step is built from ----

        // A step: a mark and a heading that say where the user is, a line under it that says why, what the
        // step asks for, and the buttons that leave it.
        private static StackPanel Page(string icon, IBrush tint, string title, string lead, Control body, params Button[] buttons)
        {
            var mark = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ElioColors.ColorOf(tint), 0.16),
                VerticalAlignment = VerticalAlignment.Top,
                Child = ElioStyle.Icon(icon, tint, 18, 1.6),
            };
            var heading = new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(12, 0, 36, 0),
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = lead, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap, LineHeight = 18 },
                },
            };
            Grid.SetColumn(heading, 1);

            return new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { mark, heading } },
                    body,
                    Actions(buttons),
                },
            };
        }

        private static Grid Point(string text)
        {
            Control tick = ElioStyle.Icon(ElioStyle.IconCheck, ElioColors.Online, 14, 1.8);
            tick.VerticalAlignment = VerticalAlignment.Top;
            tick.Margin = new Thickness(0, 2, 0, 0);
            TextBlock words = Paragraph(text);
            words.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(words, 1);
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { tick, words } };
        }

        private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };

        private static TextBlock ErrorLine() => new() { Foreground = ElioColors.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false, Tag = "sync-error" };

        // A choice the size of what it decides: a heading, a line that says what happens, and an arrow.
        private static Button Option(string title, string description, string tag, Action chosen)
        {
            Control arrow = ElioStyle.Icon(ElioStyle.IconChevronRight, ElioColors.TextMuted);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 1);

            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 11, 10, 11),
                CornerRadius = new CornerRadius(10),
                Background = ElioColors.Surface,
                BorderBrush = ElioColors.Border,
                BorderThickness = new Thickness(1),
                Tag = tag,
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap },
                                new TextBlock { Text = description, FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap, LineHeight = 17 },
                            },
                        },
                        arrow,
                    },
                },
            };
            button.Click += (_, _) => chosen();
            return button;
        }

        // Something to carry to another place: shown large in one piece, with a button that copies it.
        private Grid Chip(string text, string tag)
        {
            Button copy = MakeButton(L.T("Copy"), primary: false);
            copy.VerticalAlignment = VerticalAlignment.Center;
            copy.Margin = new Thickness(10, 0, 0, 0);
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(host)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                copy.Content = "âœ“";
                await Task.Delay(1200);
                copy.Content = L.T("Copy");
            };
            Grid.SetColumn(copy, 1);

            var box = new Border
            {
                Background = ElioColors.Surface,
                BorderBrush = ElioColors.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
                Padding = new Thickness(12, 9),
                Child = new SelectableTextBlock { Text = text, FontFamily = Mono, FontSize = 14, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap, Tag = tag },
            };
            return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { box, copy } };
        }

        private Button Back(Func<Control> page)
        {
            Button back = MakeButton(L.T("Back"), primary: false);
            back.Click += (_, _) => Show(page());
            return back;
        }

        private Uri? Server() => Uri.TryCreate(_server.Trim(), UriKind.Absolute, out Uri? address) && address.Scheme is "https" or "http" ? address : null;

        // Runs one step's work with its button held, and turns what can go wrong into a line under the fields.
        private static async Task TryAsync(Button button, TextBlock error, Func<Task> work)
        {
            void Fail(string message)
            {
                error.Text = message;
                error.IsVisible = true;
            }

            error.IsVisible = false;
            button.IsEnabled = false;
            try
            {
                await work();
            }
            catch (Exception ex) when (ex is WrongPassphraseException or PairingCodeException or SyncStepException)
            {
                Fail(ex.Message);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // The server answered, but with a keyring that cannot or must not be used. The message says which.
                Fail(ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or Elio.Localtonet.LocaltonetSignedOutException)
            {
                Fail(L.T("Could not reach the sync server: {0}", ex.Message));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                // What the server keeps as the keyring is whatever it likes to send: text that is not JSON or not
                // base64, or values the key derivation refuses. None of that may take the window down.
                Fail(L.T("The synced data is damaged."));
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        // Over plain http anyone on the way could read the sync code and swap what comes back, so only a server on
        // this very computer may be reached that way.
        private Uri RequireServer()
        {
            Uri address = Server() ?? throw new SyncStepException(L.T("Enter a valid server address."));
            if (address.Scheme != Uri.UriSchemeHttps && !address.IsLoopback) throw new SyncStepException(L.T("The sync server's address has to start with https://"));
            return address;
        }

        private SyncEngine Begin()
        {
            Uri address = RequireServer();
            AppServices.Settings.SyncServer = address.ToString();
            return sync.EngineFor(address);
        }

        // ---- The steps ----

        /// <summary>Before sync is on: what it is, and the ways into it from where this device stands.</summary>
        public Control Welcome()
        {
            bool signedIn = AppServices.Localtonet.SignedIn;
            var error = ErrorLine();
            var options = new StackPanel { Spacing = 8 };

            if (signedIn)
            {
                Button account = null!;
                account = Option(
                    L.T("Continue with my Localtonet account"),
                    L.T("Elio looks whether sync is already set up for this account, and asks for the password either way."),
                    "sync-with-account",
                    () => _ = TryAsync(account, error, async () =>
                    {
                        // The account says whose data it is, so a code left over from an earlier try must not.
                        sync.SetAccountKey(null);
                        SyncEngine engine = Begin();
                        bool exists = await Task.Run(() => engine.ExistsAsync(CancellationToken.None));
                        Show(exists ? Unlock(withAccount: true) : Create());
                    }));
                options.Children.Add(account);
                options.Children.Add(Option(
                    L.T("I have a code from another device"),
                    L.T("Type the code that device shows. No password is needed."),
                    "sync-join",
                    () => Show(Join())));
            }
            else
            {
                options.Children.Add(Option(
                    L.T("This is my first device"),
                    L.T("Choose a password and start syncing from here."),
                    "sync-first",
                    () => Show(Create())));
                options.Children.Add(Option(
                    L.T("I already sync on another device"),
                    L.T("Type the code that device shows. No password is needed."),
                    "sync-join",
                    () => Show(Join())));
                options.Children.Add(Option(
                    L.T("Sign in with Localtonet first"),
                    L.T("Then no code is needed: your account says which data is yours."),
                    "sync-sign-in",
                    () =>
                    {
                        // Signing in has a window of its own, and only one is shown at a time.
                        result.TrySetResult(true);
                        _ = Task.Run(async () =>
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(signIn);
                            if (AppServices.Localtonet.SignedIn && !sync.IsOn) await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => host.ShowSyncAsync(sync, signIn));
                        });
                    }));
            }

            // The server is the same for nearly everyone, so it stays out of the way until asked for.
            TextBox server = Input(_server, tag: "sync-server");
            server.TextChanged += (_, _) => _server = server.Text ?? string.Empty;
            var serverField = new StackPanel
            {
                Spacing = 6,
                IsVisible = false,
                Children = { server, Hint(L.T("Where the encrypted copy is kept. Change it only if you run your own sync server.")) },
            };
            var serverToggle = new Button
            {
                Content = L.T("Sync server"),
                Padding = new Thickness(0),
                Background = ElioColors.Transparent,
                Foreground = ElioColors.TextMuted,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = "sync-server-toggle",
            };
            serverToggle.Click += (_, _) => serverField.IsVisible = !serverField.IsVisible;

            var body = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 9,
                        Children =
                        {
                            Point(L.T("Your servers, groups, quick commands and trusted host keys follow you. Saved passwords too, if you want.")),
                            Point(L.T("Everything is encrypted on this device with a password only you know. The sync server cannot read it.")),
                            Point(signedIn
                                ? L.T("You are signed in as {0}. The account says which data is yours, so there is no code to carry between devices.", AppServices.Localtonet.Account)
                                : L.T("No account is needed. The first device makes a code, and the others take it.")),
                        },
                    },
                    options,
                    error,
                    new StackPanel { Spacing = 6, Children = { serverToggle, serverField } },
                },
            };

            Button close = MakeButton(L.T("Close"), primary: false);
            close.Click += (_, _) => result.TrySetResult(true);
            return Page(ElioStyle.IconRefresh, ElioColors.Accent, L.T("Sync"), L.T("The same servers on every device you use."), body, close);
        }

        /// <summary>The first device: a password is chosen, and sync starts with what is on this device.</summary>
        private Control Create()
        {
            TextBox password = Input(secret: true, tag: "sync-pass");
            TextBox again = Input(secret: true, tag: "sync-pass2");
            var error = ErrorLine();
            Button start = MakeButton(L.T("Start sync"), primary: true);

            void Submit() => _ = TryAsync(start, error, async () =>
            {
                string secret = password.Text ?? string.Empty;
                if (secret.Length < MinPassphraseLength) throw new SyncStepException(L.T("The password must be at least {0} characters.", MinPassphraseLength));
                if (secret != again.Text) throw new SyncStepException(L.T("The two passwords do not match."));

                // Without an account a code says which store on the server is this user's. It is made here.
                if (!AppServices.Localtonet.SignedIn && sync.AccountKey is null) sync.SetAccountKey(SyncCoordinator.NewAccountKey());

                SyncEngine engine = Begin();
                string recoveryKey = await Task.Run(() => engine.CreateAsync(secret, CancellationToken.None));
                AppServices.SaveSettings();
                Show(Recovery(recoveryKey));
            });

            start.Click += (_, _) => Submit();
            SubmitOnEnter(again, Submit);
            FocusWhenShown(password);

            var body = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Field(L.T("Password"), password),
                    Field(L.T("Type the password again"), again),
                    Hint(L.T("At least {0} characters. Nobody can reset it for you: the recovery key on the next step is the only way back in.", MinPassphraseLength)),
                    error,
                },
            };
            return Page(
                ElioStyle.IconLock,
                ElioColors.Accent,
                L.T("Choose a sync password"),
                L.T("It locks your data before it leaves this device. You type it once on each device you add."),
                body,
                Back(Welcome),
                start);
        }

        /// <summary>Shown once, right after sync starts: the key that opens the data if the password is forgotten.</summary>
        private Control Recovery(string recoveryKey)
        {
            CheckBox saved = Check(L.T("I saved the recovery key somewhere safe"), false, "sync-saved");
            Button done = MakeButton(L.T("Done"), primary: true);
            done.IsEnabled = false;
            saved.IsCheckedChanged += (_, _) => done.IsEnabled = saved.IsChecked == true;
            done.Click += (_, _) => Finish();

            var body = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Paragraph(L.T("Your recovery key is below. If you forget your password, it is the only way to reach your data; nobody, including Localtonet, can recover it. It is shown only once.")),
                    Chip(recoveryKey, "sync-recovery"),
                    Hint(AppServices.Localtonet.SignedIn
                        ? L.T("To add another device: sign in with the same Localtonet account there, open Sync and type your sync password.")
                        : L.T("To add another device: open Sync here again and choose Add another device. It shows a short code to type there.")),
                    saved,
                },
            };
            return Page(ElioStyle.IconCheck, ElioColors.Online, L.T("Sync is on"), L.T("One thing left: keep your recovery key."), body, done);
        }

        /// <summary>A device joining a sync that exists: only the password is asked.</summary>
        private Control Unlock(bool withAccount)
        {
            TextBox password = Input(secret: true, tag: "sync-pass");
            var error = ErrorLine();
            Button unlock = MakeButton(L.T("Unlock"), primary: true);

            void Submit() => _ = TryAsync(unlock, error, async () =>
            {
                SyncEngine engine = Begin();
                string secret = password.Text ?? string.Empty;
                await Task.Run(() => engine.UnlockAsync(secret, CancellationToken.None));
                AppServices.SaveSettings();
                Finish();
            });

            unlock.Click += (_, _) => Submit();
            SubmitOnEnter(password, Submit);
            FocusWhenShown(password);

            // The other way in, for whoever would rather not type the password: a device that already syncs hands the key over.
            var useCode = new Button
            {
                Content = L.T("I have a code from another device"),
                Padding = new Thickness(0),
                Background = ElioColors.Transparent,
                Foreground = ElioColors.Accent,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = "sync-use-code",
            };
            useCode.Click += (_, _) => Show(Join());

            var body = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Field(L.T("Sync password"), password),
                    Hint(L.T("Forgot it? Type your recovery key here instead.")),
                    useCode,
                    error,
                },
            };
            return Page(
                ElioStyle.IconLock,
                ElioColors.Accent,
                L.T("Type your sync password"),
                withAccount
                    ? L.T("Sync is already set up for this account. This is the password you chose on your first device.")
                    : L.T("This is the password you chose on your first device."),
                body,
                Back(withAccount ? Welcome : () => Join()),
                unlock);
        }

        /// <summary>
        /// A device joining with a code another device shows. The code from "Add another device" carries the key,
        /// so nothing else is asked; a bare sync code only says which data it is, and the password follows.
        /// </summary>
        public Control Join(string? link = null)
        {
            TextBox code = Input(tag: "sync-code");
            code.FontFamily = Mono;
            var error = ErrorLine();
            Button connect = MakeButton(L.T("Connect"), primary: true);

            // What a link read from a QR code leads to, said before anything is done with it. A code can come
            // from anyone, and joining a sync means sending it everything this device has saved.
            TextBlock leadsTo = Paragraph(string.Empty);
            leadsTo.IsVisible = false;

            bool Take(string? text)
            {
                if (!SyncLink.TryParse(text, out Uri? server, out string read)) return false;

                code.Text = read;
                if (server is not null) _server = server.ToString();
                leadsTo.Text = L.T("This adds the device to the sync kept on {0}. Go on only if you made this code yourself on your other device: it shares this device's servers and saved passwords with whoever made it.", Server()?.Host ?? _server);
                leadsTo.IsVisible = true;
                error.IsVisible = false;
                return true;
            }

            void NotACode()
            {
                error.Text = L.T("That QR code is not one of Elio's.");
                error.IsVisible = true;
            }

            if (link is not null && !Take(link)) NotACode();


            // Shown when the code turns out to belong to an account this device is not signed in to. Signing in
            // has a window of its own, so this one closes, and comes back on this step with the code still in it.
            Button signInFirst = MakeButton(L.T("Sign in with Localtonet"), primary: true);
            signInFirst.Tag = "sync-sign-in-first";
            signInFirst.HorizontalAlignment = HorizontalAlignment.Left;
            signInFirst.IsVisible = false;
            signInFirst.Click += (_, _) =>
            {
                string again = SyncLink.Format(RequireServer(), (code.Text ?? string.Empty).Trim());
                result.TrySetResult(true);
                _ = Task.Run(async () =>
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(signIn);
                    if (!sync.IsOn) await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => host.ShowSyncAsync(sync, signIn, again));
                });
            };

            var buttons = new List<Button> { Back(Welcome) };
            if (AppServices.ScanQrCode is { } scanner)
            {
                Button scan = MakeButton(L.T("Scan QR code"), primary: false);
                scan.Tag = "sync-scan";
                scan.Click += async (_, _) =>
                {
                    if (await scanner() is string read && !Take(read)) NotACode();
                };
                buttons.Add(scan);
            }

            buttons.Add(connect);

            void Submit() => _ = TryAsync(connect, error, async () =>
            {
                Uri address = RequireServer();
                string typed = (code.Text ?? string.Empty).Trim();

                if (SyncPairing.NormalizeLink(typed) is string normal)
                {
                    // The server hands a code's key over once. If the step stops after that, for a sign-in, the
                    // key is kept here for the few minutes that takes, so the same code still works.
                    SyncPairing.DeviceLink link = _fetched is { } kept && kept.Code == normal && kept.Until > DateTime.UtcNow
                        ? kept.Link
                        : await SyncPairing.FetchLinkAsync(address, typed);
                    _fetched = (normal, link, DateTime.UtcNow.AddMinutes(10));

                    // Without a sync code the store is found by the account, and the server will ask this device for it.
                    if (link.AccountKey is null && !AppServices.Localtonet.SignedIn)
                    {
                        signInFirst.IsVisible = true;
                        throw new SyncStepException(L.T("That sync belongs to a Localtonet account. Sign in with that account on this device, and it carries on from here."));
                    }

                    sync.SetAccountKey(link.AccountKey);
                    SyncEngine linked = Begin();
                    if (!await Task.Run(() => linked.ExistsAsync(CancellationToken.None)))
                        throw new SyncStepException(L.T("Nothing is synced under that code yet. Check the code, or go back and start sync on this device."));

                    sync.SetDataKey(link.DataKey);
                    _fetched = null;
                    AppServices.SaveSettings();
                    Finish();
                    return;
                }

                // The short code an older version shows fetches the long sync code from the server.
                if (SyncPairing.Normalize(typed) is not null) typed = await SyncPairing.FetchAsync(address, typed);
                if (!SyncCoordinator.IsAccountKey(typed))
                    throw new SyncStepException(L.T("That is not a code Elio knows. Type the code another device shows under \"Add another device\"."));

                sync.SetAccountKey(typed);
                SyncEngine engine = Begin();
                if (!await Task.Run(() => engine.ExistsAsync(CancellationToken.None)))
                    throw new SyncStepException(L.T("Nothing is synced under that code yet. Check the code, or go back and start sync on this device."));

                // A sync code says which data it is and no more, so the password is still to come.
                Show(Unlock(withAccount: false));
            });

            connect.Click += (_, _) => Submit();
            SubmitOnEnter(code, Submit);
            // On a phone the keyboard would come up over the Scan button, which is what most people are here for.
            if (AppServices.ScanQrCode is null) FocusWhenShown(code);

            // Back from signing in, with the key of this very code already in hand: nothing is left to ask.
            if (_fetched is { } waiting && waiting.Code == SyncPairing.NormalizeLink(code.Text) && AppServices.Localtonet.SignedIn)
                Avalonia.Threading.Dispatcher.UIThread.Post(Submit);

            var body = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Field(L.T("Code from the other device"), code),
                    Hint(L.T("The code carries the key, so no password is asked. It works once, for ten minutes.")),
                    leadsTo,
                    signInFirst,
                    error,
                },
            };
            return Page(
                ElioStyle.IconDevices,
                ElioColors.Accent,
                L.T("Add this device"),
                L.T("On a device that already syncs, open Sync and choose Add another device. Type the code it shows here."),
                body,
                [.. buttons]);
        }

        /// <summary>Sync is on: how it is doing, what it carries, and how the next device joins.</summary>
        public Control Status()
        {
            CheckBox secrets = Check(L.T("Also sync saved passwords"), AppServices.Settings.SyncSecrets, "sync-secrets");
            secrets.IsCheckedChanged += (_, _) =>
            {
                AppServices.Settings.SyncSecrets = secrets.IsChecked == true;
                AppServices.SaveSettings();
            };

            Button now = MakeButton(L.T("Sync now"), primary: true);
            now.Click += (_, _) => Finish();

            Button change = MakeButton(L.T("Change password"), primary: false);
            change.Click += (_, _) =>
            {
                result.TrySetResult(true);
                _ = host.ChangeSyncPassphraseAsync(sync);
            };

            Button off = MakeButton(L.T("Turn off on this device"), primary: false);
            off.Tag = "sync-off";
            off.Click += (_, _) =>
            {
                sync.TurnOff();
                result.TrySetResult(true);
            };

            var another = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = L.T("Add another device"), FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text } } };
            {
                // The code carries the key itself, so the other device asks for nothing more: no password to type on a phone.
                another.Children.Add(Paragraph(sync.AccountKey is null
                    ? L.T("Get a code here. On the other device, sign in with the same Localtonet account, open Sync, choose \"I have a code from another device\" and type it. No password is asked.")
                    : L.T("Get a code here. On the other device open Sync, choose \"I already sync on another device\" and type it. No password is asked.")));
                Button pair = MakeButton(L.T("Show a code"), primary: false);
                pair.Tag = "sync-pair";
                pair.HorizontalAlignment = HorizontalAlignment.Left;
                var shown = new StackPanel { Spacing = 6, IsVisible = false };
                var error = ErrorLine();
                pair.Click += (_, _) => _ = TryAsync(pair, error, async () =>
                {
                    byte[] dataKey = sync.DataKey ?? throw new SyncStepException(L.T("Sync is locked."));
                    string linkCode = SyncPairing.NewLinkCode();
                    await SyncPairing.OfferLinkAsync(sync.Server!, linkCode, new SyncPairing.DeviceLink(sync.AccountKey, dataKey));
                    shown.Children.Clear();
                    shown.Children.Add(Chip(linkCode, "sync-link-code"));
                    shown.Children.Add(new QrView(SyncLink.Format(sync.Server!, linkCode)) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2), Tag = "sync-link-qr" });
                    shown.Children.Add(Hint(L.T("On a phone there is nothing to type: open Sync in Elio there and choose Scan QR code, or point the phone's own camera at this.")));
                    shown.Children.Add(Hint(L.T("It works once, for ten minutes. Whoever has it can read your synced data, so show it only to your own device.")));
                    shown.IsVisible = true;
                    pair.IsVisible = false;
                });
                another.Children.Add(pair);
                another.Children.Add(shown);
                another.Children.Add(error);
            }

            string host_ = sync.Server?.Host ?? string.Empty;
            string last = sync.LastSync is DateTime when ? L.T("Last sync {0:HH:mm}.", when) : string.Empty;

            // What sync last said about itself already carries the time; the plain time is for when it has said nothing.
            string lead = sync.Status is { Length: > 0 } said ? said : last.Length > 0 ? last : host_;

            var body = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 9,
                        Children =
                        {
                            Point(sync.AccountKey is null
                                ? L.T("With your Localtonet account {0}, on {1}.", AppServices.Localtonet.Account, host_)
                                : L.T("With a sync code and no account, on {0}.", host_)),
                            Point(L.T("Encrypted with your sync password before anything leaves this device.")),
                        },
                    },
                    secrets,
                    new Border { Height = 1, Background = ElioColors.Border },
                    another,
                },
            };

            return Page(ElioStyle.IconCheck, ElioColors.Online, L.T("Sync is on"), lead, body, off, change, now);
        }
    }

    /// <summary>Something a step of the sync window found wrong with what was typed, in the user's language.</summary>
    private sealed class SyncStepException(string message) : Exception(message);
}
