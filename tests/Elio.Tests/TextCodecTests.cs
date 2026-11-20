using System.Text;
using Elio.UI.Files;

namespace Elio.Tests;

public sealed class TextCodecTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] Join(byte[] first, byte[] second) => [.. first, .. second];

    [Fact]
    public void PlainUtf8_IsReadWithoutAMarkAndWithLineFeeds()
    {
        Assert.True(TextCodec.TryDecode(Encoding.UTF8.GetBytes("héllo\nwörld\n"), out string text, out TextFormat format));

        Assert.Equal("héllo\nwörld\n", text);
        Assert.False(format.Bom);
        Assert.Equal("\n", format.LineEnding);
        Assert.Equal(65001, format.Encoding.CodePage);
    }

    [Fact]
    public void Utf8WithAMark_KeepsTheMark()
    {
        byte[] bytes = Join(Utf8Bom, Encoding.UTF8.GetBytes("a\nb"));

        Assert.True(TextCodec.TryDecode(bytes, out string text, out TextFormat format));

        Assert.Equal("a\nb", text);
        Assert.True(format.Bom);
        Assert.Equal(bytes, TextCodec.Encode(text, format));
    }

    [Fact]
    public void Utf16LittleEndian_IsReadFromItsMark()
    {
        byte[] bytes = Join([0xFF, 0xFE], new UnicodeEncoding(false, false).GetBytes("one\r\ntwo\r\n"));

        Assert.True(TextCodec.TryDecode(bytes, out string text, out TextFormat format));

        Assert.Equal("one\ntwo\n", text);
        Assert.Equal(1200, format.Encoding.CodePage);
        Assert.True(format.Bom);
        Assert.Equal("\r\n", format.LineEnding);
        Assert.Equal(bytes, TextCodec.Encode(text, format));
    }

    [Fact]
    public void Utf16BigEndian_IsReadFromItsMark()
    {
        byte[] bytes = Join([0xFE, 0xFF], new UnicodeEncoding(true, false).GetBytes("héllo\n"));

        Assert.True(TextCodec.TryDecode(bytes, out string text, out TextFormat format));

        Assert.Equal("héllo\n", text);
        Assert.Equal(1201, format.Encoding.CodePage);
        Assert.Equal(bytes, TextCodec.Encode(text, format));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\n", "\r\n")]
    [InlineData("a\nb\nc\n", "\n")]
    [InlineData("a\r\nb\r\nc\nd\r\n", "\r\n")]
    [InlineData("a\nb\nc\r\nd\n", "\n")]
    [InlineData("no line break", "\n")]
    public void LineEndings_TheMoreCommonOneWins_AndTheTextIsNormalized(string content, string expected)
    {
        Assert.True(TextCodec.TryDecode(Encoding.UTF8.GetBytes(content), out string text, out TextFormat format));

        Assert.Equal(expected, format.LineEnding);
        Assert.DoesNotContain('\r', text);
        Assert.Equal(content.Replace("\r\n", "\n"), text);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\n")]
    [InlineData("a\nb\nc\n")]
    [InlineData("é\r\n\r\n\r\nz")]
    [InlineData("")]
    [InlineData("one line")]
    public void EncodeOfDecode_GivesTheBytesBack_WhenTheEndingsAreAllOneKind(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        Assert.True(TextCodec.TryDecode(bytes, out string text, out TextFormat format));

        Assert.Equal(bytes, TextCodec.Encode(text, format));
    }

    [Fact]
    public void MixedEndings_AreMadeAllTheCommonOneOnSaving()
    {
        Assert.True(TextCodec.TryDecode(Encoding.UTF8.GetBytes("a\r\nb\r\nc\nd\r\n"), out string text, out TextFormat format));

        Assert.Equal("a\r\nb\r\nc\r\nd\r\n", Encoding.UTF8.GetString(TextCodec.Encode(text, format)));
    }

    [Fact]
    public void EmptyFile_IsEmptyText()
    {
        Assert.True(TextCodec.TryDecode([], out string text, out TextFormat format));

        Assert.Equal(string.Empty, text);
        Assert.False(format.Bom);
        Assert.Equal("\n", format.LineEnding);
        Assert.Empty(TextCodec.Encode(text, format));
    }

    [Fact]
    public void OnlyAMark_IsEmptyTextThatKeepsItsMark()
    {
        Assert.True(TextCodec.TryDecode(Utf8Bom, out string text, out TextFormat format));

        Assert.Equal(string.Empty, text);
        Assert.Equal(Utf8Bom, TextCodec.Encode(text, format));
    }

    [Fact]
    public void NulBytes_MeanBinary()
    {
        Assert.False(TextCodec.TryDecode([0x50, 0x4B, 0x00, 0x03, 0x04], out _, out _));
        Assert.False(TextCodec.TryDecode(Encoding.UTF8.GetBytes("text\0more"), out _, out _));
    }

    [Fact]
    public void InvalidUtf8_MeansBinary()
    {
        // A Latin-1 "é" on its own is not UTF-8, and showing it would turn it into something else on saving.
        Assert.False(TextCodec.TryDecode([0x63, 0x61, 0x66, 0xE9], out _, out _));
        Assert.False(TextCodec.TryDecode([0xFF, 0xD8, 0xFF, 0xE0, 0x41], out _, out _));
    }

    [Fact]
    public void Utf16WithAnOddNumberOfBytes_MeansBinary() =>
        Assert.False(TextCodec.TryDecode([0xFF, 0xFE, 0x61, 0x00, 0x62], out _, out _));

    [Fact]
    public void Utf32_IsNotRead() =>
        Assert.False(TextCodec.TryDecode([0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00], out _, out _));

    [Fact]
    public void Encode_TurnsAnyLineBreakInTheTextIntoTheFormatsOwn()
    {
        var format = new TextFormat(new UTF8Encoding(false), false, "\r\n");

        Assert.Equal("a\r\nb\r\nc", Encoding.UTF8.GetString(TextCodec.Encode("a\nb\r\nc", format)));
    }

    [Fact]
    public void Encode_WritesLineFeedsOnlyForALineFeedFormat()
    {
        var format = new TextFormat(new UTF8Encoding(false), false, "\n");

        Assert.Equal("a\nb\nc", Encoding.UTF8.GetString(TextCodec.Encode("a\r\nb\rc", format)));
    }

    [Fact]
    public void MaxBytes_IsTwoMegabytes() =>
        Assert.Equal(2 * 1024 * 1024, TextCodec.MaxBytes);
}
