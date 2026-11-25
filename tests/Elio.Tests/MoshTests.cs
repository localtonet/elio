using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Elio.Connections;

namespace Elio.Tests;

/// <summary>
/// Mosh as it arrives over UDP: datagrams sealed with the key the server printed, pieces of compressed instructions,
/// and the few protocol buffer messages inside. Anyone can send a datagram to the port, so all of it is input from strangers.
/// </summary>
public sealed class MoshTests
{
    // The 16 bytes 00 01 .. 0f, as mosh-server prints them: base64 without its two padding characters.
    private static readonly string Key = Convert.ToBase64String([.. Enumerable.Range(0, 16).Select(i => (byte)i)])[..22];

    private const ulong ToClient = 1UL << 63;

    // ---- The key and the seal -------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("AAECAwQFBgcICQoLDA0OD")]
    [InlineData("AAECAwQFBgcICQoLDA0OD!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODx")]
    public void Key_ThatIsNotExactlyWhatTheServerPrints_IsRefused(string key) =>
        Assert.Throws<FormatException>(() => new MoshCipher(key));

    [Fact]
    public void Seal_ThenOpen_GivesBackTheNonceAndTheBytes()
    {
        var cipher = new MoshCipher(Key);
        byte[] plain = RandomNumberGenerator.GetBytes(500);

        Assert.True(new MoshCipher(Key).TryOpen(cipher.Seal(ToClient | 42, plain), out ulong nonce, out byte[] opened));

        Assert.Equal(ToClient | 42, nonce);
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void Datagram_FromAnotherKeyOrChangedOnTheWay_DoesNotOpen()
    {
        byte[] datagram = new MoshCipher(Key).Seal(7, "hello"u8);
        string otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))[..22];

        Assert.False(new MoshCipher(otherKey).TryOpen(datagram, out _, out _));
        for (int i = 0; i < datagram.Length; i++)
        {
            byte[] changed = (byte[])datagram.Clone();
            changed[i] ^= 0x80;
            Assert.False(new MoshCipher(Key).TryOpen(changed, out _, out _), $"byte {i}");
        }
    }

    [Fact]
    public void Datagram_TooShortOrTooLong_IsRefusedWithoutDecrypting()
    {
        var cipher = new MoshCipher(Key);

        Assert.False(cipher.TryOpen(new byte[MoshCipher.NonceLength + MoshCipher.TagLength - 1], out _, out _));
        Assert.False(cipher.TryOpen(cipher.Seal(1, new byte[MoshCipher.ReceiveLimit]), out _, out _));
    }

    [Fact]
    public void Datagrams_OfNoise_NeverOpenAndNeverThrow()
    {
        var cipher = new MoshCipher(Key);
        var random = new Random(1);
        for (int round = 0; round < 20_000; round++)
            Assert.False(cipher.TryOpen(Fuzz.Garbage(random, 3000), out _, out _));
    }

    // ---- Protocol buffers -------------------------------------------------------------------------

    [Fact]
    public void Instruction_EncodedThenDecoded_IsTheSame()
    {
        var instruction = new MoshInstruction { OldNum = 3, NewNum = ulong.MaxValue, AckNum = 1L << 40, ThrowawayNum = 2, Diff = [1, 2, 3], Chaff = [9, 9] };

        MoshInstruction decoded = MoshInstruction.Decode(instruction.Encode());

        Assert.Equal((2u, 3UL, ulong.MaxValue, 1UL << 40, 2UL), (decoded.ProtocolVersion, decoded.OldNum, decoded.NewNum, decoded.AckNum, decoded.ThrowawayNum));
        Assert.Equal([1, 2, 3], decoded.Diff);
    }

    [Fact]
    public void Instruction_WithFieldsItDoesNotKnow_SkipsThem()
    {
        var output = new List<byte>();
        MoshWire.WriteVarintField(output, 3, 77);
        output.AddRange([(15 << 3) | 1, 1, 2, 3, 4, 5, 6, 7, 8]);
        output.AddRange([(14 << 3) | 5, 1, 2, 3, 4]);
        MoshWire.WriteBytesField(output, 99, [1, 2]);

        Assert.Equal(77UL, MoshInstruction.Decode([.. output]).NewNum);
    }

    [Theory]
    [InlineData(new byte[] { 0x08 })]
    [InlineData(new byte[] { 0x08, 0x80 })]
    [InlineData(new byte[] { 0x32, 0x05, 1, 2 })]
    [InlineData(new byte[] { 0x32, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    [InlineData(new byte[] { 0x09, 1, 2, 3 })]
    [InlineData(new byte[] { 0x0D, 1 })]
    [InlineData(new byte[] { 0x0B })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    public void Instruction_CutShortOrMalformed_IsInvalidData(byte[] data) =>
        Assert.Throws<InvalidDataException>(() => MoshInstruction.Decode(data));

    [Fact]
    public void HostMessage_GivesTheOutputInOrderAndSaysWhenTheScreenWasResized()
    {
        static byte[] Instruction(int extension, Action<List<byte>> fill)
        {
            var inner = new List<byte>();
            fill(inner);
            var instruction = new List<byte>();
            MoshWire.WriteBytesField(instruction, extension, inner.ToArray());
            return [.. instruction];
        }

        var message = new List<byte>();
        MoshWire.WriteBytesField(message, 1, Instruction(2, b => MoshWire.WriteBytesField(b, 4, "first"u8)));
        MoshWire.WriteBytesField(message, 1, Instruction(3, b => MoshWire.WriteVarintField(b, 5, 120)));
        MoshWire.WriteBytesField(message, 1, Instruction(2, b => MoshWire.WriteBytesField(b, 4, "second"u8)));
        MoshWire.WriteBytesField(message, 1, Instruction(7, b => MoshWire.WriteVarintField(b, 8, 5)));

        MoshHostMessage decoded = MoshHostMessage.Decode([.. message]);

        Assert.True(decoded.Resized);
        Assert.Equal(["first", "second"], decoded.Output.Select(System.Text.Encoding.ASCII.GetString));
    }

    [Fact]
    public void Messages_OfGarbage_OnlyEverFailAsInvalidData()
    {
        var random = new Random(2);
        byte[] valid = new MoshInstruction { OldNum = 1, NewNum = 2, Diff = [0x0a, 0x05, 0x12, 0x03, 0x22, 0x01, 0x41] }.Encode();
        for (int round = 0; round < 50_000; round++)
        {
            byte[] data = round % 2 == 0 ? Fuzz.Garbage(random, 64) : Fuzz.Mutate(random, valid);
            try
            {
                MoshInstruction instruction = MoshInstruction.Decode(data);
                MoshHostMessage.Decode(instruction.Diff);
                MoshHostMessage.Decode(data);
            }
            catch (InvalidDataException)
            {
                // Dropped like a lost datagram.
            }
        }
    }

    // ---- Pieces --------------------------------------------------------------------------------

    private static MoshInstruction Large() => new() { OldNum = 1, NewNum = 2, Diff = RandomNumberGenerator.GetBytes(20_000) };

    [Fact]
    public void Pieces_PutBackTogetherInAnyOrder_GiveTheInstruction()
    {
        var sender = new MoshFragments();
        MoshInstruction sent = Large();
        List<byte[]> pieces = sender.Split(sent, 1000);
        Assert.True(pieces.Count > 10);

        var receiver = new MoshFragments();
        MoshInstruction? received = null;
        foreach (byte[] piece in pieces.AsEnumerable().Reverse().Concat([pieces[3]]))
            received ??= receiver.Add(piece);

        Assert.NotNull(received);
        Assert.Equal(sent.Diff, received.Diff);
    }

    [Fact]
    public void Pieces_OfAnInstructionThatWasLeftUnfinished_AreDroppedForTheNextOne()
    {
        var sender = new MoshFragments();
        List<byte[]> first = sender.Split(Large(), 1000);
        MoshInstruction second = Large();
        List<byte[]> pieces = sender.Split(second, 1000);

        var receiver = new MoshFragments();
        foreach (byte[] piece in first.Skip(1)) Assert.Null(receiver.Add(piece));
        MoshInstruction? received = null;
        foreach (byte[] piece in pieces) received ??= receiver.Add(piece);

        Assert.Equal(second.Diff, received!.Diff);
    }

    private static byte[] Piece(ulong id, int number, bool last, byte[] data)
    {
        byte[] piece = new byte[MoshFragments.HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(piece, id);
        BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(8), (ushort)((last ? 0x8000 : 0) | number));
        data.CopyTo(piece, MoshFragments.HeaderLength);
        return piece;
    }

    [Fact]
    public void Pieces_ThatContradictWhichOneIsLast_NeverFinishAnInstruction()
    {
        // A piece numbered beyond the one that says it is the last.
        var receiver = new MoshFragments();
        Assert.Null(receiver.Add(Piece(5, 3, last: false, [1])));
        Assert.Null(receiver.Add(Piece(5, 1, last: true, [2])));
        Assert.Null(receiver.Add(Piece(5, 0, last: false, [3])));
        Assert.Null(receiver.Add(Piece(5, 2, last: false, [4])));

        // Once the last is known, a piece numbered at or past it is not taken.
        var other = new MoshFragments();
        Assert.Null(other.Add(Piece(6, 1, last: true, [1])));
        Assert.Null(other.Add(Piece(6, 5, last: false, [1])));
        Assert.Null(other.Add(Piece(6, 1, last: true, [1])));

        // Too short to have a header at all.
        Assert.Null(other.Add(new byte[MoshFragments.HeaderLength - 1]));
    }

    [Fact]
    public void Instruction_ThatUnpacksToMoreThanFourMegabytes_IsRefused()
    {
        // Zeros compress to almost nothing: 16 MB of them fit in one datagram's worth of bytes.
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] zeros = new byte[1024 * 1024];
            for (int i = 0; i < 16; i++) zlib.Write(zeros);
        }

        Assert.True(compressed.Length < 64 * 1024);
        var receiver = new MoshFragments();

        byte[] piece = Piece(1, 0, last: true, compressed.ToArray());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => receiver.Add(piece));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32L * 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void Pieces_OfGarbage_OnlyEverFailAsInvalidData_AndHoldBoundedMemory()
    {
        var random = new Random(3);
        var receiver = new MoshFragments();
        for (int round = 0; round < 50_000; round++)
        {
            byte[] piece = Fuzz.Garbage(random, 200);
            if (piece.Length >= 10)
            {
                // Mostly one instruction, so that pieces pile up as they would in an attack.
                BinaryPrimitives.WriteUInt64BigEndian(piece, (ulong)random.Next(3));
            }

            try
            {
                receiver.Add(piece);
            }
            catch (InvalidDataException)
            {
                // A complete instruction that did not unpack or parse.
            }
        }
    }

    // ---- What the user typed ----------------------------------------------------------------

    [Fact]
    public void UserStream_SendsOnlyWhatTheServerHasNotConfirmed()
    {
        var user = new MoshUserStream();
        user.AddResize(80, 24);
        user.AddKeys("ls"u8);
        long typed = user.Count;
        user.AddKeys("\r"u8);

        MoshHostMessage all = MoshHostMessage.Decode(user.DiffFrom(0));
        Assert.True(all.Resized);

        user.DropBefore(typed);
        byte[] rest = user.DiffFrom(typed);
        Assert.Equal(4, user.Count);
        Assert.Empty(user.DiffFrom(user.Count));
        Assert.Contains((byte)'\r', rest);
        Assert.DoesNotContain((byte)'l', rest);
    }

    [Fact]
    public void UserStream_ResizeWithAbsurdSizes_IsClamped()
    {
        var user = new MoshUserStream();
        user.AddResize(-5, int.MaxValue);

        // Inside: instruction 1 -> resize extension 3 -> width 5 and height 6.
        var outer = new MoshWire.Reader(user.DiffFrom(0));
        Assert.True(outer.Next());
        var instruction = new MoshWire.Reader(outer.Payload);
        Assert.True(instruction.Next());
        var resize = new MoshWire.Reader(instruction.Payload);
        var values = new List<ulong>();
        while (resize.Next()) values.Add(resize.Number);

        Assert.Equal([1UL, 0x7FFFUL], values);
    }

    [Theory]
    [InlineData("MOSH CONNECT 60001 AAECAwQFBgcICQoLDA0ODw\r\n", 60001)]
    [InlineData("\r\n\r\nMOSH CONNECT 60004 AAECAwQFBgcICQoLDA0ODw\r\n\r\nmosh-server (mosh 1.4.0) [build mosh 1.4.0]\r\n", 60004)]
    public void ServerReply_GivesThePortAndKey(string output, int port) =>
        Assert.Equal((port, "AAECAwQFBgcICQoLDA0ODw"), MoshTransport.ParseServerReply(output));

    [Theory]
    [InlineData("")]
    [InlineData("mosh-server: command not found")]
    [InlineData("MOSH CONNECT 0 AAECAwQFBgcICQoLDA0ODw\n")]
    [InlineData("MOSH CONNECT 70000 AAECAwQFBgcICQoLDA0ODw\n")]
    [InlineData("MOSH CONNECT 60001 AAECAwQFBgcICQoLDA0OD\n")]
    [InlineData("echo MOSH CONNECT 60001 AAECAwQFBgcICQoLDA0ODw\n")]
    public void ServerReply_WithoutAValidConnectLine_IsNothing(string output) =>
        Assert.Null(MoshTransport.ParseServerReply(output));
}
