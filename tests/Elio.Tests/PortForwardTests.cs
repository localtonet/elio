using Elio.Core;

namespace Elio.Tests;

public sealed class PortForwardTests
{
    private static (ForwardKind, string, int, string, int) Parts(PortForward? forward)
    {
        Assert.NotNull(forward);
        return (forward.Kind, forward.BindAddress, forward.BindPort, forward.TargetHost, forward.TargetPort);
    }

    [Fact]
    public void TryParse_LocalForward() =>
        Assert.Equal((ForwardKind.Local, "127.0.0.1", 8080, "localhost", 80), Parts(PortForward.TryParse("L 8080:localhost:80")));

    [Fact]
    public void TryParse_RemoteForward() =>
        Assert.Equal((ForwardKind.Remote, "127.0.0.1", 9000, "localhost", 3000), Parts(PortForward.TryParse("R 9000:localhost:3000")));

    [Fact]
    public void TryParse_DynamicForward() =>
        Assert.Equal((ForwardKind.Dynamic, "127.0.0.1", 1080, string.Empty, 0), Parts(PortForward.TryParse("D 1080")));

    [Fact]
    public void TryParse_LocalForwardWithBindAddress() =>
        Assert.Equal((ForwardKind.Local, "0.0.0.0", 8080, "db.internal", 5432), Parts(PortForward.TryParse("L 0.0.0.0:8080:db.internal:5432")));

    [Fact]
    public void TryParse_DynamicForwardWithBindAddress() =>
        Assert.Equal((ForwardKind.Dynamic, "0.0.0.0", 1080, string.Empty, 0), Parts(PortForward.TryParse("D 0.0.0.0:1080")));

    [Fact]
    public void TryParse_LowerCaseKindAndExtraSpaces() =>
        Assert.Equal((ForwardKind.Local, "127.0.0.1", 8080, "localhost", 80), Parts(PortForward.TryParse("  l   8080:localhost:80 ")));

    [Theory]
    [InlineData("")]
    [InlineData("L")]
    [InlineData("8080:localhost:80")]
    [InlineData("X 8080:localhost:80")]
    [InlineData("L 8080:localhost:80 extra")]
    [InlineData("L abc:localhost:80")]
    [InlineData("L 8080:localhost:abc")]
    [InlineData("L 8080:localhost")]
    [InlineData("L 8080")]
    [InlineData("L 8080::80")]
    [InlineData("L 8080:localhost:0")]
    [InlineData("L 8080:localhost:65536")]
    [InlineData("L 65536:localhost:80")]
    [InlineData("L -1:localhost:80")]
    [InlineData("L a:b:8080:localhost:80")]
    [InlineData("D abc")]
    [InlineData("D 1080:localhost:80")]
    public void TryParse_Garbage_ReturnsNull(string text) =>
        Assert.Null(PortForward.TryParse(text));

    [Theory]
    [InlineData("L 127.0.0.1:8080:localhost:80")]
    [InlineData("R 0.0.0.0:9000:localhost:3000")]
    [InlineData("D 127.0.0.1:1080")]
    public void ToString_IsReadBackByTryParse(string text) =>
        Assert.Equal(text, PortForward.TryParse(text)?.ToString());

    [Theory]
    [InlineData("L [::1]:8080:localhost:80")]
    [InlineData("L [::1]:8080:[2001:db8::5]:80")]
    [InlineData("R 127.0.0.1:9000:[::1]:3000")]
    [InlineData("D [::1]:1080")]
    public void AnIpv6AddressInBrackets_IsReadAndWrittenBack(string text) =>
        Assert.Equal(text, PortForward.TryParse(text)?.ToString());

    [Fact]
    public void AnIpv6Address_IsKeptWithoutItsBrackets()
    {
        PortForward? forward = PortForward.TryParse("L [::1]:8080:[2001:db8::5]:80");

        Assert.Equal("::1", forward?.BindAddress);
        Assert.Equal(8080, forward?.BindPort);
        Assert.Equal("2001:db8::5", forward?.TargetHost);
        Assert.Equal(80, forward?.TargetPort);
    }

    [Theory]
    [InlineData("L ::1:8080:localhost:80")]
    [InlineData("L [::1:8080:localhost:80")]
    [InlineData("L []:8080:localhost:80")]
    [InlineData("L [::1]8080:localhost:80")]
    public void AnIpv6AddressWrittenWrongly_IsNotGuessedAt(string text) =>
        Assert.Null(PortForward.TryParse(text));
}
