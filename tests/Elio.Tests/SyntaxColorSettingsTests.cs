using System.Text.Json;
using Elio.Core;

namespace Elio.Tests;

/// <summary>The setting that colors the command being typed: on unless it is turned off, also for settings saved before it existed.</summary>
public sealed class SyntaxColorSettingsTests
{
    [Fact]
    public void NewSettings_ColorTheCommand_ButDoNotMarkUnknownCommandsRed()
    {
        var settings = new AppSettings();

        Assert.True(settings.HighlightCommands);
        Assert.False(settings.HighlightUnknownCommands);
    }

    [Fact]
    public void SettingsSavedBeforeTheOptionExisted_ColorTheCommand()
    {
        // A file written by an earlier version says nothing about it, and that must not read as "off".
        AppSettings? old = JsonSerializer.Deserialize<AppSettings>("""{ "SchemaVersion": 1, "Theme": "Elio", "SuggestCommands": false }""");

        Assert.NotNull(old);
        Assert.True(old.HighlightCommands);
        Assert.False(old.HighlightUnknownCommands);
    }

    [Fact]
    public void TurningItOff_IsRemembered()
    {
        var settings = new AppSettings { HighlightCommands = false, HighlightUnknownCommands = true };

        AppSettings? back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(back);
        Assert.False(back.HighlightCommands);
        Assert.True(back.HighlightUnknownCommands);
    }
}
