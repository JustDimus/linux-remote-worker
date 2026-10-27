using System.Windows.Controls;

namespace LinuxRemoteWorker.Modules.Security;

public partial class SecurityView : UserControl
{
    public SecurityView()
    {
        InitializeComponent();
        // PasswordBox cannot be bound, so the passphrase is pushed to the view-model by hand
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SecurityViewModel vm)
                MyKeyPassBox.PasswordChanged += (_, _) => vm.MyKeyPassphrase = MyKeyPassBox.Password;
        };
    }
}
