using Elio.Core;

namespace Elio.Tests;

public sealed class SnippetTemplateTests
{
    [Fact]
    public void Blanks_AreListedOnceEach_InOrder_WithTheFirstDefault()
    {
        IReadOnlyList<SnippetBlank> blanks = SnippetTemplate.Blanks("git checkout {{branch:main}} && git push {{remote:origin}} {{branch:dev}}");

        Assert.Equal([new SnippetBlank("branch", "main"), new SnippetBlank("remote", "origin")], blanks);
    }

    [Fact]
    public void Blanks_WithoutADefault_HaveAnEmptyOne() =>
        Assert.Equal([new SnippetBlank("host", string.Empty)], SnippetTemplate.Blanks("ping {{host}}"));

    [Theory]
    [InlineData("docker ps --format '{{.Names}}'")]
    [InlineData("docker inspect -f '{{json .State}}' web")]
    [InlineData("kubectl get pods -o go-template='{{range .items}}{{.metadata.name}}{{end}}'")]
    [InlineData("echo {{ name }}")]
    [InlineData("echo {{}}")]
    [InlineData("echo {{1st}}")]
    [InlineData("echo {name}")]
    public void TemplatesForOtherPrograms_AreNotBlanks(string command)
    {
        Assert.Empty(SnippetTemplate.Blanks(command));
        Assert.Equal(command, SnippetTemplate.Fill(command, new Dictionary<string, string>()));
    }

    [Fact]
    public void Fill_PutsTheValueEverywhereTheNameIs()
    {
        string filled = SnippetTemplate.Fill("git checkout {{branch:main}} && git push origin {{branch}}", new Dictionary<string, string> { ["branch"] = "release/2.0" });

        Assert.Equal("git checkout release/2.0 && git push origin release/2.0", filled);
    }

    [Fact]
    public void Fill_KeepsTheDefault_WhereNoValueIsGiven() =>
        Assert.Equal("tail -n 100 /var/log/syslog", SnippetTemplate.Fill("tail -n {{lines:100}} {{file:/var/log/syslog}}", new Dictionary<string, string>()));

    [Fact]
    public void Fill_AnEmptyValue_LeavesTheBlankEmpty() =>
        Assert.Equal("ls ", SnippetTemplate.Fill("ls {{dir:/tmp}}", new Dictionary<string, string> { ["dir"] = string.Empty }));

    [Theory]
    [InlineData("x\nrm -rf ~")]
    [InlineData("x\rrm -rf ~")]
    [InlineData("x\u2028rm -rf ~")]
    public void Fill_NeverLetsAValueStartANewLine(string value)
    {
        string filled = SnippetTemplate.Fill("echo {{a}}", new Dictionary<string, string> { ["a"] = value });

        Assert.DoesNotContain('\n', filled);
        Assert.DoesNotContain('\r', filled);
        Assert.DoesNotContain('\u2028', filled);
        Assert.Equal("echo x rm -rf ~", filled);
    }

    [Fact]
    public void Fill_LeavesTheSnippetsOwnLinesAlone() =>
        Assert.Equal("cd /srv\nmake deploy", SnippetTemplate.Fill("cd {{dir:/srv}}\nmake deploy", new Dictionary<string, string>()));

    [Fact]
    public void Fill_DoesNotReadAValueAsABlank() =>
        Assert.Equal("echo {{other}}", SnippetTemplate.Fill("echo {{a}}", new Dictionary<string, string> { ["a"] = "{{other}}", ["other"] = "no" }));

    [Fact]
    public void ASnippetWithAFolder_SurvivesImportingFromAFile()
    {
        var library = new HostLibrary();
        var file = new HostLibrary { Snippets = [new Snippet { Name = "Restart", Command = "systemctl restart {{unit}}", Folder = "Services" }] };

        LibraryMerge.Merge(library, file, _ => false);

        Assert.Equal("Services", Assert.Single(library.Snippets).Folder);
    }
}
