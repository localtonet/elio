using System.Diagnostics;
using System.Security.Cryptography;
using Elio.Core;

namespace Elio.UI;

/// <summary>Why an update was not installed. The message is in the user's language and is shown as it is.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Replaces the running program with the one from a newer release. Nothing is installed that the release workflow
/// did not sign: the manifest of the release is checked against the keys built into the program, the file against the
/// manifest, and only then is anything put in place.
/// </summary>
public static class UpdateInstaller
{
    private const string DownloadFolder = "https://github.com/localtonet/elio/releases/download/";
    private const string ManifestName = "update.json";
    private const string WaitArgument = "--wait-for-exit=";
    private const int LargestSignature = 128;

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(15);

    /// <summary>The keys a manifest's signature is checked against. Replaced in tests.</summary>
    public static IReadOnlyList<byte[]> Keys { get; set; } = UpdateSignature.TrustedKeys;

    /// <summary>
    /// Writes what is at an address into a stream, and stops with an exception if it is longer than the limit.
    /// Replaced in tests.
    /// </summary>
    public static Func<Uri, Stream, long, IProgress<long>?, CancellationToken, Task> Download { get; set; } = DownloadFromGitHubAsync;

    /// <summary>The program that is running: where a new one goes. Replaced in tests.</summary>
    public static string? ProgramPath { get; set; } = Environment.ProcessPath;

    /// <summary>
    /// Whether this copy can update itself: there is a release file for this system, a key to check it against, and a
    /// program that is the single file Elio is published as, in a folder it may write to. A copy installed by a
    /// package manager, a build run from the source and a copy on a read-only disk are left to their own way of updating.
    /// </summary>
    public static bool CanInstall() => CanInstall(UpdateTarget.Current, ProgramPath, Keys.Count > 0, typeof(UpdateInstaller).Assembly.Location.Length == 0);

    internal static bool CanInstall(UpdateTarget? target, string? program, bool hasKey, bool singleFile)
    {
        if (target is null || program is null || !hasKey || !singleFile || AppServices.MobileLayout) return false;

        // The package manager owns what it put under /usr, and a program there is replaced by installing a newer package.
        if (target.Archive == UpdateArchive.TarGz && program.StartsWith("/usr/", StringComparison.Ordinal)) return false;

        string? folder = target.Archive == UpdateArchive.MacApp ? BundleParent(program) : Path.GetDirectoryName(program);
        return folder is not null && UpdateFiles.CanWriteTo(folder);
    }

    /// <summary>
    /// Downloads the release, checks it and puts it in place of the running program. The new version starts only when
    /// <see cref="Relaunch"/> is called.
    /// </summary>
    /// <param name="release">The release the check found.</param>
    /// <param name="running">The version that is running now: nothing that is not newer is installed.</param>
    /// <param name="progress">Told how much of the file is here, from 0 to 1.</param>
    /// <exception cref="UpdateException">Nothing was installed, and the message says why.</exception>
    public static async Task InstallAsync(ReleaseInfo release, Version running, IProgress<double>? progress, CancellationToken cancellation)
    {
        UpdateTarget target = UpdateTarget.Current ?? throw new UpdateException(L.T("Elio cannot update itself on this system."));
        await InstallAsync(release, running, target, ProgramPath, progress, cancellation);
    }

    internal static async Task InstallAsync(ReleaseInfo release, Version running, UpdateTarget target, string? program, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (release.Tag is not { } tag || program is null) throw new UpdateException(L.T("Elio cannot update itself on this system."));

        // What a release says about itself, and who says it. Nothing below is believed before this passes.
        byte[] manifestBytes = await FetchSmallAsync(new Uri($"{DownloadFolder}{tag}/{ManifestName}"), UpdateManifest.LargestManifest, cancellation);
        byte[] signature = await FetchSmallAsync(new Uri($"{DownloadFolder}{tag}/{ManifestName}.sig"), LargestSignature, cancellation);
        if (!UpdateSignature.Verify(manifestBytes, signature, Keys))
            throw new UpdateException(L.T("The update is not signed by Elio's release key, so it was not installed."));
        if (!UpdateManifest.TryParse(manifestBytes, out UpdateManifest? manifest))
            throw new UpdateException(L.T("The update's description is not valid."));

        // The signature belongs to one release. An older release's manifest, which is signed too, must not pass for this one.
        if (!UpdateCheck.TryParseVersion(release.Version, out Version? announced) || manifest.Version != announced || !UpdateCheck.IsNewer(manifest.Version.ToString(), running))
            throw new UpdateException(L.T("The update is not newer than this version."));
        if (!manifest.Files.TryGetValue(target.AssetName, out UpdateFile? file))
            throw new UpdateException(L.T("This release has no build for this system."));

        string work = Directory.CreateTempSubdirectory("elio-update-").FullName;
        try
        {
            string archive = Path.Combine(work, target.AssetName);
            await DownloadAndCheckAsync(new Uri($"{DownloadFolder}{tag}/{target.AssetName}"), archive, file, progress, cancellation);
            Place(target, archive, program);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<byte[]> FetchSmallAsync(Uri address, long limit, CancellationToken cancellation)
    {
        try
        {
            using var stream = new MemoryStream();
            await Download(address, stream, limit, null, cancellation);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            throw new UpdateException(L.T("The update could not be downloaded."), ex);
        }
    }

    private static async Task DownloadAndCheckAsync(Uri address, string path, UpdateFile expected, IProgress<double>? progress, CancellationToken cancellation)
    {
        try
        {
            var counted = progress is null ? null : new Counter(bytes => progress.Report(Math.Min(1.0, (double)bytes / expected.Size)));
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await Download(address, file, expected.Size, counted, cancellation);
            }

            // The file is read again from the disk, so that what is checked is what is installed.
            await using var written = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (written.Length != expected.Size) throw new UpdateException(L.T("The download is not the size the release says, so it was not installed."));

            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellation));
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(expected.Sha256)))
                throw new UpdateException(L.T("The download is damaged, so it was not installed."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            throw new UpdateException(L.T("The update could not be downloaded."), ex);
        }
    }

    private static void Place(UpdateTarget target, string archive, string program)
    {
        try
        {
            if (target.Archive == UpdateArchive.MacApp) PlaceMacApp(archive, program);
            else PlaceProgram(target, archive, program);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateException(L.T("The new version could not be put in place: {0}", ex.Message), ex);
        }
    }

    private static void PlaceProgram(UpdateTarget target, string archive, string program)
    {
        string fresh = program + UpdateFiles.NewSuffix;
        if (File.Exists(fresh)) File.Delete(fresh);

        bool found = target.Archive == UpdateArchive.TarGz
            ? UpdateFiles.ExtractFromTarGz(archive, target.ProgramName, fresh)
            : UpdateFiles.ExtractFromZip(archive, target.ProgramName, fresh);

        try
        {
            if (!found) throw new InvalidDataException(L.T("The release's file does not hold the program."));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            UpdateFiles.Swap(program, fresh);
        }
        finally
        {
            if (File.Exists(fresh)) File.Delete(fresh);
        }
    }

    // The application bundle, which holds the program three folders down: Elio.app/Contents/MacOS/elio.
    private static void PlaceMacApp(string archive, string program)
    {
        string bundle = BundleOf(program) ?? throw new InvalidDataException(L.T("Elio is not running from an application bundle."));
        string parent = Path.GetDirectoryName(bundle)!;

        // Unpacked beside the old one, on the same disk, so that moving it in is a rename.
        string staging = Path.Combine(parent, ".elio-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            // ditto keeps what a plain unzip loses: the permissions, the links and the signature's attributes.
            using Process? unpack = Process.Start(new ProcessStartInfo("/usr/bin/ditto") { ArgumentList = { "-x", "-k", archive, staging }, UseShellExecute = false });
            if (unpack is null) throw new IOException(L.T("The release's file could not be unpacked."));
            unpack.WaitForExit();
            if (unpack.ExitCode != 0) throw new IOException(L.T("The release's file could not be unpacked."));

            string? fresh = Directory.EnumerateDirectories(staging, "*.app").FirstOrDefault();
            if (fresh is null || !File.Exists(Path.Combine(fresh, "Contents", "MacOS", Path.GetFileName(program))))
                throw new InvalidDataException(L.T("The release's file does not hold the program."));

            UpdateFiles.SwapDirectory(bundle, fresh);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Reports on the thread that downloads; the caller's own progress decides where to show it.
    private sealed class Counter(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static string? BundleOf(string program)
    {
        string? bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(program)));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    private static string? BundleParent(string program) => BundleOf(program) is { } bundle ? Path.GetDirectoryName(bundle) : null;

    /// <summary>
    /// Starts the program that was put in place. It waits until this copy has gone before it does anything, so that it
    /// does not mistake the copy that is closing for one it should hand its launch to. The caller closes Elio next.
    /// </summary>
    /// <returns>False if it could not be started.</returns>
    public static bool Relaunch() => Relaunch(UpdateTarget.Current, ProgramPath);

    internal static bool Relaunch(UpdateTarget? target, string? program)
    {
        if (target is null || program is null) return false;

        try
        {
            string wait = WaitArgument + Environment.ProcessId;
            ProcessStartInfo start;
            if (target.Archive == UpdateArchive.MacApp)
            {
                if (BundleOf(program) is not { } bundle) return false;
                start = new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-n", "-a", bundle, "--args", wait } };
            }
            else
            {
                start = new ProcessStartInfo(program) { ArgumentList = { wait }, WorkingDirectory = Path.GetDirectoryName(program)! };
            }

            start.UseShellExecute = false;
            using Process? started = Process.Start(start);
            return started is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Called first thing when Elio starts. A copy started by an update waits here for the one that started it to
    /// finish closing, and what the update left behind is deleted. Does nothing for an ordinary start, and is cheap.
    /// </summary>
    public static void AfterUpdate(string[] arguments)
    {
        string? wait = arguments.FirstOrDefault(argument => argument.StartsWith(WaitArgument, StringComparison.Ordinal));
        if (wait is not null && int.TryParse(wait[WaitArgument.Length..], out int id) && id != Environment.ProcessId)
        {
            try
            {
                using Process previous = Process.GetProcessById(id);
                previous.WaitForExit(WaitLimit);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It has gone already.
            }
        }

        // Only a program that is a single file of its own: from a build run out of the source, "dotnet" would be the one to clean up beside.
        if (ProgramPath is { } program && typeof(UpdateInstaller).Assembly.Location.Length == 0) UpdateFiles.RemoveLeftovers(BundleOf(program) ?? program);
    }

    private static async Task DownloadFromGitHubAsync(Uri address, Stream destination, long limit, IProgress<long>? progress, CancellationToken cancellation)
    {
        // Only GitHub's own release files are fetched, over https, whatever an earlier answer said.
        if (address.Scheme != Uri.UriSchemeHttps || !string.Equals(address.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
            || !address.AbsoluteUri.StartsWith(DownloadFolder, StringComparison.Ordinal))
            throw new InvalidOperationException("Not a release file.");

        using var client = new HttpClient { Timeout = DownloadLimit };
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", "Elio");

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long announced && announced > limit) throw new IOException("The file is larger than it should be.");

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellation)) > 0)
        {
            total += read;
            if (total > limit) throw new IOException("The file is larger than it should be.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
            progress?.Report(total);
        }
    }
}
