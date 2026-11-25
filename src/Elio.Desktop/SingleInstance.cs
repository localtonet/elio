using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elio.Core;

namespace Elio.Desktop;

/// <summary>
/// Keeps Elio to one running copy per user and data folder. Starting it again asks the copy that is already
/// running to come forward with a new tab, and then leaves.
/// </summary>
internal static class SingleInstance
{
    // An older copy only knows "open"; a newer one also takes "launch" followed by the arguments, as JSON, on the next line.
    private const string OpenRequest = "open";
    private const string LaunchRequestLine = "launch";

    // The arguments of one launch are a few hundred bytes. A line longer than this is not one.
    private const int LongestArguments = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    // Two copies with different data folders are different apps as far as this goes: a portable copy on a
    // USB drive should not be swallowed by the installed one.
    private static string PipeName()
    {
        string identity = Environment.UserName + "|" + Path.GetFullPath(ElioPaths.DataDirectory).ToLowerInvariant();
        return "elio-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    /// <summary>Hands this launch to a copy that is already running.</summary>
    /// <returns>True if there was one, in which case this process has nothing left to do.</returns>
    public static bool TryHandOver(LaunchRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);
            string message = request.Kind == LaunchKind.NewTab
                ? OpenRequest + "\n"
                : LaunchRequestLine + "\n" + JsonSerializer.Serialize(request.ToArguments().ToArray(), DesktopJsonContext.Default.StringArray) + "\n";
            pipe.Write(Encoding.UTF8.GetBytes(message));
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts answering later launches. <paramref name="onOpen"/> is called on a background thread for each.</summary>
    public static void Listen(Action<LaunchRequest> onOpen)
    {
        var thread = new Thread(() =>
        {
            string name = PipeName();
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();

                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    switch (reader.ReadLine())
                    {
                        case OpenRequest:
                            onOpen(LaunchRequest.NewTab);
                            break;
                        case LaunchRequestLine when ReadArguments(reader) is { } arguments:
                            // Read the same way as a command line, so a copy started with nonsense cannot ask for more than a typed one could.
                            onOpen(LaunchRequest.Parse(arguments));
                            break;
                    }
                }
                catch (IOException)
                {
                    // A launch that went away halfway. The next one gets a fresh pipe.
                }
                catch (UnauthorizedAccessException)
                {
                    // Another copy answers this name already. It keeps the job.
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Elio single instance",
        };
        thread.Start();
    }

    private static string[]? ReadArguments(StreamReader reader)
    {
        var line = new StringBuilder();
        int c;
        while ((c = reader.Read()) >= 0 && c != '\n')
        {
            if (line.Length >= LongestArguments) return null;
            line.Append((char)c);
        }

        try
        {
            return JsonSerializer.Deserialize(line.ToString(), DesktopJsonContext.Default.StringArray);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class DesktopJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
