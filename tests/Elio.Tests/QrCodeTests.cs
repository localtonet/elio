using Elio.Core;
using Elio.Sync;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Elio.Tests;

/// <summary>Elio draws its own QR codes; these have a reader Elio did not write read them back.</summary>
public sealed class QrCodeTests
{
    // Paints the squares as a grey picture with the light margin a reader expects, and has ZXing read it.
    private static string Read(bool[,] squares)
    {
        const int Scale = 4, Margin = 4;
        int size = squares.GetLength(0);
        int side = (size + (2 * Margin)) * Scale;
        byte[] grey = new byte[side * side];
        Array.Fill(grey, (byte)255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (squares[y, x])
                    for (int dy = 0; dy < Scale; dy++)
                        for (int dx = 0; dx < Scale; dx++)
                            grey[(((y + Margin) * Scale) + dy) * side + ((x + Margin) * Scale) + dx] = 0;

        var picture = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(grey, side, side, RGBLuminanceSource.BitmapFormat.Gray8)));
        // The picture is exact, with no perspective or noise, so the reader is told to take it as it is rather than hunt for it.
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.PURE_BARCODE] = true };
        return new QRCodeReader().decode(picture, hints)?.Text ?? throw new InvalidOperationException($"The reader found no code in a picture {size} squares wide.");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("HELLO WORLD")]
    [InlineData("elio://sync/N5P43-CTFNS-CFHJP-4Y659?s=sync.elio.sh")]
    [InlineData("elio://sync/N5P43-CTFNS-CFHJP-4Y659?s=http%3A%2F%2F127.0.0.1%3A58231%2F")]
    [InlineData("Türkçe ve 日本語 de okunur")]
    public void WhatIsDrawn_IsReadBackTheSame(string text)
    {
        Assert.Equal(text, Read(QrCode.Encode(text)));
    }

    [Fact]
    public void EveryLengthThatFits_IsReadBack()
    {
        for (int length = 1; length <= QrCode.MaxBytes; length++)
        {
            string text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + (i % 26))));
            Assert.Equal(text, Read(QrCode.Encode(text)));
        }
    }

    [Fact]
    public void TextThatDoesNotFit_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', QrCode.MaxBytes + 1)));
    }

    [Fact]
    public void ALongerText_GetsABiggerPicture()
    {
        Assert.Equal(21, QrCode.Encode("short").GetLength(0));
        Assert.True(QrCode.Encode(new string('x', 100)).GetLength(0) > 21);
    }

    [Fact]
    public void SyncLink_GoesThroughAQrCodeAndComesBack()
    {
        string code = SyncPairing.NewLinkCode();
        string link = SyncLink.Format(new Uri("https://sync.elio.sh"), code);

        Assert.True(SyncLink.TryParse(Read(QrCode.Encode(link)), out Uri? server, out string read));
        Assert.Equal(new Uri("https://sync.elio.sh/"), server);
        Assert.Equal(code, read);
    }

    [Theory]
    [InlineData("https://sync.elio.sh", "https://sync.elio.sh/")]
    [InlineData("https://sync.example.com:8443", "https://sync.example.com:8443/")]
    [InlineData("http://127.0.0.1:58231", "http://127.0.0.1:58231/")]
    public void SyncLink_KeepsTheServer(string server, string expected)
    {
        string link = SyncLink.Format(new Uri(server), SyncPairing.NewLinkCode());

        Assert.True(SyncLink.TryParse(link, out Uri? parsed, out _));
        Assert.Equal(new Uri(expected), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/")]
    [InlineData("elio://sync/TOO-SHORT")]
    [InlineData("elio://sync/N5P43-CTFNS-CFHJP-4Y659?s=javascript%3Aalert(1)")]
    [InlineData("elio://other/N5P43-CTFNS-CFHJP-4Y659")]
    public void SyncLink_RefusesWhatIsNotOne(string? text)
    {
        Assert.False(SyncLink.TryParse(text, out _, out _));
    }
}
