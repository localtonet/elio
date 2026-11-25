using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

public sealed class KubernetesPodsTests
{
    [Fact]
    public void Parse_MakesAShellForEachRunningPod()
    {
        List<HostEntry> pods = KubernetesPods.Parse("default\tweb-7d9f8b6c4-x2kqp\tRunning\nkube-system\tcoredns-5d78c9869d-abcde\tRunning\n");

        Assert.Equal(["default/web-7d9f8b6c4-x2kqp", "kube-system/coredns-5d78c9869d-abcde"], pods.Select(p => p.Name));
        HostEntry web = pods[0];
        Assert.Equal(HostKind.Local, web.Kind);
        Assert.StartsWith("kubectl exec -it -n default web-7d9f8b6c4-x2kqp -- ", web.Address);
        Assert.Equal(["Running"], web.Tags);
        Assert.False(web.AutoReconnect);
    }

    [Fact]
    public void Parse_GivesTheSamePodTheSameId() =>
        Assert.Equal(KubernetesPods.Parse("a\tb\tRunning")[0].Id, KubernetesPods.Parse("a\tb\tRunning")[0].Id);

    [Theory]
    [InlineData("default\tweb; rm -rf ~\tRunning")]
    [InlineData("default\t-n\tRunning")]
    [InlineData("de fault\tweb\tRunning")]
    [InlineData("default\tWeb\tRunning")]
    [InlineData("default\t$(id)\tRunning")]
    [InlineData("default")]
    [InlineData("\t\t")]
    public void Parse_LeavesOutNamesKubernetesWouldNotAllow(string line) =>
        Assert.Empty(KubernetesPods.Parse(line));
}
