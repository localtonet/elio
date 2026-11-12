using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>A shell the keeper has, as <see cref="HostedShellTransport.ListAsync"/> reports it.</summary>
/// <param name="Alive">False for a shell that ended while no window was attached. Attaching once shows its last output.</param>
public sealed record HostedShell(string Id, string CommandLine, bool Alive, int ProcessId, DateTime StartedUtc);

/// <summary>The shell that was asked for is not there any more: it ended, or the keeper was restarted.</summary>
public sealed class ShellGoneException() : IOException(L.T("The shell is no longer running."));

/// <summary>The keeper could not be reached.</summary>
public sealed class ShellHostUnavailableException() : IOException(L.T("Elio's background shell process is not running."));

/// <summary>
/// A local shell that lives in the keeper process (<see cref="ShellHost"/>) rather than in this window.
/// Letting go of it leaves the shell running, and a later window attaches to it again by its id.
/// </summary>
public sealed class HostedShellTransport : ITerminalTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    private readonly Stream _stream;
    private readonly ShellFrameReader _reader;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private Task<ShellFrame?>? _next;
    private ReadOnlyMemory<byte> _left;
    private bool _ended;
    private volatile bool _disposed;

    private HostedShellTransport(Link link, string id, string commandLine, int processId)
    {
        (_stream, _reader) = (link.Stream, link.Reader);
        Id = id;
        ProcessId = processId;
        List<string> words = CommandLine.Split(Environment.ExpandEnvironmentVariables(commandLine).Trim());
        Description = words.Count > 0 ? Path.GetFileName(words[0]) : commandLine;
    }

    /// <summary>The name the shell is kept under. Saved with the window layout to find the shell again.</summary>
    public string Id { get; }

    /// <summary>The operating system's number for the shell, for finding what it is running.</summary>
    public int ProcessId { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>The keeper went away, and the shells it kept went with it.</summary>
    public bool ConnectionLost { get; private set; }

    /// <summary>Another window attached to this shell, which goes on running there. Reading ends as if it had exited.</summary>
    public bool TakenOver { get; private set; }

    /// <summary>
    /// How many bytes of earlier output the first reads give back. Zero for a new shell. That output holds
    /// the questions programs asked the terminal back then (where is the cursor, what are you), so whatever
    /// the terminal answers while it takes in these bytes must be thrown away, not sent to the shell again.
    /// </summary>
    public int ReplayLength { get; private init; }

    /// <summary>The size the shell had while the replayed output was written, which is the size it is laid out for.</summary>
    public int ReplayColumns { get; private init; }

    /// <inheritdoc cref="ReplayColumns" />
    public int ReplayRows { get; private init; }

    /// <summary>
    /// Makes sure the keeper is running, starting it if it is not. It is started on its own, outside this
    /// process's console, process group and job, so it stays when this window closes.
    /// </summary>
    /// <param name="elioExecutablePath">The Elio program to start as the keeper, normally <see cref="Environment.ProcessPath"/>.</param>
    /// <returns>False if it could not be started or did not answer in time.</returns>
    public static Task<bool> EnsureHostAsync(string elioExecutablePath, CancellationToken cancellationToken) =>
        EnsureHostAsync(elioExecutablePath, [], cancellationToken);

    // The extra arguments are for a program that needs a launcher in front, such as `dotnet elio.dll`.
    internal static async Task<bool> EnsureHostAsync(string executablePath, IReadOnlyList<string> leadingArguments, CancellationToken cancellationToken)
    {
        // Several tabs being restored at once should start one keeper between them, not one each.
        await StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HostProcessIdAsync(cancellationToken).ConfigureAwait(false) is not null) return true;
            if (!DetachedProcess.Start(executablePath, [.. leadingArguments, ShellHost.Argument])) return false;

            long deadline = Environment.TickCount64 + (long)StartTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (await HostProcessIdAsync(cancellationToken).ConfigureAwait(false) is not null) return true;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            StartGate.Release();
        }
    }

    /// <summary>The keeper's process id, or null if no keeper answers.</summary>
    public static async Task<int?> HostProcessIdAsync(CancellationToken cancellationToken)
    {
        Link? link = await Link.OpenAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (link is null) return null;

        await link.Stream.DisposeAsync().ConfigureAwait(false);
        return link.HostProcessId;
    }

    /// <summary>The shells the keeper has. Empty if no keeper is running.</summary>
    public static async Task<IReadOnlyList<HostedShell>> ListAsync(CancellationToken cancellationToken)
    {
        Link? link = await Link.OpenAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (link is null) return [];

        await using (link.Stream.ConfigureAwait(false))
        {
            try
            {
                if (await link.AskAsync(ShellFrame.List, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false) != ShellFrame.Shells) return [];

                using BinaryReader reply = ShellProtocol.Read(link.Reader.Payload);
                var shells = new List<HostedShell>();
                for (int count = reply.ReadInt32(); count > 0; count--)
                    shells.Add(new HostedShell(reply.ReadString(), reply.ReadString(), reply.ReadBoolean(), reply.ReadInt32(), new DateTime(reply.ReadInt64(), DateTimeKind.Utc)));
                return shells;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return [];
            }
        }
    }

    /// <summary>Starts a shell in the keeper and attaches to it.</summary>
    /// <param name="id">A name of the caller's choosing that no other kept shell has, such as a new GUID.</param>
    /// <exception cref="ShellHostUnavailableException">No keeper answers. See <see cref="EnsureHostAsync(string, CancellationToken)"/>.</exception>
    /// <exception cref="IOException">The program could not be started, or the id is taken.</exception>
    public static async Task<HostedShellTransport> OpenAsync(string id, string commandLine, int columns, int rows, string? directory, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        byte[] request = ShellProtocol.Build(w =>
        {
            w.Write(id);
            w.Write(commandLine);
            w.Write(columns);
            w.Write(rows);
            w.Write(directory is not null);
            if (directory is not null) w.Write(directory);
            w.Write(environment?.Count ?? 0);
            foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            {
                w.Write(name);
                w.Write(value);
            }
        });

        Link link = await Link.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false) ?? throw new ShellHostUnavailableException();
        try
        {
            ShellFrame? answer = await link.AskAsync(ShellFrame.Open, request, cancellationToken).ConfigureAwait(false);
            if (answer != ShellFrame.Opened) throw Failure(answer, link.Reader.Payload);

            using BinaryReader reply = ShellProtocol.Read(link.Reader.Payload);
            return new HostedShellTransport(link, id, commandLine, reply.ReadInt32());
        }
        catch
        {
            await link.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Attaches to a shell the keeper already has. The first reads give back its recent output, so the
    /// terminal shows what was there. A window that was attached to it before loses it to this one.
    /// </summary>
    /// <exception cref="ShellGoneException">The keeper has no shell with this id.</exception>
    /// <exception cref="ShellHostUnavailableException">No keeper answers, which also means the shell is gone.</exception>
    public static async Task<HostedShellTransport> AttachAsync(string id, int columns, int rows, CancellationToken cancellationToken)
    {
        byte[] request = ShellProtocol.Build(w =>
        {
            w.Write(id);
            w.Write(columns);
            w.Write(rows);
        });

        Link link = await Link.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false) ?? throw new ShellHostUnavailableException();
        try
        {
            ShellFrame? answer = await link.AskAsync(ShellFrame.Attach, request, cancellationToken).ConfigureAwait(false);
            if (answer != ShellFrame.Attached) throw Failure(answer, link.Reader.Payload);

            ReadOnlyMemory<byte> payload = link.Reader.Payload;
            using BinaryReader reply = ShellProtocol.Read(payload);
            int processId = reply.ReadInt32();
            string commandLine = reply.ReadString();
            int oldColumns = reply.ReadInt32();
            int oldRows = reply.ReadInt32();
            int length = reply.ReadInt32();

            // The replay is the rest of the message, and stays where it arrived until the terminal has read it.
            ReadOnlyMemory<byte> replay = payload.Slice((int)reply.BaseStream.Position, length);
            return new HostedShellTransport(link, id, commandLine, processId)
            {
                ReplayLength = length,
                ReplayColumns = oldColumns,
                ReplayRows = oldRows,
                _left = replay,
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException)
        {
            await link.Stream.DisposeAsync().ConfigureAwait(false);
            throw new ShellHostUnavailableException();
        }
        catch
        {
            await link.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IOException Failure(ShellFrame? answer, ReadOnlyMemory<byte> payload)
    {
        if (answer != ShellFrame.Error) return new ShellHostUnavailableException();

        using BinaryReader reply = ShellProtocol.Read(payload);
        var error = (ShellError)reply.ReadByte();
        string message = reply.ReadString();
        return error switch
        {
            ShellError.Gone => new ShellGoneException(),
            ShellError.IdInUse => new IOException(L.T("A shell with this id is already running.")),
            ShellError.StartFailed when message.Length > 0 => new IOException(message),
            _ => new IOException(L.T("Elio's background shell process refused the request.")),
        };
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_left.IsEmpty && !await NextOutputAsync(cancellationToken).ConfigureAwait(false)) return 0;

            int taken = Math.Min(_left.Length, buffer.Length);
            _left[..taken].CopyTo(buffer);
            _left = _left[taken..];
            return taken;
        }
        finally
        {
            _readGate.Release();
        }
    }

    // Leaves the next piece of output in _left, or returns false once there will be no more.
    private async ValueTask<bool> NextOutputAsync(CancellationToken cancellationToken)
    {
        while (!_ended)
        {
            // A read that was given up on is still out there, and stopping it halfway through a message
            // would lose the place in the stream. Its result belongs to the next caller.
            _next ??= ReadFrameAsync();
            ShellFrame? frame = await _next.WaitAsync(cancellationToken).ConfigureAwait(false);
            _next = null;

            switch (frame)
            {
                case ShellFrame.Output:
                    _left = _reader.Payload;
                    if (!_left.IsEmpty) return true;
                    break;

                case ShellFrame.Exited:
                    _ended = true;
                    break;

                case ShellFrame.TakenOver:
                    TakenOver = true;
                    _ended = true;
                    break;

                default:
                    // Closing it ourselves also ends up here, and that is not a lost connection.
                    ConnectionLost = !_disposed;
                    _ended = true;
                    break;
            }
        }

        return false;
    }

    private async Task<ShellFrame?> ReadFrameAsync()
    {
        try
        {
            return await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_disposed) return;

        try
        {
            while (!data.IsEmpty)
            {
                int count = Math.Min(data.Length, ShellProtocol.ChunkSize);
                await SendAsync(ShellFrame.Input, data[..count], cancellationToken).ConfigureAwait(false);
                data = data[count..];
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException(L.T("The shell closed."), ex);
        }
    }

    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (_disposed) return;

        try
        {
            await SendAsync(ShellFrame.Resize, ShellProtocol.Build(w =>
            {
                w.Write(columns);
                w.Write(rows);
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Reading reports the end.
        }
    }

    private async ValueTask SendAsync(ShellFrame type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ShellProtocol.WriteAsync(_stream, type, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Ends the shell itself, for a tab the user closed. <see cref="DisposeAsync"/> alone leaves it running.</summary>
    public async Task CloseAsync()
    {
        if (_disposed) return;

        using var limit = new CancellationTokenSource(CloseTimeout);
        try
        {
            await SendAsync(ShellFrame.Close, ReadOnlyMemory<byte>.Empty, limit.Token).ConfigureAwait(false);

            // Waits for the keeper to say the shell is gone, so the caller can count on it. A read the
            // terminal still has going sees that first, and then there is nothing left to wait for here.
            await _readGate.WaitAsync(limit.Token).ConfigureAwait(false);
            try
            {
                _left = default;
                while (await NextOutputAsync(limit.Token).ConfigureAwait(false)) _left = default;
            }
            finally
            {
                _readGate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The keeper is gone or slow. Closing the connection is all that is left to do.
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Lets go of the shell and leaves it running in the keeper.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync(ShellFrame.Detach, ReadOnlyMemory<byte>.Empty, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Closing the connection says the same thing.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A connection to the keeper that has been greeted.</summary>
    private sealed class Link(Stream stream)
    {
        public Stream Stream { get; } = stream;
        public ShellFrameReader Reader { get; } = new(stream);
        public int HostProcessId { get; private set; }

        /// <returns>Null if no keeper answers.</returns>
        public static async Task<Link?> OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stream? stream = await ShellHostEndpoint.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (stream is null) return null;

            try
            {
                var link = new Link(stream);
                if (await link.AskAsync(ShellFrame.Hello, ShellProtocol.Build(w => w.Write(ShellProtocol.Version)), cancellationToken).ConfigureAwait(false) == ShellFrame.Welcome)
                {
                    using BinaryReader reply = ShellProtocol.Read(link.Reader.Payload);
                    if (reply.ReadInt32() >= 1)
                    {
                        link.HostProcessId = reply.ReadInt32();
                        return link;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        /// <summary>Sends one request and waits for its answer. Null if the keeper hung up.</summary>
        /// <exception cref="IOException">The keeper did not answer in time, or the connection broke.</exception>
        public async Task<ShellFrame?> AskAsync(ShellFrame type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ReplyTimeout);
            try
            {
                await ShellProtocol.WriteAsync(Stream, type, payload, limit.Token).ConfigureAwait(false);
                return await Reader.ReadAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException(L.T("Elio's background shell process is not running."));
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException(L.T("Elio's background shell process is not running."), ex);
            }
        }
    }
}

/// <summary>Starts a program that outlives the one that started it.</summary>
internal static unsafe partial class DetachedProcess
{
    public static bool Start(string executable, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows()) return StartWindows(executable, arguments);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return StartUnix(executable, arguments);
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool StartWindows(string executable, IReadOnlyList<string> arguments)
    {
        const uint DetachedProcessFlag = 0x00000008;
        const uint CreateNewProcessGroup = 0x00000200;
        const uint CreateBreakawayFromJob = 0x01000000;

        static string Quote(string word) => word.Contains(' ') ? $"\"{word}\"" : word;
        string line = string.Join(' ', new[] { $"\"{executable}\"" }.Concat(arguments.Select(Quote)));

        // No console, so closing the one Elio was started from does not reach it. Its own process group,
        // so neither does Ctrl+C there. Out of the job, because whatever started Elio inside a job may end
        // everything in it when Elio closes. Not every job lets a process out, hence the second try.
        foreach (uint flags in new[] { DetachedProcessFlag | CreateNewProcessGroup | CreateBreakawayFromJob, DetachedProcessFlag | CreateNewProcessGroup })
        {
            var startup = new StartupInfo { Size = sizeof(StartupInfo) };
            ProcessInformation started;

            // CreateProcess may write into the command line.
            char[] command = [.. line, '\0'];
            fixed (char* commandPointer = command)
            {
                // Nothing is inherited: a handle of this window held by the keeper would keep a file or a pipe open for days.
                if (!CreateProcessW(null, commandPointer, 0, 0, false, flags, 0, null, &startup, &started)) continue;
            }

            CloseHandle(started.Thread);
            CloseHandle(started.Process);
            return true;
        }

        return false;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool StartUnix(string executable, IReadOnlyList<string> arguments)
    {
        const int ReadWrite = 2;
        const int Kill = 9;
        const int Interrupted = 4;
        const int OpaqueSize = 1024;
        bool mac = OperatingSystem.IsMacOS();

        // These types are opaque and differ per system. This is several times the largest of them.
        void* actions = NativeMemory.AllocZeroed(OpaqueSize);
        void* attributes = NativeMemory.AllocZeroed(OpaqueSize);
        void* signals = NativeMemory.AllocZeroed(OpaqueSize);
        var strings = new List<nint>();
        nint* argumentPointers = null, environmentPointers = null;
        bool actionsReady = false, attributesReady = false;

        try
        {
            if (posix_spawn_file_actions_init(actions) != 0) return false;
            actionsReady = true;

            // Nothing to read and nowhere to write: holding on to the terminal or pipe Elio was started
            // with would tie the keeper to it.
            if (posix_spawn_file_actions_addopen(actions, 0, "/dev/null", ReadWrite, 0) != 0) return false;
            if (posix_spawn_file_actions_adddup2(actions, 0, 1) != 0 || posix_spawn_file_actions_adddup2(actions, 0, 2) != 0) return false;

            if (posix_spawnattr_init(attributes) != 0) return false;
            attributesReady = true;

            // .NET ignores some signals, and an ignored signal stays ignored in whatever is started from here.
            sigfillset(signals);
            sigdelset(signals, Kill);
            sigdelset(signals, mac ? 17 : 19);
            posix_spawnattr_setsigdefault(attributes, signals);
            sigemptyset(signals);
            posix_spawnattr_setsigmask(attributes, signals);

            // Its own session, so it has no terminal to be hung up from and is not in this window's process group.
            // On macOS also: close everything but what is set up above.
            short flags = (short)(0x04 | 0x08 | (mac ? 0x0400 | 0x4000 : 0x80));
            if (posix_spawnattr_setflags(attributes, flags) != 0) return false;

            var variables = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables.Add($"{entry.Key}={entry.Value}");

            argumentPointers = Pointers([executable, .. arguments], strings);
            environmentPointers = Pointers(variables, strings);
            if (posix_spawn(out int pid, executable, actions, attributes, argumentPointers, environmentPointers) != 0) return false;

            // Collects it if it ends while this window is still here, so it does not linger as a zombie.
            // Once this window is gone the system does that.
            new Thread(() =>
            {
                while (waitpid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == Interrupted)
                {
                }
            }) { IsBackground = true, Name = "Elio shell keeper wait" }.Start();
            return true;
        }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attributesReady) posix_spawnattr_destroy(attributes);
            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(signals);
            NativeMemory.Free(argumentPointers);
            NativeMemory.Free(environmentPointers);
            foreach (nint text in strings) Marshal.FreeCoTaskMem(text);
        }
    }

    private static nint* Pointers(IReadOnlyList<string> texts, List<nint> owned)
    {
        nint* array = (nint*)NativeMemory.AllocZeroed((nuint)(texts.Count + 1), (nuint)sizeof(nint));
        for (int i = 0; i < texts.Count; i++)
        {
            array[i] = Marshal.StringToCoTaskMemUTF8(texts[i]);
            owned.Add(array[i]);
        }

        return array;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(char* application, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, char* directory, StartupInfo* startup, ProcessInformation* information);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(void* actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(void* actions);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addopen(void* actions, int descriptor, string path, int flags, uint mode);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(void* actions, int descriptor, int copy);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(void* attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(void* attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(void* attributes, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigdefault(void* attributes, void* signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigmask(void* attributes, void* signals);

    [LibraryImport("libc")]
    private static partial int sigfillset(void* set);

    [LibraryImport("libc")]
    private static partial int sigemptyset(void* set);

    [LibraryImport("libc")]
    private static partial int sigdelset(void* set, int signal);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn(out int pid, string path, void* actions, void* attributes, nint* arguments, nint* environment);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);
}
