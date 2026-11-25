using System.Net;
using System.Text.Json;

namespace Elio.Core;

/// <summary>
/// Lists the running virtual machines of the Azure subscription the Azure CLI is signed in to, as servers. It goes
/// through the user's own <c>az</c> and its login, so no app has to be registered with Microsoft and Elio is given
/// no credentials at all.
/// </summary>
public static class AzureImport
{
    /// <summary>Who the messages name.</summary>
    public const string Provider = "Azure";

    // Listing with the details asks Azure about every machine's network cards and addresses one by one, which takes
    // a while for a large subscription.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    // Only errors on the error stream, and the JSON in UTF-8 whatever the system's code page.
    private static readonly Dictionary<string, string> ToolEnvironment = new(StringComparer.Ordinal)
    {
        ["AZURE_CORE_ONLY_SHOW_ERRORS"] = "true",
        ["AZURE_CORE_NO_COLOR"] = "true",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    /// <summary>The group a subscription's machines are put in.</summary>
    public static string GroupName(string subscription) => $"Azure / {subscription}";

    /// <summary>
    /// Asks <c>az</c> which subscription it is using, then for that subscription's machines with their addresses and
    /// power state. The subscription is named in the second question, so the two answers cannot be about different
    /// ones even if the user switches in between.
    /// </summary>
    /// <param name="run">Runs az, in tests. The real one is used otherwise.</param>
    /// <exception cref="CloudImportException">az is missing, not signed in, or refused. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(CloudCliRunner? run = null, CancellationToken cancellation = default)
    {
        run ??= (arguments, token) => CloudCli.RunAsync("az", Provider, arguments, Timeout, ToolEnvironment, token);

        (string id, string? name) = ParseAccount(await run(["account", "show", "--only-show-errors", "--output", "json"], cancellation).ConfigureAwait(false));
        string machines = await run(["vm", "list", "--show-details", "--subscription", id, "--only-show-errors", "--output", "json"], cancellation).ConfigureAwait(false);

        var group = new HostGroup { Name = GroupName(name ?? id) };
        var library = new HostLibrary { Groups = [group] };
        foreach (HostEntry host in Parse(machines))
        {
            host.GroupId = group.Id;
            library.Hosts.Add(host);
        }

        return library;
    }

    /// <summary>The id of the subscription <c>az account show</c> printed, and its name if it has one.</summary>
    /// <exception cref="CloudImportException">It is not an answer that names a subscription.</exception>
    public static (string Id, string? Name) ParseAccount(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            // The id goes onto the next command line, so it has to be a GUID and nothing else.
            if (CloudJson.Text(document.RootElement, "id") is string id && Guid.TryParseExact(id, "D", out Guid subscription))
                return (subscription.ToString("D"), CloudText.Clean(CloudJson.Text(document.RootElement, "name")));
        }
        catch (JsonException)
        {
        }

        throw new CloudImportException(L.T("The Azure CLI did not say which subscription it is using. Sign in with az login first."));
    }

    /// <summary>The running machines in what <c>az vm list --show-details</c> printed. A machine with no address is left out.</summary>
    /// <exception cref="CloudImportException">It is not a list of machines.</exception>
    public static List<HostEntry> Parse(string json)
    {
        var hosts = new List<HostEntry>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw CloudHttp.Unreadable(Provider);

            foreach (JsonElement machine in document.RootElement.EnumerateArray())
            {
                if (machine.ValueKind != JsonValueKind.Object) continue;

                // Only machines that are running: a stopped one has usually given its public address back, and the
                // one it gets when it starts again is another. Where the state is not known at all, the machine stays.
                if (CloudJson.Text(machine, "powerState") is string state && !string.Equals(state.Trim(), "VM running", StringComparison.OrdinalIgnoreCase)) continue;

                // Several addresses come as one text with commas between them.
                if ((FirstAddress(machine, "publicIps") ?? FirstAddress(machine, "privateIps")) is not string address) continue;

                var tags = new List<string>();
                if (CloudText.Clean(CloudJson.Text(machine, "location")) is string location) tags.Add(location);
                if (CloudJson.Child(machine, "tags") is { } labels) tags.AddRange(CloudJson.Labels(labels).Select(CloudText.Clean).OfType<string>());

                string? user = CloudJson.Child(machine, "osProfile") is { } profile ? CloudJson.Text(profile, "adminUsername") : null;
                hosts.Add(new HostEntry
                {
                    Name = CloudText.Clean(CloudJson.Text(machine, "name")) ?? address,
                    Address = address,

                    // The administrator chosen when the machine was made, who is the one its key was put in for.
                    User = CloudText.IsUserName(user) ? user! : string.Empty,
                    UseAgent = true,
                    Tags = tags.Distinct().ToList(),
                });
            }
        }
        catch (JsonException)
        {
            throw CloudHttp.Unreadable(Provider);
        }

        return hosts;
    }

    private static string? FirstAddress(JsonElement machine, string property) =>
        CloudJson.Text(machine, property)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(CloudJson.Address)
            .OfType<IPAddress>()
            .FirstOrDefault()?.ToString();
}
