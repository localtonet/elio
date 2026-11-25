using System.Runtime.InteropServices;

namespace Elio.Core;

/// <summary>How the file of a release for one system is packed, which decides how it is put in place.</summary>
public enum UpdateArchive
{
    /// <summary>A zip with the program in it, in a folder of its own.</summary>
    Zip,

    /// <summary>A gzipped tar with the program in it, in a folder of its own.</summary>
    TarGz,

    /// <summary>A zip with an application bundle in it, as macOS has them.</summary>
    MacApp,
}

/// <summary>
/// Which file of a release belongs on this computer, and what is inside it. A release carries one file for each
/// system it is built for, under a name that does not change from one version to the next.
/// </summary>
/// <param name="AssetName">The name of the file on the release, such as "elio-win-x64.zip".</param>
/// <param name="ProgramName">The program inside it that replaces the running one, such as "elio.exe".</param>
/// <param name="Archive">How the file is packed.</param>
public sealed record UpdateTarget(string AssetName, string ProgramName, UpdateArchive Archive)
{
    /// <summary>The target for the system this program runs on, or null where Elio does not replace itself.</summary>
    /// <remarks>
    /// The processor is the one this program was built for, not the computer's: an x64 copy that Windows on ARM runs
    /// for it stays an x64 copy, and an ARM64 copy is only ever given the ARM64 build.
    /// </remarks>
    public static UpdateTarget? Current { get; } = OperatingSystem.IsWindows() ? For(OSPlatform.Windows, RuntimeInformation.ProcessArchitecture)
        : OperatingSystem.IsMacOS() ? For(OSPlatform.OSX, RuntimeInformation.ProcessArchitecture)
        : OperatingSystem.IsLinux() ? For(OSPlatform.Linux, RuntimeInformation.ProcessArchitecture, Environment.GetEnvironmentVariable("APPIMAGE"))
        : null;

    /// <summary>The target for a system and a processor, or null for one that has no build that updates itself.</summary>
    /// <param name="system">The operating system.</param>
    /// <param name="processor">The processor the running program was built for.</param>
    /// <param name="appImage">
    /// The APPIMAGE environment variable, which an AppImage sets to its own path for the program it runs. Such a copy
    /// runs from an image that is mounted read-only while it runs, and the file to replace would be the AppImage
    /// itself, which is not what the tar.gz holds. Like a .deb, it is only told about a release.
    /// </param>
    public static UpdateTarget? For(OSPlatform system, Architecture processor, string? appImage = null)
    {
        if (system == OSPlatform.Linux && !string.IsNullOrEmpty(appImage)) return null;

        if (system == OSPlatform.Windows && processor == Architecture.X64) return new("elio-win-x64.zip", "elio.exe", UpdateArchive.Zip);
        if (system == OSPlatform.Windows && processor == Architecture.Arm64) return new("elio-win-arm64.zip", "elio.exe", UpdateArchive.Zip);
        if (system == OSPlatform.OSX && processor == Architecture.Arm64) return new("elio-osx-arm64.zip", "elio", UpdateArchive.MacApp);
        if (system == OSPlatform.OSX && processor == Architecture.X64) return new("elio-osx-x64.zip", "elio", UpdateArchive.MacApp);
        if (system == OSPlatform.Linux && processor == Architecture.X64) return new("elio-linux-x64.tar.gz", "elio", UpdateArchive.TarGz);
        if (system == OSPlatform.Linux && processor == Architecture.Arm64) return new("elio-linux-arm64.tar.gz", "elio", UpdateArchive.TarGz);
        return null;
    }
}
