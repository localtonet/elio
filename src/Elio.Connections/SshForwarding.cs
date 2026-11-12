using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Elio.Connections;

/// <summary>Extras a shell can ask the server for.</summary>
/// <param name="ForwardAgent">Programs on the server may use the keys in this computer's SSH agent.</param>
/// <param name="ForwardX11">Graphical programs on the server open their windows on this computer.</param>
public sealed record SshShellOptions(bool ForwardAgent = false, bool ForwardX11 = false);

/// <summary>Answers the server's requests to the SSH agent by passing them to the agent on this computer.</summary>
internal static class AgentRelay
{
    private const byte Failure = 5;

    // An agent message is a few kilobytes at most. Anything far beyond that is not one.
    private const int MaxMessage = 256 * 1024;

    /// <summary>Serves one forwarded agent connection until the server closes it.</summary>
    public static void Serve(Stream channel)
    {
        byte[] header = new byte[4];
        while (ReadFully(channel, header))
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > MaxMessage) return;

            byte[] request = new byte[length];
            if (!ReadFully(channel, request)) return;

            byte[]? reply;
            try
            {
                reply = SshAgent.Exchange(request);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
            {
                reply = null;
            }

            reply ??= [Failure];
            byte[] framed = new byte[4 + reply.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)reply.Length);
            reply.CopyTo(framed, 4);
            channel.Write(framed, 0, framed.Length);
        }
    }

    internal static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int read = stream.Read(buffer);
            if (read == 0) return false;
            buffer = buffer[read..];
        }

        return true;
    }
}

/// <summary>Where this computer's X server is, read the way X programs read the DISPLAY variable.</summary>
internal sealed record X11Display(string? Host, string? SocketPath, int Number, int Screen)
{
    private const int FirstTcpPort = 6000;

    /// <summary>The display to forward to, or null if this computer has none to offer.</summary>
    public static X11Display? Find() => Parse(Environment.GetEnvironmentVariable("DISPLAY"), OperatingSystem.IsWindows());

    internal static X11Display? Parse(string? display, bool windows)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            // X servers for Windows listen on the first display unless told otherwise, and nothing sets the variable there.
            return windows ? new X11Display("127.0.0.1", null, 0, 0) : null;
        }

        int colon = display.LastIndexOf(':');
        if (colon < 0) return null;

        string host = display[..colon];
        string[] numbers = display[(colon + 1)..].Split('.');
        if (!int.TryParse(numbers[0], out int number) || number < 0) return null;
        int screen = numbers.Length > 1 && int.TryParse(numbers[1], out int s) && s >= 0 ? s : 0;

        // macOS hands out a socket of its own: /private/tmp/com.apple.launchd.xyz/org.xquartz:0
        if (host.StartsWith('/')) return new X11Display(null, display[..colon] + ":" + numbers[0], number, screen);
        if (host.Length == 0 || host == "unix") return windows ? new X11Display("127.0.0.1", null, number, screen) : new X11Display(null, $"/tmp/.X11-unix/X{number}", number, screen);
        return new X11Display(host, null, number, screen);
    }

    public Stream Connect()
    {
        if (SocketPath is not null)
        {
            var local = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                local.Connect(new UnixDomainSocketEndPoint(SocketPath));
                return new NetworkStream(local, ownsSocket: true);
            }
            catch
            {
                local.Dispose();
                throw;
            }
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            client.Connect(Host!, FirstTcpPort + Number);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The cookie this computer's X server expects, from the user's authority file. Null if there is none.</summary>
    public byte[]? FindCookie()
    {
        string path = Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Xauthority");

        try
        {
            return File.Exists(path) ? FindCookie(File.ReadAllBytes(path), Number) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Looks through an authority file: records of family, address, display number, method name and data.</summary>
    internal static byte[]? FindCookie(ReadOnlySpan<byte> file, int number)
    {
        string wanted = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int offset = 0;

        static bool Field(ReadOnlySpan<byte> file, ref int offset, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (offset + 2 > file.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(file[offset..]);
            offset += 2;
            if (offset + length > file.Length) return false;
            value = file.Slice(offset, length);
            offset += length;
            return true;
        }

        while (offset + 2 <= file.Length)
        {
            offset += 2;
            if (!Field(file, ref offset, out _) || !Field(file, ref offset, out ReadOnlySpan<byte> display) || !Field(file, ref offset, out ReadOnlySpan<byte> name) || !Field(file, ref offset, out ReadOnlySpan<byte> data)) return null;

            if (Encoding.ASCII.GetString(name) == X11Relay.CookieMethod && Encoding.ASCII.GetString(display) == wanted) return data.ToArray();
        }

        return null;
    }
}

/// <summary>
/// Carries one forwarded X11 connection to this computer's X server. The server was given a made-up cookie, so a
/// program there has to present that one, and only then is it swapped for the real one that never left this computer.
/// </summary>
internal static class X11Relay
{
    public const string CookieMethod = "MIT-MAGIC-COOKIE-1";
    private const int CookieLength = 16;
    private const int SetupHeader = 12;

    public static string NewCookie() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(CookieLength));

    public static void Serve(Stream channel, X11Display display, string expectedCookie)
    {
        byte[]? opening = ReadOpening(channel, Convert.FromHexString(expectedCookie), display.FindCookie());
        if (opening is null) return;

        using Stream server = display.Connect();
        server.Write(opening, 0, opening.Length);

        // One direction on its own thread, the other on this one. Whichever ends first closes both.
        var outward = new Thread(() =>
        {
            try
            {
                Pump(server, channel);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The other direction notices and ends the connection.
            }
            finally
            {
                channel.Dispose();
            }
        })
        { IsBackground = true, Name = "X11 to server" };
        outward.Start();

        try
        {
            Pump(channel, server);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Closed from the other side.
        }
        finally
        {
            server.Dispose();
        }
    }

    private static void Pump(Stream from, Stream to)
    {
        byte[] buffer = new byte[32 * 1024];
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            to.Write(buffer, 0, read);
            to.Flush();
        }
    }

    /// <summary>
    /// Reads the first message of an X11 connection, checks it carries the cookie the server was given, and returns it
    /// rewritten with the real one. Null means the connection must be dropped.
    /// </summary>
    internal static byte[]? ReadOpening(Stream channel, byte[] expected, byte[]? real)
    {
        byte[] header = new byte[SetupHeader];
        if (!AgentRelay.ReadFully(channel, header)) return null;

        bool bigEndian = header[0] == (byte)'B';
        if (!bigEndian && header[0] != (byte)'l') return null;

        ushort Read16(int at) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(at));
        static int Padded(int length) => (length + 3) & ~3;

        int nameLength = Read16(6);
        int dataLength = Read16(8);
        byte[] rest = new byte[Padded(nameLength) + Padded(dataLength)];
        if (!AgentRelay.ReadFully(channel, rest)) return null;

        bool presented = Encoding.ASCII.GetString(rest, 0, nameLength) == CookieMethod
            && CryptographicOperations.FixedTimeEquals(rest.AsSpan(Padded(nameLength), dataLength), expected);
        if (!presented) return null;

        // With no cookie on this computer the X server is one that lets local programs in without, so none is sent.
        byte[] name = real is null ? [] : Encoding.ASCII.GetBytes(CookieMethod);
        byte[] data = real ?? [];
        byte[] opening = new byte[SetupHeader + Padded(name.Length) + Padded(data.Length)];
        header.AsSpan(0, 6).CopyTo(opening);

        void Write16(int at, int value)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(opening.AsSpan(at), (ushort)value);
            else BinaryPrimitives.WriteUInt16LittleEndian(opening.AsSpan(at), (ushort)value);
        }

        Write16(6, name.Length);
        Write16(8, data.Length);
        name.CopyTo(opening, SetupHeader);
        data.CopyTo(opening, SetupHeader + Padded(name.Length));
        return opening;
    }
}
