using System.Windows.Controls;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Views;

public partial class ConnectView : UserControl
{
    public ConnectView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectViewModel vm)
                PassBox.PasswordChanged += (_, _) => vm.Passphrase = PassBox.Password;
        };
    }
}
