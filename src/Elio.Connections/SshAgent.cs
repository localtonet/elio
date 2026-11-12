using Elio.Core;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Elio.Connections;

/// <summary>Talks to a running SSH agent, so private keys never have to be read by this app.</summary>
internal static partial class SshAgent
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    private const uint FlagRsaSha256 = 2;
    private const uint FlagRsaSha512 = 4;
    private const string OpenSshPipe = "openssh-ssh-agent";
    private const int PipeConnectMilliseconds = 500;

    /// <summary>One way of logging in for each key the agent holds. Empty if there is no agent.</summary>
    public static IPrivateKeySource[] KeySources()
    {
        byte[]? reply;
        try
        {
            reply = Exchange([RequestIdentities]);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
        {
            return [];
        }

        if (reply is null || reply.Length < 5 || reply[0] != IdentitiesAnswer) return [];

        var sources = new List<IPrivateKeySource>();
        int offset = 1;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(offset));
        offset += 4;
        for (uint i = 0; i < count; i++)
        {
            byte[] blob = ReadString(reply, ref offset);
            ReadString(reply, ref offset);

            int typeOffset = 0;
            string type = Encoding.ASCII.GetString(ReadString(blob, ref typeOffset));
            sources.Add(new AgentKey(type, blob));
        }

        return [.. sources];
    }

    private static byte[] ReadString(byte[] data, ref int offset)
    {
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        byte[] value = data.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    private static byte[] Sign(byte[] blob, byte[] data, uint flags)
    {
        byte[] request = new byte[1 + 4 + blob.Length + 4 + data.Length + 4];
        int offset = 0;
        request[offset++] = SignRequest;
        WriteString(request, ref offset, blob);
        WriteString(request, ref offset, data);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(offset), flags);

        byte[]? reply = Exchange(request);
        if (reply is null || reply.Length < 5 || reply[0] != SignResponse) throw new InvalidOperationException(L.T("The SSH agent refused to sign."));

        int read = 1;
        return ReadString(reply, ref read);
    }

    private static void WriteString(byte[] target, ref int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), (uint)value.Length);
        offset += 4;
        value.CopyTo(target, offset);
        offset += value.Length;
    }

    /// <summary>Sends one message to the agent and returns its answer. Null if no agent is running.</summary>
    internal static byte[]? Exchange(byte[] request)
    {
        if (OperatingSystem.IsWindows())
        {
            string? socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
            string name = socket is { Length: > 0 } ? socket.Replace(@"\\.\pipe\", string.Empty) : OpenSshPipe;

            // Waiting for a pipe nobody has made costs the whole timeout, on every connection, when no agent is running.
            if (!File.Exists(@"\\.\pipe\" + name)) return ExchangeWithPageant(request);

            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            try
            {
                pipe.Connect(PipeConnectMilliseconds);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                return ExchangeWithPageant(request);
            }

            return ExchangeOver(pipe, request);
        }

        if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is not { Length: > 0 } path) return null;

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(client);
        return ExchangeOver(stream, request);
    }

    private static byte[] ExchangeOver(Stream stream, byte[] request)
    {
        byte[] framed = new byte[4 + request.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)request.Length);
        request.CopyTo(framed, 4);
        stream.Write(framed);
        stream.Flush();

        byte[] header = new byte[4];
        stream.ReadExactly(header);
        byte[] reply = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        stream.ReadExactly(reply);
        return reply;
    }

    private const int WmCopyData = 0x004A;
    private const nuint PageantCopyDataId = 0x804e50ba;
    private const int PageantMaxMessage = 8192;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint Data;
        public int Length;
        public nint Pointer;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string windowName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, int message, nint wParam, ref CopyDataStruct lParam);

    private static byte[]? ExchangeWithPageant(byte[] request)
    {
        nint window = FindWindow("Pageant", "Pageant");
        if (window == 0) return null;

        // Pageant reads the request from named shared memory and writes its reply back there.
        string name = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Environment.ProcessId:x8}";
        using var mapping = MemoryMappedFile.CreateNew(name, PageantMaxMessage);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();

        byte[] framed = new byte[4 + request.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)request.Length);
        request.CopyTo(framed, 4);
        view.WriteArray(0, framed, 0, framed.Length);

        nint nameMemory = Marshal.StringToHGlobalAnsi(name);
        try
        {
            var data = new CopyDataStruct { Data = PageantCopyDataId, Length = name.Length + 1, Pointer = nameMemory };
            if (SendMessage(window, WmCopyData, 0, ref data) == 0) return null;
        }
        finally
        {
            Marshal.FreeHGlobal(nameMemory);
        }

        byte[] header = new byte[4];
        view.ReadArray(0, header, 0, 4);
        byte[] reply = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        view.ReadArray(4, reply, 0, reply.Length);
        return reply;
    }

    private sealed class AgentKey : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

        public AgentKey(string type, byte[] blob) => HostKeyAlgorithms = type == "ssh-rsa"
            ? [new AgentAlgorithm("rsa-sha2-512", blob, FlagRsaSha512), new AgentAlgorithm("rsa-sha2-256", blob, FlagRsaSha256), new AgentAlgorithm("ssh-rsa", blob, 0)]
            : [new AgentAlgorithm(type, blob, 0)];
    }

    private sealed class AgentAlgorithm(string name, byte[] blob, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;

        public override byte[] Sign(byte[] data) => SshAgent.Sign(blob, data, flags);

        public override bool VerifySignature(byte[] data, byte[] signature) => false;
    }
}
