using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;
using Elio.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Elio.Connections;

/// <summary>A proxy that makes the connection to the first server of a path on this computer's behalf.</summary>
public sealed record SshProxy(ProxyKind Kind, string Host, int Port, string? User = null, string? Password = null)
{
    // As for SshEndpoint below: the password must never be what gets printed.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Host = ").Append(Host).Append(", Port = ").Append(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>The proxy did not make the connection: it refused, wanted another login, or did not answer.</summary>
public sealed class ProxyFailedException(string message, Exception inner) : IOException(message, inner);

/// <summary>One server along the way, with what is needed to log in to it.</summary>
/// <param name="PrivateKeyText">A private key as the text of a key file, for a key that is kept in the vault and not in a file.</param>
/// <param name="Proxy">The proxy to reach this server through. Only looked at on the first server of a path: the others are reached through the one before them.</param>
public sealed record SshEndpoint(string Host, int Port, string User, string? Password = null, string? PrivateKeyPath = null, string? KeyPassphrase = null, bool UseAgent = false, string? PrivateKeyText = null, SshProxy? Proxy = null)
{
    // What a record prints by default is every member, and this one travels on exceptions, which end up in logs
    // and messages. So only what says which server it is gets printed, and never the password or passphrase.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host).Append(", Port = ").Append(Port.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(", User = ").Append(User);
        return true;
    }
}

public sealed record HostKeyInfo(string Algorithm, string Fingerprint);

/// <summary>Decides whether to go on with this server key. Asked before anything secret is sent.</summary>
public delegate bool HostKeyPolicy(SshEndpoint endpoint, HostKeyInfo key);

/// <summary>The server's key was not accepted, so the connection stopped before any credentials were sent.</summary>
public sealed class HostKeyRejectedException(SshEndpoint endpoint, HostKeyInfo key) : Exception(L.T("The server's key was not accepted."))
{
    public SshEndpoint Endpoint { get; } = endpoint;
    public HostKeyInfo Key { get; } = key;
}

/// <summary>The server refused the login. The message says which methods it would have taken.</summary>
public sealed class LoginRefusedException(SshEndpoint endpoint, string message) : Exception(message)
{
    public SshEndpoint Endpoint { get; } = endpoint;
}

/// <summary>The private key is encrypted and needs its passphrase, or the one given was wrong.</summary>
public sealed class KeyPassphraseNeededException(SshEndpoint endpoint, string message) : Exception(message)
{
    public SshEndpoint Endpoint { get; } = endpoint;
}

/// <summary>A live connection to the last server in a path, through any jump hosts before it.</summary>
internal sealed class SshChain : IDisposable
{
    // An idle shell sends nothing, so without this a server that went away would go unnoticed.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    private readonly List<BaseClient> _clients = [];

    public SshClient Last => (SshClient)_clients[^1];

    /// <summary>The last server's file client, when the chain was opened for files.</summary>
    public SftpClient Files => (SftpClient)_clients[^1];

    public static Task<SshChain> OpenAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, CancellationToken cancellation) =>
        OpenAsync(path, policy, info => new SshClient(info), cancellation);

    public static Task<SshChain> OpenForFilesAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, CancellationToken cancellation) =>
        OpenAsync(path, policy, info => new SftpClient(info), cancellation);

    /// <param name="timeout">How long each step of connecting may take. Null for SSH.NET's usual half minute; tests give less.</param>
    internal static Task<SshChain> OpenAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, TimeSpan? timeout, CancellationToken cancellation) =>
        OpenAsync(path, policy, info => new SshClient(info), cancellation, timeout);

    private static async Task<SshChain> OpenAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, Func<ConnectionInfo, BaseClient> createLast, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var chain = new SshChain();
        try
        {
            string host = path[0].Host;
            int port = path[0].Port;

            for (int i = 0; i < path.Count; i++)
            {
                bool last = i + 1 == path.Count;

                // Only the first server is reached from this computer, so it is the only one a proxy can stand in front of.
                SshProxy? proxy = i == 0 ? path[0].Proxy : null;
                ConnectionInfo info = InfoFor(host, port, path[i], proxy);
                if (timeout is TimeSpan limit) info.Timeout = limit;
                await chain.ConnectHopAsync(path[i], last ? createLast(info) : new SshClient(info), policy, proxy, cancellation).ConfigureAwait(false);
                if (last) break;

                // The next server is reached through a private local port on this one, and proves who it is as usual.
                SshEndpoint next = path[i + 1];
                var tunnel = new ForwardedPortLocal("127.0.0.1", next.Host, (uint)next.Port);
                chain.Last.AddForwardedPort(tunnel);
                tunnel.Start();
                (host, port) = ("127.0.0.1", (int)tunnel.BoundPort);
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    // SSH.NET speaks SOCKS5 and HTTP CONNECT itself, so the proxy is only described to it here. It sends the server's
    // name to the proxy as it is, without looking it up first: the proxy may know names this computer does not.
    private static ConnectionInfo InfoFor(string host, int port, SshEndpoint endpoint, SshProxy? proxy)
    {
        AuthenticationMethod[] methods = AuthenticationMethods(endpoint);
        if (proxy is null || proxy.Kind == ProxyKind.None) return new ConnectionInfo(host, port, endpoint.User, methods);

        // Empty and not null where there is no login: a SOCKS proxy that asks for one anyway is then told so by
        // a login that fails, where a null would end in an error that says nothing.
        return new ConnectionInfo(
            host,
            port,
            endpoint.User,
            proxy.Kind == ProxyKind.Http ? ProxyTypes.Http : ProxyTypes.Socks5,
            proxy.Host,
            proxy.Port,
            proxy.User ?? string.Empty,
            proxy.Password ?? string.Empty,
            methods);
    }

    private async Task ConnectHopAsync(SshEndpoint endpoint, BaseClient client, HostKeyPolicy policy, SshProxy? proxy, CancellationToken cancellation)
    {
        client.KeepAliveInterval = KeepAlive;
        _clients.Add(client);

        HostKeyInfo? rejected = null;
        bool reachedServer = false;
        client.HostKeyReceived += (_, e) =>
        {
            reachedServer = true;
            var key = new HostKeyInfo(e.HostKeyName, "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('='));
            e.CanTrust = policy(endpoint, key);
            if (!e.CanTrust) rejected = key;
        };

        try
        {
            // Not ConnectAsync: the socket it sets up is several times slower once there is any latency
            // (measured at 20 ms: 10 MB/s, against 57 MB/s for the same transfer after Connect).
            Task connecting = Task.Run(client.Connect, CancellationToken.None);
            _ = connecting.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await connecting.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (rejected is not null)
        {
            throw new HostKeyRejectedException(endpoint, rejected);
        }
        catch (SshAuthenticationException ex)
        {
            throw new LoginRefusedException(endpoint, ex.Message);
        }
        catch (ProxyException ex) when (proxy is not null)
        {
            throw new ProxyFailedException(L.T("The proxy {0} did not make the connection: {1}", $"{proxy.Host}:{proxy.Port}", ex.Message), ex);
        }
        catch (SshOperationTimeoutException ex) when (proxy is not null && !reachedServer)
        {
            // Nothing of the server was seen yet, so the wait was for the proxy or for what lies behind it.
            throw new ProxyFailedException(L.T("No answer came through the proxy {0} in time.", $"{proxy.Host}:{proxy.Port}"), ex);
        }
    }

    private static AuthenticationMethod[] AuthenticationMethods(SshEndpoint endpoint)
    {
        var methods = new List<AuthenticationMethod>();

        var keys = new List<IPrivateKeySource>();
        if (endpoint.UseAgent) keys.AddRange(SshAgent.KeySources());
        if (endpoint.PrivateKeyText is not null || endpoint.PrivateKeyPath is not null) keys.Add(LoadKey(endpoint));
        if (keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(endpoint.User, [.. keys]));

        if (endpoint.Password is not null)
        {
            string password = endpoint.Password;
            var interactive = new KeyboardInteractiveAuthenticationMethod(endpoint.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (AuthenticationPrompt prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(new PasswordAuthenticationMethod(endpoint.User, Encoding.UTF8.GetBytes(password)));
            methods.Add(interactive);
        }

        // ConnectionInfo needs at least one, and "none" lets the server say what it wants.
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(endpoint.User));
        return [.. methods];
    }

    private static IPrivateKeySource LoadKey(SshEndpoint endpoint)
    {
        string? passphrase = endpoint.KeyPassphrase;

        try
        {
            // A key from the keychain is text out of the vault. It comes first: an identity stands in for the
            // server's own key file.
            if (endpoint.PrivateKeyText is string text) return SshKeys.Load(text, passphrase);

            string path = endpoint.PrivateKeyPath!;
            string certificate = path + "-cert.pub";
            if (File.Exists(certificate)) return new PrivateKeyFile(path, passphrase, certificate);
            return passphrase is null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
        }
        catch (SecurityKeyFileException ex)
        {
            // The file only points at a credential on a security key. The device does the signing.
            return SecurityKeys.Source(ex.File);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new KeyPassphraseNeededException(endpoint, ex.Message);
        }
        catch (SshException ex) when (passphrase is not null)
        {
            throw new KeyPassphraseNeededException(endpoint, ex.Message);
        }
    }

    public void Dispose()
    {
        for (int i = _clients.Count - 1; i >= 0; i--)
        {
            try
            {
                _clients[i].Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or SshException)
            {
                // The connection was already gone.
            }
        }
    }
}

/// <summary>A shell on a server over SSH, with any port forwards that go with it.</summary>
public sealed class SshTransport : ITerminalTransport
{
    private const int ShellBufferSize = 64 * 1024;

    private readonly SshChain _chain;
    private readonly ShellStream _shell;
    private readonly List<string> _forwardProblems = [];
    private int _openForwards;
    private volatile bool _lost;

    private SshTransport(SshChain chain, ShellStream shell)
    {
        (_chain, _shell) = (chain, shell);
        chain.Last.ErrorOccurred += OnConnectionError;
        shell.ErrorOccurred += OnConnectionError;
    }

    /// <inheritdoc />
    public bool ConnectionLost => _lost || !_chain.Last.IsConnected;

    private void OnConnectionError(object? sender, ExceptionEventArgs e)
    {
        _lost = true;

        // A connection that dies without saying goodbye leaves the shell's reader waiting forever. Closing the shell
        // wakes it, but not from here: this runs on the thread that delivers the replies closing would wait for.
        _ = Task.Run(_shell.Dispose);
    }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            ConnectionInfo info = _chain.Last.ConnectionInfo;
            string forwards = _openForwards == 0 ? string.Empty : "  ·  " + L.T("{0} port forwards", _openForwards);
            return $"{info.CurrentClientEncryption}  ·  {info.CurrentKeyExchangeAlgorithm}{forwards}";
        }
    }

    /// <summary>Forwards that could not be set up, for example because the port is taken.</summary>
    public IReadOnlyList<string> ForwardProblems => _forwardProblems;

    /// <summary>Opens a shell at the end of a path whose earlier servers are jump hosts.</summary>
    public static Task<SshTransport> ConnectAsync(IReadOnlyList<SshEndpoint> path, int columns, int rows, HostKeyPolicy policy, IReadOnlyList<PortForward>? forwards, CancellationToken cancellation) =>
        ConnectAsync(path, columns, rows, policy, forwards, null, cancellation);

    /// <summary>Opens a shell at the end of a path whose earlier servers are jump hosts.</summary>
    public static async Task<SshTransport> ConnectAsync(IReadOnlyList<SshEndpoint> path, int columns, int rows, HostKeyPolicy policy, IReadOnlyList<PortForward>? forwards, SshShellOptions? options, CancellationToken cancellation)
    {
        SshChain chain = await SshChain.OpenAsync(path, policy, cancellation).ConfigureAwait(false);
        try
        {
            SshClient client = chain.Last;
            var requests = new ShellRequests { AgentForwarding = options?.ForwardAgent == true };

            // The agent is only ever reached through channels the server opens after this session asked for them.
            if (requests.AgentForwarding) client.AcceptChannels(ShellRequests.AgentChannel, AgentRelay.Serve);

            if (options?.ForwardX11 == true && X11Display.Find() is X11Display display)
            {
                string cookie = X11Relay.NewCookie();
                requests.X11Cookie = cookie;
                requests.X11Screen = (uint)display.Screen;
                client.AcceptChannels(ShellRequests.X11Channel, channel => X11Relay.Serve(channel, display, cookie));
            }

            var transport = new SshTransport(chain, client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, ShellBufferSize, requests));
            if (requests.AgentForwarding && !requests.AgentForwardingAccepted) transport._forwardProblems.Add(L.T("The server refused to forward the SSH agent."));
            if (requests.X11Cookie is not null && !requests.X11Accepted) transport._forwardProblems.Add(L.T("The server refused to forward X11."));
            foreach (PortForward forward in forwards ?? []) transport.StartForward(forward);
            return transport;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The last commands the user's shell on the server remembers, oldest first. Read over the connection already
    /// open, without a second login. Empty if the server keeps none where they are usually kept.
    /// </summary>
    public async Task<string[]> ReadShellHistoryAsync(int lines, CancellationToken cancellation)
    {
        try
        {
            using SshCommand read = _chain.Last.CreateCommand($"cat ~/.bash_history ~/.zsh_history 2>/dev/null | tail -n {lines}");
            read.CommandTimeout = TimeSpan.FromSeconds(10);
            await read.ExecuteAsync(cancellation).ConfigureAwait(false);
            return read.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is SshException or SocketException or TimeoutException or IOException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
    }

    private void StartForward(PortForward forward)
    {
        ForwardedPort port = forward.Kind switch
        {
            ForwardKind.Local => new ForwardedPortLocal(forward.BindAddress, (uint)forward.BindPort, forward.TargetHost, (uint)forward.TargetPort),
            ForwardKind.Remote => new ForwardedPortRemote(forward.BindAddress, (uint)forward.BindPort, forward.TargetHost, (uint)forward.TargetPort),
            _ => new ForwardedPortDynamic(forward.BindAddress, (uint)forward.BindPort),
        };

        try
        {
            _chain.Last.AddForwardedPort(port);
            port.Start();
            _openForwards++;
        }
        catch (Exception ex) when (ex is SshException or SocketException or InvalidOperationException)
        {
            _forwardProblems.Add($"{forward}: {ex.Message}");
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        await _shell.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _shell.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _shell.Dispose();
        _chain.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>What a command did on one server.</summary>
public sealed record CommandResult(int? ExitCode, string Output, string Error, TimeSpan Duration, string? Failure)
{
    public bool Succeeded => Failure is null && ExitCode == 0;
}

public static class SshCommandRunner
{
    /// <summary>Runs one command and collects its output, without opening a shell.</summary>
    public static async Task<CommandResult> RunAsync(IReadOnlyList<SshEndpoint> path, string command, HostKeyPolicy policy, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using SshChain chain = await SshChain.OpenAsync(path, policy, cancellation).ConfigureAwait(false);
            using SshCommand run = chain.Last.CreateCommand(command);
            await run.ExecuteAsync(cancellation).ConfigureAwait(false);
            return new CommandResult(run.ExitStatus, run.Result, run.Error, clock.Elapsed, null);
        }
        catch (HostKeyRejectedException)
        {
            return new CommandResult(null, string.Empty, string.Empty, clock.Elapsed, L.T("The host key is not known. Connect once first."));
        }
        catch (Exception ex) when (ex is LoginRefusedException or KeyPassphraseNeededException or SshException or SocketException or TimeoutException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return new CommandResult(null, string.Empty, string.Empty, clock.Elapsed, ex.Message);
        }
    }
}
