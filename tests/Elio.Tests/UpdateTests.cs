using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;
using Elio.UI;

namespace Elio.Tests;

internal static class UpdateTestKeys
{
    /// <summary>A new P-256 key and its public half in the form update-keys.txt holds it.</summary>
    public static (ECDsa Key, byte[] Public) New()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportSubjectPublicKeyInfo());
    }

    public static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
}

public sealed class UpdateSignatureTests
{
    // Made with `openssl ecparam -name prime256v1 -genkey` and `openssl dgst -sha256 -sign`, the commands the release
    // workflow runs. The private key was thrown away. This is what proves that the two ends speak the same format.
    private const string OpenSslPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3H3sZmfel7Fs0zGBno5t0naGibBlcnUZ84x2EKbH/Ah6loRIgs6rKSklyhLuzO3X2L+WdWvpQ/UIDSvPO+uMYw==";
    private const string OpenSslManifest = "{\"version\":\"9.9.9\",\"files\":{\"elio-win-x64.zip\":{\"sha256\":\"2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881\",\"size\":1234}}}\n";
    private const string OpenSslSignature = "MEUCIDqxS5BYOx8rF/DokYIN/X3X9O507pwlLyjxo6VTSnF6AiEA3ATr5Vy2+2FtLtRmaeoNS9EN7BXcoGHnxnSn50RFGj4=";

    [Fact]
    public void Verify_SignatureMadeByOpenSsl_IsValid() =>
        Assert.True(UpdateSignature.Verify(Encoding.UTF8.GetBytes(OpenSslManifest), Convert.FromBase64String(OpenSslSignature), [Convert.FromBase64String(OpenSslPublicKey)]));

    [Fact]
    public void Verify_OpenSslSignatureOfAChangedManifest_IsNotValid() =>
        Assert.False(UpdateSignature.Verify(Encoding.UTF8.GetBytes(OpenSslManifest.Replace("9.9.9", "9.9.8")), Convert.FromBase64String(OpenSslSignature), [Convert.FromBase64String(OpenSslPublicKey)]));

    [Fact]
    public void Verify_OwnSignature_IsValid()
    {
        (ECDsa key, byte[] pub) = UpdateTestKeys.New();
        byte[] data = "hello"u8.ToArray();
        Assert.True(UpdateSignature.Verify(data, UpdateTestKeys.Sign(key, data), [pub]));
    }

    [Fact]
    public void Verify_ChangedData_IsNotValid()
    {
        (ECDsa key, byte[] pub) = UpdateTestKeys.New();
        byte[] signature = UpdateTestKeys.Sign(key, "hello"u8.ToArray());
        Assert.False(UpdateSignature.Verify("hellp"u8, signature, [pub]));
    }

    [Fact]
    public void Verify_AnotherKey_IsNotValid()
    {
        (ECDsa key, _) = UpdateTestKeys.New();
        (_, byte[] other) = UpdateTestKeys.New();
        byte[] data = "hello"u8.ToArray();
        Assert.False(UpdateSignature.Verify(data, UpdateTestKeys.Sign(key, data), [other]));
    }

    [Fact]
    public void Verify_AnyTrustedKeyIsEnough_SoAKeyCanBeReplaced()
    {
        (ECDsa key, byte[] pub) = UpdateTestKeys.New();
        (_, byte[] old) = UpdateTestKeys.New();
        byte[] data = "hello"u8.ToArray();
        Assert.True(UpdateSignature.Verify(data, UpdateTestKeys.Sign(key, data), [old, pub]));
    }

    [Fact]
    public void Verify_WithoutAKey_IsNotValid()
    {
        (ECDsa key, _) = UpdateTestKeys.New();
        byte[] data = "hello"u8.ToArray();
        Assert.False(UpdateSignature.Verify(data, UpdateTestKeys.Sign(key, data), []));
    }

    [Fact]
    public void Verify_KeyOnAnotherCurve_IsPassedOver()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        byte[] data = "hello"u8.ToArray();
        byte[] signature = p384.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(UpdateSignature.Verify(data, signature, [p384.ExportSubjectPublicKeyInfo()]));
    }

    [Fact]
    public void Verify_GarbageKeyAndSignature_AreNotValidAndDoNotThrow()
    {
        Assert.False(UpdateSignature.Verify("hello"u8, new byte[70], [[1, 2, 3]]));
        Assert.False(UpdateSignature.Verify("hello"u8, [], [Convert.FromBase64String(OpenSslPublicKey)]));
        Assert.False(UpdateSignature.Verify("hello"u8, new byte[4096], [Convert.FromBase64String(OpenSslPublicKey)]));
    }

    [Fact]
    public void ParseKeys_TakesBase64Lines_AndLeavesNotesAndBlanksOut()
    {
        List<byte[]> keys = UpdateSignature.ParseKeys($"# a note\r\n\r\n  {OpenSslPublicKey}  \r\nnot base64 at all!\r\n# {OpenSslPublicKey}\r\n");
        Assert.Single(keys);
        Assert.Equal(Convert.FromBase64String(OpenSslPublicKey), keys[0]);
    }

    [Fact]
    public void TrustedKeys_AreReadFromTheEmbeddedFile_AndAllOfThemAreUsable()
    {
        // Whatever the file holds, every line in it must be a key this program can use.
        foreach (byte[] key in UpdateSignature.TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            Assert.Equal("1.2.840.10045.3.1.7", ecdsa.ExportParameters(false).Curve.Oid.Value);
        }
    }
}

public sealed class UpdateManifestTests
{
    private static readonly string Hash = new('a', 64);

    private static string Json(string version = "1.2.3", string files = "") =>
        "{\"version\":\"" + version + "\",\"files\":{" + files + "}}";

    private static string File_(string name = "elio-win-x64.zip", string? hash = null, string size = "1000") =>
        "\"" + name + "\":{\"sha256\":\"" + (hash ?? Hash) + "\",\"size\":" + size + "}";

    private static bool Parse(string json, out UpdateManifest? manifest) => UpdateManifest.TryParse(Encoding.UTF8.GetBytes(json), out manifest);

    [Fact]
    public void TryParse_WhatTheWorkflowWrites_IsRead()
    {
        Assert.True(Parse(Json("1.2.3", File_() + "," + File_("elio-linux-x64.tar.gz", new string('B', 64), "2000")), out UpdateManifest? manifest));
        Assert.Equal(new Version(1, 2, 3), manifest!.Version);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(new UpdateFile(Hash, 1000), manifest.Files["elio-win-x64.zip"]);
        Assert.Equal(new string('b', 64), manifest.Files["elio-linux-x64.tar.gz"].Sha256);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("")]
    [InlineData("1.2.-3")]
    public void TryParse_VersionThatIsNotThreeNumbers_IsRefused(string version) =>
        Assert.False(Parse(Json(version, File_()), out _));

    [Theory]
    [InlineData("../elio-win-x64.zip")]
    [InlineData("folder/elio.zip")]
    [InlineData("..")]
    [InlineData("elio win.zip")]
    [InlineData("-elio.zip")]
    public void TryParse_FileNameThatIsNotPlain_IsRefused(string name) =>
        Assert.False(Parse(Json("1.2.3", File_(name)), out _));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("314572801")]
    [InlineData("1.5")]
    [InlineData("\"1000\"")]
    public void TryParse_SizeThatIsNotAPositiveNumberWithinTheLimit_IsRefused(string size) =>
        Assert.False(Parse(Json("1.2.3", File_(size: size)), out _));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParse_HashThatIsNotSha256_IsRefused(string hash) =>
        Assert.False(Parse(Json("1.2.3", File_(hash: hash)), out _));

    [Fact]
    public void TryParse_TheSameFileTwice_IsRefused() =>
        Assert.False(Parse(Json("1.2.3", File_() + "," + File_(hash: new string('c', 64))), out _));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"1.2.3\"}")]
    [InlineData("{\"version\":1,\"files\":{}}")]
    [InlineData("{\"version\":\"1.2.3\",\"files\":[]}")]
    [InlineData("not json")]
    public void TryParse_AnythingElse_IsRefused(string json) =>
        Assert.False(Parse(json, out _));

    [Fact]
    public void TryParse_ManifestLargerThanTheLimit_IsRefused() =>
        Assert.False(UpdateManifest.TryParse(new byte[UpdateManifest.LargestManifest + 1], out _));

    [Fact]
    public void Target_IsChosenBySystemAndProcessor()
    {
        Assert.Equal("elio-win-x64.zip", UpdateTarget.For(OSPlatform.Windows, Architecture.X64)!.AssetName);
        Assert.Equal("elio.exe", UpdateTarget.For(OSPlatform.Windows, Architecture.X64)!.ProgramName);
        Assert.Equal(UpdateArchive.MacApp, UpdateTarget.For(OSPlatform.OSX, Architecture.Arm64)!.Archive);
        Assert.Equal("elio-osx-x64.zip", UpdateTarget.For(OSPlatform.OSX, Architecture.X64)!.AssetName);
        Assert.Equal("elio-linux-arm64.tar.gz", UpdateTarget.For(OSPlatform.Linux, Architecture.Arm64)!.AssetName);
        Assert.Equal(UpdateArchive.TarGz, UpdateTarget.For(OSPlatform.Linux, Architecture.X64)!.Archive);
        Assert.Null(UpdateTarget.For(OSPlatform.Windows, Architecture.Arm64));
        Assert.Null(UpdateTarget.For(OSPlatform.FreeBSD, Architecture.X64));
    }
}

public sealed class UpdateFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestData.Root, "files-" + Guid.NewGuid().ToString("N"));

    public UpdateFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestData.Delete(_folder);

    private string Path_(string name) => Path.Combine(_folder, name);

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        string path = Path_(Guid.NewGuid().ToString("N") + ".zip");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private string MakeTarGz(params (string Name, string Content, TarEntryType Type)[] entries)
    {
        string path = Path_(Guid.NewGuid().ToString("N") + ".tar.gz");
        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip);
        foreach ((string name, string content, TarEntryType type) in entries)
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            else if (type == TarEntryType.SymbolicLink) entry.LinkName = "/etc/passwd";
            tar.WriteEntry(entry);
        }

        return path;
    }

    [Fact]
    public void ExtractFromZip_FileInAFolderOfItsOwn_IsWrittenToTheDestinationOnly()
    {
        string zip = MakeZip(("elio-1.0.0-win-x64/LICENSE", "license"), ("elio-1.0.0-win-x64/elio.exe", "PROGRAM"));
        string destination = Path_("out.new");

        Assert.True(UpdateFiles.ExtractFromZip(zip, "elio.exe", destination));
        Assert.Equal("PROGRAM", File.ReadAllText(destination));
        Assert.Equal(["out.new"], Directory.GetFiles(_folder).Select(Path.GetFileName).Where(n => !n!.EndsWith(".zip")));
    }

    [Fact]
    public void ExtractFromZip_FileAtTheTop_IsFound()
    {
        string destination = Path_("out.new");
        Assert.True(UpdateFiles.ExtractFromZip(MakeZip(("elio.exe", "PROGRAM")), "elio.exe", destination));
        Assert.Equal("PROGRAM", File.ReadAllText(destination));
    }

    [Theory]
    [InlineData("../elio.exe")]
    [InlineData("a/b/elio.exe")]
    [InlineData("/elio.exe")]
    [InlineData("elio.exe.txt")]
    [InlineData("other/Elio.exe")]
    public void ExtractFromZip_PathsThatAreNotTheFile_AreNotTaken(string entryName)
    {
        string destination = Path_("out.new");
        Assert.False(UpdateFiles.ExtractFromZip(MakeZip((entryName, "EVIL")), "elio.exe", destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExtractFromZip_ExistingDestination_IsNotOverwritten()
    {
        string destination = Path_("out.new");
        File.WriteAllText(destination, "KEEP");
        Assert.Throws<IOException>(() => UpdateFiles.ExtractFromZip(MakeZip(("elio.exe", "PROGRAM")), "elio.exe", destination));
        Assert.Equal("KEEP", File.ReadAllText(destination));
    }

    [Fact]
    public void ExtractFromTarGz_RegularFile_IsWritten()
    {
        string tar = MakeTarGz(("elio-1.0.0-linux-x64", "", TarEntryType.Directory), ("elio-1.0.0-linux-x64/LICENSE", "license", TarEntryType.RegularFile), ("elio-1.0.0-linux-x64/elio", "PROGRAM", TarEntryType.RegularFile));
        string destination = Path_("out.new");

        Assert.True(UpdateFiles.ExtractFromTarGz(tar, "elio", destination));
        Assert.Equal("PROGRAM", File.ReadAllText(destination));
    }

    [Fact]
    public void ExtractFromTarGz_LinkOrOtherPath_IsNotTaken()
    {
        string destination = Path_("out.new");
        Assert.False(UpdateFiles.ExtractFromTarGz(MakeTarGz(("elio", "", TarEntryType.SymbolicLink), ("../elio", "EVIL", TarEntryType.RegularFile), ("a/b/elio", "EVIL", TarEntryType.RegularFile)), "elio", destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Swap_PutsTheNewFileInPlaceAndKeepsTheOldOneAside()
    {
        string target = Path_("elio.exe"), replacement = Path_("elio.exe.new");
        File.WriteAllText(target, "OLD");
        File.WriteAllText(replacement, "NEW");

        string aside = UpdateFiles.Swap(target, replacement);

        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.Equal("OLD", File.ReadAllText(aside));
        Assert.False(File.Exists(replacement));
        Assert.EndsWith(UpdateFiles.AsideSuffix, aside);
    }

    [Fact]
    public void Swap_WhenTheNewFileIsMissing_LeavesTheOldOneWhereItWas()
    {
        string target = Path_("elio.exe");
        File.WriteAllText(target, "OLD");

        Assert.ThrowsAny<IOException>(() => UpdateFiles.Swap(target, Path_("nothing.new")));

        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Equal(["elio.exe"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Swap_TwiceInARow_DoesNotTripOverTheFirstOldCopy()
    {
        string target = Path_("elio.exe");
        File.WriteAllText(target, "ONE");
        File.WriteAllText(Path_("a.new"), "TWO");
        File.WriteAllText(Path_("b.new"), "THREE");

        UpdateFiles.Swap(target, Path_("a.new"));
        UpdateFiles.Swap(target, Path_("b.new"));

        Assert.Equal("THREE", File.ReadAllText(target));
    }

    [Fact]
    public void SwapDirectory_PutsTheNewFolderInPlaceAndKeepsTheOldOneAside()
    {
        string target = Path_("Elio.app"), replacement = Path_("fresh");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(replacement);
        File.WriteAllText(Path.Combine(target, "v"), "OLD");
        File.WriteAllText(Path.Combine(replacement, "v"), "NEW");

        string aside = UpdateFiles.SwapDirectory(target, replacement);

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(target, "v")));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(aside, "v")));
    }

    [Fact]
    public void RemoveLeftovers_DeletesTheOldAndUnfinishedCopiesOnly()
    {
        string target = Path_("elio.exe");
        foreach (string name in new[] { "elio.exe", "elio.exe.old", "elio.exe.old1", "elio.exe.new", "other.old", "elio.exe.bak" }) File.WriteAllText(Path_(name), name);
        Directory.CreateDirectory(Path_("Elio.app.old"));

        UpdateFiles.RemoveLeftovers(target);

        Assert.Equal(["elio.exe", "elio.exe.bak", "other.old"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
        Assert.True(Directory.Exists(Path_("Elio.app.old")));
        UpdateFiles.RemoveLeftovers(Path_("Elio.app"));
        Assert.False(Directory.Exists(Path_("Elio.app.old")));
    }

    [Fact]
    public void RemoveLeftovers_InAFolderThatIsNotThere_DoesNotThrow() =>
        UpdateFiles.RemoveLeftovers(Path.Combine(_folder, "nowhere", "elio.exe"));

    [Fact]
    public void CanWriteTo_FolderThatIsThere_IsTrue_AndOneThatIsNot_IsFalse()
    {
        Assert.True(UpdateFiles.CanWriteTo(_folder));
        Assert.False(UpdateFiles.CanWriteTo(Path.Combine(_folder, "nowhere")));
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }
}

public sealed class UpdateCheckReleaseTests
{
    private static string Answer(string tag, string url = "https://github.com/localtonet/elio/releases/tag/v1.2.3") =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","name":"ignored"}""";

    [Fact]
    public void TryReadRelease_PlainTag_CanBeInstalledFrom()
    {
        Assert.True(UpdateCheck.TryReadRelease(Answer("v1.2.3"), out ReleaseInfo? release));
        Assert.Equal("1.2.3", release!.Version);
        Assert.Equal("v1.2.3", release.Tag);
    }

    [Theory]
    [InlineData("v1.3.0-beta.1")]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("release-1.2.3")]
    public void TryReadRelease_OtherTag_IsAnnouncedButNotInstalledFrom(string tag)
    {
        // "release-1.2.3" has no version in it, so it is not announced at all.
        bool read = UpdateCheck.TryReadRelease(Answer(tag), out ReleaseInfo? release);
        if (read) Assert.Null(release!.Tag);
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("v10.20.30", true)]
    [InlineData("v1.2.3/../../x", false)]
    [InlineData("v1.2.3?a=b", false)]
    [InlineData("v1.2.3-rc1", false)]
    [InlineData("v1.2", false)]
    [InlineData("v12345.1.1", false)]
    [InlineData("V1.2.3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlainTag_OnlyVAndThreeShortNumbers(string? tag, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsPlainTag(tag));

    [Theory]
    [InlineData("https://example.com/localtonet/elio/releases/tag/v1.2.3")]
    [InlineData("http://github.com/localtonet/elio/releases/tag/v1.2.3")]
    [InlineData("https://user@github.com/localtonet/elio")]
    public void TryReadRelease_PageThatIsNotOnGitHubOverHttps_IsRefused(string page) =>
        Assert.False(UpdateCheck.TryReadRelease(Answer("v1.2.3", page), out _));

    [Fact]
    public void TryReadRelease_NotAReleaseAtAll_IsRefused()
    {
        Assert.False(UpdateCheck.TryReadRelease("[]", out _));
        Assert.False(UpdateCheck.TryReadRelease("{}", out _));
        Assert.False(UpdateCheck.TryReadRelease("nonsense", out _));
    }

    [Fact]
    public void CanInstall_OnlyWhereEverythingItNeedsIsThere()
    {
        string folder = Path.Combine(TestData.Root, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var zip = new UpdateTarget("elio-win-x64.zip", "elio.exe", UpdateArchive.Zip);
            string program = Path.Combine(folder, "elio.exe");

            Assert.True(UpdateInstaller.CanInstall(zip, program, hasKey: true, singleFile: true));
            Assert.False(UpdateInstaller.CanInstall(null, program, hasKey: true, singleFile: true));
            Assert.False(UpdateInstaller.CanInstall(zip, null, hasKey: true, singleFile: true));
            Assert.False(UpdateInstaller.CanInstall(zip, program, hasKey: false, singleFile: true));
            Assert.False(UpdateInstaller.CanInstall(zip, program, hasKey: true, singleFile: false));
            Assert.False(UpdateInstaller.CanInstall(zip, Path.Combine(folder, "nowhere", "elio.exe"), hasKey: true, singleFile: true));

            // A package manager's folder is left to the package manager.
            var tar = new UpdateTarget("elio-linux-x64.tar.gz", "elio", UpdateArchive.TarGz);
            Assert.False(UpdateInstaller.CanInstall(tar, "/usr/bin/elio", hasKey: true, singleFile: true));
        }
        finally
        {
            TestData.Delete(folder);
        }
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private static readonly UpdateTarget WindowsTarget = new("elio-win-x64.zip", "elio.exe", UpdateArchive.Zip);
    private static readonly UpdateTarget LinuxTarget = new("elio-linux-x64.tar.gz", "elio", UpdateArchive.TarGz);
    private static readonly Version Running = new(1, 0, 0);

    private readonly string _folder = Path.Combine(TestData.Root, "installer-" + Guid.NewGuid().ToString("N"));
    private readonly string _program;
    private readonly ECDsa _key;
    private readonly byte[] _public;
    private readonly Dictionary<string, byte[]> _served = [];
    private readonly List<Uri> _requested = [];
    private readonly IReadOnlyList<byte[]> _keysBefore = UpdateInstaller.Keys;
    private readonly Func<Uri, Stream, long, IProgress<long>?, CancellationToken, Task> _downloadBefore = UpdateInstaller.Download;

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(_folder);
        (_key, _public) = UpdateTestKeys.New();
        _program = Path.Combine(_folder, "elio.exe");
        File.WriteAllText(_program, "OLD");

        UpdateInstaller.Keys = [_public];
        UpdateInstaller.Download = (address, stream, limit, progress, _) =>
        {
            _requested.Add(address);
            if (!_served.TryGetValue(address.Segments[^1], out byte[]? bytes)) throw new HttpRequestException("404");
            if (bytes.Length > limit) throw new IOException("too long");

            stream.Write(bytes);
            progress?.Report(bytes.Length);
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        UpdateInstaller.Keys = _keysBefore;
        UpdateInstaller.Download = _downloadBefore;
        _key.Dispose();
        TestData.Delete(_folder);
    }

    private static byte[] ZipOf(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static byte[] TarGzOf(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return buffer.ToArray();
    }

    private static string ManifestJson(string version, string asset, byte[] archive, long? size = null, string? hash = null) =>
        "{\"version\":\"" + version + "\",\"files\":{\"" + asset + "\":{\"sha256\":\"" + (hash ?? Convert.ToHexStringLower(SHA256.HashData(archive))) + "\",\"size\":" + (size ?? archive.Length) + "}}}";

    /// <summary>Serves a release the way the workflow publishes one: the manifest, its signature and the archive.</summary>
    private void Serve(string asset, byte[] archive, string? manifest = null, ECDsa? signer = null, string version = "1.1.0")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(manifest ?? ManifestJson(version, asset, archive));
        _served["update.json"] = bytes;
        _served["update.json.sig"] = UpdateTestKeys.Sign(signer ?? _key, bytes);
        _served[asset] = archive;
    }

    private static ReleaseInfo Release(string version = "1.1.0", string? tag = "v1.1.0") =>
        new(version, tag, new Uri("https://github.com/localtonet/elio/releases/tag/v1.1.0"));

    private Task InstallAsync(ReleaseInfo? release = null, UpdateTarget? target = null, IProgress<double>? progress = null) =>
        UpdateInstaller.InstallAsync(release ?? Release(), Running, target ?? WindowsTarget, _program, progress, CancellationToken.None);

    private void AssertNothingChanged()
    {
        Assert.Equal("OLD", File.ReadAllText(_program));
        Assert.Equal(["elio.exe"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_ZipRelease_ReplacesTheProgram_AndKeepsTheOldOneAside()
    {
        Serve("elio-win-x64.zip", ZipOf("elio-1.1.0-win-x64/elio.exe", "NEW"));
        var reports = new List<double>();

        await InstallAsync(progress: new SyncProgress(reports.Add));

        Assert.Equal("NEW", File.ReadAllText(_program));
        Assert.Equal("OLD", File.ReadAllText(_program + UpdateFiles.AsideSuffix));
        Assert.Equal(1.0, reports.Last());
        Assert.All(_requested, address => Assert.StartsWith("https://github.com/localtonet/elio/releases/download/v1.1.0/", address.AbsoluteUri));
        Assert.Equal(["update.json", "update.json.sig", "elio-win-x64.zip"], _requested.Select(address => address.Segments[^1]));
    }

    [Fact]
    public async Task Install_TarRelease_ReplacesTheProgram()
    {
        Serve("elio-linux-x64.tar.gz", TarGzOf("elio-1.1.0-linux-x64/elio", "NEW"));
        await InstallAsync(target: LinuxTarget);
        Assert.Equal("NEW", File.ReadAllText(_program));
    }

    [Fact]
    public async Task Install_ManifestSignedByAnotherKey_ChangesNothing()
    {
        using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Serve("elio-win-x64.zip", ZipOf("elio.exe", "EVIL"), signer: stranger);

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_WithoutAnyTrustedKey_ChangesNothing()
    {
        Serve("elio-win-x64.zip", ZipOf("elio.exe", "NEW"));
        UpdateInstaller.Keys = [];

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ManifestChangedAfterItWasSigned_ChangesNothing()
    {
        byte[] archive = ZipOf("elio.exe", "NEW");
        Serve("elio-win-x64.zip", archive);
        _served["update.json"] = Encoding.UTF8.GetBytes(ManifestJson("1.1.0", "elio-win-x64.zip", ZipOf("elio.exe", "EVIL")));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ArchiveThatIsNotTheOneTheManifestDescribes_ChangesNothing()
    {
        byte[] honest = ZipOf("elio.exe", "NEW");
        Serve("elio-win-x64.zip", honest);
        _served["elio-win-x64.zip"] = [.. honest.Take(honest.Length - 1), (byte)(honest[^1] ^ 1)];

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ArchiveOfAnotherSize_ChangesNothing()
    {
        byte[] archive = ZipOf("elio.exe", "NEW");
        Serve("elio-win-x64.zip", archive, ManifestJson("1.1.0", "elio-win-x64.zip", archive, size: archive.Length + 10));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ArchiveWithoutTheProgram_ChangesNothing()
    {
        Serve("elio-win-x64.zip", ZipOf("elio-1.1.0-win-x64/LICENSE", "license"));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_SignedManifestOfAnOlderRelease_IsNotTakenForThisOne()
    {
        // An attacker who can serve files of the releases page can replay what was signed once.
        byte[] archive = ZipOf("elio.exe", "NEW");
        Serve("elio-win-x64.zip", archive, ManifestJson("1.0.5", "elio-win-x64.zip", archive));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(Release("1.1.0", "v1.1.0")));
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ReleaseThatIsNotNewerThanTheRunningVersion_ChangesNothing()
    {
        Serve("elio-win-x64.zip", ZipOf("elio.exe", "NEW"), version: "1.0.0");

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(Release("1.0.0", "v1.0.0")));
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ReleaseWithoutABuildForThisSystem_ChangesNothing()
    {
        Serve("elio-osx-arm64.zip", ZipOf("elio.exe", "NEW"));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_ReleaseWithoutAPlainTag_ChangesNothing()
    {
        Serve("elio-win-x64.zip", ZipOf("elio.exe", "NEW"));

        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(Release(tag: null)));
        Assert.Empty(_requested);
        AssertNothingChanged();
    }

    [Fact]
    public async Task Install_WhenTheDownloadFails_ChangesNothing()
    {
        await Assert.ThrowsAsync<UpdateException>(() => InstallAsync());
        AssertNothingChanged();
    }

    [Fact]
    public void AfterUpdate_AnOrdinaryStart_DoesNothing_AndAGoneParentDoesNotHoldItUp()
    {
        UpdateInstaller.AfterUpdate([]);
        UpdateInstaller.AfterUpdate(["--wait-for-exit=notanumber"]);

        // A process number that nothing has: the wait ends at once.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UpdateInstaller.AfterUpdate(["--wait-for-exit=2147483000"]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // Progress<T> posts to another thread; this one reports on the spot, so that a test can read it straight after.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
