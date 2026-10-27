using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxRemoteWorker.Core;
using Velopack;
using Velopack.Sources;

namespace LinuxRemoteWorker.Update;

/// <summary>
/// Keeps the installed app current: checks the GitHub releases at start and every few hours, downloads a newer
/// version once its signed manifest checks out against the public key built into the app, and offers a restart -
/// otherwise the update is applied when the app closes (and, should that not happen, on the next start). Off with
/// --no-update, and in builds not installed by the release's Setup.exe (development runs).
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/JustDimus/linux-remote-worker";
    public const string NoUpdateArgument = "--no-update";
    public const string PackageId = "LinuxRemoteWorker";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    private readonly UpdateManager? _manager;
    private readonly string? _publicKey;
    private readonly DispatcherTimer _timer = new() { Interval = CheckEvery };
    private VelopackAsset? _ready;
    private bool _checking;
    private bool _restarting;

    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartToUpdateCommand))]
    private bool _restartAvailable;

    public string VersionText { get; }

    public static bool IsDisabled(IEnumerable<string> args) =>
        args.Contains(NoUpdateArgument, StringComparer.OrdinalIgnoreCase);

    public UpdateService(bool disabled)
    {
        VersionText = "v" + (typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "?");

        if (disabled)
        {
            StatusText = "Automatic updates off (--no-update)";
            return;
        }

        _publicKey = ReadPublicKey();
        if (_publicKey == null)
        {
            StatusText = "Updates off: this build has no update signing key";
            return;
        }

        try
        {
            // Full packages only: each download is checked as a whole against the signed hash
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false),
                new UpdateOptions { MaximumDeltasBeforeFallback = 0 });
            if (manager.IsInstalled) _manager = manager;
            else StatusText = "Development build - automatic updates off";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Updates are not available: {ex.Message}");
            StatusText = "Updates not available";
        }
    }

    /// <summary>Checks now and then every few hours.</summary>
    public void Start()
    {
        if (_manager == null) return;
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        Application.Current.Exit += (_, _) => ApplyOnExit();
        _ = CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_manager == null || _publicKey == null || _checking || _ready != null) return;
        _checking = true;
        try
        {
            StatusText = "Checking for updates…";
            var update = await _manager.CheckForUpdatesAsync();
            if (update == null)
            {
                StatusText = "Up to date";
                return;
            }

            var target = update.TargetFullRelease;
            var version = target.Version.ToString();
            if (!await IsSignedAsync(target, version))
            {
                StatusText = $"Update {version} refused: its signature does not check out";
                AppLog.Error($"Update {version} ({target.FileName}) refused: the signed manifest is missing, invalid or describes another package");
                return;
            }

            StatusText = $"Downloading {version}…";
            await _manager.DownloadUpdatesAsync(update, percent => StatusText = $"Downloading {version}… {percent}%");
            _ready = target;
            RestartAvailable = true;
            StatusText = $"Version {version} is ready - installed when you close the app";
            AppLog.Info($"Update {version} downloaded and verified; applied on restart or when the app closes");
        }
        catch (Exception ex)
        {
            StatusText = "Update check failed - see the application log";
            AppLog.Warn($"Update check failed: {ex.Message}");
        }
        finally
        {
            _checking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(RestartAvailable))]
    private void RestartToUpdate()
    {
        if (_manager == null || _ready == null) return;
        AppLog.Info($"Restarting to apply update {_ready.Version}");
        _restarting = true;
        _manager.ApplyUpdatesAndRestart(_ready, Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    // The updater waits for this process to end, then applies the update without a window or a restart - the next
    // start is already the new version
    private void ApplyOnExit()
    {
        if (_manager == null || _ready == null || _restarting) return;
        try
        {
            AppLog.Info($"Applying update {_ready.Version} after the app closes");
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not hand update {_ready.Version} to the updater ({ex.Message}); it is applied on the next start");
        }
    }

    // The manifest is a separate release asset; Velopack then checks the downloaded package against the signed hash
    private async Task<bool> IsSignedAsync(VelopackAsset target, string version)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"LinuxRemoteWorker/{VersionText.TrimStart('v')}");
            var json = await http.GetStringAsync($"{RepositoryUrl}/releases/download/v{version}/{UpdateManifest.FileName}");
            return UpdateManifest.FromJson(json) is { } manifest
                   && manifest.IsSignedBy(_publicKey!)
                   && manifest.Describes(PackageId, version, target.FileName, target.SHA256 ?? "");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn($"The signed manifest of {version} could not be downloaded: {ex.Message}");
            return false;
        }
    }

    private static string? ReadPublicKey()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("update-signing.pub.pem");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        return pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal) ? pem : null;
    }
}
