using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Desktop;

/// <summary>
/// Keeps Elio to one running copy per user and data folder. Starting it again asks the copy that is already
/// running to come forward with a new tab, and then leaves.
/// </summary>
internal static class SingleInstance
{
    private const string OpenRequest = "open";
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
    public static bool TryHandOver()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);
            pipe.Write(Encoding.UTF8.GetBytes(OpenRequest + "\n"));
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts answering later launches. <paramref name="onOpen"/> is called on a background thread for each.</summary>
    public static void Listen(Action onOpen)
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
                    if (reader.ReadLine() == OpenRequest) onOpen();
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
}
