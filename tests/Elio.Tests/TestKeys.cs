using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// Stands in for DPAPI, the system keychain or a phone's key store when a test opens the vault, so that no test
/// ever touches the real ones. Keys it wrapped open only with a protector of the same number, the way a key
/// file copied from another computer does not open on this one.
/// </summary>
internal sealed class TestKeyProtector(byte device = 1) : IKeyProtector
{
    public int Opened { get; private set; }

    public byte[]? Protect(byte[] key) => [device, .. key.Select(static b => (byte)(b ^ 0x5A))];

    public byte[]? Unprotect(byte[] stored)
    {
        if (stored.Length == 0 || stored[0] != device) return null;
        Opened++;
        return [.. stored.Skip(1).Select(static b => (byte)(b ^ 0x5A))];
    }
}

/// <summary>Has every vault made while it lives use a test protector, and puts back what was there before.</summary>
internal sealed class TestVaultKeys : IDisposable
{
    private readonly IKeyProtector? _before = VaultKeys.Platform;

    public TestVaultKeys(IKeyProtector? protector = null) => VaultKeys.Platform = protector ?? new TestKeyProtector();

    public void Dispose() => VaultKeys.Platform = _before;
}
