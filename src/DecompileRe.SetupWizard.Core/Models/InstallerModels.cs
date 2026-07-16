using System.Text.Json.Serialization;

namespace DecompileRe.SetupWizard.Core.Models;

public sealed record IdaInstallation(
    string DisplayName,
    string RootPath,
    string ExecutablePath,
    Version? Version,
    string UserPluginDirectory,
    string? PythonExecutable,
    Version? PythonVersion,
    bool HasIdaPython)
{
    public string VersionLabel => Version is null ? "Version unknown" : $"IDA {Version.Major}.{Version.Minor}";

    public string PythonLabel => PythonVersion is null
        ? "Compatible Python not found"
        : $"Python {PythonVersion.Major}.{PythonVersion.Minor}";

    public string IdaPythonLabel => HasIdaPython ? "IDAPython detected" : "IDAPython requires configuration";
}

public sealed class ReleaseManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("minimum_ida_major")]
    public int MinimumIdaMajor { get; init; }

    [JsonPropertyName("maximum_ida_major")]
    public int MaximumIdaMajor { get; init; }

    [JsonPropertyName("plugin")]
    public required ReleaseAssetDescriptor Plugin { get; init; }

    [JsonPropertyName("python_dependencies")]
    public IReadOnlyList<PythonDependencyDescriptor> PythonDependencies { get; init; } = [];
}

public class ReleaseAssetDescriptor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

public sealed class PythonDependencyDescriptor : ReleaseAssetDescriptor
{
    [JsonPropertyName("runtime_identifier")]
    public required string RuntimeIdentifier { get; init; }

    [JsonPropertyName("python_tag")]
    public required string PythonTag { get; init; }
}

public sealed record GitHubReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Digest);

public sealed record VerifiedRelease(
    string Tag,
    ReleaseManifest Manifest,
    IReadOnlyDictionary<string, GitHubReleaseAsset> Assets);

public sealed record InstallRequest(
    IdaInstallation Ida,
    VerifiedRelease Release);

public sealed record InstallProgress(
    string Stage,
    string Message,
    double? Fraction = null);

public sealed record InstallResult(
    string Version,
    string PluginDirectory,
    string? BackupDirectory);
