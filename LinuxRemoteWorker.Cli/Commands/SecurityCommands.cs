using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Security;

namespace LinuxRemoteWorker.Cli.Commands;

[CommandGroup("security", "SSH login security: keys, an admin user with sudo, and turning password and root logins off")]
public sealed class SecurityCommands : CommandSet
{
    public SecurityCommands(CommandContext context) : base(context) { }

    private async Task<SecurityService> SecurityAsync() => new(await SshAsync());

    [Command("status", "How this session logged in (key or password, as root or through sudo), the server's host key, the effective sshd " +
                       "login settings (password, keyboard-interactive, key and root logins), what the app turned off, and the user's authorized keys.",
        ReadOnly = true)]
    public async Task<object> Status() => Describe(await (await SecurityAsync()).GetStatusAsync());

    [Command("create-key", "Create an Ed25519 key pair on the server, download the private key to this computer (readable only by the current " +
                           "user), add the public key to the user's authorized_keys, delete the key files from the server, and prove it works by " +
                           "logging in with it. Nothing is left behind on failure. Then switch the profile to it: `profiles update <name> --key <path>`.",
        Streams = true, Example = "security create-key --to C:\\Users\\me\\.ssh\\prod-01_ed25519")]
    public async Task<Outcome> CreateKey(
        [Arg("Where to save the private key on this computer (default: ~/.ssh/lrw_<host>_ed25519); the .pub goes next to it")] string? to = null,
        [Arg("Replace an existing file at that path")] bool overwrite = false)
    {
        var security = await SecurityAsync();
        var target = Context.Session.Target;
        var key = await security.CreateKeyAsync(to ?? SecurityService.DefaultKeyPath(target.Host), overwrite, Lines);
        return new Outcome($"Key created and tested: {key.LocalPath} ({key.Fingerprint})")
        {
            Data = new
            {
                privateKeyPath = key.LocalPath,
                publicKeyPath = key.LocalPath + ".pub",
                key.PublicKey,
                key.Fingerprint,
                key.Comment,
                key.AuthorizedKeysFile,
                next = target.ProfileName == null
                    ? $"Connect with --host {target.Host} --key \"{key.LocalPath}\"."
                    : $"Switch the profile to the key: lrw profiles update {target.ProfileName} --key \"{key.LocalPath}\""
            }
        };
    }

    [Command("add-key", "Let another device log in: add its public key (the line of its .pub file, or PuTTYgen's SSH2 block) to the " +
                        "authorized_keys of the login user or --user. The private key stays on that device; compare data.fingerprint with " +
                        "`ssh-keygen -lf ~/.ssh/id_ed25519.pub` there. A key that is already allowed is left alone.",
        Example = "security add-key --public-key @C:\\Users\\me\\Downloads\\mac.pub")]
    public async Task<Outcome> AddKey(
        [Arg("The public key: \"ssh-ed25519 AAAA… user@mac\" or @file (never the private key)", AllowFile = true)] string publicKey,
        [Arg("Linux user it logs in as (default: the user this session logs in as)")] string? user = null)
    {
        var added = await (await SecurityAsync()).AddPublicKeyAsync(publicKey, user, Lines);
        return new Outcome(added.AlreadyThere
            ? $"{added.Key.Fingerprint} could already log in as {added.User}"
            : $"{added.Key.Fingerprint} ({added.Key.Comment}) can now log in as {added.User}")
        {
            Data = new
            {
                user = added.User,
                added.Key.Type,
                added.Key.Fingerprint,
                added.Key.Comment,
                added.Key.File,
                alreadyThere = added.AlreadyThere
            }
        };
    }

    [Command("add-my-key", "Let this computer's existing key log in: read the public half from the private key file (it never leaves this " +
                           "computer), add it to the login user's authorized_keys and prove it by logging in with it. " +
                           "Then switch the profile to it: `profiles update <name> --key <path>`.",
        Example = "security add-my-key --key C:\\Users\\me\\.ssh\\id_ed25519")]
    public async Task<Outcome> AddMyKey(
        [Arg("Private key file on this computer (OpenSSH, PEM or PuTTY .ppk)")] string key,
        [Arg("Its passphrase, when it has one", Secret = true)] string? passphrase = null)
    {
        var added = await (await SecurityAsync()).AddMyKeyAsync(key, passphrase, Lines);
        var target = Context.Session.Target;
        return new Outcome($"{added.LocalPath} ({added.Fingerprint}) logs in as {target.Username}")
        {
            Data = new
            {
                privateKeyPath = added.LocalPath,
                added.Fingerprint,
                added.Comment,
                added.AuthorizedKeysFile,
                next = target.ProfileName == null
                    ? $"Connect with --host {target.Host} --key \"{added.LocalPath}\"."
                    : $"lrw profiles update {target.ProfileName} --key \"{added.LocalPath}\""
            }
        };
    }

    [Command("remove-key", "Stop a key from logging in as the login user (a lost device): remove it from authorized_keys by its fingerprint " +
                           "(see `security status`). Refused for the last key while password login is off, and put back when this session " +
                           "could not log in afterwards - so the key you are logged in with cannot be removed.",
        Destructive = true, Example = "security remove-key --fingerprint SHA256:abc… --yes")]
    public async Task<Outcome> RemoveKey([Arg("The key's fingerprint, SHA256:…")] string fingerprint)
    {
        var status = await (await SecurityAsync()).RemoveKeyAsync(fingerprint.Trim(), Lines);
        return new Outcome($"{fingerprint} can no longer log in as {status.User}") { Data = Describe(status) };
    }

    [Command("create-admin", "Create a Linux user with passwordless sudo and a new SSH key (downloaded to this computer), let in the keys that " +
                             "log in as the current user (--no-copy-keys: only the new key), then log in as the new user and run sudo to prove it " +
                             "works; on failure the user is removed again. Next: switch the profile to it (`profiles update <name> --user <user> " +
                             "--key <path>`), then `security disable-root-login`.",
        Streams = true, Example = "security create-admin --user admin")]
    public async Task<Outcome> CreateAdmin(
        [Arg("Name of the new user: lower-case letters, digits, '_' or '-'")] string user,
        [Arg("Where to save its private key on this computer (default: ~/.ssh/lrw_<host>_<user>_ed25519)")] string? to = null,
        [Arg("Also let in the keys that can log in as the current user")] bool copyKeys = true,
        [Arg("Replace an existing key file at that path")] bool overwrite = false)
    {
        var security = await SecurityAsync();
        var target = Context.Session.Target;
        var admin = await security.CreateAdminUserAsync(user, to ?? SecurityService.DefaultKeyPath(target.Host, user.Trim()), copyKeys, overwrite, Lines);
        return new Outcome($"User {admin.User} created with passwordless sudo; logs in with {admin.Key.LocalPath}")
        {
            Data = new
            {
                user = admin.User,
                privateKeyPath = admin.Key.LocalPath,
                admin.Key.Fingerprint,
                copiedKeys = admin.CopiedKeys,
                next = target.ProfileName == null
                    ? $"Connect with --host {target.Host} --user {admin.User} --key \"{admin.Key.LocalPath}\"."
                    : $"lrw profiles update {target.ProfileName} --user {admin.User} --key \"{admin.Key.LocalPath}\", then lrw security disable-root-login --yes"
            }
        };
    }

    [Command("disable-root-login", "Forbid SSH logins as root (PermitRootLogin no). Only from a key login of another user with passwordless sudo " +
                                   "(create one with `security create-admin` and switch the profile to it first). sshd is checked and reloaded, " +
                                   "a fresh login with sudo is tested, and everything is undone if anything fails. Profiles that log in as root stop working.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> DisableRootLogin()
    {
        var status = await (await SecurityAsync()).DisableRootLoginAsync(Lines);
        return new Outcome($"Root login is off on {Context.Session.Target.Host}; administer it as {status.User} with sudo") { Data = Describe(status) };
    }

    [Command("enable-root-login", "Allow SSH logins as root again by removing the app's setting (the server's own sshd configuration may still " +
                                  "forbid them). This weakens the server's security - only when the user asks for it.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> EnableRootLogin()
    {
        var status = await (await SecurityAsync()).EnableRootLoginAsync(Lines);
        return new Outcome(status.RootLoginAllowed
            ? "Root login is allowed again"
            : "The app's setting was removed, but the server's own sshd configuration still forbids root login")
        { Data = Describe(status) };
    }

    [Command("disable-password-login", "Turn password and keyboard-interactive SSH logins off, so only keys get in. Only from a session that " +
                                       "logged in with a key (switch the profile to a key first). sshd is checked and reloaded, a fresh key login " +
                                       "is tested, and everything is undone if anything fails - open sessions stay connected.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> DisablePasswordLogin()
    {
        var status = await (await SecurityAsync()).DisablePasswordLoginAsync(Lines);
        return new Outcome($"Password login is off on {Context.Session.Target.Host}; only SSH keys get in") { Data = Describe(status) };
    }

    [Command("enable-password-login", "Allow password logins again by removing the app's setting (the server's own sshd configuration may " +
                                      "still forbid them). This weakens the server's security - only when the user asks for it.",
        Destructive = true, Streams = true)]
    public async Task<Outcome> EnablePasswordLogin()
    {
        var status = await (await SecurityAsync()).EnablePasswordLoginAsync(Lines);
        return new Outcome(status.PasswordLoginAllowed
            ? "Password login is allowed again"
            : "The app's setting was removed, but the server's own sshd configuration still turns password login off")
        { Data = Describe(status) };
    }

    private static object Describe(SecurityStatus s) => new
    {
        user = s.User,
        privileges = s.Privileges,
        sessionAuth = s.SessionAuth == SshAuthMethod.Password ? "password" : "key",
        sessionKeyPath = s.SessionKeyPath,
        hostKey = s.HostKey?.ToString(),
        passwordLoginAllowed = s.PasswordLoginAllowed,
        passwordLoginDisabledByApp = s.PolicyInstalled,
        canDisablePasswordLogin = s.CannotDisablePasswordLogin == null,
        cannotDisableBecause = s.CannotDisablePasswordLogin,
        rootLoginAllowed = s.RootLoginAllowed,
        rootLoginDisabledByApp = s.RootPolicyInstalled,
        canDisableRootLogin = s.CannotDisableRootLogin == null,
        cannotDisableRootLoginBecause = s.CannotDisableRootLogin,
        sshd = new
        {
            passwordAuthentication = s.Sshd.PasswordAuthentication,
            kbdInteractiveAuthentication = s.Sshd.KbdInteractiveAuthentication,
            pubkeyAuthentication = s.Sshd.PubkeyAuthentication,
            permitRootLogin = s.Sshd.PermitRootLogin
        },
        authorizedKeysFiles = s.AuthorizedKeysFiles,
        authorizedKeys = s.AuthorizedKeys.Select(k => new { k.Type, k.Bits, k.Fingerprint, k.Comment, k.File })
    };
}
