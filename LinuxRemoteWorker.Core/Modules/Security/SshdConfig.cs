using System.Globalization;
using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Modules.Security;

/// <summary>The sshd settings that decide how users may log in, as `sshd -T` reports them (null: not reported).</summary>
public sealed record SshdSettings(
    bool? PasswordAuthentication,
    bool? KbdInteractiveAuthentication,
    bool? PubkeyAuthentication,
    string? PermitRootLogin,
    IReadOnlyList<string> AuthorizedKeysFiles)
{
    /// <summary>
    /// Passwords get in through "password" or through keyboard-interactive (PAM asks for the password);
    /// only both off means keys only. Unknown counts as allowed.
    /// </summary>
    public bool PasswordLoginAllowed => PasswordAuthentication != false || KbdInteractiveAuthentication != false;

    public static SshdSettings Parse(IEnumerable<string> sshdTLines)
    {
        bool? password = null, interactive = null, pubkey = null;
        string? rootLogin = null;
        var keyFiles = new List<string>();

        foreach (var raw in sshdTLines)
        {
            var parts = raw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var value = parts[1].Trim();
            switch (parts[0].ToLowerInvariant())
            {
                case "passwordauthentication": password = YesNo(value); break;
                // OpenSSH 8.7 renamed ChallengeResponseAuthentication; older servers report the old name
                case "kbdinteractiveauthentication":
                case "challengeresponseauthentication": interactive ??= YesNo(value); break;
                case "pubkeyauthentication": pubkey = YesNo(value); break;
                case "permitrootlogin": rootLogin = value; break;
                case "authorizedkeysfile": keyFiles.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)); break;
            }
        }

        return new SshdSettings(password, interactive, pubkey, rootLogin, keyFiles);
    }

    private static bool? YesNo(string value) => value.ToLowerInvariant() switch
    {
        "yes" => true,
        "no" => false,
        _ => null
    };
}

/// <summary>One line of a user's authorized_keys, as `ssh-keygen -lf` fingerprints it.</summary>
public sealed record AuthorizedKey(string File, int Bits, string Fingerprint, string Comment, string Type);

/// <summary>Parsing and the shell scripts behind the Security screen. Every value in a script is shell-quoted.</summary>
internal static class SshdConfig
{
    /// <summary>The drop-in the app writes to turn password logins off; deleting it turns them back on.</summary>
    public const string PolicyFile = "/etc/ssh/sshd_config.d/00-lrw-password-login.conf";

    // "00-" sorts first, and for sshd the first value of a setting wins - so it beats e.g. 50-cloud-init.conf.
    // ChallengeResponseAuthentication is understood by old and new OpenSSH (newer ones alias it).
    public const string PolicyContent =
        "# Written by Linux Remote Worker (Security screen): SSH key logins only.\n" +
        "# Delete this file and reload sshd (or use the app) to allow password logins again.\n" +
        "PasswordAuthentication no\n" +
        "ChallengeResponseAuthentication no\n";

    /// <summary>The drop-in the app writes to forbid logging in as root; deleting it allows it again.</summary>
    public const string RootPolicyFile = "/etc/ssh/sshd_config.d/00-lrw-root-login.conf";

    public const string RootPolicyContent =
        "# Written by Linux Remote Worker (Security screen): no SSH logins as root - use an admin user with sudo.\n" +
        "# Delete this file and reload sshd (or use the app) to allow root logins again.\n" +
        "PermitRootLogin no\n";

    public const string KeyDirPrefix = "/tmp/lrw-key.";

    private const string Sshd = "\"$(command -v sshd || echo /usr/sbin/sshd)\"";

    /// <summary>
    /// The effective settings for <paramref name="user"/> (Match blocks applied), falling back to the global ones.
    /// Commands run as root (sudo for other logins), so the user is always named, never taken from `id`.
    /// </summary>
    public static string EffectiveSettingsCommand(string user) =>
        $"{{ {Sshd} -T -C {Shell.Quote($"user={user},host=localhost,addr=127.0.0.1")} 2>/dev/null || {Sshd} -T 2>/dev/null; }}";

    public static string StatusScript(string user) => string.Join("\n",
        $"u={Shell.Quote(user)}",
        "printf '@@user %s\\n' \"$u\"",
        "getent passwd \"$u\" | awk -F: '{print \"@@uid \" $3; print \"@@home \" $6}'",
        EffectiveSettingsCommand(user) + " | sed 's/^/@@sshd /'",
        EffectiveSettingsCommand("root") + " | grep -i '^permitrootlogin ' | sed 's/^/@@root /'",
        $"[ -f {PolicyFile} ] && echo '@@policy'",
        $"[ -f {RootPolicyFile} ] && echo '@@rootpolicy'",
        "grep -Eqi '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\\.d/\\*\\.conf' /etc/ssh/sshd_config 2>/dev/null && echo '@@dropins'",
        "command -v ssh-keygen >/dev/null && echo '@@keygen'",
        "true");

    public sealed record StatusOutput(string User, string Uid, string Home, SshdSettings Settings, bool PolicyInstalled,
        bool DropInsSupported, bool HasKeygen, string? RootLogin = null, bool RootPolicyInstalled = false);

    public static StatusOutput ParseStatus(string output)
    {
        string user = "", uid = "", home = "";
        string? rootLogin = null;
        var sshd = new List<string>();
        bool policy = false, rootPolicy = false, dropIns = false, keygen = false;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("@@user ", StringComparison.Ordinal)) user = line[7..].Trim();
            else if (line.StartsWith("@@uid ", StringComparison.Ordinal)) uid = line[6..].Trim();
            else if (line.StartsWith("@@home ", StringComparison.Ordinal)) home = line[7..].Trim();
            else if (line.StartsWith("@@sshd ", StringComparison.Ordinal)) sshd.Add(line[7..]);
            else if (line.StartsWith("@@root ", StringComparison.Ordinal)) rootLogin = SshdSettings.Parse([line[7..]]).PermitRootLogin;
            else if (line == "@@policy") policy = true;
            else if (line == "@@rootpolicy") rootPolicy = true;
            else if (line == "@@dropins") dropIns = true;
            else if (line == "@@keygen") keygen = true;
        }
        return new StatusOutput(user, uid, home, SshdSettings.Parse(sshd), policy, dropIns, keygen, rootLogin, rootPolicy);
    }

    /// <summary>Linux user names the app creates: lower-case, like useradd's default NAME_REGEX, and never root.</summary>
    public static string? ValidateNewUserName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_-]{0,31}$") && name != "root"
            ? null
            : $"User name '{name}': 1-32 lower-case letters, digits, '_' or '-', starting with a letter or '_' (and not root).";

    public static string SudoersFile(string user) => $"/etc/sudoers.d/lrw-{user}";

    /// <summary>
    /// Creates <paramref name="user"/> with a home directory and bash, no usable password ("*" - logins only with a key,
    /// yet not "locked", which sshd without PAM would refuse), and passwordless sudo through a sudoers.d file
    /// checked by visudo. Installs sudo when missing.
    /// </summary>
    public static string CreateAdminScript(string user)
    {
        var u = Shell.Quote(user);
        var sudoers = Shell.Quote(SudoersFile(user));
        return string.Join("\n",
            "set -e",
            $"if id -u {u} >/dev/null 2>&1; then echo {Shell.Quote($"User {user} already exists.")}; exit {ExitUserExists}; fi",
            "if ! command -v sudo >/dev/null; then",
            "  echo '→ Installing sudo'",
            "  { DEBIAN_FRONTEND=noninteractive apt-get install -y sudo || dnf install -y sudo || yum install -y sudo; } 2>&1",
            "fi",
            "grep -Eq '^[#@]includedir[[:space:]]+/etc/sudoers\\.d' /etc/sudoers || " +
            $"{{ echo '/etc/sudoers does not read /etc/sudoers.d.'; exit {ExitNoSudoersDir}; }}",
            $"useradd -m -s /bin/bash -c 'Admin (Linux Remote Worker)' {u}",
            $"usermod -p '*' {u}",
            "t=$(mktemp)",
            $"printf '%s\\n' {Shell.Quote($"{user} ALL=(ALL:ALL) NOPASSWD: ALL")} {Shell.Quote($"Defaults:{user} !requiretty")} > \"$t\"",
            "visudo -cf \"$t\" >/dev/null",
            $"install -m 440 -o root -g root \"$t\" {sudoers}",
            "rm -f \"$t\"");
    }

    public const int ExitUserExists = 9;
    public const int ExitNoSudoersDir = 10;

    /// <summary>Undoes <see cref="CreateAdminScript"/>: the user, its home and its sudoers file.</summary>
    public static string RemoveAdminScript(string user) =>
        $"pkill -u {Shell.Quote(user)} 2>/dev/null; userdel -r {Shell.Quote(user)} 2>/dev/null; rm -f {Shell.Quote(SudoersFile(user))}; true";

    /// <summary>Appends the keys in <paramref name="fromFiles"/> to another user's authorized_keys (which must exist).</summary>
    public static string CopyKeysScript(IEnumerable<string> fromFiles, string toFile, string user) => string.Join("\n",
        $"f={Shell.Quote(toFile)}",
        $"for s in {string.Join(' ', fromFiles.Select(Shell.Quote))}; do",
        "  [ -f \"$s\" ] || continue",
        "  if [ -s \"$f\" ] && [ -n \"$(tail -c 1 \"$f\")\" ]; then echo >> \"$f\"; fi",
        "  cat \"$s\" >> \"$f\"",
        "done",
        $"chown {Shell.Quote(user)}: \"$f\"");

    /// <summary>
    /// AuthorizedKeysFile entries as absolute paths: %h is the home directory, %u the user name, %U the
    /// numeric user id, %% a percent sign; relative paths start at the home directory; "none" is skipped.
    /// </summary>
    public static List<string> ExpandAuthorizedKeysFiles(IReadOnlyList<string> entries, string user, string uid, string home)
    {
        // Not reported at all: sshd's default
        if (entries.Count == 0) entries = [".ssh/authorized_keys"];

        var files = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            var path = entry.Replace("%%", "\0").Replace("%h", home).Replace("%u", user).Replace("%U", uid).Replace("\0", "%");
            if (!path.StartsWith('/')) path = home.TrimEnd('/') + "/" + path;
            if (!files.Contains(path)) files.Add(path);
        }
        return files;
    }

    public const string FileMarker = "@@file ";

    /// <summary>Fingerprints of every key in the given files, each file announced by <see cref="FileMarker"/>.</summary>
    public static string ListKeysScript(IEnumerable<string> files) =>
        $"for f in {string.Join(' ', files.Select(Shell.Quote))}; do [ -f \"$f\" ] || continue; " +
        $"echo \"{FileMarker}$f\"; ssh-keygen -lf \"$f\" 2>/dev/null; done; true";

    /// <summary>Reads "256 SHA256:abc… comment words (ED25519)" lines under their file markers.</summary>
    public static List<AuthorizedKey> ParseKeys(string output)
    {
        var keys = new List<AuthorizedKey>();
        var file = "";
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(FileMarker, StringComparison.Ordinal)) { file = line[FileMarker.Length..]; continue; }
            if (ParseFingerprint(line) is { } key) keys.Add(key with { File = file });
        }
        return keys;
    }

    public static AuthorizedKey? ParseFingerprint(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
            || !parts[1].Contains(':') || !parts[^1].StartsWith('(') || !parts[^1].EndsWith(')'))
            return null;
        var comment = string.Join(' ', parts[2..^1]);
        return new AuthorizedKey("", bits, parts[1], comment == "no comment" ? "" : comment, parts[^1][1..^1]);
    }

    /// <summary>A new Ed25519 key pair in a private temporary directory; prints the directory, public key and fingerprint.</summary>
    public static string CreateKeyScript(string comment) => string.Join("\n",
        "set -e",
        "umask 077",
        $"d=$(mktemp -d {KeyDirPrefix}XXXXXXXX)",
        $"ssh-keygen -q -t ed25519 -N '' -C {Shell.Quote(comment)} -f \"$d/id_ed25519\" </dev/null >/dev/null",
        "printf '@@dir %s\\n@@pub %s\\n@@fp %s\\n' \"$d\" \"$(cat \"$d/id_ed25519.pub\")\" \"$(ssh-keygen -lf \"$d/id_ed25519.pub\")\"");

    public static bool IsKeyDir(string dir) =>
        dir.StartsWith(KeyDirPrefix, StringComparison.Ordinal) && dir.Length > KeyDirPrefix.Length
        && dir[KeyDirPrefix.Length..].All(char.IsAsciiLetterOrDigit);

    /// <summary>Overwrites, then removes the temporary key directory.</summary>
    public static string RemoveKeyDirScript(string dir) =>
        $"shred -u {Shell.Quote(dir + "/id_ed25519")} 2>/dev/null; rm -rf -- {Shell.Quote(dir)}";

    /// <summary>
    /// Appends a public key to an authorized_keys file, creating it (and its directory) owned by the user
    /// with the permissions sshd insists on, and keeping the last existing line intact.
    /// </summary>
    public static string AuthorizeScript(string file, string user, string publicKey)
    {
        var (f, u, k) = (Shell.Quote(file), Shell.Quote(user), Shell.Quote(publicKey));
        return string.Join("\n",
            $"f={f}; d=$(dirname \"$f\")",
            $"[ -d \"$d\" ] || {{ mkdir -p \"$d\" && chmod 700 \"$d\" && chown {u}: \"$d\"; }} || exit 1",
            $"[ -e \"$f\" ] || {{ install -m 600 /dev/null \"$f\" && chown {u}: \"$f\"; }} || exit 1",
            "if [ -s \"$f\" ] && [ -n \"$(tail -c 1 \"$f\")\" ]; then echo >> \"$f\"; fi",
            $"printf '%s\\n' {k} >> \"$f\" || exit 1",
            "if command -v restorecon >/dev/null; then restorecon -R \"$d\" 2>/dev/null; fi",
            "true");
    }

    /// <summary>Removes exactly that public key line again (rollback of <see cref="AuthorizeScript"/>), keeping the file's owner and mode.</summary>
    public static string UnauthorizeScript(string file, string publicKey) => string.Join("\n",
        $"f={Shell.Quote(file)}",
        "[ -f \"$f\" ] || exit 0",
        $"t=$(mktemp) && grep -vxF -- {Shell.Quote(publicKey)} \"$f\" > \"$t\"; cat \"$t\" > \"$f\"; rm -f \"$t\"");

    public const string TestConfigCommand = Sshd + " -t 2>&1";

    public const string ReloadCommand =
        "systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || service ssh reload 2>/dev/null || service sshd reload 2>&1";

    public static readonly string RemovePolicyCommand = $"rm -f {PolicyFile}";

    public static readonly string RemoveRootPolicyCommand = $"rm -f {RootPolicyFile}";
}
