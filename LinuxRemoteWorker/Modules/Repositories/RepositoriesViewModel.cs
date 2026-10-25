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
    [ObservableProperty] private string _cloneBranch = string.Empty;
    [ObservableProperty] private string _cloneName = string.Empty;

    // Streaming log (install / clone)
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

    [RelayCommand]
    private async Task CloneAsync()
    {
        var url = CloneUrl.Trim();
        var branch = CloneBranch.Trim();
        // Derive folder name if not provided: strip .git and path
        var name = string.IsNullOrWhiteSpace(CloneName) ? RepositoryService.DefaultFolderName(url) : CloneName.Trim();

        var error = RepositoryService.ValidateClone(url, branch, name);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            void Append(string l) => OutputLog += l + "\n";

            var dir = await _repos.CloneAsync(url, branch, name, Append);

            Append("\n✓ Done");
            CloneUrl = string.Empty;
            CloneBranch = string.Empty;
            CloneName = string.Empty;
            await LoadReposAsync();
            SetStatus($"Cloned into {dir}");
        });
        IsBusyRepo = false;
    }

    [RelayCommand]
    private async Task PullAsync(RepoInfo repo)
    {
        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            var result = await ThenReloadAsync(() => _repos.PullAsync(repo.Name), LoadReposAsync);
            SetStatus($"Pull: {result}");
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
            await LoadReposAsync();
            SetStatus($"Removed {repo.Name}");
        });
        IsBusyRepo = false;
    }
}
