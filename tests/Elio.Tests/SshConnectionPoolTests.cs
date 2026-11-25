using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

public sealed class SshConnectionPoolTests
{
    [Fact]
    public void KeyOf_IsTheSameForTheSamePath_WhateverThePassword() =>
        Assert.Equal(
            SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy", Password: "one")]),
            SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy", Password: "two")]));

    [Fact]
    public void KeyOf_TellsApartUsersPortsJumpsAndProxies()
    {
        string plain = SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy")]);

        Assert.NotEqual(plain, SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "root")]));
        Assert.NotEqual(plain, SshConnectionPool.KeyOf([new SshEndpoint("web", 2222, "deploy")]));
        Assert.NotEqual(plain, SshConnectionPool.KeyOf([new SshEndpoint("bastion", 22, "deploy"), new SshEndpoint("web", 22, "deploy")]));
        Assert.NotEqual(plain, SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy", Proxy: new SshProxy(ProxyKind.Socks5, "proxy", 1080))]));
        Assert.NotEqual(
            SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy", Proxy: new SshProxy(ProxyKind.Command, string.Empty, 0, Command: "nc %h %p"))]),
            SshConnectionPool.KeyOf([new SshEndpoint("web", 22, "deploy", Proxy: new SshProxy(ProxyKind.Command, string.Empty, 0, Command: "cloudflared access ssh --hostname %h"))]));
    }
}

/// <summary>Sharing one connection between terminals, against a real sshd. Opt-in like the rest of these.</summary>
public sealed class SshdSharedConnectionTests : TempDataDirectoryTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [SshdFact]
    public async Task TwoTerminals_ShareOneConnection_UntilTheLastCloses()
    {
        SshdTarget target = SshdTarget.FromEnvironment()!;
        int asked = 0;
        HostKeyPolicy policy = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return true;
        };
        var options = new SshShellOptions(ShareConnection: true);
        int before = SshConnectionPool.OpenCount;

        SshTransport first = await SshTransport.ConnectAsync([target.Endpoint()], 80, 24, policy, null, options, CancellationToken.None).WaitAsync(Patience);
        SshTransport second = await SshTransport.ConnectAsync([target.Endpoint()], 80, 24, policy, null, options, CancellationToken.None).WaitAsync(Patience);

        // One host key check: the second terminal never made a connection of its own.
        Assert.Equal(1, asked);
        Assert.Equal(before + 1, SshConnectionPool.OpenCount);

        await first.DisposeAsync();
        Assert.False(second.ConnectionLost);

        await second.DisposeAsync();
        Assert.Equal(before, SshConnectionPool.OpenCount);
    }
}
