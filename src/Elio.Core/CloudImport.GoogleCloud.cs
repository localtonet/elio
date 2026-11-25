using System.Text.Json;
using System.Text.RegularExpressions;

namespace Elio.Core;

/// <summary>
/// Lists the Compute Engine instances of the Google Cloud project the gcloud CLI is set to, as servers. It goes
/// through the user's own <c>gcloud</c> and its login, so Elio is given no credentials at all.
/// </summary>
public static partial class GoogleCloudImport
{
    /// <summary>Who the messages name.</summary>
    public const string Provider = "Google Cloud";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    // No question is ever asked, and the JSON comes in UTF-8 whatever the system's code page.
    private static readonly Dictionary<string, string> ToolEnvironment = new(StringComparer.Ordinal)
    {
        ["CLOUDSDK_CORE_DISABLE_PROMPTS"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    // A project id: a lowercase letter, then 4 to 28 lowercase letters, digits or dashes, and no dash at the end. Old
    // projects of an organization have their domain in front, "example.com:my-project".
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9.-]{0,61}[a-z0-9])?:)?[a-z][a-z0-9-]{4,28}[a-z0-9]$")]
    private static partial Regex ProjectPattern();

    /// <summary>Whether the text has the shape of a project id, such as "my-project-123".</summary>
    public static bool IsProject(string project) => ProjectPattern().IsMatch(project);

    /// <summary>The group a project's instances are put in.</summary>
    public static string GroupName(string project) => $"Google Cloud / {project}";

    /// <summary>
    /// Asks <c>gcloud</c> which project it is set to, then for that project's instances. The project is named in the
    /// second question, so the two answers cannot be about different ones.
    /// </summary>
    /// <param name="run">Runs gcloud, in tests. The real one is used otherwise.</param>
    /// <exception cref="CloudImportException">gcloud is missing, has no project, or refused. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(CloudCliRunner? run = null, CancellationToken cancellation = default)
    {
        run ??= (arguments, token) => CloudCli.RunAsync("gcloud", Provider, arguments, Timeout, ToolEnvironment, token);

        string project = ParseProject(await run(["config", "get-value", "project", "--quiet"], cancellation).ConfigureAwait(false));
        string instances = await run(["compute", "instances", "list", "--project", project, "--format=json", "--quiet"], cancellation).ConfigureAwait(false);

        var group = new HostGroup { Name = GroupName(project) };
        var library = new HostLibrary { Groups = [group] };
        foreach (HostEntry host in Parse(instances))
        {
            host.GroupId = group.Id;
            library.Hosts.Add(host);
        }

        return library;
    }

    /// <summary>The project id <c>gcloud config get-value project</c> printed.</summary>
    /// <exception cref="CloudImportException">No project is set, or what was printed is not a project id.</exception>
    public static string ParseProject(string output)
    {
        // The value is the last line; a note about the configuration in use may come before it.
        string project = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (project.Length == 0 || project == "(unset)")
            throw new CloudImportException(L.T("The gcloud CLI has no project set. Choose one with: gcloud config set project PROJECT_ID"));

        // It goes onto the next command line, so it has to look like a project id and nothing else.
        if (!IsProject(project)) throw new CloudImportException(L.T("The gcloud CLI named a project that does not look like a project id."));
        return project;
    }

    /// <summary>The instances in what <c>gcloud compute instances list --format=json</c> printed. One with no address is left out.</summary>
    /// <exception cref="CloudImportException">It is not a list of instances.</exception>
    public static List<HostEntry> Parse(string json)
    {
        var hosts = new List<HostEntry>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw CloudHttp.Unreadable(Provider);

            foreach (JsonElement instance in document.RootElement.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object || Address(instance) is not string address) continue;

                var tags = new List<string>();

                // The zone comes as the address of the zone's resource, and its name is the last part of that.
                if (CloudJson.Text(instance, "zone") is string zone && CloudText.Clean(zone[(zone.LastIndexOf('/') + 1)..]) is string zoneName) tags.Add(zoneName);
                if (CloudJson.Child(instance, "labels") is { } labels) tags.AddRange(CloudJson.Labels(labels).Select(CloudText.Clean).OfType<string>());
                if (CloudText.Clean(CloudJson.Text(instance, "status")) is string status) tags.Add(status);

                hosts.Add(new HostEntry
                {
                    Name = CloudText.Clean(CloudJson.Text(instance, "name")) ?? address,
                    Address = address,

                    // Compute Engine makes an account for whoever's key it was given, under their own name, so none is guessed.
                    User = string.Empty,
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

    private static string? Address(JsonElement instance)
    {
        List<JsonElement> interfaces = CloudJson.Objects(instance, "networkInterfaces").ToList();

        // The external address of the first interface that has one, and the one inside the network where there is none.
        string? external = interfaces
            .SelectMany(nic => CloudJson.Objects(nic, "accessConfigs"))
            .Select(access => CloudJson.Address(CloudJson.Text(access, "natIP"))?.ToString())
            .FirstOrDefault(ip => ip is not null);

        return external ?? interfaces.Select(nic => CloudJson.Address(CloudJson.Text(nic, "networkIP"))?.ToString()).FirstOrDefault(ip => ip is not null);
    }
}
