using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;
using Microsoft.Win32;

namespace LinuxRemoteWorker.Modules.AppLogs;

/// <summary>
/// Viewer for this application's own log file, so a failing connection can be
/// diagnosed without hunting for files on disk.
/// </summary>
public partial class AppLogViewModel : BaseViewModel
{
    private const int TailLines = 2000;

    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string? _selectedFile;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _isFollowing = true;

    public ObservableCollection<string> Files { get; } = [];

    public string LogDirectory => AppLog.LogDirectory;

    /// <summary>True while viewing today's file, where new lines can still arrive.</summary>
    private bool ViewingCurrentFile =>
        string.Equals(SelectedFile, Path.GetFileName(AppLog.CurrentFile), StringComparison.OrdinalIgnoreCase);

    public AppLogViewModel()
    {
        AppLog.EntryWritten += OnEntryWritten;
        Reload();
    }

    /// <summary>Re-reads the file list and the selected file. Called when the view is opened.</summary>
    public void Reload()
    {
        var previous = SelectedFile;

        Files.Clear();
        foreach (var f in AppLog.ListFiles())
            Files.Add(f);

        var current = Path.GetFileName(AppLog.CurrentFile);
        if (!Files.Contains(current)) Files.Insert(0, current);

        SelectedFile = previous != null && Files.Contains(previous) ? previous : current;
        LoadSelected();
    }

    partial void OnSelectedFileChanged(string? value) => LoadSelected();

    partial void OnFilterChanged(string value) => LoadSelected();

    private void LoadSelected()
    {
        if (string.IsNullOrEmpty(SelectedFile))
        {
            Content = string.Empty;
            return;
        }

        var text = AppLog.ReadTail(SelectedFile, TailLines);
        Content = ApplyFilter(text);
        SetStatus($"{AppLog.PathOf(SelectedFile)}");
    }

    private string ApplyFilter(string text)
    {
        if (string.IsNullOrWhiteSpace(Filter)) return text;

        var matches = text
            .Split('\n')
            .Where(l => l.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 0
            ? $"(no lines matching \"{Filter}\")"
            : string.Join(Environment.NewLine, matches).TrimEnd();
    }

    private void OnEntryWritten(string line)
    {
        if (!IsFollowing || !ViewingCurrentFile) return;
        if (!string.IsNullOrWhiteSpace(Filter) &&
            !line.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;

        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.BeginInvoke(() =>
        {
            Content = Content.Length == 0 ? line : Content + Environment.NewLine + line;
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        SetStatus("Reloaded");
    }

    [RelayCommand]
    private void OpenFile() => AppLog.OpenFile(SelectedFile);

    [RelayCommand]
    private void OpenFolder() => AppLog.OpenFolder();

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedFile == null) return;
        TryClipboard(AppLog.PathOf(SelectedFile), "Path copied to clipboard");
    }

    [RelayCommand]
    private void CopyContent() => TryClipboard(Content, "Log copied to clipboard");

    [RelayCommand]
    private void SaveAs()
    {
        if (SelectedFile == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save application log",
            FileName = SelectedFile,
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, Content);
            SetStatus($"Saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving log copy failed", ex);
            SetStatus(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        var answer = MessageBox.Show(
            $"Delete the contents of {SelectedFile}?",
            "Clear log", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes || SelectedFile == null) return;

        try
        {
            File.WriteAllText(AppLog.PathOf(SelectedFile), string.Empty);
            AppLog.Info("Log cleared from the in-app viewer");
            Reload();
        }
        catch (Exception ex)
        {
            AppLog.Error("Clearing the log failed", ex);
            SetStatus(ex.Message, isError: true);
        }
    }

    private void TryClipboard(string text, string okMessage)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(okMessage);
        }
        catch (Exception ex)
        {
            AppLog.Error("Clipboard copy failed", ex);
            SetStatus(ex.Message, isError: true);
        }
    }
}
