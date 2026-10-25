using System.Windows;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        vm.NavigateToCommand.Execute(vm.ConnectVM);
    }
}
