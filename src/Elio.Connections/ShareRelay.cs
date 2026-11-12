using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Elio.Core;

namespace Elio.Connections;

/// <summary>Why a shared terminal could not be reached through a sync server.</summary>
public enum ShareRelayFailure
{
    /// <summary>The server could not be reached, or did not answer in time.</summary>
    Unreachable,

    /// <summary>There is no shared terminal under the link: it was never there, or the sharing has stopped.</summary>
    NotFound,

    /// <summary>The shared terminal has all the viewers it may have.</summary>
    Full,

    /// <summary>Too many tries from this address, or too many terminals shared from this account.</summary>
    Limited,

    /// <summary>The server did not accept the account of the person sharing.</summary>
    NotSignedIn,

    /// <summary>The server does not pass shared terminals along.</summary>
    NotOffered,

    /// <summary>The server answered with something that makes no sense here.</summary>
    Protocol,
}

/// <summary>A shared terminal could not be reached through a sync server. <see cref="Failure"/> says why.</summary>
public sealed class ShareRelayException : IOException
{
    public ShareRelayException(ShareRelayFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ShareRelayFailure Failure { get; }
}

/// <summary>
/// Links to a terminal shared through a sync server, where neither end has to be reachable from the
/// internet: <c>elio-share://sync.example.com/k7qm2xpd9rk7qm2xpd9r</c>.
/// </summary>
/// <remarks>
/// The link names the server and the room there, and nothing else: the password is not in it. The port is
/// left out when it is the usual one. A server reached without TLS, which only a test or a private network
/// should have, is marked with <c>?plain=1</c>: <c>elio-share://127.0.0.1:8080/k7qm2xpd9rk7qm2xpd9r?plain=1</c>.
/// A link without a room, <c>elio-share://host:port</c>, is the other kind: it points straight at the
/// computer sharing, and <see cref="SessionShare.TryParseLink"/> reads it.
/// </remarks>
public static class ShareRelayLink
{
    // Room ids are lower-case letters and digits without 0, o, 1, i and l. The server decides how long
    // they are; the range here leaves it room to change its mind, and leaves out the 32 characters that
    // ended the direct links of older versions, so that those are not taken for rooms.
    private const string IdAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private const int MinIdLength = 16;
    private const int MaxIdLength = 31;
    private const string PlainMark = "plain=1";

    /// <summary>Reads a link to a terminal shared through a server.</summary>
    /// <param name="joinUrl">The address a viewer connects to: <c>wss://server/v1/share/join/room</c>.</param>
    /// <returns>
    /// False for anything else, including a link that points straight at a computer. The caller then
    /// tries <see cref="SessionShare.TryParseLink"/>.
    /// </returns>
    public static bool TryParse(string? link, [NotNullWhen(true)] out Uri? joinUrl)
    {
        joinUrl = null;
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        if (!uri.Scheme.Equals(SessionShare.LinkScheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return false;

        // The room is the last part of the path. A server that is not at the root of its address has its own path before it.
        string path = uri.AbsolutePath.TrimEnd('/');
        int slash = path.LastIndexOf('/');
        if (slash < 0) return false;

        string id = path[(slash + 1)..].ToLowerInvariant();
        if (!IsRoomId(id)) return false;

        bool plain = uri.Query.TrimStart('?').Split('&').Contains(PlainMark, StringComparer.OrdinalIgnoreCase);
        joinUrl = new UriBuilder(plain ? Uri.UriSchemeWs : Uri.UriSchemeWss, uri.Host, uri.Port, $"{path[..slash]}/v1/share/join/{id}").Uri;
        return true;
    }

    internal static bool IsRoomId(string id)
    {
        if (id.Length is < MinIdLength or > MaxIdLength) return false;
        foreach (char c in id)
        {
            if (!IdAlphabet.Contains(c)) return false;
        }

        return true;
    }

    /// <summary>The link for a room on a server, given the server's address as the sync settings have it.</summary>
    internal static string Format(Uri server, string roomId)
    {
        // Addresses on the server are worked out against the server's own, so what follows its last slash does not count.
        string path = server.AbsolutePath[..server.AbsolutePath.LastIndexOf('/')];
        string port = server.IsDefaultPort ? string.Empty : $":{server.Port}";
        string mark = server.Scheme == Uri.UriSchemeHttp ? "?" + PlainMark : string.Empty;
        return $"{SessionShare.LinkScheme}://{server.Host}{port}{path}/{roomId}{mark}";
    }
}

/// <summary>What both ends of a relayed share have in common: how the server is reached and spoken to.</summary>
internal static class ShareRelayWire
{
    public const int HeaderSize = 5;
    public const byte KindData = 0;
    public const byte KindOpened = 1;
    public const byte KindClosed = 2;

    /// <summary>The most the server takes in one message.</summary>
    public const int MaxMessage = 64 * 1024;

    /// <summary>How much is read from this computer and sent on at a time.</summary>
    public const int Piece = 16 * 1024;

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    // A terminal can be silent for hours. Without something passing now and then, a proxy on the way
    // would take the connection for dead and cut it.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    /// <summary>The WebSocket address of something on a sync server whose address starts with http or https.</summary>
    public static Uri Endpoint(Uri server, string path)
    {
        if (!server.IsAbsoluteUri || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(L.T("The sync server's address must start with https:// or http://."), nameof(server));

        return new UriBuilder(new Uri(server, path)) { Scheme = server.Scheme == Uri.UriSchemeHttp ? Uri.UriSchemeWs : Uri.UriSchemeWss }.Uri;
    }

    /// <exception cref="ShareRelayException">The server could not be reached, or said no.</exception>
    public static async Task<ClientWebSocket> ConnectAsync(Uri url, string? proof, bool hosting, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = KeepAlive;

        // So that a refusal can be told from a server that is not there.
        socket.Options.CollectHttpResponseDetails = true;
        if (proof is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + proof);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or SocketException or OperationCanceledException)
        {
            HttpStatusCode status = socket.HttpStatusCode;
            socket.Dispose();
            if (cancellation.IsCancellationRequested) throw;

            throw Refused(url, hosting, status, ex);
        }
    }

    private static ShareRelayException Refused(Uri url, bool hosting, HttpStatusCode status, Exception ex) => status switch
    {
        HttpStatusCode.NotFound when hosting => new ShareRelayException(ShareRelayFailure.NotOffered, L.T("The sync server at {0} does not pass shared terminals along.", url.Authority), ex),
        HttpStatusCode.NotFound => new ShareRelayException(ShareRelayFailure.NotFound, L.T("There is no shared terminal under this link. The sharing may have stopped."), ex),
        HttpStatusCode.Unauthorized => new ShareRelayException(ShareRelayFailure.NotSignedIn, L.T("The sync server did not accept this account, so the terminal cannot be shared through it."), ex),
        HttpStatusCode.Forbidden => new ShareRelayException(ShareRelayFailure.Full, L.T("This shared terminal already has as many viewers as it can take."), ex),
        HttpStatusCode.TooManyRequests when hosting => new ShareRelayException(ShareRelayFailure.Limited, L.T("Too many terminals are being shared through the sync server from this account. Stop sharing one and try again."), ex),
        HttpStatusCode.TooManyRequests => new ShareRelayException(ShareRelayFailure.Limited, L.T("Too many tries to join from this network. Wait a minute and try again."), ex),
        0 when ex is OperationCanceledException => new ShareRelayException(ShareRelayFailure.Unreachable, L.T("{0} did not answer in {1:0} seconds.", url.Authority, ConnectTimeout.TotalSeconds), ex),
        0 => new ShareRelayException(ShareRelayFailure.Unreachable, L.T("Could not reach {0} to share the terminal: {1}", url.Authority, (ex.InnerException ?? ex).Message), ex),
        _ => new ShareRelayException(ShareRelayFailure.Protocol, L.T("{0} gave an answer that is not a sync server's ({1}).", url.Authority, (int)status), ex),
    };

    /// <summary>Says goodbye and waits a moment for the server to hang up. Never throws.</summary>
    public static async Task CloseAsync(ClientWebSocket socket)
    {
        try
        {
            using var limit = new CancellationTokenSource(CloseTimeout);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Gone already, which is as good as closed.
        }
    }
}

/// <summary>
/// The sharing end of a terminal shared through a sync server. The terminal's <see cref="SessionShare"/>
/// listens on this computer only; this connects out to the server and brings each viewer who joins there
/// to that port, as if they had connected to it themselves.
/// </summary>
/// <remarks>
/// The server passes bytes along and can read none of them: they are sealed between the viewer and the
/// <see cref="SessionShare"/> with keys made from the password. To the <see cref="SessionShare"/> every
/// viewer brought in this way comes from this computer, so it is told the server's number for each, and
/// counts wrong passwords against that. Someone who joins again gets a new number, so what holds guessing
/// back here is the server's limit on joining from one address, and the length of the password.
/// </remarks>
public sealed class ShareRelayHost : IAsyncDisposable
{
    // On disposal, how long viewers' connections are given to finish by themselves. If the sharing was
    // stopped just before, this is what lets its goodbye reach the viewers.
    private static readonly TimeSpan FarewellTime = TimeSpan.FromSeconds(1);

    private readonly ClientWebSocket _socket;
    private readonly int _port;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Viewer> _viewers = [];
    private Task _receiving = Task.CompletedTask;
    private int _disposed;

    private ShareRelayHost(ClientWebSocket socket, string roomId, string link, int port)
    {
        _socket = socket;
        _port = port;
        RoomId = roomId;
        Link = link;
    }

    /// <summary>The room the server gave this terminal.</summary>
    public string RoomId { get; }

    /// <summary>The link to hand to viewers, as <see cref="ShareRelayLink"/> describes. The password is not in it.</summary>
    public string Link { get; }

    /// <summary>Why the server is gone, once <see cref="Closed"/> has been raised. In the user's language.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>
    /// The connection to the server ended without being asked to, and with it the room: the link no longer
    /// works. Raised once, on a background thread. Not raised by <see cref="DisposeAsync"/>.
    /// </summary>
    public event Action? Closed;

    /// <summary>Opens a room on the server for a terminal that is being shared on this computer.</summary>
    /// <param name="server">The sync server's address, as the sync settings have it.</param>
    /// <param name="proof">Gives the proof of who the user is, as for sync. Null shares without an account, where the server allows that.</param>
    /// <param name="localSharePort">The port the terminal's <see cref="SessionShare"/> listens on, on this computer.</param>
    /// <exception cref="ShareRelayException">The server could not be reached, or said no.</exception>
    public static async Task<ShareRelayHost> StartAsync(Uri server, Func<CancellationToken, Task<string>>? proof, int localSharePort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfLessThan(localSharePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localSharePort, 65535);

        Uri url = ShareRelayWire.Endpoint(server, "v1/share/host");
        ClientWebSocket socket = await ShareRelayWire.ConnectAsync(url, proof is null ? null : await proof(ct).ConfigureAwait(false), hosting: true, ct).ConfigureAwait(false);
        try
        {
            string id = await ReadRoomIdAsync(socket, url, ct).ConfigureAwait(false);
            var host = new ShareRelayHost(socket, id, ShareRelayLink.Format(server, id), localSharePort);
            host._receiving = Task.Run(host.ReceiveAsync, CancellationToken.None);
            return host;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The first thing the server says: {"id":"..."}.
    private static async Task<string> ReadRoomIdAsync(ClientWebSocket socket, Uri url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ShareRelayWire.ConnectTimeout);
        try
        {
            byte[] buffer = new byte[512];
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
            {
                using JsonDocument message = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                if (message.RootElement.ValueKind == JsonValueKind.Object
                    && message.RootElement.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is string room
                    && ShareRelayLink.IsRoomId(room))
                {
                    return room;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ShareRelayException(ShareRelayFailure.Unreachable, L.T("{0} did not answer in {1:0} seconds.", url.Authority, ShareRelayWire.ConnectTimeout.TotalSeconds));
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            throw new ShareRelayException(ShareRelayFailure.Unreachable, L.T("Could not reach {0} to share the terminal: {1}", url.Authority, ex.Message), ex);
        }
        catch (JsonException)
        {
            // Reported below, like anything else that is not a room id.
        }

        throw new ShareRelayException(ShareRelayFailure.Protocol, L.T("{0} gave an answer that is not a sync server's ({1}).", url.Authority, 101));
    }

    private async Task ReceiveAsync()
    {
        string? reason = null;
        try
        {
            byte[] buffer = new byte[ShareRelayWire.HeaderSize + ShareRelayWire.MaxMessage];
            while (true)
            {
                int count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length) throw new InvalidDataException(L.T("The sync server sent more at once than it may."));

                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), _stop.Token).ConfigureAwait(false);
                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = L.T("The sync server ended the sharing.");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary || count < ShareRelayWire.HeaderSize) continue;

                uint number = BinaryPrimitives.ReadUInt32BigEndian(buffer);
                switch (buffer[4])
                {
                    case ShareRelayWire.KindOpened:
                        Open(number);
                        break;

                    case ShareRelayWire.KindData:
                        await DeliverAsync(number, buffer.AsMemory(ShareRelayWire.HeaderSize, count - ShareRelayWire.HeaderSize)).ConfigureAwait(false);
                        break;

                    case ShareRelayWire.KindClosed:
                        Take(number)?.Dispose();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            reason = L.T("The connection to the sync server was lost: {0}", ex.Message);
        }

        List<Viewer> left;
        lock (_gate)
        {
            left = [.. _viewers.Values];
            _viewers.Clear();
        }

        foreach (Viewer viewer in left) viewer.Dispose();
        if (Volatile.Read(ref _disposed) != 0) return;

        CloseReason = reason ?? L.T("The connection to the sync server was lost: {0}", _socket.CloseStatusDescription ?? _socket.State.ToString());
        Closed?.Invoke();
    }

    private void Open(uint number)
    {
        var viewer = new Viewer();
        Viewer? before;
        lock (_gate)
        {
            _viewers.Remove(number, out before);
            _viewers[number] = viewer;
        }

        before?.Dispose();
        viewer.Running = Task.Run(() => RunAsync(number, viewer), CancellationToken.None);
    }

    private Viewer? Take(uint number)
    {
        lock (_gate) return _viewers.Remove(number, out Viewer? viewer) ? viewer : null;
    }

    // From the server to the terminal's own listener.
    private async Task DeliverAsync(uint number, ReadOnlyMemory<byte> data)
    {
        Viewer? viewer;
        lock (_gate) viewer = _viewers.GetValueOrDefault(number);
        if (viewer is null) return;

        try
        {
            // Whatever a viewer sends first comes right behind the news that it joined, so the
            // connection for it may still be opening. Waiting here keeps its bytes in order.
            if (await viewer.Connected.Task.ConfigureAwait(false) is NetworkStream stream)
                await stream.WriteAsync(data, viewer.Closing.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Its reading side notices the same and reports the viewer gone.
            viewer.Dispose();
        }
    }

    // From the terminal's own listener to the server, for one viewer, for as long as it lasts.
    private async Task RunAsync(uint number, Viewer viewer)
    {
        byte[] buffer = new byte[ShareRelayWire.HeaderSize + ShareRelayWire.Piece];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, number);
        int localPort = 0;
        try
        {
            // The listener sees only that this comes from this computer. It is told which viewer is behind the
            // port the connection leaves from, before the connection is made, so that it can tell them apart.
            Socket socket = viewer.Client.Client ?? throw new ObjectDisposedException(nameof(TcpClient));
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            SessionShare.NameRelayed(localPort, number);

            await viewer.Client.ConnectAsync(IPAddress.Loopback, _port, viewer.Closing.Token).ConfigureAwait(false);
            NetworkStream stream = viewer.Client.GetStream();
            viewer.Connected.TrySetResult(stream);

            buffer[4] = ShareRelayWire.KindData;
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(ShareRelayWire.HeaderSize, ShareRelayWire.Piece), viewer.Closing.Token).ConfigureAwait(false);
                if (count == 0) break;

                await SendAsync(buffer.AsMemory(0, ShareRelayWire.HeaderSize + count)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // The terminal is not being shared any more, hung up on this viewer, or the server is gone.
        }

        viewer.Connected.TrySetResult(null);
        if (localPort != 0) SessionShare.ForgetRelayed(localPort);
        bool mine;
        lock (_gate) mine = _viewers.GetValueOrDefault(number) == viewer && _viewers.Remove(number);
        viewer.Dispose();

        // The server already knows if it was the one that said so.
        if (!mine) return;

        try
        {
            buffer[4] = ShareRelayWire.KindClosed;
            await SendAsync(buffer.AsMemory(0, ShareRelayWire.HeaderSize)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // The server is gone, and all its viewers with it.
        }
    }

    // One message at a time: the viewers share the one connection.
    private async Task SendAsync(ReadOnlyMemory<byte> message)
    {
        await _sending.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(message, WebSocketMessageType.Binary, true, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// Ends the room: the link stops working and viewers are disconnected. Stop the terminal's
    /// <see cref="SessionShare"/> first, and viewers are told it is over instead of losing the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Task[] running;
        lock (_gate) running = [.. _viewers.Values.Select(static viewer => viewer.Running)];
        if (running.Length > 0 && !_receiving.IsCompleted)
        {
            try
            {
                await Task.WhenAll(running).WaitAsync(FarewellTime).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still being shared, then. These viewers are simply cut off.
            }
        }

        try
        {
            using var limit = new CancellationTokenSource(ShareRelayWire.CloseTimeout);
            await _sending.WaitAsync(limit.Token).ConfigureAwait(false);
            try
            {
                await ShareRelayWire.CloseAsync(_socket).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }

            // The server hangs up in answer, which ends the receiving.
            await _receiving.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not answering. Cut below.
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _receiving.ConfigureAwait(false);
        _socket.Dispose();
    }

    /// <summary>One viewer's connection to the terminal's own listener.</summary>
    private sealed class Viewer : IDisposable
    {
        public TcpClient Client { get; } = new(AddressFamily.InterNetwork) { NoDelay = true };

        public CancellationTokenSource Closing { get; } = new();

        /// <summary>The stream once connected, or null if it never was.</summary>
        public TaskCompletionSource<NetworkStream?> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Running { get; set; } = Task.CompletedTask;

        public void Dispose()
        {
            Closing.Cancel();
            Client.Dispose();
        }
    }
}

/// <summary>
/// The watching end of a terminal shared through a sync server. It connects to the room and offers it on a
/// port of this computer, so that <see cref="SharedSessionTransport.ConnectAsync(string, int, string, CancellationToken)"/>
/// can be pointed at <c>127.0.0.1</c> and <see cref="LocalPort"/> as if the terminal were shared from here.
/// </summary>
/// <remarks>
/// The port takes one connection and then closes. When either side of it ends, so does the other, and
/// nothing is left behind; <see cref="DisposeAsync"/> is for giving up early, and harmless afterwards.
/// </remarks>
public sealed class ShareRelayViewer : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;

    private ShareRelayViewer(ClientWebSocket socket, TcpListener listener)
    {
        _socket = socket;
        _listener = listener;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The port on this computer that leads to the shared terminal.</summary>
    public int LocalPort { get; }

    /// <param name="link">A link as <see cref="ShareRelayHost.Link"/> gives them.</param>
    /// <exception cref="FormatException">The link is not one to a terminal shared through a server.</exception>
    /// <exception cref="ShareRelayException">There is no such shared terminal, it is full, or the server cannot be reached.</exception>
    public static async Task<ShareRelayViewer> OpenAsync(string link, CancellationToken ct)
    {
        if (!ShareRelayLink.TryParse(link, out Uri? url)) throw new FormatException(L.T("The share link is not valid."));

        ClientWebSocket socket = await ShareRelayWire.ConnectAsync(url, null, hosting: false, ct).ConfigureAwait(false);
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var viewer = new ShareRelayViewer(socket, listener);
            viewer._running = Task.Run(viewer.RunAsync, CancellationToken.None);
            return viewer;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task RunAsync()
    {
        TcpClient? client = null;
        try
        {
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            finally
            {
                // One connection is all there is to carry, and an open port is an invitation.
                _listener.Stop();
            }

            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            Task up = UpAsync(stream);
            Task down = DownAsync(client, stream);

            // Whichever side ends first, the other is told and given a moment to finish what it was doing.
            await Task.WhenAny(up, down).ConfigureAwait(false);
            _stop.CancelAfter(ShareRelayWire.CloseTimeout);
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException or InvalidOperationException)
        {
            // Given up on before anything connected, or cut off part-way.
        }
        finally
        {
            client?.Dispose();
            _socket.Dispose();
        }
    }

    // From this computer to the server.
    private async Task UpAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[ShareRelayWire.Piece];
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;

                await _socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // The goodbye below is tried all the same; it does nothing on a connection that is gone.
        }

        await ShareRelayWire.CloseAsync(_socket).ConfigureAwait(false);
    }

    // From the server to this computer.
    private async Task DownAsync(TcpClient client, NetworkStream stream)
    {
        byte[] buffer = new byte[ShareRelayWire.Piece];
        try
        {
            while (true)
            {
                // Pieces of a message are passed on as they come: this side is a plain stream of bytes anyway.
                ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // Ends the same way as a goodbye, below.
        }

        try
        {
            // Only this direction: what was already passed on must still be read at the other end, and
            // closing outright could throw it away.
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _listener.Stop();
        await _running.ConfigureAwait(false);
    }
}
