using System.Text;
using System.Text.Json;
using Elio.Terminal;

namespace Elio.Tests;

public sealed class CastRecorderTests
{
    private static (CastRecorder Recorder, MemoryStream File, Func<string[]> Lines) Make(int columns = 80, int rows = 24, string? title = null)
    {
        var file = new KeepOpenStream();
        double now = 0;
        var recorder = new CastRecorder(file, "test.cast", columns, rows, title, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), () => now += 0.5);
        return (recorder, file, () => Encoding.UTF8.GetString(file.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Header_DescribesTheTerminal()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make(120, 40, "web-1");
        recorder.Dispose();

        using JsonDocument header = JsonDocument.Parse(lines()[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(120, header.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(40, header.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(1_700_000_000, header.RootElement.GetProperty("timestamp").GetInt64());
        Assert.Equal("web-1", header.RootElement.GetProperty("title").GetString());
        Assert.Equal("xterm-256color", header.RootElement.GetProperty("env").GetProperty("TERM").GetString());
    }

    [Fact]
    public void Output_IsOneEventPerPiece_WithTimeKindAndText()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make();
        recorder.Write("hello\r\n"u8);
        recorder.Write("\u001b[31mred\u001b[0m"u8);
        recorder.Dispose();

        string[] events = lines()[1..];
        Assert.Equal(2, events.Length);

        using JsonDocument first = JsonDocument.Parse(events[0]);
        Assert.Equal(0.5, first.RootElement[0].GetDouble());
        Assert.Equal("o", first.RootElement[1].GetString());
        Assert.Equal("hello\r\n", first.RootElement[2].GetString());

        using JsonDocument second = JsonDocument.Parse(events[1]);
        Assert.Equal(1.0, second.RootElement[0].GetDouble());
        Assert.Equal("\u001b[31mred\u001b[0m", second.RootElement[2].GetString());
    }

    [Fact]
    public void ACharacterSplitBetweenPieces_IsKeptWhole()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make();
        byte[] euro = Encoding.UTF8.GetBytes("€");
        recorder.Write(euro.AsSpan(0, 1));
        recorder.Write(euro.AsSpan(1));
        recorder.Dispose();

        string all = string.Concat(lines()[1..].Select(line =>
        {
            using JsonDocument e = JsonDocument.Parse(line);
            return e.RootElement[2].GetString();
        }));
        Assert.Equal("€", all);
    }

    [Fact]
    public void Resize_IsRecorded()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make();
        recorder.Resize(132, 50);
        recorder.Dispose();

        using JsonDocument resize = JsonDocument.Parse(lines()[1]);
        Assert.Equal("r", resize.RootElement[1].GetString());
        Assert.Equal("132x50", resize.RootElement[2].GetString());
    }

    [Fact]
    public void AfterDispose_NothingIsWritten()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make();
        recorder.Dispose();
        recorder.Write("late"u8);
        recorder.Resize(1, 1);

        Assert.Single(lines());
    }

    [Fact]
    public void EveryLine_IsOneLineOfJson_WhateverTheOutput()
    {
        (CastRecorder recorder, _, Func<string[]> lines) = Make();
        recorder.Write("line one\nline two\r\n\"quoted\"\\ and \u0000 nul"u8);
        recorder.Dispose();

        string[] all = lines();
        Assert.Equal(2, all.Length);
        foreach (string line in all) JsonDocument.Parse(line).Dispose();
    }

    // The recorder owns and closes its stream; the test still wants to read it afterwards.
    private sealed class KeepOpenStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}

public sealed class HyperlinkTests
{
    [Theory]
    [InlineData("https://example.com/docs", "https://example.com/docs")]
    [InlineData("http://10.0.0.5:8080/", "http://10.0.0.5:8080/")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    public void WebAddresses_AreOpened(string target, string expected) =>
        Assert.Equal(expected, TerminalLinks.FromHyperlink(target)?.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("ssh://evil")]
    [InlineData("https://example.com/\u0007")]
    [InlineData("relative/path")]
    [InlineData("https://")]
    public void AnythingElse_IsNot(string? target) =>
        Assert.Null(TerminalLinks.FromHyperlink(target));
}
