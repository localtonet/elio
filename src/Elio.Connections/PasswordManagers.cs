using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>A password manager could not give the secret. The message says why, in words the user can act on.</summary>
public sealed class PasswordManagerException(string message) : Exception(message);

/// <summary>
/// A password kept in a password manager rather than in Elio's vault. Where a password is asked for, the user may write
/// a reference instead of the password itself: <c>op://Private/web-1/password</c> for 1Password, or
/// <c>bw://web-1</c> (an item's name or id) for Bitwarden. The vault then keeps only the reference, and the password is
/// fetched through the manager's own command line tool each time it is needed, with the manager's own unlocking.
/// </summary>
public static class PasswordManagers
{
    public const string OnePasswordPrefix = "op://";
    public const string BitwardenPrefix = "bw://";

    // 1Password may show its own unlock prompt, so this leaves time to answer it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private const int LongestReference = 1024;

    private static string? _bitwardenSession;

    /// <summary>
    /// Asks for the Bitwarden master password when Bitwarden is locked, or returns null if the user backed out. Set by
    /// the app; without it a locked Bitwarden is reported and nothing more.
    /// </summary>
    public static Func<CancellationToken, Task<string?>>? AskBitwardenPassword { get; set; }

    /// <summary>Whether a stored secret is a reference to a password manager rather than the secret itself.</summary>
    public static bool IsReference(string? secret) =>
        secret is { Length: > 0 and <= LongestReference }
        && (secret.StartsWith(OnePasswordPrefix, StringComparison.Ordinal) || secret.StartsWith(BitwardenPrefix, StringComparison.Ordinal))
        && !secret.Any(char.IsControl);

    /// <summary>The secret a reference stands for. Anything that is not a reference comes back as it is.</summary>
    /// <exception cref="PasswordManagerException">The tool is missing, locked, or knows no such item.</exception>
    public static async Task<string> ResolveAsync(string secret, CancellationToken cancellation)
    {
        if (!IsReference(secret)) return secret;

        if (secret.StartsWith(OnePasswordPrefix, StringComparison.Ordinal))
        {
            // The reference is 1Password's own secret reference syntax, which `op read` takes as it is.
            (int code, string output, string error) = await RunAsync("op", ["read", "--no-newline", secret], null, cancellation).ConfigureAwait(false);
            if (code != 0) throw new PasswordManagerException(L.T("1Password did not give the password for {0}: {1}", secret, FirstLine(error)));
            return output;
        }

        string item = secret[BitwardenPrefix.Length..];
        if (item.Length == 0) throw new PasswordManagerException(L.T("A Bitwarden reference names an item: bw://<name or id>."));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            (int code, string output, string error) = await RunAsync("bw", ["get", "password", item, "--nointeraction"], _bitwardenSession, cancellation).ConfigureAwait(false);
            if (code == 0) return output.TrimEnd('\r', '\n');

            bool locked = error.Contains("locked", StringComparison.OrdinalIgnoreCase) || error.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
            if (!locked || attempt > 0 || !await UnlockBitwardenAsync(cancellation).ConfigureAwait(false))
                throw new PasswordManagerException(L.T("Bitwarden did not give the password for {0}: {1}", item, FirstLine(error)));
        }

        throw new PasswordManagerException(L.T("Bitwarden did not give the password for {0}: {1}", item, string.Empty));
    }

    // `bw unlock` hands back a session key, kept in memory for as long as Elio runs and never written anywhere.
    private static async Task<bool> UnlockBitwardenAsync(CancellationToken cancellation)
    {
        if (AskBitwardenPassword is null || await AskBitwardenPassword(cancellation).ConfigureAwait(false) is not { Length: > 0 } master) return false;

        // The master password goes to bw through its environment, never on a command line other programs can read.
        (int code, string output, _) = await RunAsync("bw", ["unlock", "--passwordenv", "ELIO_BW_PASSWORD", "--raw"], null, cancellation, ("ELIO_BW_PASSWORD", master)).ConfigureAwait(false);
        if (code != 0 || output.Trim().Length == 0) return false;

        _bitwardenSession = output.Trim();
        return true;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static async Task<(int Code, string Output, string Error)> RunAsync(string program, string[] arguments, string? bitwardenSession, CancellationToken cancellation, (string Name, string Value)? variable = null)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (bitwardenSession is not null) start.Environment["BW_SESSION"] = bitwardenSession;
        if (variable is var (name, value)) start.Environment[name] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PasswordManagerException(L.T("{0} could not be started.", program));
        }
        catch (Win32Exception)
        {
            throw new PasswordManagerException(program == "op"
                ? L.T("The 1Password command line tool (op) was not found on this computer.")
                : L.T("The Bitwarden command line tool (bw) was not found on this computer."));
        }

        using (process)
        {
            // Nothing is ever typed to it: a tool that waits for input would otherwise wait forever.
            process.StandardInput.Close();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(Timeout);
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
                Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                throw new PasswordManagerException(L.T("{0} did not answer in time.", program));
            }
        }
    }

    /// <summary>
    /// Fills in every reference in what the vault holds for an id: a plain password, or an identity's stored secret
    /// with a password or passphrase that is a reference. Returns null when there was nothing to fill in.
    /// </summary>
    public static async Task<string?> ResolveStoredAsync(string? stored, CancellationToken cancellation)
    {
        if (stored is null) return null;
        if (IsReference(stored)) return await ResolveAsync(stored, cancellation).ConfigureAwait(false);

        if (IdentitySecret.Read(stored) is not { } secret || !(IsReference(secret.Password) || IsReference(secret.Passphrase))) return null;
        if (IsReference(secret.Password)) secret.Password = await ResolveAsync(secret.Password!, cancellation).ConfigureAwait(false);
        if (IsReference(secret.Passphrase)) secret.Passphrase = await ResolveAsync(secret.Passphrase!, cancellation).ConfigureAwait(false);
        return secret.Write();
    }
}
