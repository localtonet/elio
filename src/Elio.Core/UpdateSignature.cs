using System.Reflection;
using System.Security.Cryptography;

namespace Elio.Core;

/// <summary>
/// Checks who a release comes from. The manifest of a release is signed with a private key that lives only in the
/// release workflow's secrets; the matching public keys are built into the program (<c>update-keys.txt</c>). A program
/// that is told to install an update believes nothing that is not signed this way, so whoever can only change the
/// files on the releases page can not change what Elio installs.
/// </summary>
/// <remarks>
/// The signature is ECDSA over NIST P-256 and SHA-256, in the DER form that <c>openssl dgst -sha256 -sign</c> writes,
/// and a key is its SubjectPublicKeyInfo in base64. Several keys may be trusted at once, so that one can be replaced
/// without stranding the copies that only know the old one.
/// </remarks>
public static class UpdateSignature
{
    private const string ResourceName = "update-keys.txt";
    private const string NistP256 = "1.2.840.10045.3.1.7";

    /// <summary>The keys this program trusts, from <c>update-keys.txt</c>. Empty while no release key has been made.</summary>
    public static IReadOnlyList<byte[]> TrustedKeys { get; } = ReadBuiltInKeys();

    /// <summary>
    /// Whether <paramref name="signature"/> is a valid signature of <paramref name="data"/> by any of the keys.
    /// A key that is not a P-256 public key is passed over, and with no usable key nothing is valid.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, IEnumerable<byte[]> publicKeys)
    {
        if (signature.Length is 0 or > 128) return false;

        foreach (byte[] key in publicKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                if (ecdsa.ExportParameters(false).Curve.Oid?.Value != NistP256) continue;
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (CryptographicException)
            {
                // Not a key, or not a signature: no vote either way.
            }
        }

        return false;
    }

    /// <summary>Reads keys from text: one base64 key to a line, and lines that start with # are notes.</summary>
    public static List<byte[]> ParseKeys(string text)
    {
        var keys = new List<byte[]>();
        foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) continue;

            Span<byte> buffer = new byte[line.Length];
            if (Convert.TryFromBase64String(line, buffer, out int written) && written > 0) keys.Add(buffer[..written].ToArray());
        }

        return keys;
    }

    private static List<byte[]> ReadBuiltInKeys()
    {
        Assembly assembly = typeof(UpdateSignature).Assembly;
        string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
        if (name is null) return [];

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return ParseKeys(reader.ReadToEnd());
    }
}
