using System.Text;
using System.Text.RegularExpressions;

namespace Elio.Terminal;

/// <summary>Finds web addresses in what a terminal shows.</summary>
public static class TerminalLinks
{
    // The text comes from whatever is running on the server, so the pattern must never be able to stall on it.
    private static readonly Regex Pattern = new(@"https?://[^\s<>""'`\u0000-\u001f]+", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Punctuation that ends a sentence around a link far more often than it ends the link.
    private static readonly char[] Trailing = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>'];

    /// <summary>The web address under one cell of a row, if there is one.</summary>
    /// <param name="cells">The row, one string per cell.</param>
    public static Uri? At(IReadOnlyList<string> cells, int column)
    {
        if (column < 0 || column >= cells.Count) return null;

        // A cell can hold more than one character, so positions in the text are mapped back to cells.
        var text = new StringBuilder();
        int[] starts = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            starts[i] = text.Length;
            text.Append(cells[i]);
        }

        foreach (Match match in Pattern.Matches(text.ToString()))
        {
            string value = match.Value.TrimEnd(Trailing);
            if (starts[column] < match.Index || starts[column] >= match.Index + value.Length) continue;

            return Uri.TryCreate(value, UriKind.Absolute, out Uri? link) && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps) ? link : null;
        }

        return null;
    }
}

/// <summary>
/// Writes a file path so that a shell reads it as one word and as nothing else. A file can be named
/// <c>$(something)</c>, and a shell must not be led to run that when the file is dropped on it.
/// </summary>
public static class ShellQuote
{
    private static bool IsPlainWindows(string path) =>
        path.Length > 0 && path.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\' or '/' or ':' or '-' or '~');

    /// <summary>For sh, bash, zsh and their relatives, and for a shell on another computer, which is most often one of them.</summary>
    public static string Posix(string path)
    {
        bool plain = path.Length > 0 && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or ':' or '@' or '%' or '+' or '=' or ',' or '-');
        return plain ? path : "'" + path.Replace("'", "'\\''") + "'";
    }

    /// <summary>For PowerShell. Inside single quotes it reads everything as written, and a single quote is written twice.</summary>
    public static string PowerShell(string path)
    {
        if (IsPlainWindows(path)) return path;

        // PowerShell takes the curly single quotes for the straight one, so each of them is doubled as well.
        var quoted = new StringBuilder(path.Length + 2).Append('\'');
        foreach (char c in path)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>
    /// For Command Prompt, which only knows double quotes and still fills in <c>%NAME%</c> inside them.
    /// </summary>
    /// <returns>Null for a path that cannot be written safely there, which must then not be typed at all.</returns>
    public static string? Cmd(string path)
    {
        if (IsPlainWindows(path)) return path;
        return path.AsSpan().ContainsAny("\"%^\r\n") ? null : "\"" + path + "\"";
    }

    /// <summary>
    /// For a Windows shell when it is not known whether it is Command Prompt or PowerShell. Only double quotes
    /// work in both, so a path with anything in it that either of them would act on inside those is refused.
    /// </summary>
    /// <returns>Null for a path that cannot be written safely for both, which must then not be typed at all.</returns>
    public static string? Windows(string path)
    {
        // Besides what Command Prompt acts on: PowerShell's variables and escapes, and the curly quotes it takes for the straight one.
        if (Cmd(path) is not string quoted) return null;
        return path.AsSpan().ContainsAny("$`“”„") ? null : quoted;
    }
}

/// <summary>Makes sense of the folder a shell reports with OSC 7.</summary>
public static class ReportedDirectory
{
    private const string FileScheme = "file://";

    /// <summary>
    /// A PROMPT_COMMAND that makes bash report its folder each time it shows a prompt. Where cygpath exists,
    /// as in Git Bash and MSYS2, the folder is given as Windows knows it, because those shells show some
    /// folders under names of their own.
    /// </summary>
    public const string BashPromptCommand =
        """printf '\033]7;file://%s%s\033\\' "$HOSTNAME" "$(p=$(cygpath -m "$PWD" 2>/dev/null) && printf '/%s' "$p" || printf '%s' "$PWD")" """;

    /// <summary>
    /// Turns what the shell sent into a path on this computer. The result still has to be checked: a shell
    /// on another computer, or inside WSL, reports folders that do not exist here.
    /// </summary>
    public static string? ToLocalPath(string reported)
    {
        string path = reported.Trim();
        if (path.StartsWith(FileScheme, StringComparison.OrdinalIgnoreCase))
        {
            // file://host/path: the host is whatever the shell calls its computer, and is not needed.
            int slash = path.IndexOf('/', FileScheme.Length);
            if (slash < 0) return null;

            try
            {
                path = Uri.UnescapeDataString(path[slash..]);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        if (path.Length == 0 || path.Contains('\0')) return null;
        if (!OperatingSystem.IsWindows()) return path;

        // "/C:/Users/me" as a file address writes it, and "/c/Users/me" as Git Bash and MSYS2 do.
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        else if (path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/')) path = char.ToUpperInvariant(path[1]) + ":" + path[2..];

        if (path.Length == 2 && path[1] == ':') path += "/";
        return path.Replace('/', '\\');
    }

    /// <summary>The name of the git repository a folder belongs to, or null if it is not in one.</summary>
    public static string? RepositoryName(string folder)
    {
        try
        {
            for (DirectoryInfo? current = new(folder); current is not null; current = current.Parent)
            {
                string marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return current.Name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // A folder that cannot be looked at is treated as not being in a repository.
        }

        return null;
    }
}

/// <summary>
/// What to enter in a shell so that it marks its prompts and commands with OSC 133. That is what moving
/// from prompt to prompt, knowing when a command finished and copying its output all go by.
/// </summary>
public static class ShellIntegration
{
    // Each is one line that can be typed into a running shell, safe to enter twice, and lasts until
    // that shell exits. The space in front keeps it out of the shell's own history where the shell
    // is set up to honor that.

    /// <summary>
    /// For bash 4.4 and later. The end of the last command and the start of the prompt are marked from
    /// PROMPT_COMMAND rather than from PS1, because many setups build PS1 anew for every prompt. PS0 is
    /// shown only when there is a command to run, so an empty line does not count as one.
    /// </summary>
    public const string Bash =
        """ if [ -z "$ELIO_SHELL_INTEGRATION" ]; then ELIO_SHELL_INTEGRATION=1; __elio_prompt() { local s=$?; printf '\033]133;D;%s\007\033]133;A\007' "$s"; return $s; }; PROMPT_COMMAND="__elio_prompt${PROMPT_COMMAND:+;$PROMPT_COMMAND}"; PS0="${PS0}"'\e]133;C\a'; PS1="${PS1}"'\[\e]133;B\a\]'; fi""";

    /// <summary>For zsh, through its hooks for "before a prompt" and "before a command runs".</summary>
    public const string Zsh =
        """ if [[ -z "$ELIO_SHELL_INTEGRATION" ]]; then ELIO_SHELL_INTEGRATION=1; __elio_precmd() { print -Pn '\e]133;D;%?\a\e]133;A\a' }; __elio_preexec() { print -n '\e]133;C\a' }; autoload -Uz add-zsh-hook; add-zsh-hook precmd __elio_precmd; add-zsh-hook preexec __elio_preexec; PS1="${PS1}"$'%{\e]133;B\a%}'; fi""";

    /// <summary>
    /// For PowerShell 5.1 and later. The prompt function is wrapped to mark the end of the last command
    /// and the prompt itself. Where a command's output starts can only be told with PSReadLine loaded,
    /// which it is unless the user turned it off; without it prompts are still marked.
    /// </summary>
    public const string PowerShell =
        """ if (-not $global:ElioShellIntegration) { $global:ElioShellIntegration = $true; $global:ElioPrompt = $function:prompt; function global:prompt { $ok = $?; $code = $global:LASTEXITCODE; $e = [char]27; $b = [char]7; $status = if ($ok) { 0 } elseif ($code) { $code } else { 1 }; $text = & $global:ElioPrompt; $global:LASTEXITCODE = $code; "$e]133;D;$status$b$e]133;A$b$text$e]133;B$b" }; if (Get-Module PSReadLine) { $global:ElioReadLine = $function:PSConsoleHostReadLine; function global:PSConsoleHostReadLine { $line = & $global:ElioReadLine; if ("$line".Trim()) { [Console]::Write("$([char]27)]133;C$([char]7)") }; $line } } }""";

    /// <summary>The line for a shell, going by its name or the path of its program. Null for a shell there is none for.</summary>
    public static string? For(string shell)
    {
        string name = Path.GetFileNameWithoutExtension(shell.Replace('\\', '/').TrimEnd('/')).ToLowerInvariant();
        return name switch
        {
            "bash" => Bash,
            "zsh" => Zsh,
            "pwsh" or "powershell" => PowerShell,
            _ => null,
        };
    }
}
