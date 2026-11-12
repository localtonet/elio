using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Elio.Core;
using Elio.Terminal;
using Microsoft.Win32.SafeHandles;

namespace Elio.Connections;

/// <summary>The operating system's side of a terminal: a program that believes it is talking to one.</summary>
internal interface IPty : IDisposable
{
    /// <summary>Waits for output. Returns 0 once the program is gone or <see cref="Shutdown"/> was called.</summary>
    int Read(Span<byte> buffer);

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    /// <summary>Ends the program and wakes a waiting <see cref="Read"/>. Handles stay open until <see cref="IDisposable.Dispose"/>.</summary>
    void Shutdown();

    /// <summary>The operating system's number for the program that was started.</summary>
    int ProcessId { get; }
}

/// <summary>A shell running on this computer.</summary>
/// <remarks>
/// The program is started directly on a pseudo terminal with nothing in between, so nothing of ours
/// (a job, a sandbox, an inherited handle) restricts what it can do. That is what keeps `sudo` working.
/// </remarks>
public sealed class LocalShellTransport : ITerminalTransport
{
    private const int ScratchSize = 64 * 1024;

    private readonly IPty _pty;
    private readonly byte[] _scratch = new byte[ScratchSize];
    private readonly SemaphoreSlim _wanted = new(0);
    private readonly Lock _closeGate = new();
    private TaskCompletionSource<int>? _pending;
    private int _available;
    private int _offset;
    private bool _ended;
    private bool _readerDone;
    private volatile bool _disposed;

    private LocalShellTransport(IPty pty, string description)
    {
        _pty = pty;
        Description = description;
        new Thread(ReadLoop) { IsBackground = true, Name = "Elio local shell" }.Start();
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>The operating system's number for the shell, for finding what it is running.</summary>
    public int ProcessId => _pty.ProcessId;

    /// <summary>Starts a program on a new pseudo terminal.</summary>
    /// <param name="commandLine">The program and its arguments, as they would be typed.</param>
    /// <exception cref="IOException">The program could not be started.</exception>
    /// <param name="directory">Where the program starts. The user's home folder if null or missing.</param>
    /// <param name="environment">Variables to set for the program on top of the ones Elio itself has.</param>
    public static LocalShellTransport Start(string commandLine, int columns, int rows, string? directory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        string expanded = Environment.ExpandEnvironmentVariables(commandLine).Trim();
        List<string> words = CommandLine.Split(expanded);
        if (words.Count == 0) throw new IOException(L.T("The command to run is empty."));

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directory = Directory.Exists(home) ? home : null;
        }

        environment ??= new Dictionary<string, string>();
        columns = Math.Clamp(columns, 1, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        IPty pty;
        if (OperatingSystem.IsWindows()) pty = WindowsPty.Start(expanded, directory, columns, rows, environment);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid()) pty = UnixPty.Start(words, directory, columns, rows, environment);
        else throw new IOException(L.T("A local shell cannot be opened on this system."));

        return new LocalShellTransport(pty, Path.GetFileName(words[0]));
    }

    // One thread per shell, which reads only when the terminal asks. Output the terminal has no room for
    // then stays in the pseudo terminal and the program writing it waits, instead of filling memory.
    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                _wanted.Wait();
                if (_disposed) break;

                int count = ReadOnce();
                _pending!.TrySetResult(count);
                if (count == 0) return;
            }

            // Closing a pseudo console can wait for its last output to be taken, so take it.
            while (ReadOnce() > 0)
            {
            }
        }
        finally
        {
            lock (_closeGate)
            {
                _readerDone = true;
                if (_disposed) _pty.Dispose();
            }
        }
    }

    private int ReadOnce()
    {
        try
        {
            return _pty.Read(_scratch);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
        {
            return 0;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_available == 0)
        {
            if (_ended) return 0;

            // A read that was given up on is still out there, and its output belongs to the next caller.
            if (_pending is null)
            {
                _pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _wanted.Release();
            }

            int count = await _pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pending = null;
            if (count == 0)
            {
                _ended = true;
                return 0;
            }

            (_available, _offset) = (count, 0);
        }

        int taken = Math.Min(_available, buffer.Length);
        _scratch.AsSpan(_offset, taken).CopyTo(buffer.Span);
        _offset += taken;
        _available -= taken;
        return taken;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_disposed) return ValueTask.CompletedTask;

        try
        {
            _pty.Write(data.Span);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Win32Exception)
        {
            throw new IOException(L.T("The shell closed."), ex);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (!_disposed) _pty.Resize(Math.Clamp(columns, 1, short.MaxValue), Math.Clamp(rows, 1, short.MaxValue));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            if (_readerDone)
            {
                _pty.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        // The reader thread closes the handles once it has seen the end.
        _ = Task.Run(_pty.Shutdown);
        _wanted.Release();
        return ValueTask.CompletedTask;
    }
}

internal static class CommandLine
{
    /// <summary>Divides a command line into words. Quotes keep spaces together and are not part of the word.</summary>
    public static List<string> Split(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';

        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (inWord) words.Add(word.ToString());
        return words;
    }
}

[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsPty : IPty
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly nint PseudoConsoleAttribute = 0x00020016;

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly nint _process;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ManualResetEvent _exited;
    private nint _console;
    private int _consoleClosed;

    private WindowsPty(nint console, nint process, FileStream input, FileStream output)
    {
        (_console, _process, _input, _output) = (console, process, input, output);

        // The pseudo console keeps the output open after the shell exits, so its end has to be made here.
        _exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exited, (_, _) => CloseConsole(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int ProcessId { get; private init; }

    // The form CreateProcess wants: NAME=value pairs, each ended by a zero, sorted, with one more zero at the end.
    private static char[]? EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        if (extra.Count == 0) return null;

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        foreach (var (name, value) in extra) variables[name] = value;

        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString().ToCharArray();
    }

    public static WindowsPty Start(string commandLine, string? directory, int columns, int rows, IReadOnlyDictionary<string, string> environment)
    {
        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, attributes = 0;
        bool attributesReady = false;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) || !CreatePipe(out outputRead, out outputWrite, 0, 0)) throw Failure(L.T("Could not open a pipe"));

            int result;
            try
            {
                result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out console);
            }
            catch (EntryPointNotFoundException)
            {
                throw new IOException(L.T("A local shell needs Windows 10 version 1809 or later."));
            }

            if (result != 0) throw new IOException(L.T("Could not open the pseudo console (0x{0:x8}).", result));

            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Failure(L.T("Could not prepare the process attributes"));
            attributesReady = true;
            if (!UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, nint.Size, 0, 0)) throw Failure(L.T("Could not attach the pseudo console to the process"));

            // Without this, a shell started from a program whose own output is redirected writes there instead.
            var startup = new StartupInfoEx { Size = sizeof(StartupInfoEx), Flags = StartfUseStdHandles, AttributeList = attributes };
            ProcessInformation started;

            // CreateProcess may write into the command line.
            char[] command = [.. commandLine, '\0'];
            char[]? block = EnvironmentBlock(environment);
            fixed (char* commandPointer = command)
            fixed (char* directoryPointer = directory)
            fixed (char* blockPointer = block)
            {
                if (!CreateProcessW(null, commandPointer, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, (nint)blockPointer, directoryPointer, &startup, &started))
                    throw new IOException($"{commandLine}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            CloseHandle(started.Thread);

            var pty = new WindowsPty(
                console,
                started.Process,
                new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 1, isAsync: false),
                new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 1, isAsync: false))
            {
                ProcessId = started.ProcessId,
            };
            (console, inputWrite, outputRead) = (0, 0, 0);
            return pty;
        }
        finally
        {
            // The pseudo console has its own copies of these two by now.
            if (inputRead != 0) CloseHandle(inputRead);
            if (outputWrite != 0) CloseHandle(outputWrite);

            if (inputWrite != 0) CloseHandle(inputWrite);
            if (outputRead != 0) CloseHandle(outputRead);
            if (console != 0) ClosePseudoConsole(console);
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
        }
    }

    private static IOException Failure(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    public int Read(Span<byte> buffer)
    {
        try
        {
            return _output.Read(buffer);
        }
        catch (IOException)
        {
            // The pipe breaking is how the end arrives.
            return 0;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        _input.Write(data);
        _input.Flush();
    }

    public void Resize(int columns, int rows)
    {
        lock (_exited)
        {
            if (_consoleClosed == 0) ResizePseudoConsole(_console, new Coord((short)columns, (short)rows));
        }
    }

    // Before Windows 11 24H2 this waits until the last output has been read.
    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) != 0) return;

        nint console;
        lock (_exited)
        {
            console = _console;
            _console = 0;
        }

        ClosePseudoConsole(console);
    }

    public void Shutdown()
    {
        if (WaitForSingleObject(_process, 0) != 0) TerminateProcess(_process, 1);
        CloseConsole();
    }

    public void Dispose()
    {
        Shutdown();
        _exitWait.Unregister(null);
        _exited.Dispose();
        _input.Dispose();
        _output.Dispose();
        CloseHandle(_process);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
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
        public nint AttributeList;
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
    private static partial bool CreatePipe(out nint read, out nint write, nint attributes, uint size);

    [LibraryImport("kernel32")]
    private static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);

    [LibraryImport("kernel32")]
    private static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport("kernel32")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(char* application, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, char* directory, StartupInfoEx* startup, ProcessInformation* information);

    [LibraryImport("kernel32")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android")]
internal sealed unsafe partial class UnixPty : IPty
{
    private const int ReadWrite = 2;
    private const int Interrupted = 4;
    private const short PollIn = 1;
    private const int Hangup = 1;
    private const int Kill = 9;
    private const short SetSigDefault = 0x04;
    private const short SetSigMask = 0x08;
    private const int OpaqueSize = 1024;

    private static readonly bool Mac = OperatingSystem.IsMacOS();
    private static readonly Lock NameGate = new();

    private readonly int _master;
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private readonly int _pid;
    private volatile bool _closing;

    private UnixPty(int master, int pid, int wakeRead, int wakeWrite)
    {
        (_master, _pid, _wakeRead, _wakeWrite) = (master, pid, wakeRead, wakeWrite);

        // Collects the shell when it exits, so it doesn't linger as a zombie.
        new Thread(() =>
        {
            while (waitpid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == Interrupted)
            {
            }
        }) { IsBackground = true, Name = "Elio shell wait" }.Start();
    }

    public int ProcessId => _pid;

    public static UnixPty Start(IReadOnlyList<string> words, string? directory, int columns, int rows, IReadOnlyDictionary<string, string> extra)
    {
        // The flags differ: O_NOCTTY | O_CLOEXEC on Linux, O_NOCTTY alone on macOS, which accepts nothing else here.
        int master = posix_openpt(ReadWrite | (Mac ? 0x20000 : 0x100 | 0x80000));
        if (master < 0) throw Failure(L.T("Could not open a pseudo terminal"));

        int* wake = stackalloc int[2] { -1, -1 };
        void* actions = null, attributes = null, signals = null;
        var strings = new List<nint>();
        bool actionsReady = false, attributesReady = false;

        try
        {
            if (grantpt(master) != 0 || unlockpt(master) != 0) throw Failure(L.T("Could not prepare the pseudo terminal"));

            string slave;
            lock (NameGate) slave = Marshal.PtrToStringUTF8(ptsname(master)) ?? throw Failure(L.T("Could not get the pseudo terminal's name"));

            SetSize(master, columns, rows);
            if ((Mac ? pipe(wake) : pipe2(wake, 0x80000)) != 0) throw Failure(L.T("Could not open a pipe"));

            // These types are opaque and differ per system. This is several times the largest of them.
            actions = NativeMemory.AllocZeroed(OpaqueSize);
            attributes = NativeMemory.AllocZeroed(OpaqueSize);
            signals = NativeMemory.AllocZeroed(OpaqueSize);

            Check(posix_spawn_file_actions_init(actions));
            actionsReady = true;

            // The new session's leader opening the terminal is what makes it that session's terminal.
            Check(posix_spawn_file_actions_addopen(actions, 0, slave, ReadWrite, 0));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2));
            if (directory is not null) TryChangeDirectory(actions, directory);

            Check(posix_spawnattr_init(attributes));
            attributesReady = true;

            // .NET ignores some signals, and an ignored signal stays ignored in whatever is started from here.
            sigfillset(signals);
            sigdelset(signals, Kill);
            sigdelset(signals, Mac ? 17 : 19);
            Check(posix_spawnattr_setsigdefault(attributes, signals));
            sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals));

            // A new session, so the shell owns its terminal. On macOS also: close everything but what is set up above.
            short flags = (short)(SetSigDefault | SetSigMask | (Mac ? 0x0400 | 0x4000 : 0x80));
            Check(posix_spawnattr_setflags(attributes, flags));

            nint* arguments = Pointers(words, strings);
            nint* environment = Pointers(EnvironmentFor(extra), strings);
            int error = posix_spawnp(out int pid, words[0], actions, attributes, arguments, environment);
            NativeMemory.Free(arguments);
            NativeMemory.Free(environment);
            if (error != 0) throw new IOException($"{words[0]}: {new Win32Exception(error).Message}");

            var pty = new UnixPty(master, pid, wake[0], wake[1]);
            master = wake[0] = wake[1] = -1;
            return pty;
        }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attributesReady) posix_spawnattr_destroy(attributes);
            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(signals);
            foreach (nint text in strings) Marshal.FreeCoTaskMem(text);
            if (master >= 0) close(master);
            if (wake[0] >= 0) close(wake[0]);
            if (wake[1] >= 0) close(wake[1]);
        }
    }

    private static void TryChangeDirectory(void* actions, string directory)
    {
        try
        {
            posix_spawn_file_actions_addchdir_np(actions, directory);
        }
        catch (EntryPointNotFoundException)
        {
            // Older systems don't have it. The shell then starts where Elio did.
        }
    }

    private static List<string> EnvironmentFor(IReadOnlyDictionary<string, string> extra)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        foreach (var (name, value) in extra) variables[name] = value;
        return variables.Select(v => $"{v.Key}={v.Value}").ToList();
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

    private static void Check(int error)
    {
        if (error != 0) throw new IOException(L.T("Could not start the shell: {0}", new Win32Exception(error).Message));
    }

    private static IOException Failure(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private static void SetSize(int master, int columns, int rows)
    {
        var size = new WindowSize { Rows = (ushort)rows, Columns = (ushort)columns };

        // ioctl takes its third argument the way printf takes its extras. On Apple's arm64 those go on the
        // stack rather than in registers, and filling the registers first is the way to put it there.
        if (Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) ioctl_stack(master, 0x80087467, 0, 0, 0, 0, 0, 0, &size);
        else ioctl(master, Mac ? 0x80087467 : 0x5414, &size);
    }

    public int Read(Span<byte> buffer)
    {
        PollTarget* targets = stackalloc PollTarget[2];
        targets[0] = new PollTarget { Descriptor = _master, Events = PollIn };
        targets[1] = new PollTarget { Descriptor = _wakeRead, Events = PollIn };

        fixed (byte* pointer = buffer)
        {
            while (!_closing)
            {
                targets[0].Returned = targets[1].Returned = 0;
                if (poll(targets, 2, -1) < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted) continue;
                    return 0;
                }

                if (targets[1].Returned != 0) return 0;

                nint count = read(_master, pointer, (nuint)buffer.Length);
                if (count > 0) return (int)count;
                if (count < 0 && Marshal.GetLastPInvokeError() == Interrupted) continue;

                // Linux reports the shell being gone as an error, macOS as an ordinary end.
                return 0;
            }
        }

        return 0;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            int sent = 0;
            while (sent < data.Length && !_closing)
            {
                nint count = write(_master, pointer + sent, (nuint)(data.Length - sent));
                if (count > 0) sent += (int)count;
                else if (Marshal.GetLastPInvokeError() != Interrupted) throw new IOException(L.T("The shell closed."));
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!_closing) SetSize(_master, columns, rows);
    }

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        kill(_pid, Hangup);

        byte wake = 1;
        write(_wakeWrite, &wake, 1);
    }

    public void Dispose()
    {
        Shutdown();
        close(_master);
        close(_wakeRead);
        close(_wakeWrite);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollTarget
    {
        public int Descriptor;
        public short Events;
        public short Returned;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint ptsname(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe(int* descriptors);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe2(int* descriptors, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int descriptor, nuint request, WindowSize* size);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_stack(int descriptor, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, WindowSize* size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(PollTarget* targets, nuint count, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint read(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc")]
    private static partial int close(int descriptor);

    [LibraryImport("libc")]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc")]
    private static partial int sigfillset(void* set);

    [LibraryImport("libc")]
    private static partial int sigemptyset(void* set);

    [LibraryImport("libc")]
    private static partial int sigdelset(void* set, int signal);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(void* actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(void* actions);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addopen(void* actions, int descriptor, string path, int flags, uint mode);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(void* actions, int descriptor, int copy);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addchdir_np(void* actions, string path);

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

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawnp(out int pid, string file, void* actions, void* attributes, nint* arguments, nint* environment);
}
