using Elio.Core;
using Elio.Localtonet;

namespace Elio.Tests;

public sealed class DeviceEntriesTests
{
    private static readonly Guid Laptop = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Server = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static LocaltonetDevice Device(Guid id, string name, string? runAs = null) => new() { Id = id, Name = name, RunAsUser = runAs };

    private static Dictionary<Guid, HostEntry> Known(params HostEntry[] entries) => entries.ToDictionary(entry => entry.Id);

    private static List<HostEntry> Build(IEnumerable<LocaltonetDevice> devices, Dictionary<Guid, HostEntry>? previous = null, Func<Guid, bool>? hasKey = null) =>
        DeviceEntries.Build(devices, "acct", previous ?? [], hasKey ?? (_ => false));

    [Fact]
    public void ANewDevice_IsShownUnderItsOwnNameAndIsNotOpenToAiTools()
    {
        HostEntry entry = Assert.Single(Build([Device(Laptop, "laptop", "ferha")]));

        Assert.Equal(Laptop, entry.Id);
        Assert.Equal("laptop", entry.Name);
        Assert.Equal("laptop", entry.Address);
        Assert.Equal("ferha", entry.User);
        Assert.True(entry.Localtonet);
        Assert.Equal("acct", entry.LocaltonetAccountId);
        Assert.False(entry.AllowMcpCommands);
        Assert.False(DeviceEntries.IsRenamed(entry));
    }

    [Fact]
    public void ADeviceWithoutARunAsUser_IsGivenTheNameTheNodeLogsInWith() =>
        Assert.Equal("device", Assert.Single(Build([Device(Laptop, "laptop")])).User);

    [Fact]
    public void WhetherAnAccessKeyIsSaved_DecidesWhetherItReconnectsByItself()
    {
        List<HostEntry> entries = Build([Device(Laptop, "laptop"), Device(Server, "server")], hasKey: id => id == Server);

        Assert.False(entries.Single(e => e.Id == Laptop).AutoReconnect);
        Assert.True(entries.Single(e => e.Id == Server).AutoReconnect);
    }

    [Fact]
    public void WhenTheListIsAskedForAgain_TheNameTheUserGaveStays()
    {
        HostEntry before = Build([Device(Laptop, "laptop")]).Single();
        DeviceEntries.Customize(before, "Work laptop", allowMcpCommands: false);

        HostEntry after = Build([Device(Laptop, "laptop")], Known(before)).Single();

        Assert.Equal("Work laptop", after.Name);
        Assert.Equal("laptop", after.Address);
        Assert.True(DeviceEntries.IsRenamed(after));
    }

    [Fact]
    public void WhenTheListIsAskedForAgain_AiToolsStayAllowedOrNot()
    {
        HostEntry open = Build([Device(Laptop, "laptop")]).Single();
        DeviceEntries.Customize(open, null, allowMcpCommands: true);
        HostEntry closed = Build([Device(Server, "server")]).Single();

        List<HostEntry> after = Build([Device(Laptop, "laptop"), Device(Server, "server")], Known(open, closed));

        Assert.True(after.Single(e => e.Id == Laptop).AllowMcpCommands);
        Assert.False(after.Single(e => e.Id == Server).AllowMcpCommands);
    }

    [Fact]
    public void ADeviceLocaltonetRenames_FollowsItUnlessTheUserNamedItToo()
    {
        HostEntry plain = Build([Device(Laptop, "laptop")]).Single();
        HostEntry named = Build([Device(Server, "server")]).Single();
        DeviceEntries.Customize(named, "Build box", allowMcpCommands: false);

        List<HostEntry> after = Build([Device(Laptop, "laptop-2"), Device(Server, "server-2")], Known(plain, named));

        HostEntry followed = after.Single(e => e.Id == Laptop);
        Assert.Equal("laptop-2", followed.Name);
        Assert.Equal("laptop-2", followed.Address);

        HostEntry kept = after.Single(e => e.Id == Server);
        Assert.Equal("Build box", kept.Name);
        Assert.Equal("server-2", kept.Address);
    }

    [Fact]
    public void ADeviceThatIsNoLongerListed_IsLeftOut() =>
        Assert.Empty(Build([], Known(Build([Device(Laptop, "laptop")]).Single())));

    [Fact]
    public void TheEntriesAreOrderedByTheNameTheyAreShownUnder()
    {
        HostEntry plain = Build([Device(Laptop, "alpha")]).Single();
        DeviceEntries.Customize(plain, "Zulu", allowMcpCommands: false);

        List<HostEntry> after = Build([Device(Laptop, "alpha"), Device(Server, "mike")], Known(plain));

        Assert.Equal(["mike", "Zulu"], after.Select(e => e.Name));
    }

    [Fact]
    public void Customizing_WithoutAName_GoesBackToTheDevicesOwn()
    {
        HostEntry entry = Build([Device(Laptop, "laptop")]).Single();
        DeviceEntries.Customize(entry, "Work laptop", allowMcpCommands: true);

        DeviceEntries.Customize(entry, "   ", allowMcpCommands: true);

        Assert.Equal("laptop", entry.Name);
        Assert.False(DeviceEntries.IsRenamed(entry));
        Assert.True(entry.AllowMcpCommands);
    }

    [Fact]
    public void Customizing_TrimsTheNameAndNeverTouchesWhatConnectingAsksFor()
    {
        HostEntry entry = Build([Device(Laptop, "laptop")]).Single();

        DeviceEntries.Customize(entry, "  Work laptop ", allowMcpCommands: false);

        Assert.Equal("Work laptop", entry.Name);
        Assert.Equal("laptop", entry.Address);
    }
}
