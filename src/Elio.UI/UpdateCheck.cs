using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Elio.UI;

/// <summary>A release that is newer than the running version, as the check found it.</summary>
/// <param name="Version">The version, as the numbers it was compared by.</param>
/// <param name="Tag">The release's tag, such as "v1.2.3", when it is a plain version that can be installed from. Null otherwise.</param>
/// <param name="Page">The release's page on github.com.</param>
public sealed record ReleaseInfo(string Version, string? Tag, Uri Page);

/// <summary>
/// Asks GitHub, at most once a day, whether a newer version of Elio has been released, and says so if there is one. It is
/// the only request Elio makes that the user did not ask for, it sends nothing but the question, and it can be turned
/// off in the settings. Whatever goes wrong, it stays quiet. Installing the new version is another matter: that happens
/// only when the user presses the button for it (<see cref="UpdateInstaller"/>).
/// </summary>
public static class UpdateCheck
{
    private const string LatestRelease = "https://api.github.com/repos/localtonet/elio/releases/latest";
    private const string ReleaseHost = "github.com";
    private const int LargestAnswer = 1024 * 1024;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Pause = TimeSpan.FromDays(1);

    private static int _started;

    /// <summary>Fetches what GitHub says about the latest release, as JSON. Replaced in tests.</summary>
    public static Func<CancellationToken, Task<string>> Fetch { get; set; } = FetchFromGitHubAsync;

    /// <summary>The version that is running.</summary>
    public static Version? Running => typeof(UpdateCheck).Assembly.GetName().Version;

    /// <summary>
    /// Starts the checks, the first time it is called, and shows what they find in the given window: one now, and one a
    /// day later for as long as Elio keeps running, since a copy that sits in the tray can run for weeks. Call it from
    /// the thread the window lives on.
    /// </summary>
    public static void Start(WorkspaceView workspace)
    {
        if (AppServices.MobileLayout || !AppServices.Settings.CheckForUpdates) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _ = RunAsync(workspace);
    }

    private static async Task RunAsync(WorkspaceView workspace)
    {
        while (true)
        {
            // A time in the future is a clock that was set back since, and does not put the check off.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan wait = AppServices.Settings.LastUpdateCheck is DateTimeOffset last && now >= last && now - last < Pause ? Pause - (now - last) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            // The setting may have been turned off since the last round.
            if (!AppServices.Settings.CheckForUpdates) return;

            if (await CheckAsync() is { } release && !string.Equals(AppServices.Settings.UpdateNoticeVersion, release.Version, StringComparison.Ordinal))
            {
                AppServices.Settings.UpdateNoticeVersion = release.Version;
                AppServices.SaveSettings();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => workspace.ShowUpdateNotice(release));
            }
        }
    }

    /// <summary>
    /// Asks now, whatever was asked before, and says what came of it in the window: that there is a newer version, that
    /// there is not, or that GitHub could not be reached. Used by the command that checks on request.
    /// </summary>
    public static async Task CheckNowAsync(WorkspaceView workspace)
    {
        if (AppServices.MobileLayout) return;

        (bool asked, ReleaseInfo? release) = await CheckWithOutcomeAsync();
        if (!asked) workspace.ShowNotice(Elio.Core.L.T("Elio could not find out if there is a newer version."));
        else if (release is null) workspace.ShowNotice(Elio.Core.L.T("Elio is up to date."));
        else workspace.ShowUpdateNotice(release);
    }

    private static async Task<ReleaseInfo?> CheckAsync() => (await CheckWithOutcomeAsync()).Release;

    // Whether GitHub answered, and the release if it is a newer one.
    private static async Task<(bool Asked, ReleaseInfo? Release)> CheckWithOutcomeAsync()
    {
        Elio.Core.AppSettings settings = AppServices.Settings;

        // Written down before asking, so that a check that fails is not tried again at every start either.
        settings.LastUpdateCheck = DateTimeOffset.UtcNow;
        AppServices.SaveSettings();

        string answer;
        try
        {
            using var cancellation = new CancellationTokenSource(Limit);
            answer = await Fetch(cancellation.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // Offline, or GitHub has had enough questions from this address for now.
            return (false, null);
        }

        if (!TryReadRelease(answer, out ReleaseInfo? release)) return (false, null);
        return (true, IsNewer(release.Version, Running) ? release : null);
    }

    /// <summary>
    /// Whether a release tag such as "v1.2.3" names a later version than the one given. A "v" in front and anything
    /// from a dash or a plus on, as in "v1.3.0-beta.1", are left out of the comparison. A tag that is not a version
    /// is never newer.
    /// </summary>
    public static bool IsNewer(string? tag, Version? running) =>
        TryParseVersion(tag, out Version? released) && Whole(released) > Whole(running ?? new Version(0, 0));

    /// <summary>Reads a version out of a release tag. Returns false if there is none in it.</summary>
    public static bool TryParseVersion(string? tag, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        int extra = text.IndexOfAny(['-', '+']);
        if (extra >= 0) text = text[..extra];

        // "2" alone is a version to people, though not to the parser.
        if (text.Length > 0 && !text.Contains('.')) text += ".0";
        return Version.TryParse(text, out version);
    }

    /// <summary>
    /// Takes the version, the tag and the page of a release out of GitHub's answer. The page is only accepted when it
    /// is an https address on github.com, since it is opened in the browser; without one there is nothing to offer.
    /// The tag is kept only when it is a plain "v1.2.3": a release with any other name is announced, not installed.
    /// </summary>
    public static bool TryReadRelease(string json, [NotNullWhen(true)] out ReleaseInfo? release)
    {
        release = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("html_url", out JsonElement url) || url.ValueKind != JsonValueKind.String) return false;
            if (!TryParseVersion(tag.GetString(), out Version? parsed)) return false;
            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? address) || !IsReleasePage(address)) return false;

            // Shown as the numbers it was compared by, so that nothing else from the answer reaches the screen.
            release = new ReleaseInfo(parsed.ToString(), IsPlainTag(tag.GetString()) ? tag.GetString() : null, address);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a tag is "v" and three numbers of at most four digits, which is all that goes into an address it is fetched from.</summary>
    public static bool IsPlainTag(string? tag)
    {
        if (tag is null || tag.Length is < 6 or > 17 || tag[0] != 'v') return false;

        string[] parts = tag[1..].Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is >= 1 and <= 4 && part.All(char.IsAsciiDigit));
    }

    /// <summary>Whether an address is one that may be opened as a release's page: https, on github.com itself.</summary>
    public static bool IsReleasePage(Uri address) =>
        address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && address.IsDefaultPort && address.UserInfo.Length == 0
        && string.Equals(address.IdnHost, ReleaseHost, StringComparison.OrdinalIgnoreCase);

    // 1.2 and 1.2.0.0 are the same version, though the parts left out count as less than zero when compared.
    private static Version Whole(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static async Task<string> FetchFromGitHubAsync(CancellationToken cancellation)
    {
        using var client = new HttpClient { Timeout = Limit, MaxResponseContentBufferSize = LargestAnswer };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);

        // GitHub turns away requests that do not say who is asking.
        request.Headers.TryAddWithoutValidation("User-Agent", "Elio");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using HttpResponseMessage response = await client.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation);
    }
}
