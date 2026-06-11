using System.ComponentModel;
using System.Windows;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Views.Dialogs;

public partial class AuthorizationDeniedDialog : Window, INotifyPropertyChanged
{
    private readonly AuthClient _authClient;
    private bool _isRefreshing = true;

    public string Message { get; }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRefreshing))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AuthorizationDeniedDialog(string message, AuthClient authClient)
    {
        _authClient = authClient;
        Message = message;
        DataContext = this;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Fire background refresh — cascades through CurrentUserHolder → CapabilityChecker
        try
        {
            await _authClient.FetchMeAsync();
        }
        catch
        {
            // Best-effort
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
