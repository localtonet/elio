using System.Text;
using Elio.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Elio.Tests;

public sealed class SshKeysTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("what a server would ask the key to sign");

    // RSA is left out of the passphrase cases only because making a 4096-bit key takes seconds; the one case
    // below covers its fields, and the encryption around them is the same for every type.
    [Theory]
    [InlineData(SshKeyType.Ed25519, null)]
    [InlineData(SshKeyType.Ed25519, "correct horse")]
    [InlineData(SshKeyType.EcdsaP256, null)]
    [InlineData(SshKeyType.EcdsaP256, "correct horse")]
    [InlineData(SshKeyType.Rsa4096, null)]
    public void Generate_WhatItWrites_LoadsWithTheLoaderConnectionsUse_AndSigns(SshKeyType type, string? passphrase)
    {
        GeneratedKey made = SshKeys.Generate(type, "laptop", passphrase);

        using PrivateKeyFile loaded = SshKeys.Load(made.PrivateKey, passphrase);

        // The public half SSH.NET works out from the private key is the one that was written into the public line.
        string[] line = made.PublicKey.Split(' ');
        Assert.Equal(3, line.Length);
        Assert.Equal("laptop", line[2]);
        Assert.All(loaded.HostKeyAlgorithms, algorithm => Assert.Equal(line[1], Convert.ToBase64String(algorithm.Data)));
        Assert.Equal("laptop", loaded.Key.Comment);

        // And the loaded key works: what it signs, it verifies, and nothing else.
        HostAlgorithm signer = loaded.HostKeyAlgorithms.First();
        byte[] signature = loaded.Key.Sign(Message);
        Assert.True(loaded.Key.VerifySignature(Message, signature));
        Assert.False(loaded.Key.VerifySignature([.. Message, 1], signature));
        Assert.NotEmpty(signer.Sign(Message));
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519, "ssh-ed25519")]
    [InlineData(SshKeyType.EcdsaP256, "ecdsa-sha2-nistp256")]
    public void Generate_PublicLine_IsAPlainPublicKeyLineOfThatType(SshKeyType type, string name)
    {
        GeneratedKey made = SshKeys.Generate(type, "me@laptop", null);

        Assert.StartsWith(name + " ", made.PublicKey, StringComparison.Ordinal);
        Assert.True(SshKeys.IsPublicLine(made.PublicKey));
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", made.PrivateKey, StringComparison.Ordinal);
        Assert.EndsWith("-----END OPENSSH PRIVATE KEY-----\n", made.PrivateKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TwoKeys_AreDifferent() =>
        Assert.NotEqual(SshKeys.Generate(SshKeyType.Ed25519, "a", null).PublicKey, SshKeys.Generate(SshKeyType.Ed25519, "a", null).PublicKey);

    [Fact]
    public void Load_EncryptedKeyWithoutPassphrase_SaysAPassphraseIsNeeded()
    {
        GeneratedKey made = SshKeys.Generate(SshKeyType.Ed25519, "laptop", "correct horse");

        Assert.Throws<SshPassPhraseNullOrEmptyException>(() => SshKeys.Load(made.PrivateKey, null));
    }

    [Fact]
    public void Load_EncryptedKeyWithTheWrongPassphrase_Fails()
    {
        GeneratedKey made = SshKeys.Generate(SshKeyType.Ed25519, "laptop", "correct horse");

        Assert.ThrowsAny<SshException>(() => SshKeys.Load(made.PrivateKey, "battery staple"));
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519, null)]
    [InlineData(SshKeyType.EcdsaP256, "correct horse")]
    public void PublicLine_FromThePrivateKey_IsTheLineGenerateGave(SshKeyType type, string? passphrase)
    {
        GeneratedKey made = SshKeys.Generate(type, "laptop", passphrase);

        Assert.Equal(made.PublicKey, SshKeys.PublicLine(made.PrivateKey, passphrase, "ignored, the key has a name"));
    }

    [Theory]
    [InlineData("laptop", "laptop")]
    [InlineData("  me@work-laptop  ", "me@work-laptop")]
    [InlineData("my key; rm -rf ~", "my-key-rm--rf")]
    [InlineData("$(reboot)", "reboot")]
    [InlineData("a\"b'c`d\\e\nf", "a-b-c-d-e-f")]
    [InlineData("", "elio")]
    [InlineData("'''", "elio")]
    public void SafeComment_LeavesNothingAShellWouldRead(string given, string expected) =>
        Assert.Equal(expected, SshKeys.SafeComment(given));

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS", true)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS me@laptop", true)]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQ== work_key-2", true)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS two words", false)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS $(reboot)", false)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k\"; reboot; \"", false)]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS me\nssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQ==", false)]
    [InlineData("command=\"sh\" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS", false)]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAJ3hB5SAF6mBXPlZlRoJEZi0KSIN+NU2iGiaXZXi9CDrgVxp7", false)]
    [InlineData("", false)]
    public void IsPublicLine_TakesOnlyAKeyTypeBase64AndAPlainComment(string line, bool expected) =>
        Assert.Equal(expected, SshKeys.IsPublicLine(line));

    [Fact]
    public void InstallCommand_ForAGeneratedKey_CarriesTheLineInsideOnePairOfSingleQuotes()
    {
        string line = SshKeys.Generate(SshKeyType.Ed25519, "me@laptop", null).PublicKey;

        string command = SshKeys.InstallCommand(line);

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith("'", command, StringComparison.Ordinal);
        Assert.Contains($"k=\"{line}\"", command, StringComparison.Ordinal);

        // Nothing inside may end the quoting early, whatever shell reads the command first.
        Assert.DoesNotContain('\'', command["sh -c '".Length..^1]);
        Assert.Contains("authorized_keys", command, StringComparison.Ordinal);
        Assert.Contains("umask 077", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS '; reboot; '")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGtQUDZWhs8k/cZcykMkaoX7ZE7DXld8TP79HyddMVTS `reboot`")]
    [InlineData("not a key")]
    public void InstallCommand_ForAnythingThatIsNotAPlainKeyLine_Refuses(string line) =>
        Assert.Throws<ArgumentException>(() => SshKeys.InstallCommand(line));
}
