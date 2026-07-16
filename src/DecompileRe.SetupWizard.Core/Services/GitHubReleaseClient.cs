using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DecompileRe.SetupWizard.Core.Models;

namespace DecompileRe.SetupWizard.Core.Services;

public sealed class GitHubReleaseClient
{
    private const int MaximumRedirects = 5;
    private const int MaximumManifestBytes = 512 * 1024;
    private const int MaximumSignatureBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    private readonly HttpClient _httpClient;
    private readonly InstallerOptions _options;

    public GitHubReleaseClient(HttpClient httpClient, InstallerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Decompile.re-Setup-Wizard/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<VerifiedRelease> GetLatestVerifiedReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.LatestReleaseUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("GitHub returned an empty release response.");

        if (release.Draft || release.Prerelease)
        {
            throw new InvalidDataException("GitHub returned a non-production release.");
        }

        var assets = release.Assets.ToDictionary(
            asset => asset.Name,
            asset => new GitHubReleaseAsset(asset.Name, asset.DownloadUri, asset.Size, asset.Digest),
            StringComparer.Ordinal);

        var manifestAsset = RequireAsset(assets, _options.ManifestAssetName);
        var signatureAsset = RequireAsset(assets, _options.SignatureAssetName);
        var manifestBytes = await DownloadBytesAsync(manifestAsset, MaximumManifestBytes, cancellationToken);
        var signatureBytes = await DownloadBytesAsync(signatureAsset, MaximumSignatureBytes, cancellationToken);
        ReleaseSecurity.VerifyManifestSignature(manifestBytes, signatureBytes, _options.SigningPublicKeyPem);

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("The release manifest is empty.");
        ValidateManifest(manifest, assets);
        return new VerifiedRelease(release.TagName, manifest, assets);
    }

    public async Task DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        long maximumBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > maximumBytes)
        {
            throw new InvalidDataException("The release asset size is outside the permitted range.");
        }

        using var response = await SendFollowingTrustedRedirectsAsync(asset.DownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
        {
            throw new InvalidDataException("The release asset size does not match GitHub metadata.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes || total > asset.Size)
            {
                throw new InvalidDataException("The release asset exceeded its declared size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report((double)total / asset.Size);
        }

        if (total != asset.Size)
        {
            throw new InvalidDataException("The release asset ended before its declared size.");
        }
    }

    private async Task<byte[]> DownloadBytesAsync(
        GitHubReleaseAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > maximumBytes)
        {
            throw new InvalidDataException("A release metadata asset is outside the permitted size range.");
        }

        using var response = await SendFollowingTrustedRedirectsAsync(asset.DownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream((int)asset.Size);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("A release metadata asset exceeded the permitted size.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendFollowingTrustedRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateDownloadUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new HttpRequestException("GitHub returned a redirect without a destination.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException("The GitHub release download exceeded the redirect limit.");
    }

    private static GitHubReleaseAsset RequireAsset(
        IReadOnlyDictionary<string, GitHubReleaseAsset> assets,
        string name) =>
        assets.TryGetValue(name, out var asset)
            ? asset
            : throw new InvalidDataException($"The release is missing required asset '{name}'.");

    private static void ValidateManifest(
        ReleaseManifest manifest,
        IReadOnlyDictionary<string, GitHubReleaseAsset> assets)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("The release manifest schema is not supported.");
        }

        if (!Version.TryParse(manifest.Version, out _) ||
            manifest.MinimumIdaMajor < 8 ||
            manifest.MaximumIdaMajor < manifest.MinimumIdaMajor)
        {
            throw new InvalidDataException("The release manifest contains invalid compatibility metadata.");
        }

        ValidateDescriptor(manifest.Plugin, assets);
        foreach (var dependency in manifest.PythonDependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.RuntimeIdentifier) ||
                string.IsNullOrWhiteSpace(dependency.PythonTag))
            {
                throw new InvalidDataException("A Python dependency asset is missing platform metadata.");
            }

            ValidateDescriptor(dependency, assets);
        }
    }

    private static void ValidateDescriptor(
        ReleaseAssetDescriptor descriptor,
        IReadOnlyDictionary<string, GitHubReleaseAsset> assets)
    {
        ReleaseSecurity.ValidateSha256(descriptor.Sha256);
        var asset = RequireAsset(assets, descriptor.Name);
        if (descriptor.Size <= 0 || descriptor.Size != asset.Size)
        {
            throw new InvalidDataException($"Release asset '{descriptor.Name}' has inconsistent size metadata.");
        }

        if (asset.Digest is { Length: > 0 } digest &&
            !digest.Equals($"sha256:{descriptor.Sha256}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Release asset '{descriptor.Name}' has inconsistent digest metadata.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedDownloadHosts.Contains(uri.Host))
        {
            throw new InvalidDataException("A release asset redirected to an untrusted location.");
        }
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubAssetResponse> Assets { get; init; } = [];
    }

    private sealed class GitHubAssetResponse
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public required Uri DownloadUri { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
