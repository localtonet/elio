using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>What one message between a shared terminal and someone watching it carries.</summary>
internal enum ShareFrame : byte
{
    /// <summary>Viewer to host, first: being able to seal it proves the viewer has the password.</summary>
    Hello = 1,

    /// <summary>Host to viewer, first: whether typing is allowed, and the host's size.</summary>
    Welcome = 2,

    /// <summary>Host to viewer: what the terminal printed.</summary>
    Output = 3,

    /// <summary>Viewer to host: what the viewer typed.</summary>
    Input = 4,

    /// <summary>Host to viewer: the host's columns and rows.</summary>
    Resize = 5,

    /// <summary>Host to viewer: bytes that repaint the screen as it is now.</summary>
    Snapshot = 6,

    /// <summary>Either way: leaving on purpose.</summary>
    Goodbye = 7,
}

/// <summary>The sealed messages of one shared-session connection, for either end.</summary>
/// <remarks>
/// Both ends open with eight fixed bytes and 32 random ones; the host adds the salt of its password. The
/// password is short enough to be guessed by someone who recorded a connection and can try guesses at
/// leisure, so it is first put through PBKDF2 with that salt, which makes every guess expensive. The keys
/// come from the result and both sets of random bytes, one key for each direction, so a recording of an
/// earlier connection is useless and an end cannot be fed its own messages back. Each message is four
/// bytes of length, then AES-256-GCM over a type byte and the payload. The messages of a direction are
/// numbered, and the number is the nonce: one that is changed, repeated, dropped or moved fails to open,
/// and the connection ends there.
/// This is not a PAKE: the first sealed message lets whoever carried or recorded the connection, the relay
/// included, check guesses at the password offline, each at the price of one PBKDF2. What keeps that out of
/// reach is the length of the password, which is why the ones made here are long and a chosen one may not be short.
/// </remarks>
internal sealed class ShareChannel : IDisposable
{
    public const int MaxPayload = 64 * 1024;
    public const int NonceSize = 32;
    public const int SaltSize = 16;
    public const int SecretSize = 32;
    public const byte Version = 2;

    private const int TagSize = 16;

    // The number in it went up when the password replaced the token, so that an older Elio is turned away
    // at the first bytes instead of failing in some confusing way later.
    private static readonly byte[] Magic = "ELIOSHR2"u8.ToArray();

    /// <summary>
    /// How many rounds of PBKDF2 the password goes through. Both ends must use the same number. It can be
    /// changed only so that tests need not pay for the real one on every connection.
    /// </summary>
    internal static int Iterations = 600_000;

    private readonly Stream _stream;
    private readonly AesGcm _sealer;
    private readonly AesGcm _opener;
    private readonly byte[] _header = new byte[4];
    private ulong _sent;
    private ulong _received;

    private ShareChannel(Stream stream, byte[] sendKey, byte[] receiveKey)
    {
        _stream = stream;
        _sealer = new AesGcm(sendKey, TagSize);
        _opener = new AesGcm(receiveKey, TagSize);
    }

    public static int GreetingSize => Magic.Length + NonceSize;

    public static int HostGreetingSize => GreetingSize + SaltSize;

    /// <summary>The opening bytes a viewer sends: the fixed part and fresh random bytes.</summary>
    public static byte[] Greeting(out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(NonceSize);
        return [.. Magic, .. nonce];
    }

    /// <summary>The opening bytes a host sends: the same, and the salt its password was stretched with.</summary>
    public static byte[] Greeting(byte[] salt, out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(NonceSize);
        return [.. Magic, .. nonce, .. salt];
    }

    /// <summary>The random bytes in a viewer's opening, or null if it is not one.</summary>
    public static byte[]? ReadGreeting(ReadOnlySpan<byte> greeting) =>
        greeting.Length == GreetingSize && greeting[..Magic.Length].SequenceEqual(Magic) ? greeting[Magic.Length..].ToArray() : null;

    /// <summary>Takes a host's opening apart. False if it is not one.</summary>
    public static bool ReadHostGreeting(ReadOnlySpan<byte> greeting, out byte[] nonce, out byte[] salt)
    {
        (nonce, salt) = ([], []);
        if (greeting.Length != HostGreetingSize || !greeting[..Magic.Length].SequenceEqual(Magic)) return false;

        nonce = greeting.Slice(Magic.Length, NonceSize).ToArray();
        salt = greeting[GreetingSize..].ToArray();
        return true;
    }

    /// <summary>
    /// A password as both ends use it: without spaces and dashes, in upper case. A password is read out or
    /// typed over by a person, and how they grouped or cased it must not matter.
    /// </summary>
    public static string Normalize(string password) =>
        string.Concat(password.Where(static c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();

    /// <summary>Turns a password into the secret the keys are made from. Slow on purpose.</summary>
    public static byte[] Stretch(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(password)), salt, Iterations, HashAlgorithmName.SHA256, SecretSize);

    /// <param name="secret">What <see cref="Stretch"/> made of the password.</param>
    public static ShareChannel Create(Stream stream, byte[] secret, byte[] viewerNonce, byte[] hostNonce, bool host)
    {
        byte[] salt = [.. viewerNonce, .. hostNonce];
        byte[] toHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "elio-share viewer to host"u8.ToArray());
        byte[] toViewer = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "elio-share host to viewer"u8.ToArray());
        return host ? new ShareChannel(stream, toViewer, toHost) : new ShareChannel(stream, toHost, toViewer);
    }

    /// <summary>Sends one message. Only one send may be under way at a time.</summary>
    public async ValueTask SendAsync(ShareFrame type, ReadOnlyMemory<byte> payload, CancellationToken cancellation)
    {
        byte[] wire = Seal(type, payload.Span);
        await _stream.WriteAsync(wire, cancellation).ConfigureAwait(false);
    }

    private byte[] Seal(ShareFrame type, ReadOnlySpan<byte> payload)
    {
        int length = payload.Length + 1;
        byte[] plain = new byte[length];
        plain[0] = (byte)type;
        payload.CopyTo(plain.AsSpan(1));

        byte[] wire = new byte[_header.Length + length + TagSize];
        BinaryPrimitives.WriteInt32BigEndian(wire, length);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sent++);

        // The length is checked along with the rest, so it cannot be changed on the way either.
        _sealer.Encrypt(nonce, plain, wire.AsSpan(4, length), wire.AsSpan(4 + length, TagSize), wire.AsSpan(0, 4));
        return wire;
    }

    /// <summary>Waits for one message. Only one receive may be under way at a time.</summary>
    /// <exception cref="CryptographicException">The message was not sealed with the right key, or was altered.</exception>
    public async ValueTask<(ShareFrame Type, ReadOnlyMemory<byte> Payload)> ReceiveAsync(CancellationToken cancellation)
    {
        await _stream.ReadExactlyAsync(_header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(_header);

        // Refused before anything is set aside for it, so a stranger cannot make this end reserve memory.
        if (length < 1 || length > MaxPayload + 1) throw new CryptographicException("The message has an impossible length.");

        byte[] body = new byte[length + TagSize];
        await _stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);

        byte[] plain = Open(body, length);
        return ((ShareFrame)plain[0], plain.AsMemory(1));
    }

    private byte[] Open(byte[] body, int length)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _received++);

        byte[] plain = new byte[length];
        _opener.Decrypt(nonce, body.AsSpan(0, length), body.AsSpan(length, TagSize), plain, _header);
        return plain;
    }

    public static byte[] Size(int columns, int rows)
    {
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)Math.Clamp(columns, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(size.AsSpan(2), (ushort)Math.Clamp(rows, 0, ushort.MaxValue));
        return size;
    }

    public static (int Columns, int Rows) ReadSize(ReadOnlySpan<byte> size) =>
        (BinaryPrimitives.ReadUInt16BigEndian(size), BinaryPrimitives.ReadUInt16BigEndian(size[2..]));

    public void Dispose()
    {
        _sealer.Dispose();
        _opener.Dispose();
    }
}

/// <summary>
/// Lets other copies of Elio watch one terminal, and type into it if allowed. This is the listening side;
/// it belongs to the <see cref="SharedTransport"/> wrapped around that terminal's connection.
/// </summary>
/// <remarks>
/// Nothing listens until <see cref="Start"/>. The link only says where to connect; getting in takes the
/// password, which is meant to reach the viewer some other way than the link did. A viewer has to prove it
/// knows the password before it is sent a single byte of the terminal, and everything after that is
/// encrypted with keys made from it. Each start has a new salt, and a new password unless the caller
/// insists on its own, so stopping takes back what was handed out before.
/// </remarks>
public sealed class SessionShare
{
    public const string LinkScheme = "elio-share";

    private const int MaxViewers = 16;

    // How many strangers may be part-way through proving themselves at once.
    private const int MaxWaiting = 8;

    // After this many failed tries an address is turned away for a while. This is what keeps a password
    // from being guessed by simply trying: a handful of guesses a minute gets nowhere.
    private const int MaxFailures = 5;
    private const int SnapshotPiece = 32 * 1024;
    private const int MinPasswordLength = 12;

    // 20 characters of 31 are 99 bits. Guesses can be checked offline by anyone who saw a connection (see
    // ShareChannel), so the password itself has to be out of reach, not just slow to try here.
    private const int MadePasswordLength = 20;
    private const int MadePasswordGroup = 5;

    // For passwords made here. Without 0 and O, or 1, I and L: they get mixed up when read out or copied by hand.
    private const string PasswordAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    // Past this many addresses on record, the ones whose time is up are forgotten.
    private const int MaxFailureRecords = 256;

    // Viewers a relay brings in all connect from this computer. The relay says here, by the port each of its
    // connections leaves from, which of its viewers that is, so that their wrong tries are counted apart.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Relayed = new();

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RejectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BlockTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FarewellTime = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _input;
    private readonly List<Viewer> _viewers = [];
    private readonly Dictionary<string, (int Count, long Since)> _failures = new(StringComparer.Ordinal);

    // Viewers that have proved themselves and wait for the terminal to be between two pieces of output,
    // which is the only moment a picture of the screen and the output that follows it line up.
    private List<Viewer> _joining = [];
    private TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private int _waiting;
    private int _columns;
    private int _rows;

    internal SessionShare(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> input, int columns, int rows)
    {
        _input = input;
        _columns = columns;
        _rows = rows;
    }

    /// <summary>Whether viewers can connect right now.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _listener is not null;
        }
    }

    /// <summary>The port being listened on, or 0 while not sharing.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// What a viewer must type to get in, as it is to be shown: one made here looks like XXXXX-XXXXX-XXXXX-XXXXX.
    /// Spaces, dashes and case do not matter when it is typed. Empty while not sharing.
    /// </summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>Whether what viewers type reaches the terminal.</summary>
    public bool AllowInput { get; private set; }

    /// <summary>How far behind, in bytes of output, a viewer may fall before it is dropped.</summary>
    public int MaxViewerBacklog { get; set; } = 4 * 1024 * 1024;

    public int ViewerCount
    {
        get
        {
            lock (_gate) return _viewers.Count;
        }
    }

    /// <summary>Someone joined or left. Raised on a background thread.</summary>
    public event Action? ViewersChanged;

    /// <summary>Starts listening.</summary>
    /// <param name="bind">The address to listen on, or null for this computer only.</param>
    /// <param name="port">The port, or 0 to take any free one.</param>
    /// <param name="allowInput">Whether viewers may type.</param>
    /// <param name="password">The password viewers must give, or null to have a new one made.</param>
    /// <exception cref="ArgumentException">The password is too short.</exception>
    /// <exception cref="SocketException">The port cannot be listened on.</exception>
    public void Start(IPAddress? bind, int port, bool allowInput, string? password = null)
    {
        string shown;
        if (password is null)
        {
            char[] made = RandomNumberGenerator.GetItems<char>(PasswordAlphabet, MadePasswordLength);
            shown = string.Join('-', made.Chunk(MadePasswordGroup).Select(static group => new string(group)));
        }
        else
        {
            // Counted as it will be used, so that dashes and spaces cannot pad out a password that is too short.
            if (ShareChannel.Normalize(password).Length < MinPasswordLength)
                throw new ArgumentException(L.T("The password must be at least {0} characters, not counting spaces and dashes.", MinPasswordLength));

            shown = password.Trim();
        }

        TcpListener listener;
        Task<byte[]> secret;
        byte[] salt;
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (_listener is not null) throw new InvalidOperationException(L.T("This terminal is already being shared."));

            listener = new TcpListener(bind ?? IPAddress.Loopback, port);
            listener.Start();

            // Stretched once for all viewers, and in the background: it takes long enough to be felt, and
            // the caller is likely the UI. A viewer that arrives before it is done waits for it.
            byte[] fresh = salt = RandomNumberGenerator.GetBytes(ShareChannel.SaltSize);
            secret = Task.Run(() => ShareChannel.Stretch(shown, fresh));

            stop = new CancellationTokenSource();
            _listener = listener;
            _stop = stop;
            _failures.Clear();
            Password = shown;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            AllowInput = allowInput;
        }

        _ = AcceptAsync(listener, secret, salt, allowInput, stop.Token);
    }

    /// <summary>Stops listening and tells everyone watching that it is over. The old link and password stop working.</summary>
    public void Stop()
    {
        List<Viewer> leaving;
        bool counted;
        lock (_gate)
        {
            if (_listener is null) return;

            _listener.Stop();
            _listener = null;
            _stop?.Cancel();
            _stop = null;
            Password = string.Empty;
            Port = 0;

            counted = _viewers.Count > 0;
            leaving = [.. _viewers, .. _joining];
            _viewers.Clear();
            _joining.Clear();
        }

        foreach (Viewer viewer in leaving) Farewell(viewer);
        if (counted) ViewersChanged?.Invoke();
    }

    /// <summary>The link to hand to a viewer. It says where to connect and nothing else: the password is not in it.</summary>
    /// <param name="host">A name or address the viewer can reach this computer by.</param>
    public static string FormatLink(string host, int port)
    {
        // An IPv6 address has colons of its own, so it goes in brackets.
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        return $"{LinkScheme}://{host}:{port}";
    }

    public static bool TryParseLink(string? link, out string host, out int port)
    {
        (host, port) = (string.Empty, 0);
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        if (!uri.Scheme.Equals(LinkScheme, StringComparison.OrdinalIgnoreCase) || uri.Port is <= 0 or > 65535 || uri.DnsSafeHost.Length == 0) return false;

        // Links used to end in a token. One that still does is taken for where it points, and the rest is ignored.
        (host, port) = (uri.DnsSafeHost, uri.Port);
        return true;
    }

    /// <summary>
    /// Tells every share on this computer that the connection about to be made from a local port carries a relay's
    /// viewer. Called before connecting, and <see cref="ForgetRelayed"/> once the connection is over.
    /// </summary>
    /// <param name="viewer">The number the relay gave the viewer. Each time someone joins there, they get a new one.</param>
    internal static void NameRelayed(int localPort, uint viewer) => Relayed[localPort] = $"relay {viewer}";

    internal static void ForgetRelayed(int localPort) => Relayed.TryRemove(localPort, out _);

    /// <summary>Completes when a viewer is waiting to be let in.</summary>
    internal Task Joined
    {
        get
        {
            lock (_gate) return _joined.Task;
        }
    }

    internal bool HasJoiners
    {
        get
        {
            lock (_gate) return _joining.Count > 0;
        }
    }

    /// <summary>
    /// Lets in the viewers that are waiting, giving each the screen as it is now. Called by the terminal's
    /// own reading, between two pieces of output, so that nothing is missed and nothing arrives twice.
    /// </summary>
    internal void Admit(Func<byte[]> screen)
    {
        List<Viewer> joiners;
        lock (_gate)
        {
            if (_joining.Count == 0) return;

            joiners = _joining;
            _joining = [];
            _joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Not under the lock: the picture may come from the UI, which must not be made to wait on it.
        byte[] picture = screen();

        bool counted = false;
        List<Viewer> late = [];
        lock (_gate)
        {
            foreach (Viewer viewer in joiners)
            {
                if (viewer.Gone) continue;
                if (_listener is null)
                {
                    // Sharing stopped while the picture was being taken.
                    late.Add(viewer);
                    continue;
                }

                viewer.Offer(ShareFrame.Resize, ShareChannel.Size(_columns, _rows), int.MaxValue);
                for (int offset = 0; offset < picture.Length; offset += SnapshotPiece)
                    viewer.Offer(ShareFrame.Snapshot, picture[offset..Math.Min(picture.Length, offset + SnapshotPiece)], int.MaxValue);

                _viewers.Add(viewer);
                counted = true;
            }
        }

        foreach (Viewer viewer in late) Farewell(viewer);
        if (counted) ViewersChanged?.Invoke();
    }

    /// <summary>Queues output for every viewer. Never waits: a viewer with too much queued is dropped instead.</summary>
    internal void Broadcast(byte[] output)
    {
        List<Viewer>? dropped = null;
        lock (_gate)
        {
            foreach (Viewer viewer in _viewers)
            {
                if (!viewer.Offer(ShareFrame.Output, output, MaxViewerBacklog)) (dropped ??= []).Add(viewer);
            }
        }

        if (dropped is null) return;
        foreach (Viewer viewer in dropped) Close(viewer);
    }

    internal void SetSize(int columns, int rows)
    {
        lock (_gate)
        {
            if (columns == _columns && rows == _rows) return;

            (_columns, _rows) = (columns, rows);
            byte[] size = ShareChannel.Size(columns, rows);
            foreach (Viewer viewer in _viewers) viewer.Offer(ShareFrame.Resize, size, int.MaxValue);
        }
    }

    private async Task AcceptAsync(TcpListener listener, Task<byte[]> secret, byte[] salt, bool allowInput, CancellationToken stopped)
    {
        while (!stopped.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopped).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            catch (SocketException)
            {
                // One caller that hung up while waiting must not end the listening.
                continue;
            }

            // Who to hold a wrong try against: the address, or for a viewer a relay on this computer brought in, the
            // relay's number for it. Otherwise a few wrong tries by one of a relay's viewers would shut out all of them.
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            string from = remote is not null && IPAddress.IsLoopback(remote.Address) && Relayed.TryGetValue(remote.Port, out string? relayed)
                ? relayed
                : (remote?.Address ?? IPAddress.None).ToString();
            bool refuse;
            lock (_gate)
            {
                refuse = _waiting >= MaxWaiting || _viewers.Count + _joining.Count >= MaxViewers || Blocked(from);
                if (!refuse) _waiting++;
            }

            if (refuse) client.Dispose();
            else _ = GreetAsync(client, from, secret, salt, allowInput, stopped);
        }
    }

    private bool Blocked(string from)
    {
        if (!_failures.TryGetValue(from, out var record)) return false;
        if (Environment.TickCount64 - record.Since > (long)BlockTime.TotalMilliseconds)
        {
            _failures.Remove(from);
            return false;
        }

        return record.Count >= MaxFailures;
    }

    private async Task GreetAsync(TcpClient client, string from, Task<byte[]> secret, byte[] salt, bool allowInput, CancellationToken stopped)
    {
        Viewer? viewer = null;
        ShareChannel? channel = null;
        try
        {
            client.NoDelay = true;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopped);
            limit.CancelAfter(HandshakeTimeout);

            NetworkStream stream = client.GetStream();
            byte[] greeting = new byte[ShareChannel.GreetingSize];
            await stream.ReadExactlyAsync(greeting, limit.Token).ConfigureAwait(false);
            if (ShareChannel.ReadGreeting(greeting) is byte[] viewerNonce)
            {
                await stream.WriteAsync(ShareChannel.Greeting(salt, out byte[] hostNonce), limit.Token).ConfigureAwait(false);
                channel = ShareChannel.Create(stream, await secret.WaitAsync(limit.Token).ConfigureAwait(false), viewerNonce, hostNonce, host: true);

                // This is where a wrong password shows: the message does not open. It looks the same as
                // any other nonsense from here, so the viewer is not told which it was. Nothing about
                // the terminal has been sent yet, and nothing will be.
                var (type, _) = await channel.ReceiveAsync(limit.Token).ConfigureAwait(false);
                if (type == ShareFrame.Hello)
                {
                    int columns, rows;
                    lock (_gate) (columns, rows) = (_columns, _rows);

                    byte[] welcome = [allowInput ? (byte)1 : (byte)0, .. ShareChannel.Size(columns, rows)];
                    await channel.SendAsync(ShareFrame.Welcome, welcome, limit.Token).ConfigureAwait(false);

                    // A viewer may sit silent for hours, so without this one that vanished would be kept forever.
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    viewer = new Viewer(client, channel);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or CryptographicException or ObjectDisposedException)
        {
            // Counted as a failed try below.
        }

        if (viewer is null)
        {
            // Held for a moment before being let go, so guesses cannot come quickly.
            try
            {
                await Task.Delay(RejectDelay, stopped).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Sharing stopped; there is nothing left to guess at.
            }

            // Counted before hanging up: hanging up is what tells the guesser to try again, and the next
            // try must find this one already on the record.
            lock (_gate)
            {
                _waiting--;
                long now = Environment.TickCount64;

                // A relay's viewers each leave a record of their own, so the list is kept from growing without end.
                if (_failures.Count >= MaxFailureRecords)
                {
                    foreach (string old in _failures.Where(pair => now - pair.Value.Since > (long)BlockTime.TotalMilliseconds).Select(pair => pair.Key).ToList())
                        _failures.Remove(old);
                }

                _failures[from] =_failures.TryGetValue(from, out var record) && now - record.Since <= (long)BlockTime.TotalMilliseconds
                    ? (record.Count + 1, record.Since)
                    : (1, now);
            }

            channel?.Dispose();
            client.Dispose();
            return;
        }

        bool stoppedMeanwhile;
        lock (_gate)
        {
            _waiting--;
            stoppedMeanwhile = stopped.IsCancellationRequested;
            if (!stoppedMeanwhile)
            {
                _joining.Add(viewer);
                _joined.TrySetResult();
            }
        }

        _ = SendAsync(viewer);
        _ = ReceiveAsync(viewer, allowInput);
        if (stoppedMeanwhile) Farewell(viewer);
    }

    private async Task SendAsync(Viewer viewer)
    {
        try
        {
            await foreach (var (type, data) in viewer.Queue.Reader.ReadAllAsync(viewer.Closing.Token).ConfigureAwait(false))
            {
                await viewer.Channel.SendAsync(type, data, viewer.Closing.Token).ConfigureAwait(false);
                viewer.Sent(data.Length);
                if (type != ShareFrame.Goodbye) continue;

                // Only this direction is closed. The other stays open until the viewer hangs up, so that
                // something it typed a moment ago cannot turn the goodbye into a reset.
                viewer.Client.Client.Shutdown(SocketShutdown.Send);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(viewer);
        }
    }

    private async Task ReceiveAsync(Viewer viewer, bool allowInput)
    {
        try
        {
            while (true)
            {
                var (type, payload) = await viewer.Channel.ReceiveAsync(viewer.Closing.Token).ConfigureAwait(false);
                if (type == ShareFrame.Goodbye) break;

                // The viewer was told it may not type, but that is its word only: the rule is kept here.
                if (type == ShareFrame.Input && allowInput && !viewer.Leaving && !payload.IsEmpty)
                    await _input(payload, viewer.Closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The viewer went away, sent something that does not open, or the terminal itself is gone.
        }
        finally
        {
            Close(viewer);
        }
    }

    // Says goodbye, and hangs up shortly after whether or not the goodbye got through.
    private static void Farewell(Viewer viewer)
    {
        viewer.Leaving = true;
        viewer.Offer(ShareFrame.Goodbye, [], int.MaxValue);
        viewer.Queue.Writer.TryComplete();
        viewer.Closing.CancelAfter(FarewellTime);
    }

    private void Close(Viewer viewer)
    {
        bool counted;
        lock (_gate)
        {
            if (viewer.Gone) return;

            viewer.Gone = true;
            counted = _viewers.Remove(viewer);
            _joining.Remove(viewer);
        }

        viewer.Queue.Writer.TryComplete();
        viewer.Closing.Cancel();
        viewer.Client.Dispose();
        viewer.Channel.Dispose();
        if (counted) ViewersChanged?.Invoke();
    }

    private sealed class Viewer(TcpClient client, ShareChannel channel)
    {
        private long _queued;

        public TcpClient Client { get; } = client;

        public ShareChannel Channel { get; } = channel;

        public Channel<(ShareFrame Type, byte[] Data)> Queue { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(ShareFrame, byte[])>(new UnboundedChannelOptions { SingleReader = true });

        public CancellationTokenSource Closing { get; } = new();

        /// <summary>Hung up and forgotten. Only changed under the share's lock.</summary>
        public bool Gone { get; set; }

        /// <summary>Has been told goodbye, so what it still types is not passed on.</summary>
        public volatile bool Leaving;

        /// <returns>False if this would put more than <paramref name="limit"/> bytes in the queue, and so was not added.</returns>
        public bool Offer(ShareFrame type, byte[] data, int limit)
        {
            if (Interlocked.Read(ref _queued) + data.Length > limit) return false;

            Interlocked.Add(ref _queued, data.Length);
            Queue.Writer.TryWrite((type, data));
            return true;
        }

        public void Sent(int count) => Interlocked.Add(ref _queued, -count);
    }
}

/// <summary>
/// A terminal's connection with a door in it for viewers. The terminal uses it exactly as it would the
/// connection inside; while <see cref="Share"/> is running, what the terminal reads is also sent to
/// whoever is watching, and what they type, if allowed, goes down the same connection.
/// </summary>
/// <remarks>
/// Viewers are a step behind on purpose. Output goes to them when the terminal comes back for more, which
/// is when it has finished with the last piece. A viewer who joins is let in at that same moment, so the
/// picture of the screen it starts from and the output it gets afterwards fit together exactly.
/// Nothing a viewer does can hold the terminal up: their output waits in a queue of its own, and a viewer
/// whose queue grows too long is dropped.
/// </remarks>
public sealed class SharedTransport : ITerminalTransport
{
    private const int HistorySize = 256 * 1024;

    private readonly ITerminalTransport _inner;
    private readonly Func<byte[]>? _snapshot;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly byte[] _chunk = new byte[32 * 1024];

    // A read of the connection inside outlives the call that began it: the call may have to stop waiting
    // to let a viewer in, and the read cannot safely be called off and started again.
    private Task<int>? _read;
    private int _offset;
    private int _have;

    // The piece the terminal is working through now, kept for the viewers until it is done with it.
    private byte[]? _unsent;

    // The most recent output, for a viewer who joins when nobody supplied a way to redraw the screen.
    private byte[]? _history;
    private int _historyNext;
    private bool _historyFull;

    /// <param name="inner">The terminal's real connection. Disposed along with this.</param>
    /// <param name="columns">The terminal's size now, which viewers are told when they join.</param>
    /// <param name="snapshot">
    /// Gives bytes that, written to an empty terminal, draw the screen as it is now. Called while the
    /// terminal is waiting for output, from a background thread. Without one, a new viewer is replayed the
    /// last 256 KB of output instead.
    /// </param>
    public SharedTransport(ITerminalTransport inner, int columns, int rows, Func<byte[]>? snapshot = null)
    {
        _inner = inner;
        _snapshot = snapshot;
        Share = new SessionShare(WriteFromViewerAsync, columns, rows);
    }

    /// <summary>Starts and stops the sharing, and says who is watching.</summary>
    public SessionShare Share { get; }

    /// <inheritdoc />
    public bool ConnectionLost => _inner.ConnectionLost;

    /// <inheritdoc />
    public string? Description => _inner.Description;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            // The terminal is back for more, so it has taken in everything it was given.
            Settle();

            if (_have == 0)
            {
                Task joined = Share.Joined;
                _read ??= _inner.ReadAsync(_chunk, _closed.Token).AsTask();
                if (!_read.IsCompleted)
                {
                    await Task.WhenAny(_read, joined).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!_read.IsCompleted) continue;
                }

                Task<int> finished = _read;
                _read = null;
                int count;
                try
                {
                    count = await finished.ConfigureAwait(false);
                }
                catch
                {
                    Share.Stop();
                    throw;
                }

                if (count == 0)
                {
                    Share.Stop();
                    return 0;
                }

                (_offset, _have) = (0, count);
            }

            int take = Math.Min(_have, buffer.Length);
            _chunk.AsSpan(_offset, take).CopyTo(buffer.Span);
            Keep(_offset, take);
            _offset += take;
            _have -= take;
            return take;
        }
    }

    // Passes on what the terminal has finished with, then lets in whoever is waiting.
    private void Settle()
    {
        if (_unsent is byte[] output)
        {
            _unsent = null;
            Share.Broadcast(output);
        }

        if (Share.HasJoiners) Share.Admit(Screen);
    }

    private void Keep(int offset, int count)
    {
        ReadOnlySpan<byte> output = _chunk.AsSpan(offset, count);
        if (_snapshot is null) Remember(output);
        if (Share.ViewerCount > 0) _unsent = output.ToArray();
    }

    private void Remember(ReadOnlySpan<byte> output)
    {
        _history ??= new byte[HistorySize];
        if (output.Length >= HistorySize)
        {
            output[^HistorySize..].CopyTo(_history);
            (_historyNext, _historyFull) = (0, true);
            return;
        }

        int first = Math.Min(output.Length, HistorySize - _historyNext);
        output[..first].CopyTo(_history.AsSpan(_historyNext));
        output[first..].CopyTo(_history);
        if (_historyNext + output.Length >= HistorySize) _historyFull = true;
        _historyNext = (_historyNext + output.Length) % HistorySize;
    }

    private byte[] Screen()
    {
        if (_snapshot is not null)
        {
            try
            {
                return _snapshot();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A viewer with a blank screen that fills in as output arrives beats no viewer at all.
                return [];
            }
        }

        // A full reset first: the replay may begin in the middle of anything.
        ReadOnlySpan<byte> reset = "\u001bc"u8;
        if (_history is null) return reset.ToArray();

        int length = _historyFull ? HistorySize : _historyNext;
        byte[] screen = new byte[reset.Length + length];
        reset.CopyTo(screen);
        Span<byte> replay = screen.AsSpan(reset.Length);
        if (_historyFull)
        {
            _history.AsSpan(_historyNext).CopyTo(replay);
            _history.AsSpan(0, _historyNext).CopyTo(replay[(HistorySize - _historyNext)..]);
        }
        else
        {
            _history.AsSpan(0, length).CopyTo(replay);
        }

        return screen;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // One at a time, so the host's typing and a viewer's never end up interleaved byte by byte.
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    private ValueTask WriteFromViewerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation) => WriteAsync(data, cancellation);

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        Share.SetSize(columns, rows);
        return _inner.ResizeAsync(columns, rows, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Share.Stop();
        await _closed.CancelAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);

        // The read left waiting ends with an error now that the connection is closed. Nobody is waiting
        // for it, so it is looked at here to keep it from being reported as forgotten.
        _ = _read?.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
    }
}

/// <summary>
/// The password given for a shared terminal is not the right one. Separate from other failures so that
/// the viewer can simply be asked again.
/// </summary>
public sealed class WrongSharePasswordException : IOException
{
    public WrongSharePasswordException(string message)
        : base(message)
    {
    }

    public WrongSharePasswordException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Someone else's terminal, watched from here: the viewing end of a <see cref="SessionShare"/>.</summary>
public sealed class SharedSessionTransport : ITerminalTransport
{
    private const int InputPiece = 16 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpClient _client;
    private readonly ShareChannel _channel;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private ReadOnlyMemory<byte> _rest;
    private volatile bool _lost;
    private volatile bool _ended;
    private volatile bool _disposed;

    private SharedSessionTransport(TcpClient client, ShareChannel channel, bool canType, int columns, int rows)
    {
        _client = client;
        _channel = channel;
        CanType = canType;
        (HostColumns, HostRows) = (columns, rows);
    }

    /// <summary>Whether the host lets viewers type. When it does not, typing is dropped here and there.</summary>
    public bool CanType { get; }

    /// <summary>The size of the terminal being watched. A viewer shows that size; it cannot change it.</summary>
    public int HostColumns { get; private set; }

    public int HostRows { get; private set; }

    /// <summary>The host's terminal changed size. Raised from inside <see cref="ReadAsync"/>, on its thread.</summary>
    public event Action<int, int>? HostResized;

    /// <inheritdoc />
    public bool ConnectionLost => _lost;

    /// <inheritdoc />
    public string Description => CanType ? L.T("Shared session") : L.T("Shared session") + " · " + L.T("view only");

    /// <param name="link">A link as <see cref="SessionShare.FormatLink"/> makes them.</param>
    /// <param name="password">The password the person sharing gave. Spaces, dashes and case do not matter.</param>
    /// <exception cref="FormatException">The link is not one.</exception>
    /// <exception cref="WrongSharePasswordException">The password is not the right one.</exception>
    public static Task<SharedSessionTransport> ConnectAsync(string link, string password, CancellationToken cancellation) =>
        SessionShare.TryParseLink(link, out string host, out int port)
            ? ConnectAsync(host, port, password, cancellation)
            : Task.FromException<SharedSessionTransport>(new FormatException(L.T("The share link is not valid.")));

    /// <exception cref="WrongSharePasswordException">The password is not the right one.</exception>
    public static async Task<SharedSessionTransport> ConnectAsync(string host, int port, string password, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(password);

        // No host takes an empty password, and the host allows only a few tries: this one is not spent on it.
        if (ShareChannel.Normalize(password).Length == 0) throw new WrongSharePasswordException(L.T("That is not the password for this shared terminal."));

        var client = new TcpClient { NoDelay = true };
        ShareChannel? channel = null;
        bool greeted = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(ShareChannel.Greeting(out byte[] viewerNonce), timeout.Token).ConfigureAwait(false);

                byte[] greeting = new byte[ShareChannel.HostGreetingSize];
                await stream.ReadExactlyAsync(greeting, timeout.Token).ConfigureAwait(false);
                if (!ShareChannel.ReadHostGreeting(greeting, out byte[] hostNonce, out byte[] salt)) throw new IOException(L.T("{0}:{1} is not a shared Elio session.", host, port));

                greeted = true;

                // Slow on purpose, so not on the caller's thread, which may well be the UI's.
                byte[] secret = await Task.Run(() => ShareChannel.Stretch(password, salt), timeout.Token).ConfigureAwait(false);

                channel = ShareChannel.Create(stream, secret, viewerNonce, hostNonce, host: false);
                await channel.SendAsync(ShareFrame.Hello, new[] { ShareChannel.Version }, timeout.Token).ConfigureAwait(false);

                // Only someone with the password can seal this answer, so it also proves the host is the real one.
                var (type, payload) = await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (type != ShareFrame.Welcome || payload.Length < 5) throw new IOException(L.T("{0}:{1} is not a shared Elio session.", host, port));

                var (columns, rows) = ShareChannel.ReadSize(payload.Span[1..]);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                return new SharedSessionTransport(client, channel, (payload.Span[0] & 1) != 0, columns, rows);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(L.T("{0}:{1} did not answer in {2:0} seconds.", host, port, ConnectTimeout.TotalSeconds));
            }
            catch (Exception ex) when (ex is EndOfStreamException or CryptographicException || (ex is IOException && ex.InnerException is SocketException))
            {
                // The host never says a password is wrong; it waits a moment and hangs up, as it would on
                // anything it cannot read. Once it has greeted properly, that is by far the likeliest reason.
                if (greeted) throw new WrongSharePasswordException(L.T("That is not the password for this shared terminal."), ex);

                // Hung up on before a greeting: not sharing any more, full, or turning this address away
                // after too many wrong passwords.
                throw new IOException(L.T("The shared terminal did not let this connection in. The sharing may have stopped, or there were too many wrong passwords; wait a minute and try again."), ex);
            }
        }
        catch
        {
            channel?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_rest.IsEmpty)
        {
            if (_ended) return 0;

            try
            {
                while (_rest.IsEmpty)
                {
                    var (type, payload) = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    switch (type)
                    {
                        case ShareFrame.Output or ShareFrame.Snapshot:
                            _rest = payload;
                            break;

                        case ShareFrame.Resize when payload.Length >= 4:
                            (HostColumns, HostRows) = ShareChannel.ReadSize(payload.Span);
                            HostResized?.Invoke(HostColumns, HostRows);
                            break;

                        case ShareFrame.Goodbye:
                            _ended = true;
                            return 0;
                    }
                }
            }
            catch (CryptographicException ex)
            {
                // Whatever this was, it did not come from the host as sent. Nothing after it can be trusted.
                _lost = true;
                _client.Dispose();
                throw new IOException(L.T("The shared session sent something that could not be verified, so the link was closed."), ex);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _lost = !cancellationToken.IsCancellationRequested && !_disposed;
                throw new IOException(ex.Message, ex);
            }
        }

        int count = Math.Min(_rest.Length, buffer.Length);
        _rest[..count].CopyTo(buffer);
        _rest = _rest[count..];
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // The host would drop it anyway; not sending it saves the trip.
        if (!CanType || _ended || data.IsEmpty) return;

        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int offset = 0; offset < data.Length; offset += InputPiece)
                await _channel.SendAsync(ShareFrame.Input, data.Slice(offset, Math.Min(InputPiece, data.Length - offset)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _lost = !cancellationToken.IsCancellationRequested && !_disposed;
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Does nothing: the size is the host's to choose. See <see cref="HostResized"/>.</summary>
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
