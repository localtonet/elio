using Elio.Connections;

namespace Elio.Tests;

/// <summary>
/// The transfer queue without a server: what it does when connecting fails, and the names it gives files a server
/// chose. Moving real files is in <see cref="SshdIntegrationTests"/>, against a real server.
/// </summary>
public sealed class TransferQueueTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static readonly SshEndpoint Nowhere = new("sftp.example.invalid", 22, "deploy");

    private static async Task<TransferJob> UntilAsync(TransferQueue queue, TransferJob job, Func<TransferJob, bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition(job))
        {
            if (clock.Elapsed > Patience) Assert.Fail($"The job stayed {job.State}.");
            await Task.Delay(10);
        }

        return job;
    }

    // Waits for ever, until the queue gives up on it.
    private static async Task<SftpConnection> NeverConnectsAsync(CancellationToken cancellation)
    {
        await Task.Delay(Timeout.Infinite, cancellation);
        throw new InvalidOperationException("Unreachable.");
    }

    public static TheoryData<Exception> LoginProblems =>
    [
        new LoginRefusedException(Nowhere, "Permission denied (publickey)."),
        new HostKeyRejectedException(Nowhere, new HostKeyInfo("ssh-ed25519", "SHA256:x")),
        new KeyPassphraseNeededException(Nowhere, "The key needs its passphrase."),
    ];

    [Theory]
    [MemberData(nameof(LoginProblems))]
    public async Task Login_ThatNeedsTheUser_FailsTheJobsOnceInsteadOfRetrying(Exception problem)
    {
        int attempts = 0;
        await using var queue = new TransferQueue(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<SftpConnection>(problem);
        });

        TransferJob job = queue.Upload([Path.Combine(TestData.Root, "nothing.txt")], "/srv");
        await UntilAsync(queue, job, j => j.IsFinished);
        await Task.Delay(300);

        Assert.Equal(TransferState.Failed, job.State);
        Assert.Equal(problem.Message, Assert.Single(job.Errors));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Connection_ThatCannotBeMade_IsTriedAgainAndSaysSo()
    {
        int attempts = 0;
        await using var queue = new TransferQueue(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<SftpConnection>(new System.Net.Sockets.SocketException(10061));
        });

        TransferJob job = queue.Download([new RemoteEntry("a.txt", "/srv/a.txt", 3, DateTime.UtcNow, false, false, "rw-r--r--")], Path.Combine(TestData.Root, "downloads"));
        await UntilAsync(queue, job, j => j.Note is not null);

        Assert.False(job.IsFinished);
        Assert.True(attempts >= 1);
    }

    [Fact]
    public async Task Retry_AfterAFailure_TriesAgainFromTheTop()
    {
        int attempts = 0;
        await using var queue = new TransferQueue(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<SftpConnection>(new LoginRefusedException(Nowhere, "no"));
        });
        TransferJob job = queue.Upload([Path.Combine(TestData.Root, "nothing.txt")], "/srv");
        await UntilAsync(queue, job, j => j.State == TransferState.Failed);

        queue.Retry(job);
        await UntilAsync(queue, job, _ => Volatile.Read(ref attempts) == 2);
        await UntilAsync(queue, job, j => j.State == TransferState.Failed);

        Assert.Single(job.Errors);
    }

    [Fact]
    public async Task Cancel_OfAJobWaitingToConnect_EndsItAndRemoveFinishedForgetsIt()
    {
        await using var queue = new TransferQueue(NeverConnectsAsync);
        TransferJob job = queue.Upload([Path.Combine(TestData.Root, "nothing.txt")], "/srv");

        queue.Cancel(job);
        queue.Pause(job);
        queue.Resume(job);

        Assert.Equal(TransferState.Canceled, job.State);
        queue.RemoveFinished();
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Copy_OnAQueueForOneServer_IsRefused()
    {
        await using var queue = new TransferQueue(NeverConnectsAsync);

        Assert.Throws<InvalidOperationException>(() => queue.Copy([new RemoteEntry("a", "/a", 1, DateTime.UtcNow, false, false, "")], "/b"));
    }

    // ---- Names a server chose -------------------------------------------------------------------

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("../../.ssh/authorized_keys", ".._.._.ssh_authorized_keys")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("", "_")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("bell\u0007\nname", "bell__name")]
    public void LocalName_FromTheServer_StaysInsideTheFolder(string name, string expected) =>
        Assert.Equal(expected, LocalNames.Safe(name));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("report.   ", "report")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    public void LocalName_ThatWindowsWouldMisread_IsMadeSafeThere(string name, string onWindows) =>
        Assert.Equal(OperatingSystem.IsWindows() ? onWindows : name, LocalNames.Safe(name));

    [Fact]
    public void LocalName_OfAnything_NeverLeadsOutOfTheFolder()
    {
        string folder = Path.GetFullPath(Path.Combine(TestData.Root, "downloads"));
        var random = new Random(2718);
        for (int round = 0; round < 20_000; round++)
        {
            const string Alphabet = "./\\:*?<>|\"\0\u0001 aZ.~";
            string name = new([.. Enumerable.Range(0, random.Next(12)).Select(_ => Alphabet[random.Next(Alphabet.Length)])]);

            string safe = LocalNames.Safe(name);
            string path = Path.GetFullPath(Path.Combine(folder, safe));

            Assert.True(safe.Length > 0 && safe is not ("." or ".."), $"\"{name}\" became \"{safe}\"");
            Assert.Equal(folder, Path.GetDirectoryName(path));
            Assert.DoesNotContain('\0', safe);
        }
    }
}
