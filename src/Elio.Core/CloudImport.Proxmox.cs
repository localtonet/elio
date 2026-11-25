using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Elio.Core;

/// <summary>
/// What a Proxmox VE server is asked with: its address and an API token. A class and not a record, so that printing
/// one can never print the secret.
/// </summary>
public sealed class ProxmoxCredentials
{
    internal ProxmoxCredentials(Uri server, string tokenId, string secret) => (Server, TokenId, Secret) = (server, tokenId, secret);

    /// <summary>The server's address, with the scheme and port and nothing after them, such as https://pve.example.com:8006/.</summary>
    public Uri Server { get; }

    /// <summary>The token's id, "user@realm!name".</summary>
    public string TokenId { get; }

    internal string Secret { get; }

    /// <summary>The server's address as it is kept beside the certificate it was trusted with, such as https://pve.example.com:8006.</summary>
    public string Origin => Server.GetLeftPart(UriPartial.Authority);
}

/// <summary>What Proxmox listed: the machines that could be given an address, and how many could not.</summary>
public sealed record ProxmoxListing(HostLibrary Library, int LeftOut);

/// <summary>What to do with the certificate a server presented.</summary>
public enum CertificateVerdict
{
    /// <summary>Go on: the system trusts it, or it is the very one the user accepted.</summary>
    Accept,

    /// <summary>Stop and ask the user, showing its fingerprint. Nothing has been sent yet.</summary>
    Ask,

    /// <summary>Stop: it is not the one the user accepted.</summary>
    Refuse,
}

/// <summary>
/// The certificate of a server that is trusted because the user said so: one with its own signature, as Proxmox VE
/// makes for itself. Nothing is turned off for anyone else. The one certificate the user saw and accepted is let
/// through for that server, and any other is refused.
/// </summary>
public static class PinnedCertificate
{
    /// <summary>
    /// Decides on a certificate. With a pin, exactly that certificate is accepted and every other refused, even one
    /// the system would trust: the user asked for this one. Without a pin, one the system trusts is accepted as
    /// anywhere else, and any other is for the user to decide on.
    /// </summary>
    /// <param name="errors">What the system found wrong with it.</param>
    /// <param name="presented">The fingerprint of the certificate the server presented, or null if it presented none.</param>
    /// <param name="pinned">The fingerprint the user accepted, or null if they have accepted none for this server.</param>
    public static CertificateVerdict Decide(SslPolicyErrors errors, string? presented, string? pinned)
    {
        string? shown = Normalize(presented);
        if (shown is null) return CertificateVerdict.Refuse;

        if (pinned is not null) return Normalize(pinned) == shown ? CertificateVerdict.Accept : CertificateVerdict.Refuse;
        return errors == SslPolicyErrors.None ? CertificateVerdict.Accept : CertificateVerdict.Ask;
    }

    /// <summary>The SHA-256 fingerprint of a certificate, written the way Proxmox shows it: pairs of capital hex digits with colons between.</summary>
    public static string Fingerprint(X509Certificate certificate) => Format(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>A SHA-256 hash in the form fingerprints are shown in.</summary>
    public static string Format(byte[] hash) => string.Join(':', hash.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// A fingerprint in the one form they are compared in, whichever case and separators it was written with. Null
    /// if it is not a SHA-256 fingerprint at all.
    /// </summary>
    public static string? Normalize(string? fingerprint)
    {
        if (fingerprint is null) return null;

        string hex = new(fingerprint.Where(c => c is not (':' or ' ' or '-')).ToArray());
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? Format(Convert.FromHexString(hex)) : null;
    }
}

/// <summary>
/// The server presented a certificate the system does not trust and the user has not accepted. Nothing was sent to
/// it: the connection stopped before the request. Show the fingerprint, and on a yes ask again with it as the pin.
/// </summary>
public sealed class UntrustedCertificateException(string host, string fingerprint)
    : Exception(L.T("{0} presented a certificate this computer does not trust.", host))
{
    public string Host { get; } = host;

    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// Lists the virtual machines and containers of a Proxmox VE server or cluster as servers, with an API token. A
/// machine is only listed with the address Proxmox knows for it: from the QEMU guest agent for a virtual machine,
/// and from the container itself for a container.
/// </summary>
public static partial class ProxmoxImport
{
    /// <summary>Who the messages name.</summary>
    public const string Provider = "Proxmox VE";

    /// <summary>The port the Proxmox VE API listens on.</summary>
    public const int DefaultPort = 8006;

    // A cluster with more guests than this is not being listed, it is being looped.
    private const int MaxGuests = 2000;

    // The addresses of a few guests are asked for at once: the guest agent of one can take its time.
    private const int Parallel = 4;

    // "user@realm!name". The user may itself hold an @, as an e-mail address does; the realm and the token's name
    // are letters, digits, dots, dashes and underscores. No space, colon, slash, equals sign or anything outside
    // ASCII: it goes into a header, followed by "=" and the secret.
    [GeneratedRegex(@"^[\x21-\x7E-[:/!=]]+@[A-Za-z][A-Za-z0-9._-]*![A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex TokenIdPattern();

    // The secret is a UUID. Anything else is refused before it gets anywhere near a header.
    [GeneratedRegex(@"^[A-Za-z0-9-]{8,128}$")]
    private static partial Regex SecretPattern();

    // A node is named like a host: letters, digits and dashes.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex NodePattern();

    [GeneratedRegex(@":\d{1,5}$")]
    private static partial Regex PortPattern();

    /// <summary>The group a server's machines are put in.</summary>
    public static string GroupName(string host) => $"Proxmox VE / {host}";

    /// <summary>
    /// Checks what the user typed and puts it together. Nothing typed is repeated in a message: the secret never, and
    /// the rest not either, since a secret pasted into the wrong box would be.
    /// </summary>
    /// <param name="address">The server, such as https://pve.example.com:8006. Without a port, 8006; without a scheme, HTTPS.</param>
    /// <exception cref="CloudImportException">One of them is not what it should be.</exception>
    public static ProxmoxCredentials Credentials(string address, string tokenId, string secret)
    {
        Uri server = ServerAddress(address) ?? throw new CloudImportException(L.T("That is not the address of a Proxmox VE server. One looks like https://pve.example.com:8006."));
        tokenId = tokenId.Trim();
        secret = secret.Trim();

        if (!TokenIdPattern().IsMatch(tokenId)) throw new CloudImportException(L.T("That is not the ID of a Proxmox API token. One looks like root@pam!elio."));
        if (!SecretPattern().IsMatch(secret)) throw new CloudImportException(L.T("That is not the secret of a Proxmox API token. It is the UUID Proxmox showed once, when the token was made."));
        return new ProxmoxCredentials(server, tokenId, secret);
    }

    /// <summary>
    /// The server's address from what was typed: HTTPS only, the port 8006 unless another is given, and nothing after
    /// the port (the address of a page of Proxmox's own, pasted from the browser, comes down to the server). Null
    /// if it is not an address at all, or not HTTPS.
    /// </summary>
    public static Uri? ServerAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? typed) || typed.Scheme != Uri.UriSchemeHttps || typed.UserInfo.Length > 0) return null;
        if (typed.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;

        // Only a port that was written counts: Uri gives 443 to an address that has none.
        string authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        int port = PortPattern().IsMatch(authority) ? typed.Port : DefaultPort;
        return new UriBuilder(Uri.UriSchemeHttps, typed.Host, port).Uri;
    }

    /// <summary>The value of the Authorization header Proxmox VE wants for an API token.</summary>
    public static string AuthorizationHeader(ProxmoxCredentials credentials) => $"PVEAPIToken={credentials.TokenId}={credentials.Secret}";

    /// <summary>
    /// Asks Proxmox for every virtual machine and container the token may see, then for the address of each one
    /// that is running. The token goes in the request's header and nowhere else, and only once the server's
    /// certificate has been accepted: the connection stops before the request is sent otherwise.
    /// </summary>
    /// <param name="pinnedFingerprint">The certificate the user accepted for this server, or null.</param>
    /// <param name="handler">Answers in place of the network, in tests. The certificate is not checked then.</param>
    /// <exception cref="UntrustedCertificateException">The certificate is not trusted and was not accepted. Nothing was sent.</exception>
    /// <exception cref="CloudImportException">Proxmox refused, could not be reached, or presented another certificate than the one accepted.</exception>
    public static async Task<ProxmoxListing> ListAsync(ProxmoxCredentials credentials, string? pinnedFingerprint, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        var check = new CertificateCheck(credentials.Server.Host, pinnedFingerprint);

        // A redirect is never followed: it would carry the token on to wherever it points.
        using HttpMessageHandler? own = handler is null ? new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = check.Validate } : null;
        using HttpClient client = CloudHttp.Client(handler ?? own);

        string body = await GetAsync(client, credentials, check, "cluster/resources?type=vm", cancellation).ConfigureAwait(false);
        List<ProxmoxGuest> guests = ParseResources(body).Where(guest => !guest.Template).Take(MaxGuests).ToList();

        // Only a running guest can say where it is. Each one is asked on its own, and one that does not answer is left out.
        var addresses = new string?[guests.Count];
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(guests.Select(async (guest, index) =>
        {
            if (!guest.Running) return;
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                addresses[index] = await AddressAsync(client, credentials, check, guest, cancellation).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var group = new HostGroup { Name = GroupName(credentials.Server.Host) };
        var library = new HostLibrary { Groups = [group] };
        for (int i = 0; i < guests.Count; i++)
        {
            if (addresses[i] is not string address) continue;

            ProxmoxGuest guest = guests[i];
            library.Hosts.Add(new HostEntry
            {
                Name = guest.Name ?? $"{guest.Type} {guest.VmId}",
                Address = address,

                // A container's own templates let root in. A virtual machine's user is whatever its image made.
                User = guest.Container ? "root" : string.Empty,
                UseAgent = true,
                Tags = new[] { guest.Node }.Concat(guest.Tags).Distinct().ToList(),
                GroupId = group.Id,
            });
        }

        return new ProxmoxListing(library, guests.Count - library.Hosts.Count);
    }

    /// <summary>One virtual machine or container, as the list of a cluster's resources gives it.</summary>
    public sealed record ProxmoxGuest(string Type, string Node, int VmId, string? Name, string Status, bool Template, IReadOnlyList<string> Tags)
    {
        public bool Container => Type == "lxc";

        public bool Running => Status == "running";
    }

    /// <summary>
    /// The virtual machines and containers in the answer to /cluster/resources?type=vm. One whose node or id could
    /// not go into an address safely is left out: both are put into the path of the request for its address.
    /// </summary>
    /// <exception cref="CloudImportException">It is not such an answer.</exception>
    public static List<ProxmoxGuest> ParseResources(string json)
    {
        var guests = new List<ProxmoxGuest>();
        using JsonDocument document = CloudJson.ParseList(json, "data", Provider, out JsonElement resources);

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object) continue;
            if (CloudJson.Text(resource, "type") is not { } type || type is not ("qemu" or "lxc")) continue;
            if (CloudJson.Text(resource, "node") is not string node || !NodePattern().IsMatch(node)) continue;
            if (CloudJson.Number(resource, "vmid") is not int vmid || vmid is < 1 or > 999_999_999) continue;

            // Tags are kept with semicolons between them, and were written with commas or spaces before that.
            List<string> tags = (CloudJson.Text(resource, "tags") ?? string.Empty)
                .Split([';', ',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(CloudText.Clean).OfType<string>().ToList();

            guests.Add(new ProxmoxGuest(type, node, vmid, CloudText.Clean(CloudJson.Text(resource, "name")), CloudJson.Text(resource, "status") ?? string.Empty, IsTrue(resource, "template"), tags));
        }

        return guests;
    }

    /// <summary>
    /// The first IPv4 address, other than a loopback or link-local one, in what the QEMU guest agent said about the
    /// machine's network interfaces. Null if there is none.
    /// </summary>
    public static string? ParseAgentInterfaces(string json)
    {
        JsonDocument? document = Parse(json);
        if (document is null) return null;

        using (document)
        {
            if (CloudJson.Child(document.RootElement, "data") is not { } data) return null;
            return FirstAddress(CloudJson.Objects(data, "result").SelectMany(nic => AddressesOf(nic)));
        }
    }

    /// <summary>
    /// The first IPv4 address, other than a loopback or link-local one, in what a container said about its network
    /// interfaces: in the list of addresses newer versions give, or the one "inet" older ones do. Null if there is none.
    /// </summary>
    public static string? ParseContainerInterfaces(string json)
    {
        JsonDocument? document = Parse(json);
        if (document is null) return null;

        using (document)
        {
            return FirstAddress(CloudJson.Objects(document.RootElement, "data").SelectMany(nic =>
            {
                List<string> listed = AddressesOf(nic).ToList();
                return listed.Count > 0 ? listed : [CloudJson.Text(nic, "inet")?.Split('/')[0] ?? string.Empty];
            }));
        }
    }

    /// <summary>
    /// What Proxmox said was wrong: the reasons it gave for each parameter, its message, or the words after the status
    /// code, which is where it puts most of them. The secret is taken out of whatever it said, in case it was repeated.
    /// </summary>
    public static string? ErrorMessage(string body, string? reason, ProxmoxCredentials credentials)
    {
        string? said = null;
        using (JsonDocument? document = CloudJson.ParseError(body))
        {
            if (document is not null)
            {
                if (CloudJson.Child(document.RootElement, "errors") is { ValueKind: JsonValueKind.Object } errors)
                {
                    List<string> reasons = errors.EnumerateObject()
                        .Where(error => error.Value.ValueKind == JsonValueKind.String)
                        .Select(error => $"{error.Name}: {error.Value.GetString()}")
                        .ToList();
                    if (reasons.Count > 0) said = string.Join("; ", reasons);
                }

                said ??= CloudJson.Text(document.RootElement, "message");
            }
        }

        said ??= reason;
        return said is null ? null : CloudText.Clean(Scrub(said, credentials));
    }

    /// <summary>The text with the secret, and the header it goes in, taken out.</summary>
    internal static string Scrub(string text, ProxmoxCredentials credentials) =>
        text.Replace(AuthorizationHeader(credentials), "PVEAPIToken=…", StringComparison.Ordinal).Replace(credentials.Secret, "…", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> AddressAsync(HttpClient client, ProxmoxCredentials credentials, CertificateCheck check, ProxmoxGuest guest, CancellationToken cancellation)
    {
        // Both parts of the path were checked when the list was read, and are escaped all the same.
        string node = Uri.EscapeDataString(guest.Node);
        string path = guest.Container ? $"nodes/{node}/lxc/{guest.VmId}/interfaces" : $"nodes/{node}/qemu/{guest.VmId}/agent/network-get-interfaces";
        try
        {
            string body = await GetAsync(client, credentials, check, path, cancellation).ConfigureAwait(false);
            return guest.Container ? ParseContainerInterfaces(body) : ParseAgentInterfaces(body);
        }
        catch (CloudImportException) when (!check.Failed)
        {
            // No guest agent, a container too old to say, a permission the token lacks for this one: it is left out.
            return null;
        }
    }

    /// <summary>Asks for one path of the API and gives back the answer if it was a success.</summary>
    private static async Task<string> GetAsync(HttpClient client, ProxmoxCredentials credentials, CertificateCheck check, string path, CancellationToken cancellation)
    {
        // Only the path is ever added to the server's own address: nothing an answer said decides where a request goes.
        var address = new Uri(credentials.Server, "api2/json/" + path);
        if (address.Scheme != Uri.UriSchemeHttps || address.Authority != credentials.Server.Authority) throw new CloudImportException(L.T("{0} can only be asked over HTTPS.", Provider));

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader(credentials));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        cancellation.ThrowIfCancellationRequested();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            // A redirect comes back as what it is, never followed, and is refused here along with every other non-success.
            if (!response.IsSuccessStatusCode)
                throw new CloudImportException(ErrorMessage(body, response.ReasonPhrase, credentials) ?? CloudHttp.Refused(Provider, response.StatusCode).Message);
            return body;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // Nobody cancelled, so it was the clock.
            throw new CloudImportException(L.T("{0} did not answer in time.", Provider));
        }
        catch (HttpRequestException ex)
        {
            // The certificate's own reason comes first: the connection stopped because of it, before anything was sent.
            throw check.Problem() ?? new CloudImportException(Scrub(ex.Message, credentials));
        }
    }

    private static JsonDocument? Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> AddressesOf(JsonElement nic) =>
        CloudJson.Objects(nic, "ip-addresses")
            .Where(ip => CloudJson.Text(ip, "ip-address-type") is null or "ipv4" or "inet")
            .Select(ip => CloudJson.Text(ip, "ip-address") ?? string.Empty);

    private static string? FirstAddress(IEnumerable<string> candidates) =>
        candidates.Select(CloudJson.Address)
            .FirstOrDefault(ip => ip is { AddressFamily: AddressFamily.InterNetwork } && !IPAddress.IsLoopback(ip) && !(ip.GetAddressBytes() is [169, 254, ..]))
            ?.ToString();

    // Perl writes a yes as 1, and newer versions sometimes as true.
    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number != 0));

    /// <summary>Checks the server's certificate on every connection of one import, and remembers why it stopped one.</summary>
    private sealed class CertificateCheck(string host, string? pinned)
    {
        private readonly Lock _gate = new();
        private CertificateVerdict _verdict = CertificateVerdict.Accept;
        private string? _presented;

        /// <summary>Whether a connection was stopped because of the certificate.</summary>
        public bool Failed
        {
            get
            {
                lock (_gate) return _verdict != CertificateVerdict.Accept;
            }
        }

        public bool Validate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            string? presented = certificate is null ? null : PinnedCertificate.Fingerprint(certificate);
            CertificateVerdict verdict = PinnedCertificate.Decide(errors, presented, pinned);
            if (verdict != CertificateVerdict.Accept)
            {
                lock (_gate) (_verdict, _presented) = (verdict, presented);
            }

            return verdict == CertificateVerdict.Accept;
        }

        /// <summary>The reason a connection was stopped because of the certificate, or null if none was.</summary>
        public Exception? Problem()
        {
            lock (_gate)
            {
                return _verdict switch
                {
                    CertificateVerdict.Ask when _presented is not null => new UntrustedCertificateException(host, _presented),
                    CertificateVerdict.Accept => null,
                    _ => new CloudImportException(L.T("{0} presented a different certificate from the one you accepted, so nothing was sent to it. If you replaced the certificate yourself, untick \"Remember in the vault\" and import again to be asked about the new one.", host)),
                };
            }
        }
    }
}
