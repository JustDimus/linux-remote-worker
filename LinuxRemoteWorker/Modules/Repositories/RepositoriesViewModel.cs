using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.ViewModels;

namespace LinuxRemoteWorker.Modules.Repositories;

public partial class RepositoriesViewModel : BaseViewModel, IModule
{
    public string Title => "Repositories";
    public string Icon => "📁";

    private readonly SshService _ssh;
    private readonly BootstrapService _bootstrap;

    // https://host/owner/repo(.git), ssh://…, or scp-style git@host:owner/repo.git
    private static readonly Regex RepoUrl = new(@"^((https?|ssh|git)://\S+|[\w.-]+@[\w.-]+:[\w./~-]+)$");
    private static readonly Regex FolderName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");
    private static readonly Regex BranchName = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$");

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
        _ssh = ssh;
        _bootstrap = new BootstrapService(ssh);
    }

    public async Task LoadAsync(SshService ssh) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusyRepo = true;
        await RunSafeAsync(async () =>
        {
            await _bootstrap.EnsureAsync();

            var git = await _ssh.RunCommandAsync("git --version 2>/dev/null || echo ''");
            GitInstalled = !string.IsNullOrWhiteSpace(git);
            GitVersion = git.Trim();

            var pub = await _ssh.RunCommandAsync(
                $"[ -f {DeployPaths.GitKeyPub} ] && cat {DeployPaths.GitKeyPub} || echo ''");
            HasDeployKey = !string.IsNullOrWhiteSpace(pub);
            DeployPublicKey = pub.Trim();

            await LoadReposAsync();
        });
        IsBusyRepo = false;
    }

    private async Task LoadReposAsync()
    {
        Repos.Clear();
        var names = await _ssh.RunCommandAsync(
            $"ls -1 {DeployPaths.Repos} 2>/dev/null");
        foreach (var name in names.Split('\n').Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var dir = Shell.Quote(DeployPaths.RepoDir(name.Trim()));
            var remote = await _ssh.RunCommandAsync($"git -C {dir} remote get-url origin 2>/dev/null || echo '-'");
            var branch = await _ssh.RunCommandAsync($"git -C {dir} branch --show-current 2>/dev/null || echo '-'");
            var commit = await _ssh.RunCommandAsync($"git -C {dir} log -1 --format='%h %s' 2>/dev/null || echo '-'");
            Repos.Add(new RepoInfo(name.Trim(), remote.Trim(), branch.Trim(), commit.Trim()));
        }
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
            var r = await _ssh.ExecuteStreamAsync(Shell.AptInstall("git"), Append);
            if (!r.Succeeded)
                throw new InvalidOperationException($"Installing git failed (exit {r.ExitCode}) - see the output.");
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
            await _bootstrap.EnsureAsync();
            // Don't overwrite an existing key
            await _ssh.RunCommandAsync(
                $"[ -f {DeployPaths.GitKey} ] || ssh-keygen -t ed25519 -f {DeployPaths.GitKey} -N '' -C 'lrw-deploy'");
            await _ssh.RunCommandAsync(
                $"chown {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {DeployPaths.GitKey} {DeployPaths.GitKeyPub} && chmod 600 {DeployPaths.GitKey}");

            var pub = await _ssh.RunCommandAsync($"cat {DeployPaths.GitKeyPub}");
            DeployPublicKey = pub.Trim();
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
        var name = string.IsNullOrWhiteSpace(CloneName)
            ? Regex.Replace(url.TrimEnd('/').Split('/', ':').Last(), @"\.git$", "")
            : CloneName.Trim();

        var error = !RepoUrl.IsMatch(url)
            ? "Enter a repository URL such as https://github.com/owner/repo.git or git@github.com:owner/repo.git"
            : branch.Length > 0 && (!BranchName.IsMatch(branch) || branch.Contains(".."))
                ? "That branch name is not valid."
                : !FolderName.IsMatch(name) || name.Contains("..")
                    ? "Folder name: letters, digits, '.', '-' and '_' only."
                    : null;
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        IsBusyRepo = true;
        OutputLog = string.Empty;
        await RunSafeAsync(async () =>
        {
            await _bootstrap.EnsureAsync();

            var dir = DeployPaths.RepoDir(name);
            var branchPart = branch.Length == 0 ? "" : $"--branch {Shell.Quote(branch)} ";

            void Append(string l) => OutputLog += l + "\n";
            Append($"→ Cloning {url} → {dir}");

            // Clone using deploy key; "--" so nothing in the URL can be read as an option
            var r = await _ssh.ExecuteStreamAsync(
                $"{DeployPaths.GitSshEnv} git clone {branchPart}-- {Shell.Quote(url)} {Shell.Quote(dir)} 2>&1", Append);
            if (!r.Succeeded)
                throw new InvalidOperationException("git clone failed - see the output (is the deploy key added to the repository?).");

            // Hand ownership to service user
            await _ssh.RunCommandAsync($"chown -R {DeployPaths.ServiceUser}:{DeployPaths.ServiceUser} {Shell.Quote(dir)}");

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
            var dir = Shell.Quote(DeployPaths.RepoDir(repo.Name));
            var r = await _ssh.ExecuteAsync($"{DeployPaths.GitSshEnv} git -C {dir} pull 2>&1");
            await LoadReposAsync();
            if (!r.Succeeded) throw new InvalidOperationException($"Pull failed: {r.Text}");
            SetStatus($"Pull: {r.Text}");
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
            await _ssh.RunCommandAsync($"rm -rf -- {Shell.Quote(DeployPaths.RepoDir(repo.Name))}");
            await LoadReposAsync();
            SetStatus($"Removed {repo.Name}");
        });
        IsBusyRepo = false;
    }
}
