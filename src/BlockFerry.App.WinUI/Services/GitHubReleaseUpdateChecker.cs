using System.Net.Http.Headers;
using System.Text.Json;

namespace BlockFerry.App.WinUI.Services;

internal static class BlockFerryReleaseInfo
{
    internal const string CurrentVersion = "0.1.0-beta.5";
    internal const string RepositoryUrl = "https://github.com/Rem021/BlockFerry";
    internal const string ReleasesApiUrl =
        "https://api.github.com/repos/Rem021/BlockFerry/releases?per_page=20";
}

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Unavailable,
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersion = null,
    Uri? ReleasePage = null)
{
    internal static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);

    internal static UpdateCheckResult Unavailable() => new(UpdateCheckStatus.Unavailable);
}

internal sealed class GitHubReleaseUpdateChecker : IDisposable
{
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private int hasChecked;
    private bool disposed;

    internal GitHubReleaseUpdateChecker()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal GitHubReleaseUpdateChecker(HttpClient httpClient, bool ownsHttpClient = false)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.ownsHttpClient = ownsHttpClient;
    }

    internal async Task<UpdateCheckResult> CheckOnceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref hasChecked, 1) != 0)
        {
            return UpdateCheckResult.Unavailable();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BlockFerryReleaseInfo.ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
                "BlockFerry",
                BlockFerryReleaseInfo.CurrentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return UpdateCheckResult.Unavailable();
            }

            var payload = await ReadBoundedAsync(response.Content, timeout.Token)
                .ConfigureAwait(false);
            return EvaluateReleasePayload(BlockFerryReleaseInfo.CurrentVersion, payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Unavailable();
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Unavailable();
        }
        catch (IOException)
        {
            return UpdateCheckResult.Unavailable();
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Unavailable();
        }
    }

    internal static UpdateCheckResult EvaluateReleasePayload(
        string currentVersion,
        ReadOnlyMemory<byte> payload)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
        {
            return UpdateCheckResult.Unavailable();
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return UpdateCheckResult.Unavailable();
        }

        SemanticVersion? newestVersion = null;
        string? newestTag = null;
        Uri? newestPage = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                ReadBoolean(release, "draft") ||
                !TryReadBoundedString(release, "tag_name", 64, out var tag) ||
                !SemanticVersion.TryParse(tag, out var candidate) ||
                candidate.CompareTo(current) <= 0 ||
                !TryReadBoundedString(release, "html_url", 512, out var pageText) ||
                !TryValidateReleasePage(pageText, out var page))
            {
                continue;
            }

            if (newestVersion is null || candidate.CompareTo(newestVersion.Value) > 0)
            {
                newestVersion = candidate;
                newestTag = tag;
                newestPage = page;
            }
        }

        return newestVersion is null
            ? UpdateCheckResult.UpToDate()
            : new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                newestTag,
                newestPage);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new IOException("The update response exceeded its size limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool TryReadBoundedString(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryValidateReleasePage(string value, out Uri page)
    {
        page = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            candidate.UserInfo.Length != 0 ||
            candidate.Port != 443 ||
            !candidate.AbsolutePath.StartsWith(
                "/Rem021/BlockFerry/releases/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        page = candidate;
        return true;
    }
}

internal readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string[] Prerelease) : IComparable<SemanticVersion>
{
    internal static bool TryParse(string value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        var candidate = value.AsSpan().Trim();
        if (candidate.Length > 0 && (candidate[0] == 'v' || candidate[0] == 'V'))
        {
            candidate = candidate[1..];
        }

        var buildIndex = candidate.IndexOf('+');
        if (buildIndex >= 0)
        {
            candidate = candidate[..buildIndex];
        }

        ReadOnlySpan<char> prereleaseSpan = [];
        var prereleaseIndex = candidate.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prereleaseSpan = candidate[(prereleaseIndex + 1)..];
            candidate = candidate[..prereleaseIndex];
            if (prereleaseSpan.IsEmpty)
            {
                return false;
            }
        }

        var coreParts = candidate.ToString().Split('.');
        if (coreParts.Length != 3 ||
            !TryParseCoreNumber(coreParts[0], out var major) ||
            !TryParseCoreNumber(coreParts[1], out var minor) ||
            !TryParseCoreNumber(coreParts[2], out var patch))
        {
            return false;
        }

        var identifiers = prereleaseSpan.IsEmpty
            ? Array.Empty<string>()
            : prereleaseSpan.ToString().Split('.');
        if (identifiers.Any(identifier => !IsValidPrereleaseIdentifier(identifier)))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, identifiers);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0)
        {
            coreComparison = Minor.CompareTo(other.Minor);
        }

        if (coreComparison == 0)
        {
            coreComparison = Patch.CompareTo(other.Patch);
        }

        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
        {
            return Prerelease.Length == other.Prerelease.Length
                ? 0
                : Prerelease.Length == 0 ? 1 : -1;
        }

        var sharedLength = Math.Min(Prerelease.Length, other.Prerelease.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = CompareIdentifier(Prerelease[index], other.Prerelease[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    private static bool TryParseCoreNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               value.All(char.IsAsciiDigit) &&
               int.TryParse(value, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool IsValidPrereleaseIdentifier(string value)
    {
        if (value.Length == 0 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            return false;
        }

        return !value.All(char.IsAsciiDigit) || value.Length == 1 || value[0] != '0';
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.Compare(left, right, StringComparison.Ordinal);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
