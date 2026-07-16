using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DecompileRe.SetupWizard.Core.Models;

namespace DecompileRe.SetupWizard.Core.Platform;

public interface IIdaDiscoveryService
{
    Task<IReadOnlyList<IdaInstallation>> FindInstallationsAsync(CancellationToken cancellationToken);

    Task<IdaInstallation?> InspectInstallationAsync(string rootPath, CancellationToken cancellationToken);
}

public sealed partial class IdaDiscoveryService : IIdaDiscoveryService
{
    public async Task<IReadOnlyList<IdaInstallation>> FindInstallationsAsync(CancellationToken cancellationToken)
    {
        var pythonCandidates = await FindPythonInstallationsAsync(cancellationToken);
        var installations = new List<IdaInstallation>();
        foreach (var root in FindCandidateRoots().Distinct(PathComparer()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installation = InspectInstallation(root, pythonCandidates);
            if (installation is not null)
            {
                installations.Add(installation);
            }
        }

        return installations
            .OrderByDescending(installation => installation.Version)
            .ThenBy(installation => installation.RootPath, PathComparer())
            .ToArray();
    }

    public async Task<IdaInstallation?> InspectInstallationAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
        {
            return null;
        }

        var pythonCandidates = await FindPythonInstallationsAsync(cancellationToken);
        return InspectInstallation(fullPath, pythonCandidates);
    }

    private static IdaInstallation? InspectInstallation(
        string root,
        IReadOnlyList<PythonInstallation> pythonCandidates)
    {
        var executable = FindIdaExecutable(root);
        if (executable is null)
        {
            return null;
        }

        var version = InferIdaVersion(root, executable);
        var python = SelectPython(pythonCandidates, version);
        return new IdaInstallation(
            DisplayName: Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            RootPath: root,
            ExecutablePath: executable,
            Version: version,
            UserPluginDirectory: GetUserPluginDirectory(),
            PythonExecutable: python?.Path,
            PythonVersion: python?.Version,
            HasIdaPython: HasIdaPython(root));
    }

    private static IEnumerable<string> FindCandidateRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var basePath in ExistingDirectories(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")))
            {
                foreach (var directory in EnumerateDirectories(basePath, "IDA*"))
                {
                    yield return directory;
                }

                var hexRays = Path.Combine(basePath, "Hex-Rays");
                foreach (var directory in EnumerateDirectories(hexRays, "IDA*"))
                {
                    yield return directory;
                }
            }

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var basePath in ExistingDirectories(
                         "/Applications",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")))
            {
                foreach (var directory in EnumerateDirectories(basePath, "IDA*.app"))
                {
                    yield return directory;
                }
            }

            yield break;
        }

        foreach (var basePath in ExistingDirectories(
                     "/opt",
                     "/usr/local",
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            foreach (var pattern in new[] { "ida*", "IDA*" })
            {
                foreach (var directory in EnumerateDirectories(basePath, pattern))
                {
                    yield return directory;
                }
            }
        }
    }

    private static string? FindIdaExecutable(string root)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "ida.exe", "ida64.exe" }
            : OperatingSystem.IsMacOS()
                ? new[] { "Contents/MacOS/ida", "Contents/MacOS/ida64" }
                : new[] { "ida", "ida64" };
        return candidates
            .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(File.Exists);
    }

    private static Version? InferIdaVersion(string root, string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            if (TryParseVersion(productVersion, out var fileVersion))
            {
                return fileVersion;
            }
        }

        return TryParseVersion(Path.GetFileName(root), out var directoryVersion) ? directoryVersion : null;
    }

    private static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = VersionPattern().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
        {
            return false;
        }

        var minor = int.TryParse(match.Groups[2].Value, out var parsedMinor) ? parsedMinor : 0;
        version = new Version(major, minor);
        return true;
    }

    private static bool HasIdaPython(string root)
    {
        var pluginDirectories = OperatingSystem.IsMacOS()
            ? new[] { Path.Combine(root, "Contents", "MacOS", "plugins"), Path.Combine(root, "plugins") }
            : new[] { Path.Combine(root, "plugins") };
        return pluginDirectories.Any(directory =>
            Directory.Exists(directory) &&
            EnumerateFiles(directory, "*idapython*").Length > 0);
    }

    private static string GetUserPluginDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Hex-Rays",
                "IDA Pro",
                "plugins");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Hex-Rays",
                "IDA Pro",
                "plugins");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".idapro", "plugins");
    }

    private static async Task<IReadOnlyList<PythonInstallation>> FindPythonInstallationsAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(PathComparer());
        var configured = Environment.GetEnvironmentVariable("DECOMPILE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Python");
            foreach (var directory in EnumerateDirectories(programs, "Python*"))
            {
                candidates.Add(Path.Combine(directory, "python.exe"));
            }
        }
        else
        {
            foreach (var path in new[]
                     {
                         "/usr/bin/python3",
                         "/usr/local/bin/python3",
                         "/opt/homebrew/bin/python3",
                     })
            {
                candidates.Add(path);
            }
        }

        var pathExecutable = FindOnPath(OperatingSystem.IsWindows() ? "python.exe" : "python3");
        if (pathExecutable is not null)
        {
            candidates.Add(pathExecutable);
        }

        var installations = new List<PythonInstallation>();
        foreach (var candidate in candidates.Where(File.Exists))
        {
            var version = await ReadPythonVersionAsync(candidate, cancellationToken);
            if (version is not null && version.Major == 3)
            {
                installations.Add(new PythonInstallation(candidate, version));
            }
        }

        return installations;
    }

    private static PythonInstallation? SelectPython(
        IReadOnlyList<PythonInstallation> candidates,
        Version? idaVersion)
    {
        var compatible = candidates.Where(candidate => candidate.Version >= new Version(3, 10));
        if (idaVersion?.Major == 8)
        {
            compatible = compatible.Where(candidate => candidate.Version < new Version(3, 12));
        }

        return compatible.OrderByDescending(candidate => candidate.Version).FirstOrDefault();
    }

    private static async Task<Version?> ReadPythonVersionAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask) + " " + (await errorTask);
            return TryParseVersion(output, out var version) ? version : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> ExistingDirectories(params string[] paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    private static string[] EnumerateDirectories(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string[] EnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"(?<!\d)(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    private sealed record PythonInstallation(string Path, Version Version);
}
