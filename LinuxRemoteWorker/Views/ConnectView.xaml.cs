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
            if (DataContext is not ConnectViewModel vm) return;
            // PasswordBox cannot be bound, so the secrets are pushed to the view-model by hand
            PassBox.PasswordChanged += (_, _) => vm.Passphrase = PassBox.Password;
            LoginPassBox.PasswordChanged += (_, _) => vm.Password = LoginPassBox.Password;
            vm.ClearSecrets += () =>
            {
                PassBox.Clear();
                LoginPassBox.Clear();
            };
        };
    }
}
