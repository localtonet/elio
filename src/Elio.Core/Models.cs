using System.Text.Json.Serialization;

namespace Elio.Core;

/// <summary>What a saved entry connects to.</summary>
public enum HostKind
{
    Ssh,
    Telnet,
    Serial,
    Local,
}

public enum SerialParity
{
    None,
    Odd,
    Even,
}

public enum SerialFlow
{
    None,
    Hardware,
    Software,
}

/// <summary>What the Enter key sends down a serial line.</summary>
public enum SerialEnter
{
    Cr,
    Lf,
    CrLf,
}

public sealed class SerialSettings
{
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }

    /// <summary>One or two.</summary>
    public int StopBits { get; set; } = 1;

    public SerialFlow Flow { get; set; }
    public SerialEnter Enter { get; set; }

    /// <summary>Devices that end lines with a bare line feed would otherwise draw a staircase.</summary>
    public bool LfToCrLf { get; set; }

    /// <summary>Shows what arrives as a hex dump instead of text.</summary>
    public bool HexDump { get; set; }
}

public sealed class HostEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    public HostKind Kind { get; set; }

    /// <summary>The server's address. For a serial entry the port's name, for a local shell its command line.</summary>
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string User { get; set; } = string.Empty;
    public string? KeyPath { get; set; }

    /// <summary>
    /// The saved identity this server logs in with, in place of its own user name, key and password. Null means
    /// the server's own, or what its group says.
    /// </summary>
    public Guid? IdentityId { get; set; }
    public Guid? GroupId { get; set; }
    public List<string> Tags { get; set; } = [];

    /// <summary>Servers to pass through on the way, outermost first.</summary>
    public List<Guid> JumpHosts { get; set; } = [];

    public bool UseAgent { get; set; }

    /// <summary>Programs on the server may use the keys in this computer's SSH agent.</summary>
    public bool ForwardAgent { get; set; }

    /// <summary>Whether AI tools connected through `elio --mcp` may run commands on this server. The setting for all of them has to be on as well.</summary>
    public bool AllowMcpCommands { get; set; }

    /// <summary>Graphical programs on the server open their windows on this computer.</summary>
    public bool ForwardX11 { get; set; }

    /// <summary>Whether the shell runs over Mosh, which survives sleep and changing networks. SSH is still used to log in.</summary>
    public bool UseMosh { get; set; }

    /// <summary>Lines typed into the shell once it opens.</summary>
    public string? LoginScript { get; set; }

    /// <summary>Runs in place of the login shell, like ssh's RemoteCommand.</summary>
    public string? RemoteCommand { get; set; }

    public List<PortForward> Forwards { get; set; } = [];
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Line settings, for a serial entry.</summary>
    public SerialSettings? Serial { get; set; }

    /// <summary>Where a local shell starts. Null means the home folder.</summary>
    public string? Directory { get; set; }

    /// <summary>Variables set for a local shell, on top of the ones Elio itself has.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>A computer on the user's Localtonet account, reached through Localtonet. <see cref="Address"/> holds its device name.</summary>
    public bool Localtonet { get; set; }

    /// <summary>Which of the signed-in Localtonet accounts the computer belongs to.</summary>
    public string? LocaltonetAccountId { get; set; }

    /// <summary>The proxy the connection is made through. Null leaves it to the group, and without one there the connection is direct.</summary>
    public ProxySettings? Proxy { get; set; }

    /// <summary>When this entry last changed, so syncing can keep the newer copy.</summary>
    public DateTime ModifiedUtc { get; set; }
}

public enum ForwardKind
{
    Local,
    Remote,
    Dynamic,
}

public sealed class PortForward
{
    public ForwardKind Kind { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }

    /// <summary>Reads the form "L 8080:localhost:80", "R 9000:localhost:3000" or "D 1080".</summary>
    public static PortForward? TryParse(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2) return null;

        ForwardKind? kind = words[0].ToUpperInvariant() switch
        {
            "L" => ForwardKind.Local,
            "R" => ForwardKind.Remote,
            "D" => ForwardKind.Dynamic,
            _ => null,
        };
        return kind is ForwardKind found ? FromSpec(found, words[1]) : null;
    }

    /// <summary>Reads "[bind:]port:host:hostport", or "[bind:]port" for a dynamic forward.</summary>
    public static PortForward? FromSpec(ForwardKind kind, string spec)
    {
        if (Parts(spec) is not { } parts) return null;
        int bindParts = kind == ForwardKind.Dynamic ? parts.Length : parts.Length - 2;
        if (bindParts is < 1 or > 2) return null;
        if (!int.TryParse(parts[bindParts - 1], out int bindPort) || bindPort is < 0 or > 65535) return null;

        var forward = new PortForward { Kind = kind, BindPort = bindPort };
        if (bindParts == 2) forward.BindAddress = parts[0];
        if (kind == ForwardKind.Dynamic) return forward;

        if (!int.TryParse(parts[^1], out int targetPort) || targetPort is < 1 or > 65535 || parts[^2].Length == 0) return null;
        forward.TargetHost = parts[^2];
        forward.TargetPort = targetPort;
        return forward;
    }

    // Splits on the colons between the parts. An IPv6 address has colons of its own, so it is written in
    // brackets, "[::1]:8080", and comes out here without them.
    private static string[]? Parts(string spec)
    {
        var parts = new List<string>();
        int at = 0;
        while (true)
        {
            int end;
            if (at < spec.Length && spec[at] == '[')
            {
                int close = spec.IndexOf(']', at);
                if (close < 0 || close == at + 1) return null;
                parts.Add(spec[(at + 1)..close]);
                end = close + 1;
                if (end < spec.Length && spec[end] != ':') return null;
            }
            else
            {
                end = spec.IndexOf(':', at);
                if (end < 0) end = spec.Length;
                parts.Add(spec[at..end]);
            }

            if (end >= spec.Length) return [.. parts];
            at = end + 1;
        }
    }

    private static string Written(string address) => address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;

    public override string ToString() => Kind switch
    {
        ForwardKind.Dynamic => $"D {Written(BindAddress)}:{BindPort}",
        ForwardKind.Local => $"L {Written(BindAddress)}:{BindPort}:{Written(TargetHost)}:{TargetPort}",
        _ => $"R {Written(BindAddress)}:{BindPort}:{Written(TargetHost)}:{TargetPort}",
    };
}

public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>Text to mark in terminal output, with the color to mark it in.</summary>
public sealed class HighlightRule
{
    public string Pattern { get; set; } = string.Empty;
    public uint Color { get; set; }
}

public sealed class HostGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public bool Collapsed { get; set; }

    // What follows is what the group's servers connect with unless they say otherwise themselves. Each one left
    // as it starts out here means the group has nothing to say about it, which is all a group was before.

    /// <summary>The saved identity the group's servers log in with.</summary>
    public Guid? IdentityId { get; set; }

    /// <summary>The user name for servers that have none of their own.</summary>
    public string? User { get; set; }

    /// <summary>The port for SSH servers that have none of their own.</summary>
    public int? Port { get; set; }

    /// <summary>Servers to pass through on the way, outermost first, for servers that name none themselves.</summary>
    public List<Guid> JumpHosts { get; set; } = [];

    /// <summary>Whether the group's servers try the keys in the SSH agent.</summary>
    public bool? UseAgent { get; set; }

    /// <summary>Whether the group's servers run their shell over Mosh.</summary>
    public bool? UseMosh { get; set; }

    /// <summary>The proxy the group's servers connect through.</summary>
    public ProxySettings? Proxy { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>What kind of proxy stands between this computer and the first server.</summary>
public enum ProxyKind
{
    /// <summary>The connection is direct. On a server this also overrules a proxy its group names.</summary>
    None,
    Socks5,
    Http,
}

/// <summary>A proxy to connect through. Its password is kept in the vault, never here.</summary>
public sealed class ProxySettings
{
    public ProxyKind Kind { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string User { get; set; } = string.Empty;

    private static readonly byte[] SecretContext = "elio-proxy-secret"u8.ToArray();

    /// <summary>
    /// Where the vault keeps the proxy password of a server or group. The vault holds one secret per id and the
    /// owner's own id is taken by its login password, so this is a second id worked out from the first: the same
    /// on every device, which lets the password sync like any other.
    /// </summary>
    public static Guid SecretId(Guid owner) =>
        new(System.Security.Cryptography.SHA256.HashData([.. SecretContext, .. owner.ToByteArray()]).AsSpan(0, 16));
}

/// <summary>What an identity logs in with.</summary>
public enum IdentityKind
{
    Password,
    Key,
}

/// <summary>
/// A named way of logging in that any number of servers can share: a user name with a password or a private key.
/// The password, or the key and its passphrase, are in the vault under <see cref="Id"/>, never here.
/// </summary>
public sealed class Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>The user name to log in as. Empty leaves that to the server or its group, so one key can serve several accounts.</summary>
    public string User { get; set; } = string.Empty;

    public IdentityKind Kind { get; set; }

    /// <summary>
    /// The public half of the key, as the line that goes into authorized_keys. It is not a secret, and is kept here
    /// so that it can be shown without opening the vault.
    /// </summary>
    public string? PublicKey { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>What the vault holds for an identity, as one entry so that its parts cannot drift apart while syncing.</summary>
public sealed class IdentitySecret
{
    public string? Password { get; set; }

    /// <summary>The private key, as the text of a key file.</summary>
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }

    /// <summary>Whether there is anything to log in with.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKey);

    /// <summary>Reads what <see cref="Write"/> produced. Null for nothing, or for text that is not one.</summary>
    public static IdentitySecret? Read(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(stored, CoreJsonContext.Default.IdentitySecret);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public string Write() => System.Text.Json.JsonSerializer.Serialize(this, CoreJsonContext.Default.IdentitySecret);
}

/// <summary>Everything saved about the user's servers. Passwords never go in here.</summary>
public sealed class HostLibrary : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public List<HostEntry> Hosts { get; set; } = [];
    public List<HostGroup> Groups { get; set; } = [];
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>The keychain: ways of logging in that servers and groups point to.</summary>
    public List<Identity> Identities { get; set; } = [];

    /// <summary>When each removed host, group, snippet or identity was removed, so syncing doesn't bring it back.</summary>
    public Dictionary<Guid, DateTime> Deleted { get; set; } = [];
}

public sealed class KnownHostKey
{
    public string Algorithm { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class KnownHostsFile : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public Dictionary<string, KnownHostKey> Hosts { get; set; } = [];
}

public sealed class AppSettings : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public string Theme { get; set; } = "Elio";

    /// <summary>Whether the terminal switches between two themes as the operating system goes light or dark.</summary>
    public bool FollowSystemTheme { get; set; }

    /// <summary>The terminal theme used while the operating system is light, when it is being followed.</summary>
    public string LightTheme { get; set; } = "Elio Light";

    /// <summary>The language tag the interface is shown in. Null follows the operating system.</summary>
    public string? Language { get; set; }
    public double FontSize { get; set; } = 14;
    public bool Ligatures { get; set; } = true;
    public bool RestoreSessions { get; set; } = true;
    public bool CopyOnSelect { get; set; }

    /// <summary>Whether tabs working in the same git repository are kept next to each other.</summary>
    public bool GroupTabsByRepository { get; set; }

    /// <summary>Whether local shells keep running after the window closes, to be picked up by the next one.</summary>
    public bool KeepShellsRunning { get; set; }

    /// <summary>
    /// The server that passes shared terminals along, when it is not the sync server. Null uses the sync server, or the
    /// usual one where sync was never set up.
    /// </summary>
    public string? ShareServer { get; set; }

    /// <summary>How the find bar reads what is typed into it: as plain text, or as a regular expression.</summary>
    public bool FindWithRegex { get; set; }

    /// <summary>
    /// Whether commands typed before are offered again while typing. On unless turned off; it has a name of its own
    /// because an earlier setting for the same thing was off unless turned on, and files saved then still say so.
    /// </summary>
    public bool SuggestCommands { get; set; } = true;

    /// <summary>
    /// Whether suggestions start out with what the shell's own history file holds, on this computer and on a server.
    /// Off unless turned on, because that copies commands typed elsewhere into Elio's own files.
    /// </summary>
    public bool SeedSuggestionsFromShellHistory { get; set; }
    public bool RightClickPaste { get; set; }

    /// <summary>Whether the interface around the terminal is light. The terminal's own colors are its theme.</summary>
    public bool LightInterface { get; set; }

    /// <summary>
    /// Whether Elio stays shut until the device's own check succeeds: fingerprint, face or passcode. Only a phone
    /// offers it. It is a screen lock on top of the vault, which is protected the same with or without it.
    /// </summary>
    public bool LockApp { get; set; }

    /// <summary>Whether AI tools connected through `elio --mcp` may run commands on the saved servers.</summary>
    public bool McpRunCommands { get; set; }

    /// <summary>A key that shows or hides the window from anywhere, such as "Ctrl+Oem3". Empty means there is none.</summary>
    public string? SummonKey { get; set; }

    /// <summary>Whether the window drops down from the top of the screen when summoned.</summary>
    public bool SummonFromTop { get; set; }
    public List<HighlightRule> Highlights { get; set; } = [];

    /// <summary>The Localtonet account Elio is signed in to, for showing. The key itself is in the vault.</summary>
    public string? LocaltonetAccount { get; set; }

    /// <summary>The Localtonet accounts Elio is signed in to. The first one is the one sync and sharing speak for.</summary>
    public List<LocaltonetLogin> LocaltonetAccounts { get; set; } = [];

    /// <summary>Where encrypted copies of the library are kept. Empty means sync is off.</summary>
    public string? SyncServer { get; set; }

    /// <summary>Whether saved passwords travel with the library. They are encrypted end to end either way.</summary>
    public bool SyncSecrets { get; set; } = true;

    /// <summary>
    /// The program a server's file is opened in for editing, with any arguments that go before the file, such as
    /// "code --wait". Empty leaves the choice to the system.
    /// </summary>
    public string EditorCommand { get; set; } = string.Empty;

    /// <summary>Whether Elio asks GitHub at startup if there is a newer version. It is the only request it makes on its own.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When a newer version was last asked about, so that it is asked at most once a day.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>The newest version the user has been told about, so that the same one is not announced twice.</summary>
    public string? UpdateNoticeVersion { get; set; }

    /// <summary>Action name to key sequence, only for the shortcuts the user changed.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];
}

/// <summary>One pane, or a division into two.</summary>
public sealed class PaneLayout
{
    public Guid? HostId { get; set; }

    /// <summary>The pane shows the server's files rather than a terminal.</summary>
    public bool Files { get; set; }

    /// <summary>The second server of a file pane that shows two servers side by side, in place of this computer.</summary>
    public Guid? OtherHostId { get; set; }

    /// <summary>The folder a local shell was in, so it can start there again.</summary>
    public string? Directory { get; set; }

    /// <summary>The name of a local shell that was left running, so it can be picked up again.</summary>
    public string? ShellId { get; set; }

    public bool Horizontal { get; set; }
    public double Ratio { get; set; } = 0.5;
    public PaneLayout? First { get; set; }
    public PaneLayout? Second { get; set; }
}

public sealed class TabLayout
{
    public PaneLayout Root { get; set; } = new();

    /// <summary>The name the user gave the tab, if they gave it one.</summary>
    public string? Title { get; set; }

    /// <summary>The color the user marked the tab with, as 0xRRGGBB.</summary>
    public uint? Color { get; set; }

    /// <summary>Pinned tabs stay at the front and are not closed along with the others.</summary>
    public bool Pinned { get; set; }
}

/// <summary>The tabs that were open, so they can come back after a restart.</summary>
public sealed class SessionLayout : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public List<TabLayout> Tabs { get; set; } = [];
    public int ActiveTab { get; set; }
}

/// <summary>A layout the user saved under a name, to open again whenever they like.</summary>
public sealed class SavedWorkspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>The tabs as they were, in the same form the last session is kept in. Servers are named by their id.</summary>
    public List<TabLayout> Tabs { get; set; } = [];

    public DateTime SavedUtc { get; set; }
}

/// <summary>The workspaces saved on this device. They are not synced.</summary>
public sealed class WorkspaceFile : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public List<SavedWorkspace> Workspaces { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HostLibrary))]
[JsonSerializable(typeof(KnownHostsFile))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionLayout))]
[JsonSerializable(typeof(WorkspaceFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(VaultContents))]
[JsonSerializable(typeof(IdentitySecret))]
[JsonSerializable(typeof(LocaltonetCache))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>One saved secret, with the time it was set.</summary>
public sealed class VaultEntry
{
    public string Secret { get; set; } = string.Empty;
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>What the vault file holds once decrypted.</summary>
public sealed class VaultContents
{
    public int Format { get; set; } = 2;

    /// <summary>Per server: passwords and key passphrases. These may be synced.</summary>
    public Dictionary<Guid, VaultEntry> Entries { get; set; } = [];

    public Dictionary<Guid, DateTime> Deleted { get; set; } = [];

    /// <summary>This device's own secrets, such as the Localtonet key and the sync key. Never synced.</summary>
    public Dictionary<string, string> Local { get; set; } = [];
}

/// <summary>One Localtonet account Elio is signed in to. Its key is kept in the vault, never here.</summary>
public sealed class LocaltonetLogin
{
    /// <summary>What tells the accounts apart on this computer. Empty for the first one, which predates having several.</summary>
    public string Id { get; set; } = string.Empty;

    public string? Email { get; set; }
}

/// <summary>The device list from the last time Localtonet was asked, so restored tabs can find their device.</summary>
public sealed class LocaltonetCache : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;
    public List<HostEntry> Devices { get; set; } = [];
}
