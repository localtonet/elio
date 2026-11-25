using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>
/// The pods running in the cluster kubectl is pointed at on this computer, each as something a tab can open. Like
/// Docker containers, they are opened with kubectl itself, so its own login and context apply and Elio holds nothing.
/// </summary>
public static class KubernetesPods
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    // One pod per line, its namespace, name and phase apart by tabs, which none of them can contain.
    private const string ListFormat = "{range .items[*]}{.metadata.namespace}{\"\\t\"}{.metadata.name}{\"\\t\"}{.status.phase}{\"\\n\"}{end}";

    // bash where the image has it, sh where it does not.
    private const string ShellCommand = "sh -c \"if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi\"";

    /// <summary>Asks kubectl for the running pods of every namespace it may list.</summary>
    /// <exception cref="IOException">kubectl is not installed, has no cluster, or did not answer.</exception>
    public static async Task<List<HostEntry>> ListAsync(CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("kubectl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "get", "pods", "--all-namespaces", "--field-selector=status.phase=Running", "-o", "jsonpath=" + ListFormat })
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException(L.T("kubectl could not be started."));
        }
        catch (Win32Exception)
        {
            throw new IOException(L.T("kubectl was not found on this computer."));
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(ListTimeout);
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    string said = (await error.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    throw new IOException(said.Length > 0 ? said : L.T("kubectl could not list the pods."));
                }

                return Parse(await output.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                throw new IOException(L.T("kubectl did not answer."));
            }
        }
    }

    /// <summary>Reads the lines kubectl printed with <see cref="ListFormat"/>.</summary>
    internal static List<HostEntry> Parse(string output)
    {
        var pods = new List<HostEntry>();
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < 2) continue;

            string space = fields[0].Trim();
            string name = fields[1].Trim();

            // Both go onto a command line, so anything Kubernetes would not have allowed in a name is not trusted.
            if (!IsName(space) || !IsName(name)) continue;

            pods.Add(new HostEntry
            {
                Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"elio-kubernetes-pod:{space}/{name}"))),
                Kind = HostKind.Local,
                Name = $"{space}/{name}",
                Address = $"kubectl exec -it -n {space} {name} -- {ShellCommand}",
                Tags = fields.Length > 2 && fields[2].Trim().Length > 0 ? [fields[2].Trim()] : [],
                AutoReconnect = false,
            });
        }

        return pods;
    }

    // Lowercase letters, digits, hyphens and dots, as Kubernetes allows in a name, starting with a letter or digit.
    private static bool IsName(string value) =>
        value.Length is > 0 and <= 253
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.');
}
