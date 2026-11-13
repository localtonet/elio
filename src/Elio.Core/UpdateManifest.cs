using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Elio.Core;

/// <summary>One file of a release, as the signed manifest describes it.</summary>
/// <param name="Sha256">The SHA-256 of the file, as 64 lower-case hexadecimal digits.</param>
/// <param name="Size">The size of the file in bytes.</param>
public sealed record UpdateFile(string Sha256, long Size);

/// <summary>
/// What a release says about itself in <c>update.json</c>: its version and the SHA-256 and size of every file that
/// can be installed from it. The manifest is signed (<see cref="UpdateSignature"/>), so what it says can be believed,
/// and the files it lists are only taken if they match it.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The most a manifest may take. Anything larger is not one.</summary>
    public const int LargestManifest = 64 * 1024;

    /// <summary>The most a file of a release may take, packed.</summary>
    public const long LargestFile = 300L * 1024 * 1024;

    /// <summary>The version of the release, with three numbers.</summary>
    public required Version Version { get; init; }

    /// <summary>The files by name.</summary>
    public required IReadOnlyDictionary<string, UpdateFile> Files { get; init; }

    /// <summary>
    /// Reads a manifest. Returns false for anything that is not exactly what the release workflow writes: a version of
    /// three numbers, and files with a plain name, a SHA-256 and a size that is neither zero nor larger than
    /// <see cref="LargestFile"/>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out UpdateManifest? manifest)
    {
        manifest = null;
        if (json.Length is 0 or > LargestManifest) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("version", out JsonElement versionText) || versionText.ValueKind != JsonValueKind.String) return false;
            if (!TryParseVersion(versionText.GetString(), out Version? version)) return false;
            if (!root.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Object) return false;

            var files = new Dictionary<string, UpdateFile>(StringComparer.Ordinal);
            foreach (JsonProperty entry in filesElement.EnumerateObject())
            {
                if (!IsPlainName(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object) return false;
                if (!entry.Value.TryGetProperty("sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String) return false;
                if (!entry.Value.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes)) return false;
                if (bytes is <= 0 or > LargestFile || !IsSha256(hash.GetString())) return false;

                // The same name twice is a manifest that says two things, and neither can be taken.
                if (!files.TryAdd(entry.Name, new UpdateFile(hash.GetString()!.ToLowerInvariant(), bytes))) return false;
            }

            manifest = new UpdateManifest { Version = version, Files = files };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a version of exactly three numbers, such as "1.2.3". Anything else, a pre-release included, is refused.</summary>
    public static bool TryParseVersion(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (text is null || text.Length is 0 or > 32) return false;

        string[] parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length is 0 or > 5 || !part.All(char.IsAsciiDigit))) return false;
        return Version.TryParse(text, out version);
    }

    // A file name is letters, digits, dots, dashes and underscores: no folder, so nothing can leave the folder it is put in.
    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsSha256(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
}
