using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>
/// A terminal on a server over Mosh: encrypted UDP datagrams that keep working when the network
/// drops out, the address changes or the computer sleeps.
/// </summary>
/// <remarks>
/// <para>
/// Mosh does not carry a byte stream. Each side keeps a numbered state (here: everything typed so far;
/// on the server: the screen) and keeps sending "from state A, this change gives state B" until the
/// other side says it has B. Lost datagrams are never resent as they were. The newest state is sent instead.
/// </para>
/// <para>
/// The real client keeps a copy of the screen for every state it has received. This class has no screen:
/// it hands the server's screen changes to the terminal as they come. So it can only take a change that
/// starts from the screen the terminal is showing now, and turns down the rest until the server catches up.
/// </para>
/// </remarks>
public sealed partial class MoshTransport : ITerminalTransport
{
    /// <summary>
    /// The command to run over SSH to start a server. It prints a line that <see cref="ParseServerReply"/> reads.
    /// </summary>
    public const string StartCommand = "mosh-server new -s -c 256 -l LANG=en_US.UTF-8";

    // The timing below is the real client's, so the server sees the behaviour it was written for.
    private const int SendIntervalMin = 20;
    private const int SendIntervalMax = 250;
    private const int AckInterval = 3000;
    private const int PeerAckDelay = 100;
    private const int SendMinDelay = 1;
    private const int ActiveRetryTimeout = 10_000;
    private const int ShutdownRetries = 16;
    private const int MinRto = 50;
    private const int MaxRto = 1000;
    private const int PortHopInterval = 10_000;
    private const int MostSentStates = 32;

    // The real client waits 100 ms before confirming, hoping to send a key along with it. Confirming at once
    // costs a small datagram and keeps the server's idea of this screen fresh, which matters more here.
    private const int OwnAckDelay = 0;

    private const int ConnectTimeout = 15_000;
    private const int LostAfter = 60_000;
    private const int RepaintRetry = 3000;
    private const int DisposeWait = 1000;
    private const int MostQueuedOutput = 4 * 1024 * 1024;
    private const long Never = long.MaxValue;

    private const ulong DirectionToClient = 1UL << 63;

    private readonly Lock _gate = new();
    private readonly MoshCipher _cipher;
    private readonly IPEndPoint _remote;
    private readonly int _pieceSize;
    private readonly long _started = Environment.TickCount64;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _outputReady = new(0);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The datagram layer.
    private Socket _socket;
    private ulong _nextSequence;
    private ulong _expectedSequence;
    private int _savedTimestamp = -1;
    private long _savedTimestampAt;
    private long _lastHeard;
    private bool _everHeard;
    private long _lastPortChoice;
    private long _lastRoundTrip;
    private bool _rttMeasured;
    private double _srtt = 1000;
    private double _rttVar = 500;

    // What was typed, and which of it the server may have.
    private readonly MoshUserStream _user = new();
    private readonly List<SentState> _sent = [];
    private long _nextAckTime;
    private long _nextSendTime;
    private long _minDelayClock = -1;
    private long _senderLastHeard;
    private bool _pendingAck;
    private bool _closing;
    private long _closingSince;
    private int _closingTries;

    // The server's screen, as far as it has been handed to the terminal.
    private readonly MoshFragments _fragments = new();
    private readonly SortedSet<ulong> _sameScreen = [0];
    private readonly Queue<byte[]> _output = new();
    private ulong _remoteNum;
    private int _headOffset;
    private int _queuedOutput;
    private ulong _stuckProbe;
    private long _nextRepaintTime;
    private int _unusableGoodbyes;
    private bool _repaintPending;
    private int _repaints;
    private int _columns;
    private int _rows;

    private bool _over;
    private Exception? _failure;

    private MoshTransport(IPEndPoint remote, string base64Key, int columns, int rows)
    {
        _cipher = new MoshCipher(base64Key);
        _remote = remote;
        (_columns, _rows) = (columns, rows);

        // Datagrams are kept well under what any network carries whole. The numbers are the real client's.
        int datagram = remote.AddressFamily == AddressFamily.InterNetworkV6 ? 1280 - 64 : 1280 - 28;
        _pieceSize = datagram - MoshCipher.NonceLength - 4 - MoshCipher.TagLength;

        long now = Now;
        _lastHeard = now;
        _lastPortChoice = now;
        _nextAckTime = now;
        _nextSendTime = now;
        _sent.Add(new SentState(now, 0, 0));
        _socket = OpenSocket();

        // The server starts at 80 by 24 and has to be told the real size.
        _user.AddResize(columns, rows);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A Mosh session is not over when the network goes away. This only says that nothing has been
    /// heard for a while. It goes back to false by itself when the server is reachable again.
    /// </remarks>
    public bool ConnectionLost
    {
        get
        {
            lock (_gate) return _failure is not null || (!_over && Now - _lastHeard > LostAfter);
        }
    }

    /// <inheritdoc />
    public string Description => "Mosh · UDP " + _remote.Port;

    /// <summary>How long ago the server was last heard. Short while all is well, since it speaks every three seconds.</summary>
    public TimeSpan Silence
    {
        get
        {
            lock (_gate) return TimeSpan.FromMilliseconds(Now - _lastHeard);
        }
    }

    /// <summary>How often a full redraw had to be asked for because the server lost track of this screen.</summary>
    internal int Repaints
    {
        get
        {
            lock (_gate) return _repaints;
        }
    }

    // Starts well above zero so that a zero in a field safely means "long ago".
    private long Now => Environment.TickCount64 - _started + 1_000_000;

    /// <summary>Finds the "MOSH CONNECT port key" line in what <see cref="StartCommand"/> printed.</summary>
    /// <returns>Null when the server did not start, in which case the output says why.</returns>
    public static (int Port, string Key)? ParseServerReply(string output)
    {
        Match match = ServerReply().Match(output);
        if (!match.Success) return null;

        return int.TryParse(match.Groups[1].Value, out int port) && port is > 0 and <= 65535
            ? (port, match.Groups[2].Value)
            : null;
    }

    [GeneratedRegex(@"(?:^|[\r\n])\s*MOSH CONNECT (\d{1,5}) ([A-Za-z0-9+/]{22})\s*(?:$|[\r\n])")]
    private static partial Regex ServerReply();

    /// <summary>Starts talking to a server that <see cref="StartCommand"/> has just started.</summary>
    /// <remarks>
    /// Nothing has been proven when this returns: UDP has no connection to open. If the server cannot be
    /// reached at all, <see cref="ReadAsync"/> fails after a few seconds.
    /// </remarks>
    public static MoshTransport Connect(string host, int udpPort, string base64Key, int columns, int rows)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
        {
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException ex)
            {
                throw new IOException(L.T("The address of {0} could not be found: {1}", host, ex.Message), ex);
            }

            // IPv4 first: it is what the SSH connection that started the server most likely used.
            address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault()
                ?? throw new IOException(L.T("The address of {0} could not be found: {1}", host, "-"));
        }

        var transport = new MoshTransport(new IPEndPoint(address, udpPort), base64Key, columns, rows);
        _ = transport.ReceiveAsync(transport._socket);
        _ = transport.RunTimersAsync();
        return transport;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;

        while (true)
        {
            lock (_gate)
            {
                int copied = 0;
                while (copied < buffer.Length && _output.TryPeek(out byte[]? head))
                {
                    int count = Math.Min(buffer.Length - copied, head.Length - _headOffset);
                    head.AsSpan(_headOffset, count).CopyTo(buffer.Span[copied..]);
                    copied += count;
                    _headOffset += count;
                    if (_headOffset == head.Length)
                    {
                        _output.Dequeue();
                        _headOffset = 0;
                    }
                }

                if (copied > 0)
                {
                    _queuedOutput -= copied;
                    return copied;
                }

                if (_failure is not null) throw new IOException(_failure.Message, _failure);
                if (_over) return 0;
            }

            await _outputReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_over && !_closing) _user.AddKeys(data.Span);
        }

        Wake();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            (_columns, _rows) = (columns, rows);
            if (!_over && !_closing) _user.AddResize(columns, rows);
        }

        Wake();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        bool sayGoodbye;
        lock (_gate)
        {
            // Without a goodbye the server and the shell under it would stay around for good.
            // If the server was never reached there is nobody to tell.
            sayGoodbye = !_over && _everHeard && !_closing;
            if (sayGoodbye)
            {
                _closing = true;
                _closingSince = Now;
            }
        }

        if (sayGoodbye)
        {
            Wake();
            await Task.WhenAny(_finished.Task, Task.Delay(DisposeWait)).ConfigureAwait(false);
        }

        Finish(null);
    }

    private void Wake()
    {
        // One pending wake-up is enough. More would only make the timer loop spin.
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Ends the session, cleanly or with the reason it broke. Only the first call counts.</summary>
    private void Finish(Exception? failure)
    {
        Socket socket;
        lock (_gate)
        {
            if (_over) return;

            _over = true;
            _failure = failure;
            socket = _socket;
        }

        _stop.Cancel();
        socket.Dispose();
        _outputReady.Release();
        _finished.TrySetResult();
    }

    private Socket OpenSocket()
    {
        var socket = new Socket(_remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Windows otherwise fails the next receive whenever a datagram bounced ("port unreachable"),
                // which happens during any outage and says nothing a Mosh session cares about.
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
                // The receive loop copes with those failures anyway.
            }
        }

        socket.Bind(new IPEndPoint(_remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        return socket;
    }

    private async Task ReceiveAsync(Socket socket)
    {
        // Larger than anything valid, so an oversized datagram is seen as such rather than cut short.
        byte[] buffer = new byte[MoshCipher.ReceiveLimit * 2];
        EndPoint anyone = new IPEndPoint(_remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!_stop.IsCancellationRequested)
        {
            int count;
            try
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyone, _stop.Token).ConfigureAwait(false);
                count = received.ReceivedBytes;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // The network is down or changing. That is exactly what a Mosh session sits out.
                lock (_gate)
                {
                    if (!ReferenceEquals(socket, _socket)) return;
                }

                try
                {
                    await Task.Delay(200, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Exception? failure = null;
            bool goodbye = false;
            lock (_gate)
            {
                if (_over) return;

                try
                {
                    goodbye = Receive(buffer.AsSpan(0, count), Now);
                }
                catch (InvalidDataException)
                {
                    // It carried the right key but made no sense. Dropped like any lost datagram.
                }
                catch (NotSupportedException ex)
                {
                    failure = ex;
                }
            }

            if (failure is not null || goodbye) Finish(failure);
            else Wake();
        }
    }

    private async Task RunTimersAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int wait;
                Exception? failure = null;
                bool done = false;
                lock (_gate)
                {
                    if (_over) return;

                    long now = Now;
                    if (!_everHeard && now - _lastHeard > ConnectTimeout)
                    {
                        failure = new TimeoutException(L.T(
                            "Nothing came back from {0} on UDP port {1}. A firewall may be blocking Mosh.", _remote.Address, _remote.Port));
                    }
                    else if (_closing && (_sent[0].Num == MoshInstruction.ShutdownNum
                        || _closingTries >= ShutdownRetries
                        || now - _closingSince >= ActiveRetryTimeout))
                    {
                        // The server confirmed the goodbye, or is not going to.
                        done = true;
                    }

                    Tick(now);
                    wait = WaitTime(Now);
                }

                if (failure is not null || done)
                {
                    Finish(failure);
                    return;
                }

                await _wake.WaitAsync(wait, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- Datagrams ----

    private void SendDatagram(byte[] piece, long now)
    {
        // Each datagram carries the sender's clock, and gives back the last clock value received,
        // moved on by how long it was held. That is how both sides measure the round trip.
        ushort reply = ushort.MaxValue;
        if (_savedTimestamp >= 0 && now - _savedTimestampAt < 1000)
        {
            reply = (ushort)(_savedTimestamp + (now - _savedTimestampAt));
            _savedTimestamp = -1;
            _savedTimestampAt = 0;
        }

        byte[] plain = new byte[4 + piece.Length];
        BinaryPrimitives.WriteUInt16BigEndian(plain, Timestamp16(now));
        BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2), reply);
        piece.CopyTo(plain.AsSpan(4));

        // The top bit stays clear: this travels towards the server.
        byte[] datagram = _cipher.Seal(_nextSequence++ & ~DirectionToClient, plain);
        try
        {
            _socket.SendTo(datagram, _remote);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // No route right now. The state is sent again later, so nothing is lost.
        }

        // A network that changed can leave the old socket tied to an address that no longer works.
        // After ten seconds without a confirmed round trip a new socket is tried. The server follows
        // whatever address the newest valid datagram came from.
        if (now - _lastPortChoice > PortHopInterval && now - _lastRoundTrip > PortHopInterval)
        {
            try
            {
                Socket fresh = OpenSocket();
                _socket.Dispose();
                _socket = fresh;
                _ = ReceiveAsync(fresh);
            }
            catch (SocketException)
            {
                // Tried again in ten seconds.
            }

            _lastPortChoice = now;
        }
    }

    private static ushort Timestamp16(long now)
    {
        ushort value = (ushort)now;

        // All ones means "no timestamp".
        return value == ushort.MaxValue ? (ushort)0 : value;
    }

    /// <returns>True when the server said the session is over.</returns>
    private bool Receive(ReadOnlySpan<byte> datagram, long now)
    {
        if (!_cipher.TryOpen(datagram, out ulong nonce, out byte[] plain)) return false;

        // A datagram of our own played back at us would pass the key check. The direction bit catches it.
        if ((nonce & DirectionToClient) == 0 || plain.Length < 4) return false;

        // Timing is only taken from datagrams that arrive in order, so an old one played back cannot upset it.
        ulong sequence = nonce & ~DirectionToClient;
        if (sequence >= _expectedSequence)
        {
            _expectedSequence = sequence + 1;

            ushort timestamp = BinaryPrimitives.ReadUInt16BigEndian(plain);
            ushort reply = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2));
            if (timestamp != ushort.MaxValue)
            {
                _savedTimestamp = timestamp;
                _savedTimestampAt = now;
            }

            if (reply != ushort.MaxValue)
            {
                double sample = (ushort)(Timestamp16(now) - reply);

                // Larger values mean the server was suspended, not that the network is that slow.
                if (sample < 5000)
                {
                    if (!_rttMeasured)
                    {
                        _srtt = sample;
                        _rttVar = sample / 2;
                        _rttMeasured = true;
                    }
                    else
                    {
                        _rttVar = (0.75 * _rttVar) + (0.25 * Math.Abs(_srtt - sample));
                        _srtt = (0.875 * _srtt) + (0.125 * sample);
                    }
                }
            }

            _lastHeard = now;
            _everHeard = true;
        }

        MoshInstruction? instruction = _fragments.Add(plain.AsSpan(4));
        if (instruction is null) return false;

        if (instruction.ProtocolVersion != MoshInstruction.Version)
        {
            throw new NotSupportedException(L.T("The server speaks a different version of Mosh."));
        }

        Acknowledged(instruction.AckNum);
        _lastRoundTrip = _sent[0].Time;

        return _closing ? false : Apply(instruction, now);
    }

    /// <summary>Takes a screen change from the server if it fits the screen the terminal has.</summary>
    /// <returns>True when the server said the session is over.</returns>
    private bool Apply(MoshInstruction instruction, long now)
    {
        if (instruction.NewNum == _remoteNum) return false;

        bool goodbye = instruction.NewNum == MoshInstruction.ShutdownNum;
        MoshHostMessage message = MoshHostMessage.Decode(instruction.Diff);

        // A change can be used when it starts from the screen the terminal shows, or when it redraws everything.
        bool usable = _sameScreen.Contains(instruction.OldNum)
            || (message.Resized && instruction.NewNum > _remoteNum);

        if (usable && !goodbye && _queuedOutput > MostQueuedOutput)
        {
            // The terminal is behind. Not confirming makes the server send one bigger change later
            // instead of many small ones now, which is how Mosh skips output nobody could read anyway.
            return false;
        }

        if (!usable)
        {
            if (goodbye && ++_unusableGoodbyes >= 3)
            {
                // The last screen change cannot be had, but the session is over all the same.
                ConfirmGoodbye(now);
                return true;
            }

            // The server is working from a screen it thinks we have: one whose confirmation is still on
            // its way, or one it sent that never arrived. Confirming again at once sets it right.
            _nextAckTime = now;
            if (_stuckProbe == 0)
            {
                // Everything sent from here on confirms the screen we have and gets this number or a higher one.
                _stuckProbe = _sent[^1].Num + 1;
            }
            else if (instruction.OldNum < _remoteNum
                && instruction.AckNum >= _stuckProbe
                && instruction.AckNum != MoshInstruction.ShutdownNum
                && now >= _nextRepaintTime)
            {
                // The server has had our confirmation and still starts from an older screen. So it no longer
                // knows the one we confirmed, and waiting will not help.
                AskForRepaint(now);
            }

            return false;
        }

        foreach (byte[] bytes in message.Output)
        {
            _output.Enqueue(bytes);
            _queuedOutput += bytes.Length;
        }

        if (message.Output.Count > 0)
        {
            _sameScreen.Clear();
            _outputReady.Release();
        }

        // States that changed nothing visible are as good a starting point as the one they came from.
        _sameScreen.Add(instruction.NewNum);
        _sameScreen.RemoveWhere(num => num < instruction.ThrowawayNum && num != instruction.NewNum);
        while (_sameScreen.Count > 256) _sameScreen.Remove(_sameScreen.Min);

        _remoteNum = instruction.NewNum;
        _senderLastHeard = now;
        _stuckProbe = 0;
        if (instruction.Diff.Length > 0) _pendingAck = true;

        if (message.Resized && _repaintPending)
        {
            // The redraw that was asked for has arrived. Now the window can have its real size back.
            _repaintPending = false;
            _user.AddResize(_columns, _rows);
        }

        if (goodbye)
        {
            ConfirmGoodbye(now);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the session going again when the server has forgotten the screen the terminal shows.
    /// </summary>
    /// <remarks>
    /// The server keeps only its last few states. After a long outage it can have dropped the one
    /// confirmed last, and then every change it offers starts from a screen that cannot be rebuilt here.
    /// A change between screens of different sizes is always a full redraw, so the window size is nudged
    /// by a row to get one, and set back when it has arrived.
    /// </remarks>
    private void AskForRepaint(long now)
    {
        _repaints++;
        _repaintPending = true;
        _nextRepaintTime = now + RepaintRetry;
        _user.AddResize(_columns, _rows + 1 + (_repaints % 2));
    }

    private void ConfirmGoodbye(long now)
    {
        // The server waits for this before it exits. Sent a few times because there is no second chance.
        for (int i = 0; i < 3; i++)
        {
            SendInstruction([], _sent[^1].Num + 1, _sent[^1].Num, MoshInstruction.ShutdownNum, now);
        }
    }

    // ---- Sending what was typed ----

    private readonly record struct SentState(long Time, ulong Num, long Events);

    private int SendInterval => (int)Math.Clamp(Math.Ceiling(_srtt / 2), SendIntervalMin, SendIntervalMax);

    private int RetransmitTimeout => (int)Math.Clamp(Math.Ceiling(_srtt + (4 * _rttVar)), MinRto, MaxRto);

    /// <summary>Works out when to send next. Returns the newest sent state the server can be assumed to have.</summary>
    private int UpdateTimers(long now)
    {
        // States sent a moment ago are given the benefit of the doubt, so the same keys are not sent twice
        // just because the confirmation is still on its way.
        int assumed = 0;
        for (int i = 1; i < _sent.Count; i++)
        {
            if (now - _sent[i].Time >= RetransmitTimeout + PeerAckDelay) break;
            assumed = i;
        }

        // Keys the server has confirmed are of no further use.
        _user.DropBefore(_sent[0].Events);

        if (_pendingAck && _nextAckTime > now + OwnAckDelay) _nextAckTime = now + OwnAckDelay;

        SentState last = _sent[^1];
        long current = _user.Count;
        bool heardLately = _senderLastHeard + ActiveRetryTimeout > now;
        if (current != last.Events)
        {
            if (_minDelayClock < 0) _minDelayClock = now;
            _nextSendTime = Math.Max(_minDelayClock + SendMinDelay, last.Time + SendInterval);
        }
        else if (current != _sent[assumed].Events && heardLately)
        {
            _nextSendTime = last.Time + SendInterval;
            if (_minDelayClock >= 0) _nextSendTime = Math.Max(_nextSendTime, _minDelayClock + SendMinDelay);
        }
        else if (current != _sent[0].Events && heardLately)
        {
            _nextSendTime = last.Time + RetransmitTimeout + PeerAckDelay;
        }
        else
        {
            _nextSendTime = Never;
        }

        // A goodbye is repeated quickly rather than every three seconds.
        if (_closing) _nextAckTime = last.Time + SendInterval;

        return assumed;
    }

    private int WaitTime(long now)
    {
        UpdateTimers(now);
        long next = Math.Min(_nextAckTime, _nextSendTime);

        // Never sleeps long, so the start-up timeout and a closing session are looked at in good time.
        return (int)Math.Clamp(next - now, 0, 1000);
    }

    /// <summary>Sends what the server is missing, or an empty "still here" message, when one is due.</summary>
    private void Tick(long now)
    {
        int assumed = UpdateTimers(now);
        if (now < _nextAckTime && now < _nextSendTime) return;

        byte[] diff = _user.DiffFrom(_sent[assumed].Events);
        if (assumed != 0)
        {
            // If sending everything since the last confirmed state costs little more, do that:
            // it then works even when the unconfirmed states never arrived.
            byte[] resend = _user.DiffFrom(_sent[0].Events);
            if (resend.Length <= diff.Length || (resend.Length < 1000 && resend.Length - diff.Length < 100))
            {
                assumed = 0;
                diff = resend;
            }
        }

        ulong oldNum = _sent[assumed].Num;
        SentState last = _sent[^1];
        if (diff.Length == 0)
        {
            if (now >= _nextAckTime)
            {
                ulong newNum = _closing ? MoshInstruction.ShutdownNum : last.Num + 1;
                AddSentState(new SentState(now, newNum, _user.Count));
                SendInstruction(diff, newNum, oldNum, _remoteNum, now);
                _nextAckTime = now + AckInterval;
            }

            _nextSendTime = Never;
        }
        else
        {
            ulong newNum = _user.Count == last.Events ? last.Num : last.Num + 1;
            if (_closing) newNum = MoshInstruction.ShutdownNum;

            if (newNum == last.Num) _sent[^1] = last with { Time = now };
            else AddSentState(new SentState(now, newNum, _user.Count));

            SendInstruction(diff, newNum, oldNum, _remoteNum, now);
            _nextAckTime = now + AckInterval;
            _nextSendTime = Never;
        }

        _minDelayClock = -1;
    }

    private void AddSentState(SentState state)
    {
        _sent.Add(state);

        // During a long outage the list would grow without end. One from the middle goes, never the
        // first (the server has it for sure) or the newest (the server may confirm those soon).
        if (_sent.Count > MostSentStates) _sent.RemoveAt(_sent.Count - 16);
    }

    private void SendInstruction(byte[] diff, ulong newNum, ulong oldNum, ulong ackNum, long now)
    {
        var instruction = new MoshInstruction
        {
            OldNum = oldNum,
            NewNum = newNum,
            AckNum = ackNum,
            ThrowawayNum = _sent[0].Num,
            Diff = diff,
            Chaff = RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(17)),
        };

        if (newNum == MoshInstruction.ShutdownNum) _closingTries++;

        foreach (byte[] piece in _fragments.Split(instruction, _pieceSize)) SendDatagram(piece, now);
        _pendingAck = false;
    }

    /// <summary>The server has state <paramref name="ackNum"/>: everything before it can be forgotten.</summary>
    private void Acknowledged(ulong ackNum)
    {
        // A confirmation for a state that was dropped from the list says nothing usable.
        int index = _sent.FindIndex(state => state.Num == ackNum);
        if (index > 0) _sent.RemoveAll(state => state.Num < ackNum);
    }
}
