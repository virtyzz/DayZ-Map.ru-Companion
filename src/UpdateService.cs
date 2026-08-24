using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CrosshairMarker;

internal sealed class UpdateService
{
    private const string ReleasesPageUrl = "https://github.com/virtyzz/DayZ-Map.ru-Companion/releases";
    private const string LatestReleaseUrl = "https://api.github.com/repos/virtyzz/DayZ-Map.ru-Companion/releases/latest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient = new();
    private UpdateInfo? cachedInfo;

    public UpdateService()
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DayZMapCompanion-Updater");
    }

    public async Task<UpdateInfo> GetLatestAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && cachedInfo is not null)
        {
            return cachedInfo;
        }

        try
        {
            using var response = await httpClient.GetAsync(LatestReleaseUrl);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions);
            if (release is null)
            {
                return cachedInfo = UpdateInfo.Failed(CurrentVersionText, "GitHub вернул пустой ответ.");
            }

            var installerUrl = release.Assets?
                .FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

            var latestVersion = NormalizeVersionText(release.TagName);
            return cachedInfo = new UpdateInfo(
                CurrentVersion: CurrentVersionText,
                LatestVersion: latestVersion,
                LatestTag: release.TagName,
                IsUpdateAvailable: IsNewerVersion(latestVersion, CurrentVersionText),
                ReleaseUrl: release.HtmlUrl,
                InstallerUrl: installerUrl,
                ReleaseNotes: BuildDisplayReleaseNotes(release.Body),
                PublishedAt: release.PublishedAt,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return cachedInfo = UpdateInfo.Failed(CurrentVersionText, ex.Message);
        }
    }

    public static void OpenDownload(UpdateInfo info)
    {
        var url = string.IsNullOrWhiteSpace(info.InstallerUrl) ? info.ReleaseUrl : info.InstallerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    public static void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo(ReleasesPageUrl)
        {
            UseShellExecute = true
        });
    }

    private static string CurrentVersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "0.0.0";
            return NormalizeVersionText(version);
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        return TryParseVersion(latest, out var latestVersion)
            && TryParseVersion(current, out var currentVersion)
            && latestVersion > currentVersion;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var clean = NormalizeVersionText(value);
        var dashIndex = clean.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            clean = clean[..dashIndex];
        }

        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 3)
        {
            parts.Add("0");
        }

        return Version.TryParse(string.Join(".", parts.Take(4)), out version!);
    }

    private static string NormalizeVersionText(string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? "0.0.0" : value.Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[1..];
        }

        var plusIndex = clean.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            clean = clean[..plusIndex];
        }

        return clean;
    }

    private static string BuildDisplayReleaseNotes(string? body)
    {
        var clean = string.IsNullOrWhiteSpace(body) ? "" : body.Trim();
        if (clean.Length > 0)
        {
            clean = Regex.Replace(clean, @"(?im)^\s*\*\*Full Changelog\*\*:.*$", "").Trim();
            clean = Regex.Replace(clean, @"(?im)^\s*Full Changelog:.*$", "").Trim();
        }

        if (!string.IsNullOrWhiteSpace(clean))
        {
            return clean;
        }

        return """
Изменения в релизе:
- Исправления и улучшения стабильности.
- Подробный список изменений доступен на странице релиза.
""";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}

internal sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string LatestTag,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    string? InstallerUrl,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    string? ErrorMessage)
{
    public static UpdateInfo Failed(string currentVersion, string message) => new(
        CurrentVersion: currentVersion,
        LatestVersion: currentVersion,
        LatestTag: "",
        IsUpdateAvailable: false,
        ReleaseUrl: "",
        InstallerUrl: null,
        ReleaseNotes: "",
        PublishedAt: null,
        ErrorMessage: message);
}
