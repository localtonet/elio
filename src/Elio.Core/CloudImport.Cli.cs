using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Elio.Core;

/// <summary>Runs a cloud provider's command-line tool with these arguments and gives back what it printed.</summary>
/// <exception cref="CloudImportException">The tool is not there, refused, or did not answer.</exception>
public delegate Task<string> CloudCliRunner(IReadOnlyList<string> arguments, CancellationToken cancellation);

/// <summary>
/// What the imports that go through a provider's own command-line tool have in common: the tool is found, run with
/// a fixed list of arguments, given a time limit, and read with as much care as an answer from the network. The
/// login is the tool's own, so Elio is given nothing and keeps nothing.
/// </summary>
internal static class CloudCli
{
    // A subscription of a few hundred machines prints well under a megabyte. Nothing honest comes near this.
    private const int LargestOutput = 16 * 1024 * 1024;

    // What a provider says when it fails is cut short here: it is shown in a dialog, not read as a log.
    private const int LongestComplaint = 600;

    /// <summary>
    /// Runs the tool and gives back what it printed, if it ended well. Every argument is checked to be a plain word
    /// first: on Windows the tools are batch files, and cmd.exe reads its command line again by rules of its own.
    /// </summary>
    /// <param name="tool">The tool's name, such as "az", without an extension.</param>
    /// <param name="provider">Who the messages say did not answer.</param>
    /// <param name="environment">Variables set for the tool on top of Elio's own.</param>
    /// <exception cref="CloudImportException">The tool is not there, refused, or did not answer in time.</exception>
    public static Task<string> RunAsync(string tool, string provider, IReadOnlyList<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation)
    {
        if (arguments.FirstOrDefault(argument => !IsPlainArgument(argument)) is not null)
            throw new CloudImportException(L.T("{0} was given something it cannot be asked.", provider));

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            throw new CloudImportException(L.T("{0} can only be run on a computer.", tool));

        string program = Locate(tool) ?? throw new CloudImportException(L.T("{0} was not found on this computer. Install it and sign in with it first.", tool));
        return RunProgramAsync(program, tool, provider, arguments, timeout, environment, cancellation);
    }

    /// <summary>Runs the program found for a tool. The arguments are checked again: this is where they go onto a command line.</summary>
    /// <param name="program">The complete path of the program, or of the batch file on Windows.</param>
    internal static async Task<string> RunProgramAsync(string program, string tool, string provider, IReadOnlyList<string> arguments, TimeSpan timeout, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation)
    {
        if (arguments.FirstOrDefault(argument => !IsPlainArgument(argument)) is not null)
            throw new CloudImportException(L.T("{0} was given something it cannot be asked.", provider));

        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;

        // A tool installed in a folder the PATH of a program started from the desktop does not name (Homebrew's, on a
        // Mac) still finds what it brought along.
        string folder = Path.GetDirectoryName(program) ?? string.Empty;
        start.Environment["PATH"] = folder + Path.PathSeparator + (start.Environment.TryGetValue("PATH", out string? path) ? path : string.Empty);

        cancellation.ThrowIfCancellationRequested();

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CloudImportException(L.T("{0} could not be started.", tool));
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            throw new CloudImportException(L.T("{0} could not be started.", tool));
        }

        using (process)
        {
            // Nothing is ever typed in: a tool that stops to ask a question reads the end of its input and gives up.
            process.StandardInput.Close();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout);
            try
            {
                // Both are read at once, so that neither fills up and stalls the tool. One that prints more than the
                // limit is stopped there and then, which ends the wait for it.
                Task<string> output = ReadAsync(process.StandardOutput, provider, () => Stop(process), limit.Token);
                Task<string> error = ReadAsync(process.StandardError, provider, () => Stop(process), limit.Token);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

                string printed = await output.ConfigureAwait(false);
                string complaint = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new CloudImportException(Complaint(complaint) ?? Complaint(printed) ?? L.T("{0} ended with exit code {1}.", tool, process.ExitCode));
                return printed;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                Stop(process);
                throw new CloudImportException(L.T("{0} did not answer in time.", provider));
            }
            catch
            {
                Stop(process);
                throw;
            }
        }
    }

    /// <summary>
    /// Whether an argument is a plain word: letters, digits and the few marks that options, ids and names of
    /// projects are written with. Nothing a shell or cmd.exe gives a meaning to.
    /// </summary>
    internal static bool IsPlainArgument(string argument) =>
        argument.Length is > 0 and <= 200 && argument.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '=' or '/');

    /// <summary>
    /// Where the tool is: the folders of the PATH, then the places its installers usually put it. Only complete paths
    /// are looked in, never the folder Elio happens to be working in.
    /// </summary>
    internal static string? Locate(string tool)
    {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool + ".cmd", tool + ".bat"] : [tool];
        IEnumerable<string> folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Environment.ExpandEnvironmentVariables(folder.Trim('"')))
            .Concat(UsualFolders());

        foreach (string folder in folders)
        {
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (string name in names)
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> UsualFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programs32 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                Path.Combine(programs, "Microsoft SDKs", "Azure", "CLI2", "wbin"),
                Path.Combine(programs32, "Microsoft SDKs", "Azure", "CLI2", "wbin"),
                Path.Combine(local, "Google", "Cloud SDK", "google-cloud-sdk", "bin"),
                Path.Combine(programs32, "Google", "Cloud SDK", "google-cloud-sdk", "bin"),
                Path.Combine(programs, "Google", "Cloud SDK", "google-cloud-sdk", "bin"),
            ];
        }

        return ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin", Path.Combine(home, "google-cloud-sdk", "bin"), Path.Combine(home, ".local", "bin")];
    }

    // Read in pieces, so that a tool that prints without end is stopped at the limit rather than when memory runs out.
    private static async Task<string> ReadAsync(StreamReader reader, string provider, Action stop, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > LargestOutput)
            {
                stop();
                throw new CloudImportException(L.T("{0} printed more than a list of servers could be.", provider));
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    /// <summary>The first few lines of what a tool said when it failed, without its "ERROR:" in front, or null if it said nothing.</summary>
    internal static string? Complaint(string text)
    {
        List<string> lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CloudText.Clean(line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? line[6..] : line))
            .OfType<string>()
            .Take(4)
            .ToList();
        if (lines.Count == 0) return null;

        string joined = string.Join('\n', lines);
        return joined.Length <= LongestComplaint ? joined : joined[..LongestComplaint] + "…";
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended by itself in the meantime.
        }
    }
}

/// <summary>Text from a provider that is about to be shown as a name.</summary>
internal static class CloudText
{
    private const int LongestName = 200;

    /// <summary>
    /// The text without control characters (a terminal's escape among them) and the spaces around it, cut to a
    /// length a list can show. Null where nothing is left.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;

        string clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > LongestName) clean = clean[..LongestName];
        return clean.Length > 0 ? clean : null;
    }

    /// <summary>
    /// Whether text is a user name SSH can be given as it is: letters, digits, dots, dashes and underscores, not
    /// starting with a dash, which ssh would read as an option.
    /// </summary>
    public static bool IsUserName(string? text) =>
        text is { Length: > 0 and <= 64 } && text[0] != '-' && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
