using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GodjiVpn.ViewModels;

namespace GodjiVpn.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Globe.SetStatusAsync("off");
    }

    private void OnBotBannerClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.OpenBotCommand.Execute(null);
    }

    private void OnTermsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.OpenTermsCommand.Execute(null);
    }

    private void OnPrivacyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) vm.OpenPrivacyCommand.Execute(null);
    }
}
