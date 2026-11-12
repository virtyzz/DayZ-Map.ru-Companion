using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CrosshairMarker;

internal sealed class UpdateService
{
    internal static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(12);
    private const string ReleasesPageUrl = "https://github.com/virtyzz/DayZ-Map.ru-Companion/releases";
    private const string LatestReleaseUrl = "https://api.github.com/repos/virtyzz/DayZ-Map.ru-Companion/releases/latest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private UpdateInfo? cachedInfo;
    public UpdateProgress Progress { get; private set; } = new(false, "");
    public event Action? ProgressChanged;

    public UpdateService(HttpClient? client = null)
    {
        httpClient = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DayZMapCompanion-Updater");
    }

    public async Task<UpdateInfo> GetLatestAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        if (!forceRefresh && cachedInfo is not null)
        {
            return cachedInfo;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await httpClient.GetAsync(LatestReleaseUrl, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, timeout.Token);
            if (release is null)
            {
                return cachedInfo = UpdateInfo.Failed(CurrentVersionText, "GitHub вернул пустой ответ.");
            }

            var installer = release.Assets?
                .OrderBy(asset => asset.Name.Contains("latest", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

            var latestVersion = NormalizeVersionText(release.TagName);
            return cachedInfo = new UpdateInfo(
                CurrentVersion: CurrentVersionText,
                LatestVersion: latestVersion,
                LatestTag: release.TagName,
                IsUpdateAvailable: IsNewerVersion(latestVersion, CurrentVersionText),
                ReleaseUrl: release.HtmlUrl,
                InstallerUrl: installer?.BrowserDownloadUrl,
                ReleaseNotes: BuildDisplayReleaseNotes(release.Body),
                PublishedAt: release.PublishedAt,
                ErrorMessage: null,
                InstallerSha256: ParseDigest(installer?.Digest),
                InstallerSize: installer?.Size ?? 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return cachedInfo = UpdateInfo.Failed(CurrentVersionText, ex.Message);
        }
    }

    internal static string? ParseDigest(string? digest) => digest is not null
        && Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$") ? digest[7..].ToLowerInvariant() : null;

    internal static bool IsInstallerUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme == "https" && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith("/virtyzz/DayZ-Map.ru-Companion/releases/download/", StringComparison.Ordinal)
        && url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(url.UserInfo);

    public void Report(UpdateProgress value) { Progress = value; ProgressChanged?.Invoke(); }

    public async Task<string> DownloadInstallerAsync(UpdateInfo info, string directory, CancellationToken token)
    {
        if (!info.CanInstall) throw new InvalidOperationException("Для этого релиза нет проверенного обновления. Используйте «Скачать установщик».");
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "installer.download");
        var target = Path.Combine(directory, "installer.exe");
        try
        {
            Report(new(true, "Скачивание обновления…", 0, true));
            using var response = await httpClient.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var expected = info.InstallerSize;
            if (response.Content.Headers.ContentLength is long length && length != expected)
                throw new InvalidDataException("Размер обновления не совпадает с данными релиза.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count;
                    if (received > expected) throw new InvalidDataException("Размер обновления превышает ожидаемый.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    var percent = (int)(received * 100 / expected);
                    if (percent != lastPercent)
                    {
                        Report(new(true, $"Скачивание обновления — {percent}%", percent, true));
                        lastPercent = percent;
                    }
                }
                Report(new(true, "Проверка обновления…"));
                if (received != expected || !Convert.ToHexString(hash.GetHashAndReset()).Equals(info.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Проверка обновления не пройдена. Файл не будет запущен.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, target);
            return target;
        }
        catch { File.Delete(partial); throw; }
    }

    public static void OpenDownload(UpdateInfo info)
    {
        if (AppDistribution.IsMicrosoftStore) { AppDistribution.OpenStore(); return; }
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

    internal static bool ShouldShowReminder(UpdateInfo info, string? lastPromptedVersion, DateTimeOffset? lastPromptAt, DateTimeOffset now)
    {
        if (!info.IsUpdateAvailable || string.IsNullOrWhiteSpace(info.LatestVersion)) return false;
        if (!string.Equals(lastPromptedVersion, info.LatestVersion, StringComparison.OrdinalIgnoreCase)) return true;
        return !lastPromptAt.HasValue || now - lastPromptAt.Value >= ReminderInterval;
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
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
        [JsonPropertyName("size")]
        public long Size { get; set; }
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
    string? ErrorMessage,
    string? InstallerSha256 = null,
    long InstallerSize = 0)
{
    public bool CanInstall => IsUpdateAvailable && UpdateService.IsInstallerUrl(InstallerUrl)
        && InstallerSize is > 0 and <= 2L * 1024 * 1024 * 1024
        && Regex.IsMatch(InstallerSha256 ?? "", "^[0-9a-fA-F]{64}$");
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

internal sealed record UpdateProgress(bool IsBusy, string Message, int? Percent = null, bool CanCancel = false);
