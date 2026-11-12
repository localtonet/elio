using Elio.Core;

namespace Elio.UI;

/// <summary>
/// Decides when Elio is locked behind the device's own check: fingerprint, face or passcode. Only the deciding
/// is here; asking the device and covering the screen are done by whoever is told to.
/// </summary>
/// <remarks>
/// The clock and the check are handed in, so every rule here can be tried without a phone. This is a screen lock
/// on top of the vault, which keeps its own protection either way.
/// </remarks>
public sealed class AppLock
{
    /// <summary>
    /// How long Elio may be out of sight and still come back open. Long enough to copy a password from another
    /// app, short enough that a phone put down and picked up by someone else asks again.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _isOn;
    private readonly Action<bool> _setOn;
    private readonly Func<Func<string, Task<bool>>?> _authenticator;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _leftAt;
    private bool _authenticating;
    private bool _askedByItself;
    private bool _leftForTheCheck;

    /// <param name="isOn">Whether the user turned the lock on.</param>
    /// <param name="setOn">Saves that choice.</param>
    /// <param name="authenticator">The device's check, or null where there is none. Asked for each time, since a platform head sets it after this is made.</param>
    /// <param name="now">The time of day. Not a stopwatch: on a phone those stand still while the phone sleeps.</param>
    public AppLock(Func<bool> isOn, Action<bool> setOn, Func<Func<string, Task<bool>>?> authenticator, Func<DateTimeOffset> now)
    {
        _isOn = isOn;
        _setOn = setOn;
        _authenticator = authenticator;
        _now = now;
    }

    /// <summary>Whether the lock is in use: turned on, and on a device that can do the check. Without a check nothing could ever unlock it.</summary>
    public bool IsOn => _isOn() && _authenticator() is not null;

    /// <summary>Whether nothing may be shown or done until the device's check succeeds.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// Whether Elio is out of sight with the lock on. Its content is covered then too, so the picture the system
    /// keeps of the app for its list of open apps shows nothing.
    /// </summary>
    public bool IsCovered { get; private set; }

    /// <summary>Whether the content has to be hidden right now, for either reason.</summary>
    public bool HidesContent => IsLocked || IsCovered;

    /// <summary>Raised whenever <see cref="IsLocked"/> or <see cref="IsCovered"/> changed. Not always on the thread the windows live on.</summary>
    public event Action? Changed;

    /// <summary>Locks at start-up when the lock is on. Called before anything is shown.</summary>
    public void Start()
    {
        if (IsOn) Lock();
    }

    /// <summary>Elio went out of sight. The time is noted, and the content covered.</summary>
    public void WentToBackground()
    {
        // The device's own prompt can take the screen away for a moment. Coming back from that is not the user
        // returning, and must not raise the prompt again, or cancelling it would never end.
        _leftForTheCheck = _authenticating;
        if (!IsOn) return;

        // The first time counts: leaving twice without coming back in between must not start the wait over.
        _leftAt ??= _now();
        if (IsCovered) return;

        IsCovered = true;
        Changed?.Invoke();
    }

    /// <summary>Elio is in front again. Locks if it was away for longer than <see cref="GracePeriod"/>, and asks for the check once.</summary>
    public void CameToForeground()
    {
        DateTimeOffset? leftAt = _leftAt;
        _leftAt = null;
        bool changed = IsCovered;
        IsCovered = false;

        if (IsOn && !IsLocked && leftAt is { } left)
        {
            // A clock that went backwards says nothing about how long it really was, so that locks too.
            TimeSpan away = _now() - left;
            if (away > GracePeriod || away < TimeSpan.Zero)
            {
                IsLocked = true;
                _askedByItself = false;
                changed = true;
            }
        }

        // Locked, left and back again: the user is at the lock screen anew, and is asked anew.
        if (!_leftForTheCheck) _askedByItself = false;
        _leftForTheCheck = false;
        if (changed) Changed?.Invoke();
        AskOnce();
    }

    /// <summary>The lock screen is on show. Asks for the check, unless that was already done for this showing.</summary>
    public void ShownToUser() => AskOnce();

    // Once by itself, so that the user who cancels is left with the button and not with a prompt that keeps coming back.
    private void AskOnce()
    {
        if (!IsLocked || IsCovered || _authenticating || _askedByItself) return;

        _askedByItself = true;
        _ = UnlockAsync();
    }

    /// <summary>Asks the device to check who is holding it, and unlocks if it says yes. False leaves everything locked.</summary>
    public async Task<bool> UnlockAsync()
    {
        if (!IsLocked) return true;
        if (_authenticating || !await AuthenticateAsync(L.T("Unlock Elio"))) return false;

        IsLocked = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Turns the lock on or off, after the device's check either way: on, so that nobody is locked out by a check
    /// that does not work on this device; off, so that someone handed the open phone for a moment cannot remove it.
    /// </summary>
    /// <returns>True if the lock is now as asked.</returns>
    public async Task<bool> SetOnAsync(bool on)
    {
        if (_isOn() == on) return true;
        if (_authenticating || !await AuthenticateAsync(L.T("Confirm that it is you to change the lock"))) return false;

        _setOn(on);

        // Turned on, the user has just been checked, so it stays open until Elio has been away.
        if (!on && HidesContent)
        {
            IsLocked = false;
            IsCovered = false;
            Changed?.Invoke();
        }

        return true;
    }

    private async Task<bool> AuthenticateAsync(string reason)
    {
        if (_authenticator() is not { } authenticate) return false;

        _authenticating = true;
        try
        {
            return await authenticate(reason);
        }
        finally
        {
            _authenticating = false;
        }
    }

    private void Lock()
    {
        IsLocked = true;
        _askedByItself = false;
        Changed?.Invoke();
    }
}
