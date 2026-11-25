using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// Pictures sent with the Kitty graphics protocol, through the real terminal engine: what a program on the server
/// prints is all it takes, so every size and format it claims is input from a stranger.
/// </summary>
public sealed class KittyGraphicsTests : IAsyncLifetime
{
    private readonly TerminalSession _terminal = new(new ScriptedTransport(), 80, 24);

    public Task InitializeAsync()
    {
        // Another grid than the one it was made with: see Resize_ToTheSameGrid_StillTellsTheEngineTheCellSize.
        _terminal.Resize(100, 30, 10, 20);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        TerminalImages.DecodePng = null;
        await _terminal.DisposeAsync();
    }

    private void Print(string text) => _terminal.Feed(Encoding.UTF8.GetBytes(text));

    // One graphics command: ESC _ G keys ; base64 payload ESC \.
    private static string Command(string keys, byte[]? payload = null) =>
        $"\u001b_G{keys}{(payload is null ? "" : ";" + Convert.ToBase64String(payload))}\u001b\\";

    private static byte[] Pixels(int count, int perPixel) => [.. Enumerable.Range(0, count * perPixel).Select(i => (byte)(i * 37))];

    private string Row(int row) => string.Concat(_terminal.RowCells(row) ?? []).TrimEnd();

    [Fact]
    public void Rgba_IsStoredAsSent()
    {
        byte[] pixels = Pixels(6, 4);

        Print(Command("a=t,f=32,s=3,v=2,i=1,q=2", pixels));

        ImagePixels image = _terminal.ReadImagePixels(1)!;
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(pixels, image.Rgba);
    }

    [Fact]
    public void Rgb_GetsAnOpaqueAlpha()
    {
        Print(Command("a=t,f=24,s=2,v=1,i=2,q=2", [1, 2, 3, 4, 5, 6]));

        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255], _terminal.ReadImagePixels(2)!.Rgba);
    }

    [Fact]
    public void Picture_SentInChunks_IsPutTogether()
    {
        byte[] pixels = Pixels(64 * 64, 4);
        string base64 = Convert.ToBase64String(pixels);
        var commands = new StringBuilder();
        for (int at = 0; at < base64.Length; at += 4096)
        {
            bool last = at + 4096 >= base64.Length;
            string keys = at == 0 ? "a=t,f=32,s=64,v=64,i=3,q=2," : string.Empty;
            commands.Append($"\u001b_G{keys}m={(last ? 0 : 1)};{base64.Substring(at, Math.Min(4096, base64.Length - at))}\u001b\\");
        }

        Print(commands.ToString());

        Assert.Equal(pixels, _terminal.ReadImagePixels(3)!.Rgba);
    }

    [Fact]
    public void Picture_PlacedOnScreen_IsListedWithItsSizeAndPlace()
    {
        Print("\r\n\r\n  ");
        Print(Command("a=T,f=32,s=4,v=4,i=4,c=2,r=1,q=2", Pixels(16, 4)));
        _terminal.ReadFrame((_, _) => { }, FrameRead.All);

        var images = new List<TerminalImage>();
        _terminal.ReadImages(images);

        TerminalImage image = Assert.Single(images);
        Assert.Equal((4u, 2, 2), (image.Id, image.Column, image.Row));
        Assert.Equal((20, 20), (image.Width, image.Height));
        Assert.Equal((4, 4), (image.SourceWidth, image.SourceHeight));
    }

    [Fact]
    public async Task Resize_ToTheSameGrid_StillTellsTheEngineTheCellSize()
    {
        await using var fresh = new TerminalSession(new ScriptedTransport(), 80, 24);
        fresh.Resize(80, 24, 10, 20);

        fresh.Feed(Encoding.ASCII.GetBytes(Command("a=T,f=32,s=4,v=4,i=4,c=2,r=1,q=2", Pixels(16, 4))));
        fresh.ReadFrame((_, _) => { }, FrameRead.All);
        var images = new List<TerminalImage>();
        fresh.ReadImages(images);

        Assert.Equal((20, 20), (Assert.Single(images).Width, images[0].Height));
    }

    [Fact]
    public void Picture_ThatIsDeleted_IsGone()
    {
        Print(Command("a=T,f=32,s=1,v=1,i=5,q=2", [1, 2, 3, 4]));
        Print(Command("a=d,d=I,i=5,q=2"));
        _terminal.ReadFrame((_, _) => { }, FrameRead.All);

        var images = new List<TerminalImage>();
        _terminal.ReadImages(images);
        Assert.Empty(images);
    }

    [Theory]
    [InlineData("a=t,f=32,s=100000,v=100000,i=6,q=2")]
    [InlineData("a=t,f=32,s=4294967295,v=4294967295,i=6,q=2")]
    [InlineData("a=t,f=32,s=3,v=3,i=6,q=2")]
    [InlineData("a=t,f=24,s=0,v=0,i=6,q=2")]
    [InlineData("a=t,f=7,s=1,v=1,i=6,q=2")]
    public void Picture_WhoseSizeOrFormatDoesNotFitItsBytes_IsRefusedAndTheTerminalCarriesOn(string keys)
    {
        Print(Command(keys, Pixels(2, 4)));
        Print("still here");

        ImagePixels? image = _terminal.ReadImagePixels(6);
        Assert.True(image is null || image.Rgba.Length == (long)image.Width * image.Height * 4);
        Assert.Contains("still here", Row(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Png_IsHandedToTheDecoderAndAWellFormedResultKept()
    {
        TerminalImages.DecodePng = _ => new ImagePixels(1, 2, [9, 8, 7, 6, 5, 4, 3, 2]);

        Print(Command("a=t,f=100,i=7,q=2", "\u0089PNG not really"u8.ToArray()));

        ImagePixels image = _terminal.ReadImagePixels(7)!;
        Assert.Equal((1, 2), (image.Width, image.Height));
        Assert.Equal([9, 8, 7, 6, 5, 4, 3, 2], image.Rgba);
    }

    public static TheoryData<string> BadDecodes => ["throws", "null", "huge", "short", "negative"];

    [Theory]
    [MemberData(nameof(BadDecodes))]
    public void Png_ThatTheDecoderCannotOrMustNotGiveBack_IsRefusedWithoutHarm(string outcome)
    {
        TerminalImages.DecodePng = _ => outcome switch
        {
            "throws" => throw new InvalidOperationException("broken file"),
            "null" => null,
            "huge" => new ImagePixels(1 << 16, 1 << 16, new byte[4]),
            "short" => new ImagePixels(10, 10, new byte[10]),
            _ => new ImagePixels(-1, -1, []),
        };

        Print(Command("a=t,f=100,i=8,q=2", "\u0089PNG"u8.ToArray()));
        Print("after");

        Assert.Null(_terminal.ReadImagePixels(8));
        Assert.Contains("after", Row(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_OfGarbage_NeverHurtTheTerminal()
    {
        var random = new Random(1977);
        string[] keys = ["a=t", "a=T", "a=p", "a=d", "a=q", "f=24", "f=32", "f=100", "s=2", "v=2", "s=99999", "i=1", "i=2", "I=3", "p=1", "m=1", "m=0", "c=1", "r=1", "x=1", "y=1", "w=1", "h=1", "z=-1", "C=1", "U=1", "q=2", "o=z", "t=f", "t=d", "t=s"];

        for (int round = 0; round < 2000; round++)
        {
            string chosen = string.Join(',', Enumerable.Range(0, random.Next(8)).Select(_ => keys[random.Next(keys.Length)]));
            byte[] payload = random.Next(4) == 0 ? Fuzz.Garbage(random, 64) : Pixels(random.Next(16), 4);
            string body = random.Next(5) == 0 ? Encoding.Latin1.GetString(Fuzz.Garbage(random, 40)) : Convert.ToBase64String(payload);

            Print($"\u001b_G{chosen};{body}\u001b\\");

            for (uint id = 1; id <= 3; id++)
            {
                if (_terminal.ReadImagePixels(id) is ImagePixels image)
                    Assert.True(image.Width > 0 && image.Height > 0 && image.Rgba.Length == (long)image.Width * image.Height * 4, $"round {round}");
            }
        }

        Print("\u001b[2J\u001b[Hdone");
        _terminal.ReadFrame((_, _) => { }, FrameRead.All);
        Assert.StartsWith("done", Row(0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("t")]
    public void Picture_FromAFileOnThisComputer_IsNotReadOnAProgramsSayso(string medium)
    {
        // The program is on the server; the path it names is on this computer, which it has no business reading.
        string path = Path.Combine(TestData.Root, "kitty-" + Guid.NewGuid().ToString("N") + ".rgba");
        File.WriteAllBytes(path, [10, 20, 30, 40]);
        try
        {
            Print(Command($"a=t,t={medium},f=32,s=1,v=1,i=9,q=2", Encoding.UTF8.GetBytes(path)));

            Assert.Null(_terminal.ReadImagePixels(9));
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
