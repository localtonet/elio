using Avalonia.Controls;
using Elio.Core;

namespace Elio.UI;

/// <summary>One thing a cloud provider wants to be told before it lists an account's servers.</summary>
/// <param name="Secret">Whether it is shown as dots while typed.</param>
/// <param name="Value">What the box starts out holding: what the vault remembered, if anything.</param>
/// <param name="Optional">Whether the form may be sent with this left empty.</param>
/// <param name="Placeholder">An example of what belongs in the box.</param>
public sealed record CredentialField(string Label, bool Secret, string? Value = null, bool Optional = false, string? Placeholder = null);

/// <summary>What was typed into each field, in the order the fields were given, and whether to keep it.</summary>
public sealed record CredentialAnswer(IReadOnlyList<string> Values, bool Remember);

/// <content>Asking for what a cloud import needs to sign in with.</content>
public sealed partial class DialogHost
{
    /// <summary>
    /// Asks for an account's credentials. They are asked for every time unless the user ticks the box that keeps
    /// them in the vault, which starts out unticked.
    /// </summary>
    /// <param name="offerRemember">Whether there is a vault to keep them in.</param>
    /// <param name="remembered">Whether what the boxes hold came from the vault, so the box that keeps them starts ticked.</param>
    /// <returns>What was entered, or null if cancelled.</returns>
    public Task<CredentialAnswer?> AskCredentialsAsync(string title, string note, IReadOnlyList<CredentialField> fields, bool offerRemember, bool remembered) => Enqueue<CredentialAnswer?>(null, result =>
    {
        var body = new StackPanel { Spacing = 12, Children = { Paragraph(note) } };
        var inputs = new List<TextBox>();
        foreach (CredentialField field in fields)
        {
            TextBox input = Input(field.Value, field.Secret, "credential-" + inputs.Count);
            input.PlaceholderText = field.Placeholder;
            inputs.Add(input);
            body.Children.Add(Field(field.Label, input));
        }

        CheckBox remember = Check(L.T("Remember in the vault"), remembered, "remember");
        remember.IsVisible = offerRemember;
        body.Children.Add(remember);

        Button ok = MakeButton(L.T("Import"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            List<string> values = inputs.Select(input => input.Text?.Trim() ?? string.Empty).ToList();

            // Left empty where something is needed, the form stays up with the keyboard in the box that wants filling.
            for (int i = 0; i < values.Count; i++)
            {
                if (fields[i].Optional || values[i].Length > 0) continue;
                inputs[i].Focus();
                return;
            }

            result.TrySetResult(new CredentialAnswer(values, offerRemember && remember.IsChecked == true));
        }

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        foreach (TextBox input in inputs) SubmitOnEnter(input, Submit);
        if (inputs.Count > 0) FocusWhenShown(inputs[0]);
        return Frame(title, body, cancel, ok);
    });
}
