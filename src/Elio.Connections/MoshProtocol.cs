using System.Buffers.Binary;
using System.IO.Compression;

namespace Elio.Connections;

/// <summary>Just enough of the protocol buffers wire format for the three small Mosh messages.</summary>
internal static class MoshWire
{
    private const int Varint = 0;
    private const int Fixed64 = 1;
    private const int Bytes = 2;
    private const int Fixed32 = 5;

    public static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    public static void WriteVarintField(List<byte> output, int field, ulong value)
    {
        WriteVarint(output, ((ulong)field << 3) | Varint);
        WriteVarint(output, value);
    }

    public static void WriteBytesField(List<byte> output, int field, ReadOnlySpan<byte> value)
    {
        WriteVarint(output, ((ulong)field << 3) | Bytes);
        WriteVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    /// <summary>Walks the fields of a message one at a time.</summary>
    public ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _rest = data;

        public int Field { get; private set; }

        /// <summary>The value of a number field.</summary>
        public ulong Number { get; private set; }

        /// <summary>The contents of a bytes field or an inner message. Empty for a number field.</summary>
        public ReadOnlySpan<byte> Payload { get; private set; }

        public bool Next()
        {
            if (_rest.IsEmpty) return false;

            ulong key = ReadVarint();
            Field = (int)(key >> 3);
            Number = 0;
            Payload = default;
            switch ((int)(key & 7))
            {
                case Varint:
                    Number = ReadVarint();
                    break;
                case Bytes:
                    ulong length = ReadVarint();
                    if (length > (ulong)_rest.Length) throw new InvalidDataException();
                    Payload = _rest[..(int)length];
                    _rest = _rest[(int)length..];
                    break;
                case Fixed64:
                    Skip(8);
                    break;
                case Fixed32:
                    Skip(4);
                    break;
                default:
                    throw new InvalidDataException();
            }

            return true;
        }

        private void Skip(int count)
        {
            if (count > _rest.Length) throw new InvalidDataException();
            _rest = _rest[count..];
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_rest.IsEmpty) throw new InvalidDataException();
                byte b = _rest[0];
                _rest = _rest[1..];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return value;
            }

            throw new InvalidDataException();
        }
    }
}

/// <summary>
/// One step of the state synchronisation: "if you have state <see cref="OldNum"/>, apply <see cref="Diff"/>
/// and you have state <see cref="NewNum"/>". It also carries the newest state received from the other side.
/// </summary>
internal sealed class MoshInstruction
{
    /// <summary>The only version there has been since echo acknowledgements were added.</summary>
    public const uint Version = 2;

    /// <summary>A state with this number means "the session is over".</summary>
    public const ulong ShutdownNum = ulong.MaxValue;

    public uint ProtocolVersion { get; init; } = Version;

    public ulong OldNum { get; init; }

    public ulong NewNum { get; init; }

    /// <summary>The newest state of the other side that the sender of this has.</summary>
    public ulong AckNum { get; init; }

    /// <summary>The receiver may forget every state older than this one.</summary>
    public ulong ThrowawayNum { get; init; }

    public byte[] Diff { get; init; } = [];

    /// <summary>Random filler, so the length of a datagram does not give away what was typed.</summary>
    public byte[] Chaff { get; init; } = [];

    public byte[] Encode()
    {
        var output = new List<byte>(Diff.Length + 48);
        MoshWire.WriteVarintField(output, 1, ProtocolVersion);
        MoshWire.WriteVarintField(output, 2, OldNum);
        MoshWire.WriteVarintField(output, 3, NewNum);
        MoshWire.WriteVarintField(output, 4, AckNum);
        MoshWire.WriteVarintField(output, 5, ThrowawayNum);
        MoshWire.WriteBytesField(output, 6, Diff);
        MoshWire.WriteBytesField(output, 7, Chaff);
        return [.. output];
    }

    public static MoshInstruction Decode(ReadOnlySpan<byte> data)
    {
        uint version = 0;
        ulong oldNum = 0, newNum = 0, ackNum = 0, throwawayNum = 0;
        byte[] diff = [];
        var reader = new MoshWire.Reader(data);
        while (reader.Next())
        {
            switch (reader.Field)
            {
                case 1: version = (uint)reader.Number; break;
                case 2: oldNum = reader.Number; break;
                case 3: newNum = reader.Number; break;
                case 4: ackNum = reader.Number; break;
                case 5: throwawayNum = reader.Number; break;
                case 6: diff = reader.Payload.ToArray(); break;
            }
        }

        return new MoshInstruction
        {
            ProtocolVersion = version,
            OldNum = oldNum,
            NewNum = newNum,
            AckNum = ackNum,
            ThrowawayNum = throwawayNum,
            Diff = diff,
        };
    }
}

/// <summary>What the server says changed on its screen between two states.</summary>
internal sealed class MoshHostMessage
{
    /// <summary>Terminal escape sequences that turn the old screen into the new one, in order.</summary>
    public List<byte[]> Output { get; } = [];

    /// <summary>
    /// The screen changed size between the two states. The server then clears and redraws everything,
    /// so the output is right whatever was on the screen before.
    /// </summary>
    public bool Resized { get; private set; }

    public static MoshHostMessage Decode(ReadOnlySpan<byte> data)
    {
        var message = new MoshHostMessage();
        var instructions = new MoshWire.Reader(data);
        while (instructions.Next())
        {
            if (instructions.Field != 1) continue;

            var instruction = new MoshWire.Reader(instructions.Payload);
            while (instruction.Next())
            {
                if (instruction.Field == 2)
                {
                    var hostBytes = new MoshWire.Reader(instruction.Payload);
                    while (hostBytes.Next())
                    {
                        if (hostBytes.Field == 4 && hostBytes.Payload.Length > 0) message.Output.Add(hostBytes.Payload.ToArray());
                    }
                }
                else if (instruction.Field == 3)
                {
                    message.Resized = true;
                }

                // Field 7 says how far typed keys have been shown, for guessing echo locally. Not used here.
            }
        }

        return message;
    }
}

/// <summary>
/// Everything the user has done since the session began: keys and window sizes, in order.
/// The server is brought up to date by sending the tail it has not confirmed yet.
/// </summary>
/// <remarks>
/// A state is simply "the first N events", so a state is a number and the difference between two
/// states is a slice. Events the server has confirmed are dropped from the front.
/// </remarks>
internal sealed class MoshUserStream
{
    private const int ResizeFlag = 1 << 30;

    // One entry per typed byte, or a window size packed into one number.
    private readonly List<int> _events = [];
    private long _dropped;

    /// <summary>How many events there have been, which is also the name of the current state.</summary>
    public long Count => _dropped + _events.Count;

    public void AddKeys(ReadOnlySpan<byte> keys)
    {
        foreach (byte key in keys) _events.Add(key);
    }

    public void AddResize(int columns, int rows) =>
        _events.Add(ResizeFlag | (Math.Clamp(columns, 1, 0x7FFF) << 15) | Math.Clamp(rows, 1, 0x7FFF));

    /// <summary>Forgets the events before <paramref name="state"/>. The server has them for good.</summary>
    public void DropBefore(long state)
    {
        int count = (int)(state - _dropped);
        if (count <= 0) return;

        _events.RemoveRange(0, count);
        _dropped = state;
    }

    /// <summary>The message that takes the server from <paramref name="state"/> to now. Empty when nothing happened since.</summary>
    public byte[] DiffFrom(long state)
    {
        var output = new List<byte>();
        var keys = new List<byte>();
        var inner = new List<byte>();
        var instruction = new List<byte>();

        void Emit(int extension)
        {
            instruction.Clear();
            MoshWire.WriteBytesField(instruction, extension, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inner));
            MoshWire.WriteBytesField(output, 1, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instruction));
            inner.Clear();
        }

        void FlushKeys()
        {
            if (keys.Count == 0) return;

            MoshWire.WriteBytesField(inner, 4, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keys));
            keys.Clear();
            Emit(2);
        }

        for (int i = (int)(state - _dropped); i < _events.Count; i++)
        {
            int e = _events[i];
            if ((e & ResizeFlag) == 0)
            {
                // Keys typed one after another travel as one keystroke message.
                keys.Add((byte)e);
                continue;
            }

            FlushKeys();
            MoshWire.WriteVarintField(inner, 5, (ulong)((e >> 15) & 0x7FFF));
            MoshWire.WriteVarintField(inner, 6, (ulong)(e & 0x7FFF));
            Emit(3);
        }

        FlushKeys();
        return [.. output];
    }
}

/// <summary>Cuts an instruction into datagram-sized pieces and puts received pieces back together.</summary>
/// <remarks>
/// A piece starts with the 8 byte number of the instruction it belongs to and a 2 byte piece number
/// whose top bit marks the last piece. The instruction is zlib-compressed before it is cut.
/// </remarks>
internal sealed class MoshFragments
{
    public const int HeaderLength = 10;

    // A whole screen of changes is far below this. It is what the real client allows too.
    private const int LargestInstruction = 4 * 1024 * 1024;
    private const int MostPieces = 0x8000;

    private readonly List<byte[]?> _pieces = [];
    private ulong _sendId;
    private ulong _receiveId = ulong.MaxValue;
    private int _arrived;
    private int _total = -1;

    public List<byte[]> Split(MoshInstruction instruction, int pieceSize)
    {
        byte[] payload = Compress(instruction.Encode());
        int room = pieceSize - HeaderLength;
        ulong id = ++_sendId;

        var pieces = new List<byte[]>();
        for (int offset = 0, number = 0; offset < payload.Length; offset += room, number++)
        {
            int length = Math.Min(room, payload.Length - offset);
            bool last = offset + length == payload.Length;
            byte[] piece = new byte[HeaderLength + length];
            BinaryPrimitives.WriteUInt64BigEndian(piece, id);
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(8), (ushort)((last ? 0x8000 : 0) | number));
            payload.AsSpan(offset, length).CopyTo(piece.AsSpan(HeaderLength));
            pieces.Add(piece);
        }

        return pieces;
    }

    /// <summary>Takes one received piece. Gives back the instruction once all its pieces are here.</summary>
    public MoshInstruction? Add(ReadOnlySpan<byte> piece)
    {
        if (piece.Length < HeaderLength) return null;

        ulong id = BinaryPrimitives.ReadUInt64BigEndian(piece);
        int number = BinaryPrimitives.ReadUInt16BigEndian(piece[8..]);
        bool last = (number & 0x8000) != 0;
        number &= 0x7FFF;

        if (id != _receiveId)
        {
            // A piece of another instruction: whatever was half collected is of no use any more.
            _pieces.Clear();
            _arrived = 0;
            _total = -1;
            _receiveId = id;
        }

        if (_total >= 0 && number >= _total) return null;

        while (_pieces.Count <= number) _pieces.Add(null);
        if (_pieces[number] is null)
        {
            _pieces[number] = piece[HeaderLength..].ToArray();
            _arrived++;
        }

        if (last)
        {
            if (_pieces.Count > number + 1) return null;
            _total = number + 1;
        }

        if (_arrived != _total || _total > MostPieces) return null;

        using var joined = new MemoryStream();
        foreach (byte[]? part in _pieces) joined.Write(part!);
        _pieces.Clear();
        _arrived = 0;
        _total = -1;

        return MoshInstruction.Decode(Decompress(joined.ToArray()));
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = zlib.Read(buffer)) > 0)
        {
            if (output.Length + count > LargestInstruction) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }
}
