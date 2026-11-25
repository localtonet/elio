namespace Elio.Connections;

/// <summary>
/// One SSH connection to a server, shared by every terminal opened to it while it is up, as OpenSSH's ControlMaster
/// does: the second tab opens at once, without a second login, a second host key check or a second prompt for a
/// one-time code. It is closed when the last terminal using it closes. Only servers set to share use it.
/// </summary>
internal static class SshConnectionPool
{
    internal sealed class Entry(string key, SshChain chain)
    {
        public string Key { get; } = key;
        public SshChain Chain { get; } = chain;
        public int Users;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    // One opening at a time per server, so two tabs opened together share one connection rather than racing to make two.
    private static readonly Dictionary<string, SemaphoreSlim> Openings = new(StringComparer.Ordinal);

    /// <summary>A terminal's hold on a shared connection. Disposing it lets go; the last to let go closes the connection.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly Entry _entry;
        private int _released;

        internal Lease(Entry entry, bool opened) => (_entry, Opened) = (entry, opened);

        public SshChain Chain => _entry.Chain;

        /// <summary>Whether this lease opened the connection, rather than joining one that was open.</summary>
        public bool Opened { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            bool last;
            lock (Gate)
            {
                last = --_entry.Users == 0;
                if (last && Entries.TryGetValue(_entry.Key, out Entry? current) && ReferenceEquals(current, _entry)) Entries.Remove(_entry.Key);
            }

            if (last) _entry.Chain.Dispose();
        }
    }

    /// <summary>
    /// What makes two paths the same connection: every server along the way, its port and user, and the proxy in front.
    /// The password is not part of it; the server already took one for this connection.
    /// </summary>
    internal static string KeyOf(IReadOnlyList<SshEndpoint> path)
    {
        string servers = string.Join(" > ", path.Select(e => $"{e.User}@{e.Host}:{e.Port}"));
        return path.Count > 0 && path[0].Proxy is { } proxy ? $"{servers} via {proxy.Kind} {proxy.Host}:{proxy.Port} {proxy.Command}" : servers;
    }

    public static async Task<Lease> AcquireAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, CancellationToken cancellation)
    {
        string key = KeyOf(path);
        SemaphoreSlim opening;
        lock (Gate)
        {
            if (!Openings.TryGetValue(key, out opening!)) Openings[key] = opening = new SemaphoreSlim(1, 1);
        }

        await opening.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (Gate)
            {
                // A connection that went down is not joined; its terminals let go of it on their own.
                if (Entries.TryGetValue(key, out Entry? open) && open.Chain.Last.IsConnected)
                {
                    open.Users++;
                    return new Lease(open, opened: false);
                }

                Entries.Remove(key);
            }

            SshChain chain = await SshChain.OpenAsync(path, policy, cancellation).ConfigureAwait(false);
            var entry = new Entry(key, chain) { Users = 1 };
            lock (Gate) Entries[key] = entry;
            return new Lease(entry, opened: true);
        }
        finally
        {
            opening.Release();
        }
    }

    /// <summary>How many connections are being shared, for tests.</summary>
    internal static int OpenCount
    {
        get
        {
            lock (Gate) return Entries.Count;
        }
    }
}
