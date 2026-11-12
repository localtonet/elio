using System.Net;
using System.Text;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// The cloud imports cannot be tried against real accounts from here, so everything about them that can be
/// checked without one is: the signature against AWS's own published examples, and both providers' answers,
/// page after page, from canned responses.
/// </summary>
public sealed class CloudImportTests
{
    // The credentials AWS uses throughout its documentation and its Signature Version 4 test suite.
    private static readonly AwsCredentials Example = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
    private static readonly DateTime ExampleTime = new(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);

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

    [Fact]
    public void SigningKey_MatchesTheOneInAwsDocumentation()
    {
        byte[] key = AwsSignature.SigningKey(Example.SecretAccessKey, ExampleTime, "us-east-1", "iam");

        Assert.Equal("c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void Authorization_ForGetVanilla_MatchesTheAwsTestSuite()
    {
        KeyValuePair<string, string>[] headers = [new("Host", "example.amazonaws.com"), new("X-Amz-Date", "20150830T123600Z")];

        string request = AwsSignature.CanonicalRequest("GET", "/", string.Empty, headers, [], out string signed);
        string authorization = AwsSignature.Authorization("GET", "/", string.Empty, headers, [], Example, "us-east-1", "service", ExampleTime);

        Assert.Equal("host;x-amz-date", signed);
        Assert.Equal(
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            request);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    [Fact]
    public void Authorization_ForAQueryRequest_MatchesTheExampleInAwsDocumentation()
    {
        // The ListUsers request the Signature Version 4 guide walks through: the same kind of request as
        // DescribeInstances, with its parameters in the address.
        KeyValuePair<string, string>[] headers =
        [
            new("Host", "iam.amazonaws.com"),
            new("Content-Type", "application/x-www-form-urlencoded; charset=utf-8"),
            new("X-Amz-Date", "20150830T123600Z"),
        ];
        string query = AwsSignature.CanonicalQuery([new("Version", "2010-05-08"), new("Action", "ListUsers")]);

        string authorization = AwsSignature.Authorization("GET", "/", query, headers, [], Example, "us-east-1", "iam", ExampleTime);

        Assert.Equal("Action=ListUsers&Version=2010-05-08", query);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
            authorization);
    }

    [Fact]
    public void CanonicalQuery_SortsAndEncodesEveryCharacterAwsDoes()
    {
        string query = AwsSignature.CanonicalQuery([new("b", "x y"), new("NextToken", "a+b/c=="), new("a", "~-_.*")]);

        Assert.Equal("NextToken=a%2Bb%2Fc%3D%3D&a=~-_.%2A&b=x%20y", query);
    }

    [Fact]
    public void CanonicalRequest_TrimsHeaderValuesAndMakesRunsOfSpacesOne()
    {
        string request = AwsSignature.CanonicalRequest("GET", "/", string.Empty, [new("My-Header", "  a   b  "), new("Host", "h")], [], out string signed);

        Assert.Equal("host;my-header", signed);
        Assert.Contains("host:h\nmy-header:a b\n", request);
    }

    private const string Ec2FirstPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <DescribeInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
          <requestId>8f7724cf-496f-496e-8fe3-example</requestId>
          <reservationSet>
            <item>
              <reservationId>r-1</reservationId>
              <instancesSet>
                <item>
                  <instanceId>i-0aaa</instanceId>
                  <instanceState><code>16</code><name>running</name></instanceState>
                  <privateDnsName>ip-10-0-0-12.ec2.internal</privateDnsName>
                  <dnsName>ec2-54-0-0-1.compute-1.amazonaws.com</dnsName>
                  <privateIpAddress>10.0.0.12</privateIpAddress>
                  <ipAddress>54.0.0.1</ipAddress>
                  <tagSet>
                    <item><key>Name</key><value>web &amp; api</value></item>
                    <item><key>env</key><value>prod</value></item>
                    <item><key>aws:autoscaling:groupName</key><value>asg-1</value></item>
                    <item><key>spot</key><value/></item>
                  </tagSet>
                </item>
                <item>
                  <instanceId>i-0bbb</instanceId>
                  <instanceState><code>16</code><name>running</name></instanceState>
                  <dnsName/>
                  <privateIpAddress>10.0.0.13</privateIpAddress>
                  <ipAddress>54.0.0.2</ipAddress>
                  <tagSet/>
                </item>
              </instancesSet>
            </item>
            <item>
              <reservationId>r-2</reservationId>
              <instancesSet>
                <item>
                  <instanceId>i-0ccc</instanceId>
                  <instanceState><code>80</code><name>stopped</name></instanceState>
                  <privateIpAddress>10.0.0.14</privateIpAddress>
                </item>
              </instancesSet>
            </item>
          </reservationSet>
          <nextToken>page+two/==</nextToken>
        </DescribeInstancesResponse>
        """;

    private const string Ec2SecondPage = """
        <DescribeInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
          <reservationSet>
            <item>
              <instancesSet>
                <item>
                  <instanceId>i-0ddd</instanceId>
                  <instanceState><code>16</code><name>running</name></instanceState>
                  <privateIpAddress>10.0.0.15</privateIpAddress>
                </item>
                <item>
                  <instanceId>i-0eee</instanceId>
                  <instanceState><code>16</code><name>running</name></instanceState>
                </item>
              </instancesSet>
            </item>
          </reservationSet>
        </DescribeInstancesResponse>
        """;

    [Fact]
    public void Ec2Page_GivesOneServerPerRunningInstance()
    {
        List<HostEntry> hosts = AwsEc2Import.ParsePage(Ec2FirstPage, out string? next);

        Assert.Equal("page+two/==", next);
        Assert.Equal(2, hosts.Count);

        // The Name tag and the public DNS name, with the other tags kept and AWS's own left out.
        Assert.Equal(("web & api", "ec2-54-0-0-1.compute-1.amazonaws.com", 22, "ec2-user"), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["env=prod", "spot"], hosts[0].Tags);

        // No Name tag and no DNS name: the instance id and the public address.
        Assert.Equal(("i-0bbb", "54.0.0.2"), (hosts[1].Name, hosts[1].Address));
        Assert.Empty(hosts[1].Tags);
    }

    [Fact]
    public void Ec2Page_WithoutAPublicAddress_FallsBackToThePrivateOne_AndSkipsWhatHasNone()
    {
        HostEntry host = Assert.Single(AwsEc2Import.ParsePage(Ec2SecondPage, out string? next));

        Assert.Null(next);
        Assert.Equal(("i-0ddd", "10.0.0.15"), (host.Name, host.Address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<html><body>Sign in</body></html>")]
    [InlineData("<!DOCTYPE a [<!ENTITY b \"c\">]><DescribeInstancesResponse>&b;</DescribeInstancesResponse>")]
    public void Ec2Page_ThatIsNotAnAnswer_IsRefused(string body) =>
        Assert.Throws<CloudImportException>(() => AwsEc2Import.ParsePage(body, out _));

    [Fact]
    public void Ec2Error_IsShownInAwsOwnWords()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Response><Errors><Error><Code>AuthFailure</Code><Message>AWS was not able to validate the provided access credentials</Message></Error></Errors><RequestID>1</RequestID></Response>
            """;

        Assert.Equal("AWS was not able to validate the provided access credentials (AuthFailure)", AwsEc2Import.ErrorMessage(body));
        Assert.Null(AwsEc2Import.ErrorMessage("<html/>"));
        Assert.Null(AwsEc2Import.ErrorMessage("Service Unavailable"));
    }

    [Fact]
    public async Task Ec2List_FollowsNextToken_AndSignsEveryRequest()
    {
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? Ec2FirstPage : Ec2SecondPage));
        var credentials = new AwsCredentials("AKIDEXAMPLE", Example.SecretAccessKey, "session-token");

        HostLibrary library = await AwsEc2Import.ListAsync(credentials, " EU-West-1 ", handler, clock: () => ExampleTime);

        Assert.Equal(["web & api", "i-0bbb", "i-0ddd"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("AWS / eu-west-1", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://ec2.eu-west-1.amazonaws.com/?Action=DescribeInstances&Filter.1.Name=instance-state-name&Filter.1.Value.1=running&MaxResults=1000&Version=2016-11-15",
            handler.Requests[0].Address.AbsoluteUri);
        Assert.Equal(
            "https://ec2.eu-west-1.amazonaws.com/?Action=DescribeInstances&Filter.1.Name=instance-state-name&Filter.1.Value.1=running&MaxResults=1000&NextToken=page%2Btwo%2F%3D%3D&Version=2016-11-15",
            handler.Requests[1].Address.AbsoluteUri);

        foreach (var (address, headers) in handler.Requests)
        {
            Assert.Equal("20150830T123600Z", headers["x-amz-date"]);
            Assert.Equal("session-token", headers["x-amz-security-token"]);

            // What was sent carries the signature this very request works out to, token header and all.
            KeyValuePair<string, string>[] signedHeaders = [new("host", "ec2.eu-west-1.amazonaws.com"), new("x-amz-date", "20150830T123600Z"), new("x-amz-security-token", "session-token")];
            string expected = AwsSignature.Authorization("GET", "/", address.Query.TrimStart('?'), signedHeaders, [], credentials, "eu-west-1", "ec2", ExampleTime);
            Assert.Equal(expected, headers["authorization"]);
            Assert.Contains("Credential=AKIDEXAMPLE/20150830/eu-west-1/ec2/aws4_request, SignedHeaders=host;x-amz-date;x-amz-security-token, Signature=", headers["authorization"]);

            // The secret signs, and is never itself sent.
            Assert.DoesNotContain(Example.SecretAccessKey, address.AbsoluteUri + string.Join("\n", headers.Values));
        }
    }

    [Fact]
    public async Task Ec2List_WithATokenThatRepeats_Stops()
    {
        var handler = new CannedHandler((_, _) => Answer(Ec2FirstPage));

        HostLibrary library = await AwsEc2Import.ListAsync(Example, "us-east-1", handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(4, library.Hosts.Count);
    }

    [Fact]
    public async Task Ec2List_WhenRefused_SaysWhatAwsSaid()
    {
        var handler = new CannedHandler((_, _) => Answer(
            "<Response><Errors><Error><Code>UnauthorizedOperation</Code><Message>You are not authorized to perform this operation.</Message></Error></Errors></Response>",
            HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => AwsEc2Import.ListAsync(Example, "us-east-1", handler));

        Assert.Equal("You are not authorized to perform this operation. (UnauthorizedOperation)", error.Message);
    }

    [Fact]
    public async Task Ec2List_WhenTheErrorSaysNothing_GivesTheStatus()
    {
        var handler = new CannedHandler((_, _) => Answer("Bad Gateway", HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => AwsEc2Import.ListAsync(Example, "us-east-1", handler));

        Assert.Contains("502", error.Message);
    }

    [Theory]
    [InlineData("eu-west-1", true)]
    [InlineData("us-gov-west-1", true)]
    [InlineData("ap-southeast-2", true)]
    [InlineData("cn-north-1", true)]
    [InlineData("", false)]
    [InlineData("europe", false)]
    [InlineData("eu-west-1.evil.example/", false)]
    [InlineData("eu-west-1@evil.example", false)]
    [InlineData("EU-WEST-1", false)]
    public void Ec2Region_HasToLookLikeOne(string region, bool valid) =>
        Assert.Equal(valid, AwsEc2Import.IsRegion(region));

    [Fact]
    public async Task Ec2List_WithSomethingElseForARegion_AsksNobody()
    {
        var handler = new CannedHandler((_, _) => Answer(Ec2SecondPage));

        await Assert.ThrowsAsync<CloudImportException>(() => AwsEc2Import.ListAsync(Example, "evil.example/?", handler));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Ec2Request_ForARegionInChina_GoesToItsOwnDomain()
    {
        using HttpRequestMessage request = AwsEc2Import.Request(Example, "cn-north-1", null, ExampleTime);

        Assert.Equal("ec2.cn-north-1.amazonaws.com.cn", request.RequestUri!.Host);
        Assert.False(request.Headers.Contains("x-amz-security-token"));
    }

    private const string DropletsFirstPage = """
        {
          "droplets": [
            {
              "id": 3164444,
              "name": "web-1",
              "status": "active",
              "networks": {
                "v4": [
                  { "ip_address": "10.128.192.124", "netmask": "255.255.0.0", "gateway": "nope", "type": "private" },
                  { "ip_address": "192.241.165.154", "netmask": "255.255.255.0", "gateway": "192.241.165.1", "type": "public" }
                ],
                "v6": [ { "ip_address": "2604:a880:0:1010::18a:a001", "netmask": 64, "gateway": "2604:a880:0:1010::1", "type": "public" } ]
              },
              "region": { "name": "New York 3", "slug": "nyc3" },
              "tags": [ "web", "env:prod", "web" ]
            },
            {
              "id": 3164445,
              "name": "internal-only",
              "networks": { "v4": [ { "ip_address": "10.128.192.125", "type": "private" } ], "v6": [] },
              "region": { "slug": "fra1" },
              "tags": []
            },
            { "id": 3164446, "name": "still-building", "networks": { "v4": [], "v6": [] }, "region": { "slug": "fra1" }, "tags": [] }
          ],
          "links": { "pages": { "last": "https://api.digitalocean.com/v2/droplets?page=2&per_page=200", "next": "https://api.digitalocean.com/v2/droplets?page=2&per_page=200" } },
          "meta": { "total": 4 }
        }
        """;

    private const string DropletsLastPage = """
        {
          "droplets": [ { "id": 1, "name": "", "networks": { "v4": [ { "ip_address": "203.0.113.7", "type": "public" } ] }, "region": null, "tags": null } ],
          "links": { "pages": { "first": "https://api.digitalocean.com/v2/droplets?page=1&per_page=200", "prev": "https://api.digitalocean.com/v2/droplets?page=1&per_page=200" } },
          "meta": { "total": 4 }
        }
        """;

    [Fact]
    public void DropletPage_GivesOneServerPerDropletWithAnAddress()
    {
        List<HostEntry> hosts = DigitalOceanImport.ParsePage(DropletsFirstPage, out Uri? next);

        Assert.Equal("https://api.digitalocean.com/v2/droplets?page=2&per_page=200", next?.AbsoluteUri);
        Assert.Equal(2, hosts.Count);
        Assert.Equal(("web-1", "192.241.165.154", 22, "root"), (hosts[0].Name, hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["nyc3", "web", "env:prod"], hosts[0].Tags);
        Assert.Equal(("internal-only", "10.128.192.125"), (hosts[1].Name, hosts[1].Address));
        Assert.Equal(["fra1"], hosts[1].Tags);
    }

    [Fact]
    public void DropletPage_TheLastOne_HasNoNext_AndSurvivesMissingParts()
    {
        HostEntry host = Assert.Single(DigitalOceanImport.ParsePage(DropletsLastPage, out Uri? next));

        Assert.Null(next);
        Assert.Equal(("203.0.113.7", "203.0.113.7"), (host.Name, host.Address));
        Assert.Empty(host.Tags);
    }

    [Theory]
    [InlineData("http://api.digitalocean.com/v2/droplets?page=2")]
    [InlineData("https://api.digitalocean.com.evil.example/v2/droplets?page=2")]
    [InlineData("https://evil.example/v2/droplets?page=2")]
    [InlineData("/v2/droplets?page=2")]
    public void DropletPage_ANextPageSomewhereElse_IsNotFollowed(string link)
    {
        string page = "{\"droplets\": [], \"links\": {\"pages\": {\"next\": \"" + link + "\"}}}";

        Assert.Empty(DigitalOceanImport.ParsePage(page, out Uri? next));
        Assert.Null(next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"droplets\": 3}")]
    [InlineData("{\"id\": \"unauthorized\", \"message\": \"Unable to authenticate you\"}")]
    public void DropletPage_ThatIsNotAnAnswer_IsRefused(string body) =>
        Assert.Throws<CloudImportException>(() => DigitalOceanImport.ParsePage(body, out _));

    [Fact]
    public async Task DigitalOceanList_FollowsThePages_WithTheTokenInTheHeaderOnly()
    {
        var handler = new CannedHandler((_, count) => Answer(count == 1 ? DropletsFirstPage : DropletsLastPage));

        HostLibrary library = await DigitalOceanImport.ListAsync(" dop_v1_secret ", handler);

        Assert.Equal(["web-1", "internal-only", "203.0.113.7"], library.Hosts.Select(h => h.Name));
        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("DigitalOcean", group.Name);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));

        Assert.Equal(
            ["https://api.digitalocean.com/v2/droplets?per_page=200", "https://api.digitalocean.com/v2/droplets?page=2&per_page=200"],
            handler.Requests.Select(r => r.Address.AbsoluteUri));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer dop_v1_secret", r.Headers["authorization"]);
            Assert.DoesNotContain("dop_v1_secret", r.Address.AbsoluteUri);
        });
    }

    [Fact]
    public async Task DigitalOceanList_WhenRefused_SaysWhatDigitalOceanSaid()
    {
        var handler = new CannedHandler((_, _) => Answer("{\"id\": \"Unauthorized\", \"message\": \"Unable to authenticate you\"}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => DigitalOceanImport.ListAsync("wrong", handler));

        Assert.Equal("Unable to authenticate you", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DigitalOceanList_WhenTheNetworkFails_SaysSoAsAnImportError()
    {
        var handler = new CannedHandler((_, _) => throw new HttpRequestException("No such host is known."));

        var error = await Assert.ThrowsAsync<CloudImportException>(() => DigitalOceanImport.ListAsync("token", handler));

        Assert.Equal("No such host is known.", error.Message);
    }

    [Fact]
    public async Task DigitalOceanList_WhenCancelled_IsCancelledRatherThanAnError()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var handler = new CannedHandler((_, _) => Answer(DropletsLastPage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DigitalOceanImport.ListAsync("token", handler, cancel.Token));
    }
}
