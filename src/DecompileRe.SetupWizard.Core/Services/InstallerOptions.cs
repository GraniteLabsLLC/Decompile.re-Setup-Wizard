namespace DecompileRe.SetupWizard.Core.Services;

public sealed class InstallerOptions
{
    public const string DefaultOwner = "AI-Reversal";
    public const string DefaultRepository = "IDA-Pro-Client";

    public string GitHubOwner { get; init; } = DefaultOwner;

    public string GitHubRepository { get; init; } = DefaultRepository;

    public string ManifestAssetName { get; init; } = "release-manifest.json";

    public string SignatureAssetName { get; init; } = "release-manifest.sig";

    public required string SigningPublicKeyPem { get; init; }

    public long MaximumPluginBytes { get; init; } = 128 * 1024 * 1024;

    public long MaximumDependencyBytes { get; init; } = 256 * 1024 * 1024;

    public Uri LatestReleaseUri => new(
        $"https://api.github.com/repos/{Uri.EscapeDataString(GitHubOwner)}/{Uri.EscapeDataString(GitHubRepository)}/releases/latest");
}
