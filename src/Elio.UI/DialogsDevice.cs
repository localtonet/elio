using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Elio.Core;

namespace Elio.UI;

/// <summary>What the user decided about a Localtonet device: the name to show it under, and whether AI tools may run commands on it.</summary>
/// <param name="Name">Empty means the name Localtonet gives the device.</param>
public sealed record DeviceEdit(string Name, bool AllowMcpCommands);

public sealed partial class DialogHost
{
    /// <summary>
    /// Edits what is kept about a Localtonet device on this computer. The device is not changed at Localtonet:
    /// its name there is what connecting asks for, so the name given here is only what Elio shows.
    /// </summary>
    /// <returns>What was decided, or null if cancelled.</returns>
    public Task<DeviceEdit?> EditDeviceAsync(HostEntry device, bool hasAccessKey) => Enqueue<DeviceEdit?>(null, result =>
    {
        TextBox name = Input(device.Name, tag: "name");
        name.PlaceholderText = device.Address;
        FocusWhenShown(name);

        CheckBox mcpCommands = Check(L.T("AI tools may run commands on this server"), device.AllowMcpCommands, "mcp-commands");
        (Control mcpNote, CheckBox mcpEverywhere) = McpSettingsNote(mcpCommands);

        // Nobody is there to approve a connection in the browser when an AI tool asks, so without a key it could never get in.
        var keyNote = new TextBlock
        {
            Text = L.T("AI tools cannot approve a connection in the browser, so this device also needs an access key. Right-click it and choose Enter access key."),
            Foreground = ElioColors.Accent,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(26, 0, 0, 0),
            Tag = "mcp-key-note",
        };
        void ShowKeyNote() => keyNote.IsVisible = mcpCommands.IsChecked == true && !hasAccessKey;
        mcpCommands.IsCheckedChanged += (_, _) => ShowKeyNote();
        ShowKeyNote();

        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        void Submit()
        {
            SwitchOnMcpInSettings(mcpCommands, mcpEverywhere);
            result.TrySetResult(new DeviceEdit((name.Text ?? string.Empty).Trim(), mcpCommands.IsChecked == true));
        }

        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(name, Submit);

        return Frame(
            L.T("Edit {0}", device.Address),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Field(L.T("Name"), name),
                    Paragraph(L.T("This name is only kept on this computer. Localtonet still calls the device {0}.", device.Address)),
                    mcpCommands,
                    mcpNote,
                    keyNote,
                },
            },
            cancel,
            save);
    });
}
