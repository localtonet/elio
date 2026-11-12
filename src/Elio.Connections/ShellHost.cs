using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>What a message between a window and the shell keeper is. Windows send the low ones, the keeper the high ones.</summary>
internal enum ShellFrame : byte
{
    Hello = 1,
    Open = 2,
    Attach = 3,
    Input = 4,
    Resize = 5,
    Close = 6,
    Detach = 7,
    List = 8,

    Welcome = 0x81,
    Opened = 0x82,
    Attached = 0x83,
    Output = 0x84,
    Exited = 0x85,
    Error = 0x86,
    Shells = 0x87,
    TakenOver = 0x88,
}

internal enum ShellError : byte
{
    Gone = 1,
    IdInUse = 2,
    StartFailed = 3,
    BadRequest = 4,
}

/// <summary>
/// The wire format: one byte saying what the message is, four bytes (little endian) saying how long its
/// content is, then the content. Input and Output carry raw terminal bytes. Everything else is a few
/// numbers and strings written with <see cref="BinaryWriter"/>.
/// </summary>
internal static class ShellProtocol
{
    public const int Version = 1;
    public const int HeaderSize = 5;
    public const int ChunkSize = 64 * 1024;

    // Room for a whole replay and the few fields in front of it.
    public const int MaxPayload = 1024 * 1024;

    // Header and content leave in one write, so a write that is given up on cannot leave half a header behind
    // with someone else's message following it.
    public static async ValueTask WriteAsync(Stream stream, ShellFrame type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] frame = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length);
        try
        {
            frame[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
            payload.CopyTo(frame.AsMemory(HeaderSize));
            await stream.WriteAsync(frame.AsMemory(0, HeaderSize + payload.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    public static byte[] Build(Action<BinaryWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }

    public static BinaryReader Read(ReadOnlyMemory<byte> payload) => new(new MemoryStream(payload.ToArray(), writable: false), Encoding.UTF8);
}

/// <summary>Takes messages off a connection one at a time. The content of one is only good until the next is read.</summary>
internal sealed class ShellFrameReader(Stream stream)
{
    private readonly byte[] _header = new byte[ShellProtocol.HeaderSize];
    private byte[] _payload = new byte[ShellProtocol.ChunkSize];

    public ReadOnlyMemory<byte> Payload { get; private set; }

    /// <returns>Null once the other side has closed the connection.</returns>
    /// <exception cref="InvalidDataException">What arrived is not this protocol.</exception>
    public async ValueTask<ShellFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(1));
            if (length is < 0 or > ShellProtocol.MaxPayload) throw new InvalidDataException();

            // A replay needs a large buffer once. Ordinary output afterwards should not keep it alive.
            if (_payload.Length < length) _payload = new byte[length];
            else if (_payload.Length > ShellProtocol.ChunkSize * 2 && length <= ShellProtocol.ChunkSize) _payload = new byte[ShellProtocol.ChunkSize];

            await stream.ReadExactlyAsync(_payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            Payload = _payload.AsMemory(0, length);
            return (ShellFrame)_header[0];
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}

/// <summary>Where the shell keeper is reached: a named pipe on Windows, a socket file in the data folder elsewhere.</summary>
internal static partial class ShellHostEndpoint
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // The longest path a socket may have is about a hundred bytes, and it differs per system.
    private const int LongestSocketPath = 100;

    /// <summary>Held open by the keeper for as long as it runs, which is what keeps it to one per data folder.</summary>
    public static string LockPath => Path.Combine(ElioPaths.DataDirectory, "shell-host.lock");

    // Derived from the data folder like the single instance pipe, so a portable copy has its own keeper.
    public static string PipeName()
    {
        string identity = Environment.UserName + "|" + Path.GetFullPath(ElioPaths.DataDirectory).ToLowerInvariant();
        return "elio-shells-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    /// <exception cref="IOException">The data folder is too deep for a socket and there is no safe place to put one instead.</exception>
    /// <exception cref="UnauthorizedAccessException">The same.</exception>
    [UnsupportedOSPlatform("windows")]
    public static string SocketPath()
    {
        string path = Path.Combine(Path.GetFullPath(ElioPaths.DataDirectory), "shell-host.sock");
        if (Encoding.UTF8.GetByteCount(path) <= LongestSocketPath) return path;

        // Too deep for a socket. The name still comes from the data folder, so the two sides agree on it.
        return Path.Combine(PrivateDirectory(), PipeName() + ".sock");
    }

    // The name of the socket can be worked out by anyone, and /tmp is open to every user of the computer. One of
    // them could put a socket of their own there first and be sent everything typed into a shell. So the socket
    // goes into a folder nobody else can enter or change.
    [UnsupportedOSPlatform("windows")]
    private static string PrivateDirectory()
    {
        // The system makes this one for each user, and for that user alone.
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)) return runtime;

        string directory = "/tmp/elio-" + geteuid().ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(directory, OwnerOnly);

        // Something of that name may have been there already, made by someone else. A link could lead anywhere.
        // Setting the permissions only works on a folder that is this user's own, which settles whose it is,
        // and it is looked at again afterwards in case it was swapped in between.
        var found = new DirectoryInfo(directory);
        if (found.LinkTarget is not null) throw new IOException($"'{directory}' is a link.");
        File.SetUnixFileMode(directory, OwnerOnly);
        found.Refresh();
        if (found.LinkTarget is not null || !found.Exists || found.UnixFileMode != OwnerOnly) throw new IOException($"'{directory}' is not private.");

        return directory;
    }

    [LibraryImport("libc")]
    private static partial uint geteuid();

    /// <returns>Null if nothing answers there.</returns>
    public static async Task<Stream?> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath()), limit.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // No safe place for the socket counts as no keeper: better none than one that might be someone else's.
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The keeper's side of the endpoint. Only the user it runs as can connect.</summary>
internal sealed partial class ShellHostListener : IDisposable
{
    private const int PipeBuffer = 64 * 1024;

    private readonly string? _pipeName;
    private readonly Socket? _socket;
    private readonly string? _socketPath;
    private NamedPipeServerStream? _waiting;

    public ShellHostListener()
    {
        if (OperatingSystem.IsWindows())
        {
            _pipeName = ShellHostEndpoint.PipeName();
            _waiting = CreatePipe();
            return;
        }

        // The lock is ours by now, so a file found here was left by a keeper that is gone.
        _socketPath = ShellHostEndpoint.SocketPath();
        File.Delete(_socketPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        // The file must never exist with wider permissions, not even for a moment, so they are narrowed
        // before it is made rather than after.
        uint before = umask(0x7F);
        try
        {
            _socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        }
        finally
        {
            umask(before);
        }

        File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _socket.Listen(16);
    }

    private NamedPipeServerStream CreatePipe() =>
        new(_pipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBuffer, PipeBuffer);

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null) return new NetworkStream(await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false), ownsSocket: true);

        NamedPipeServerStream pipe = _waiting ?? CreatePipe();
        _waiting = null;
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // The next one is made before this one is served, so a window that connects right now finds a pipe.
        try
        {
            _waiting = CreatePipe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again on the next round.
        }

        return pipe;
    }

    public void Dispose()
    {
        _waiting?.Dispose();
        _socket?.Dispose();
        if (_socketPath is null) return;

        try
        {
            File.Delete(_socketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next keeper removes it.
        }
    }

    [LibraryImport("libc")]
    private static partial uint umask(uint mask);
}

/// <summary>
/// The keeper: a copy of Elio without a window that owns the local shells, so they go on running while
/// windows are closed, restarted or replaced by an update. Windows reach it through
/// <see cref="HostedShellTransport"/>. It leaves by itself once it has had nothing to keep for a while.
/// </summary>
public sealed class ShellHost
{
    /// <summary>The command line argument that makes Elio run as the keeper.</summary>
    public const string Argument = "--shell-host";

    /// <summary>Overrides how many seconds the keeper stays around with nothing to keep. For tests.</summary>
    public const string IdleVariable = "ELIO_SHELL_HOST_IDLE_SECONDS";

    /// <summary>The exit code when another keeper already serves this data folder.</summary>
    public const int AlreadyRunning = 2;

    private const int RingSize = 512 * 1024;
    private static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeadShellKept = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Shell> _shells = new(StringComparer.Ordinal);
    private readonly TimeSpan _idle;
    private int _connections;
    private bool _stopping;

    private ShellHost(TimeSpan idle) => _idle = idle;

    /// <summary>Runs the keeper until it has nothing left to keep. Returns the process exit code.</summary>
    public static int Run()
    {
        TimeSpan idle = DefaultIdle;
        if (double.TryParse(Environment.GetEnvironmentVariable(IdleVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
            idle = TimeSpan.FromSeconds(seconds);

        // Staying in the folder Elio happened to be started from would keep that folder from being removed.
        try
        {
            Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return new ShellHost(idle).RunAsync().GetAwaiter().GetResult();
    }

    private async Task<int> RunAsync()
    {
        FileStream held;
        ShellHostListener listener;
        try
        {
            Directory.CreateDirectory(ElioPaths.DataDirectory);
            held = new FileStream(ShellHostEndpoint.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AlreadyRunning;
        }

        await using (held.ConfigureAwait(false))
        {
            try
            {
                listener = new ShellHostListener();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException)
            {
                return AlreadyRunning;
            }

            using var stop = new CancellationTokenSource();
            Task accepting = AcceptAsync(listener, stop.Token);
            await WaitUntilIdleAsync().ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            listener.Dispose();
            await accepting.ConfigureAwait(false);
        }

        return 0;
    }

    private async Task WaitUntilIdleAsync()
    {
        TimeSpan tick = TimeSpan.FromMilliseconds(Math.Clamp(_idle.TotalMilliseconds / 5, 50, 1000));
        long quietSince = Environment.TickCount64;

        while (true)
        {
            await Task.Delay(tick).ConfigureAwait(false);
            long now = Environment.TickCount64;

            lock (_gate)
            {
                // A shell that ended with nobody watching is kept for a last look, but not for ever.
                foreach (Shell dead in _shells.Values.Where(s => !s.Alive && now - s.EndedAt > DeadShellKept.TotalMilliseconds).ToList()) _shells.Remove(dead.Id);

                if (_connections > 0 || _shells.Values.Any(s => s.Alive))
                {
                    quietSince = now;
                }
                else if (now - quietSince >= _idle.TotalMilliseconds)
                {
                    // Decided under the lock a new connection is counted under, so none slips in after this.
                    _stopping = true;
                    return;
                }
            }
        }
    }

    private async Task AcceptAsync(ShellHostListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // One connection that went wrong while arriving. The shells matter more than it does.
                if (cancellationToken.IsCancellationRequested) return;
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            lock (_gate)
            {
                if (_stopping)
                {
                    stream.Dispose();
                    return;
                }

                _connections++;
            }

            _ = Task.Run(() => ServeAsync(stream), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        var connection = new Connection(stream);
        try
        {
            var reader = new ShellFrameReader(stream);

            // Something that connects and says nothing must not keep the keeper alive for ever.
            using (var limit = new CancellationTokenSource(HelloTimeout))
            {
                if (await reader.ReadAsync(limit.Token).ConfigureAwait(false) != ShellFrame.Hello) return;
            }

            await connection.SendAsync(ShellFrame.Welcome, ShellProtocol.Build(w =>
            {
                w.Write(ShellProtocol.Version);
                w.Write(Environment.ProcessId);
            })).ConfigureAwait(false);

            while (await reader.ReadAsync(connection.Dropped).ConfigureAwait(false) is ShellFrame frame)
            {
                switch (frame)
                {
                    case ShellFrame.Input:
                        if (connection.Shell is { Alive: true } typedInto)
                        {
                            try
                            {
                                await typedInto.Transport.WriteAsync(reader.Payload, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (IOException)
                            {
                                // The shell just ended. Its window hears about that from the output side.
                            }
                        }

                        break;

                    case ShellFrame.Resize:
                        if (connection.Shell is { } resized)
                        {
                            using BinaryReader size = ShellProtocol.Read(reader.Payload);
                            await ResizeAsync(resized, size.ReadInt32(), size.ReadInt32()).ConfigureAwait(false);
                        }

                        break;

                    case ShellFrame.Open:
                        await OpenAsync(connection, reader.Payload).ConfigureAwait(false);
                        break;

                    case ShellFrame.Attach:
                        await AttachAsync(connection, reader.Payload).ConfigureAwait(false);
                        break;

                    case ShellFrame.List:
                        await connection.SendAsync(ShellFrame.Shells, ListPayload()).ConfigureAwait(false);
                        break;

                    case ShellFrame.Close:
                        if (connection.Shell is { } closed)
                        {
                            await KillAsync(closed).ConfigureAwait(false);

                            // The window waits for word that the shell is gone, and that word is sent when the output ends.
                            await closed.Pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        }

                        return;

                    case ShellFrame.Detach:
                        return;

                    default:
                        await connection.SendAsync(ShellFrame.Error, ErrorPayload(ShellError.BadRequest, string.Empty)).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (Exception)
        {
            // Whatever a single connection did wrong, the keeper stays: every other shell depends on it.
        }
        finally
        {
            if (connection.Shell is { } shell)
            {
                lock (shell.Gate)
                {
                    if (shell.Client == connection) shell.Client = null;
                }
            }

            connection.Drop();
            lock (_gate) _connections--;
        }
    }

    private async Task OpenAsync(Connection connection, ReadOnlyMemory<byte> payload)
    {
        string id, commandLine;
        int columns, rows;
        string? directory;
        var environment = new Dictionary<string, string>();
        using (BinaryReader request = ShellProtocol.Read(payload))
        {
            id = request.ReadString();
            commandLine = request.ReadString();
            columns = request.ReadInt32();
            rows = request.ReadInt32();
            directory = request.ReadBoolean() ? request.ReadString() : null;
            for (int count = request.ReadInt32(); count > 0; count--) environment[request.ReadString()] = request.ReadString();
        }

        if (id.Length == 0 || connection.Shell is not null)
        {
            await connection.SendAsync(ShellFrame.Error, ErrorPayload(ShellError.BadRequest, string.Empty)).ConfigureAwait(false);
            return;
        }

        Shell? shell = null;
        string? failure = null;
        lock (_gate)
        {
            // Started under the lock, so two windows asking for the same id cannot both get a shell.
            if (!_shells.ContainsKey(id))
            {
                try
                {
                    var transport = LocalShellTransport.Start(commandLine, columns, rows, directory, environment);
                    shell = new Shell(id, commandLine, transport, columns, rows) { Client = connection };
                    _shells[id] = shell;
                }
                catch (IOException ex)
                {
                    failure = ex.Message;
                }
            }
        }

        if (shell is null)
        {
            await connection.SendAsync(ShellFrame.Error, ErrorPayload(failure is null ? ShellError.IdInUse : ShellError.StartFailed, failure ?? string.Empty)).ConfigureAwait(false);
            return;
        }

        connection.Shell = shell;
        try
        {
            await connection.SendAsync(ShellFrame.Opened, ShellProtocol.Build(w => w.Write(shell.ProcessId))).ConfigureAwait(false);
        }
        finally
        {
            // Only now, so the answer reaches the window before the first output does. Started even if the
            // window is already gone, because the shell must be read from either way.
            shell.Pump = Task.Run(() => PumpAsync(shell));
        }
    }

    private async Task AttachAsync(Connection connection, ReadOnlyMemory<byte> payload)
    {
        string id;
        int columns, rows;
        using (BinaryReader request = ShellProtocol.Read(payload))
        {
            id = request.ReadString();
            columns = request.ReadInt32();
            rows = request.ReadInt32();
        }

        Shell? shell;
        lock (_gate) _shells.TryGetValue(id, out shell);
        if (shell is null || connection.Shell is not null)
        {
            await connection.SendAsync(ShellFrame.Error, ErrorPayload(shell is null ? ShellError.Gone : ShellError.BadRequest, string.Empty)).ConfigureAwait(false);
            return;
        }

        // Held from before the shell knows this window until the replay has left, so output that comes in
        // meanwhile queues up behind the replay instead of overtaking it.
        await connection.SendGate.WaitAsync(connection.Dropped).ConfigureAwait(false);
        Connection? previous;
        bool alive;
        try
        {
            byte[] replay;
            int oldColumns, oldRows;
            lock (shell.Gate)
            {
                // Under the lock the output is recorded under: each piece of it is either in this replay
                // or sent to this window afterwards, never both and never neither.
                previous = shell.Client;
                shell.Client = connection;
                replay = shell.Output.Snapshot();
                (oldColumns, oldRows) = (shell.Columns, shell.Rows);
                alive = shell.Alive;
            }

            connection.Shell = shell;
            await ShellProtocol.WriteAsync(connection.Stream, ShellFrame.Attached, ShellProtocol.Build(w =>
            {
                w.Write(shell.ProcessId);
                w.Write(shell.CommandLine);
                w.Write(oldColumns);
                w.Write(oldRows);
                w.Write(replay.Length);
                w.Write(replay);
            }), connection.Dropped).ConfigureAwait(false);
        }
        finally
        {
            connection.SendGate.Release();
        }

        // The newest window wins. The one before it is told why it lost the shell.
        if (previous is not null && previous != connection) _ = Task.Run(() => DismissAsync(previous));

        if (alive)
        {
            await ResizeAsync(shell, columns, rows).ConfigureAwait(false);
            return;
        }

        // It ended while nobody was looking. This window has now seen how, so there is nothing left to keep.
        lock (_gate) _shells.Remove(shell.Id);
        await connection.SendAsync(ShellFrame.Exited, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
    }

    private static async Task DismissAsync(Connection previous)
    {
        try
        {
            // If output is stuck on its way to that window, the message could only land in the middle of
            // it, so the connection is just closed instead.
            if (await previous.SendGate.WaitAsync(250).ConfigureAwait(false))
            {
                try
                {
                    using var limit = new CancellationTokenSource(GoodbyeTimeout);
                    await ShellProtocol.WriteAsync(previous.Stream, ShellFrame.TakenOver, ReadOnlyMemory<byte>.Empty, limit.Token).ConfigureAwait(false);
                }
                finally
                {
                    previous.SendGate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        previous.Drop();
    }

    private static async Task ResizeAsync(Shell shell, int columns, int rows)
    {
        lock (shell.Gate)
        {
            if (!shell.Alive) return;
            (shell.Columns, shell.Rows) = (columns, rows);
        }

        await shell.Transport.ResizeAsync(columns, rows, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task KillAsync(Shell shell)
    {
        lock (_gate) _shells.Remove(shell.Id);
        await shell.Transport.DisposeAsync().ConfigureAwait(false);

        // A read that starts after the shell was disposed of would never come back by itself.
        await shell.Killed.CancelAsync().ConfigureAwait(false);
    }

    // Reads the shell for as long as it lives, whether a window is attached or not, so the shell never
    // stops because nobody is looking. With a window attached, the next read waits until that window has
    // taken the last one: the connection holds a bounded amount, and past that the shell itself is slowed
    // down, the way a terminal that cannot keep up slows down the program writing to it.
    private async Task PumpAsync(Shell shell)
    {
        byte[] buffer = new byte[ShellProtocol.ChunkSize];
        try
        {
            while (true)
            {
                int count = await shell.Transport.ReadAsync(buffer, shell.Killed.Token).ConfigureAwait(false);
                if (count == 0) break;

                Connection? client;
                lock (shell.Gate)
                {
                    shell.Output.Write(buffer.AsSpan(0, count));
                    client = shell.Client;
                }

                if (client is not null) await SendOrLetGoAsync(shell, client, ShellFrame.Output, buffer.AsMemory(0, count)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Connection? last;
        lock (_gate)
        {
            lock (shell.Gate)
            {
                shell.Alive = false;
                shell.EndedAt = Environment.TickCount64;
                last = shell.Client;
            }

            // With nobody attached it stays listed as ended, so the next window can still show its last output.
            if (last is not null) _shells.Remove(shell.Id);
        }

        await shell.Transport.DisposeAsync().ConfigureAwait(false);
        if (last is not null) await SendOrLetGoAsync(shell, last, ShellFrame.Exited, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
    }

    private static async Task SendOrLetGoAsync(Shell shell, Connection client, ShellFrame type, ReadOnlyMemory<byte> payload)
    {
        try
        {
            await client.SendAsync(type, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // That window is gone or was replaced. What it missed is in the buffer for the next one.
            lock (shell.Gate)
            {
                if (shell.Client == client) shell.Client = null;
            }

            client.Drop();
        }
    }

    private byte[] ListPayload()
    {
        lock (_gate)
        {
            return ShellProtocol.Build(w =>
            {
                w.Write(_shells.Count);
                foreach (Shell shell in _shells.Values)
                {
                    w.Write(shell.Id);
                    w.Write(shell.CommandLine);
                    w.Write(shell.Alive);
                    w.Write(shell.ProcessId);
                    w.Write(shell.StartedUtc.Ticks);
                }
            });
        }
    }

    private static byte[] ErrorPayload(ShellError error, string message) => ShellProtocol.Build(w =>
    {
        w.Write((byte)error);
        w.Write(message);
    });

    private sealed class Shell(string id, string commandLine, LocalShellTransport transport, int columns, int rows)
    {
        public readonly Lock Gate = new();
        public readonly CancellationTokenSource Killed = new();
        public readonly OutputRing Output = new(RingSize);

        public string Id { get; } = id;
        public string CommandLine { get; } = commandLine;
        public LocalShellTransport Transport { get; } = transport;
        public int ProcessId { get; } = transport.ProcessId;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public Task Pump { get; set; } = Task.CompletedTask;

        // These are guarded by Gate.
        public int Columns = columns;
        public int Rows = rows;
        public Connection? Client;
        public bool Alive = true;
        public long EndedAt;
    }

    private sealed class Connection(Stream stream)
    {
        private readonly CancellationTokenSource _dropped = new();
        private int _closed;

        public Stream Stream { get; } = stream;

        /// <summary>One message at a time: the shell's output and the answers to requests share the connection.</summary>
        public SemaphoreSlim SendGate { get; } = new(1, 1);

        /// <summary>Cancelled when the connection ends, which frees anything stuck sending to a window that stopped reading.</summary>
        public CancellationToken Dropped => _dropped.Token;

        public Shell? Shell { get; set; }

        public async ValueTask SendAsync(ShellFrame type, ReadOnlyMemory<byte> payload)
        {
            await SendGate.WaitAsync(Dropped).ConfigureAwait(false);
            try
            {
                await ShellProtocol.WriteAsync(Stream, type, payload, Dropped).ConfigureAwait(false);
            }
            finally
            {
                SendGate.Release();
            }
        }

        /// <summary>Ends the connection and whatever is waiting on it. Safe to call more than once.</summary>
        public void Drop()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _dropped.Cancel();
            Stream.Dispose();
        }
    }

    /// <summary>The most recent output of one shell, for showing a window that attaches later what was on screen.</summary>
    private sealed class OutputRing(int capacity)
    {
        // How far into the oldest output to look for a line start once older output has been dropped.
        private const int LineSearch = 8 * 1024;

        private readonly byte[] _bytes = new byte[capacity];
        private int _start;
        private int _count;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.Length >= _bytes.Length)
            {
                data[^_bytes.Length..].CopyTo(_bytes);
                (_start, _count) = (0, _bytes.Length);
                return;
            }

            int end = (_start + _count) % _bytes.Length;
            int first = Math.Min(data.Length, _bytes.Length - end);
            data[..first].CopyTo(_bytes.AsSpan(end));
            data[first..].CopyTo(_bytes);

            int overflow = _count + data.Length - _bytes.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _bytes.Length;
                _count = _bytes.Length;
            }
            else
            {
                _count += data.Length;
            }
        }

        public byte[] Snapshot()
        {
            byte[] copy = new byte[_count];
            int first = Math.Min(_count, _bytes.Length - _start);
            _bytes.AsSpan(_start, first).CopyTo(copy);
            _bytes.AsSpan(0, _count - first).CopyTo(copy.AsSpan(first));
            if (_count < _bytes.Length) return copy;

            // The oldest part was cut off somewhere, perhaps in the middle of a character or a colour code.
            // Starting at the next line keeps that half of something off the screen.
            int line = copy.AsSpan(0, LineSearch).IndexOf((byte)'\n');
            return line < 0 ? copy : copy[(line + 1)..];
        }
    }
}
