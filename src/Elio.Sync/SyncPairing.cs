using System.Net;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Sync;

/// <summary>The pairing code typed in does not lead anywhere: it is wrong, was already used, or is more than ten minutes old.</summary>
public sealed class PairingCodeException() : Exception(L.T("That code does not lead anywhere. It may be mistyped, already used, or more than ten minutes old."));

/// <summary>
/// Carries a sync code from one device to another by way of a short code that can be read out and typed in.
/// The long sync code is left with the sync server for ten minutes, encrypted with a key that comes from the short
/// code, under a name that also comes from it. The server is never told the short code, so it can neither read what
/// it holds nor tell which short code would fetch it, and it hands the thing over once only.
/// </summary>
public static class SyncPairing
{
    // No look-alikes: no 0 or O, no 1, I or L.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 10;
    private const int Rounds = 200_000;
    private static readonly byte[] Salt = "elio-pairing-v1"u8.ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A new code, grouped for reading out: XXXXX-XXXXX.</summary>
    public static string NewCode()
    {
        string raw = RandomNumberGenerator.GetString(Alphabet, CodeLength);
        return $"{raw[..5]}-{raw[5..]}";
    }

    /// <summary>The code as the two ends must agree on it, however it was typed. Null if it cannot be one.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;

        string code = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return code.Length == CodeLength && code.All(Alphabet.Contains) ? code : null;
    }

    /// <summary>What the server files the secret under, and the key it is locked with. Slow on purpose, against guessing.</summary>
    private static (string Name, byte[] Key) Derive(string code)
    {
        byte[] stretched = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), Salt, Rounds, HashAlgorithmName.SHA256, 64);
        return (Convert.ToHexStringLower(SHA256.HashData(stretched.AsSpan(0, 32))), stretched[32..]);
    }

    // ---- Adding a device without its user typing the password ----
    //
    // The device that already syncs hands the new one the data key itself. That key opens everything, so the
    // code that carries it is twice as long as the one above: twenty characters hold 99 bits, which nobody,
    // the server included, can guess their way through, however fast they can try. The short code only ever
    // carried the sync code, which is useless without the password.

    private const int LinkLength = 20;
    private static readonly byte[] LinkSalt = "elio-link-v1"u8.ToArray();

    /// <summary>What a device that already syncs gives a new one: which store on the server, and the key to it.</summary>
    /// <param name="AccountKey">The sync code, or null when a Localtonet account says whose store it is.</param>
    public sealed record DeviceLink(string? AccountKey, byte[] DataKey);

    /// <summary>A new code for handing over the key, grouped for reading out: XXXXX-XXXXX-XXXXX-XXXXX.</summary>
    public static string NewLinkCode()
    {
        string raw = RandomNumberGenerator.GetString(Alphabet, LinkLength);
        return string.Join('-', Enumerable.Range(0, LinkLength / 5).Select(i => raw.Substring(i * 5, 5)));
    }

    /// <summary>A key-carrying code as the two ends must agree on it, however it was typed. Null if it cannot be one.</summary>
    public static string? NormalizeLink(string? typed)
    {
        if (typed is null) return null;

        string code = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return code.Length == LinkLength && code.All(Alphabet.Contains) ? code : null;
    }

    // The name and the key come from the code by two separate derivations, so knowing the name the server
    // files it under says nothing about the key.
    private static (string Name, byte[] Key) DeriveLink(string code)
    {
        byte[] secret = Encoding.UTF8.GetBytes(code);
        byte[] name = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, LinkSalt, "name"u8.ToArray());
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, LinkSalt, "key"u8.ToArray());
        return (Convert.ToHexStringLower(name), key);
    }

    /// <summary>Locks a link with its code. Apart from the network, this is all of <see cref="OfferLinkAsync"/>.</summary>
    public static (string Name, string Sealed) SealLink(string code, DeviceLink link)
    {
        string normal = NormalizeLink(code) ?? throw new PairingCodeException();
        (string name, byte[] key) = DeriveLink(normal);

        // One line each, so nothing has to be parsed that an attacker could shape: the sync code or nothing, then the key.
        byte[] plain = Encoding.UTF8.GetBytes($"{link.AccountKey}\n{Convert.ToBase64String(link.DataKey)}");
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] sealedText = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, sealedText, tag, LinkSalt);
        return (name, Convert.ToBase64String([.. nonce, .. tag, .. sealedText]));
    }

    /// <summary>Opens what <see cref="SealLink"/> locked.</summary>
    /// <exception cref="PairingCodeException">The code is not the one it was locked with, or the text is not a link.</exception>
    public static DeviceLink OpenLink(string code, string sealedLink)
    {
        string normal = NormalizeLink(code) ?? throw new PairingCodeException();
        (_, byte[] key) = DeriveLink(normal);

        try
        {
            byte[] all = Convert.FromBase64String(sealedLink);
            byte[] plain = new byte[all.Length - 28];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, LinkSalt);

            string[] lines = Encoding.UTF8.GetString(plain).Split('\n');
            if (lines.Length != 2) throw new PairingCodeException();
            byte[] dataKey = Convert.FromBase64String(lines[1]);
            if (dataKey.Length != 32) throw new PairingCodeException();
            return new DeviceLink(lines[0].Length == 0 ? null : lines[0], dataKey);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or OverflowException)
        {
            throw new PairingCodeException();
        }
    }

    /// <summary>Leaves the key with the server for ten minutes, for a new device to fetch once with <paramref name="code"/>.</summary>
    public static async Task OfferLinkAsync(Uri server, string code, DeviceLink link, HttpClient? http = null, CancellationToken cancellation = default)
    {
        (string name, string sealedLink) = SealLink(code, link);

        using HttpClient? own = http is null ? new HttpClient { Timeout = Timeout } : null;
        using var body = new StringContent(sealedLink, Encoding.ASCII, "text/plain");
        using HttpResponseMessage response = await (http ?? own!).PostAsync(new Uri(server, "v1/pair/" + name), body, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Fetches the key another device left under <paramref name="code"/>. It can be fetched once.</summary>
    /// <exception cref="PairingCodeException">Nothing is there under that code.</exception>
    public static async Task<DeviceLink> FetchLinkAsync(Uri server, string code, HttpClient? http = null, CancellationToken cancellation = default)
    {
        string normal = NormalizeLink(code) ?? throw new PairingCodeException();
        (string name, _) = DeriveLink(normal);

        using HttpClient? own = http is null ? new HttpClient { Timeout = Timeout } : null;
        using HttpResponseMessage response = await (http ?? own!).GetAsync(new Uri(server, "v1/pair/" + name), cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PairingCodeException();
        response.EnsureSuccessStatusCode();
        return OpenLink(normal, await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>Leaves a sync code with the server for another device to fetch with <paramref name="code"/>.</summary>
    public static async Task OfferAsync(Uri server, string code, string syncCode, HttpClient? http = null, CancellationToken cancellation = default)
    {
        string normal = Normalize(code) ?? throw new PairingCodeException();
        (string name, byte[] key) = await Task.Run(() => Derive(normal), cancellation).ConfigureAwait(false);

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(syncCode);
        byte[] sealedText = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, sealedText, tag);

        using HttpClient? own = http is null ? new HttpClient { Timeout = Timeout } : null;
        using var body = new StringContent(Convert.ToBase64String([.. nonce, .. tag, .. sealedText]), Encoding.ASCII, "text/plain");
        using HttpResponseMessage response = await (http ?? own!).PostAsync(new Uri(server, "v1/pair/" + name), body, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Fetches the sync code another device left under <paramref name="code"/>. It can be fetched once.</summary>
    /// <exception cref="PairingCodeException">Nothing is there under that code.</exception>
    public static async Task<string> FetchAsync(Uri server, string code, HttpClient? http = null, CancellationToken cancellation = default)
    {
        string normal = Normalize(code) ?? throw new PairingCodeException();
        (string name, byte[] key) = await Task.Run(() => Derive(normal), cancellation).ConfigureAwait(false);

        using HttpClient? own = http is null ? new HttpClient { Timeout = Timeout } : null;
        using HttpResponseMessage response = await (http ?? own!).GetAsync(new Uri(server, "v1/pair/" + name), cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PairingCodeException();
        response.EnsureSuccessStatusCode();

        try
        {
            byte[] all = Convert.FromBase64String(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            byte[] plain = new byte[all.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or OverflowException)
        {
            throw new PairingCodeException();
        }
    }
}

/// <summary>
/// The link a QR code carries from a device that already syncs to one being added: which server, and the
/// code that fetches the key from it. Written as <c>elio://sync/CODE?s=server</c>, so that a phone's own
/// camera can hand it to Elio as well as Elio's scanner.
/// </summary>
public static class SyncLink
{
    private const string Prefix = "elio://sync/";

    /// <summary>The link for a code on a server.</summary>
    public static string Format(Uri server, string code)
    {
        // The usual case is https on its usual port, and the shorter the text the coarser and easier to read the picture.
        string where = server.Scheme == Uri.UriSchemeHttps && server.AbsolutePath == "/" ? server.Authority : server.ToString();
        return $"{Prefix}{code}?s={Uri.EscapeDataString(where)}";
    }

    /// <summary>Reads a link back. False for anything that is not one.</summary>
    public static bool TryParse(string? text, out Uri? server, out string code)
    {
        server = null;
        code = string.Empty;
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string rest = text[Prefix.Length..];
        int query = rest.IndexOf("?s=", StringComparison.Ordinal);
        string typed = query < 0 ? rest : rest[..query];
        if (SyncPairing.NormalizeLink(typed) is null) return false;

        if (query >= 0)
        {
            string where = Uri.UnescapeDataString(rest[(query + 3)..]);
            if (!where.Contains("://", StringComparison.Ordinal)) where = "https://" + where;
            if (!Uri.TryCreate(where, UriKind.Absolute, out server) || server.Scheme is not ("https" or "http")) return false;
        }

        code = typed;
        return true;
    }
}
