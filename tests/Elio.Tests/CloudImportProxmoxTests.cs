using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// Proxmox VE: reading what the token and address typed are, the header the token goes in, the list of guests and
/// their addresses, the certificate the user accepted, and that the secret is never in a message.
/// </summary>
public sealed class CloudImportProxmoxTests
{
    private const string Secret = "aaaaaaaa-bbbb-cccc-dddd-ef0123456789";

    /// <summary>Answers requests by their path instead of the network, and keeps what was asked.</summary>
    private sealed class ProxmoxServer(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri Address, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null));
            return Task.FromResult(answer(request.RequestUri!.PathAndQuery));
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK, string? reason = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (reason is not null) response.ReasonPhrase = reason;
        return response;
    }

    private static ProxmoxCredentials Credentials() => ProxmoxImport.Credentials("https://pve.example.com:8006", "root@pam!elio", Secret);

    // What /api2/json/cluster/resources?type=vm gives: every guest of the cluster, with its node, id, name, state,
    // tags (kept with semicolons between them) and whether it is a template.
    private const string Resources = """
        {
          "data": [
            { "id": "qemu/100", "type": "qemu", "node": "pve1", "vmid": 100, "name": "web", "status": "running", "tags": "prod;web", "template": 0, "cpu": 0.0123, "maxcpu": 2, "mem": 1073741824, "maxmem": 4294967296, "disk": 0, "maxdisk": 34359738368, "netin": 123, "netout": 456, "diskread": 0, "diskwrite": 0, "uptime": 86400 },
            { "id": "lxc/101", "type": "lxc", "node": "pve1", "vmid": 101, "name": "dns", "status": "running", "tags": "infra", "template": 0, "maxmem": 536870912, "uptime": 3600 },
            { "id": "lxc/105", "type": "lxc", "node": "pve2", "vmid": 105, "name": "cache", "status": "running", "template": 0 },
            { "id": "qemu/102", "type": "qemu", "node": "pve2", "vmid": 102, "name": "stopped-vm", "status": "stopped", "template": 0 },
            { "id": "qemu/103", "type": "qemu", "node": "pve2", "vmid": 103, "name": "no-agent", "status": "running", "template": 0 },
            { "id": "qemu/9000", "type": "qemu", "node": "pve1", "vmid": 9000, "name": "ubuntu-template", "status": "stopped", "template": 1 },
            { "id": "qemu/104", "type": "qemu", "node": "../../access/users", "vmid": 104, "name": "sneaky", "status": "running" },
            { "id": "storage/pve1/local", "type": "storage", "node": "pve1", "storage": "local", "status": "available" },
            { "id": "node/pve1", "type": "node", "node": "pve1", "status": "online" }
          ]
        }
        """;

    // What the QEMU guest agent says about a virtual machine's interfaces, under "result".
    private const string AgentInterfaces = """
        {
          "data": {
            "result": [
              {
                "name": "lo",
                "hardware-address": "00:00:00:00:00:00",
                "ip-addresses": [
                  { "ip-address-type": "ipv4", "ip-address": "127.0.0.1", "prefix": 8 },
                  { "ip-address-type": "ipv6", "ip-address": "::1", "prefix": 128 }
                ],
                "statistics": { "rx-bytes": 100, "rx-dropped": 0, "rx-errs": 0, "rx-packets": 1, "tx-bytes": 100, "tx-dropped": 0, "tx-errs": 0, "tx-packets": 1 }
              },
              {
                "name": "ens18",
                "hardware-address": "bc:24:11:aa:bb:cc",
                "ip-addresses": [
                  { "ip-address-type": "ipv6", "ip-address": "fe80::be24:11ff:feaa:bbcc", "prefix": 64 },
                  { "ip-address-type": "ipv4", "ip-address": "192.168.1.50", "prefix": 24 }
                ],
                "statistics": { "rx-bytes": 5000, "rx-dropped": 0, "rx-errs": 0, "rx-packets": 50, "tx-bytes": 4000, "tx-dropped": 0, "tx-errs": 0, "tx-packets": 40 }
              }
            ]
          }
        }
        """;

    // What a container says about its interfaces, in the form of the versions before the list of addresses.
    private const string ContainerInterfacesOld = """
        {
          "data": [
            { "name": "lo", "hwaddr": "00:00:00:00:00:00", "inet": "127.0.0.1/8", "inet6": "::1/128" },
            { "name": "eth0", "hwaddr": "bc:24:11:11:22:33", "inet": "192.168.1.53/24", "inet6": "fe80::be24:11ff:fe11:2233/64" }
          ]
        }
        """;

    // And in that of the versions that list every address, the old fields kept beside them.
    private const string ContainerInterfacesNew = """
        {
          "data": [
            { "name": "lo", "hwaddr": "00:00:00:00:00:00", "hardware-address": "00:00:00:00:00:00", "inet": "127.0.0.1/8", "inet6": "::1/128",
              "ip-addresses": [ { "ip-address-type": "inet", "ip-address": "127.0.0.1", "prefix": 8 }, { "ip-address-type": "inet6", "ip-address": "::1", "prefix": 128 } ] },
            { "name": "eth0", "hwaddr": "bc:24:11:44:55:66", "hardware-address": "bc:24:11:44:55:66", "inet": "192.168.1.60/24",
              "ip-addresses": [ { "ip-address-type": "inet", "ip-address": "169.254.10.10", "prefix": 16 }, { "ip-address-type": "inet", "ip-address": "192.168.1.60", "prefix": 24 } ] }
          ]
        }
        """;

    private static HttpResponseMessage Cluster(string path) => path switch
    {
        "/api2/json/cluster/resources?type=vm" => Answer(Resources),
        "/api2/json/nodes/pve1/qemu/100/agent/network-get-interfaces" => Answer(AgentInterfaces),
        "/api2/json/nodes/pve1/lxc/101/interfaces" => Answer(ContainerInterfacesOld),
        "/api2/json/nodes/pve2/lxc/105/interfaces" => Answer(ContainerInterfacesNew),
        "/api2/json/nodes/pve2/qemu/103/agent/network-get-interfaces" => Answer("{\"data\":null}", HttpStatusCode.InternalServerError, "QEMU guest agent is not running"),
        _ => Answer("{\"data\":null}", HttpStatusCode.NotImplemented, "Method not implemented"),
    };

    // Reading what was typed

    [Theory]
    [InlineData("https://pve.example.com:8006", "https://pve.example.com:8006/")]
    [InlineData("pve.example.com", "https://pve.example.com:8006/")]
    [InlineData("https://pve.example.com", "https://pve.example.com:8006/")]
    [InlineData("https://pve.example.com:443", "https://pve.example.com/")]
    [InlineData("192.168.1.10:8006", "https://192.168.1.10:8006/")]
    [InlineData("  https://pve:8006/#v1:0:18:4:::::::  ", "https://pve:8006/")]
    [InlineData("https://pve.example.com:8443/api2/json?x=1", "https://pve.example.com:8443/")]
    [InlineData("https://[fd00::10]", "https://[fd00::10]:8006/")]
    [InlineData("https://[fd00::10]:8007/", "https://[fd00::10]:8007/")]
    public void ServerAddress_IsHttpsToTheServerItself(string typed, string expected) =>
        Assert.Equal(expected, ProxmoxImport.ServerAddress(typed)?.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("http://pve.example.com:8006")]
    [InlineData("ftp://pve.example.com")]
    [InlineData("https://root:secret@pve.example.com:8006")]
    [InlineData("https://pve example.com")]
    [InlineData("file:///etc/passwd")]
    public void ServerAddress_ThatIsNotHttpsToAServer_IsRefused(string typed) =>
        Assert.Null(ProxmoxImport.ServerAddress(typed));

    [Theory]
    [InlineData("root@pam!elio")]
    [InlineData("monitor@pve!read-only_1")]
    [InlineData("john.doe@example.com@pve!import")]
    public void Credentials_AcceptTokenIdsProxmoxMakes(string tokenId)
    {
        ProxmoxCredentials credentials = ProxmoxImport.Credentials("pve.example.com", tokenId, Secret);

        Assert.Equal(tokenId, credentials.TokenId);
        Assert.Equal("https://pve.example.com:8006", credentials.Origin);
    }

    [Theory]
    [InlineData("someone@pve")]
    [InlineData("root!elio")]
    [InlineData("root@pam!elio\r\nX-Evil: 1")]
    [InlineData("root@pam!elio=other")]
    [InlineData("ro ot@pam!elio")]
    [InlineData("root@pam!1elio")]
    public void Credentials_RefuseWhatIsNotATokenId_WithoutRepeatingIt(string tokenId)
    {
        var error = Assert.Throws<CloudImportException>(() => ProxmoxImport.Credentials("pve.example.com", tokenId, Secret));

        Assert.DoesNotContain(tokenId, error.Message);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-ef0123456789\r\nX-Evil: 1")]
    [InlineData("aaaaaaaa bbbb")]
    [InlineData("aaaaaaaa=bbbb-cccc")]
    public void Credentials_RefuseWhatIsNotASecret_WithoutRepeatingIt(string secret)
    {
        var error = Assert.Throws<CloudImportException>(() => ProxmoxImport.Credentials("pve.example.com", "root@pam!elio", secret));

        Assert.DoesNotContain(secret, error.Message);
    }

    [Fact]
    public void Credentials_RefuseAnAddressThatIsNotHttps_WithoutRepeatingIt()
    {
        var error = Assert.Throws<CloudImportException>(() => ProxmoxImport.Credentials("http://" + Secret, "root@pam!elio", Secret));

        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public void Credentials_PrintedSomewhere_DoNotShowTheSecret() =>
        Assert.DoesNotContain(Secret, Credentials().ToString());

    [Fact]
    public void AuthorizationHeader_IsTheTokenIdAndSecret_AsProxmoxWantsThem() =>
        Assert.Equal("PVEAPIToken=root@pam!elio=aaaaaaaa-bbbb-cccc-dddd-ef0123456789", ProxmoxImport.AuthorizationHeader(Credentials()));

    // Reading what Proxmox said

    [Fact]
    public void Resources_GiveTheGuests_ThatCanBeAskedAboutSafely()
    {
        List<ProxmoxImport.ProxmoxGuest> guests = ProxmoxImport.ParseResources(Resources);

        // Not the storage or the node, and not the guest whose node would take the request somewhere else.
        Assert.Equal([100, 101, 105, 102, 103, 9000], guests.Select(g => g.VmId));

        ProxmoxImport.ProxmoxGuest web = guests[0];
        Assert.Equal(("qemu", "pve1", "web", true, false), (web.Type, web.Node, web.Name, web.Running, web.Template));
        Assert.Equal(["prod", "web"], web.Tags);
        Assert.True(guests[1].Container);
        Assert.False(guests[3].Running);
        Assert.True(guests[5].Template);
    }

    [Theory]
    [InlineData("prod;web", new[] { "prod", "web" })]
    [InlineData("prod,web", new[] { "prod", "web" })]
    [InlineData("prod web", new[] { "prod", "web" })]
    [InlineData("", new string[0])]
    public void Resources_ReadTagsWrittenEitherWay(string written, string[] tags)
    {
        string json = "{\"data\": [{\"type\": \"lxc\", \"node\": \"pve\", \"vmid\": 200, \"status\": \"running\", \"tags\": \"" + written + "\"}]}";

        Assert.Equal(tags, Assert.Single(ProxmoxImport.ParseResources(json)).Tags);
    }

    [Theory]
    [InlineData("{\"type\": \"qemu\", \"node\": \"pve\", \"vmid\": \"100; rm\"}")]
    [InlineData("{\"type\": \"qemu\", \"node\": \"pve\", \"vmid\": -1}")]
    [InlineData("{\"type\": \"qemu\", \"node\": \"pve/../x\", \"vmid\": 100}")]
    [InlineData("{\"type\": \"qemu\", \"node\": \"pve?x=1\", \"vmid\": 100}")]
    [InlineData("{\"type\": \"openvz\", \"node\": \"pve\", \"vmid\": 100}")]
    [InlineData("{\"node\": \"pve\", \"vmid\": 100}")]
    public void Resources_AGuestThatCouldNotGoIntoAnAddressSafely_IsLeftOut(string guest) =>
        Assert.Empty(ProxmoxImport.ParseResources("{\"data\": [" + guest + "]}"));

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"data\": null}")]
    public void Resources_ThatAreNotAnAnswer_AreRefused(string body) =>
        Assert.Throws<CloudImportException>(() => ProxmoxImport.ParseResources(body));

    [Fact]
    public void AgentInterfaces_GiveTheFirstIPv4AddressThatIsNotTheLoopback() =>
        Assert.Equal("192.168.1.50", ProxmoxImport.ParseAgentInterfaces(AgentInterfaces));

    [Fact]
    public void ContainerInterfaces_GiveTheFirstIPv4AddressThatIsNotTheLoopback_InEitherForm()
    {
        Assert.Equal("192.168.1.53", ProxmoxImport.ParseContainerInterfaces(ContainerInterfacesOld));

        // Not the link-local address either.
        Assert.Equal("192.168.1.60", ProxmoxImport.ParseContainerInterfaces(ContainerInterfacesNew));
    }

    [Theory]
    [InlineData("{\"data\": {\"result\": [{\"name\": \"lo\", \"ip-addresses\": [{\"ip-address-type\": \"ipv4\", \"ip-address\": \"127.0.0.1\"}]}]}}")]
    [InlineData("{\"data\": {\"result\": [{\"name\": \"eth0\", \"ip-addresses\": [{\"ip-address-type\": \"ipv4\", \"ip-address\": \"1.2.3.4.evil.example\"}]}]}}")]
    [InlineData("{\"data\": {\"result\": [{\"name\": \"eth0\", \"ip-addresses\": [{\"ip-address-type\": \"ipv4\", \"ip-address\": \"-oProxyCommand=calc\"}]}]}}")]
    [InlineData("{\"data\": {\"result\": [{\"name\": \"eth0\", \"ip-addresses\": [{\"ip-address-type\": \"ipv6\", \"ip-address\": \"2001:db8::5\"}]}]}}")]
    [InlineData("{\"data\": null}")]
    [InlineData("not json")]
    public void AgentInterfaces_WithoutAnUsableAddress_GiveNone(string json) =>
        Assert.Null(ProxmoxImport.ParseAgentInterfaces(json));

    // Asking Proxmox

    [Fact]
    public async Task List_GivesTheRunningGuestsWithAnAddress_AndCountsTheOthers()
    {
        var server = new ProxmoxServer(Cluster);

        ProxmoxListing listing = await ProxmoxImport.ListAsync(Credentials(), null, server);

        HostLibrary library = listing.Library;
        Assert.Equal(["web", "dns", "cache"], library.Hosts.Select(h => h.Name));
        Assert.Equal(["192.168.1.50", "192.168.1.53", "192.168.1.60"], library.Hosts.Select(h => h.Address));

        // A container lets root in; a virtual machine's user is not guessed. The node comes first among the tags.
        Assert.Equal([string.Empty, "root", "root"], library.Hosts.Select(h => h.User));
        Assert.Equal(["pve1", "prod", "web"], library.Hosts[0].Tags);
        Assert.Equal(["pve2"], library.Hosts[2].Tags);

        // The stopped machine and the one without a guest agent; the template is no machine at all.
        Assert.Equal(2, listing.LeftOut);

        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Proxmox VE / pve.example.com", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));
    }

    [Fact]
    public async Task List_AsksOnlyTheServer_OverHttps_WithTheTokenInTheHeaderOnly()
    {
        var server = new ProxmoxServer(Cluster);

        await ProxmoxImport.ListAsync(Credentials(), null, server);

        // The list, then one question for each running guest that is not a template: never for a stopped one.
        Assert.Equal(5, server.Requests.Count);
        Assert.Equal("https://pve.example.com:8006/api2/json/cluster/resources?type=vm", server.Requests[0].Address.AbsoluteUri);
        Assert.Contains(server.Requests, r => r.Address.AbsolutePath == "/api2/json/nodes/pve1/lxc/101/interfaces");
        Assert.DoesNotContain(server.Requests, r => r.Address.AbsolutePath.Contains("/102/", StringComparison.Ordinal) || r.Address.AbsolutePath.Contains("/9000/", StringComparison.Ordinal));
        Assert.All(server.Requests, r =>
        {
            Assert.Equal((Uri.UriSchemeHttps, "pve.example.com", 8006), (r.Address.Scheme, r.Address.Host, r.Address.Port));
            Assert.Equal("PVEAPIToken=root@pam!elio=" + Secret, r.Authorization);
            Assert.DoesNotContain(Secret, r.Address.AbsoluteUri);
        });
    }

    [Fact]
    public async Task List_WhenRefused_SaysWhatProxmoxSaid()
    {
        var server = new ProxmoxServer(_ => Answer("{\"data\":null}", HttpStatusCode.Unauthorized, "invalid token value!"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(Credentials(), null, server));

        Assert.Equal("invalid token value!", error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task List_WhenAParameterIsWrong_SaysWhichAndWhy()
    {
        var server = new ProxmoxServer(_ => Answer("{\"errors\":{\"type\":\"value 'vm' does not have a value in the enumeration 'node, storage'\"},\"data\":null}", HttpStatusCode.BadRequest, "Parameter verification failed."));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(Credentials(), null, server));

        Assert.Equal("type: value 'vm' does not have a value in the enumeration 'node, storage'", error.Message);
    }

    [Fact]
    public async Task List_AServerThatRepeatsTheSecret_DoesNotGetItIntoTheMessage()
    {
        var server = new ProxmoxServer(_ => Answer("{\"message\":\"bad token PVEAPIToken=root@pam!elio=" + Secret + "\"}", HttpStatusCode.Unauthorized, "no " + Secret.ToUpperInvariant()));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(Credentials(), null, server));

        Assert.DoesNotContain(Secret, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad token", error.Message);
    }

    [Fact]
    public async Task List_WhenTheNetworkFails_SaysSo_WithoutTheSecret()
    {
        var server = new FailingHandler(new HttpRequestException("Connection refused while sending " + Secret));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(Credentials(), null, server));

        Assert.Contains("Connection refused", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task List_ARedirect_IsRefusedAndNotFollowed()
    {
        var server = new ProxmoxServer(_ =>
        {
            HttpResponseMessage moved = Answer("<html>Moved</html>", HttpStatusCode.MovedPermanently);
            moved.Headers.Location = new Uri("https://evil.example/api2/json/cluster/resources");
            return moved;
        });

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(Credentials(), null, server));

        Assert.DoesNotContain(Secret, error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task List_WhenCancelled_IsCancelledRatherThanAnError_AndAsksNothing()
    {
        var server = new ProxmoxServer(Cluster);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProxmoxImport.ListAsync(Credentials(), null, server, cancelled.Token));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task List_ImportedTwice_AddsNothingTheSecondTime()
    {
        var library = new HostLibrary();
        int first = LibraryMerge.Merge(library, (await ProxmoxImport.ListAsync(Credentials(), null, new ProxmoxServer(Cluster))).Library, _ => false);
        int second = LibraryMerge.Merge(library, (await ProxmoxImport.ListAsync(Credentials(), null, new ProxmoxServer(Cluster))).Library, _ => false);

        Assert.Equal((3, 0), (first, second));
    }

    // The certificate the user accepted

    private const string Fingerprint = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";
    private const string Other = "00:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    [Fact]
    public void Certificate_TheSystemTrusts_IsAccepted_WhenNoneWasPinned() =>
        Assert.Equal(CertificateVerdict.Accept, PinnedCertificate.Decide(SslPolicyErrors.None, Fingerprint, null));

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void Certificate_TheSystemDoesNotTrust_IsForTheUser_WhenNoneWasPinned(SslPolicyErrors errors) =>
        Assert.Equal(CertificateVerdict.Ask, PinnedCertificate.Decide(errors, Fingerprint, null));

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.None)]
    public void Certificate_ThatIsThePinnedOne_IsAccepted(SslPolicyErrors errors) =>
        Assert.Equal(CertificateVerdict.Accept, PinnedCertificate.Decide(errors, Fingerprint, Fingerprint));

    [Fact]
    public void Certificate_ThatIsThePinnedOne_IsRecognisedHoweverThePinIsWritten() =>
        Assert.Equal(CertificateVerdict.Accept, PinnedCertificate.Decide(SslPolicyErrors.RemoteCertificateChainErrors, Fingerprint, Fingerprint.Replace(":", string.Empty).ToLowerInvariant()));

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.None)]
    public void Certificate_ThatIsNotThePinnedOne_IsRefused_EvenOneTheSystemTrusts(SslPolicyErrors errors) =>
        Assert.Equal(CertificateVerdict.Refuse, PinnedCertificate.Decide(errors, Other, Fingerprint));

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, Fingerprint)]
    [InlineData("", null)]
    [InlineData("AB:CD", null)]
    [InlineData(Fingerprint, "not a fingerprint")]
    [InlineData(Fingerprint, "")]
    public void Certificate_ThatIsMissingOrAPinThatIsNoFingerprint_IsRefused(string? presented, string? pinned) =>
        Assert.Equal(CertificateVerdict.Refuse, PinnedCertificate.Decide(SslPolicyErrors.RemoteCertificateChainErrors, presented, pinned));

    [Fact]
    public void Certificate_Fingerprint_IsTheSha256OfTheCertificate_AsProxmoxWritesIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pve.example.com", key, HashAlgorithmName.SHA256);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        string fingerprint = PinnedCertificate.Fingerprint(certificate);

        Assert.Equal(string.Join(':', SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2"))), fingerprint);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(fingerprint, PinnedCertificate.Normalize(fingerprint.ToLowerInvariant().Replace(':', ' ')));
    }

    [Fact]
    public void UntrustedCertificate_SaysWhichServer_AndNothingSecret()
    {
        var question = new UntrustedCertificateException("pve.example.com", Fingerprint);

        Assert.Contains("pve.example.com", question.Message);
        Assert.Equal(Fingerprint, question.Fingerprint);
        Assert.DoesNotContain(Secret, question.Message);
    }

    // The certificate over a real connection: a server on this computer's loopback address with a certificate it
    // signed itself, as a Proxmox VE that was just set up has.

    [Fact]
    public async Task List_ASelfSignedCertificate_IsShownToTheUser_BeforeAnythingIsSent()
    {
        await using var server = new SelfSignedServer();

        var question = await Assert.ThrowsAsync<UntrustedCertificateException>(() => ProxmoxImport.ListAsync(server.Credentials, null));

        Assert.Equal(server.Fingerprint, question.Fingerprint);
        Assert.Equal("127.0.0.1", question.Host);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task List_WithThatCertificateAccepted_GoesThrough_WithTheToken()
    {
        await using var server = new SelfSignedServer();

        ProxmoxListing listing = await ProxmoxImport.ListAsync(server.Credentials, server.Fingerprint.Replace(":", string.Empty).ToLowerInvariant());

        Assert.Empty(listing.Library.Hosts);
        string request = Assert.Single(server.Requests);
        Assert.StartsWith("GET /api2/json/cluster/resources?type=vm HTTP/1.1\r\n", request);
        Assert.Contains("\r\nAuthorization: PVEAPIToken=root@pam!elio=" + Secret + "\r\n", request);
    }

    [Fact]
    public async Task List_WithAnotherCertificateAccepted_IsRefused_AndSendsNothing()
    {
        await using var server = new SelfSignedServer();

        var error = await Assert.ThrowsAsync<CloudImportException>(() => ProxmoxImport.ListAsync(server.Credentials, Other));

        Assert.Contains("different certificate", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.Empty(server.Requests);
    }

    /// <summary>
    /// Answers every request with an empty list over TLS, with a certificate of its own signing, and keeps the head
    /// of each request that got as far as being sent.
    /// </summary>
    private sealed class SelfSignedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public SelfSignedServer()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=pve.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            // Windows only serves TLS with a key it holds itself, so the certificate goes through PKCS #12 once.
            _certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
            _listener.Start();
            _serving = ServeAsync();
        }

        public List<string> Requests { get; } = [];

        public string Fingerprint => PinnedCertificate.Fingerprint(_certificate);

        public ProxmoxCredentials Credentials => ProxmoxImport.Credentials($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}", "root@pam!elio", Secret);

        private async Task ServeAsync()
        {
            var handling = new List<Task>();
            try
            {
                while (true) handling.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            await Task.WhenAll(handling);
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(_certificate);

                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int read = await tls.ReadAsync(buffer, _stop.Token);
                        if (read == 0) return;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    lock (Requests) Requests.Add(head.ToString());
                    byte[] body = "{\"data\":[]}"u8.ToArray();
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await tls.WriteAsync(body);
                    await tls.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client turned the certificate down, which is what some of the tests are about.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
            _certificate.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>Fails every request the way the network does.</summary>
    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);
    }
}
