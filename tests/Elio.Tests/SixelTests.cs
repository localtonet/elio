using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// Sixel images taken out of a program's output and shown: first what the filter in front of the engine lets through,
/// then the whole way through a real terminal, where the image ends up drawn by the engine's Kitty graphics.
/// </summary>
public sealed class SixelTests
{
    // A 3 by 7 image: three red columns of six, the first painted over in green, then a blue pixel on the next band.
    private const string Image = "\u001bP0;0;0q\"1;1;3;7#1;2;100;0;0!3~$#2;2;0;100;0~-#3;2;0;0;100@\u001b\\";

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>
    /// Everything the filter passed on, with each image it took out written where it was as [image W×H], after
    /// feeding it the pieces in order.
    /// </summary>
    private static string Filter(IEnumerable<byte[]> pieces, List<ImagePixels>? images = null)
    {
        var log = new StringBuilder();
        var filter = new SixelFilter(
            data => log.Append(Encoding.Latin1.GetString(data)),
            image =>
            {
                images?.Add(image);
                log.Append($"[image {image.Width}x{image.Height}]");
            },
            clear => new SixelDecoder(clear ? null : 0x000000));

        foreach (byte[] piece in pieces) filter.Write(piece);
        return log.ToString();
    }

    private static string Filter(string text) => Filter([Bytes(text)]);

    // Every way of cutting the text in two, and one byte at a time.
    private static IEnumerable<byte[][]> Cuts(string text)
    {
        byte[] data = Bytes(text);
        for (int at = 0; at <= data.Length; at++) yield return [data[..at], data[at..]];
        yield return [.. data.Select(b => new[] { b })];
    }

    public static TheoryData<string> NotImages =>
    [
        "plain text, then \u001b[1;31mred\u001b[0m and a title \u001b]0;hello\u0007",
        "DECRQSS \u001bP$qm\u001b\\ and XTGETTCAP \u001bP+q544e\u001b\\",
        "tmux passthrough \u001bPtmux;\u001b\u001b[1m\u001b\\ and control mode \u001bP1000p%begin\r\n",
        "a Kitty picture \u001b_Ga=T,f=24,s=1,v=1;AAAA\u001b\\ and an Escape on its own \u001b then more",
        "DCS with a private marker \u001bP?1q\u001b\\ or an intermediate \u001bP1$q\u001b\\ is not an image",
        "\u001bP" + new string('1', 40) + "q has more numbers than any image",
        "UTF-8 Å\u0090 and the bytes \u0090 and \u009c, which this engine does not take as DCS or ST",
        "\u001b\u001b\u001bP\u001b\u001bP1\u001bPx\u001b[",
    ];

    [Theory]
    [MemberData(nameof(NotImages))]
    public void Output_ThatIsNoImage_PassesByteForByte_HoweverItIsCut(string text)
    {
        foreach (byte[][] pieces in Cuts(text)) Assert.Equal(text, Filter(pieces));
    }

    [Fact]
    public void Image_IsTakenOut_AndWhatIsAroundIt_StaysInOrder()
    {
        var images = new List<ImagePixels>();
        Assert.Equal("ab[image 3x7]cd\u001b[1mef", Filter([Bytes("ab" + Image + "cd\u001b[1mef")], images));

        byte[][] pixels = [.. Assert.Single(images).Rgba.Chunk(4)];
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[0]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[2]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[6 * 3]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels[(6 * 3) + 1]);
    }

    [Fact]
    public void Image_CutAnywhere_IsTheSame()
    {
        string text = "ab" + Image + "cd";
        string whole = Filter(text);

        foreach (byte[][] pieces in Cuts(text)) Assert.Equal(whole, Filter(pieces));

        // And every way of cutting it in three.
        byte[] data = Bytes(text);
        for (int first = 0; first <= data.Length; first++)
        {
            for (int second = first; second <= data.Length; second++)
                Assert.Equal(whole, Filter([data[..first], data[first..second], data[second..]]));
        }
    }

    [Fact]
    public void Images_OneAfterAnother_AreEachShown()
    {
        Assert.Equal("[image 3x7][image 3x7]x[image 3x7]", Filter(Image + Image + "x" + Image));
    }

    [Theory]
    [InlineData("\u001bPq#1~\u001b[2Jafter", "[image 1x6]\u001b[2Jafter")]
    [InlineData("\u001bPq#1~\u001bPq#1@\u001b\\after", "[image 1x6][image 1x1]after")]
    [InlineData("\u001bPq#1~\u0018after", "after")]
    [InlineData("\u001bPq#1~\u001aafter", "after")]
    [InlineData("\u001bPq\u001b\\after", "after")]
    [InlineData("before\u001bPq#1~", "before")]
    public void Image_EndsAtST_OrAnyOtherEscape_AndCanOrSubCancelIt(string text, string passed)
    {
        foreach (byte[][] pieces in Cuts(text)) Assert.Equal(passed, Filter(pieces));
    }

    [Fact]
    public void Image_ThatNeverEnds_HoldsNoData_AndPassesNothingOn()
    {
        // A painted pixel, then more carriage returns than an image may have bytes. Data that size is never kept: the
        // image is given up past the limit, and what follows the end of it is ordinary output again.
        byte[] chunk = new byte[1024 * 1024];
        Array.Fill(chunk, (byte)'$');
        var log = new StringBuilder();
        int shown = 0;
        var filter = new SixelFilter(data => log.Append(Encoding.Latin1.GetString(data)), _ => shown++, _ => new SixelDecoder(null));

        filter.Write(Bytes("before\u001bPq#1;2;100;0;0@"));
        long allocated = Fuzz.Allocated(() =>
        {
            for (int i = 0; i <= SixelFilter.MaxImageBytes / chunk.Length; i++) filter.Write(chunk);
        });
        filter.Write(Bytes("\u001b\\after"));

        Assert.Equal("beforeafter", log.ToString());
        Assert.Equal(0, shown);
        Assert.True(allocated < 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void Filter_FedGarbage_PassesTheSameHoweverItIsCut()
    {
        var random = new Random(1987);
        ReadOnlySpan<byte> alphabet = "\u001bP\\q0;1#2!-$\"~@?Ax[m\u0018\u001a"u8;

        for (int round = 0; round < 2000; round++)
        {
            byte[] data = Fuzz.Garbage(random, 300, alphabet);
            var images = new List<ImagePixels>();
            string whole = Filter([data], images);

            var again = new List<ImagePixels>();
            Assert.Equal(whole, Filter(Fuzz.Pieces(random, data, 16), again));
            Assert.Equal(images.Count, again.Count);
            for (int i = 0; i < images.Count; i++) Assert.Equal(images[i].Rgba, again[i].Rgba);
        }
    }

    private static TerminalSession Terminal(ScriptedTransport? transport = null)
    {
        var terminal = new TerminalSession(transport ?? new ScriptedTransport(), 100, 30);
        terminal.Resize(100, 30, 10, 20);
        return terminal;
    }

    private static List<TerminalImage> ImagesOn(TerminalSession terminal)
    {
        terminal.ReadFrame((_, _) => { }, FrameRead.All);
        var images = new List<TerminalImage>();
        terminal.ReadImages(images);
        return images;
    }

    private static string Row(TerminalSession terminal, int row) => string.Concat(terminal.RowCells(row) ?? []).TrimEnd();

    // A red image of this size, sent with its raster attributes the way encoders send them.
    private static string Solid(int width, int height, string introducer = "\u001bP0;0;0q")
    {
        var image = new StringBuilder(introducer + $"\"1;1;{width};{height}#1;2;100;0;0");
        for (int band = 0; band < height; band += 6)
        {
            int rows = Math.Min(6, height - band);
            image.Append($"!{width}{(char)('?' + (1 << rows) - 1)}-");
        }
        return image.Append("\u001b\\").ToString();
    }

    [EngineFact]
    public async Task Image_IsDrawnAtTheCursor_AtItsOwnSize()
    {
        await using TerminalSession terminal = Terminal();
        terminal.Feed(Bytes("\r\n\r\n   " + Solid(30, 50)));

        TerminalImage image = Assert.Single(ImagesOn(terminal));
        Assert.Equal((3, 2), (image.Column, image.Row));
        Assert.Equal((30, 50), (image.Width, image.Height));
        Assert.Equal((30, 50), (image.SourceWidth, image.SourceHeight));

        ImagePixels pixels = terminal.ReadImagePixels(image.Id)!;
        Assert.Equal((30, 50), (pixels.Width, pixels.Height));
        Assert.All(pixels.Rgba.Chunk(4), pixel => Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixel));
    }

    [EngineFact]
    public async Task Cursor_EndsBelowTheImage_InTheColumnItStartedIn()
    {
        await using TerminalSession terminal = Terminal();

        // 50 pixels over cells 20 tall is three rows.
        terminal.Feed(Bytes("\r\n\r\n   " + Solid(30, 50) + "X"));

        Assert.Equal("   X", Row(terminal, 5));
        Assert.Equal("", Row(terminal, 4));
    }

    [EngineFact]
    public async Task Image_AtTheBottom_ScrollsTheScreenUp_WithItself()
    {
        await using TerminalSession terminal = Terminal();
        terminal.Feed(Bytes("first\u001b[30;1H" + Solid(10, 40) + "X"));

        Assert.Equal("X", Row(terminal, 29));
        Assert.Equal(27, Assert.Single(ImagesOn(terminal)).Row);
        Assert.Equal("", Row(terminal, 0));
    }

    [EngineFact]
    public async Task UnpaintedPixels_AreTheBackground_UnlessTheImageAsksForThemClear()
    {
        await using TerminalSession terminal = Terminal();
        uint background = TerminalTheme.Default.Background;
        byte[] backgroundPixel = [(byte)(background >> 16), (byte)(background >> 8), (byte)background, 255];

        terminal.Feed(Bytes("\u001bPq\"1;1;2;1#1;2;100;0;0@\u001b\\\u001bP0;1q\"1;1;2;1#1;2;100;0;0@\u001b\\"));

        List<TerminalImage> images = ImagesOn(terminal);
        Assert.Equal(2, images.Count);
        byte[][] filled = [.. terminal.ReadImagePixels(images.Single(i => i.Row == 0).Id)!.Rgba.Chunk(4)];
        byte[][] clear = [.. terminal.ReadImagePixels(images.Single(i => i.Row == 1).Id)!.Rgba.Chunk(4)];
        Assert.Equal(new byte[][] { [255, 0, 0, 255], backgroundPixel }, filled);
        Assert.Equal(new byte[][] { [255, 0, 0, 255], [0, 0, 0, 0] }, clear);
    }

    [EngineFact]
    public async Task Image_CutAtAnyByte_IsTheSameImage()
    {
        string text = "ab" + Image + "cd";
        foreach (byte[][] pieces in Cuts(text))
        {
            await using TerminalSession terminal = Terminal();
            foreach (byte[] piece in pieces) terminal.Feed(piece);

            TerminalImage image = Assert.Single(ImagesOn(terminal));
            Assert.Equal((2, 0, 3, 7), (image.Column, image.Row, image.SourceWidth, image.SourceHeight));
            Assert.Equal("ab", Row(terminal, 0));
            Assert.Equal("  cd", Row(terminal, 1));
        }
    }

    [EngineTheory]
    [InlineData(1581, 1581, true)]
    [InlineData(4096, 610, true)]
    [InlineData(1582, 1582, false)]
    [InlineData(SixelDecoder.MaxSide, SixelDecoder.MaxSide, false)]
    public async Task Image_IsShownUpToWhatTheEngineHolds(int width, int height, bool shown)
    {
        // The engine keeps 10 MB of pictures unless told otherwise, and refuses one larger than that outright. A sixel
        // image past it is given up while it is read, and the terminal carries on.
        await using TerminalSession terminal = Terminal();
        terminal.Feed(Bytes(Solid(width, height) + "\u001b[Hafter"));

        List<TerminalImage> images = ImagesOn(terminal);
        Assert.Equal(shown, images.Count == 1);
        if (shown) Assert.Equal((width, height), (images[0].SourceWidth, images[0].SourceHeight));
        Assert.Equal("after", Row(terminal, 0)[..5]);
    }

    [EngineFact]
    public async Task DeviceAttributes_SaySixelWorks()
    {
        var transport = new ScriptedTransport();
        await using TerminalSession terminal = Terminal(transport);
        terminal.Start();

        terminal.Feed(Bytes("\u001b[c"));
        Assert.Equal("\u001b[?62;4;22c", Encoding.ASCII.GetString(await transport.HeardAsync()));

        terminal.Feed(Bytes("\u001b[>c"));
        Assert.Equal("\u001b[>1;0;0c", Encoding.ASCII.GetString(await transport.HeardAsync()));
    }

    [EngineFact]
    public async Task SizesInPixels_AreAnswered_OnceTheCellSizeIsKnown()
    {
        var transport = new ScriptedTransport();
        await using var unsized = new TerminalSession(transport, 100, 30);
        unsized.Start();
        unsized.Feed(Bytes("\u001b[16t\u001b[c"));
        Assert.Equal("\u001b[?62;4;22c", Encoding.ASCII.GetString(await transport.HeardAsync()));

        unsized.Resize(100, 30, 10, 20);
        unsized.Feed(Bytes("\u001b[14t"));
        Assert.Equal("\u001b[4;600;1000t", Encoding.ASCII.GetString(await transport.HeardAsync()));
        unsized.Feed(Bytes("\u001b[16t"));
        Assert.Equal("\u001b[6;20;10t", Encoding.ASCII.GetString(await transport.HeardAsync()));
        unsized.Feed(Bytes("\u001b[18t"));
        Assert.Equal("\u001b[8;30;100t", Encoding.ASCII.GetString(await transport.HeardAsync()));
    }

    [EngineFact]
    public async Task OtherDcs_StillReachesTheEngine()
    {
        var transport = new ScriptedTransport();
        await using TerminalSession terminal = Terminal(transport);
        terminal.Start();

        // DECRQSS, cut where the filter has to hold the start back to see whether it is an image.
        terminal.Feed(Bytes("one \u001b"));
        terminal.Feed(Bytes("P"));
        terminal.Feed(Bytes("$qm\u001b\\two"));

        Assert.Equal("\u001bP1$r0m\u001b\\", Encoding.ASCII.GetString(await transport.HeardAsync()));
        Assert.Equal("one two", Row(terminal, 0));
        Assert.Empty(ImagesOn(terminal));
    }

    [EngineFact]
    public async Task Image_ThatNeverEnds_KeepsTheTerminalWorking()
    {
        await using TerminalSession terminal = Terminal();
        byte[] chunk = Bytes(string.Concat(Enumerable.Repeat("#1;2;100;0;0!4096~$", 64 * 1024)));

        terminal.Feed(Bytes("\u001bPq"));
        Fuzz.Within(() =>
        {
            for (int fed = 0; fed <= SixelFilter.MaxImageBytes; fed += chunk.Length) terminal.Feed(chunk);
        });
        terminal.Feed(Bytes("\u001b\\done"));

        Assert.Equal("done", Row(terminal, 0));
        Assert.Empty(ImagesOn(terminal));
    }

    [EngineFact]
    public async Task Garbage_ThroughTheTerminal_NeverHurtsIt()
    {
        var random = new Random(2026);
        byte[] alphabet = "\u001bP\\q0;1#2!-$\"~@?Ax[mc\u0018"u8.ToArray();
        await using TerminalSession terminal = Terminal();

        Fuzz.Within(() =>
        {
            long allocated = Fuzz.Allocated(() =>
            {
                for (int round = 0; round < 500; round++)
                {
                    byte[] data = random.Next(3) == 0 ? Fuzz.Mutate(random, Bytes(Solid(1 + random.Next(80), 1 + random.Next(80)))) : Fuzz.Garbage(random, 400, alphabet);
                    foreach (byte[] piece in Fuzz.Pieces(random, data)) terminal.Feed(piece);
                }
            });

            // Every image the garbage makes is a few kilobytes at most, and the data itself is never kept.
            Assert.True(allocated < 512L * 1024 * 1024, $"{allocated} bytes allocated");
        });

        foreach (TerminalImage image in ImagesOn(terminal))
        {
            if (terminal.ReadImagePixels(image.Id) is ImagePixels pixels)
                Assert.Equal((long)pixels.Width * pixels.Height * 4, pixels.Rgba.Length);
        }

        terminal.Feed(Bytes("\u001b\\\u0018\u001b[2J\u001b[Hdone"));
        Assert.Equal("done", Row(terminal, 0));
    }
}
