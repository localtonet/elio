using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>The containers running under Docker on this computer, each as something a tab can open.</summary>
public static class DockerContainers
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    // Tabs keep the fields apart, since a container name cannot contain one.
    private const string ListFormat = "{{.Names}}\t{{.Image}}\t{{.Status}}";

    // bash where the image has it, sh where it does not.
    private const string ShellCommand = "sh -c \"if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi\"";

    /// <summary>Asks Docker what is running.</summary>
    /// <exception cref="IOException">Docker is not installed, is not running, or did not answer.</exception>
    public static async Task<List<HostEntry>> ListAsync(CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("ps");
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add(ListFormat);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException(L.T("Docker could not be started."));
        }
        catch (Win32Exception)
        {
            throw new IOException(L.T("Docker was not found on this computer."));
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

                if (process.ExitCode != 0) throw new IOException(FirstLine(await error.ConfigureAwait(false)) ?? L.T("Docker is not running."));
                return Parse(await output.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                TryKill(process);
                throw new IOException(L.T("Docker did not answer."));
            }
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It ended by itself in the meantime.
        }
    }

    /// <summary>Reads the lines `docker ps` printed with <see cref="ListFormat"/>.</summary>
    internal static List<HostEntry> Parse(string output)
    {
        var containers = new List<HostEntry>();
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            string name = fields[0].Trim();

            // The name goes onto a command line, so anything Docker would not have allowed is not trusted.
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) continue;

            containers.Add(new HostEntry
            {
                Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("elio-docker-container:" + name))),
                Kind = HostKind.Local,
                Name = name,
                Address = $"docker exec -it {name} {ShellCommand}",
                Tags = fields.Skip(1).Select(f => f.Trim()).Where(f => f.Length > 0).ToList(),
                AutoReconnect = false,
            });
        }

        return containers;
    }
}
