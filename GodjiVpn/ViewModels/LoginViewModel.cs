using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Services;

namespace GodjiVpn.ViewModels;

/// <summary>Аналог LoginViewModel.kt/AuthRepository.kt (Android) — email+OTP и OAuth
/// (Google/Yandex) через тот же redirect URI, что и мобильное приложение. Telegram OAuth
/// оставлена выключенной кнопкой ("СКОРО") — на бэкенде она сломана (invalid_client при
/// обмене токена), это не недоработка порта, так же было решено и в Android-версии.</summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private const string OAuthRedirectUri = "godjivpn://oauth2redirect";
    private const string BotUrl = "https://t.me/Shadow_Duck_bot";
    // Домен/бренд ShadowDuck — легаси-название бэкенда (см. память project-remnawave-backend-
    // architecture), не Godji; ссылки скопированы как есть из рабочего Android-кода.
    private const string TermsUrl = "https://telegra.ph/Polzovatelskoe-soglashenie-servisa-ShadowDuck-10-10";
    private const string PrivacyUrl = "https://telegra.ph/Politika-konfidencialnosti-servisa-ShadowDuck-10-10";

    private readonly ApiClient _api;
    private readonly TokenStore _tokenStore;

    private string? _pendingVerifier;
    private string? _pendingProvider;

    public event Action? LoggedIn;

    [ObservableProperty] private bool emailMode;
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string code = "";
    [ObservableProperty] private bool otpSent;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? infoMessage;

    public LoginViewModel(ApiClient api, TokenStore tokenStore)
    {
        _api = api;
        _tokenStore = tokenStore;
    }

    [RelayCommand]
    private void ShowEmailForm() => EmailMode = true;

    [RelayCommand]
    private void ShowOAuthOptions()
    {
        EmailMode = false;
        OtpSent = false;
        ErrorMessage = null;
        InfoMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSendOtp))]
    private async Task SendOtpAsync()
    {
        ErrorMessage = null;
        InfoMessage = null;
        IsBusy = true;
        try
        {
            await _api.SendOtpAsync(Email.Trim());
            OtpSent = true;
            InfoMessage = $"Код отправлен на {Email.Trim()}";
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось отправить код: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSendOtp() => !IsBusy && Email.Contains('@');

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var (_, token) = await _api.VerifyOtpAsync(Email.Trim(), Code.Trim());
            await OnAuthenticatedAsync(token);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Неверный код: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanVerify() => !IsBusy && OtpSent && Code.Trim().Length > 0;

    /// <summary>Вызывается при разлогине — экземпляр ViewModel переживает выход, см.
    /// MainViewModel.OnLoggedOut().</summary>
    public void Reset()
    {
        EmailMode = false;
        Email = "";
        Code = "";
        OtpSent = false;
        IsBusy = false;
        ErrorMessage = null;
        InfoMessage = null;
        _pendingVerifier = null;
        _pendingProvider = null;
    }

    [RelayCommand]
    private void ChangeEmail()
    {
        OtpSent = false;
        Code = "";
        ErrorMessage = null;
        InfoMessage = null;
    }

    /// <summary>Шаг 1: открывает системный браузер на OAuth-странице провайдера. Шаг 2
    /// (обмен кода на токен) происходит в CompleteOAuthAsync — вызывается из App.xaml.cs,
    /// когда Windows перезапускает нас по godjivpn://oauth2redirect (см. SingleInstanceService/
    /// OAuthProtocolRegistrar).</summary>
    private async Task StartOAuthAsync(string provider)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var verifier = Pkce.GenerateVerifier();
            var challenge = Pkce.ChallengeFor(verifier);
            var response = await _api.StartOAuthAsync(provider, OAuthRedirectUri, challenge);
            _pendingVerifier = verifier;
            _pendingProvider = provider;
            Process.Start(new ProcessStartInfo(response.AuthUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось начать вход: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task StartGoogleOAuth() => StartOAuthAsync("google");

    [RelayCommand]
    private Task StartYandexOAuth() => StartOAuthAsync("yandex");

    /// <summary>Вызывается после возврата из браузера по godjivpn://oauth2redirect?code=...</summary>
    public async Task CompleteOAuthAsync(string? provider, string? code)
    {
        var verifier = _pendingVerifier;
        provider ??= _pendingProvider;
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(verifier))
        {
            ErrorMessage = "Не удалось завершить вход — попробуйте ещё раз";
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var response = await _api.ExchangeNativeOAuthAsync(code, verifier, provider);
            _pendingVerifier = null;
            _pendingProvider = null;
            await OnAuthenticatedAsync(response.AccessToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось завершить вход: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OnAuthenticatedAsync(string token)
    {
        _tokenStore.Save(token);
        // Согласия на обработку данных — требуются один раз после первой регистрации. Не
        // блокируем вход, если запрос не удался — согласие можно принять позже.
        try
        {
            var me = await _api.GetMeAsync();
            if (me.ConsentRequired) await _api.ConsentAsync();
        }
        catch { /* не критично для входа */ }

        LoggedIn?.Invoke();
    }

    [RelayCommand]
    private void OpenBot() => OpenUrl(BotUrl);

    [RelayCommand]
    private void OpenTerms() => OpenUrl(TermsUrl);

    [RelayCommand]
    private void OpenPrivacy() => OpenUrl(PrivacyUrl);

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    partial void OnEmailChanged(string value) => SendOtpCommand.NotifyCanExecuteChanged();
    partial void OnCodeChanged(string value) => VerifyCommand.NotifyCanExecuteChanged();
    partial void OnOtpSentChanged(bool value) => VerifyCommand.NotifyCanExecuteChanged();
    /// <summary>Как loginSendingOtp/loginGetCode в Android Strings.kt — во время отправки
    /// кода кнопка показывает "Отправляем…" вместо "Получить код".</summary>
    public string SendOtpButtonText => IsBusy ? "Отправляем…" : "Получить код";

    partial void OnIsBusyChanged(bool value)
    {
        SendOtpCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendOtpButtonText));
    }
}
