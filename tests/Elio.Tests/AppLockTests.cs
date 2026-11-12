using Elio.UI;

namespace Elio.Tests;

/// <summary>The rules of the app lock, with a clock that is moved by hand and a device check that answers as told.</summary>
public sealed class AppLockTests
{
    private sealed class Device
    {
        private TaskCompletionSource<bool>? _waiting;

        public bool SettingOn { get; set; }

        public bool HasCheck { get; set; } = true;

        /// <summary>What the check answers. Null leaves the prompt up until <see cref="Answer"/>.</summary>
        public bool? Answers { get; set; } = true;

        public int Prompts { get; private set; }

        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public AppLock Lock { get; }

        public int Changes { get; private set; }

        public Device(bool settingOn = true)
        {
            SettingOn = settingOn;
            Lock = new AppLock(() => SettingOn, on => SettingOn = on, () => HasCheck ? Check : null, () => Now);
            Lock.Changed += () => Changes++;
        }

        private Task<bool> Check(string reason)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Prompts++;
            if (Answers is { } answer) return Task.FromResult(answer);

            _waiting = new TaskCompletionSource<bool>();
            return _waiting.Task;
        }

        public void Answer(bool confirmed) => _waiting!.SetResult(confirmed);

        public void Away(TimeSpan time)
        {
            Lock.WentToBackground();
            Now += time;
            Lock.CameToForeground();
        }
    }

    [Fact]
    public void Start_WithTheSettingOn_IsLockedBeforeAnythingIsAsked()
    {
        var device = new Device();
        device.Lock.Start();

        Assert.True(device.Lock.IsLocked);
        Assert.True(device.Lock.HidesContent);
        Assert.Equal(0, device.Prompts);
    }

    [Fact]
    public void Start_WithTheSettingOff_IsOpen()
    {
        var device = new Device(settingOn: false);
        device.Lock.Start();

        Assert.False(device.Lock.HidesContent);
    }

    [Fact]
    public void Start_WhereTheDeviceCannotCheck_IsOpenEvenWithTheSettingOn()
    {
        var device = new Device { HasCheck = false };
        device.Lock.Start();
        device.Away(TimeSpan.FromHours(1));

        Assert.False(device.Lock.IsOn);
        Assert.False(device.Lock.HidesContent);
    }

    [Fact]
    public async Task Unlock_WhenTheCheckSucceeds_Opens()
    {
        var device = new Device();
        device.Lock.Start();

        Assert.True(await device.Lock.UnlockAsync());
        Assert.False(device.Lock.IsLocked);
        Assert.Equal(1, device.Prompts);
    }

    [Fact]
    public async Task Unlock_WhenTheCheckFailsOrIsCancelled_StaysLocked_AndCanBeTriedAgain()
    {
        var device = new Device { Answers = false };
        device.Lock.Start();

        Assert.False(await device.Lock.UnlockAsync());
        Assert.True(device.Lock.IsLocked);

        device.Answers = true;
        Assert.True(await device.Lock.UnlockAsync());
        Assert.False(device.Lock.IsLocked);
    }

    [Fact]
    public async Task Unlock_WhenTheCheckThrows_StaysLocked()
    {
        var failing = new AppLock(() => true, _ => { }, () => _ => throw new InvalidOperationException(), () => DateTimeOffset.UnixEpoch);
        failing.Start();

        await Assert.ThrowsAsync<InvalidOperationException>(failing.UnlockAsync);
        Assert.True(failing.IsLocked);
    }

    [Fact]
    public async Task Unlock_WhileOnePromptIsUp_DoesNotRaiseASecond()
    {
        var device = new Device { Answers = null };
        device.Lock.Start();

        Task<bool> first = device.Lock.UnlockAsync();
        Assert.False(await device.Lock.UnlockAsync());
        Assert.Equal(1, device.Prompts);

        device.Answer(true);
        Assert.True(await first);
        Assert.False(device.Lock.IsLocked);
    }

    [Fact]
    public void LockScreenShown_AsksOnce_AndNotAgainAfterACancel()
    {
        var device = new Device { Answers = false };
        device.Lock.Start();

        device.Lock.ShownToUser();
        device.Lock.ShownToUser();

        Assert.Equal(1, device.Prompts);
        Assert.True(device.Lock.IsLocked);
    }

    [Fact]
    public async Task Background_WithinTheGracePeriod_ComesBackOpen()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();

        device.Away(AppLock.GracePeriod);

        Assert.False(device.Lock.HidesContent);
        Assert.Equal(1, device.Prompts);
    }

    [Fact]
    public async Task Background_LongerThanTheGracePeriod_Locks_AndAsksOnce()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();
        device.Answers = false;

        device.Away(AppLock.GracePeriod + TimeSpan.FromSeconds(1));

        Assert.True(device.Lock.IsLocked);
        Assert.Equal(2, device.Prompts);
    }

    [Fact]
    public async Task Background_ClockSetBack_Locks()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();
        device.Answers = false;

        device.Away(TimeSpan.FromHours(-2));

        Assert.True(device.Lock.IsLocked);
    }

    [Fact]
    public async Task Background_CoversTheContentWhileAway_WithoutAsking()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();

        device.Lock.WentToBackground();

        Assert.True(device.Lock.IsCovered);
        Assert.True(device.Lock.HidesContent);
        Assert.False(device.Lock.IsLocked);
        Assert.Equal(1, device.Prompts);
    }

    [Fact]
    public async Task Background_LeavingTwice_CountsFromTheFirstTime()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();
        device.Answers = false;

        device.Lock.WentToBackground();
        device.Now += TimeSpan.FromSeconds(50);
        device.Lock.WentToBackground();
        device.Now += TimeSpan.FromSeconds(50);
        device.Lock.CameToForeground();

        Assert.True(device.Lock.IsLocked);
    }

    [Fact]
    public void Background_WithTheSettingOff_NeitherCoversNorLocks()
    {
        var device = new Device(settingOn: false);
        device.Lock.Start();

        device.Lock.WentToBackground();
        Assert.False(device.Lock.HidesContent);
        device.Now += TimeSpan.FromDays(1);
        device.Lock.CameToForeground();

        Assert.False(device.Lock.HidesContent);
        Assert.Equal(0, device.Prompts);
    }

    [Fact]
    public void ComingBackWhileLocked_AfterTheUserLeft_AsksAgain()
    {
        var device = new Device { Answers = false };
        device.Lock.Start();
        device.Lock.ShownToUser();

        device.Away(TimeSpan.FromSeconds(5));

        Assert.Equal(2, device.Prompts);
        Assert.True(device.Lock.IsLocked);
    }

    [Fact]
    public void ComingBackFromThePromptItself_DoesNotAskAgain()
    {
        // Some phones take the app off the screen to show their own check. Cancelling it must end at the button.
        var device = new Device { Answers = null };
        device.Lock.Start();
        device.Lock.ShownToUser();

        device.Lock.WentToBackground();
        device.Answer(false);
        device.Lock.CameToForeground();

        Assert.Equal(1, device.Prompts);
        Assert.True(device.Lock.IsLocked);
        Assert.False(device.Lock.IsCovered);
    }

    [Fact]
    public async Task TurningOn_NeedsTheCheck_AndDoesNotLockTheUserWhoJustPassedIt()
    {
        var device = new Device(settingOn: false) { Answers = false };

        Assert.False(await device.Lock.SetOnAsync(true));
        Assert.False(device.SettingOn);

        device.Answers = true;
        Assert.True(await device.Lock.SetOnAsync(true));
        Assert.True(device.SettingOn);
        Assert.False(device.Lock.HidesContent);
        Assert.Equal(2, device.Prompts);
    }

    [Fact]
    public async Task TurningOn_WhereTheDeviceCannotCheck_IsRefused()
    {
        var device = new Device(settingOn: false) { HasCheck = false };

        Assert.False(await device.Lock.SetOnAsync(true));
        Assert.False(device.SettingOn);
    }

    [Fact]
    public async Task TurningOff_NeedsTheCheck()
    {
        var device = new Device();
        device.Lock.Start();
        await device.Lock.UnlockAsync();
        device.Answers = false;

        Assert.False(await device.Lock.SetOnAsync(false));
        Assert.True(device.SettingOn);

        device.Answers = true;
        Assert.True(await device.Lock.SetOnAsync(false));
        Assert.False(device.SettingOn);

        device.Away(TimeSpan.FromHours(1));
        Assert.False(device.Lock.HidesContent);
    }

    [Fact]
    public async Task SettingItToWhatItAlreadyIs_AsksNothing()
    {
        var device = new Device(settingOn: false);

        Assert.True(await device.Lock.SetOnAsync(false));
        Assert.Equal(0, device.Prompts);
    }

    [Fact]
    public async Task Changed_IsRaisedForLockingCoveringAndUnlocking()
    {
        var device = new Device();
        device.Lock.Start();
        Assert.Equal(1, device.Changes);

        await device.Lock.UnlockAsync();
        Assert.Equal(2, device.Changes);

        device.Lock.WentToBackground();
        Assert.Equal(3, device.Changes);

        device.Lock.CameToForeground();
        Assert.Equal(4, device.Changes);
    }
}
