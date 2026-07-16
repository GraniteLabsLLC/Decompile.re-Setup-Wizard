using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DecompileRe.SetupWizard.Core.Models;

namespace DecompileRe.SetupWizard.Core.Services;

public sealed class PluginInstaller
{
    private const long MaximumExpandedPluginBytes = 256 * 1024 * 1024;
    private const long MaximumExpandedDependencyBytes = 512 * 1024 * 1024;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly InstallerOptions _options;
    private readonly SafeZipExtractor _extractor;

    public PluginInstaller(
        GitHubReleaseClient releaseClient,
        InstallerOptions options,
        SafeZipExtractor extractor)
    {
        _releaseClient = releaseClient;
        _options = options;
        _extractor = extractor;
    }

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateCompatibility(request);
        var workDirectory = Path.Combine(Path.GetTempPath(), $"decompile-re-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            progress?.Report(new InstallProgress("download", "Downloading the verified plugin release..."));
            var pluginArchive = await DownloadAndVerifyAsync(
                request.Release,
                request.Release.Manifest.Plugin,
                _options.MaximumPluginBytes,
                workDirectory,
                progress,
                cancellationToken);

            var extractedPlugin = Path.Combine(workDirectory, "plugin");
            await _extractor.ExtractAsync(
                pluginArchive,
                extractedPlugin,
                MaximumExpandedPluginBytes,
                cancellationToken);
            var payloadRoot = LocatePluginPayload(extractedPlugin);

            var dependency = SelectDependency(request);
            if (dependency is not null)
            {
                if (request.Ida.PythonExecutable is null)
                {
                    throw new InvalidOperationException("A compatible Python installation is required for this release.");
                }

                progress?.Report(new InstallProgress("dependencies", "Installing verified Python dependencies..."));
                var dependencyArchive = await DownloadAndVerifyAsync(
                    request.Release,
                    dependency,
                    _options.MaximumDependencyBytes,
                    workDirectory,
                    progress,
                    cancellationToken);
                var extractedDependencies = Path.Combine(workDirectory, "dependencies");
                await _extractor.ExtractAsync(
                    dependencyArchive,
                    extractedDependencies,
                    MaximumExpandedDependencyBytes,
                    cancellationToken);
                await InstallPythonDependenciesAsync(
                    request.Ida.PythonExecutable,
                    extractedDependencies,
                    cancellationToken);
            }

            progress?.Report(new InstallProgress("install", "Installing Decompile.re for IDA Pro..."));
            var backup = InstallPluginAtomically(
                payloadRoot,
                request.Ida.UserPluginDirectory,
                request.Release.Manifest.Version);
            progress?.Report(new InstallProgress("complete", "Installation complete.", 1));
            return new InstallResult(
                request.Release.Manifest.Version,
                request.Ida.UserPluginDirectory,
                backup);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task<string> DownloadAndVerifyAsync(
        VerifiedRelease release,
        ReleaseAssetDescriptor descriptor,
        long maximumBytes,
        string workDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!release.Assets.TryGetValue(descriptor.Name, out var asset))
        {
            throw new InvalidDataException($"Release asset '{descriptor.Name}' was not returned by GitHub.");
        }

        var path = Path.Combine(workDirectory, Path.GetRandomFileName());
        var downloadProgress = new Progress<double>(fraction =>
            progress?.Report(new InstallProgress("download", $"Downloading {descriptor.Name}...", fraction)));
        await _releaseClient.DownloadAssetAsync(asset, path, maximumBytes, downloadProgress, cancellationToken);
        await ReleaseSecurity.VerifyFileHashAsync(path, descriptor.Sha256, cancellationToken);
        return path;
    }

    private static void ValidateCompatibility(InstallRequest request)
    {
        if (request.Ida.Version is { } version &&
            (version.Major < request.Release.Manifest.MinimumIdaMajor ||
             version.Major > request.Release.Manifest.MaximumIdaMajor))
        {
            throw new InvalidOperationException(
                $"This plugin release supports IDA {request.Release.Manifest.MinimumIdaMajor}.x through " +
                $"{request.Release.Manifest.MaximumIdaMajor}.x.");
        }
    }

    private static PythonDependencyDescriptor? SelectDependency(InstallRequest request)
    {
        if (request.Release.Manifest.PythonDependencies.Count == 0)
        {
            return null;
        }

        if (request.Ida.PythonVersion is null)
        {
            throw new InvalidOperationException(
                "This release requires Python dependencies, but a compatible Python installation was not found.");
        }

        var runtimeIdentifier = GetRuntimeIdentifier();
        var pythonTag = $"cp{request.Ida.PythonVersion.Major}{request.Ida.PythonVersion.Minor}";
        return request.Release.Manifest.PythonDependencies.SingleOrDefault(dependency =>
                   dependency.RuntimeIdentifier.Equals(runtimeIdentifier, StringComparison.OrdinalIgnoreCase) &&
                   dependency.PythonTag.Equals(pythonTag, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"This release does not provide dependencies for {runtimeIdentifier}/{pythonTag}.");
    }

    private static string GetRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Only x64 and arm64 systems are supported."),
        };
        return $"{os}-{architecture}";
    }

    private static string LocatePluginPayload(string extractionRoot)
    {
        if (ContainsPlugin(extractionRoot))
        {
            return extractionRoot;
        }

        var candidates = Directory.EnumerateDirectories(extractionRoot)
            .Where(ContainsPlugin)
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw new InvalidDataException("The plugin archive does not have the expected layout.");
    }

    private static bool ContainsPlugin(string root) =>
        File.Exists(Path.Combine(root, "ida_ai_client.py")) &&
        Directory.Exists(Path.Combine(root, "ida_ai_client"));

    private static async Task InstallPythonDependenciesAsync(
        string pythonExecutable,
        string dependencyRoot,
        CancellationToken cancellationToken)
    {
        var lockFile = Path.Combine(dependencyRoot, "requirements.lock");
        var wheels = Path.Combine(dependencyRoot, "wheels");
        if (!File.Exists(lockFile) || !Directory.Exists(wheels))
        {
            throw new InvalidDataException("The Python dependency bundle is incomplete.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("pip");
        process.StartInfo.ArgumentList.Add("install");
        process.StartInfo.ArgumentList.Add("--disable-pip-version-check");
        process.StartInfo.ArgumentList.Add("--no-index");
        process.StartInfo.ArgumentList.Add("--require-hashes");
        process.StartInfo.ArgumentList.Add("--only-binary=:all:");
        process.StartInfo.ArgumentList.Add("--user");
        process.StartInfo.ArgumentList.Add("--find-links");
        process.StartInfo.ArgumentList.Add(wheels);
        process.StartInfo.ArgumentList.Add("--requirement");
        process.StartInfo.ArgumentList.Add(lockFile);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Python dependency installation failed.\n{SanitizeProcessOutput(error, output)}");
        }
    }

    internal static string? InstallPluginAtomically(string payloadRoot, string pluginDirectory, string version)
    {
        Directory.CreateDirectory(pluginDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(pluginDirectory, $".decompile-re-staging-{operationId}");
        var backup = Path.Combine(pluginDirectory, ".decompile-re-backups", $"{DateTime.UtcNow:yyyyMMddHHmmss}-{operationId}");
        var targetModule = Path.Combine(pluginDirectory, "ida_ai_client");
        var targetEntry = Path.Combine(pluginDirectory, "ida_ai_client.py");
        var targetMarker = Path.Combine(pluginDirectory, "decompile-re-install.json");
        var stagedModule = Path.Combine(staging, "ida_ai_client");
        var stagedEntry = Path.Combine(staging, "ida_ai_client.py");
        var stagedMarker = Path.Combine(staging, "decompile-re-install.json");
        string? completedBackup = null;
        var moduleActivated = false;
        var entryActivated = false;
        var markerActivated = false;

        try
        {
            Directory.CreateDirectory(staging);
            CopyDirectory(Path.Combine(payloadRoot, "ida_ai_client"), stagedModule);
            File.Copy(Path.Combine(payloadRoot, "ida_ai_client.py"), stagedEntry, overwrite: false);
            File.WriteAllText(
                stagedMarker,
                JsonSerializer.Serialize(new { version, installed_at = DateTimeOffset.UtcNow }));

            if (Directory.Exists(targetModule) || File.Exists(targetEntry) || File.Exists(targetMarker))
            {
                Directory.CreateDirectory(backup);
                completedBackup = backup;
                if (Directory.Exists(targetModule))
                {
                    Directory.Move(targetModule, Path.Combine(backup, "ida_ai_client"));
                }

                if (File.Exists(targetEntry))
                {
                    File.Move(targetEntry, Path.Combine(backup, "ida_ai_client.py"));
                }

                if (File.Exists(targetMarker))
                {
                    File.Move(targetMarker, Path.Combine(backup, "decompile-re-install.json"));
                }

            }

            Directory.Move(stagedModule, targetModule);
            moduleActivated = true;
            File.Move(stagedEntry, targetEntry);
            entryActivated = true;
            File.Move(stagedMarker, targetMarker);
            markerActivated = true;
            Directory.Delete(staging);
            return completedBackup;
        }
        catch
        {
            if (moduleActivated)
            {
                TryDeleteDirectory(targetModule);
            }

            if (entryActivated)
            {
                TryDeleteFile(targetEntry);
            }

            if (markerActivated)
            {
                TryDeleteFile(targetMarker);
            }

            if (completedBackup is not null)
            {
                var backupModule = Path.Combine(completedBackup, "ida_ai_client");
                var backupEntry = Path.Combine(completedBackup, "ida_ai_client.py");
                var backupMarker = Path.Combine(completedBackup, "decompile-re-install.json");
                if (Directory.Exists(backupModule))
                {
                    Directory.Move(backupModule, targetModule);
                }

                if (File.Exists(backupEntry))
                {
                    File.Move(backupEntry, targetEntry);
                }

                if (File.Exists(backupMarker))
                {
                    File.Move(backupMarker, targetMarker);
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(destination, relative), overwrite: false);
        }
    }

    private static string SanitizeProcessOutput(params string[] values)
    {
        var output = string.Join(Environment.NewLine, values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return output.Length <= 4_096 ? output : output[..4_096];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
