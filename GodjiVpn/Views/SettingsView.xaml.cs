using System.Windows.Controls;
using System.Windows.Input;
using GodjiVpn.ViewModels;

namespace GodjiVpn.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnSupportClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.OpenSupportCommand.Execute(null);
    }
}
