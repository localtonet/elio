namespace Elio.Core;

public enum HostKeyStatus
{
    Trusted,
    Unknown,
    Changed,
}

/// <summary>Remembers each server's key, so a server pretending to be another one is caught.</summary>
public sealed class KnownHosts
{
    private readonly JsonFileStore<KnownHostsFile> _store = Stores.KnownHosts();
    private readonly KnownHostsFile _file;

    public KnownHosts() => _file = _store.Load();

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    public HostKeyStatus Check(string host, int port, string fingerprint) =>
        !_file.Hosts.TryGetValue(Key(host, port), out KnownHostKey? known) ? HostKeyStatus.Unknown
        : known.Fingerprint == fingerprint ? HostKeyStatus.Trusted
        : HostKeyStatus.Changed;

    public string? Fingerprint(string host, int port) =>
        _file.Hosts.TryGetValue(Key(host, port), out KnownHostKey? known) ? known.Fingerprint : null;

    /// <summary>Every remembered key, by "host:port".</summary>
    public IReadOnlyDictionary<string, KnownHostKey> Entries => _file.Hosts;

    /// <summary>
    /// Takes in keys learned elsewhere, for servers this computer does not know yet. A key it already has is never
    /// replaced by one from elsewhere: if the server's key really changed, that is found out on connecting, where
    /// the user is asked, and a changed key that only arrived from another device would otherwise pass unnoticed.
    /// </summary>
    public void Merge(IReadOnlyDictionary<string, KnownHostKey> other)
    {
        bool changed = false;
        foreach (var (key, value) in other)
        {
            if (_file.Hosts.ContainsKey(key)) continue;
            _file.Hosts[key] = value;
            changed = true;
        }

        if (changed) _store.Save(_file);
    }

    public void Trust(string host, int port, string algorithm, string fingerprint)
    {
        _file.Hosts[Key(host, port)] = new KnownHostKey { Algorithm = algorithm, Fingerprint = fingerprint, AddedAt = DateTimeOffset.UtcNow };
        _store.Save(_file);
    }
}
