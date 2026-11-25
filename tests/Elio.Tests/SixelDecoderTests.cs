using System.Diagnostics;
using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>Sixel image data turned into pixels: what each kind of character does, and what is too large or too much.</summary>
public sealed class SixelDecoderTests
{
    private static readonly (byte, byte, byte, byte) Clear = (0, 0, 0, 0);
    private static readonly (byte, byte, byte, byte) Red = (255, 0, 0, 255);
    private static readonly (byte, byte, byte, byte) Green = (0, 255, 0, 255);
    private static readonly (byte, byte, byte, byte) Blue = (0, 0, 255, 255);

    private static ImagePixels? Decode(string data, uint? background = null)
    {
        var decoder = new SixelDecoder(background);
        decoder.Write(Encoding.Latin1.GetBytes(data));
        return decoder.Finish();
    }

    private static (byte, byte, byte, byte) Pixel(ImagePixels image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Rgba[at], image.Rgba[at + 1], image.Rgba[at + 2], image.Rgba[at + 3]);
    }

    [Fact]
    public void Character_IsOneColumnOfSixPixels_FromTheTopDown()
    {
        ImagePixels full = Decode("#1;2;100;0;0~")!;
        Assert.Equal((1, 6), (full.Width, full.Height));
        for (int y = 0; y < 6; y++) Assert.Equal(Red, Pixel(full, 0, y));

        // 'B' is ? plus 3: the top two pixels. Rows below the lowest painted pixel are not part of the image.
        ImagePixels top = Decode("#1;2;100;0;0B")!;
        Assert.Equal((1, 2), (top.Width, top.Height));

        // 'A' is ? plus 2: only the second pixel, so the first stays clear.
        ImagePixels second = Decode("#1;2;100;0;0A")!;
        Assert.Equal((Clear, Red), (Pixel(second, 0, 0), Pixel(second, 0, 1)));
    }

    [Fact]
    public void Repeat_PaintsTheNextCharacterThatManyTimes()
    {
        ImagePixels image = Decode("#1;2;0;0;100!5~")!;

        Assert.Equal((5, 6), (image.Width, image.Height));
        Assert.All(Enumerable.Range(0, 30), i => Assert.Equal(Blue, Pixel(image, i % 5, i / 5)));
    }

    [Fact]
    public void Repeat_ThatIsNotFollowedByACharacter_IsDropped()
    {
        Assert.Equal(1, Decode("#1;2;0;0;100!5$@")!.Width);
        Assert.Equal(1, Decode("#1;2;0;0;100!0@")!.Width);
    }

    [Fact]
    public void Dollar_GoesBackToTheStartOfTheBand_AndMinus_OnToTheNext()
    {
        ImagePixels image = Decode("#1;2;100;0;0~$#2;2;0;100;0@-#3;2;0;0;100@")!;

        Assert.Equal((1, 7), (image.Width, image.Height));
        Assert.Equal(Green, Pixel(image, 0, 0));
        for (int y = 1; y < 6; y++) Assert.Equal(Red, Pixel(image, 0, y));
        Assert.Equal(Blue, Pixel(image, 0, 6));
    }

    [Fact]
    public void Colours_ChangedLater_LeaveWhatWasPaintedAlone()
    {
        ImagePixels image = Decode("#1;2;100;0;0@#1;2;0;100;0@#1@")!;

        Assert.Equal((Red, Green, Green), (Pixel(image, 0, 0), Pixel(image, 1, 0), Pixel(image, 2, 0)));
    }

    [Theory]
    [InlineData("#7;2;50;0;100", 128, 0, 255)]
    [InlineData("#7;2;200;0;100", 255, 0, 255)]
    [InlineData("#7;1;0;50;100", 0, 0, 255)]
    [InlineData("#7;1;120;50;100", 255, 0, 0)]
    [InlineData("#7;1;240;50;100", 0, 255, 0)]
    [InlineData("#7;1;180;50;100", 255, 255, 0)]
    [InlineData("#7;1;300;50;100", 0, 255, 255)]
    [InlineData("#7;1;0;50;0", 128, 128, 128)]
    [InlineData("#7;1;0;100;100", 255, 255, 255)]
    [InlineData("#2", 204, 33, 33)]
    [InlineData("#1031;2;100;0;0", 255, 0, 0)]
    [InlineData("#7", 135, 135, 135)]
    [InlineData("#20;3;10;10;10", 0, 0, 0)]
    public void Colour_IsReadInPercentOrHls(string colour, byte red, byte green, byte blue)
    {
        // HLS hue starts at blue, as DEC has it. Registers start with the VT340's colours, and 1031 is 7 again.
        Assert.Equal((red, green, blue, (byte)255), Pixel(Decode(colour + "@")!, 0, 0));
    }

    [Fact]
    public void RasterAttributes_GiveTheSize_AndTheBackgroundFillsTheRest()
    {
        ImagePixels clear = Decode("\"1;1;4;3#1;2;100;0;0@")!;
        Assert.Equal((4, 3), (clear.Width, clear.Height));
        Assert.Equal(Red, Pixel(clear, 0, 0));
        Assert.Equal(Clear, Pixel(clear, 3, 2));

        ImagePixels filled = Decode("\"1;1;4;3#1;2;100;0;0@", background: 0x102030)!;
        Assert.Equal(Red, Pixel(filled, 0, 0));
        Assert.Equal(((byte)0x10, (byte)0x20, (byte)0x30, (byte)255), Pixel(filled, 3, 2));

        // Painting past the size it gave makes the image larger, not cut off.
        ImagePixels larger = Decode("\"1;1;2;2#1;2;100;0;0!3~")!;
        Assert.Equal((3, 6), (larger.Width, larger.Height));
    }

    [Fact]
    public void Spaces_LineBreaks_AndOtherCharacters_MeanNothing()
    {
        ImagePixels image = Decode("#1;2;100;0;0\r\n@ @\u0007@%@")!;
        Assert.Equal((4, 1), (image.Width, image.Height));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#1;2;100;0;0")]
    [InlineData("????$-??")]
    public void NothingPainted_AndNoSizeGiven_IsNoImage(string data) => Assert.Null(Decode(data));

    [Fact]
    public void Image_UpToTheLargestSize_IsKept()
    {
        Assert.Equal(SixelDecoder.MaxSide, Decode($"!{SixelDecoder.MaxSide}@")!.Width);

        // Band 682 starts at row 4092; F paints its top three rows.
        Assert.Equal(4095, Decode(string.Concat(Enumerable.Repeat("-", 682)) + "F")!.Height);
    }

    [Theory]
    [InlineData("!4097@")]
    [InlineData("!99999999999999999999@")]
    [InlineData("\"1;1;4097;1@")]
    [InlineData("\"1;1;1;99999@")]
    public void Image_LargerThanAllowed_IsGivenUp(string data)
    {
        var decoder = new SixelDecoder(null);
        decoder.Write(Encoding.Latin1.GetBytes(data));

        Assert.True(decoder.Failed);
        Assert.Null(decoder.Finish());
    }

    [Theory]
    [InlineData("!10~", true)]
    [InlineData("!11~", false)]
    [InlineData("\"1;1;8;8@", false)]
    [InlineData("\"1;1;10;6@", true)]
    [InlineData("!10~-@", false)]
    [InlineData("\"1;1;60;1-@", false)]
    public void Image_WithMorePixelsInAllThanAllowed_IsGivenUp(string data, bool kept)
    {
        var decoder = new SixelDecoder(null, maxPixels: 60);
        decoder.Write(Encoding.Latin1.GetBytes(data));

        Assert.Equal(kept, decoder.Finish() is not null);
    }

    [Fact]
    public void Image_CloseToThePixelsAllowed_KeepsEveryPixel()
    {
        // Room for 1300 pixels: the second band no longer fits rows of the doubled width, so rows as wide as the image are used.
        var decoder = new SixelDecoder(null, maxPixels: 1300);
        decoder.Write("#1;2;100;0;0!100~-#2;2;0;100;0!100~"u8);
        ImagePixels image = decoder.Finish()!;

        Assert.Equal((100, 12), (image.Width, image.Height));
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 100; x++) Assert.Equal(y < 6 ? Red : Green, Pixel(image, x, y));
        }
    }

    [Fact]
    public void Image_TallerThanAllowed_IsGivenUp()
    {
        Assert.Null(Decode(string.Concat(Enumerable.Repeat("-", 682)) + "~"));
        Assert.Null(Decode(string.Concat(Enumerable.Repeat("-", 100_000)) + "@"));
    }

    [Fact]
    public void PaintingTheSameBandOverAndOver_IsCutShort()
    {
        // Seven bytes paint 24576 pixels. Allowed to go on, a few megabytes of this would take minutes.
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("!4096~$", 1_000_000)));
        var decoder = new SixelDecoder(null);

        var clock = Stopwatch.StartNew();
        Fuzz.Within(() => decoder.Write(data));

        Assert.True(decoder.Failed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Image_SentAsAnEncoderWouldSendIt_ComesBackPixelForPixel()
    {
        var random = new Random(340);
        const int width = 37, height = 23;
        (byte R, byte G, byte B)[] colours = [(255, 0, 0), (0, 128, 255), (255, 255, 255), (0, 0, 0), (51, 204, 102)];
        int[] picture = [.. Enumerable.Range(0, width * height).Select(_ => random.Next(colours.Length))];

        ImagePixels image = Decode(Encode(picture, width, height, colours))!;

        Assert.Equal((width, height), (image.Width, image.Height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = colours[picture[(y * width) + x]];
                Assert.Equal((r, g, b, (byte)255), Pixel(image, x, y));
            }
        }
    }

    [Fact]
    public void Garbage_NeverThrows_AndAnyImageIsWellFormed()
    {
        var random = new Random(1983);
        ReadOnlySpan<byte> alphabet = "?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~!#$-\";0123456789 \r\n"u8;

        for (int round = 0; round < 3000; round++)
        {
            byte[] data = Fuzz.Garbage(random, 600, alphabet);
            var decoder = new SixelDecoder(random.Next(2) == 0 ? null : 0x123456u);
            decoder.Write(data);

            if (decoder.Finish() is ImagePixels image)
            {
                Assert.InRange(image.Width, 1, SixelDecoder.MaxSide);
                Assert.InRange(image.Height, 1, SixelDecoder.MaxSide);
                Assert.Equal((long)image.Width * image.Height * 4, image.Rgba.Length);
            }
        }
    }

    // What img2sixel and the like send: the size first, every colour defined, then each band painted a colour at a time,
    // runs of the same column as repeats.
    internal static string Encode(int[] picture, int width, int height, (byte R, byte G, byte B)[] colours)
    {
        var data = new StringBuilder($"\"1;1;{width};{height}");
        for (int c = 0; c < colours.Length; c++)
            data.Append($"#{c};2;{colours[c].R * 100 / 255};{colours[c].G * 100 / 255};{colours[c].B * 100 / 255}");

        for (int band = 0; band < height; band += 6)
        {
            for (int c = 0; c < colours.Length; c++)
            {
                data.Append($"#{c}");
                for (int x = 0; x < width;)
                {
                    int bits = Column(band, c, x);
                    int run = 1;
                    while (x + run < width && Column(band, c, x + run) == bits) run++;
                    data.Append(run > 3 ? $"!{run}{(char)('?' + bits)}" : new string((char)('?' + bits), run));
                    x += run;
                }
                data.Append('$');
            }
            data.Append('-');
        }

        return data.ToString();

        // The pixels of one column of a band that have this colour, as the six bits of a sixel character.
        int Column(int band, int colour, int x)
        {
            int bits = 0;
            for (int i = 0; i < 6 && band + i < height; i++)
                if (picture[((band + i) * width) + x] == colour) bits |= 1 << i;
            return bits;
        }
    }
}
