using Elio.Connections;

namespace Elio.Tests;

/// <summary>
/// The speed limit of transfers, on a clock the test moves by hand. Waiting for a moment on it runs the rest of the
/// transfer straight away when the clock gets there, so nothing here depends on how fast the machine is.
/// </summary>
public sealed class TransferThrottleTests
{
    private const int KiB = 1024;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    /// <summary>A clock that only moves when told to, and wakes whoever was waiting for the time it reaches.</summary>
    private sealed class ManualClock
    {
        private readonly List<(long Due, TaskCompletionSource Done)> _sleepers = [];

        public long Now { get; private set; }

        public int Sleeps { get; private set; }

        public TimeSpan Elapsed => TimeSpan.FromTicks(Now);

        public Task Delay(TimeSpan span, CancellationToken cancellation)
        {
            Sleeps++;

            // Without asynchronous continuations: whoever waited carries on inside Advance, before it returns.
            var done = new TaskCompletionSource();
            if (cancellation.IsCancellationRequested)
            {
                done.SetCanceled(cancellation);
                return done.Task;
            }

            cancellation.Register(() => done.TrySetCanceled(cancellation));
            _sleepers.Add((Now + span.Ticks, done));
            return done.Task;
        }

        public void Advance(TimeSpan by)
        {
            Now += by.Ticks;
            List<(long Due, TaskCompletionSource Done)> due = _sleepers.Where(s => s.Due <= Now).ToList();
            _sleepers.RemoveAll(s => s.Due <= Now);
            foreach ((_, TaskCompletionSource done) in due) done.TrySetResult();
        }

        /// <summary>Moves the clock on a little at a time until the work is done, and says how long that took.</summary>
        public TimeSpan RunUntil(Task work, TimeSpan most)
        {
            while (!work.IsCompleted && Elapsed < most) Advance(Step);
            Assert.True(work.IsCompletedSuccessfully, $"Still not done after {Elapsed.TotalSeconds} s.");
            return Elapsed;
        }
    }

    /// <summary>
    /// Runs a test where an await carries on at once on the thread that ended the wait. The test runner's own threads
    /// have a context that sends every such continuation to the thread pool, where it would race the clock.
    /// </summary>
    private static Task Plainly(Action test) => Task.Run(test);

    private static TransferThrottle Throttle(ManualClock clock, long bytesPerSecond) =>
        new(() => clock.Now, TimeSpan.TicksPerSecond, clock.Delay) { BytesPerSecond = bytesPerSecond };

    // Sends this much the way an upload does: in the pieces the limits call for, each waiting for its turn first.
    private static async Task SendAsync(IReadOnlyList<TransferThrottle> limits, long total, Action<int>? sent = null)
    {
        long done = 0;
        while (done < total)
        {
            int piece = (int)Math.Min(TransferThrottle.PieceFor(limits, 32 * KiB), total - done);
            await TransferThrottle.WaitAsync(limits, piece, CancellationToken.None).ConfigureAwait(false);
            done += piece;
            sent?.Invoke(piece);
        }
    }

    [Fact]
    public Task NoLimit_NeverWaits() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 0);

        for (int i = 0; i < 1000; i++) Assert.True(throttle.WaitAsync(1024 * KiB, CancellationToken.None).IsCompletedSuccessfully);

        Assert.Equal(0, clock.Sleeps);
        Assert.Equal(32 * KiB, throttle.PieceFor(32 * KiB));
    });

    [Fact]
    public Task OneTransfer_GoesAtTheLimit() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 100 * KiB);
        int pieces = 0;

        TimeSpan took = clock.RunUntil(SendAsync([throttle], 1024 * KiB, _ => pieces++), TimeSpan.FromMinutes(1));

        // A mebibyte at 100 KiB a second is 10.24 seconds.
        Assert.InRange(took.TotalSeconds, 10.2, 10.3);

        // Each piece slept once at most: nothing went round and round asking.
        Assert.True(clock.Sleeps <= pieces, $"{clock.Sleeps} sleeps for {pieces} pieces.");
    });

    [Fact]
    public Task TransfersAtOnce_ShareTheLimit_AndTakeTurns() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 100 * KiB);
        TimeSpan? firstDone = null;

        async Task SendAndNoteAsync()
        {
            await SendAsync([throttle], 512 * KiB).ConfigureAwait(false);
            firstDone ??= clock.Elapsed;
        }

        TimeSpan took = clock.RunUntil(Task.WhenAll(SendAndNoteAsync(), SendAndNoteAsync()), TimeSpan.FromMinutes(1));

        // Together they get the limit, not each of them; and neither is starved, so both end near the end.
        Assert.InRange(took.TotalSeconds, 10.2, 10.3);
        Assert.InRange(firstDone!.Value.TotalSeconds, 9.9, 10.3);
    });

    [Fact]
    public Task ACopyBetweenServers_GoesByTheLowerOfTheTwoLimits() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle downloads = Throttle(clock, 50 * KiB);
        TransferThrottle uploads = Throttle(clock, 200 * KiB);

        TimeSpan took = clock.RunUntil(SendAsync([downloads, uploads], 512 * KiB), TimeSpan.FromMinutes(1));

        Assert.InRange(took.TotalSeconds, 10.2, 10.4);
    });

    [Fact]
    public Task LiftingTheLimit_WakesWhoeverIsWaiting() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 1 * KiB);
        Task waiting = throttle.WaitAsync(64 * KiB, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);

        throttle.BytesPerSecond = 0;

        Assert.True(waiting.IsCompletedSuccessfully);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    });

    [Fact]
    public Task ANewLimit_TakesHoldAtOnce() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 1 * KiB);
        Task waiting = throttle.WaitAsync(10 * KiB, CancellationToken.None).AsTask();

        // Ten seconds at the old limit, one at the new.
        throttle.BytesPerSecond = 10 * KiB;
        TimeSpan took = clock.RunUntil(waiting, TimeSpan.FromSeconds(20));

        Assert.InRange(took.TotalSeconds, 0.99, 1.01);
    });

    [Fact]
    public Task AWaitThatIsCancelled_GivesItsBytesBack() => Plainly(() =>
    {
        var clock = new ManualClock();
        TransferThrottle throttle = Throttle(clock, 10 * KiB);
        using var stop = new CancellationTokenSource();

        Task first = throttle.WaitAsync(10 * KiB, stop.Token).AsTask();
        stop.Cancel();
        Assert.True(first.IsCanceled);

        // Had the cancelled bytes stayed owed, this would wait two seconds.
        TimeSpan took = clock.RunUntil(throttle.WaitAsync(10 * KiB, CancellationToken.None).AsTask(), TimeSpan.FromSeconds(5));
        Assert.InRange(took.TotalSeconds, 0.99, 1.01);
    });

    [Fact]
    public void Pieces_AreSmallWhileHeldBack()
    {
        var clock = new ManualClock();

        Assert.Equal(1024, Throttle(clock, 1 * KiB).PieceFor(32 * KiB));
        Assert.Equal(10 * KiB, Throttle(clock, 100 * KiB).PieceFor(32 * KiB));
        Assert.Equal(32 * KiB, Throttle(clock, 100 * 1024 * KiB).PieceFor(32 * KiB));
        Assert.Equal(16 * KiB, TransferThrottle.PieceFor([Throttle(clock, 1024 * KiB), Throttle(clock, 160 * KiB)], 32 * KiB));
    }

    [Fact]
    public void ANegativeLimit_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferThrottle { BytesPerSecond = -1 });

    private static async Task<SftpConnection> NeverConnectsAsync(CancellationToken cancellation)
    {
        await Task.Delay(Timeout.Infinite, cancellation);
        throw new InvalidOperationException("Unreachable.");
    }

    [Fact]
    public async Task TheQueue_GivesEachKindOfTransferItsLimits()
    {
        TransferThrottle down = new(), up = new();
        await using var queue = new TransferQueue(NeverConnectsAsync, NeverConnectsAsync) { DownloadLimit = down, UploadLimit = up };

        Assert.Equal([down], queue.LimitsFor(TransferKind.Download));
        Assert.Equal([up], queue.LimitsFor(TransferKind.Upload));
        Assert.Equal([down, up], queue.LimitsFor(TransferKind.Copy));
    }

    [Fact]
    public async Task TheQueue_SharesTheLimitsWithEveryOtherQueue()
    {
        await using var one = new TransferQueue(NeverConnectsAsync);
        await using var other = new TransferQueue(NeverConnectsAsync);

        Assert.Same(TransferThrottle.Downloads, one.DownloadLimit);
        Assert.Same(one.UploadLimit, other.UploadLimit);
    }

    [Fact]
    public async Task AJobWhoseConflictsWereDecided_DoesNotAsk()
    {
        await using var queue = new TransferQueue(NeverConnectsAsync);

        TransferJob decided = queue.Upload([Path.Combine(TestData.Root, "a.txt")], "/srv", whenExists: ConflictChoice.Overwrite);
        TransferJob asks = queue.Download([new RemoteEntry("b.txt", "/srv/b.txt", 1, DateTime.UtcNow, false, false, "rw-r--r--")], TestData.Root);

        Assert.Equal(ConflictChoice.Overwrite, decided.DecisionForAll);
        Assert.Null(asks.DecisionForAll);
    }
}
