using CommunityToolkit.Mvvm.ComponentModel;

namespace LinuxRemoteWorker.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    protected void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        HasError = isError;
    }

    /// <summary>Yes/No question before a destructive action.</summary>
    protected static bool Confirm(string message, string caption) =>
        System.Windows.MessageBox.Show(message, caption, System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    protected async Task RunSafeAsync(Func<Task> action, string? busyMessage = null)
    {
        IsBusy = true;
        HasError = false;
        if (busyMessage != null) StatusMessage = busyMessage;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Core.AppLog.Error($"{GetType().Name} action failed", ex);
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
