using CommunityToolkit.Mvvm.ComponentModel;
using GodjiVpn.Services;

namespace GodjiVpn.ViewModels;

/// <summary>Переключает содержимое главного окна между LoginViewModel и ShellViewModel —
/// какая View показывается, решают DataTemplate'ы в MainWindow.xaml по типу CurrentViewModel.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly TokenStore _tokenStore;
    private readonly LoginViewModel _login;
    private readonly ShellViewModel _shell;

    [ObservableProperty] private object currentViewModel;

    public MainViewModel(TokenStore tokenStore, LoginViewModel login, ShellViewModel shell)
    {
        _tokenStore = tokenStore;
        _login = login;
        _shell = shell;

        _login.LoggedIn += OnLoggedIn;
        _shell.LoggedOut += OnLoggedOut;

        currentViewModel = _tokenStore.IsLoggedIn ? _shell : _login;
    }

    public async Task InitializeAsync()
    {
        if (CurrentViewModel == _shell) await _shell.LoadAsync();
    }

    private async void OnLoggedIn()
    {
        CurrentViewModel = _shell;
        await _shell.LoadAsync();
    }

    private void OnLoggedOut()
    {
        // LoginViewModel — один и тот же экземпляр на всё время жизни процесса (не
        // пересоздаётся при выходе), поэтому если пользователь на прошлом заходе успел
        // переключиться на email-форму (EmailMode=true), после разлогина экран молча
        // показывал бы её вместо стартовых кнопок OAuth — сбрасываем состояние явно.
        _login.Reset();
        CurrentViewModel = _login;
    }
}
