using Elio.UI;

namespace Elio.Tests;

public sealed class AiActivityTests
{
    [Fact]
    public void ALogLine_IsReadIntoItsFourParts()
    {
        AiCommand? command = AiActivity.Parse("2026-11-12T09:53:19Z\tlocalmac\texit 0\tsw_vers -productVersion; whoami");

        Assert.NotNull(command);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 53, 19, DateTimeKind.Utc), command.Utc);
        Assert.Equal("localmac", command.Server);
        Assert.Equal("exit 0", command.Outcome);
        Assert.Equal("sw_vers -productVersion; whoami", command.Command);
    }

    [Fact]
    public void ATabInsideTheCommand_StaysInTheCommand()
    {
        AiCommand? command = AiActivity.Parse("2026-11-12T09:53:19Z\tweb-1\tfailed\tprintf 'a\tb'");

        Assert.Equal("printf 'a\tb'", command?.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a log line")]
    [InlineData("yesterday\tweb-1\texit 0\tuptime")]
    [InlineData("2026-11-12T09:53:19Z\tweb-1\texit 0")]
    public void WhatIsNotALogLine_IsLeftOut(string line)
    {
        Assert.Null(AiActivity.Parse(line));
    }

    private const string Start = """{"id":"a1","t":"start","utc":"2026-11-12T09:53:19Z","server":"localmac","command":"uptime"}""";

    [Fact]
    public void AStartedCommand_IsListedAsStillRunning()
    {
        var runs = new List<AiRun>();

        (AiRun Run, bool Ended)? taken = AiRuns.Take(Start, runs);

        Assert.NotNull(taken);
        Assert.False(taken.Value.Ended);
        AiRun run = Assert.Single(runs);
        Assert.Equal("localmac", run.Server);
        Assert.Equal("uptime", run.Command);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 53, 19, DateTimeKind.Utc), run.StartedUtc);
        Assert.Null(run.Outcome);
    }

    [Fact]
    public void ItsEnd_FillsInWhatItPrinted()
    {
        var runs = new List<AiRun>();
        AiRuns.Take(Start, runs);

        (AiRun Run, bool Ended)? taken = AiRuns.Take("""{"id":"a1","t":"end","utc":"2026-11-12T09:53:20Z","outcome":"exit 1","ms":1250,"output":"up 3 days\n","error":"warning"}""", runs);

        Assert.True(taken?.Ended);
        AiRun run = Assert.Single(runs);
        Assert.Equal("exit 1", run.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), run.Duration);
        Assert.Equal("up 3 days\n", run.Output);
        Assert.Equal("warning", run.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"id\":\"a1\",\"t\":\"sta")]
    [InlineData("[1,2]")]
    [InlineData("{\"t\":\"start\"}")]
    [InlineData("{\"id\":\"zz\",\"t\":\"end\",\"outcome\":\"exit 0\"}")]
    public void ALineThatIsNotOne_OrEndsAnUnknownCommand_ChangesNothing(string line)
    {
        var runs = new List<AiRun>();
        AiRuns.Take(Start, runs);

        Assert.Null(AiRuns.Take(line, runs));
        Assert.Null(Assert.Single(runs).Outcome);
    }

    [Fact]
    public void OnlyTheNewestCommandsAreKept()
    {
        var runs = new List<AiRun>();
        for (int i = 0; i < 230; i++)
            AiRuns.Take($$"""{"id":"r{{i}}","t":"start","utc":"2026-11-12T09:53:19Z","server":"s","command":"c"}""", runs);

        Assert.Equal(200, runs.Count);
        Assert.Equal("r229", runs[^1].Id);
    }
}
