using System.Windows.Controls;
using System.Windows.Input;
using GodjiVpn.ViewModels;

namespace GodjiVpn.Views;

public partial class PlansView : UserControl
{
    public PlansView()
    {
        InitializeComponent();
    }

    private void OnSupportClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlansViewModel vm) vm.OpenSupportCommand.Execute(null);
    }
}
