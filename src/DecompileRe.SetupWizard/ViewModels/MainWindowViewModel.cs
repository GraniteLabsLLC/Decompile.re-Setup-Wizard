using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DecompileRe.SetupWizard.Core.Models;
using DecompileRe.SetupWizard.Core.Platform;
using DecompileRe.SetupWizard.Core.Services;

namespace DecompileRe.SetupWizard.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IIdaDiscoveryService _discovery;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly PluginInstaller _installer;
    private readonly CancellationTokenSource _lifetime = new();
    private WizardPage _page = WizardPage.Welcome;
    private IdaInstallation? _selectedInstallation;
    private VerifiedRelease? _release;
    private bool _isBusy;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private double _progressPercent;
    private bool _isProgressIndeterminate = true;
    private string _installedLocation = string.Empty;
    private bool _disposed;

    public MainWindowViewModel(
        IIdaDiscoveryService discovery,
        GitHubReleaseClient releaseClient,
        PluginInstaller installer)
    {
        _discovery = discovery;
        _releaseClient = releaseClient;
        _installer = installer;
        PrimaryCommand = new AsyncCommand(ExecutePrimaryAsync, () => CanUsePrimary);
        BackCommand = new DelegateCommand(GoBack, () => ShowBack && !_isBusy);
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<IdaInstallation> Installations { get; } = [];

    public ObservableCollection<string> ActivityLog { get; } = [];

    public ICommand PrimaryCommand { get; }

    public ICommand BackCommand { get; }

    public IdaInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (SetField(ref _selectedInstallation, value))
            {
                NotifyNavigation();
            }
        }
    }

    public bool IsWelcome => _page == WizardPage.Welcome;

    public bool IsDetection => _page == WizardPage.Detection;

    public bool IsReview => _page == WizardPage.Review;

    public bool IsInstalling => _page == WizardPage.Installing;

    public bool IsComplete => _page == WizardPage.Complete;

    public string Step1Background => StepBackground(WizardPage.Welcome);

    public string Step2Background => StepBackground(WizardPage.Detection);

    public string Step3Background => StepBackground(WizardPage.Review);

    public string Step4Background => _page is WizardPage.Installing or WizardPage.Complete ? "#E8F0FF" : "#2A2F36";

    public string Step1Foreground => StepForeground(WizardPage.Welcome);

    public string Step2Foreground => StepForeground(WizardPage.Detection);

    public string Step3Foreground => StepForeground(WizardPage.Review);

    public string Step4Foreground => _page is WizardPage.Installing or WizardPage.Complete ? "#11151B" : "#E8EAED";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                NotifyNavigation();
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetField(ref _isProgressIndeterminate, value);
    }

    public string ReleaseVersion => _release?.Manifest.Version ?? "Checking latest release...";

    public string InstalledLocation
    {
        get => _installedLocation;
        private set => SetField(ref _installedLocation, value);
    }

    public bool ShowBack => _page is WizardPage.Detection or WizardPage.Review;

    public bool CanUsePrimary => !IsBusy && (_page != WizardPage.Detection || SelectedInstallation is not null);

    public string PrimaryButtonText => _page switch
    {
        WizardPage.Welcome => "Continue",
        WizardPage.Detection => "Review",
        WizardPage.Review => "Install",
        WizardPage.Installing => "Installing...",
        WizardPage.Complete => "Close",
        _ => "Continue",
    };

    public async Task AddManualInstallationAsync(string path)
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var installation = await _discovery.InspectInstallationAsync(path, _lifetime.Token);
            if (installation is null)
            {
                ErrorMessage = "The selected folder does not contain a supported IDA executable.";
                return;
            }

            var existing = Installations.FirstOrDefault(item =>
                item.RootPath.Equals(installation.RootPath, PathComparison()));
            if (existing is null)
            {
                Installations.Insert(0, installation);
                SelectedInstallation = installation;
            }
            else
            {
                SelectedInstallation = existing;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "The selected IDA folder could not be inspected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _disposed = true;
    }

    private async Task ExecutePrimaryAsync()
    {
        ErrorMessage = string.Empty;
        switch (_page)
        {
            case WizardPage.Welcome:
                SetPage(WizardPage.Detection);
                await DetectInstallationsAsync();
                break;
            case WizardPage.Detection:
                await LoadReleaseAsync();
                break;
            case WizardPage.Review:
                await InstallAsync();
                break;
            case WizardPage.Complete:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async Task DetectInstallationsAsync()
    {
        IsBusy = true;
        StatusMessage = "Looking for IDA Pro installations...";
        try
        {
            var installations = await _discovery.FindInstallationsAsync(_lifetime.Token);
            Installations.Clear();
            foreach (var installation in installations)
            {
                Installations.Add(installation);
            }

            SelectedInstallation = Installations.FirstOrDefault();
            if (Installations.Count == 0)
            {
                ErrorMessage = "No IDA installation was detected. Choose its folder manually.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "IDA installations could not be scanned. Choose the installation folder manually.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadReleaseAsync()
    {
        if (SelectedInstallation is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Verifying the latest release...";
        try
        {
            _release = await _releaseClient.GetLatestVerifiedReleaseAsync(_lifetime.Token);
            OnPropertyChanged(nameof(ReleaseVersion));
            SetPage(WizardPage.Review);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = $"The latest release could not be verified: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync()
    {
        if (SelectedInstallation is null || _release is null)
        {
            return;
        }

        SetPage(WizardPage.Installing);
        IsBusy = true;
        ActivityLog.Clear();
        var progress = new Progress<InstallProgress>(update =>
        {
            StatusMessage = update.Message;
            IsProgressIndeterminate = update.Fraction is null;
            if (update.Fraction is { } fraction)
            {
                ProgressPercent = Math.Clamp(fraction * 100, 0, 100);
            }

            if (ActivityLog.Count == 0 || ActivityLog[^1] != update.Message)
            {
                ActivityLog.Add(update.Message);
            }
        });

        try
        {
            var result = await _installer.InstallAsync(
                new InstallRequest(SelectedInstallation, _release),
                progress,
                _lifetime.Token);
            InstalledLocation = result.PluginDirectory;
            SetPage(WizardPage.Complete);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = exception.Message;
            SetPage(WizardPage.Review);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void GoBack()
    {
        ErrorMessage = string.Empty;
        if (_page == WizardPage.Review)
        {
            SetPage(WizardPage.Detection);
        }
        else if (_page == WizardPage.Detection)
        {
            SetPage(WizardPage.Welcome);
        }
    }

    private void SetPage(WizardPage page)
    {
        _page = page;
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsDetection));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(Step1Background));
        OnPropertyChanged(nameof(Step2Background));
        OnPropertyChanged(nameof(Step3Background));
        OnPropertyChanged(nameof(Step4Background));
        OnPropertyChanged(nameof(Step1Foreground));
        OnPropertyChanged(nameof(Step2Foreground));
        OnPropertyChanged(nameof(Step3Foreground));
        OnPropertyChanged(nameof(Step4Foreground));
        NotifyNavigation();
    }

    private string StepBackground(WizardPage page) => _page == page ? "#E8F0FF" : "#2A2F36";

    private string StepForeground(WizardPage page) => _page == page ? "#11151B" : "#E8EAED";

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(ShowBack));
        OnPropertyChanged(nameof(CanUsePrimary));
        OnPropertyChanged(nameof(PrimaryButtonText));
        (PrimaryCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (BackCommand as DelegateCommand)?.NotifyCanExecuteChanged();
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private enum WizardPage
    {
        Welcome,
        Detection,
        Review,
        Installing,
        Complete,
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _executing;

    public AsyncCommand(Func<Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_executing && _canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _executing = true;
        NotifyCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _executing = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public DelegateCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter) => _execute();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
