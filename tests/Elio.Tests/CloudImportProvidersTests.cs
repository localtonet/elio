using System.Net;
using System.Text;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// Hetzner Cloud, Linode, Vultr and Tailscale, from canned answers shaped like the ones in each provider's API
/// documentation: what becomes of a page, how the pages are followed, what an error says, and that the token
/// goes in the header to the provider's own address and nowhere else.
/// </summary>
public sealed class CloudImportProvidersTests
{
    /// <summary>Answers requests from a list instead of the network, and keeps what was asked.</summary>
    private sealed class CannedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri Address, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value))));
            return Task.FromResult(answer(request, Requests.Count));
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    /// <summary>A redirect, as the real handler hands it back when it is told not to follow it.</summary>
    private static HttpResponseMessage Redirect(string location)
    {
        HttpResponseMessage response = Answer("<html><body>Moved</body></html>", HttpStatusCode.MovedPermanently);
        response.Headers.Location = new Uri(location);
        return response;
    }

    /// <summary>Every request went to the provider's own host over HTTPS, with the token in the header only.</summary>
    private static void AssertTokenOnlyInHeader(CannedHandler handler, string host, string token)
    {
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal((Uri.UriSchemeHttps, host), (r.Address.Scheme, r.Address.Host));
            Assert.Equal("Bearer " + token, r.Headers["authorization"]);
            Assert.DoesNotContain(token, r.Address.AbsoluteUri);
        });
    }

    /// <summary>Puts what a provider listed into a library twice, the way the import does, and says how many each time added.</summary>
    private static (int First, int Second, HostLibrary Library) MergeTwice(Func<HostLibrary> list)
    {
        var library = new HostLibrary();
        int first = LibraryMerge.Merge(library, list(), _ => false);
        int second = LibraryMerge.Merge(library, list(), _ => false);
        return (first, second, library);
    }

    // Hetzner Cloud

    private const string HetznerFirstPage = """
        {
          "servers": [
            {
              "id": 42,
              "name": "web-1",
              "status": "running",
              "created": "2016-01-30T23:55:00Z",
              "public_net": {
                "ipv4": { "id": 42, "ip": "203.0.113.10", "blocked": false, "dns_ptr": "server01.example.com" },
                "ipv6": { "id": 43, "ip": "2001:db8::/64", "blocked": false, "dns_ptr": [ { "ip": "2001:db8::1", "dns_ptr": "server.example.com" } ] },
                "floating_ips": [ 478 ],
                "firewalls": [ { "id": 42, "status": "applied" } ]
              },
              "private_net": [ { "network": 4711, "ip": "10.0.0.2", "alias_ips": [ "10.0.0.3" ], "mac_address": "86:00:ff:2a:7d:e1" } ],
              "server_type": { "id": 1, "name": "cpx22", "description": "CPX22", "cores": 2, "memory": 4, "disk": 80 },
              "location": { "id": 1, "name": "fsn1", "description": "Falkenstein DC Park 1", "country": "DE", "city": "Falkenstein", "network_zone": "eu-central" },
              "image": { "id": 42, "type": "system", "status": "available", "name": "ubuntu-24.04", "os_flavor": "ubuntu" },
              "iso": null,
              "rescue_enabled": false,
              "locked": false,
              "backup_window": "22-02",
              "protection": { "delete": false, "rebuild": false },
              "labels": { "environment": "prod", "example.com/my": "label", "just-a-key": "" },
              "volumes": [],
              "load_balancers": [],
              "primary_disk_size": 50,
              "placement_group": null
            },
            {
              "id": 43,
              "name": "v6-only",
              "status": "running",
              "public_net": { "ipv4": null, "ipv6": { "id": 44, "ip": "2a01:4f8:c17:1234::/64", "blocked": false, "dns_ptr": [] }, "floating_ips": [], "firewalls": [] },
              "private_net": [],
              "location": { "id": 3, "name": "hel1" },
              "labels": {}
            },
            {
              "id": 44,
              "name": "behind-the-bastion",
              "status": "running",
              "public_net": { "ipv4": null, "ipv6": null, "floating_ips": [], "firewalls": [] },
              "private_net": [ { "network": 4711, "ip": "10.0.0.7", "alias_ips": [], "mac_address": "86:00:ff:2a:7d:e2" } ],
              "location": { "id": 3, "name": "hel1" },
              "labels": { "role": "db" }
            },
            {
              "id": 45,
              "name": "nowhere",
              "status": "initializing",
              "public_net": { "ipv4": null, "ipv6": null, "floating_ips": [], "firewalls": [] },
              "private_net": [],
              "labels": {}
            }
          ],
          "meta": { "pagination": { "page": 1, "per_page": 50, "previous_page": null, "next_page": 2, "last_page": 2, "total_entries": 5 } }
        }
        """;

    private const string HetznerLastPage = """
        {
          "servers": [
            { "id": 46, "name": "", "public_net": { "ipv4": { "ip": "198.51.100.4" }, "ipv6": null }, "private_net": [], "location": null, "labels": null }
          ],
          "meta": { "pagination": { "page": 2, "per_page": 50, "previous_page": 1, "next_page": null, "last_page": 2, "total_entries": 5 } }
        }
        """;

    [Fact]
    public void HetznerPage_GivesOneServerPerServerWithAnAddress()
    {
        List<HostEntry> hosts = HetznerImport.ParsePage(HetznerFirstPage, out int? next);

        Assert.Equal(2, next);
        Assert.Equal(3, hosts.Count);

        // The public IPv4 address, with the location and the labels as tags.
        Assert.Equal(("web-1", "203.0.113.10", 22, "root"), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["fsn1", "environment=prod", "example.com/my=label", "just-a-key"], hosts[0].Tags);
        Assert.True(hosts[0].UseAgent);

        // No IPv4: the first address of the IPv6 network, which is the one Hetzner sets up on the server.
        Assert.Equal(("v6-only", "2a01:4f8:c17:1234::1"), (hosts[1].Name, hosts[1].Address));
        Assert.Equal(["hel1"], hosts[1].Tags);

        // No public address at all: the one in the private network.
        Assert.Equal(("behind-the-bastion", "10.0.0.7"), (hosts[2].Name, hosts[2].Address));
        Assert.Equal(["hel1", "role=db"], hosts[2].Tags);
    }

    [Fact]
    public void HetznerPage_TheLastOne_HasNoNext_AndSurvivesMissingParts()
    {
        HostEntry host = Assert.Single(HetznerImport.ParsePage(HetznerLastPage, out int? next));

        Assert.Null(next);
        Assert.Equal(("198.51.100.4", "198.51.100.4"), (host.Name, host.Address));
        Assert.Empty(host.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"servers\": 3}")]
    [InlineData("{\"error\": {\"code\": \"unauthorized\", \"message\": \"unable to authenticate\"}}")]
    public void HetznerPage_ThatIsNotAnAnswer_IsRefused(string body) =>
        Assert.Throws<CloudImportException>(() => HetznerImport.ParsePage(body, out _));

    [Theory]
    [InlineData("1.2.3.4.evil.example")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("0.0.0.0")]
    [InlineData("1")]
    public void HetznerPage_AnAddressThatIsNotOne_IsNotKept(string ip)
    {
        string page = "{\"servers\": [{\"name\": \"x\", \"public_net\": {\"ipv4\": {\"ip\": \"" + ip + "\"}}}], \"meta\": {\"pagination\": {\"next_page\": null}}}";

        Assert.Empty(HetznerImport.ParsePage(page, out _));
    }

    [Fact]
    public void HetznerError_IsShownInHetznerOwnWords()
    {
        Assert.Equal("unable to authenticate (unauthorized)", HetznerImport.ErrorMessage("{\"error\": {\"code\": \"unauthorized\", \"message\": \"unable to authenticate\", \"details\": {}}}"));
        Assert.Equal("project limit exceeded", HetznerImport.ErrorMessage("{\"error\": {\"message\": \"project limit exceeded\"}}"));
        Assert.Null(HetznerImport.ErrorMessage("{\"error\": \"nope\"}"));
        Assert.Null(HetznerImport.ErrorMessage("<html>Bad Gateway</html>"));
        Assert.Null(HetznerImport.ErrorMessage(string.Empty));
    }

    [Fact]
    public async Task HetznerList_FollowsThePages_WithTheTokenInTheHeaderOnly()
    {
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? HetznerFirstPage : HetznerLastPage));

        HostLibrary library = await HetznerImport.ListAsync(" hcloud-secret ", handler);

        Assert.Equal(["web-1", "v6-only", "behind-the-bastion", "198.51.100.4"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Hetzner Cloud", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(
            ["https://api.hetzner.cloud/v1/servers?page=1&per_page=50", "https://api.hetzner.cloud/v1/servers?page=2&per_page=50"],
            handler.Requests.Select(r => r.Address.AbsoluteUri));
        AssertTokenOnlyInHeader(handler, "api.hetzner.cloud", "hcloud-secret");
    }

    [Fact]
    public async Task HetznerList_WithAPageThatPointsBack_Stops()
    {
        var handler = new CannedHandler((_, _) => Answer(HetznerFirstPage));

        HostLibrary library = await HetznerImport.ListAsync("token", handler);

        // Page 1 says 2 is next, and page 2 (the same answer) says 2 again.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(6, library.Hosts.Count);
    }

    [Fact]
    public async Task HetznerList_WhenRefused_SaysWhatHetznerSaid()
    {
        var handler = new CannedHandler((_, _) => Answer("{\"error\": {\"code\": \"unauthorized\", \"message\": \"unable to authenticate\"}}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => HetznerImport.ListAsync("wrong", handler));

        Assert.Equal("unable to authenticate (unauthorized)", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HetznerList_ARedirect_IsRefusedAndNotFollowed()
    {
        var handler = new CannedHandler((_, _) => Redirect("http://evil.example/v1/servers"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => HetznerImport.ListAsync("token", handler));

        Assert.Contains("301", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HetznerList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await HetznerImport.ListAsync("token", new CannedHandler((_, count) => Answer(count == 1 ? HetznerFirstPage : HetznerLastPage))));

        var (added, again, library) = MergeTwice(queue.Dequeue);

        Assert.Equal((4, 0), (added, again));
        Assert.Equal("Hetzner Cloud", Assert.Single(library.Groups).Name);
    }

    // Linode

    private const string LinodeFirstPage = """
        {
          "data": [
            {
              "id": 123,
              "label": "linode123",
              "group": "",
              "status": "running",
              "created": "2018-01-01T00:01:01",
              "updated": "2018-01-01T00:01:01",
              "type": "g6-standard-1",
              "ipv4": [ "192.168.139.20", "203.0.113.1" ],
              "ipv6": "2600:3c03::f03c:91ff:fe24:3a2f/128",
              "image": "linode/debian12",
              "region": "us-east",
              "specs": { "disk": 81920, "memory": 4096, "vcpus": 2, "gpus": 0, "transfer": 4000 },
              "hypervisor": "kvm",
              "tags": [ "example tag", "another example" ]
            },
            {
              "id": 124,
              "label": "v6-only",
              "status": "running",
              "ipv4": [],
              "ipv6": "2001:DB8::5/128",
              "region": "eu-central",
              "tags": []
            },
            {
              "id": 125,
              "label": "private-only",
              "status": "offline",
              "ipv4": [ "192.168.200.3" ],
              "ipv6": null,
              "region": "eu-central",
              "tags": [ "db" ]
            },
            { "id": 126, "label": "provisioning", "status": "provisioning", "ipv4": [], "ipv6": null, "region": "eu-central", "tags": [] }
          ],
          "page": 1,
          "pages": 2,
          "results": 5
        }
        """;

    private const string LinodeLastPage = """
        {
          "data": [ { "id": 127, "label": "", "ipv4": [ "198.51.100.9" ], "ipv6": "2001:db8::/64", "region": null, "tags": null } ],
          "page": 2,
          "pages": 2,
          "results": 5
        }
        """;

    [Fact]
    public void LinodePage_GivesOneServerPerInstanceWithAnAddress()
    {
        List<HostEntry> hosts = LinodeImport.ParsePage(LinodeFirstPage, out int? next);

        Assert.Equal(2, next);
        Assert.Equal(3, hosts.Count);

        // The public address, whichever place it has in the list, with the region and the tags.
        Assert.Equal(("linode123", "203.0.113.1", 22, "root"), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["us-east", "example tag", "another example"], hosts[0].Tags);

        // No IPv4: the IPv6 address, written as the address alone.
        Assert.Equal(("v6-only", "2001:db8::5"), (hosts[1].Name, hosts[1].Address));

        // Only a private address: that one.
        Assert.Equal(("private-only", "192.168.200.3"), (hosts[2].Name, hosts[2].Address));
        Assert.Equal(["eu-central", "db"], hosts[2].Tags);
    }

    [Fact]
    public void LinodePage_TheLastOne_HasNoNext_AndSurvivesMissingParts()
    {
        HostEntry host = Assert.Single(LinodeImport.ParsePage(LinodeLastPage, out int? next));

        Assert.Null(next);
        Assert.Equal(("198.51.100.9", "198.51.100.9"), (host.Name, host.Address));
        Assert.Empty(host.Tags);
    }

    [Fact]
    public void LinodePage_AnIPv6RangeRatherThanAnAddress_IsNotGuessedAt() =>
        Assert.Empty(LinodeImport.ParsePage("{\"data\": [{\"label\": \"x\", \"ipv4\": [], \"ipv6\": \"2001:db8::/64\"}], \"page\": 1, \"pages\": 1}", out _));

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"data\": {}}")]
    [InlineData("{\"errors\": [{\"reason\": \"Invalid Token\"}]}")]
    public void LinodePage_ThatIsNotAnAnswer_IsRefused(string body) =>
        Assert.Throws<CloudImportException>(() => LinodeImport.ParsePage(body, out _));

    [Fact]
    public void LinodeError_IsShownInLinodeOwnWords()
    {
        Assert.Equal("Invalid Token", LinodeImport.ErrorMessage("{\"errors\": [{\"reason\": \"Invalid Token\"}]}"));
        Assert.Equal(
            "page_size: Must be between 25 and 500; Too many requests",
            LinodeImport.ErrorMessage("{\"errors\": [{\"field\": \"page_size\", \"reason\": \"Must be between 25 and 500\"}, {\"field\": null, \"reason\": \"Too many requests\"}]}"));
        Assert.Null(LinodeImport.ErrorMessage("{\"errors\": []}"));
        Assert.Null(LinodeImport.ErrorMessage("Service Unavailable"));
    }

    [Fact]
    public async Task LinodeList_FollowsThePages_WithTheTokenInTheHeaderOnly()
    {
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? LinodeFirstPage : LinodeLastPage));

        HostLibrary library = await LinodeImport.ListAsync(" linode-secret ", handler);

        Assert.Equal(["linode123", "v6-only", "private-only", "198.51.100.9"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Linode", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(
            ["https://api.linode.com/v4/linode/instances?page=1&page_size=500", "https://api.linode.com/v4/linode/instances?page=2&page_size=500"],
            handler.Requests.Select(r => r.Address.AbsoluteUri));
        AssertTokenOnlyInHeader(handler, "api.linode.com", "linode-secret");
    }

    [Fact]
    public async Task LinodeList_WithAPageThatPointsBack_Stops()
    {
        // Whatever was asked for, the answer claims to be page 1 of 2.
        var handler = new CannedHandler((_, _) => Answer(LinodeFirstPage));

        HostLibrary library = await LinodeImport.ListAsync("token", handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(6, library.Hosts.Count);
    }

    [Fact]
    public async Task LinodeList_WhenRefused_SaysWhatLinodeSaid()
    {
        var handler = new CannedHandler((_, _) => Answer("{\"errors\": [{\"reason\": \"Invalid Token\"}]}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => LinodeImport.ListAsync("wrong", handler));

        Assert.Equal("Invalid Token", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LinodeList_ARedirect_IsRefusedAndNotFollowed()
    {
        var handler = new CannedHandler((_, _) => Redirect("https://evil.example/v4/linode/instances"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => LinodeImport.ListAsync("token", handler));

        Assert.Contains("301", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LinodeList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await LinodeImport.ListAsync("token", new CannedHandler((_, count) => Answer(count == 1 ? LinodeFirstPage : LinodeLastPage))));

        var (added, again, library) = MergeTwice(queue.Dequeue);

        Assert.Equal((4, 0), (added, again));
        Assert.Equal("Linode", Assert.Single(library.Groups).Name);
    }

    // Vultr

    private const string VultrFirstPage = """
        {
          "instances": [
            {
              "id": "cb676a46-66fd-4dfb-b839-443f2e6c0b60",
              "os": "Ubuntu 24.04 LTS x64",
              "ram": 2048,
              "disk": 60,
              "main_ip": "192.0.2.123",
              "vcpu_count": 1,
              "region": "ewr",
              "plan": "vc2-1c-2gb",
              "date_created": "2020-10-10T01:56:20+00:00",
              "status": "active",
              "allowed_bandwidth": 2000,
              "netmask_v4": "255.255.254.0",
              "gateway_v4": "192.0.2.1",
              "power_status": "running",
              "server_status": "ok",
              "v6_network": "2001:db8:1000::",
              "v6_main_ip": "2001:db8:1000:0:5400:3ff:fe1d:1",
              "v6_network_size": 64,
              "label": "Example Instance",
              "internal_ip": "10.1.96.3",
              "hostname": "my-hostname",
              "tag": "web",
              "tags": [ "web", "env:prod" ],
              "os_id": 2284,
              "app_id": 0,
              "image_id": "",
              "firewall_group_id": "",
              "features": [ "ipv6" ]
            },
            {
              "id": "2",
              "main_ip": "0.0.0.0",
              "region": "fra",
              "status": "pending",
              "v6_main_ip": "2001:db8:2000:0:5400:3ff:fe1d:2",
              "label": "",
              "internal_ip": "",
              "hostname": "still-setting-up",
              "tag": "legacy",
              "tags": []
            },
            {
              "id": "3",
              "main_ip": "0.0.0.0",
              "region": "fra",
              "v6_main_ip": "",
              "label": "vpc-only",
              "internal_ip": "10.1.96.4",
              "tags": []
            },
            { "id": "4", "main_ip": "0.0.0.0", "v6_main_ip": "", "internal_ip": "", "label": "nothing-yet", "tags": [] }
          ],
          "meta": { "total": 5, "links": { "next": "bmV4dF9fQ0xPVURfSUQ=", "prev": "" } }
        }
        """;

    private const string VultrLastPage = """
        {
          "instances": [ { "id": "5", "main_ip": "198.51.100.77", "label": "", "hostname": "", "region": null, "tags": null } ],
          "meta": { "total": 5, "links": { "next": "", "prev": "cHJldl9fQ0xPVURfSUQ=" } }
        }
        """;

    [Fact]
    public void VultrPage_GivesOneServerPerInstanceWithAnAddress()
    {
        List<HostEntry> hosts = VultrImport.ParsePage(VultrFirstPage, out string? next);

        Assert.Equal("bmV4dF9fQ0xPVURfSUQ=", next);
        Assert.Equal(3, hosts.Count);

        // The main address, the label as the name, and the region and tags, the single old tag being among them.
        Assert.Equal(("Example Instance", "192.0.2.123", 22, "root"), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["ewr", "web", "env:prod"], hosts[0].Tags);

        // Not given its IPv4 yet: the IPv6 one, named by the host name, with the old single tag where there is no list.
        Assert.Equal(("still-setting-up", "2001:db8:2000:0:5400:3ff:fe1d:2"), (hosts[1].Name, hosts[1].Address));
        Assert.Equal(["fra", "legacy"], hosts[1].Tags);

        // Only in a private network: that address.
        Assert.Equal(("vpc-only", "10.1.96.4"), (hosts[2].Name, hosts[2].Address));
    }

    [Fact]
    public void VultrPage_TheLastOne_HasAnEmptyNext_AndSurvivesMissingParts()
    {
        HostEntry host = Assert.Single(VultrImport.ParsePage(VultrLastPage, out string? next));

        Assert.Null(next);
        Assert.Equal(("198.51.100.77", "198.51.100.77"), (host.Name, host.Address));
        Assert.Empty(host.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"instances\": null}")]
    [InlineData("{\"error\": \"Invalid API token.\", \"status\": 401}")]
    public void VultrPage_ThatIsNotAnAnswer_IsRefused(string body) =>
        Assert.Throws<CloudImportException>(() => VultrImport.ParsePage(body, out _));

    [Fact]
    public void VultrError_IsShownInVultrOwnWords()
    {
        Assert.Equal("Invalid API token.", VultrImport.ErrorMessage("{\"error\": \"Invalid API token.\", \"status\": 401}"));
        Assert.Null(VultrImport.ErrorMessage("{\"error\": \"\", \"status\": 500}"));
        Assert.Null(VultrImport.ErrorMessage("Bad Gateway"));
    }

    [Fact]
    public async Task VultrList_FollowsTheCursor_WithTheKeyInTheHeaderOnly()
    {
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? VultrFirstPage : VultrLastPage));

        HostLibrary library = await VultrImport.ListAsync(" vultr-secret ", handler);

        Assert.Equal(["Example Instance", "still-setting-up", "vpc-only", "198.51.100.77"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Vultr", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(
            ["https://api.vultr.com/v2/instances?per_page=500", "https://api.vultr.com/v2/instances?per_page=500&cursor=bmV4dF9fQ0xPVURfSUQ%3D"],
            handler.Requests.Select(r => r.Address.AbsoluteUri));
        AssertTokenOnlyInHeader(handler, "api.vultr.com", "vultr-secret");
    }

    [Fact]
    public async Task VultrList_ACursorThatLooksLikeAnAddress_StaysAValueSentToVultr()
    {
        string page = "{\"instances\": [], \"meta\": {\"links\": {\"next\": \"x&per_page=1#@http://evil.example/\"}}}";
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? page : VultrLastPage));

        await VultrImport.ListAsync("token", handler);

        Uri second = handler.Requests[1].Address;
        Assert.Equal((Uri.UriSchemeHttps, "api.vultr.com", "/v2/instances"), (second.Scheme, second.Host, second.AbsolutePath));
        Assert.Equal("?per_page=500&cursor=x%26per_page%3D1%23%40http%3A%2F%2Fevil.example%2F", second.Query);
        Assert.Empty(second.Fragment);
    }

    [Fact]
    public async Task VultrList_WithACursorThatRepeats_Stops()
    {
        var handler = new CannedHandler((_, _) => Answer(VultrFirstPage));

        HostLibrary library = await VultrImport.ListAsync("token", handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(6, library.Hosts.Count);
    }

    [Fact]
    public async Task VultrList_WhenRefused_SaysWhatVultrSaid()
    {
        var handler = new CannedHandler((_, _) => Answer("{\"error\": \"Invalid API token.\", \"status\": 401}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => VultrImport.ListAsync("wrong", handler));

        Assert.Equal("Invalid API token.", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VultrList_ARedirect_IsRefusedAndNotFollowed()
    {
        var handler = new CannedHandler((_, _) => Redirect("http://api.vultr.com/v2/instances?per_page=500"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => VultrImport.ListAsync("token", handler));

        Assert.Contains("301", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VultrList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await VultrImport.ListAsync("token", new CannedHandler((_, count) => Answer(count == 1 ? VultrFirstPage : VultrLastPage))));

        var (added, again, library) = MergeTwice(queue.Dequeue);

        Assert.Equal((4, 0), (added, again));
        Assert.Equal("Vultr", Assert.Single(library.Groups).Name);
    }

    // Tailscale

    private const string TailscaleDevices = """
        {
          "devices": [
            {
              "addresses": [ "100.87.74.78", "fd7a:115c:a1e0:ac82:4843:ca90:697d:c36e" ],
              "id": "92960230385",
              "nodeId": "n292kg92CNTRL",
              "user": "amelie@example.com",
              "name": "pangolin.tailfe8c.ts.net",
              "hostname": "pangolin",
              "clientVersion": "v1.36.0",
              "updateAvailable": false,
              "os": "linux",
              "created": "2022-12-01T05:23:30Z",
              "connectedToControl": true,
              "keyExpiryDisabled": false,
              "expires": "2023-05-30T04:44:05Z",
              "authorized": true,
              "isExternal": false,
              "machineKey": "",
              "nodeKey": "nodekey:01234567890abcdef",
              "blocksIncomingConnections": false,
              "tailnetLockKey": "tlpub:01234567890abcdef",
              "tailnetLockError": "",
              "tags": [ "tag:golink", "tag:prod" ],
              "isEphemeral": false
            },
            {
              "addresses": [ "fd7a:115c:a1e0:ab12:4843:cd96:6258:b240", "100.101.102.103" ],
              "id": "2",
              "name": "",
              "hostname": "laptop",
              "os": "windows",
              "authorized": true,
              "tags": []
            },
            {
              "addresses": [ "fd7a:115c:a1e0:ab12:4843:cd96:6258:b241" ],
              "id": "3",
              "hostname": "v6-only",
              "os": "macOS",
              "authorized": true
            },
            {
              "addresses": [ "100.64.0.9" ],
              "id": "4",
              "name": "waiting.tailfe8c.ts.net",
              "hostname": "waiting",
              "os": "linux",
              "authorized": false
            },
            { "addresses": [ "8.8.8.8" ], "id": "5", "name": "", "hostname": "not-tailscale", "authorized": true }
          ]
        }
        """;

    [Fact]
    public void TailscaleDevices_GivesOneServerPerAuthorizedDevice()
    {
        List<HostEntry> hosts = TailscaleImport.ParsePage(TailscaleDevices);

        Assert.Equal(3, hosts.Count);

        // The MagicDNS name, the machine name as the name, no user, and the tags without their "tag:".
        Assert.Equal(("pangolin", "pangolin.tailfe8c.ts.net", 22, ""), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["linux", "golink", "prod"], hosts[0].Tags);
        Assert.True(hosts[0].UseAgent);

        // No MagicDNS name: the 100.x address, wherever it is in the list.
        Assert.Equal(("laptop", "100.101.102.103"), (hosts[1].Name, hosts[1].Address));
        Assert.Equal(["windows"], hosts[1].Tags);

        // Only IPv6: the Tailscale IPv6 address.
        Assert.Equal(("v6-only", "fd7a:115c:a1e0:ab12:4843:cd96:6258:b241"), (hosts[2].Name, hosts[2].Address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"devices\": \"all of them\"}")]
    [InlineData("{\"message\": \"API token invalid\"}")]
    public void TailscaleDevices_ThatAreNotAnAnswer_AreRefused(string body) =>
        Assert.Throws<CloudImportException>(() => TailscaleImport.ParsePage(body));

    [Theory]
    [InlineData("evil.example/path")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("-o.example.com")]
    [InlineData("pangolin")]
    public void TailscaleDevices_ANameThatIsNotAHostName_GivesWayToTheAddress(string name)
    {
        string devices = "{\"devices\": [{\"name\": \"" + name + "\", \"hostname\": \"h\", \"addresses\": [\"100.64.0.1\"]}]}";

        Assert.Equal("100.64.0.1", Assert.Single(TailscaleImport.ParsePage(devices)).Address);
    }

    [Fact]
    public void TailscaleError_IsShownInTailscaleOwnWords()
    {
        Assert.Equal("API token invalid", TailscaleImport.ErrorMessage("{\"message\": \"API token invalid\"}"));
        Assert.Null(TailscaleImport.ErrorMessage("{\"message\": \"\"}"));
        Assert.Null(TailscaleImport.ErrorMessage("upstream connect error"));
    }

    [Fact]
    public async Task TailscaleList_AsksOnce_WithTheTokenInTheHeaderOnly()
    {
        var handler = new CannedHandler((_, _) => Answer(TailscaleDevices));

        HostLibrary library = await TailscaleImport.ListAsync(" tskey-api-secret ", handler);

        Assert.Equal(["pangolin", "laptop", "v6-only"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Tailscale", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(["https://api.tailscale.com/api/v2/tailnet/-/devices"], handler.Requests.Select(r => r.Address.AbsoluteUri));
        AssertTokenOnlyInHeader(handler, "api.tailscale.com", "tskey-api-secret");
    }

    [Fact]
    public async Task TailscaleList_WhenRefused_SaysWhatTailscaleSaid()
    {
        var handler = new CannedHandler((_, _) => Answer("{\"message\": \"API token invalid\"}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => TailscaleImport.ListAsync("wrong", handler));

        Assert.Equal("API token invalid", error.Message);
    }

    [Fact]
    public async Task TailscaleList_WhenTheErrorSaysNothing_GivesTheStatus()
    {
        var handler = new CannedHandler((_, _) => Answer(string.Empty, HttpStatusCode.GatewayTimeout));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => TailscaleImport.ListAsync("token", handler));

        Assert.Contains("504", error.Message);
    }

    [Fact]
    public async Task TailscaleList_ARedirect_IsRefusedAndNotFollowed()
    {
        var handler = new CannedHandler((_, _) => Redirect("https://login.tailscale.com.evil.example/"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => TailscaleImport.ListAsync("token", handler));

        Assert.Contains("301", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TailscaleList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await TailscaleImport.ListAsync("token", new CannedHandler((_, _) => Answer(TailscaleDevices))));

        var (added, again, library) = MergeTwice(queue.Dequeue);

        Assert.Equal((3, 0), (added, again));
        Assert.Equal("Tailscale", Assert.Single(library.Groups).Name);
    }

    // What they all share

    [Fact]
    public async Task EveryList_WhenTheNetworkFails_SaysSoAsAnImportError()
    {
        Func<HttpMessageHandler, Task<HostLibrary>>[] lists =
        [
            handler => HetznerImport.ListAsync("token", handler),
            handler => LinodeImport.ListAsync("token", handler),
            handler => VultrImport.ListAsync("token", handler),
            handler => TailscaleImport.ListAsync("token", handler),
        ];

        foreach (var list in lists)
        {
            var error = await Assert.ThrowsAsync<CloudImportException>(() => list(new CannedHandler((_, _) => throw new HttpRequestException("No such host is known."))));
            Assert.Equal("No such host is known.", error.Message);
        }
    }

    [Fact]
    public async Task EveryList_WhenCancelled_IsCancelledRatherThanAnError_AndAsksNothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var handler = new CannedHandler((_, _) => Answer("{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HetznerImport.ListAsync("token", handler, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinodeImport.ListAsync("token", handler, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VultrImport.ListAsync("token", handler, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailscaleImport.ListAsync("token", handler, cancel.Token));
        Assert.Empty(handler.Requests);
    }
}
