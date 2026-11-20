using Elio.Terminal;

namespace Elio.Tests;

/// <summary>Commands in a folder that can be run from but not listed, as /system/bin is on Android.</summary>
public sealed class CommandIndexProbeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elio-tests", Guid.NewGuid().ToString("N"));

    public CommandIndexProbeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The system cleans its temp folder up in time.
        }
    }

    [Fact]
    public void AnExecutableFile_IsFoundByName_AndOneThatIsNotExecutableOrNotThereIsNot()
    {
        // Windows runs what its kind says, and has no folder that cannot be listed.
        if (OperatingSystem.IsWindows()) return;

        string tool = Path.Combine(_folder, "tool");
        File.WriteAllText(tool, "#!/bin/sh\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string data = Path.Combine(_folder, "notes");
        File.WriteAllText(data, "text");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.True(CommandIndex.IsInUnlisted([_folder], "tool"));
        Assert.False(CommandIndex.IsInUnlisted([_folder], "notes"));
        Assert.False(CommandIndex.IsInUnlisted([_folder], "nothing"));
        Assert.False(CommandIndex.IsInUnlisted([Path.Combine(_folder, "no-such-folder")], "tool"));
    }

    [Fact]
    public void ANameThatCannotBeAFileName_IsNotFound()
    {
        Assert.False(CommandIndex.IsInUnlisted([_folder], "a\0b"));
        Assert.False(CommandIndex.IsInUnlisted([], "tool"));
    }
}
