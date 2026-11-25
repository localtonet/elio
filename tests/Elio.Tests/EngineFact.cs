using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// Whether the terminal engine, libghostty-vt, can be loaded here. The repository carries it for Windows and Linux only;
/// on a Mac it has to be built first (build/native-apple.sh), so tests that need the real engine say so and step aside.
/// </summary>
internal static class Engine
{
    private static readonly Lazy<bool> Loads = new(() =>
    {
        try
        {
            // The cheapest way to know is to do what the tests do: make a terminal, and put it away again.
            var terminal = new TerminalSession(new ScriptedTransport(), 2, 2);
            terminal.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    public static bool IsAvailable => Loads.Value;

    public const string Missing = "The terminal engine (libghostty-vt) is not built for this platform: run build/native-apple.sh on a Mac first.";
}

/// <summary>A test that runs the real terminal engine; skipped, saying why, where the engine is not there.</summary>
internal sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        if (!Engine.IsAvailable) Skip = Engine.Missing;
    }
}

/// <summary>A theory that runs the real terminal engine; skipped, saying why, where the engine is not there.</summary>
internal sealed class EngineTheoryAttribute : TheoryAttribute
{
    public EngineTheoryAttribute()
    {
        if (!Engine.IsAvailable) Skip = Engine.Missing;
    }
}
