using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Security;

/// <summary>How the server lets users log in, and how this session logged in.</summary>
public sealed record SecurityStatus(
    string User,
    SshAuthMethod SessionAuth,
    string? SessionKeyPath,
    HostKey? HostKey,
    SshdSettings Sshd,
    bool PolicyInstalled,
    bool DropInsSupported,
    IReadOnlyList<string> AuthorizedKeysFiles,
    IReadOnlyList<AuthorizedKey> AuthorizedKeys,
    string Privileges = "root",
    string? RootLogin = null,
    bool RootPolicyInstalled = false)
{
    public bool PasswordLoginAllowed => Sshd.PasswordLoginAllowed;

    public bool IsRoot => User == "root";

    /// <summary>Root may log in unless PermitRootLogin is "no" (unknown counts as allowed).</summary>
    public bool RootLoginAllowed => !string.Equals(RootLogin, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why password login cannot be turned off from this session, or null when it can.</summary>
    public string? CannotDisablePasswordLogin =>
        !PasswordLoginAllowed ? "Password login is already off."
        : SessionAuth != SshAuthMethod.Key
            ? "This session logged in with a password. Create an SSH key, log in with it, then turn password login off - " +
              "so the key is proven to work before passwords stop working."
        : !DropInsSupported ? NoDropIns
        : null;

    /// <summary>Why root login cannot be turned off from this session, or null when it can.</summary>
    public string? CannotDisableRootLogin =>
        !RootLoginAllowed ? "Root login is already off."
        : IsRoot
            ? "This session is logged in as root. Create an admin user, switch this connection to it, then turn root " +
              "login off - so the admin user and its sudo are proven to work first."
        : SessionAuth != SshAuthMethod.Key ? "Log in as the admin user with its SSH key first."
        : Privileges != "sudo" ? $"{User} has no passwordless sudo, so nobody could administer the server without root."
        : !DropInsSupported ? NoDropIns
        : null;

    private const string NoDropIns =
        "This server's /etc/ssh/sshd_config does not include /etc/ssh/sshd_config.d (an older OpenSSH), so the app " +
        "cannot add its setting safely. Change sshd_config by hand.";
}

/// <summary>A key pair made by <see cref="SecurityService.CreateKeyAsync"/>: the private half is only on this computer.</summary>
public sealed record CreatedKey(string LocalPath, string PublicKey, string Fingerprint, string Comment, string AuthorizedKeysFile);

/// <summary>A public key added for another device; <see cref="AlreadyThere"/> when it could log in already.</summary>
public sealed record AddedKey(AuthorizedKey Key, string User, bool AlreadyThere);

/// <summary>An admin user made by <see cref="SecurityService.CreateAdminUserAsync"/>, with the key it logs in with.</summary>
public sealed record CreatedAdmin(string User, CreatedKey Key, int CopiedKeys);

/// <summary>
/// The Security screen: SSH login settings of the server, keys, an admin user with sudo, and turning password and
/// root logins off and on. Each lock-down is allowed only from a session that already logs in the way that remains
/// possible afterwards, and is tested with a fresh login and rolled back when that fails - so it cannot lock anyone out.
/// </summary>
public sealed class SecurityService
{
    private readonly SshService _ssh;

    public SecurityService(SshService ssh)
    {
        _ssh = ssh;
    }

    private SshCredentials Session =>
        _ssh.Credentials ?? throw new InvalidOperationException("Not connected");

    public async Task<SecurityStatus> GetStatusAsync()
    {
        var session = Session;
        var (status, files, keys) = await ReadUserAsync(session.Username, listKeys: true);
        return new SecurityStatus(
            session.Username,
            session.Method,
            session.Method == SshAuthMethod.Key ? session.KeyPath : null,
            _ssh.ServerKey,
            status.Settings,
            status.PolicyInstalled,
            status.DropInsSupported,
            files,
            keys,
            _ssh.Privileges,
            status.RootLogin,
            status.RootPolicyInstalled);
    }

    // sshd's view of one user: settings, authorized_keys files and (optionally) the keys in them
    private async Task<(SshdConfig.StatusOutput Status, List<string> Files, List<AuthorizedKey> Keys)> ReadUserAsync(string user, bool listKeys)
    {
        var status = SshdConfig.ParseStatus((await _ssh.ExecuteAsync(SshdConfig.StatusScript(user))).Output);
        var files = status.Home.Length == 0
            ? []
            : SshdConfig.ExpandAuthorizedKeysFiles(status.Settings.AuthorizedKeysFiles, user, status.Uid, status.Home);
        var keys = !listKeys || files.Count == 0 ? [] : SshdConfig.ParseKeys((await _ssh.ExecuteAsync(SshdConfig.ListKeysScript(files))).Output);
        return (status, files, keys);
    }

    /// <summary>The error message for a user name the app would not create, or null.</summary>
    public static string? ValidateNewUserName(string name) => SshdConfig.ValidateNewUserName(name.Trim());

    /// <summary>Where a new key for <paramref name="host"/> goes by default: ~/.ssh/lrw_&lt;host&gt;[_&lt;user&gt;]_ed25519, numbered when taken.</summary>
    public static string DefaultKeyPath(string host, string? user = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var stem = "lrw_" + Regex.Replace(host, "[^A-Za-z0-9.-]", "_") + (user == null ? "" : "_" + Safe(user)) + "_ed25519";
        var path = Path.Combine(dir, stem);
        for (var n = 2; File.Exists(path) || File.Exists(path + ".pub"); n++)
            path = Path.Combine(dir, $"{stem}_{n}");
        return path;
    }

    /// <summary>
    /// Makes an Ed25519 key pair on the server, downloads the private key to <paramref name="localPath"/>
    /// (readable only by the current user), adds the public key to the login user's authorized_keys, removes the
    /// key files from the server, and proves it works by logging in with it. On failure nothing is left behind.
    /// </summary>
    public async Task<CreatedKey> CreateKeyAsync(string localPath, bool overwrite, Action<string> onLine)
    {
        var session = Session;
        localPath = CheckLocalPath(localPath, overwrite);
        var (status, files, _) = await ReadUserAsync(session.Username, listKeys: false);

        var key = await CreateAndAuthorizeKeyAsync(session.Username, status, files, localPath, onLine);

        onLine($"→ Logging in with the new key ({session.Username}@{session.Host})");
        if (await _ssh.TryLoginAsync(SshCredentials.WithKey(session.Host, session.Username, localPath)) is { } failure)
        {
            await _ssh.ExecuteAsync(SshdConfig.UnauthorizeScript(key.AuthorizedKeysFile, key.PublicKey));
            DeleteLocal(localPath);
            throw LoginFailed("The new key did not work, so it was removed again", failure, session.Host, session.Username, localPath);
        }

        onLine("✓ The key works");
        return key;
    }

    /// <summary>
    /// Creates <paramref name="user"/> with passwordless sudo and a new SSH key (downloaded to <paramref name="localPath"/>);
    /// with <paramref name="copyCurrentKeys"/> the keys that log in as the current user work for it too. Then logs in as
    /// the new user and runs sudo to prove both work. On failure the user, its sudoers file and the key are removed again.
    /// </summary>
    public async Task<CreatedAdmin> CreateAdminUserAsync(string user, string localPath, bool copyCurrentKeys, bool overwrite, Action<string> onLine)
    {
        var session = Session;
        user = user.Trim();
        OperationException.ThrowIfInvalid(SshdConfig.ValidateNewUserName(user));
        if (!_ssh.SudoWorks)
            throw OperationException.Refused($"{session.Username} cannot run commands as root, so it cannot create users.");
        localPath = CheckLocalPath(localPath, overwrite);

        onLine($"→ Creating user {user} with passwordless sudo");
        var created = await _ssh.ExecuteStreamAsync(SshdConfig.CreateAdminScript(user), onLine);
        if (created.ExitCode == SshdConfig.ExitUserExists)
            throw OperationException.Invalid($"User {user} already exists on the server - choose another name.");
        if (!created.Succeeded)
        {
            await _ssh.ExecuteAsync(SshdConfig.RemoveAdminScript(user));
            throw OperationException.Failed($"Could not create user {user}.", created.Text);
        }

        var copied = 0;
        CreatedKey key;
        try
        {
            var (status, files, _) = await ReadUserAsync(user, listKeys: false);
            key = await CreateAndAuthorizeKeyAsync(user, status, files, localPath, onLine);

            if (copyCurrentKeys)
            {
                var (_, currentFiles, currentKeys) = await ReadUserAsync(session.Username, listKeys: true);
                if (currentKeys.Count > 0)
                {
                    onLine($"→ Letting in the {currentKeys.Count} key(s) that log in as {session.Username}");
                    var copy = await _ssh.ExecuteAsync(SshdConfig.CopyKeysScript(currentFiles, key.AuthorizedKeysFile, user));
                    if (!copy.Succeeded) throw OperationException.Failed($"Could not copy the keys of {session.Username}.", copy.Text);
                    copied = currentKeys.Count;
                }
            }
        }
        catch
        {
            await _ssh.ExecuteAsync(SshdConfig.RemoveAdminScript(user));
            DeleteLocal(localPath);
            throw;
        }

        onLine($"→ Logging in as {user} with the new key and testing sudo");
        if (await _ssh.TryLoginAsync(SshCredentials.WithKey(session.Host, user, localPath)) is { } failure)
        {
            await _ssh.ExecuteAsync(SshdConfig.RemoveAdminScript(user));
            DeleteLocal(localPath);
            throw LoginFailed($"Logging in as {user} failed, so the user was removed again", failure, session.Host, user, localPath);
        }

        onLine($"✓ {user} logs in with the key and has passwordless sudo");
        return new CreatedAdmin(user, key, copied);
    }

    /// <summary>
    /// Lets another device in: appends its public key (pasted, or read from its .pub file) to the authorized_keys of
    /// <paramref name="user"/> (default: the login user). The private key stays on that device, so the login cannot
    /// be tested from here - compare the returned fingerprint with `ssh-keygen -lf ~/.ssh/id_ed25519.pub` there.
    /// A key that is already allowed is left alone.
    /// </summary>
    public async Task<AddedKey> AddPublicKeyAsync(string publicKey, string? user, Action<string> onLine)
    {
        var key = SshPublicKey.Parse(publicKey);
        user = string.IsNullOrWhiteSpace(user) ? Session.Username : user.Trim();
        OperationException.ThrowIfInvalid(ValidateExistingUserName(user));

        var (status, files, keys) = await ReadUserAsync(user, listKeys: true);
        if (status.Home.Length == 0) throw OperationException.NotFound($"There is no user {user} on the server.");
        if (keys.FirstOrDefault(k => k.Fingerprint == key.Fingerprint) is { } existing)
        {
            onLine($"✓ {key.Fingerprint} can already log in as {user}");
            return new AddedKey(existing, user, AlreadyThere: true);
        }
        var file = files.FirstOrDefault()
                   ?? throw OperationException.Failed($"sshd reads no authorized_keys file for {user} (AuthorizedKeysFile none).");

        onLine($"→ Adding {key.Type} {key.Fingerprint} {key.Comment} to {file}");
        var authorized = await _ssh.ExecuteAsync(SshdConfig.AuthorizeScript(file, user, key.Line));
        if (!authorized.Succeeded) throw OperationException.Failed($"Could not add the key to {file}.", authorized.Text);

        // The server's own ssh-keygen must read it the same way - e.g. an old OpenSSH does not know sk- keys
        var (_, _, after) = await ReadUserAsync(user, listKeys: true);
        if (after.FirstOrDefault(k => k.Fingerprint == key.Fingerprint) is not { } added)
        {
            await _ssh.ExecuteAsync(SshdConfig.UnauthorizeScript(file, key.Line));
            throw OperationException.Failed($"The server's OpenSSH does not accept {key.Type} keys, so the key was removed again.");
        }

        onLine($"✓ The key can log in as {user}");
        return new AddedKey(added, user, AlreadyThere: false);
    }

    /// <summary>
    /// Lets this computer's own key in: reads the public half from the private key file (the private half never
    /// leaves this computer), adds it to the login user's authorized_keys, and proves it by logging in with it.
    /// </summary>
    public async Task<CreatedKey> AddMyKeyAsync(string privateKeyPath, string? passphrase, Action<string> onLine)
    {
        var session = Session;
        privateKeyPath = Path.GetFullPath(privateKeyPath.Trim());
        var key = ReadLocalPublicKey(privateKeyPath, passphrase);

        var added = await AddPublicKeyAsync(key.Line, session.Username, onLine);

        onLine($"→ Logging in with {privateKeyPath} ({session.Username}@{session.Host})");
        var credentials = SshCredentials.WithKey(session.Host, session.Username, privateKeyPath, passphrase);
        if (await _ssh.TryLoginAsync(credentials) is { } failure)
        {
            if (!added.AlreadyThere) await _ssh.ExecuteAsync(SshdConfig.UnauthorizeScript(added.Key.File, key.Line));
            throw LoginFailed("Logging in with the key failed, so it was removed again", failure, session.Host, session.Username, privateKeyPath);
        }

        onLine("✓ The key works");
        return new CreatedKey(privateKeyPath, key.Line, key.Fingerprint, key.Comment, added.Key.File);
    }

    /// <summary>
    /// The public half of a private key file on this computer (OpenSSH, PEM or PuTTY), with the comment of the .pub
    /// file next to it when that one matches.
    /// </summary>
    public static SshPublicKey ReadLocalPublicKey(string privateKeyPath, string? passphrase)
    {
        if (ConnectionDiagnostics.PreflightCheck(SshCredentials.WithKey("-", "-", privateKeyPath)) is { } problem)
            throw OperationException.Invalid($"{problem.Summary}. {problem.Hint}");

        byte[] blob;
        try
        {
            using var file = string.IsNullOrEmpty(passphrase) ? new Renci.SshNet.PrivateKeyFile(privateKeyPath)
                : new Renci.SshNet.PrivateKeyFile(privateKeyPath, passphrase);
            blob = file.HostKeyAlgorithms.First().Data;
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            throw OperationException.Invalid("The key is encrypted - enter its passphrase.");
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Security.Cryptography.CryptographicException)
        {
            throw OperationException.Invalid($"The key could not be read (wrong passphrase or unsupported format): {ex.Message}");
        }

        var comment = "";
        try
        {
            if (File.Exists(privateKeyPath + ".pub")
                && SshPublicKey.FromAuthorizedKeysLine(File.ReadAllText(privateKeyPath + ".pub")) is { } pub
                && pub.Base64 == Convert.ToBase64String(blob))
                comment = pub.Comment;
        }
        catch (IOException) { /* the comment is a nicety */ }

        return SshPublicKey.FromBlob(blob, comment.Length > 0 ? comment : $"{Safe(Environment.UserName)}@{Safe(Environment.MachineName)}");
    }

    /// <summary>
    /// Removes a key from the login user's authorized_keys (a lost device). Refused when it is the last key and
    /// password login is off; put back when this session could not log in again afterwards - so you cannot remove
    /// the key you are logged in with. Open sessions of that key stay connected until they end.
    /// </summary>
    public async Task<SecurityStatus> RemoveKeyAsync(string fingerprint, Action<string> onLine)
    {
        var session = Session;
        var status = await GetStatusAsync();
        var key = status.AuthorizedKeys.FirstOrDefault(k => k.Fingerprint == fingerprint)
                  ?? throw OperationException.NotFound($"{fingerprint} is not among the keys of {status.User}.");
        if (!status.PasswordLoginAllowed && status.AuthorizedKeys.All(k => k.Fingerprint == fingerprint))
            throw OperationException.Refused(
                $"This is the last key of {status.User} and password login is off - nobody could log in as {status.User} any more. Add another key first.");

        var read = await _ssh.ExecuteAsync($"cat -- {Shell.Quote(key.File)}");
        if (!read.Succeeded) throw OperationException.Failed($"Could not read {key.File}.", read.Text);
        var (content, removed) = SshPublicKey.RemoveFromAuthorizedKeys(read.Output, fingerprint);
        if (removed == 0)
            throw OperationException.Failed($"{fingerprint} is listed, but its line in {key.File} could not be read - remove it by hand.");

        onLine($"→ Removing {key.Type} {fingerprint} {key.Comment} from {key.File}");
        var written = await _ssh.ExecuteAsync(Shell.WriteFile(key.File, content));
        if (!written.Succeeded) throw OperationException.Failed($"Could not write {key.File}.", written.Text);

        if (session.Method == SshAuthMethod.Key)
        {
            onLine("→ Checking that this session's key still logs in");
            if (await _ssh.TryLoginAsync(session) is not null)
            {
                await _ssh.ExecuteAsync(Shell.WriteFile(key.File, read.Output));
                throw OperationException.Refused("That is the key this session logs in with - removing it would lock you out, so it was put back.");
            }
        }

        onLine($"✓ {fingerprint} can no longer log in as {status.User}");
        return await GetStatusAsync();
    }

    /// <summary>Existing users may have names the app would not create; only what could break a path or a command is refused.</summary>
    public static string? ValidateExistingUserName(string name) =>
        name.Length is > 0 and <= 64 && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ':' or '/' or '\\')
            ? null
            : $"'{name}' is not a Linux user name.";

    /// <summary>
    /// Turns password and keyboard-interactive logins off with a drop-in in /etc/ssh/sshd_config.d. Allowed only
    /// from a key login; rolled back when sshd rejects the config, the setting does not take effect, or a fresh
    /// login with this session's key fails afterwards.
    /// </summary>
    public async Task<SecurityStatus> DisablePasswordLoginAsync(Action<string> onLine)
    {
        var status = await GetStatusAsync();
        if (status.CannotDisablePasswordLogin is { } reason)
            throw OperationException.Refused(reason);

        await LockDownAsync(SshdConfig.PolicyFile, SshdConfig.PolicyContent, SshdConfig.RemovePolicyCommand,
            async () => SshdSettings.Parse((await _ssh.ExecuteAsync(SshdConfig.EffectiveSettingsCommand(status.User))).Lines).PasswordLoginAllowed,
            "password login on", onLine);

        onLine("✓ Password login is off - only SSH keys get in");
        return await GetStatusAsync();
    }

    /// <summary>Removes the app's drop-in again. The server's own configuration may still keep passwords off.</summary>
    public async Task<SecurityStatus> EnablePasswordLoginAsync(Action<string> onLine)
    {
        await RemovePolicyAsync(SshdConfig.PolicyFile, SshdConfig.RemovePolicyCommand, onLine);
        var status = await GetStatusAsync();
        onLine(status.PasswordLoginAllowed
            ? "✓ Password login is allowed again"
            : "⚠ The server's own sshd configuration still turns password login off.");
        return status;
    }

    /// <summary>
    /// Forbids SSH logins as root (PermitRootLogin no). Allowed only from a key login of another user with passwordless
    /// sudo; rolled back when sshd rejects it, it does not take effect, or a fresh login with sudo fails afterwards.
    /// </summary>
    public async Task<SecurityStatus> DisableRootLoginAsync(Action<string> onLine)
    {
        var status = await GetStatusAsync();
        if (status.CannotDisableRootLogin is { } reason)
            throw OperationException.Refused(reason);

        await LockDownAsync(SshdConfig.RootPolicyFile, SshdConfig.RootPolicyContent, SshdConfig.RemoveRootPolicyCommand,
            async () => !string.Equals(
                SshdSettings.Parse((await _ssh.ExecuteAsync(SshdConfig.EffectiveSettingsCommand("root"))).Lines).PermitRootLogin,
                "no", StringComparison.OrdinalIgnoreCase),
            "root login allowed", onLine);

        onLine("✓ Root login is off - administer the server as " + status.User + " with sudo");
        return await GetStatusAsync();
    }

    /// <summary>Removes the app's root-login drop-in again. The server's own configuration may still forbid it.</summary>
    public async Task<SecurityStatus> EnableRootLoginAsync(Action<string> onLine)
    {
        await RemovePolicyAsync(SshdConfig.RootPolicyFile, SshdConfig.RemoveRootPolicyCommand, onLine);
        var status = await GetStatusAsync();
        onLine(status.RootLoginAllowed
            ? $"✓ Root login is allowed again (PermitRootLogin {status.RootLogin ?? "default"})"
            : "⚠ The server's own sshd configuration still forbids root login.");
        return status;
    }

    // Write the drop-in, check it, make sure it takes effect, reload, and prove a fresh login still works - else undo
    private async Task LockDownAsync(string file, string content, string removeCommand, Func<Task<bool>> stillOpen,
        string what, Action<string> onLine)
    {
        onLine($"→ Writing {file}");
        var written = await _ssh.ExecuteAsync("mkdir -p /etc/ssh/sshd_config.d && " + Shell.WriteFile(file, content));
        if (!written.Succeeded) throw OperationException.Failed($"Could not write {file}.", written.Text);

        var test = await _ssh.ExecuteAsync(SshdConfig.TestConfigCommand);
        if (!test.Succeeded)
        {
            await _ssh.ExecuteAsync(removeCommand);
            throw OperationException.Failed("sshd rejected the configuration, so nothing was changed.", test.Text);
        }

        if (await stillOpen())
        {
            await _ssh.ExecuteAsync(removeCommand);
            throw OperationException.Failed(
                $"Another part of the sshd configuration (a Match block, or a setting sshd reads first) keeps {what}, " +
                "so the change was undone. Check /etc/ssh/sshd_config.");
        }

        onLine("→ Reloading sshd (open sessions stay connected)");
        var reloaded = await _ssh.ExecuteAsync(SshdConfig.ReloadCommand);

        onLine("→ Testing a fresh login with this session's key" + (_ssh.UsesSudo ? " and sudo" : ""));
        if (await _ssh.TryLoginAsync(Session) is { } failure)
        {
            onLine("✗ The test login failed - undoing the change");
            await _ssh.ExecuteAsync($"{removeCommand}; {SshdConfig.ReloadCommand}");
            var problem = ConnectionDiagnostics.Explain(failure, Session);
            throw OperationException.Failed($"A fresh login failed after the change, so it was undone: {problem.Summary}.", problem.Detail);
        }

        if (!reloaded.Succeeded)
            onLine("⚠ sshd could not be reloaded; the setting applies after `systemctl restart ssh` or a reboot.");
    }

    private async Task RemovePolicyAsync(string file, string removeCommand, Action<string> onLine)
    {
        onLine($"→ Removing {file}");
        await _ssh.ExecuteAsync(removeCommand);

        var test = await _ssh.ExecuteAsync(SshdConfig.TestConfigCommand);
        if (!test.Succeeded)
            throw OperationException.Failed("sshd reports a configuration error - it was not reloaded. Fix it before reconnecting.", test.Text);

        onLine("→ Reloading sshd");
        var reloaded = await _ssh.ExecuteAsync(SshdConfig.ReloadCommand);
        if (!reloaded.Succeeded)
            onLine("⚠ sshd could not be reloaded; the setting applies after `systemctl restart ssh` or a reboot.");
    }

    // Generate on the server, download, authorize for the user, always clean the server's copy
    private async Task<CreatedKey> CreateAndAuthorizeKeyAsync(string user, SshdConfig.StatusOutput status, List<string> files,
        string localPath, Action<string> onLine)
    {
        if (!status.HasKeygen)
            throw OperationException.Failed("ssh-keygen is not installed on the server (package openssh-client).");
        if (status.Settings.PubkeyAuthentication == false)
            throw OperationException.Refused("The server has key logins turned off (PubkeyAuthentication no) - a key would not work.");
        var authorizedKeys = files.FirstOrDefault()
                             ?? throw OperationException.Failed($"sshd reads no authorized_keys file for {user} (AuthorizedKeysFile none).");

        var comment = $"lrw-{Safe(Environment.UserName)}@{Safe(Environment.MachineName)}-{DateTime.Now:yyyyMMdd}";
        onLine($"→ Creating an Ed25519 key pair on {Session.Host}");
        var created = await _ssh.ExecuteAsync(SshdConfig.CreateKeyScript(comment));
        var fields = created.Output.Replace("\r", "").Split('\n')
            .Where(l => l.StartsWith("@@", StringComparison.Ordinal) && l.Contains(' '))
            .ToDictionary(l => l[2..l.IndexOf(' ')], l => l[(l.IndexOf(' ') + 1)..].Trim());
        if (!created.Succeeded || !fields.TryGetValue("dir", out var dir) || !SshdConfig.IsKeyDir(dir)
            || !fields.TryGetValue("pub", out var publicKey) || publicKey.Length == 0)
            throw OperationException.Failed("ssh-keygen could not create the key.", created.Text);
        var fingerprint = SshdConfig.ParseFingerprint(fields.GetValueOrDefault("fp", ""))?.Fingerprint ?? "";

        try
        {
            onLine($"→ Downloading the private key to {localPath}");
            WritePrivateFile(localPath, await _ssh.ReadBytesAsync(dir + "/id_ed25519"));
            File.WriteAllText(localPath + ".pub", publicKey + "\n");

            onLine($"→ Adding the public key to {authorizedKeys}");
            var authorized = await _ssh.ExecuteAsync(SshdConfig.AuthorizeScript(authorizedKeys, user, publicKey));
            if (!authorized.Succeeded)
                throw OperationException.Failed($"Could not add the key to {authorizedKeys}.", authorized.Text);
        }
        catch
        {
            DeleteLocal(localPath);
            throw;
        }
        finally
        {
            onLine("→ Removing the key files from the server");
            await _ssh.ExecuteAsync(SshdConfig.RemoveKeyDirScript(dir));
        }

        return new CreatedKey(localPath, publicKey, fingerprint, comment, authorizedKeys);
    }

    private static string CheckLocalPath(string localPath, bool overwrite)
    {
        localPath = Path.GetFullPath(localPath.Trim());
        if (!overwrite && (File.Exists(localPath) || File.Exists(localPath + ".pub")))
            throw OperationException.Invalid($"{localPath} already exists - choose another file name.");
        return localPath;
    }

    private static OperationException LoginFailed(string what, Exception failure, string host, string user, string keyPath)
    {
        var problem = ConnectionDiagnostics.Explain(failure, SshCredentials.WithKey(host, user, keyPath));
        return OperationException.Failed($"{what}: {problem.Summary}. {problem.Hint}", problem.Detail);
    }

    // Comments end up in authorized_keys: keep them to one word
    private static string Safe(string text) => Regex.Replace(text, "[^A-Za-z0-9._-]", "_");

    /// <summary>Writes a private key readable only by the current user - OpenSSH refuses keys others can read.</summary>
    private static void WritePrivateFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void DeleteLocal(string path)
    {
        foreach (var file in new[] { path, path + ".pub" })
        {
            try { File.Delete(file); }
            catch (Exception ex) { AppLog.Warn($"Could not delete {file}: {ex.Message}"); }
        }
    }
}
