using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Elio.SyncServer;

/// <summary>The first message on a host's connection: the room it was given.</summary>
internal sealed class ShareRoomCreated
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Passes a shared terminal along between the person sharing it and the people watching, so that neither
/// has to be reachable from the internet: both connect out to this server.
/// </summary>
/// <remarks>
/// Everything that passes is already encrypted between the two ends with a key made from a password this
/// server never sees, so there is nothing here to read. What is left to guard is the server itself: the
/// limits below keep it from being used as a free pipe for anything else.
/// The host's connection carries all of its viewers. Each binary message there is four bytes of viewer
/// number, big-endian, one byte of kind and then the payload. A viewer's connection carries only its own
/// bytes, as binary messages.
/// </remarks>
internal sealed class ShareRelay : IDisposable
{
    /// <summary>The most payload one message may carry. A connection that sends more is closed.</summary>
    public const int MaxMessage = 64 * 1024;

    public const int HeaderSize = 5;

    public const byte KindData = 0;

    /// <summary>Server to host: a viewer joined under this number.</summary>
    public const byte KindOpened = 1;

    /// <summary>Either way: this viewer is gone.</summary>
    public const byte KindClosed = 2;

    // 20 characters of 31 are 99 bits: far too many to find a room by trying. Lower case, and without
    // 0, o, 1, i and l, so that an id survives being read out or copied by hand.
    private const int IdLength = 20;
    private const string IdAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    // A host that takes this long to accept one message is not coming back. Until then its viewers wait.
    private static readonly TimeSpan HostSendTimeout = TimeSpan.FromSeconds(60);

    // How long a viewer that is being let go has to take what is still queued for it.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly SyncServerOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _perAccount = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Timer _sweep;
    private int _open;

    public ShareRelay(SyncServerOptions options, ILogger log)
    {
        _options = options;
        _log = log;

        // Often enough that an idle room outlives its time by little, whatever the time is set to.
        TimeSpan every = TimeSpan.FromTicks(Math.Clamp(options.ShareIdleTimeout.Ticks / 4, TimeSpan.FromMilliseconds(250).Ticks, TimeSpan.FromMinutes(1).Ticks));
        _sweep = new Timer(_ => Sweep(), null, every, every);
    }

    /// <summary>Serves one host for as long as it stays. The account has been checked by the caller.</summary>
    public async Task HostAsync(HttpContext context, string subject)
    {
        if (!Reserve(subject))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        Room? room = null;
        string id = string.Empty;
        try
        {
            WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            room = new Room(socket, _options);
            do
            {
                id = new string(RandomNumberGenerator.GetItems<char>(IdAlphabet, IdLength));
            }
            while (!_rooms.TryAdd(id, room));

            // The server may have begun to stop just before the room was listed, and so have missed it.
            if (_stopping.IsCancellationRequested) room.End();

            // The id is never logged: with it and the password, someone could watch.
            _log.LogDebug("A shared terminal was opened. {Rooms} are open.", _rooms.Count);

            byte[] created = JsonSerializer.SerializeToUtf8Bytes(new ShareRoomCreated { Id = id }, ServerJson.Default.ShareRoomCreated);
            await room.SendToHostAsync(created, WebSocketMessageType.Text, room.Ending.Token);
            await ReceiveFromHostAsync(room, socket);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The host went away, was too slow, or the room was ended from here.
        }
        finally
        {
            // Out of the list first, so that nobody joins a room that is being taken down.
            if (room is not null)
            {
                _rooms.TryRemove(new KeyValuePair<string, Room>(id, room));
                room.Close();
            }

            Release(subject);
            _log.LogDebug("A shared terminal was closed. {Rooms} are open.", _rooms.Count);
        }
    }

    private async Task ReceiveFromHostAsync(Room room, WebSocket socket)
    {
        var inbox = new Inbox(socket, 0, MaxMessage + HeaderSize);
        while (true)
        {
            var (type, count) = await inbox.NextAsync(room.Ending.Token);
            if (type == WebSocketMessageType.Close)
            {
                await room.CloseHostAsync(WebSocketCloseStatus.NormalClosure);
                return;
            }

            if (count < 0)
            {
                await room.CloseHostAsync(WebSocketCloseStatus.MessageTooBig);
                return;
            }

            if (type != WebSocketMessageType.Binary || count < HeaderSize)
            {
                await room.CloseHostAsync(WebSocketCloseStatus.ProtocolError);
                return;
            }

            room.Touch();
            uint number = BinaryPrimitives.ReadUInt32BigEndian(inbox.Buffer);
            byte kind = inbox.Buffer[4];

            // A viewer that has just left may still be sent something. That is nobody's fault.
            Viewer? viewer = room.Find(number);
            if (kind == KindClosed)
            {
                viewer?.EndByHost();
            }
            else if (kind == KindData)
            {
                if (viewer is not null && count > HeaderSize)
                {
                    viewer.Touch();

                    // Never waited for: a viewer that cannot keep up is dropped, so that it holds up nobody else.
                    if (!viewer.Offer(inbox.Buffer.AsSpan(HeaderSize, count - HeaderSize).ToArray(), _options.ShareViewerBacklog)) viewer.Drop();
                }

                await room.ThrottleAsync(count, room.Ending.Token);
            }
        }
    }

    /// <summary>Serves one viewer for as long as it stays. Answers 404 for a room that is not there.</summary>
    public async Task JoinAsync(HttpContext context, string id)
    {
        // One answer for every id that leads nowhere, whatever it looks like.
        if (!_rooms.TryGetValue(id, out Room? room) || room.TryReserve() is not bool free)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!free)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
        }
        catch
        {
            room.Unreserve();
            throw;
        }

        Viewer? viewer = room.Admit(socket);
        if (viewer is null)
        {
            // The room ended while this viewer was on its way in.
            socket.Abort();
            return;
        }

        Task sending = SendToViewerAsync(viewer);
        try
        {
            var inbox = new Inbox(socket, HeaderSize, MaxMessage);
            BinaryPrimitives.WriteUInt32BigEndian(inbox.Buffer, viewer.Number);
            inbox.Buffer[4] = KindOpened;
            await room.SendToHostAsync(inbox.Buffer.AsMemory(0, HeaderSize), WebSocketMessageType.Binary, viewer.Gone.Token);

            while (true)
            {
                var (type, count) = await inbox.NextAsync(viewer.Gone.Token);
                if (type == WebSocketMessageType.Close) break;

                if (count < 0)
                {
                    viewer.Finish(WebSocketCloseStatus.MessageTooBig);
                    break;
                }

                if (type != WebSocketMessageType.Binary)
                {
                    viewer.Finish(WebSocketCloseStatus.InvalidMessageType);
                    break;
                }

                if (count == 0) continue;

                viewer.Touch();
                room.Touch();

                // The message was read in behind room for the header, so it goes on as it is.
                BinaryPrimitives.WriteUInt32BigEndian(inbox.Buffer, viewer.Number);
                inbox.Buffer[4] = KindData;
                await room.SendToHostAsync(inbox.Buffer.AsMemory(0, HeaderSize + count), WebSocketMessageType.Binary, viewer.Gone.Token);

                // Against this viewer's own share, not the room's: one that floods slows down only itself.
                await viewer.Budget.SpendAsync(count, viewer.Gone.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The viewer went away, was dropped, or the host is gone.
        }
        finally
        {
            bool toldByHost = viewer.EndedByHost;
            room.Remove(viewer);
            viewer.Finish(WebSocketCloseStatus.NormalClosure);

            if (!toldByHost)
            {
                try
                {
                    byte[] closed = new byte[HeaderSize];
                    BinaryPrimitives.WriteUInt32BigEndian(closed, viewer.Number);
                    closed[4] = KindClosed;
                    await room.SendToHostAsync(closed, WebSocketMessageType.Binary, room.Ending.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The host is gone too, and has no use for the news.
                }
            }

            // The sending side says goodbye once it has delivered what was queued. It is given a moment
            // for that, and then the connection is cut whatever state it is in.
            try
            {
                await sending.WaitAsync(CloseTimeout);
            }
            catch (TimeoutException)
            {
            }

            viewer.Drop();
        }
    }

    // The only place a viewer's connection is written to, so that two writes never overlap.
    private static async Task SendToViewerAsync(Viewer viewer)
    {
        try
        {
            await foreach (byte[] data in viewer.Queue.Reader.ReadAllAsync(viewer.Gone.Token))
            {
                await viewer.Socket.SendAsync(data, WebSocketMessageType.Binary, true, viewer.Gone.Token);
                viewer.Sent(data.Length);
            }

            await viewer.Socket.CloseOutputAsync(viewer.CloseStatus, null, viewer.Gone.Token);

            // The viewer answers a goodbye by hanging up. One that does not is hung up on.
            viewer.Gone.CancelAfter(CloseTimeout);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            viewer.Drop();
        }
    }

    private bool Reserve(string subject)
    {
        lock (_gate)
        {
            _perAccount.TryGetValue(subject, out int mine);
            if (mine >= _options.ShareRoomsPerAccount || _open >= _options.ShareMaxRooms) return false;

            _perAccount[subject] = mine + 1;
            _open++;
            return true;
        }
    }

    private void Release(string subject)
    {
        lock (_gate)
        {
            _open--;
            if (_perAccount[subject] <= 1) _perAccount.Remove(subject);
            else _perAccount[subject]--;
        }
    }

    // Ends what nobody has used for a long time: a forgotten room, or a viewer left open in some tab.
    private void Sweep()
    {
        long idle = (long)_options.ShareIdleTimeout.TotalMilliseconds;
        long now = Environment.TickCount64;
        foreach (Room room in _rooms.Values)
        {
            if (now - room.LastTraffic > idle) room.End();
            else room.DropIdle(now, idle);
        }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        _stopping.Cancel();
        foreach (Room room in _rooms.Values) room.End();
    }

    /// <summary>Reads whole messages, however many pieces they arrive in, up to a size.</summary>
    /// <param name="reserve">Bytes left free in front of each message, for a header to be written into.</param>
    private sealed class Inbox(WebSocket socket, int reserve, int max)
    {
        // Small to begin with and grown for whoever sends more: most connections carry keystrokes, and
        // there may be very many of them.
        private byte[] _buffer = new byte[4096];

        public byte[] Buffer => _buffer;

        /// <returns>The kind of message and its length, which is -1 for one that is too long.</returns>
        public async ValueTask<(WebSocketMessageType Type, int Count)> NextAsync(CancellationToken cancellation)
        {
            int count = 0;
            while (true)
            {
                if (reserve + count == _buffer.Length) Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, reserve + max + 1));

                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(_buffer.AsMemory(reserve + count), cancellation);
                if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, 0);

                count += result.Count;
                if (count > max) return (result.MessageType, -1);
                if (result.EndOfMessage) return (result.MessageType, count);
            }
        }
    }

    /// <summary>
    /// So many bytes a second. Counts what passes and makes the caller wait when it is ahead of that; the caller
    /// reads nothing more meanwhile, which is what slows the sender down.
    /// </summary>
    private sealed class Budget(double rate)
    {
        private readonly Lock _gate = new();
        private readonly double _rate = rate;

        // A second's worth may pass at once, so that a screen being repainted is not slowed down.
        private double _tokens = rate;
        private long _refilled = Stopwatch.GetTimestamp();

        public Task SpendAsync(int bytes, CancellationToken cancellation)
        {
            double wait;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(_rate, _tokens + (Stopwatch.GetElapsedTime(_refilled, now).TotalSeconds * _rate));
                _refilled = now;
                _tokens -= bytes;
                wait = _tokens < 0 ? -_tokens / _rate : 0;
            }

            return wait > 0 ? Task.Delay(TimeSpan.FromSeconds(wait), cancellation) : Task.CompletedTask;
        }
    }

    private sealed class Viewer(uint number, WebSocket socket, double rate)
    {
        private long _queued;
        private int _closeStatus = (int)WebSocketCloseStatus.NormalClosure;

        public uint Number { get; } = number;

        /// <summary>What this viewer may send to the host.</summary>
        public Budget Budget { get; } = new(rate);

        public WebSocket Socket { get; } = socket;

        public Channel<byte[]> Queue { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Cancelled to cut the connection at once.</summary>
        public CancellationTokenSource Gone { get; } = new();

        /// <summary>The host said this viewer is done, or the room ended. The host need not be told.</summary>
        public volatile bool EndedByHost;

        public long LastTraffic = Environment.TickCount64;

        public WebSocketCloseStatus CloseStatus => (WebSocketCloseStatus)Volatile.Read(ref _closeStatus);

        public void Touch() => Volatile.Write(ref LastTraffic, Environment.TickCount64);

        /// <returns>False if this would put more than <paramref name="limit"/> bytes in the queue, and so was not added.</returns>
        public bool Offer(byte[] data, int limit)
        {
            if (Interlocked.Read(ref _queued) + data.Length > limit) return false;

            Interlocked.Add(ref _queued, data.Length);
            if (!Queue.Writer.TryWrite(data)) Interlocked.Add(ref _queued, -data.Length);
            return true;
        }

        public void Sent(int count) => Interlocked.Add(ref _queued, -count);

        /// <summary>Lets the viewer have what is queued, then says goodbye. The first reason given is the one sent.</summary>
        public void Finish(WebSocketCloseStatus status)
        {
            if (!Queue.Writer.TryComplete()) return;

            Volatile.Write(ref _closeStatus, (int)status);
            Gone.CancelAfter(DrainTimeout);
        }

        public void EndByHost()
        {
            EndedByHost = true;
            Finish(WebSocketCloseStatus.NormalClosure);
        }

        public void Drop()
        {
            Queue.Writer.TryComplete();
            Gone.Cancel();
            Socket.Abort();
        }
    }

    private sealed class Room
    {
        private readonly WebSocket _host;
        private readonly int _maxViewers;
        private readonly double _viewerRate;
        private readonly Budget _fromHost;
        private readonly Lock _gate = new();
        private readonly Dictionary<uint, Viewer> _viewers = [];
        private readonly SemaphoreSlim _toHost = new(1, 1);
        private uint _next;
        private int _reserved;
        private bool _closed;

        public Room(WebSocket host, SyncServerOptions options)
        {
            _host = host;
            _maxViewers = options.ShareViewersPerRoom;

            // The host has the whole of what the room may carry for what it sends. The same again is split evenly
            // between the places for viewers, so that a viewer who has not even given the password yet cannot use
            // up what the others, or the host, need.
            double rate = Math.Max(1, options.ShareBytesPerSecond);
            _fromHost = new Budget(rate);
            _viewerRate = Math.Max(1, rate / Math.Max(1, _maxViewers));
        }

        /// <summary>Cancelled to end the room. That cuts the host's connection.</summary>
        public CancellationTokenSource Ending { get; } = new();

        public long LastTraffic = Environment.TickCount64;

        public void Touch() => Volatile.Write(ref LastTraffic, Environment.TickCount64);

        public void End() => Ending.Cancel();

        /// <summary>
        /// Keeps a place for a viewer that is about to connect, so that the answer can still be a refusal.
        /// </summary>
        /// <returns>True if there is room, false if the room is full, null if the room is over.</returns>
        public bool? TryReserve()
        {
            lock (_gate)
            {
                if (_closed) return null;
                if (_viewers.Count + _reserved >= _maxViewers) return false;

                _reserved++;
                return true;
            }
        }

        public void Unreserve()
        {
            lock (_gate) _reserved--;
        }

        public Viewer? Admit(WebSocket socket)
        {
            lock (_gate)
            {
                _reserved--;
                if (_closed) return null;

                var viewer = new Viewer(++_next, socket, _viewerRate);
                _viewers[viewer.Number] = viewer;
                return viewer;
            }
        }

        public Viewer? Find(uint number)
        {
            lock (_gate) return _viewers.GetValueOrDefault(number);
        }

        public void Remove(Viewer viewer)
        {
            lock (_gate)
            {
                if (_viewers.GetValueOrDefault(viewer.Number) == viewer) _viewers.Remove(viewer.Number);
            }
        }

        public void DropIdle(long now, long idle)
        {
            List<Viewer>? silent = null;
            lock (_gate)
            {
                foreach (Viewer viewer in _viewers.Values)
                {
                    if (now - Volatile.Read(ref viewer.LastTraffic) > idle) (silent ??= []).Add(viewer);
                }
            }

            if (silent is null) return;
            foreach (Viewer viewer in silent) viewer.Drop();
        }

        /// <summary>Ends the room: every viewer is given what it is still owed, and then let go.</summary>
        public void Close()
        {
            List<Viewer> leaving;
            lock (_gate)
            {
                _closed = true;
                leaving = [.. _viewers.Values];
                _viewers.Clear();
            }

            foreach (Viewer viewer in leaving) viewer.EndByHost();

            Ending.Cancel();
            _host.Abort();
        }

        /// <summary>Sends one message to the host. Messages from different viewers take turns.</summary>
        /// <param name="waiting">
        /// Gives up the wait for a turn. It does not interrupt a message already on its way: stopping one
        /// half-sent would break the connection for everyone in the room.
        /// </param>
        public async Task SendToHostAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken waiting)
        {
            await _toHost.WaitAsync(waiting);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ending.Token);
                limit.CancelAfter(HostSendTimeout);
                await _host.SendAsync(message, type, true, limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Too slow, or over already. Either way the connection is of no more use.
                Ending.Cancel();
                throw;
            }
            finally
            {
                _toHost.Release();
            }
        }

        public async Task CloseHostAsync(WebSocketCloseStatus status)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ending.Token);
            limit.CancelAfter(CloseTimeout);
            await _toHost.WaitAsync(limit.Token);
            try
            {
                await _host.CloseOutputAsync(status, null, limit.Token);
            }
            finally
            {
                _toHost.Release();
            }
        }

        /// <summary>Counts bytes the host sent against what it may send, and waits when it is ahead of that.</summary>
        public Task ThrottleAsync(int bytes, CancellationToken cancellation) => _fromHost.SpendAsync(bytes, cancellation);
    }
}
