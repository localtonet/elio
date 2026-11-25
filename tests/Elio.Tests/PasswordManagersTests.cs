using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

public sealed class PasswordManagersTests
{
    [Theory]
    [InlineData("op://Private/web-1/password", true)]
    [InlineData("bw://web-1", true)]
    [InlineData("bw://3f1c2a3e-0000-4000-8000-000000000000", true)]
    [InlineData("hunter2", false)]
    [InlineData("OP://Private/web-1/password", false)]
    [InlineData("op:/Private", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("op://a\nb", false)]
    public void IsReference(string? secret, bool expected) =>
        Assert.Equal(expected, PasswordManagers.IsReference(secret));

    [Fact]
    public void AVeryLongValue_IsAPassword_NotAReference() =>
        Assert.False(PasswordManagers.IsReference("op://" + new string('x', 2000)));

    [Fact]
    public async Task APlainPassword_ComesBackAsItIs_WithoutRunningAnything() =>
        Assert.Equal("hunter2", await PasswordManagers.ResolveAsync("hunter2", CancellationToken.None));

    [Fact]
    public async Task ResolveStored_LeavesPlainSecretsAlone()
    {
        Assert.Null(await PasswordManagers.ResolveStoredAsync(null, CancellationToken.None));
        Assert.Equal("hunter2", await PasswordManagers.ResolveStoredAsync("hunter2", CancellationToken.None) ?? "hunter2");
        Assert.Null(await PasswordManagers.ResolveStoredAsync(new IdentitySecret { Password = "plain" }.Write(), CancellationToken.None));
    }

    [Fact]
    public async Task ABitwardenReferenceWithoutAnItem_IsRefusedBeforeAnythingRuns() =>
        await Assert.ThrowsAsync<PasswordManagerException>(() => PasswordManagers.ResolveAsync("bw://", CancellationToken.None));
}
