using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// Azure and Google Cloud, which are listed through the user's own az and gcloud: what becomes of what they print,
/// what is asked of them in which order, and that nothing they print can put something onto a command line.
/// </summary>
public sealed class CloudImportCliTests
{
    /// <summary>Plays a command-line tool: answers each command from a list, and keeps what was asked.</summary>
    private sealed class FakeTool(params string[] answers)
    {
        private int _next;

        public List<IReadOnlyList<string>> Asked { get; } = [];

        public Task<string> Run(IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            Asked.Add(arguments);
            return Task.FromResult(answers[_next++]);
        }
    }

    private static (int First, int Second) MergeTwice(Func<HostLibrary> list)
    {
        var library = new HostLibrary();
        int first = LibraryMerge.Merge(library, list(), _ => false);
        int second = LibraryMerge.Merge(library, list(), _ => false);
        return (first, second);
    }

    // Azure: what `az account show --output json` prints.

    private const string AzureAccount = """
        {
          "environmentName": "AzureCloud",
          "homeTenantId": "11111111-2222-3333-4444-555555555555",
          "id": "0b1f6471-1bf0-4dda-aec3-111122223333",
          "isDefault": true,
          "managedByTenants": [],
          "name": "Pay-As-You-Go",
          "state": "Enabled",
          "tenantId": "11111111-2222-3333-4444-555555555555",
          "user": { "name": "someone@example.com", "type": "user" }
        }
        """;

    // What `az vm list --show-details --output json` prints: the machine as ARM gives it, with the CLI's own
    // powerState, publicIps, privateIps, fqdns and macAddresses added, each a text with commas between values.
    private const string AzureMachines = """
        [
          {
            "additionalCapabilities": null,
            "availabilitySet": null,
            "fqdns": "",
            "hardwareProfile": { "vmSize": "Standard_B2s", "vmSizeProperties": null },
            "id": "/subscriptions/0b1f6471-1bf0-4dda-aec3-111122223333/resourceGroups/WEB-RG/providers/Microsoft.Compute/virtualMachines/web-1",
            "location": "westeurope",
            "macAddresses": "00-0D-3A-2B-4C-5D",
            "name": "web-1",
            "networkProfile": { "networkInterfaces": [ { "id": "/subscriptions/0b1f6471-1bf0-4dda-aec3-111122223333/resourceGroups/WEB-RG/providers/Microsoft.Network/networkInterfaces/web-1VMNic", "primary": null, "resourceGroup": "WEB-RG" } ] },
            "osProfile": {
              "adminPassword": null,
              "adminUsername": "azureuser",
              "allowExtensionOperations": true,
              "computerName": "web-1",
              "linuxConfiguration": { "disablePasswordAuthentication": true, "provisionVmAgent": true, "ssh": { "publicKeys": [ { "keyData": "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGk", "path": "/home/azureuser/.ssh/authorized_keys" } ] } },
              "secrets": [],
              "windowsConfiguration": null
            },
            "powerState": "VM running",
            "privateIps": "10.0.0.4",
            "provisioningState": "Succeeded",
            "publicIps": "203.0.113.20",
            "resourceGroup": "WEB-RG",
            "storageProfile": { "dataDisks": [], "imageReference": { "offer": "0001-com-ubuntu-server-jammy", "publisher": "Canonical", "sku": "22_04-lts-gen2", "version": "latest" }, "osDisk": { "osType": "Linux" } },
            "tags": { "env": "prod", "team": "" },
            "type": "Microsoft.Compute/virtualMachines",
            "vmId": "6d0d3f5c-0000-4000-8000-000000000001",
            "zones": null
          },
          {
            "name": "db-1",
            "location": "westeurope",
            "osProfile": { "adminUsername": "dbadmin" },
            "powerState": "VM running",
            "publicIps": "",
            "privateIps": "10.0.0.5,10.0.1.5",
            "tags": null
          },
          {
            "name": "two-addresses",
            "location": "northeurope",
            "osProfile": { "adminUsername": "azureuser" },
            "powerState": "VM running",
            "publicIps": "198.51.100.7,203.0.113.8",
            "privateIps": "10.1.0.4"
          },
          {
            "name": "asleep",
            "location": "westeurope",
            "osProfile": { "adminUsername": "azureuser" },
            "powerState": "VM deallocated",
            "publicIps": "",
            "privateIps": "10.0.0.9"
          },
          {
            "name": "stopping",
            "powerState": "VM stopping",
            "publicIps": "203.0.113.99",
            "privateIps": "10.0.0.10"
          },
          {
            "name": "no-network-yet",
            "powerState": "VM running",
            "publicIps": "",
            "privateIps": ""
          }
        ]
        """;

    [Fact]
    public void AzureAccount_GivesTheSubscriptionIdAndName() =>
        Assert.Equal(("0b1f6471-1bf0-4dda-aec3-111122223333", "Pay-As-You-Go"), AzureImport.ParseAccount(AzureAccount));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"name\": \"x\"}")]
    [InlineData("{\"id\": \"--subscription=x\", \"name\": \"x\"}")]
    [InlineData("{\"id\": \"0b1f6471-1bf0-4dda-aec3-111122223333 & calc\"}")]
    public void AzureAccount_WithoutASubscriptionId_IsRefused(string json) =>
        Assert.Throws<CloudImportException>(() => AzureImport.ParseAccount(json));

    [Fact]
    public void AzureMachines_GivesTheRunningOnesWithAnAddress()
    {
        List<HostEntry> hosts = AzureImport.Parse(AzureMachines);

        Assert.Equal(["web-1", "db-1", "two-addresses"], hosts.Select(h => h.Name));

        // The public address, the administrator as the user, the location and the tags.
        Assert.Equal(("203.0.113.20", 22, "azureuser"), (hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["westeurope", "env=prod", "team"], hosts[0].Tags);
        Assert.True(hosts[0].UseAgent);

        // No public address: the first private one.
        Assert.Equal(("10.0.0.5", "dbadmin"), (hosts[1].Address, hosts[1].User));
        Assert.Equal(["westeurope"], hosts[1].Tags);

        // Two public addresses: the first of them.
        Assert.Equal("198.51.100.7", hosts[2].Address);
    }

    [Theory]
    [InlineData("1.2.3.4.evil.example")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("0.0.0.0")]
    [InlineData("1")]
    [InlineData("203.0.113.5 ; calc")]
    public void AzureMachines_AnAddressThatIsNotOne_IsNotKept(string ip)
    {
        string json = "[{\"name\": \"x\", \"powerState\": \"VM running\", \"publicIps\": \"" + ip + "\", \"privateIps\": \"\"}]";

        Assert.Empty(AzureImport.Parse(json));
    }

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("root;calc")]
    [InlineData("a b")]
    [InlineData("$(id)")]
    public void AzureMachines_AUserThatSshCouldMisread_IsLeftEmpty(string user)
    {
        string json = "[{\"name\": \"x\", \"powerState\": \"VM running\", \"publicIps\": \"203.0.113.1\", \"osProfile\": {\"adminUsername\": \"" + user + "\"}}]";

        Assert.Equal(string.Empty, Assert.Single(AzureImport.Parse(json)).User);
    }

    [Fact]
    public void AzureMachines_ANameWithControlCharacters_IsShownWithoutThem()
    {
        string json = "[{\"name\": \"web\\u001b]0;owned\\u0007-1\", \"powerState\": \"VM running\", \"publicIps\": \"203.0.113.1\"}, {\"name\": \"\\u001b\", \"powerState\": \"VM running\", \"publicIps\": \"203.0.113.2\"}]";

        List<HostEntry> hosts = AzureImport.Parse(json);

        Assert.Equal(["web]0;owned-1", "203.0.113.2"], hosts.Select(h => h.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"value\": []}")]
    [InlineData("WARNING: something")]
    public void AzureMachines_ThatAreNotAList_AreRefused(string json) =>
        Assert.Throws<CloudImportException>(() => AzureImport.Parse(json));

    [Fact]
    public async Task AzureList_AsksForTheSubscriptionThenItsMachines_ByItsId()
    {
        var az = new FakeTool(AzureAccount, AzureMachines);

        HostLibrary library = await AzureImport.ListAsync(az.Run);

        Assert.Equal(["account", "show", "--only-show-errors", "--output", "json"], az.Asked[0]);
        Assert.Equal(["vm", "list", "--show-details", "--subscription", "0b1f6471-1bf0-4dda-aec3-111122223333", "--only-show-errors", "--output", "json"], az.Asked[1]);

        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Azure / Pay-As-You-Go", group.Name);
        Assert.Equal(3, library.Hosts.Count);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));
    }

    [Fact]
    public async Task AzureList_ASubscriptionWithoutAName_IsNamedByItsId()
    {
        HostLibrary library = await AzureImport.ListAsync(new FakeTool("{\"id\": \"0b1f6471-1bf0-4dda-aec3-111122223333\"}", "[]").Run);

        Assert.Equal("Azure / 0b1f6471-1bf0-4dda-aec3-111122223333", Assert.Single(library.Groups).Name);
        Assert.Empty(library.Hosts);
    }

    [Fact]
    public async Task AzureList_WhenAzRefuses_SaysWhatItSaid_AndAsksNothingMore()
    {
        int asked = 0;
        Task<string> Refuse(IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            asked++;
            throw new CloudImportException("Please run 'az login' to setup account.");
        }

        var error = await Assert.ThrowsAsync<CloudImportException>(() => AzureImport.ListAsync(Refuse));

        Assert.Equal("Please run 'az login' to setup account.", error.Message);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AzureList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await AzureImport.ListAsync(new FakeTool(AzureAccount, AzureMachines).Run));

        Assert.Equal((3, 0), MergeTwice(queue.Dequeue));
    }

    // Google Cloud: what `gcloud compute instances list --format=json` prints, the Instance resource of the Compute
    // Engine API as it is, with the zone as the address of its resource.

    private const string GoogleInstances = """
        [
          {
            "canIpForward": false,
            "cpuPlatform": "Intel Broadwell",
            "creationTimestamp": "2024-02-29T07:58:39.343-08:00",
            "deletionProtection": false,
            "id": "1234567890123456789",
            "kind": "compute#instance",
            "labelFingerprint": "42WmSpB8rSM=",
            "labels": { "env": "prod", "team": "web" },
            "machineType": "https://www.googleapis.com/compute/v1/projects/my-project-123/zones/us-central1-a/machineTypes/e2-medium",
            "name": "web-1",
            "networkInterfaces": [
              {
                "accessConfigs": [ { "kind": "compute#accessConfig", "name": "External NAT", "natIP": "203.0.113.30", "networkTier": "PREMIUM", "type": "ONE_TO_ONE_NAT" } ],
                "fingerprint": "tYH4vYxNfdE=",
                "kind": "compute#networkInterface",
                "name": "nic0",
                "network": "https://www.googleapis.com/compute/v1/projects/my-project-123/global/networks/default",
                "networkIP": "10.128.0.2",
                "stackType": "IPV4_ONLY",
                "subnetwork": "https://www.googleapis.com/compute/v1/projects/my-project-123/regions/us-central1/subnetworks/default"
              }
            ],
            "selfLink": "https://www.googleapis.com/compute/v1/projects/my-project-123/zones/us-central1-a/instances/web-1",
            "status": "RUNNING",
            "tags": { "fingerprint": "6smc4R4d39I=", "items": [ "http-server" ] },
            "zone": "https://www.googleapis.com/compute/v1/projects/my-project-123/zones/us-central1-a"
          },
          {
            "name": "batch-1",
            "labels": {},
            "networkInterfaces": [
              {
                "accessConfigs": [ { "kind": "compute#accessConfig", "name": "External NAT", "networkTier": "PREMIUM", "type": "ONE_TO_ONE_NAT" } ],
                "name": "nic0",
                "networkIP": "10.132.0.7"
              }
            ],
            "status": "TERMINATED",
            "zone": "https://www.googleapis.com/compute/v1/projects/my-project-123/zones/europe-west1-b"
          },
          {
            "name": "second-nic",
            "networkInterfaces": [
              { "name": "nic0", "networkIP": "10.128.0.9" },
              { "name": "nic1", "networkIP": "192.168.5.2", "accessConfigs": [ { "natIP": "198.51.100.40" } ] }
            ],
            "status": "RUNNING",
            "zone": "us-east1-c"
          },
          {
            "name": "being-made",
            "networkInterfaces": [],
            "status": "PROVISIONING",
            "zone": "https://www.googleapis.com/compute/v1/projects/my-project-123/zones/us-central1-a"
          }
        ]
        """;

    [Fact]
    public void GoogleInstances_GivesOneServerPerInstanceWithAnAddress()
    {
        List<HostEntry> hosts = GoogleCloudImport.Parse(GoogleInstances);

        Assert.Equal(["web-1", "batch-1", "second-nic"], hosts.Select(h => h.Name));

        // The external address, the zone's name, the labels and the status. Nobody's user is guessed.
        Assert.Equal(("203.0.113.30", 22, string.Empty), (hosts[0].Address, hosts[0].Port, hosts[0].User));
        Assert.Equal(["us-central1-a", "env=prod", "team=web", "RUNNING"], hosts[0].Tags);
        Assert.True(hosts[0].UseAgent);

        // A stopped instance has given its external address back: the internal one.
        Assert.Equal("10.132.0.7", hosts[1].Address);
        Assert.Equal(["europe-west1-b", "TERMINATED"], hosts[1].Tags);

        // An external address on any interface comes before an internal one; a zone given by name stays as it is.
        Assert.Equal("198.51.100.40", hosts[2].Address);
        Assert.Equal(["us-east1-c", "RUNNING"], hosts[2].Tags);
    }

    [Theory]
    [InlineData("1.2.3.4.evil.example")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("0.0.0.0")]
    [InlineData("0x7f.1")]
    public void GoogleInstances_AnAddressThatIsNotOne_IsNotKept(string ip)
    {
        string json = "[{\"name\": \"x\", \"networkInterfaces\": [{\"networkIP\": \"" + ip + "\", \"accessConfigs\": [{\"natIP\": \"" + ip + "\"}]}]}]";

        Assert.Empty(GoogleCloudImport.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("Listed 0 items.")]
    public void GoogleInstances_ThatAreNotAList_AreRefused(string json) =>
        Assert.Throws<CloudImportException>(() => GoogleCloudImport.Parse(json));

    [Theory]
    [InlineData("my-project-123\n", "my-project-123")]
    [InlineData("Your active configuration is: [default]\nmy-project-123\n", "my-project-123")]
    [InlineData("example.com:my-project\r\n", "example.com:my-project")]
    public void GoogleProject_IsTheLastLinePrinted(string output, string project) =>
        Assert.Equal(project, GoogleCloudImport.ParseProject(output));

    [Theory]
    [InlineData("")]
    [InlineData("(unset)")]
    [InlineData("my-project & calc")]
    [InlineData("--impersonate-service-account=x")]
    [InlineData("My-Project")]
    [InlineData("short")]
    [InlineData("ends-with-dash-")]
    [InlineData("a\"b-project")]
    public void GoogleProject_ThatIsNotAProjectId_IsRefused(string output) =>
        Assert.Throws<CloudImportException>(() => GoogleCloudImport.ParseProject(output));

    [Fact]
    public async Task GoogleList_AsksForTheProjectThenItsInstances_ByItsId()
    {
        var gcloud = new FakeTool("my-project-123\n", GoogleInstances);

        HostLibrary library = await GoogleCloudImport.ListAsync(gcloud.Run);

        Assert.Equal(["config", "get-value", "project", "--quiet"], gcloud.Asked[0]);
        Assert.Equal(["compute", "instances", "list", "--project", "my-project-123", "--format=json", "--quiet"], gcloud.Asked[1]);

        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal("Google Cloud / my-project-123", group.Name);
        Assert.Equal(3, library.Hosts.Count);
        Assert.All(library.Hosts, h => Assert.Equal(group.Id, h.GroupId));
    }

    [Fact]
    public async Task GoogleList_WithoutAProject_AsksNothingMore()
    {
        var gcloud = new FakeTool("(unset)\n", GoogleInstances);

        await Assert.ThrowsAsync<CloudImportException>(() => GoogleCloudImport.ListAsync(gcloud.Run));

        Assert.Single(gcloud.Asked);
    }

    [Fact]
    public async Task GoogleList_ImportedTwice_AddsNothingTheSecondTime()
    {
        var queue = new Queue<HostLibrary>();
        for (int i = 0; i < 2; i++) queue.Enqueue(await GoogleCloudImport.ListAsync(new FakeTool("my-project-123", GoogleInstances).Run));

        Assert.Equal((3, 0), MergeTwice(queue.Dequeue));
    }

    // The runner itself.

    [Theory]
    [InlineData("vm")]
    [InlineData("--show-details")]
    [InlineData("--format=json")]
    [InlineData("example.com:my-project")]
    [InlineData("0b1f6471-1bf0-4dda-aec3-111122223333")]
    public void CliArgument_ThatIsAPlainWord_IsAllowed(string argument) =>
        Assert.True(CloudCli.IsPlainArgument(argument));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a&calc")]
    [InlineData("a|calc")]
    [InlineData("%PATH%")]
    [InlineData("a^b")]
    [InlineData("\"")]
    [InlineData("$(id)")]
    [InlineData("a\nb")]
    [InlineData("a>b")]
    public void CliArgument_ThatAShellOrCmdWouldRead_IsRefused(string argument) =>
        Assert.False(CloudCli.IsPlainArgument(argument));

    [Fact]
    public async Task CliRun_WithAnArgumentThatIsNotPlain_RunsNothing()
    {
        var error = await Assert.ThrowsAsync<CloudImportException>(() =>
            CloudCli.RunAsync("definitely-not-a-tool-elio", "Test", ["list", "x&calc"], TimeSpan.FromSeconds(5), new Dictionary<string, string>(), CancellationToken.None));

        Assert.DoesNotContain("calc", error.Message);
    }

    [Fact]
    public async Task CliRun_AToolThatIsNotThere_SaysSo()
    {
        Assert.Null(CloudCli.Locate("definitely-not-a-tool-elio"));

        var error = await Assert.ThrowsAsync<CloudImportException>(() =>
            CloudCli.RunAsync("definitely-not-a-tool-elio", "Test", ["list"], TimeSpan.FromSeconds(5), new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("definitely-not-a-tool-elio", error.Message);
    }

    [Fact]
    public async Task CliRun_GivesBackWhatTheToolPrinted()
    {
        (string tool, string[] arguments) = OperatingSystem.IsWindows() ? ("cmd", new[] { "/c", "echo", "hello" }) : ("echo", new[] { "hello" });

        string printed = await CloudCli.RunAsync(tool, "Test", arguments, TimeSpan.FromSeconds(20), new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal("hello", printed.Trim());
    }

    /// <summary>
    /// A tool the way az and gcloud are installed: a batch file on Windows, which cmd.exe runs and which hands its
    /// whole command line on with %*, as az.cmd and gcloud.cmd do; a script elsewhere.
    /// </summary>
    private static string Script(string folder, string windows, string unix)
    {
        string path = Path.Combine(folder, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool");
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo off\r\n" + windows + "\r\n" : "#!/bin/sh\n" + unix + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task CliRun_AToolThatIsABatchFile_GetsItsArgumentsAsTheyWere()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("elio-cli-");
        try
        {
            string tool = Script(folder.FullName, "echo %*", "echo \"$@\"");

            string printed = await CloudCli.RunProgramAsync(tool, "fake-tool", "Test", ["vm", "--subscription", "0b1f6471-1bf0-4dda-aec3-111122223333", "--format=json"], TimeSpan.FromSeconds(20), new Dictionary<string, string>(), CancellationToken.None);

            Assert.Equal("vm --subscription 0b1f6471-1bf0-4dda-aec3-111122223333 --format=json", printed.Trim());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CliRun_AToolThatRefuses_SaysWhatItSaid()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("elio-cli-");
        try
        {
            string tool = Script(folder.FullName, "echo ERROR: Please run 'az login' to setup account. 1>&2\r\nexit /b 1", "echo \"ERROR: Please run 'az login' to setup account.\" >&2\nexit 1");

            var error = await Assert.ThrowsAsync<CloudImportException>(() =>
                CloudCli.RunProgramAsync(tool, "fake-tool", "Test", ["account", "show"], TimeSpan.FromSeconds(20), new Dictionary<string, string>(), CancellationToken.None));

            Assert.Equal("Please run 'az login' to setup account.", error.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CliRun_AToolThatFails_IsAnImportError()
    {
        (string tool, string[] arguments) = OperatingSystem.IsWindows() ? ("cmd", new[] { "/c", "exit", "3" }) : ("false", Array.Empty<string>());

        var error = await Assert.ThrowsAsync<CloudImportException>(() =>
            CloudCli.RunAsync(tool, "Test", arguments, TimeSpan.FromSeconds(20), new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("exit code", error.Message);
    }

    [Fact]
    public async Task CliRun_AToolThatTakesTooLong_IsStoppedAndSaysSo()
    {
        (string tool, string[] arguments) = OperatingSystem.IsWindows() ? ("ping", new[] { "-n", "30", "127.0.0.1" }) : ("sleep", new[] { "30" });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<CloudImportException>(() =>
            CloudCli.RunAsync(tool, "Test", arguments, TimeSpan.FromMilliseconds(500), new Dictionary<string, string>(), CancellationToken.None));

        Assert.Equal("Test did not answer in time.", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void CliComplaint_IsTheToolsOwnWords_WithoutItsPrefixOrControlCharacters()
    {
        Assert.Equal("Please run 'az login' to setup account.", CloudCli.Complaint("ERROR: Please run 'az login' to setup account.\n"));
        Assert.Equal("(gcloud.compute.instances.list) Some requests did not succeed:\n- Required 'compute.instances.list' permission", CloudCli.Complaint("ERROR: (gcloud.compute.instances.list) Some requests did not succeed:\n - Required 'compute.instances.list' permission\n"));
        Assert.Equal("[31mred", CloudCli.Complaint("\u001b[31mred"));
        Assert.Null(CloudCli.Complaint("\n \n"));
    }
}
