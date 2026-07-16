using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DecompileRe.SetupWizard.Core.Services;
using Xunit;

namespace DecompileRe.SetupWizard.Core.Tests;

public sealed class ReleaseSecurityTests
{
    [Fact]
    public void VerifyManifestSignatureAcceptsMatchingKey()
    {
        var manifest = Encoding.UTF8.GetBytes("{\"schema_version\":1}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(
            manifest,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();

        ReleaseSecurity.VerifyManifestSignature(manifest, signature, publicKey);
    }

    [Fact]
    public void VerifyManifestSignatureRejectsModifiedManifest()
    {
        var original = Encoding.UTF8.GetBytes("{\"schema_version\":1}");
        var modified = Encoding.UTF8.GetBytes("{\"schema_version\":2}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(
            original,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();

        Assert.Throws<CryptographicException>(() =>
            ReleaseSecurity.VerifyManifestSignature(modified, signature, publicKey));
    }

    [Fact]
    public async Task SafeZipExtractorRejectsPathTraversal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(root, "malicious.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("unsafe");
            }

            var extractor = new SafeZipExtractor();
            await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(
                archivePath,
                Path.Combine(root, "output"),
                1024,
                CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SafeZipExtractorExtractsRegularFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(root, "release.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("ida_ai_client/module.py");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("value = 1\n");
            }

            var output = Path.Combine(root, "output");
            var extractor = new SafeZipExtractor();
            await extractor.ExtractAsync(archivePath, output, 1024, CancellationToken.None);

            Assert.Equal("value = 1\n", await File.ReadAllTextAsync(
                Path.Combine(output, "ida_ai_client", "module.py")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"decompile-re-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
