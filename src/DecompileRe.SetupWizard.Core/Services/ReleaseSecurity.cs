using System.IO.Compression;
using System.Security.Cryptography;

namespace DecompileRe.SetupWizard.Core.Services;

public static class ReleaseSecurity
{
    public static void VerifyManifestSignature(
        ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> signaturePayload,
        string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);

        var signature = DecodeSignature(signaturePayload);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);

        if (!key.VerifyData(
                manifest,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new CryptographicException("The release manifest signature is invalid.");
        }
    }

    public static async Task VerifyFileHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateSha256(expectedSha256);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The downloaded release asset failed SHA-256 verification.");
        }
    }

    public static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("A release asset contains an invalid SHA-256 digest.");
        }
    }

    private static byte[] DecodeSignature(ReadOnlySpan<byte> payload)
    {
        var text = System.Text.Encoding.ASCII.GetString(payload).Trim();
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return payload.ToArray();
        }
    }
}

public sealed class SafeZipExtractor
{
    private readonly int _maximumEntries;

    public SafeZipExtractor(int maximumEntries = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        _maximumEntries = maximumEntries;
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        long maximumExpandedBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = EnsureTrailingSeparator(Path.GetFullPath(destinationDirectory));

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > _maximumEntries)
        {
            throw new InvalidDataException("The release archive contains too many entries.");
        }

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(entry);
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > maximumExpandedBytes)
            {
                throw new InvalidDataException("The release archive expands beyond the permitted size.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!targetPath.StartsWith(destinationRoot, StringComparisonForCurrentPlatform()))
            {
                throw new InvalidDataException("The release archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 128 * 1024, cancellationToken);
        }
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixMode == unixSymbolicLink)
        {
            throw new InvalidDataException("The release archive contains a symbolic link.");
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static StringComparison StringComparisonForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
