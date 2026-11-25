using System.Text.Json;

namespace Elio.Core;

/// <summary>Copies of server entries, for opening a server in another way without changing the saved one.</summary>
public static class HostCopy
{
    /// <summary>
    /// A copy that shares nothing with the original: its lists, proxy and serial settings are copies too, so changing
    /// one leaves the other as it was. Every setting comes along, the ones added after this was written included,
    /// because the copy is made the way the library is saved. It still has the original's id, for the caller to change.
    /// </summary>
    public static HostEntry Of(HostEntry host) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(host, CoreJsonContext.Default.HostEntry), CoreJsonContext.Default.HostEntry)
        ?? throw new InvalidOperationException("A server could not be copied.");
}
