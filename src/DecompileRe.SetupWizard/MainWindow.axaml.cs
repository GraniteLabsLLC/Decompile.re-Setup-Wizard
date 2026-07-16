using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DecompileRe.SetupWizard.ViewModels;

namespace DecompileRe.SetupWizard;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void BrowseIda_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the IDA Pro installation folder",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].Path.IsFile)
        {
            await viewModel.AddManualInstallationAsync(folders[0].Path.LocalPath);
        }
    }
}
