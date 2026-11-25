using System.Security.Cryptography;
using System.Text;
using Elio.Connections;
using Elio.Core;
using Renci.SshNet;

namespace Elio.Tests;

/// <summary>
/// A real sshd to test against, named by ELIO_TEST_SSHD as host:port:user:password (the password may itself hold
/// colons). The workflow in .github/workflows/integration.yml starts one in a container and sets it.
/// </summary>
internal sealed record SshdTarget(string Host, int Port, string User, string Password)
{
    public const string Variable = "ELIO_TEST_SSHD";

    public static SshdTarget? FromEnvironment()
    {
        string[] parts = (Environment.GetEnvironmentVariable(Variable) ?? string.Empty).Split(':', 4);
        return parts.Length == 4 && int.TryParse(parts[1], out int port) && parts[0].Length > 0 && parts[2].Length > 0
            ? new SshdTarget(parts[0], port, parts[2], parts[3])
            : null;
    }

    public SshEndpoint Endpoint(string? password = null) => new(Host, Port, User, password ?? Password);
}

/// <summary>Runs only when ELIO_TEST_SSHD names a server; skipped, and saying why, everywhere else.</summary>
internal sealed class SshdFactAttribute : FactAttribute
{
    public SshdFactAttribute()
    {
        if (SshdTarget.FromEnvironment() is null) Skip = $"Set {SshdTarget.Variable}=host:port:user:password to run this against a real sshd.";
    }
}

/// <summary>
/// Connecting, trusting and refusing host keys, running commands and moving files against a real OpenSSH server.
/// Opt-in: these never run unless ELIO_TEST_SSHD is set, so the ordinary test run touches no network.
/// </summary>
public sealed class SshdIntegrationTests : TempDataDirectoryTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static SshdTarget Target => SshdTarget.FromEnvironment()!;

    /// <summary>Trusts a server's key the first time, as the app does after asking, and holds it to that key afterwards.</summary>
    private static HostKeyPolicy Pinned(KnownHosts known) => (endpoint, key) => known.Check(endpoint.Host, endpoint.Port, key.Fingerprint) switch
    {
        HostKeyStatus.Trusted => true,
        HostKeyStatus.Unknown => Trust(known, endpoint, key),
        _ => false,
    };

    private static bool Trust(KnownHosts known, SshEndpoint endpoint, HostKeyInfo key)
    {
        known.Trust(endpoint.Host, endpoint.Port, key.Algorithm, key.Fingerprint);
        return true;
    }

    private static Task<SftpConnection> ConnectFilesAsync(KnownHosts known) =>
        SftpConnection.ConnectAsync([Target.Endpoint()], Pinned(known), CancellationToken.None).WaitAsync(Patience);

    // A folder of its own on the server for one test, removed afterwards.
    private static async Task<string> RemoteFolderAsync(SftpConnection connection)
    {
        string folder = RemotePath.Combine(connection.HomeDirectory, "elio-test-" + Guid.NewGuid().ToString("N"));
        await connection.CreateDirectoryAsync(folder, CancellationToken.None);
        return folder;
    }

    private static void RemoveRemote(SftpConnection connection, string folder)
    {
        foreach (var entry in connection.Client.ListDirectory(folder))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.IsDirectory) RemoveRemote(connection, entry.FullName);
            else connection.Client.DeleteFile(entry.FullName);
        }

        connection.Client.DeleteDirectory(folder);
    }

    // Without this, a variable written wrongly would quietly skip every test below and the run would still pass.
    [Fact]
    public void Variable_WhenSet_IsUnderstood()
    {
        if (Environment.GetEnvironmentVariable(SshdTarget.Variable) is { Length: > 0 } value)
            Assert.True(SshdTarget.FromEnvironment() is not null, $"{SshdTarget.Variable} is set but is not host:port:user:password: {value.Split(':')[0]}:...");
    }

    [SshdFact]
    public async Task Connect_TrustsTheKeyOnFirstUseAndAcceptsItAfterwards()
    {
        var known = new KnownHosts();

        CommandResult first = await SshCommandRunner.RunAsync([Target.Endpoint()], "echo hello", Pinned(known), CancellationToken.None).WaitAsync(Patience);
        string? pinned = known.Fingerprint(Target.Host, Target.Port);
        CommandResult second = await SshCommandRunner.RunAsync([Target.Endpoint()], "echo again", Pinned(new KnownHosts()), CancellationToken.None).WaitAsync(Patience);

        Assert.True(first.Succeeded, first.Failure);
        Assert.Equal("hello\n", first.Output);
        Assert.StartsWith("SHA256:", pinned, StringComparison.Ordinal);
        Assert.True(second.Succeeded, second.Failure);
        Assert.Equal(pinned, new KnownHosts().Fingerprint(Target.Host, Target.Port));
    }

    [SshdFact]
    public async Task Connect_ToAServerWhoseKeyChanged_IsRefusedBeforeLoggingIn()
    {
        var known = new KnownHosts();
        known.Trust(Target.Host, Target.Port, "ssh-ed25519", "SHA256:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('='));
        string pinned = known.Fingerprint(Target.Host, Target.Port)!;

        HostKeyRejectedException refused = await Assert.ThrowsAsync<HostKeyRejectedException>(() => ConnectFilesAsync(known));
        CommandResult command = await SshCommandRunner.RunAsync([Target.Endpoint()], "touch should-not-exist", Pinned(known), CancellationToken.None).WaitAsync(Patience);

        Assert.NotEqual(pinned, refused.Key.Fingerprint);
        Assert.False(command.Succeeded);
        Assert.Null(command.ExitCode);

        // The key that was trusted stays trusted: a changed key is never taken over quietly.
        Assert.Equal(pinned, new KnownHosts().Fingerprint(Target.Host, Target.Port));
    }

    [SshdFact]
    public async Task Connect_WithTheWrongPassword_IsALoginRefusal()
    {
        var known = new KnownHosts();

        await Assert.ThrowsAsync<LoginRefusedException>(() =>
            SftpConnection.ConnectAsync([Target.Endpoint("not the password " + Guid.NewGuid())], Pinned(known), CancellationToken.None).WaitAsync(Patience));
    }

    [SshdFact]
    public async Task Command_GivesItsExitCodeAndBothOutputs()
    {
        CommandResult result = await SshCommandRunner.RunAsync([Target.Endpoint()], "echo out; echo err >&2; exit 3", Pinned(new KnownHosts()), CancellationToken.None).WaitAsync(Patience);

        Assert.Null(result.Failure);
        Assert.Equal((3, "out\n", "err\n"), (result.ExitCode, result.Output, result.Error));
    }

    [SshdFact]
    public async Task Shell_RunsWhatIsTyped()
    {
        await using SshTransport shell = await SshTransport.ConnectAsync([Target.Endpoint()], 80, 24, Pinned(new KnownHosts()), null, CancellationToken.None).WaitAsync(Patience);

        await shell.WriteAsync("echo marker-$((6*7))\n"u8.ToArray(), CancellationToken.None);

        var seen = new StringBuilder();
        byte[] buffer = new byte[4096];
        using var limit = new CancellationTokenSource(Patience);
        while (!seen.ToString().Contains("marker-42", StringComparison.Ordinal))
        {
            int count = await shell.ReadAsync(buffer, limit.Token);
            Assert.NotEqual(0, count);
            seen.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }
    }

    [SshdFact]
    public async Task Sftp_UploadAndDownload_GiveBackTheSameBytes()
    {
        using SftpConnection connection = await ConnectFilesAsync(new KnownHosts());
        string folder = await RemoteFolderAsync(connection);
        string local = Path.Combine(DataDirectory, "upload.bin");
        byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        File.WriteAllBytes(local, content);
        try
        {
            string remote = RemotePath.Combine(folder, "upload.bin");
            long sent = 0;
            await SftpCopy.UploadAsync(connection.Client, local, remote, _ => { }, n => Interlocked.Add(ref sent, n), CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(content.Length, sent);
            Assert.False(connection.Client.Exists(remote + SftpCopy.PartSuffix));

            string back = Path.Combine(DataDirectory, "download.bin");
            await SftpCopy.DownloadAsync(connection.Client, remote, back, DateTime.UtcNow, _ => { }, _ => { }, CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(content, File.ReadAllBytes(back));
            Assert.False(File.Exists(back + SftpCopy.PartSuffix));
        }
        finally
        {
            RemoveRemote(connection, folder);
        }
    }

    [SshdFact]
    public async Task Sftp_UploadLeftHalfDone_IsResumedAndAWrongLeftoverStartedOver()
    {
        using SftpConnection connection = await ConnectFilesAsync(new KnownHosts());
        string folder = await RemoteFolderAsync(connection);
        string local = Path.Combine(DataDirectory, "big.bin");
        byte[] content = RandomNumberGenerator.GetBytes(12 * 1024 * 1024);
        File.WriteAllBytes(local, content);
        string remote = RemotePath.Combine(folder, "big.bin");
        try
        {
            // What a broken upload leaves: the first 8 MB under the temporary name.
            using (var part = new MemoryStream(content, 0, 8 * 1024 * 1024)) connection.Client.UploadFile(part, remote + SftpCopy.PartSuffix);

            long resumedAt = -1;
            await SftpCopy.UploadAsync(connection.Client, local, remote, at => resumedAt = at, _ => { }, CancellationToken.None).WaitAsync(Patience);

            Assert.InRange(resumedAt, 1, 8 * 1024 * 1024);
            using (var check = new MemoryStream())
            {
                connection.Client.DownloadFile(remote, check);
                Assert.Equal(content, check.ToArray());
            }

            // A leftover that does not match the file is not built on.
            byte[] wrong = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
            using (var part = new MemoryStream(wrong)) connection.Client.UploadFile(part, remote + SftpCopy.PartSuffix);
            resumedAt = -1;
            await SftpCopy.UploadAsync(connection.Client, local, remote, at => resumedAt = at, _ => { }, CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(0, resumedAt);
            using (var check = new MemoryStream())
            {
                connection.Client.DownloadFile(remote, check);
                Assert.Equal(content, check.ToArray());
            }
        }
        finally
        {
            RemoveRemote(connection, folder);
        }
    }

    [SshdFact]
    public async Task Sftp_DownloadLeftHalfDone_IsResumedAndAWrongLeftoverStartedOver()
    {
        using SftpConnection connection = await ConnectFilesAsync(new KnownHosts());
        string folder = await RemoteFolderAsync(connection);
        byte[] content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        string remote = RemotePath.Combine(folder, "file.bin");
        using (var upload = new MemoryStream(content)) connection.Client.UploadFile(upload, remote);
        string local = Path.Combine(DataDirectory, "file.bin");
        try
        {
            File.WriteAllBytes(local + SftpCopy.PartSuffix, content[..(1024 * 1024)]);
            long resumedAt = -1;
            await SftpCopy.DownloadAsync(connection.Client, remote, local, DateTime.UtcNow, at => resumedAt = at, _ => { }, CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(1024 * 1024, resumedAt);
            Assert.Equal(content, File.ReadAllBytes(local));

            File.WriteAllBytes(local + SftpCopy.PartSuffix, RandomNumberGenerator.GetBytes(1024 * 1024));
            await SftpCopy.DownloadAsync(connection.Client, remote, local, DateTime.UtcNow, at => resumedAt = at, _ => { }, CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(0, resumedAt);
            Assert.Equal(content, File.ReadAllBytes(local));
        }
        finally
        {
            RemoveRemote(connection, folder);
        }
    }

    [SshdFact]
    public async Task TransferQueue_UploadsAFolderAndDownloadsItBack()
    {
        var known = new KnownHosts();
        using SftpConnection setup = await ConnectFilesAsync(known);
        string folder = await RemoteFolderAsync(setup);
        string source = Path.Combine(DataDirectory, "project");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "first file");
        File.WriteAllBytes(Path.Combine(source, "b.bin"), RandomNumberGenerator.GetBytes(300_000));
        try
        {
            await using var queue = new TransferQueue(cancellation => SftpConnection.ConnectAsync([Target.Endpoint()], Pinned(known), cancellation));

            TransferJob upload = queue.Upload([source], folder);
            await UntilFinishedAsync(upload);
            Assert.Equal(TransferState.Completed, upload.State);
            Assert.Equal(2, upload.DoneFiles);

            List<RemoteEntry> uploaded = await setup.ListAsync(folder, CancellationToken.None);
            string back = Path.Combine(DataDirectory, "back");
            TransferJob download = queue.Download(uploaded, back);
            await UntilFinishedAsync(download);

            Assert.Equal(TransferState.Completed, download.State);
            Assert.Equal("first file", File.ReadAllText(Path.Combine(back, "project", "a.txt")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, "b.bin")), File.ReadAllBytes(Path.Combine(back, "project", "b.bin")));
        }
        finally
        {
            RemoveRemote(setup, folder);
        }
    }

    private static async Task UntilFinishedAsync(TransferJob job)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!job.IsFinished)
        {
            if (clock.Elapsed > Patience) Assert.Fail($"The transfer stayed {job.State}: {job.Note}");
            await Task.Delay(50);
        }

        Assert.True(job.Errors.Count == 0, string.Join("; ", job.Errors));
    }
}
