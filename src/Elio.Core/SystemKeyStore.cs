using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Elio.Core;

/// <summary>
/// Keeps one small secret, the vault's key, with the operating system's own keeper of secrets: the login
/// keychain on macOS, and the Secret Service (GNOME Keyring, KWallet) on Linux.
/// </summary>
/// <remarks>
/// Both are reached through the command-line tools the systems ship for the purpose, `security` and
/// `secret-tool`, rather than by binding their libraries. Where the tool or the service is missing, there is
/// simply no store, and the vault says so.
/// </remarks>
internal static class SystemKeyStore
{
    private const string Service = "Elio vault";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);

    // The tools are started from where the system keeps them and never looked up by name: a program of the same
    // name earlier on the PATH would otherwise be handed the vault's key.
    private const string Security = "/usr/bin/security";
    private static readonly string? SecretTool = new[] { "/usr/bin/secret-tool", "/bin/secret-tool" }.FirstOrDefault(File.Exists);

    /// <summary>Whether this system has a store this class knows how to use.</summary>
    public static bool Supported => OperatingSystem.IsMacOS() || (OperatingSystem.IsLinux() && SecretTool is not null);

    // One key per data folder, so a portable copy and the installed one do not share or overwrite a key.
    private static string Account(string dataDirectory) =>
        "elio-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))))[..16].ToLowerInvariant();

    /// <summary>The stored key, or null if there is none or the store cannot be reached.</summary>
    public static byte[]? Read(string dataDirectory)
    {
        string account = Account(dataDirectory);
        string? text = OperatingSystem.IsMacOS()
            ? Run(Security, ["find-generic-password", "-a", account, "-s", Service, "-w"], null)
            : Run(SecretTool, ["lookup", "application", "elio", "account", account], null);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Stores the key. Returns false if the store would not take it.</summary>
    public static bool Write(string dataDirectory, byte[] key)
    {
        string account = Account(dataDirectory);
        string encoded = Convert.ToBase64String(key);

        // secret-tool reads the secret from its input. `security` takes it as an argument, where the process
        // list shows it for the moment the tool runs. It has no way of reading it from its input that works on
        // every macOS still in use (without a value, -w asks on the terminal, not on the input), so that stays.
        string? result = OperatingSystem.IsMacOS()
            ? Run(Security, ["add-generic-password", "-U", "-a", account, "-s", Service, "-w", encoded], null)
            : Run(SecretTool, ["store", "--label=" + Service, "application", "elio", "account", account], encoded);
        return result is not null;
    }

    /// <returns>What the tool printed, or null if it is missing, failed, or took too long.</returns>
    private static string? Run(string? tool, string[] arguments, string? input)
    {
        if (tool is null) return null;

        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return null;

            if (input is not null) process.StandardInput.Write(input);
            process.StandardInput.Close();

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
