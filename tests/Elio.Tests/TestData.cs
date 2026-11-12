using System.Runtime.CompilerServices;

// Argon2 at the real settings takes 64 MiB a go, and the data folder is named by a variable the whole process shares.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Elio.Tests;

/// <summary>
/// Points Elio's data folder at a temporary one before any test runs, so nothing here can read or write the
/// real settings, server list or known hosts of whoever runs the tests.
/// </summary>
internal static class TestData
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "elio-tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("ELIO_DATA_DIR", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(Root);
    }

    public static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is not worth failing a test run over.
        }
    }
}

/// <summary>A data folder of its own for one test, removed when the test is over.</summary>
public abstract class TempDataDirectoryTest : IDisposable
{
    protected string DataDirectory { get; } = Path.Combine(TestData.Root, Guid.NewGuid().ToString("N"));

    protected TempDataDirectoryTest()
    {
        Directory.CreateDirectory(DataDirectory);
        Environment.SetEnvironmentVariable("ELIO_DATA_DIR", DataDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ELIO_DATA_DIR", TestData.Root);
        TestData.Delete(DataDirectory);
        GC.SuppressFinalize(this);
    }
}
