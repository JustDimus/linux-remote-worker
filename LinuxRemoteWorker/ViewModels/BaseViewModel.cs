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
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
