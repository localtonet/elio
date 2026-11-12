using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Connections;
using Elio.Core;
using Elio.Terminal;

namespace Elio.UI;

public sealed record SecretAnswer(string Secret, bool Remember);

public sealed record PaletteItem(string Title, string? Detail, Action Run);

/// <summary>
/// Shows one dialog at a time inside the window. Real windows don't exist on phones,
/// so this is the only dialog mechanism the app has.
/// </summary>
public sealed partial class DialogHost : Panel
{
    // Room for the halo a text box gets when it has the keyboard, which a scrolling area would otherwise cut
    // off at its edges. The scroller is pulled out by as much as its content is pushed in, so nothing moves.
    private static readonly Thickness ScrollInset = new(4, 4, 16, 4);
    private static readonly Thickness ScrollOutset = new(-4, -4, 0, -4);

    private readonly Queue<Func<Task>> _waiting = new();
    private bool _showing;
    private Action? _cancelCurrent;

    public Control? CurrentDialog { get; private set; }

    public DialogHost()
    {
        IsVisible = false;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || _cancelCurrent is null) return;
            _cancelCurrent();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => Fit();
    }

    private const double EdgeGap = 8;
    private double _ownMaxWidth = double.PositiveInfinity;
    private double _ownMaxHeight = double.PositiveInfinity;

    // A dialog is as wide as it was made to be, unless the screen is narrower: a phone held upright has less
    // room than most dialogs ask for, and one that sticks out past both edges cannot be read or cancelled.
    private void Fit()
    {
        if (CurrentDialog is not { } dialog || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        dialog.MaxWidth = Math.Min(_ownMaxWidth, Math.Max(1, Bounds.Width - 2 * EdgeGap));
        dialog.MaxHeight = Math.Min(_ownMaxHeight, Math.Max(1, Bounds.Height - 2 * EdgeGap));
    }

    private Task<T> Enqueue<T>(T cancelled, Func<TaskCompletionSource<T>, Control> build)
    {
        var result = new TaskCompletionSource<T>();
        _waiting.Enqueue(async () =>
        {
            Control dialog = build(result);
            CurrentDialog = dialog;
            _cancelCurrent = () => result.TrySetResult(cancelled);
            Children.Add(new Border { Background = ElioColors.Scrim });
            _ownMaxWidth = dialog.MaxWidth;
            _ownMaxHeight = dialog.MaxHeight;
            Children.Add(WithCloseButton(dialog, () => result.TrySetResult(cancelled)));
            IsVisible = true;
            Fit();

            // Whatever way the dialog ends, it comes down. One whose work failed used to stay up for good, over
            // everything, with no dialog able to follow it; the failure itself reaches whoever asked for the dialog.
            await ((Task)result.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            Children.Clear();
            CurrentDialog = null;
            _cancelCurrent = null;
            IsVisible = false;
        });
        _ = Pump();
        return result.Task;
    }

    /// <summary>
    /// Puts a small close button on the dialog's top corner. Closing that way is the same as cancelling, so every
    /// dialog has a way out that does not depend on finding the right button.
    /// </summary>
    private static Grid WithCloseButton(Control dialog, Action close)
    {
        // Drawn plainly, on a fill of its own: a faint mark in the corner was easy to miss.
        Button button = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.Text, 16, 1.8), L.T("Close"), 30);
        button.Background = ElioColors.Control;
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Margin = new Thickness(0, 12, 12, 0);
        button.Tag = "dialog-close";
        button.Click += (_, _) => close();

        // The holder stands where the dialog stood, so the button follows the dialog's corner wherever that is.
        var holder = new Grid { HorizontalAlignment = dialog.HorizontalAlignment, VerticalAlignment = dialog.VerticalAlignment, Margin = dialog.Margin };
        dialog.Margin = default;
        holder.Children.Add(dialog);
        holder.Children.Add(button);
        return holder;
    }

    private async Task Pump()
    {
        if (_showing) return;
        _showing = true;
        while (_waiting.TryDequeue(out Func<Task>? next)) await next();
        _showing = false;
    }

    private static Button MakeButton(string text, bool primary, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(16, 6),
            MinWidth = 76,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
            Tag = primary || danger ? "primary" : "secondary",
        };

        // The class is what keeps the color while the pointer is over the button. Without it the theme's grey takes over.
        if (danger || primary)
        {
            button.Background = danger ? ElioColors.Danger : ElioColors.Accent;
            button.Foreground = ElioColors.AccentText;
            button.Classes.Add(danger ? "danger" : "primary");
        }

        return button;
    }

    private static Border Card(Control child, double width = 440) => new()
    {
        Background = ElioColors.Elevated,
        BorderBrush = ElioColors.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(ElioStyle.DialogRadius),
        BoxShadow = ElioStyle.DialogShadow,
        Padding = new Thickness(24, 22, 24, 20),
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = child,
    };

    private static Control Frame(string title, Control body, params Button[] buttons)
    {
        return Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Title(title),
                body,
                Actions(buttons),
            },
        });
    }

    // The buttons at the foot of a dialog, against its right edge. On a screen too narrow for them in one
    // row they go on to a second one rather than off the side.
    private static WrapPanel Actions(params Button[] buttons)
    {
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (Button button in buttons) actions.Children.Add(button);
        return actions;
    }

    // Leaves room at the right for the close button every dialog carries in that corner.
    private static TextBlock Title(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 36, 0) };

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 0, 5) };

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ElioColors.Text, LineHeight = 19 };

    private static TextBox Input(string? value = null, bool secret = false, string? tag = null) => new()
    {
        Text = value,
        PasswordChar = secret ? '•' : default,
        Tag = tag,
    };

    private static TextBox MultiLine(string? value, string tag) => new()
    {
        Text = value,
        AcceptsReturn = true,
        MinHeight = 64,
        MaxHeight = 140,
        TextWrapping = TextWrapping.NoWrap,
        Tag = tag,
    };

    private static CheckBox Check(string text, bool value, string tag) => new() { Content = text, IsChecked = value, Foreground = ElioColors.Text, Tag = tag };

    private static StackPanel Field(string label, Control input) => new() { Children = { Label(label), input } };

    private static void SubmitOnEnter(Control input, Action submit) => input.KeyDown += (_, e) =>
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        submit();
    };

    private static void FocusWhenShown(Control input) => input.AttachedToVisualTree += (_, _) => input.Focus();

    /// <returns>What was entered, or null if cancelled.</returns>
    public Task<SecretAnswer?> AskSecretAsync(string title, string message, bool offerRemember) => Enqueue<SecretAnswer?>(null, result =>
    {
        TextBox input = Input(secret: true, tag: "secret");
        var remember = new CheckBox { Content = L.T("Store in the encrypted vault"), IsChecked = true, IsVisible = offerRemember, Foreground = ElioColors.Text, Tag = "remember" };
        Button ok = MakeButton(L.T("Connect"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        void Submit() => result.TrySetResult(new SecretAnswer(input.Text ?? string.Empty, remember.IsChecked == true));
        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(input, Submit);
        FocusWhenShown(input);
        return Frame(title, new StackPanel { Spacing = 10, Children = { Paragraph(message), input, remember } }, cancel, ok);
    });

    /// <returns>The text entered, or null if cancelled.</returns>
    public Task<string?> AskTextAsync(string title, string label, string? initial, string confirmText) => Enqueue<string?>(null, result =>
    {
        TextBox input = Input(initial, tag: "text");
        Button ok = MakeButton(confirmText, primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        void Submit()
        {
            if (!string.IsNullOrWhiteSpace(input.Text)) result.TrySetResult(input.Text.Trim());
        }

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(input, Submit);
        FocusWhenShown(input);
        return Frame(title, Field(label, input), cancel, ok);
    });

    /// <summary>
    /// Shows a picture that was just taken, with what one usually wants next: copy it again, open it, or see where it went.
    /// </summary>
    /// <param name="copy">Puts the picture on the clipboard.</param>
    /// <param name="open">Opens the saved file, or the folder it is in when asked for that.</param>
    public Task ShowPictureAsync(string title, Avalonia.Media.Imaging.Bitmap picture, string path, Func<Task> copy, Action<bool> open) => Enqueue(false, result =>
    {
        const double MaxWidth = 720, MaxHeight = 400, MinCardWidth = 440, Padding = 48;

        Button folder = MakeButton(L.T("Show in folder"), primary: false);
        Button file = MakeButton(L.T("Open"), primary: false);
        Button again = MakeButton(L.T("Copy"), primary: true);
        folder.Click += (_, _) => open(true);
        file.Click += (_, _) => open(false);
        again.Click += async (_, _) =>
        {
            await copy();
            result.TrySetResult(true);
        };

        // The picture is shown whole and never enlarged, and the window is made to fit it: a tall narrow terminal
        // gets a narrow window rather than a wide one with empty bands beside the picture.
        double scale = Math.Min(1, Math.Min(MaxWidth / Math.Max(1, picture.Size.Width), MaxHeight / Math.Max(1, picture.Size.Height)));
        double width = Math.Max(1, picture.Size.Width * scale);
        double height = Math.Max(1, picture.Size.Height * scale);

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border
                {
                    Width = width,
                    Height = height,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
                    BoxShadow = ElioStyle.PopoverShadow,
                    ClipToBounds = true,
                    Child = new Image { Source = picture, Stretch = Stretch.Uniform, Tag = "picture" },
                },
                new TextBlock
                {
                    Text = L.T("Copied. Saved as {0}", System.IO.Path.GetFileName(path)),
                    FontSize = 12,
                    Foreground = ElioColors.TextMuted,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        Control frame = Frame(title, body, folder, file, again);
        if (frame is Border card) card.Width = Math.Max(MinCardWidth, width + Padding);
        return frame;
    });

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string? cancelText = null) => Enqueue(false, result =>
    {
        Button ok = MakeButton(confirmText, primary: !danger, danger: danger);
        Button cancel = MakeButton(cancelText ?? L.T("Cancel"), primary: false);
        ok.Click += (_, _) => result.TrySetResult(true);
        cancel.Click += (_, _) => result.TrySetResult(false);
        return Frame(title, Paragraph(message), cancel, ok);
    });

    /// <summary>Asks which of a few ways to go on. The first is offered as the usual one.</summary>
    /// <returns>The place of the chosen one in the list, or -1 if cancelled.</returns>
    public Task<int> AskChoiceAsync(string title, string message, params string[] choices) => Enqueue(-1, result =>
    {
        // One under the other, since two long labels and Cancel do not fit side by side in every language.
        var body = new StackPanel { Spacing = 8, Children = { Paragraph(message) } };
        for (int i = 0; i < choices.Length; i++)
        {
            int choice = i;
            Button button = MakeButton(choices[i], primary: i == 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => result.TrySetResult(choice);
            body.Children.Add(button);
        }

        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        cancel.Click += (_, _) => result.TrySetResult(-1);
        return Frame(title, body, cancel);
    });

    /// <returns>The host as edited, or null if cancelled.</returns>
    public Task<HostEntry?> EditHostAsync(HostEntry? existing, IReadOnlyList<(Guid? Id, string Name)> groups, IReadOnlyList<HostEntry> allHosts) => Enqueue<HostEntry?>(null, result =>
    {
        TextBox name = Input(existing?.Name, tag: "name");
        TextBox address = Input(existing?.Address, tag: "address");
        // An empty port is "the usual one": 22, or what the server's group says. A server that had its own keeps it.
        TextBox port = Input(existing is { Port: > 0 } ? existing.Port.ToString() : null, tag: "port");
        TextBox user = Input(existing?.User, tag: "user");
        TextBox key = Input(existing?.KeyPath, tag: "key");

        // The keychain's identities, led by "none", which is also where an identity the group supplies is shown.
        HostLibrary library = AppServices.Library;
        List<Identity> identities = library.Identities.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var identity = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "identity" };
        int identityIndex = 1 + identities.FindIndex(i => i.Id == existing?.IdentityId);
        var proxy = new ProxyFields(existing?.Proxy, forServer: true, existing is not null && !AppServices.Vault.IsLocked && AppServices.Vault.Get(ProxySettings.SecretId(existing.Id)) is not null);
        TextBox tags = Input(existing is null ? null : string.Join(", ", existing.Tags), tag: "tags");
        var group = new ComboBox { ItemsSource = groups.Select(g => g.Name).ToList(), SelectedIndex = Math.Max(0, groups.ToList().FindIndex(g => g.Id == existing?.GroupId)), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "group" };
        var error = new TextBlock { Foreground = ElioColors.Danger, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        var kind = new ComboBox { ItemsSource = new[] { "SSH", "Telnet", L.T("Serial port"), L.T("Local shell") }, SelectedIndex = (int)(existing?.Kind ?? HostKind.Ssh), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "kind" };

        SerialSettings line = existing?.Serial ?? new SerialSettings();
        int[] dataBitChoices = [5, 6, 7, 8];
        ComboBox Choice(string[] items, int selected, string tag) => new() { ItemsSource = items, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = tag };
        TextBox baud = Input(line.BaudRate.ToString(), tag: "baud");
        TextBox folder = Input(existing?.Directory, tag: "folder");
        TextBox variables = MultiLine(existing is null ? null : string.Join('\n', existing.Environment.Select(v => $"{v.Key}={v.Value}")), "environment");
        var local = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Field(L.T("Starting folder (empty for your home folder)"), folder),
                Field(L.T("Environment variables (one per line: NAME=value)"), variables),
            },
        };
        ComboBox dataBits = Choice(["5", "6", "7", "8"], Math.Max(0, Array.IndexOf(dataBitChoices, line.DataBits)), "data-bits");
        ComboBox parity = Choice([L.T("None"), L.T("Odd"), L.T("Even")],(int)line.Parity, "parity");
        ComboBox stopBits = Choice(["1", "2"], line.StopBits == 2 ? 1 : 0, "stop-bits");
        ComboBox flow = Choice([L.T("None"), L.T("Hardware (RTS/CTS)"), L.T("Software (XON/XOFF)")],(int)line.Flow, "flow");
        ComboBox enter = Choice(["CR", "LF", "CR+LF"], (int)line.Enter, "enter");
        CheckBox lineFeeds = Check(L.T("Show incoming LF as CR+LF"),line.LfToCrLf, "lf-crlf");
        CheckBox hexDump = Check(L.T("Show incoming data as a hex dump"),line.HexDump, "hex-dump");

        var frameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*") };
        Control[] frameFields = [Field(L.T("Data bits"), dataBits), Field(L.T("Parity"), parity), Field(L.T("Stop bits"), stopBits)];
        for (int i = 0; i < frameFields.Length; i++)
        {
            Grid.SetColumn(frameFields[i], i * 2);
            frameRow.Children.Add(frameFields[i]);
        }

        var serial = new StackPanel
        {
            Spacing = 12,
            Children = { Field(L.T("Baud rate"), baud), frameRow, Field(L.T("Flow control"), flow), Field(L.T("Enter key sends"), enter), lineFeeds, hexDump },
        };

        var agent = new InheritableCheck(L.T("Use the keys in the SSH agent"), existing?.UseAgent == true, "agent");
        CheckBox forwardAgent = Check(L.T("Let the server use my SSH agent (agent forwarding)"), existing?.ForwardAgent == true, "forward-agent");
        CheckBox mcpCommands = Check(L.T("AI tools may run commands on this server"), existing?.AllowMcpCommands == true, "mcp-commands");

        // The box above is one of two switches, and the other one is in Settings. Ticked with that one off it
        // would look allowed and do nothing, so the form says so and offers to turn the other on from here.
        CheckBox mcpEverywhere = Check(L.T("Switch it on in Settings too"), false, "mcp-everywhere");
        var mcpNote = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(26, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = L.T("This does nothing yet: running commands is switched off for all AI tools in Settings."), Foreground = ElioColors.Accent, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                mcpEverywhere,
            },
        };
        void ShowMcpNote() => mcpNote.IsVisible = mcpCommands.IsChecked == true && !AppServices.Settings.McpRunCommands;
        mcpCommands.IsCheckedChanged += (_, _) => ShowMcpNote();
        ShowMcpNote();
        CheckBox forwardX11 = Check(L.T("Show the server's graphical programs here (X11 forwarding)"), existing?.ForwardX11 == true, "forward-x11");
        var mosh = new InheritableCheck(L.T("Use Mosh, which survives sleep and changing networks (the server needs mosh-server)"), existing?.UseMosh == true, "mosh");
        CheckBox reconnect = Check(L.T("Reconnect automatically if the connection drops"),existing?.AutoReconnect ?? true, "reconnect");
        string jumpNames = existing is null ? string.Empty : string.Join(", ", existing.JumpHosts.Select(id => allHosts.FirstOrDefault(h => h.Id == id)?.Name).OfType<string>());
        TextBox jumps = Input(jumpNames, tag: "jumps");
        TextBox forwards = MultiLine(existing is null ? null : string.Join('\n', existing.Forwards), "forwards");
        TextBox script = MultiLine(existing?.LoginScript, "script");
        TextBox remote = Input(existing?.RemoteCommand, tag: "remote");

        Control jumpsField = Field(L.T("Jump hosts (in order, by saved name, separated by commas)"), jumps);
        Control forwardsField = Field(L.T("Port forwards (one per line: L 8080:localhost:80, R 9000:localhost:3000, D 1080)"), forwards);
        Control remoteField = Field(L.T("Remote command (runs instead of the shell)"), remote);
        var advanced = new StackPanel
        {
            Spacing = 12,
            IsVisible = existing is not null && (existing.UseAgent || existing.ForwardAgent || existing.AllowMcpCommands || existing.ForwardX11 || existing.UseMosh || !existing.AutoReconnect || existing.JumpHosts.Count > 0 || existing.Forwards.Count > 0 || existing.LoginScript is not null || existing.RemoteCommand is not null || existing.Proxy is not null),
            Children =
            {
                agent.Box,
                forwardAgent,
                forwardX11,
                mosh.Box,
                reconnect,
                mcpCommands,
                mcpNote,
                jumpsField,
                proxy.View,
                forwardsField,
                Field(L.T("Login script (typed line by line after connecting)"), script),
                remoteField,
            },
        };
        var toggleAdvanced = new Button { Content = L.T("Advanced"), Padding = new Thickness(12, 4), HorizontalAlignment = HorizontalAlignment.Left, Tag = "advanced" };
        toggleAdvanced.Click += (_, _) => advanced.IsVisible = !advanced.IsVisible;

        void Fail(string message, bool inAdvanced)
        {
            error.Text = message;
            error.IsVisible = true;
            if (inAdvanced) advanced.IsVisible = true;
        }

        void Submit()
        {
            var chosen = (HostKind)kind.SelectedIndex;
            bool ssh = chosen == HostKind.Ssh;
            bool network = chosen is HostKind.Ssh or HostKind.Telnet;
            string host = address.Text?.Trim() ?? string.Empty;
            string account = user.Text?.Trim() ?? string.Empty;
            int number = 0;

            // A user name or a port left empty is fine where the identity or the group supplies one.
            ResolvedHost inherited = Inherited(withIdentity: true);
            bool portGiven = !string.IsNullOrWhiteSpace(port.Text);
            if (host.Length == 0 || (ssh && account.Length == 0 && inherited.User.Length == 0) || (network && portGiven && (!int.TryParse(port.Text, out number) || number is < 1 or > 65535)))
            {
                Fail(chosen switch
                {
                    HostKind.Ssh => L.T("An address, a user name and a valid port are required."),
                    HostKind.Telnet => L.T("An address and a valid port are required."),
                    HostKind.Serial => L.T("A port name is required."),
                    _ => L.T("A command to run is required."),
                }, inAdvanced: false);
                return;
            }

            // Zero is "the group's port", and is only saved where a group really has one to give. Everywhere
            // else the usual port is written out, as it always was.
            if (network && !portGiven) number = !ssh ? 23 : inherited.PortFrom is null ? ConnectionResolver.DefaultSshPort : 0;

            ProxySettings? through = null;
            if (ssh && !proxy.TryRead(out through, out string proxyProblem))
            {
                Fail(proxyProblem, inAdvanced: true);
                return;
            }

            SerialSettings? serialSettings = null;
            if (chosen == HostKind.Serial)
            {
                if (!int.TryParse(baud.Text, out int speed) || speed < 1)
                {
                    Fail(L.T("The baud rate must be a positive number."),inAdvanced: false);
                    return;
                }

                serialSettings = new SerialSettings
                {
                    BaudRate = speed,
                    DataBits = dataBitChoices[Math.Max(0, dataBits.SelectedIndex)],
                    Parity = (SerialParity)Math.Max(0, parity.SelectedIndex),
                    StopBits = stopBits.SelectedIndex == 1 ? 2 : 1,
                    Flow = (SerialFlow)Math.Max(0, flow.SelectedIndex),
                    Enter = (SerialEnter)Math.Max(0, enter.SelectedIndex),
                    LfToCrLf = lineFeeds.IsChecked == true,
                    HexDump = hexDump.IsChecked == true,
                };
            }

            var environment = new Dictionary<string, string>();
            foreach (string variable in chosen == HostKind.Local ? (variables.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [])
            {
                int equals = variable.IndexOf('=');
                if (equals < 1)
                {
                    Fail(L.T("This is not a variable: {0}", variable), inAdvanced: false);
                    return;
                }

                environment[variable[..equals].Trim()] = variable[(equals + 1)..];
            }

            var jumpIds = new List<Guid>();
            foreach (string jumpName in ssh ? (jumps.Text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [])
            {
                HostEntry? hop = allHosts.FirstOrDefault(h => h.Id != existing?.Id && h.Name.Equals(jumpName, StringComparison.CurrentCultureIgnoreCase));
                if (hop is null)
                {
                    Fail(L.T("Jump host not found: {0}", jumpName),inAdvanced: true);
                    return;
                }

                jumpIds.Add(hop.Id);
            }

            var parsedForwards = new List<PortForward>();
            foreach (string forwardLine in ssh ? (forwards.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [])
            {
                if (PortForward.TryParse(forwardLine) is not PortForward forward)
                {
                    Fail(L.T("Could not read the port forward: {0}", forwardLine),inAdvanced: true);
                    return;
                }

                parsedForwards.Add(forward);
            }

            // Asked for on this very form, with the reason in front of the user: the other half of the permission.
            if (ssh && mcpCommands.IsChecked == true && mcpEverywhere.IsChecked == true && !AppServices.Settings.McpRunCommands)
            {
                AppServices.Settings.McpRunCommands = true;
                AppServices.SaveSettings();
            }

            var entry = new HostEntry
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Kind = chosen,
                Name = string.IsNullOrWhiteSpace(name.Text) ? host : name.Text.Trim(),
                Address = host,
                Port = network ? number : 0,
                User = ssh ? account : string.Empty,
                KeyPath = !ssh || string.IsNullOrWhiteSpace(key.Text) ? null : key.Text.Trim(),
                IdentityId = ssh && identity.SelectedIndex > 0 ? identities[identity.SelectedIndex - 1].Id : null,
                GroupId = group.SelectedIndex >= 0 ? groups[group.SelectedIndex].Id : null,
                Tags = (tags.Text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                UseAgent = ssh && agent.Own,
                ForwardAgent = ssh && forwardAgent.IsChecked == true,
                AllowMcpCommands = ssh && mcpCommands.IsChecked == true,
                ForwardX11 = ssh && forwardX11.IsChecked == true,
                UseMosh = ssh && mosh.Own,
                Proxy = through,

                // A shell that was closed has nothing to come back to.
                AutoReconnect = chosen != HostKind.Local && reconnect.IsChecked == true,
                JumpHosts = jumpIds,
                Forwards = parsedForwards,
                LoginScript = string.IsNullOrWhiteSpace(script.Text) ? null : script.Text.Trim(),
                RemoteCommand = !ssh || string.IsNullOrWhiteSpace(remote.Text) ? null : remote.Text.Trim(),
                Serial = serialSettings,
                Directory = chosen != HostKind.Local || string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim(),
                Environment = environment,
            };
            proxy.Store(entry.Id, through);
            result.TrySetResult(entry);
        }

        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        foreach (TextBox box in new[] { name, address, port, user, key, tags, jumps, remote, baud }) SubmitOnEnter(box, Submit);
        FocusWhenShown(name);

        var addressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,90") };
        StackPanel addressField = Field(L.T("Address"), address);
        Control portField = Field(L.T("Port"), port);
        Grid.SetColumn(portField, 2);
        addressRow.Children.Add(addressField);
        addressRow.Children.Add(portField);
        Control identityField = Field(L.T("Identity"), identity);
        Control userField = Field(L.T("User name"), user);
        Control keyField = Field(L.T("Private key file (optional)"), key);

        // What an SSH server with these choices would connect with if it said nothing itself: with the identity
        // picked here, or without it, which is what the first entry of the identity list has to show.
        ResolvedHost Inherited(bool withIdentity) => ConnectionResolver.Resolve(
            new HostEntry
            {
                Id = existing?.Id ?? Guid.Empty,
                Kind = HostKind.Ssh,
                Port = 0,
                GroupId = group.SelectedIndex >= 0 ? groups[group.SelectedIndex].Id : null,
                IdentityId = withIdentity && identity.SelectedIndex > 0 ? identities[identity.SelectedIndex - 1].Id : null,

                // A key file of the server's own keeps the group's identity out, and with it that identity's user name.
                KeyPath = withIdentity && !string.IsNullOrWhiteSpace(key.Text) ? key.Text.Trim() : null,
            },
            library);

        // Shows, in the fields a server may leave empty, what then applies and which group it comes from.
        bool showingInherited = false;
        void ShowInherited()
        {
            if (showingInherited) return;
            showingInherited = true;

            ResolvedHost fromGroup = Inherited(withIdentity: false);
            int chosenIdentity = identity.ItemsSource is null ? identityIndex : Math.Max(0, identity.SelectedIndex);
            identity.ItemsSource = identities.Select(i => i.Name)
                .Prepend(fromGroup.Identity is null ? L.T("None (enter below)") : FromGroup(fromGroup.Identity.Name, fromGroup.IdentityFrom))
                .ToList();
            identity.SelectedIndex = chosenIdentity;

            ResolvedHost applies = Inherited(withIdentity: true);
            bool ssh = (HostKind)kind.SelectedIndex == HostKind.Ssh;
            bool own = identity.SelectedIndex > 0;

            // An identity stands in for the key file and the password, and for the user name when it has one.
            userField.IsVisible = ssh && !(own && identities[identity.SelectedIndex - 1].User.Length > 0);
            keyField.IsVisible = ssh && !own;
            user.PlaceholderText = applies.User.Length == 0 ? null : FromGroup(applies.User, applies.UserFrom);
            port.PlaceholderText = ssh ? FromGroup(applies.Port.ToString(), applies.PortFrom) : "23";
            jumps.PlaceholderText = applies.JumpHosts.Count == 0 ? null : FromGroup(JumpHostNames(applies.JumpHosts, allHosts), applies.JumpHostsFrom);
            agent.ShowInherited(fromGroup.AgentFrom);
            mosh.ShowInherited(fromGroup.MoshFrom);
            proxy.SetInherited(fromGroup.Proxy is { } inheritedProxy
                ? FromGroup($"{(inheritedProxy.Kind == ProxyKind.Http ? "HTTP CONNECT" : "SOCKS5")} {inheritedProxy.Host}:{inheritedProxy.Port}", fromGroup.ProxyFrom)
                : null);

            showingInherited = false;
        }

        group.SelectionChanged += (_, _) => ShowInherited();
        identity.SelectionChanged += (_, _) => ShowInherited();
        agent.Box.IsCheckedChanged += (_, _) => ShowInherited();
        mosh.Box.IsCheckedChanged += (_, _) => ShowInherited();

        // Shows the fields that mean something for the chosen kind of connection.
        void ShowKind()
        {
            var chosen = (HostKind)kind.SelectedIndex;
            bool ssh = chosen == HostKind.Ssh;
            bool network = chosen is HostKind.Ssh or HostKind.Telnet;

            IReadOnlyList<string> ports = chosen == HostKind.Serial ? SerialTransport.PortNames() : [];
            ((TextBlock)addressField.Children[0]).Text = chosen switch
            {
                HostKind.Serial => ports.Count == 0 ? L.T("Port (for example COM3 or /dev/ttyUSB0)") : L.T("Port (on this computer: {0})", string.Join(", ", ports)),
                HostKind.Local => L.T("Command (for example C:\\msys64\\usr\\bin\\bash.exe --login -i)"),
                _ => L.T("Address"),
            };
            Grid.SetColumnSpan(addressField, network ? 1 : 3);
            portField.IsVisible = network;
            identityField.IsVisible = ssh;
            serial.IsVisible = chosen == HostKind.Serial;
            local.IsVisible = chosen == HostKind.Local;
            agent.Box.IsVisible = forwardAgent.IsVisible = mcpCommands.IsVisible = forwardX11.IsVisible = mosh.Box.IsVisible = jumpsField.IsVisible = forwardsField.IsVisible = remoteField.IsVisible = proxy.View.IsVisible = ssh;
            reconnect.IsVisible = chosen != HostKind.Local;

            // Follows the kind only while the port is still the other kind's usual one.
            if (chosen == HostKind.Telnet && port.Text == "22") port.Text = "23";
            else if (ssh && port.Text == "23") port.Text = "22";

            // The user name and key fields are shown or hidden there, by the kind and by the identity together.
            ShowInherited();
        }

        kind.SelectionChanged += (_, _) => ShowKind();
        ShowKind();

        return Frame(
            existing is null ? L.T("Add server") : L.T("Edit server"),
            new ScrollViewer
            {
                MaxHeight = 500,
                Margin = ScrollOutset,
                Content = new StackPanel
                {
                    Margin = ScrollInset,
                    Spacing = 12,
                    Children =
                    {
                        Field(L.T("Type"), kind),
                        Field(L.T("Name"), name),
                        addressRow,
                        identityField,
                        userField,
                        keyField,
                        serial,
                        local,
                        Field(L.T("Group"), group),
                        Field(L.T("Tags (separated by commas)"), tags),
                        toggleAdvanced,
                        advanced,
                        error,
                    },
                },
            },
            cancel,
            save);
    });

    /// <summary>Lets the user change the settings in place. Returns true if they were saved.</summary>
    public Task<bool> EditSettingsAsync(AppSettings settings) => Enqueue(false, result =>
    {
        var themes = TerminalTheme.All.Select(t => t.Name).ToList();
        var theme = new ComboBox { ItemsSource = themes, SelectedIndex = Math.Max(0, themes.IndexOf(settings.Theme)), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "theme" };

        // The first choice follows the operating system. Each language is listed under its own name.
        var languageNames = new List<string> { L.T("Same as the system") };
        languageNames.AddRange(L.Languages.Select(l => l.Name));
        int currentLanguage = settings.Language is null ? 0 : 1 + Math.Max(0, L.Languages.ToList().FindIndex(l => l.Code == settings.Language));
        var language = new ComboBox { ItemsSource = languageNames, SelectedIndex = currentLanguage, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "language" };
        TextBox size = Input(settings.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture), tag: "size");
        CheckBox ligatures = Check(L.T("Use font ligatures"), settings.Ligatures, "ligatures");
        CheckBox restore = Check(L.T("Restore tabs at startup"), settings.RestoreSessions, "restore");
        CheckBox copy = Check(L.T("Copy on select"), settings.CopyOnSelect, "copy");
        CheckBox paste = Check(L.T("Paste with right click"), settings.RightClickPaste, "paste");
        CheckBox light = Check(L.T("Light interface (applies after a restart)"), settings.LightInterface, "light");
        CheckBox followSystem = Check(L.T("Switch theme when the system goes light or dark"), settings.FollowSystemTheme, "follow-system");
        var lightTheme = new ComboBox { ItemsSource = themes, SelectedIndex = Math.Max(0, themes.IndexOf(settings.LightTheme)), HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "light-theme" };
        CheckBox groupTabs = Check(L.T("Keep tabs of the same repository together"), settings.GroupTabsByRepository, "group-tabs");
        TextBox shareServer = Input(settings.ShareServer, tag: "share-server");
        shareServer.PlaceholderText = AppServices.Sync.RelayServer.ToString();
        CheckBox keepShells = Check(L.T("Keep local shells running when Elio closes, and pick them up again"), settings.KeepShellsRunning, "keep-shells");
        CheckBox suggestions = Check(L.T("Suggest commands typed before while typing (Right arrow accepts, Alt with Up or Down chooses)"), settings.SuggestCommands, "suggestions");
        CheckBox seedSuggestions = Check(L.T("Start suggestions from the shell's own history file"), settings.SeedSuggestionsFromShellHistory, "seed-suggestions");
        TextBox editor = Input(settings.EditorCommand, tag: "editor");
        editor.PlaceholderText = L.T("A program's path or name, such as {0} or {1}", "code", @"C:\Program Files\Notepad++\notepad++.exe");

        // A phone gets its versions from the store, so nothing is asked there and there is nothing to turn off.
        CheckBox updates = Check(L.T("Check for new versions when Elio starts"), settings.CheckForUpdates, "updates");
        updates.IsVisible = !AppServices.MobileLayout;
        Vault vault = AppServices.Vault;
        bool helloOffered = OperatingSystem.IsWindows() && vault.HelloSupported && (vault.HelloEnabled || WindowsHelloProtector.IsAvailable());
        CheckBox hello = Check(L.T("Ask for Windows Hello before saved passwords are used"), helloOffered && vault.HelloEnabled, "hello");
        hello.IsVisible = helloOffered;

        // Only where the device can check who is holding it. The setting is not written here: it changes only
        // after that check has succeeded, which happens once the dialog is out of its way.
        CheckBox lockApp = Check(L.T("Lock Elio with this device's fingerprint, face or passcode"), settings.LockApp, "lock-app");
        lockApp.IsVisible = AppServices.Authenticate is not null;
        CheckBox mcp = Check(L.T("Let AI tools connected with \"elio --mcp\" run commands on your servers"), settings.McpRunCommands, "mcp");
        TextBox summon = Input(settings.SummonKey, tag: "summon");
        CheckBox fromTop = Check(L.T("Drop the window down from the top of the screen"), settings.SummonFromTop, "summon-top");
        TextBox highlights = MultiLine(string.Join('\n', settings.Highlights.Select(FormatHighlight)), "highlights");
        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        save.Click += (_, _) =>
        {
            settings.Theme = themes[Math.Max(0, theme.SelectedIndex)];
            settings.Language = language.SelectedIndex <= 0 ? null : L.Languages[language.SelectedIndex - 1].Code;
            if (double.TryParse(size.Text, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) settings.FontSize = Math.Clamp(parsed, 8, 32);
            settings.Ligatures = ligatures.IsChecked == true;
            settings.RestoreSessions = restore.IsChecked == true;
            settings.CopyOnSelect = copy.IsChecked == true;
            settings.RightClickPaste = paste.IsChecked == true;
            settings.LightInterface = light.IsChecked == true;
            settings.FollowSystemTheme = followSystem.IsChecked == true;
            settings.LightTheme = themes[Math.Max(0, lightTheme.SelectedIndex)];
            settings.SummonKey = string.IsNullOrWhiteSpace(summon.Text) ? null : summon.Text.Trim();
            settings.SummonFromTop = fromTop.IsChecked == true;
            settings.McpRunCommands = mcp.IsChecked == true;
            settings.GroupTabsByRepository = groupTabs.IsChecked == true;
            settings.ShareServer = string.IsNullOrWhiteSpace(shareServer.Text) ? null : shareServer.Text.Trim();
            settings.KeepShellsRunning = keepShells.IsChecked == true;
            settings.SuggestCommands = suggestions.IsChecked == true;
            settings.SeedSuggestionsFromShellHistory = seedSuggestions.IsChecked == true;
            settings.EditorCommand = (editor.Text ?? string.Empty).Trim();
            settings.CheckForUpdates = updates.IsChecked == true;
            if (helloOffered && (hello.IsChecked == true) != vault.HelloEnabled) _ = ChangeHelloAsync(vault, hello.IsChecked == true);
            if (lockApp.IsVisible && (lockApp.IsChecked == true) != settings.LockApp) _ = ChangeLockAsync(lockApp.IsChecked == true);
            settings.Highlights = (highlights.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseHighlight).ToList();
            result.TrySetResult(true);
        };
        cancel.Click += (_, _) => result.TrySetResult(false);

        return Frame(L.T("Settings"), new ScrollViewer { MaxHeight = 520, Margin = ScrollOutset, Content = new StackPanel { Margin = ScrollInset, Spacing = 12, Children = { Field(L.T("Language (applies after a restart)"), language), Field(L.T("Theme"), theme), followSystem, Field(L.T("Theme while the system is light"), lightTheme), Field(L.T("Font size"), size), light, ligatures, restore, updates, copy, paste, groupTabs, keepShells, suggestions, seedSuggestions, hello, lockApp, Field(L.T("Editor for remote files (empty for the system's)"), editor), Field(L.T("Server that passes shared terminals along (empty uses the sync server)"), shareServer), Field(L.T("Key that shows or hides the window from anywhere (for example Ctrl+Oem3, applies after a restart)"), summon), fromTop, mcp, Field(L.T("Text to highlight (one per line: #ff5555 error)"), highlights) } } }, cancel, save);
    });

    // Windows shows its own prompt, and waits for it, so this stays off the thread the window lives on.
    private async Task ChangeHelloAsync(Vault vault, bool enable)
    {
        try
        {
            await Task.Run(() =>
            {
                if (enable) vault.EnableHello();
                else vault.DisableHello();
            });
        }
        catch (VaultLockedException ex) when (ex.Reason == VaultLockReason.Cancelled)
        {
            // The user changed their mind, and the vault is as it was.
        }
        catch (VaultLockedException ex)
        {
            await ConfirmAsync("Windows Hello", ex.Message, L.T("Close"));
        }
    }

    // The device shows its own prompt. Refused or cancelled, the lock stays as it was, and the user is told so:
    // the box they ticked would otherwise seem to have been saved.
    private async Task ChangeLockAsync(bool on)
    {
        if (!await AppServices.Lock.SetOnAsync(on))
            await ConfirmAsync(L.T("Settings"), L.T("The device could not confirm that it is you, so the lock was left as it was."), L.T("Close"));
    }

    /// <summary>Lets the user change which keys run which command. Returns true if they were saved.</summary>
    /// <param name="actions">Each command's internal name and the name to show for it.</param>
    public Task<bool> EditShortcutsAsync(AppSettings settings, IReadOnlyList<(string Action, string Name)> actions) => Enqueue(false, result =>
    {
        var current = new ShortcutMap(settings.Shortcuts);
        var boxes = new Dictionary<string, TextBox>();
        var rows = new StackPanel { Spacing = 8 };
        foreach (var (action, name) in actions)
        {
            if (!ShortcutMap.Defaults.ContainsKey(action)) continue;

            TextBox box = Input(current.KeysFor(action), tag: "shortcut-" + action);
            boxes[action] = box;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,190") };
            Grid.SetColumn(box, 2);
            row.Children.Add(new TextBlock { Text = name, Foreground = ElioColors.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(box);
            rows.Children.Add(row);
        }

        var error = new TextBlock { Foreground = ElioColors.Danger, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        Button reset = MakeButton(L.T("Restore defaults"), primary: false);
        reset.Tag = "shortcuts-reset";
        reset.Click += (_, _) =>
        {
            foreach (var (action, box) in boxes) box.Text = ShortcutMap.Defaults[action];
        };

        save.Click += (_, _) =>
        {
            var changed = new Dictionary<string, string>();
            foreach (var (action, name) in actions)
            {
                if (!boxes.TryGetValue(action, out TextBox? box)) continue;

                string keys = string.Join(' ', (box.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (!ShortcutMap.IsValid(keys))
                {
                    error.Text = L.T("The keys for \"{0}\" could not be read: {1}", name, keys);
                    error.IsVisible = true;
                    return;
                }

                // Only what differs from the usual keys is kept, so later changes to the usual ones still arrive.
                if (new ShortcutMap(new Dictionary<string, string> { [action] = keys }).KeysFor(action) != new ShortcutMap(new Dictionary<string, string>()).KeysFor(action))
                    changed[action] = keys;
            }

            settings.Shortcuts = changed;
            result.TrySetResult(true);
        };
        cancel.Click += (_, _) => result.TrySetResult(false);

        return Frame(
            L.T("Keyboard shortcuts"),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Paragraph(L.T("Write keys like Ctrl+Shift+P. For a sequence of presses, put a space between them: Ctrl+K Ctrl+S.")),
                    new ScrollViewer { MaxHeight = 380, Margin = ScrollOutset, Content = new StackPanel { Margin = ScrollInset, Children = { rows } } },
                    error,
                },
            },
            reset,
            cancel,
            save);
    });

    private const uint DefaultHighlightColor = 0xE0AF68;

    private static string FormatHighlight(HighlightRule rule) => $"#{rule.Color:x6} {rule.Pattern}";

    private static HighlightRule ParseHighlight(string line)
    {
        int space = line.IndexOf(' ');
        if (line.StartsWith('#') && space == 7 && uint.TryParse(line.AsSpan(1, 6), System.Globalization.NumberStyles.HexNumber, null, out uint color))
            return new HighlightRule { Color = color, Pattern = line[8..].Trim() };

        return new HighlightRule { Color = DefaultHighlightColor, Pattern = line };
    }

    /// <summary>A searchable list of commands. Returns the chosen one, or null if dismissed.</summary>
    public Task<PaletteItem?> ChooseAsync(IReadOnlyList<PaletteItem> items, string placeholder) => Enqueue<PaletteItem?>(null, result =>
    {
        const int MaxVisible = 12;
        const double RowHeight = 32;

        TextBox search = ElioStyle.SearchBox(placeholder);
        search.Tag = "search";
        search.MinHeight = 34;
        var list = new StackPanel { Spacing = 1 };
        List<PaletteItem> shown = [];
        int selected = 0;

        void Render()
        {
            list.Children.Clear();
            for (int i = 0; i < shown.Count; i++)
            {
                PaletteItem item = shown[i];
                var row = new Border
                {
                    Background = i == selected ? ElioColors.Selection : ElioColors.Transparent,
                    CornerRadius = new CornerRadius(ElioStyle.RowRadius),
                    Padding = new Thickness(10, 0),
                    Height = RowHeight,
                    Child = new DockPanel
                    {
                        Children =
                        {
                            DockRight(new TextBlock { Text = item.Detail, Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }),
                            new TextBlock { Text = item.Title, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                };
                row.PointerPressed += (_, _) => result.TrySetResult(item);
                list.Children.Add(row);
            }
        }

        void Filter()
        {
            string text = search.Text?.Trim() ?? string.Empty;
            shown = items.Where(i => text.Length == 0 || i.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || (i.Detail?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)).Take(MaxVisible).ToList();
            selected = 0;
            Render();
        }

        search.TextChanged += (_, _) => Filter();
        search.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Down) selected = Math.Min(selected + 1, shown.Count - 1);
            else if (e.Key == Key.Up) selected = Math.Max(selected - 1, 0);
            else if (e.Key == Key.Enter)
            {
                if (shown.Count > 0) result.TrySetResult(shown[selected]);
                e.Handled = true;
                return;
            }
            else return;

            Render();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        Filter();
        FocusWhenShown(search);

        Border card = Card(new StackPanel { Spacing = 8, Children = { search, list } }, 580);
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Margin = new Thickness(0, 84, 0, 0);
        card.Padding = new Thickness(10);
        return card;
    });

    private static Control DockRight(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }
}
