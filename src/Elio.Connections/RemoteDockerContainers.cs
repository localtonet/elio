using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>A container running on a server, as <c>docker ps</c> on that server described it.</summary>
public sealed record RemoteContainer(string Name, string Image, string Status);

/// <summary>
/// The containers running under Docker on a saved SSH server, and a shell in one of them. The list is asked for the
/// way a command run on several servers is, and the shell is opened as the server itself would be, with
/// <c>docker exec</c> run in place of the login shell.
/// </summary>
public static class RemoteDockerContainers
{
    /// <summary>
    /// What is run on the server. The server's shell sees the format in single quotes, as it is; Docker turns each
    /// \t in it into a tab, which keeps the fields apart since a container name cannot contain one.
    /// </summary>
    public const string ListCommand = "docker ps --format '{{.Names}}\\t{{.Image}}\\t{{.Status}}'";

    // What the server said is shown in a dialog, not read as a log.
    private const int LongestComplaint = 600;

    /// <summary>
    /// Reads what <see cref="ListCommand"/> printed. A line without all three fields (a greeting that the server's
    /// shell printed on its own, say) is passed over, and so is a name Docker would not have allowed: it is about to
    /// go onto a command line.
    /// </summary>
    public static List<RemoteContainer> Parse(string output)
    {
        var containers = new List<RemoteContainer>();
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < 3) continue;

            string name = fields[0].Trim();
            if (!DockerContainers.IsName(name)) continue;

            containers.Add(new RemoteContainer(name, Printable(fields[1]), Printable(fields[2])));
        }

        return containers;
    }

    /// <summary>
    /// What went wrong, in the server's own words, when the command ran but did not end well: Docker missing, or the
    /// user not allowed to use it without sudo. Null when it ended well.
    /// </summary>
    public static string? Problem(CommandResult result)
    {
        if (result.Failure is not null) return result.Failure;
        if (result.ExitCode == 0) return null;

        string said = FirstLines(result.Error) ?? FirstLines(result.Output) ?? L.T("The command ended with exit code {0}.", result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");

        // The usual reason on a server where Docker is installed: the user is not in its group.
        if (said.Contains("permission denied", StringComparison.OrdinalIgnoreCase) && said.Contains("docker", StringComparison.OrdinalIgnoreCase))
            said += "\n\n" + L.T("The user this server logs in as may not use Docker without sudo. Adding them to the docker group lets them.");
        return said;
    }

    /// <summary>
    /// An unsaved copy of the server that opens a shell in the container instead of the server's own: the same
    /// address, port, login, jump hosts, proxy and group, so it connects exactly as the server does. It has an id of
    /// its own, which is the same each time for the same container on the same server, and a name such as
    /// "web-1 / nginx". The server's login script and port forwards stay with the server: the first would be typed
    /// into the container, and the second are already open in the server's own tab or would clash with it.
    /// </summary>
    /// <exception cref="ArgumentException">The container's name is not one Docker allows.</exception>
    public static HostEntry ShellIn(HostEntry server, RemoteContainer container)
    {
        if (!DockerContainers.IsName(container.Name)) throw new ArgumentException(L.T("That is not the name of a container."), nameof(container));

        HostEntry copy = HostCopy.Of(server);
        copy.Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"elio-remote-docker:{server.Id:N}/{container.Name}")));
        copy.Name = $"{server.Name} / {container.Name}";
        copy.RemoteCommand = $"docker exec -it {container.Name} {DockerContainers.ShellCommand}";
        copy.LoginScript = null;
        copy.Forwards = [];
        copy.Tags = container.Image.Length > 0 ? [container.Image] : [];

        // When the container's shell ends, so does the session: there is nothing to reconnect to.
        copy.AutoReconnect = false;
        return copy;
    }

    private static string? FirstLines(string text)
    {
        List<string> lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Printable)
            .Where(line => line.Length > 0)
            .Take(3)
            .ToList();
        if (lines.Count == 0) return null;

        string joined = string.Join('\n', lines);
        return joined.Length <= LongestComplaint ? joined : joined[..LongestComplaint] + "…";
    }

    // What a server printed, with nothing in it that a terminal or a dialog would take as more than text.
    private static string Printable(string text) => new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
}
