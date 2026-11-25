using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>
/// OpenSSH's ProxyCommand: a program on this computer, such as "cloudflared access ssh --hostname %h", whose input and
/// output carry the connection to the server. SSH.NET only connects to an address, so the program's input and output
/// are handed to it through a port on this computer's loopback address that takes exactly one connection.
/// </summary>
public static class ProxyCommand
{
    /// <summary>
    /// Decides whether a command may run. It runs on this computer with the user's rights, and a server entry can
    /// arrive from sync or from a file, so the user sees it and agrees before it runs the first time. Refuses
    /// everything until a head that can ask sets it.
    /// </summary>
    public static Func<string, CancellationToken, Task<bool>> Approve { get; set; } = (_, _) => Task.FromResult(false);

    /// <summary>The name an approval is kept under: the same command, word for word, is the same approval.</summary>
    public static string ApprovalName(string command) =>
        "proxy-command." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();

    /// <summary>
    /// The command with OpenSSH's tokens filled in: %h the server's address, %p its port, %r the user, %n the address
    /// as written, %% a percent sign. Null when a value would change what the command does: the command was approved
    /// as written, so nothing filled in may add a word, a quote or a second command to it.
    /// </summary>
    public static string? Expand(string command, string host, int port, string user)
    {
        if (!IsPlain(host) || !IsPlain(user)) return null;

        var expanded = new StringBuilder(command.Length + 32);
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c != '%' || i + 1 >= command.Length)
            {
                expanded.Append(c);
                continue;
            }

            char token = command[++i];
            switch (token)
            {
                case 'h' or 'n':
                    expanded.Append(host);
                    break;
                case 'p':
                    expanded.Append(port.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'r':
                    expanded.Append(user);
                    break;
                case '%':
                    expanded.Append('%');
                    break;
                default:
                    // A token Elio does not know stays as it was written, as it does in ssh.
                    expanded.Append('%').Append(token);
                    break;
            }
        }

        return expanded.ToString();
    }

    // Letters, digits and the punctuation of names, addresses and user names. No space, quote or shell character.
    private static bool IsPlain(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '@' or '[' or ']');

    /// <summary>How a command line is started: through the shell, as ssh does, so that pipes and quoting work as the user expects.</summary>
    public static ProcessStartInfo StartInfo(string expanded)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(expanded);
        }
        else
        {
            start = new ProcessStartInfo("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(expanded);
        }

        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        return start;
    }

    /// <summary>Asks for approval, then starts the bridge. The caller connects to the port it gives and disposes it with the connection.</summary>
    public static async Task<ProxyCommandBridge> StartAsync(string command, string host, int port, string user, CancellationToken cancellation)
    {
        if (!await Approve(command, cancellation).ConfigureAwait(false))
            throw new ProxyFailedException(L.T("The command to connect through was not allowed to run: {0}", command), new UnauthorizedAccessException());

        string expanded = Expand(command, host, port, user)
            ?? throw new ProxyFailedException(L.T("The server's address or user name cannot be put into the command to connect through."), new ArgumentException(null, nameof(host)));

        return ProxyCommandBridge.Start(expanded);
    }
}

/// <summary>One run of a proxy command, reachable on a loopback port for one connection.</summary>
public sealed class ProxyCommandBridge : IDisposable
{
    // Long enough for SSH.NET to connect, short enough that a port nobody came to does not stay open.
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(30);

    private const int StderrLines = 20;

    private readonly TcpListener _listener;
    private readonly string _command;
    private readonly Queue<string> _stderr = new();
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private TcpClient? _client;

    public int Port { get; }

    private ProxyCommandBridge(string command)
    {
        _command = command;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>What the command last wrote to its error output, to explain a connection that went nowhere.</summary>
    public string ErrorOutput
    {
        get
        {
            lock (_stderr) return string.Join('\n', _stderr);
        }
    }

    internal static ProxyCommandBridge Start(string expanded)
    {
        var bridge = new ProxyCommandBridge(expanded);
        _ = bridge.RunAsync();
        return bridge;
    }

    private async Task RunAsync()
    {
        try
        {
            using var accepting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            accepting.CancelAfter(AcceptTimeout);
            _client = await _listener.AcceptTcpClientAsync(accepting.Token).ConfigureAwait(false);

            // One connection is all this port is for; nobody else gets to it after the first.
            _listener.Stop();
            _client.NoDelay = true;

            _process = Process.Start(ProxyCommand.StartInfo(_command)) ?? throw new IOException(L.T("The command to connect through could not be started."));
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > StderrLines) _stderr.Dequeue();
                }
            };
            _process.BeginErrorReadLine();

            NetworkStream network = _client.GetStream();
            Task up = PumpAsync(network, _process.StandardInput.BaseStream, closeTarget: true);
            Task down = PumpAsync(_process.StandardOutput.BaseStream, network, closeTarget: false);

            // Whichever side ends first ends the connection: a command that exits, or SSH.NET hanging up.
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_stderr) _stderr.Enqueue(ex.Message);
        }
        finally
        {
            Dispose();
        }
    }

    private async Task PumpAsync(Stream from, Stream to, bool closeTarget)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
                await to.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            if (closeTarget) to.Close();
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        _client?.Dispose();
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        _process?.Dispose();
    }
}
