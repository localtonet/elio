using Elio.Core;

namespace Elio.Localtonet;

/// <summary>
/// How the devices Localtonet lists become the entries Elio connects with. The list is asked for again and
/// again, and the entries are made anew each time, so what the user decided about a device on this computer
/// has to be carried over from the entry it had before.
/// </summary>
public static class DeviceEntries
{
    /// <summary>
    /// The entries for one account's devices, ordered by the name each is shown under.
    /// </summary>
    /// <param name="previous">The entries made the last time, by device id. A device not in it has had nothing decided yet.</param>
    /// <param name="hasAccessKey">Whether an access key is saved for the device.</param>
    public static List<HostEntry> Build(IEnumerable<LocaltonetDevice> devices, string accountId, IReadOnlyDictionary<Guid, HostEntry> previous, Func<Guid, bool> hasAccessKey)
    {
        var entries = new List<HostEntry>();
        foreach (LocaltonetDevice device in devices)
        {
            previous.TryGetValue(device.Id, out HostEntry? before);
            entries.Add(new HostEntry
            {
                Id = device.Id,

                // A name the user gave it stays, even when Localtonet's name for the computer changes. Otherwise it follows Localtonet's.
                Name = before is not null && IsRenamed(before) ? before.Name : device.Name,

                // What Localtonet knows the computer by, which is what connecting asks for.
                Address = device.Name,
                User = device.RunAsUser ?? "device",
                Localtonet = true,
                LocaltonetAccountId = accountId,
                AllowMcpCommands = before?.AllowMcpCommands == true,
                ModifiedUtc = before?.ModifiedUtc ?? default,

                // Without an access key every new connection is approved in the browser, which shouldn't pop up by itself.
                AutoReconnect = hasAccessKey(device.Id),
            });
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Whether the user gave the device a name of their own. <see cref="HostEntry.Name"/> is what Elio shows and
    /// <see cref="HostEntry.Address"/> is the device's name at Localtonet; they only differ when the user chose one.
    /// </summary>
    public static bool IsRenamed(HostEntry device) => !string.Equals(device.Name, device.Address, StringComparison.Ordinal);

    /// <summary>
    /// Gives the device the name it is shown under, or its own name at Localtonet when <paramref name="name"/> is empty,
    /// and says whether AI tools may run commands on it. Only the entry changes: connecting still goes by <see cref="HostEntry.Address"/>.
    /// </summary>
    public static void Customize(HostEntry device, string? name, bool allowMcpCommands)
    {
        device.Name = string.IsNullOrWhiteSpace(name) ? device.Address : name.Trim();
        device.AllowMcpCommands = allowMcpCommands;
        device.ModifiedUtc = DateTime.UtcNow;
    }
}
