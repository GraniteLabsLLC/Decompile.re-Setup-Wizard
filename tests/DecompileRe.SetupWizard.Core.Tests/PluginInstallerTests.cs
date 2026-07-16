using DecompileRe.SetupWizard.Core.Services;
using Xunit;

namespace DecompileRe.SetupWizard.Core.Tests;

public sealed class PluginInstallerTests
{
    [Fact]
    public void FailedStagingDoesNotRemoveExistingPlugin()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var payload = Path.Combine(root, "incomplete-payload");
            var plugins = Path.Combine(root, "plugins");
            Directory.CreateDirectory(payload);
            CreatePlugin(plugins, "old");

            Assert.Throws<DirectoryNotFoundException>(() =>
                PluginInstaller.InstallPluginAtomically(payload, plugins, "2.0.0"));

            Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "ida_ai_client.py")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "ida_ai_client", "module.py")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulActivationBacksUpExistingPlugin()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var payload = Path.Combine(root, "payload");
            var plugins = Path.Combine(root, "plugins");
            CreatePlugin(payload, "new");
            CreatePlugin(plugins, "old");

            var backup = PluginInstaller.InstallPluginAtomically(payload, plugins, "2.0.0");

            Assert.NotNull(backup);
            Assert.Equal("new", File.ReadAllText(Path.Combine(plugins, "ida_ai_client.py")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(plugins, "ida_ai_client", "module.py")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "ida_ai_client.py")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "ida_ai_client", "module.py")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreatePlugin(string root, string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "ida_ai_client"));
        File.WriteAllText(Path.Combine(root, "ida_ai_client.py"), content);
        File.WriteAllText(Path.Combine(root, "ida_ai_client", "module.py"), content);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"decompile-re-installer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
