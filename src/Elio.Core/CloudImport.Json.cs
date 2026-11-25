using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Elio.Core;

/// <summary>
/// What the imports from providers that take a bearer token and answer in JSON have in common: asking for one
/// page, and reading what came back without trusting any of it to be the shape it should be.
/// </summary>
internal static class CloudJson
{
    /// <summary>
    /// Asks for one page with the token in the Authorization header, the only place it goes, and gives back the
    /// answer if it was a success.
    /// </summary>
    /// <param name="errorMessage">Finds what the provider said was wrong in an answer that is not a success.</param>
    /// <exception cref="CloudImportException">The provider refused or could not be reached.</exception>
    public static async Task<string> GetAsync(HttpClient client, Uri address, string token, string provider, Func<string, string?> errorMessage, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        // A redirect comes back as what it is, never followed, and is refused here along with every other non-success.
        var (status, body) = await CloudHttp.SendAsync(client, request, provider, cancellation).ConfigureAwait(false);
        if ((int)status is < 200 or > 299) throw new CloudImportException(errorMessage(body) ?? CloudHttp.Refused(provider, status).Message);
        return body;
    }

    /// <summary>The answer as JSON whose top is an object holding a list under the given name.</summary>
    /// <exception cref="CloudImportException">It is not.</exception>
    public static JsonDocument ParseList(string json, string listName, string provider, out JsonElement list)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw CloudHttp.Unreadable(provider);
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(listName, out list) && list.ValueKind == JsonValueKind.Array) return document;

        document.Dispose();
        throw CloudHttp.Unreadable(provider);
    }

    /// <summary>The JSON in an error answer, or null if it is not JSON whose top is an object.</summary>
    public static JsonDocument? ParseError(string body)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>The object under a name, or null if there is none there.</summary>
    public static JsonElement? Child(JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }

        return element;
    }

    /// <summary>The text under a name, or null if there is none or it is empty.</summary>
    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>A whole number under a name, or null if there is none.</summary>
    public static int? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) ? number : null;

    /// <summary>The items of a list under a name that are objects. Anything else there is passed over.</summary>
    public static IEnumerable<JsonElement> Objects(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    /// <summary>The texts in a list under a name, without the empty ones and whatever is not text.</summary>
    public static List<string> Texts(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(text => text.Length > 0).ToList()
            : [];

    /// <summary>
    /// The address in its usual spelling if the text is one, and one that can be connected to. Anything else
    /// (a name, an address with a range or zone on it, 0.0.0.0) is null: what is kept is about to be connected to.
    /// </summary>
    public static IPAddress? Address(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out IPAddress? address)) return null;

        // IPAddress also reads "1", "0x7f.1" and the like, which nobody means as an address.
        bool written = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => address.ToString() == text,
            AddressFamily.InterNetworkV6 => !text.Contains('%') && address.ScopeId == 0,
            _ => false,
        };

        return written && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) ? address : null;
    }

    /// <summary>Whether an IPv4 address is one the internet can reach, rather than a private, shared or local one.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] >= 224);
    }

    /// <summary>
    /// The address an IPv6 network gives its server, from text such as "2001:db8::/64": the network's first
    /// address, ::1, which is where the providers put it. A network of one address is that address.
    /// </summary>
    public static string? FirstAddress(string? network)
    {
        if (string.IsNullOrEmpty(network) || !IPNetwork.TryParse(network, out IPNetwork parsed) || parsed.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6) return null;

        byte[] bytes = parsed.BaseAddress.GetAddressBytes();
        if (parsed.PrefixLength < 128) bytes[^1] |= 1;
        return new IPAddress(bytes).ToString();
    }

    /// <summary>Tags from a provider's labels: "key=value", or the key alone where the value is empty.</summary>
    public static IEnumerable<string> Labels(JsonElement labels) =>
        labels.ValueKind == JsonValueKind.Object
            ? labels.EnumerateObject()
                .Where(label => label.Name.Length > 0 && label.Value.ValueKind == JsonValueKind.String)
                .Select(label => label.Value.GetString() is { Length: > 0 } value ? $"{label.Name}={value}" : label.Name)
            : [];
}
