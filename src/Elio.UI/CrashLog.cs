using System.Globalization;
using System.Runtime.InteropServices;
using Elio.Core;

namespace Elio.UI;

/// <summary>
/// Keeps a report on this computer when Elio fails, and says so at the next start. Nothing is sent anywhere: the
/// report is a file in the logs folder, for the user to read, or to attach to an issue if they choose.
/// </summary>
public static class CrashLog
{
    private const string Prefix = "crash-";
    private const string SeenMarker = "crash-seen";

    // Old reports are of no use to anyone and are cleared away as new ones come.
    private const int KeptReports = 10;

    private static int _installed;

    /// <summary>Starts writing a report for any error nothing else caught. Called once, as early as possible.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "unobserved task");
    }

    /// <summary>Writes a report. Never throws: a failure while writing about a failure helps nobody.</summary>
    public static string? Write(Exception? error, string kind)
    {
        if (error is null) return null;

        try
        {
            PrivateFiles.CreateDirectory(ElioPaths.LogDirectory);
            string path = Path.Combine(ElioPaths.LogDirectory, $"{Prefix}{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log");
            string report =
                $"Elio {UpdateCheck.RunningText}\n" +
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}\n" +
                $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}, {kind}\n\n" +
                error + "\n";

            using (FileStream file = PrivateFiles.Open(path, FileMode.CreateNew))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(report);
            }

            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The newest report written since the user was last told about one, or null. Telling counts as seen.</summary>
    public static string? TakeUnseen()
    {
        try
        {
            if (!Directory.Exists(ElioPaths.LogDirectory)) return null;

            string marker = Path.Combine(ElioPaths.LogDirectory, SeenMarker);
            DateTime seen = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
            FileInfo? newest = new DirectoryInfo(ElioPaths.LogDirectory)
                .GetFiles(Prefix + "*.log")
                .Where(f => f.LastWriteTimeUtc > seen)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            return newest?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune()
    {
        foreach (FileInfo old in new DirectoryInfo(ElioPaths.LogDirectory).GetFiles(Prefix + "*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeptReports))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
