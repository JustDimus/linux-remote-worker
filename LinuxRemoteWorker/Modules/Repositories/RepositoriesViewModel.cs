using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Repositories;

public partial class RepositoriesViewModel : BaseViewModel, IModule
{
    public string Title => "Repositories";
    public string Icon => "📁";

    private readonly RepositoryService _repos;

    // Tooling state
    [ObservableProperty] private bool _gitInstalled;
    [ObservableProperty] private string _gitVersion = string.Empty;

    // Deploy key
    [ObservableProperty] private bool _hasDeployKey;
    [ObservableProperty] private string _deployPublicKey = string.Empty;

    // Clone form
    [ObservableProperty] private string _cloneUrl = string.Empty;
    [ObservableProperty] private string _cloneName = string.Empty;
    public GitSourcePickerViewModel ClonePicker { get; } = new(allowDefaultBranch: true);

    // Switch panel: the clone being moved to another version, null while closed
    [ObservableProperty] private RepoInfo? _switchRepo;
    public GitSourcePickerViewModel SwitchPicker { get; } = new(allowDefaultBranch: false);

    // Streaming log (install / clone / update)
    [ObservableProperty] private string _outputLog = string.Empty;
    [ObservableProperty] private bool _isBusyRepo;

    public ObservableCollection<RepoInfo> Repos { get; } = [];

    public RepositoriesViewModel(SshService ssh)
    {
        _repos = new RepositoryService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            var status = await _repos.GetStatusAsync();
            GitInstalled = status.GitVersion != null;
            GitVersion = status.GitVersion ?? string.Empty;
            HasDeployKey = status.DeployPublicKey != null;
            DeployPublicKey = status.DeployPublicKey ?? string.Empty;
            ShowRepos(status.Repos);
        });
        IsBusyRepo = false;
    }

    private async Task LoadReposAsync() => ShowRepos(await _repos.ListAsync());

    private void ShowRepos(IEnumerable<RepoInfo> repos)
    {
        Repos.Clear();
        foreach (var repo in repos) Repos.Add(repo);
    }

    [RelayCommand]
    private async Task InstallGitAsync()
    {
        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            Append("→ Installing git...");
            await _repos.InstallGitAsync(Append);
            Append("\n✓ Done");
            await RefreshAsync();
        });
        IsBusyRepo = false;
    }

    [RelayCommand]
    private async Task GenerateDeployKeyAsync()
    {
        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            DeployPublicKey = await _repos.GenerateDeployKeyAsync();
            HasDeployKey = true;
            SetStatus("Deploy key ready — add it to your repo's Deploy Keys.");
        });
        IsBusyRepo = false;
    }

    [RelayCommand]
    private void CopyDeployKey()
    {
        if (!string.IsNullOrEmpty(DeployPublicKey))
        {
            System.Windows.Clipboard.SetText(DeployPublicKey);
            SetStatus("Public key copied to clipboard!");
        }
    }

    // ── Clone ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadCloneRefsAsync()
    {
        var url = CloneUrl.Trim();
        if (RepositoryService.ValidateUrl(url) is { } error)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            ClonePicker.Load(await _repos.ListRefsAsync(url));
            SetStatus(ClonePicker.RefsSummary);
        }, $"Reading branches and tags of {url}...");
        IsBusyRepo = false;
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        var url = CloneUrl.Trim();
        // Derive folder name if not provided: strip .git and path
        var name = string.IsNullOrWhiteSpace(CloneName) ? RepositoryService.DefaultFolderName(url) : CloneName.Trim();

        GitSource? source;
        try
        {
            source = ClonePicker.Build();
        }
        catch (OperationException ex)
        {
            SetStatus(ex.Message, isError: true);
            return;
        }

        if (RepositoryService.ValidateClone(url, name, source) is { } error)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var change = await _repos.CloneAsync(url, source, name, Append);

            Append($"\n✓ {change.Summary}");
            CloneUrl = string.Empty;
            CloneName = string.Empty;
            ClonePicker.Reset();
            await LoadReposAsync();
            SetStatus(change.Summary);
        });
        IsBusyRepo = false;
    }

    // ── Update / switch ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task UpdateAsync(RepoInfo repo)
    {
        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var change = await ThenReloadAsync(
                () => WithForceRetryAsync(force => _repos.UpdateAsync(repo.Name, force, Append), repo.Name),
                LoadReposAsync);
            Append($"\n✓ {change.Summary}");
            SetStatus(change.Summary + (change.Changed ? " - redeploy the services built from it to run this version." : ""));
        });
        IsBusyRepo = false;
    }

    [RelayCommand]
    private async Task OpenSwitchAsync(RepoInfo repo)
    {
        SwitchRepo = repo;
        SwitchPicker.Reset();
        SwitchPicker.Preset(repo.Source);

        IsBusyRepo = true;
        await RunSafeAsync(async () => SwitchPicker.Load(await _repos.ListRefsAsync(repo.Name)),
            $"Reading branches and tags of {repo.Name}...");
        IsBusyRepo = false;
    }

    [RelayCommand]
    private void CancelSwitch() => SwitchRepo = null;

    [RelayCommand]
    private async Task ApplySwitchAsync()
    {
        if (SwitchRepo is not { } repo) return;

        GitSource source;
        try
        {
            source = SwitchPicker.Build()!;
        }
        catch (OperationException ex)
        {
            SetStatus(ex.Message, isError: true);
            return;
        }

        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";
            var change = await ThenReloadAsync(
                () => WithForceRetryAsync(force => _repos.SwitchAsync(repo.Name, source, force, Append), repo.Name),
                LoadReposAsync);
            Append($"\n✓ {change.Summary}");
            SwitchRepo = null;
            SetStatus(change.Summary + " - redeploy the services built from it to run this version.");
        });
        IsBusyRepo = false;
    }

    [RelayCommand]
    private async Task DeleteRepoAsync(RepoInfo repo)
    {
        if (!Confirm($"Delete the clone {DeployPaths.RepoDir(repo.Name)}?\n\nUncommitted changes on the server are lost.",
                "Delete repository"))
            return;

        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            await _repos.DeleteAsync(repo.Name);
            if (SwitchRepo?.Name == repo.Name) SwitchRepo = null;
            await LoadReposAsync();
            SetStatus($"Removed {repo.Name}");
        });
        IsBusyRepo = false;
    }
}
