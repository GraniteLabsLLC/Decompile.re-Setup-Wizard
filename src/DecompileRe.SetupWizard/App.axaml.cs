using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DecompileRe.SetupWizard.Core.Platform;
using DecompileRe.SetupWizard.Core.Services;
using DecompileRe.SetupWizard.ViewModels;

namespace DecompileRe.SetupWizard;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = new InstallerOptions
            {
                SigningPublicKeyPem = ReadSigningPublicKey(),
            };
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 4,
            };
            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(15),
            };
            var releaseClient = new GitHubReleaseClient(httpClient, options);
            var installer = new PluginInstaller(releaseClient, options, new SafeZipExtractor());
            var viewModel = new MainWindowViewModel(
                new IdaDiscoveryService(),
                releaseClient,
                installer);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            viewModel.CloseRequested += (_, _) => window.Close();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string ReadSigningPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "DecompileRe.SetupWizard.release-signing-public-key.pem")
            ?? throw new InvalidOperationException("The release signing public key is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
