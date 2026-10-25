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

    /// <summary>
    /// Runs <paramref name="action"/>, then <paramref name="reload"/> whether it worked or not, so the screen
    /// shows the server's real state. When the action failed, its error is the one reported - a reload that
    /// fails as well (often for the same reason) must not replace it.
    /// </summary>
    protected static async Task<T> ThenReloadAsync<T>(Func<Task<T>> action, Func<Task> reload)
    {
        T result;
        try
        {
            result = await action();
        }
        catch
        {
            try { await reload(); }
            catch (Exception ex) { Core.AppLog.Warn($"Reload after a failed action failed too: {ex.Message}"); }
            throw;
        }
        await reload();
        return result;
    }

    protected static Task ThenReloadAsync(Func<Task> action, Func<Task> reload) =>
        ThenReloadAsync(async () => { await action(); return true; }, reload);

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
