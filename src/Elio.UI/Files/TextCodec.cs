using System.Text;

namespace Elio.UI.Files;

/// <summary>How a text file was written, so that saving it puts back exactly what was there.</summary>
/// <param name="LineEnding">"\n" or "\r\n".</param>
public sealed record TextFormat(Encoding Encoding, bool Bom, string LineEnding);

/// <summary>Reads a file's bytes as text for the editor, and turns the text back into bytes the way the file had them.</summary>
public static class TextCodec
{
    /// <summary>The most a file may hold to be opened for editing. Larger ones are refused by whoever opens the editor.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private const int Utf8Page = 65001;
    private const int Utf16LittleEndianPage = 1200;
    private const int Utf16BigEndianPage = 1201;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads the bytes as text. A file with NUL bytes, or one that is not valid UTF-8 (or UTF-16 after its byte order
    /// mark), is not text this editor can show without damaging it, so nothing is returned for it.
    /// </summary>
    /// <param name="text">The text, with every line ending turned into "\n".</param>
    public static bool TryDecode(byte[] bytes, out string text, out TextFormat format)
    {
        text = string.Empty;
        format = new TextFormat(Utf8, false, "\n");

        (Encoding encoding, int bom) = Detect(bytes);

        // UTF-32 is not read, and its byte order mark begins like UTF-16's: it must not be mistaken for it.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return false;
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return false;

        if (bom == 0 && Array.IndexOf(bytes, (byte)0) >= 0) return false;

        string decoded;
        try
        {
            decoded = encoding.GetString(bytes, bom, bytes.Length - bom);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            return false;
        }

        if (decoded.Contains('\0')) return false;

        string ending = LineEndingOf(decoded);
        text = Normalize(decoded);
        format = new TextFormat(encoding, bom > 0, ending);
        return true;
    }

    /// <summary>The bytes of the text as the file had them: its encoding, byte order mark and line ending.</summary>
    public static byte[] Encode(string text, TextFormat format)
    {
        string body = Normalize(text);
        if (format.LineEnding == "\r\n") body = body.Replace("\n", "\r\n");

        byte[] mark = format.Bom ? ByteOrderMark(format.Encoding) : [];
        byte[] content = format.Encoding.GetBytes(body);
        if (mark.Length == 0) return content;

        byte[] all = new byte[mark.Length + content.Length];
        mark.CopyTo(all, 0);
        content.CopyTo(all, mark.Length);
        return all;
    }

    /// <summary>Turns every line ending, whatever it is, into "\n".</summary>
    internal static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private static (Encoding Encoding, int BomLength) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (Utf8, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Utf16LittleEndian, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (Utf16BigEndian, 2);
        return (Utf8, 0);
    }

    private static byte[] ByteOrderMark(Encoding encoding) => encoding.CodePage switch
    {
        Utf16LittleEndianPage => [0xFF, 0xFE],
        Utf16BigEndianPage => [0xFE, 0xFF],
        Utf8Page => [0xEF, 0xBB, 0xBF],
        _ => [],
    };

    // Whichever of the two is more common wins, so a file that is mostly one and has a few of the other keeps its own.
    private static string LineEndingOf(string text)
    {
        int crlf = 0;
        int lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    lf++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>What the encoding is called in the editor's status line.</summary>
    internal static string NameOf(TextFormat format)
    {
        string name = format.Encoding.CodePage switch
        {
            Utf16LittleEndianPage => "UTF-16 LE",
            Utf16BigEndianPage => "UTF-16 BE",
            _ => "UTF-8",
        };
        return format.Bom ? name + " BOM" : name;
    }
}
