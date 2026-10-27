# Linux Remote Worker

**A Windows desktop console for the Linux servers you actually run.**
Connect over SSH with a key, then install PostgreSQL, clone repositories, deploy .NET services as
systemd units, put them behind nginx with free HTTPS, open firewall ports and read logs — from one
window, without memorising a single `systemctl` incantation.

The same operations are available from the command line as **`lrw`** — for scripts, and for AI
agents through a Claude skill and an MCP server. See [Command line & AI agents](#command-line--ai-agents).

<p align="left">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF%20(MVVM)-0078D4">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-blue">
  <img alt="SSH" src="https://img.shields.io/badge/transport-SSH%20key%20auth-6CCF97">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-green"></a>
</p>

## Quick install

**Desktop app** (Windows 10+): download and run
[LinuxRemoteWorker-win-Setup.exe](https://github.com/JustDimus/linux-remote-worker/releases/latest/download/LinuxRemoteWorker-win-Setup.exe).
No administrator rights, no .NET, and it keeps itself up to date ([details](#install)). Uninstall it under
*Settings → Apps*.

**Command line `lrw`**, plus its MCP server and skill when Claude Code is installed: one line. Run it again to
update, or to connect lrw to a Claude Code installed later.

```powershell
# Windows (PowerShell)
irm https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.ps1 | iex
```

```bash
# macOS, Ubuntu and other Linux
curl -fsSL https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.sh | bash
```

To remove `lrw` with its PATH entry, MCP registration and skill (saved servers stay):

```powershell
# Windows (PowerShell)
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.ps1))) -Uninstall
```

```bash
# macOS, Ubuntu and other Linux
curl -fsSL https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.sh | bash -s -- --uninstall
```

Options and other ways to install: [docs/cli.md](docs/cli.md#install).

---

## Table of contents

- [Quick install](#quick-install)
- [What it does](#what-it-does)
- [Screens](#screens)
- [Getting started](#getting-started)
- [Connecting to a server](#connecting-to-a-server)
- [When a connection fails](#when-a-connection-fails)
- [The application log](#the-application-log)
- [What it puts on your server](#what-it-puts-on-your-server)
- [Modules in detail](#modules-in-detail)
- [Command line & AI agents](#command-line--ai-agents)
- [Where your data is stored](#where-your-data-is-stored)
- [Releasing](#releasing)
- [Building from source](#building-from-source)
- [Project layout](#project-layout)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)
- [Roadmap](#roadmap)

---

## What it does

Linux Remote Worker is an SSH client with opinions. Instead of a blank terminal it gives you a
screen per job, and each screen runs the same commands a competent sysadmin would type by hand.

| Module | What you get |
| --- | --- |
| 🖥 **System Info** | OS, kernel, uptime, CPU, RAM, disk and network interfaces at a glance |
| 🐘 **PostgreSQL** | Install the server, edit `listen_addresses` and `pg_hba.conf`, manage users and databases, grant access, and generate a ready-to-paste connection string |
| 📁 **Repositories** | Install git, generate a deploy key, clone private repos at a branch, tag, commit or the latest release, update or switch versions, remove clones |
| ⚙ **.NET Services** | Install the .NET SDK, publish a project from a cloned repo, write a systemd unit, then start / stop / restart / redeploy it — at the newest or any chosen version, as one or several instances |
| 🌐 **Nginx & SSL** | Install nginx, create reverse-proxy or static sites, edit/enable/disable/delete them safely, get Let's Encrypt certificates, watch expiry and renew |
| 🛡 **Firewall** | Enable or disable `ufw`, list rules, open and close ports |
| 🔐 **Security** | See how the server lets users log in, create an SSH key from a password login, turn password logins off |
| 📋 **Server Logs** | `journalctl` for any managed unit (last hour, last day, or live tail) plus the app's own file logs, viewable and downloadable |
| 🐞 **Application Log** | This app's own diagnostic log — always available, even before you connect |

Everything is idempotent: re-running an action on an already-configured server is safe.

---

## Screens

```
┌────────────────────────┬──────────────────────────────────────────────────────┐
│ Linux Remote Worker    │                                                      │
│ → 203.0.113.10         │   SSH Connection                                     │
│                        │   Connect to your Linux server using an SSH key       │
│ 🔗 Connection          │                                                      │
│                        │   HOST / IP      [ 203.0.113.10             ]        │
│ MODULES                │   USERNAME       [ root                     ]        │
│ 🖥  System Info        │   PRIVATE KEY    [ C:\keys\id_rsa  ] [Browse]        │
│ 🐘 PostgreSQL          │   PASSPHRASE     [ •••••••                  ]        │
│ 📁 Repositories        │                                                      │
│ ⚙  .NET Services       │   [        Connect        ] [ 💾 Save ]              │
│ 🌐 Nginx & SSL         │                                                      │
│ 🛡  Firewall           │                                                      │
│ 📋 Server Logs         │   ⚠ The server rejected the key                      │
│                        │     Check that the matching public key is in         │
│ DIAGNOSTICS            │     ~/.ssh/authorized_keys of user 'root' …          │
│ 🐞 Application Log     │     [📄 Open log file] [📋 Copy details]             │
│                        │                                                      │
│ ● Connected            │                                                      │
└────────────────────────┴──────────────────────────────────────────────────────┘
```

---

## Getting started

### Requirements

**On your Windows machine**

- Windows 10 or newer (the installer brings its own .NET runtime)
- An SSH **private key**, or the user's **password** (Security can then make a key for you)

**On the Linux server**

- A `systemd` distribution — Debian/Ubuntu family is the best-tested
- SSH reachable on port 22
- `root`, or a user with passwordless `sudo` (Security can create one) — the modules write to `/etc` and
  `/srv`, and manage systemd units

### Install

Download `LinuxRemoteWorker-win-Setup.exe` from the [latest release](https://github.com/JustDimus/linux-remote-worker/releases/latest)
and run it. The app installs for your Windows user into `%LocalAppData%\LinuxRemoteWorker` (no administrator
rights), with shortcuts on the desktop and in the Start menu and an entry under *Settings → Apps* to uninstall it. Windows SmartScreen may
warn once, because the installer is not code-signed — *More info → Run anyway*.

**It keeps itself up to date.** At every start and every few hours the app looks at the GitHub releases; a newer
version is downloaded in the background, and the sidebar shows *⟳ Restart to update* — otherwise it is installed
when you close the app, so the next start is the new version. Saved servers and logs (`%AppData%\LinuxRemoteWorker`) are never touched by updates or by
uninstalling.

- `LinuxRemoteWorker.exe --no-update` switches the checks off for that run (and does not apply a downloaded update).
- **Updates are signed.** Each release carries the update's SHA256, signed with a private key that only the
  release workflow holds (an ECDSA P-256 key in the repository's secrets); the app checks that signature with the
  public key built into it before it downloads anything, and the download is checked against the signed hash.
  A file swapped in a release by anyone without that key is refused.

### Run from source

```powershell
git clone https://github.com/<you>/linux-remote-worker.git
cd linux-remote-worker
dotnet run --project LinuxRemoteWorker
```

A build that was not installed by the Setup.exe does not update itself (the sidebar says so).

### Publish a single-file build

```powershell
dotnet publish LinuxRemoteWorker -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o .\publish
```

The result is one `LinuxRemoteWorker.exe` in `.\publish` that runs on a machine with no .NET
installed.

---

## Connecting to a server

1. Open **🔗 Connection**.
2. Fill in **HOST / IP** and **USERNAME**.
3. Under **SIGN IN WITH** choose
   - **🔑 SSH key** — pick the **PRIVATE KEY PATH** (*Browse*) and add its **PASSPHRASE** if it has one, or
   - **🔒 Password** — type the **PASSWORD**; tick *Remember password* to keep it with the profile.
4. Press **Connect**.

Press **💾 Save** to store the host, user and login method as a named profile. Profiles appear in the
**SAVED SERVERS** list on the left of the connection screen; clicking one fills the form.
Key passphrases are never saved. A remembered password is encrypted with Windows DPAPI for your
Windows account, so only you, on this computer, can use it.

**Server identity.** The server's SSH host key is remembered on the first successful login
(`known_hosts.json`). If it is different later, the app refuses to connect and shows both
fingerprints: expected after a reinstall, otherwise a sign that someone may be intercepting the
connection. *Trust the new server key* accepts it once you have checked it.

**Key formats:** OpenSSH (`-----BEGIN OPENSSH PRIVATE KEY-----`), PEM (`-----BEGIN RSA PRIVATE KEY-----`,
`-----BEGIN EC PRIVATE KEY-----`) and PuTTY `.ppk` files, with RSA, ECDSA or Ed25519 keys, encrypted or
not. DSA keys are not supported.

---

## When a connection fails

The connection screen tells you **what** went wrong and **what to do about it**, instead of showing
a raw exception. Before dialling out, the app checks the obvious things (empty host, missing key
file, a `.pub` file selected by mistake); if the dial-out itself fails, the exception is translated
into plain language:

| What happened | What you see |
| --- | --- |
| DNS cannot resolve the host | *Host name could not be resolved* — check the spelling or use the IP |
| Nothing listening on port 22 | *Connection refused on port 22* — is `sshd` running, or on another port? |
| No answer at all | *The server did not answer in time* — firewall, cloud security group, or VPN |
| IPv6 host, IPv4-only network | *This computer has no IPv6 route to the server* — use the server's IPv4 address |
| Server refuses the key | *The server rejected the key* — check `~/.ssh/authorized_keys` for that user |
| Wrong password or user | *The server rejected the password* — root password logins are often forbidden |
| Server takes keys only | *This server does not accept passwords* — log in with a key |
| Server's host key changed | *The server's identity changed* — both fingerprints, and *Trust the new server key* once checked |
| Encrypted key, no passphrase | *The private key is encrypted and needs a passphrase* |
| Wrong passphrase | *Wrong key passphrase, or an unsupported key format* — with `ssh-keygen -y -f <key>` to test the passphrase |
| A non-key file or a DSA key | *The private key file could not be read* — with the list of supported formats |

Each message comes with the raw technical detail underneath, plus two buttons:

- **📄 Open log file** — opens the full log in your text editor
- **📋 Copy details** — copies host, user, key path, error, hint and log path to the clipboard,
  ready to paste into a chat or an issue

---

## The application log

Every run is written to a dated file, and the sidebar has a **🐞 Application Log** entry that is
enabled *even when you are not connected* — that is the point of it.

```
%AppData%\LinuxRemoteWorker\logs\app-YYYY-MM-DD.log
```

The command line writes its own `cli-YYYY-MM-DD.log` (and `mcp-YYYY-MM-DD.log` when serving MCP)
next to it, so the app and an agent never write to the same file; the viewer lists all of them.

```
2026-09-01 12:32:33.657 [INFO ] App started - v1.0.0.0
2026-09-01 12:32:33.659 [INFO ] OS: Microsoft Windows NT 10.0.19045.0 | 64-bit: True | user: alice
2026-09-01 12:34:02.114 [INFO ] ---- Connect requested: root@203.0.113.10 ----
2026-09-01 12:34:02.180 [INFO ] Private key loaded
2026-09-01 12:34:22.204 [ERROR] SSH connect failed: root@203.0.113.10
2026-09-01 12:34:22.208 [WARN ] Connection problem: The server did not answer in time | …
```

The viewer gives you:

- a **file picker** for earlier days
- a **filter box** — type `ERROR`, a server IP, or a command to narrow the view
- **Follow new lines**, so entries appear as they are written
- **📂 Open folder**, **📄 Open in editor**, **📋 Copy path**, **⧉ Copy log**, **⬇ Save as…**
- **🗑 Clear** to empty the current file

What is recorded: startup and environment details, every connection attempt and its outcome, every
SSH command with its exit code and (truncated) output, profile changes, and every unhandled
exception with its full stack trace. Files older than **14 days** are deleted automatically at
startup.

> **Heads up:** the log records the shell commands the app runs. Those include database and user
> names — check a log before sharing it publicly. Passphrases, key contents and database passwords
> are never written (password commands are logged with `'***'`).

---

## What it puts on your server

The app keeps everything it owns under one root, so you can always see — and remove — what it did.

```
/srv/lrw/
├── repos/                 git clones
├── apps/                  published .NET output (+ .lrw-deploy.json: what each app runs)
├── keys/                  the git deploy key (chmod 700)
│   ├── git_deploy
│   └── git_deploy.pub
└── logs/<app>/            per-app file logs, owned by the service user
```

- **Service user:** `lrw` — a system account with no login shell and no home directory. It owns
  `/srv/lrw` and runs the deployed services.
- **systemd units:** named `lrw-<app>.service` in `/etc/systemd/system/`, so the app can tell its
  own units apart from everything else on the box.
- **Bootstrap:** the user, directory tree and permissions are created on demand and re-checked
  before each action. Running it twice changes nothing.

The **🌐 Nginx & SSL** module works in the standard Debian/Ubuntu nginx layout rather than under
`/srv/lrw`, because that is where nginx and certbot expect things:

| Path | What |
| --- | --- |
| `/etc/nginx/sites-available/<site>` | One file per site; created ones start with `# Created by LinuxRemoteWorker` |
| `/etc/nginx/sites-enabled/<site>` | Symlink that turns a site on |
| `/etc/nginx/conf.d/lrw-websocket.conf` | A tiny `map` used by proxied sites for WebSocket upgrades |
| `/var/www/<site>/` | Default root of static sites (a placeholder `index.html` is added only if missing) |
| `/var/log/nginx/<site>.access.log`, `.error.log` | Per-site logs |
| `/etc/letsencrypt/` | certbot's certificates, account and renewal settings |

To remove the app's footprint entirely:

```bash
systemctl disable --now 'lrw-*.service'
rm -f /etc/systemd/system/lrw-*.service && systemctl daemon-reload
rm -rf /srv/lrw
userdel lrw
```

Sites and certificates are ordinary nginx/certbot state — remove them from the Nginx screen (or with
`certbot delete --cert-name <name>`) if you no longer want them.

---

## Modules in detail

### 🖥 System Info
Hostname, distribution, kernel, uptime, load, CPU model and core count, memory and disk usage, and
every network interface with its addresses.

### 🐘 PostgreSQL
Install the server package, then manage it:

- edit `listen_addresses` and the port — applied with `ALTER SYSTEM` and a restart; if PostgreSQL does
  not come back within 15 seconds the previous values are restored automatically
- view, add and remove `pg_hba.conf` rules — the list is what PostgreSQL itself parsed
  (`pg_hba_file_rules`); a new rule PostgreSQL rejects is rolled back, a bare IP gets its `/32` or
  `/128`, and the local rule the app itself connects through cannot be removed
- create and drop databases (with confirmation), create users, change passwords, drop users.
  Names and passwords may contain any character: SQL is passed to `psql` on stdin, never through the
  shell. New users get `CREATEDB` (for migrations) but not `CREATEROLE`
- grant a user access to a database
- generate a connection string from the current selection and copy it to the clipboard

### 📁 Repositories
Install git if missing, generate an SSH **deploy key** and copy the public half to add on
GitHub/GitLab, clone private repositories into `/srv/lrw/repos`, and delete clones. Git operations
use the deploy key explicitly, so they never depend on the root user's own SSH configuration.

Every clone **follows** something, chosen when it is cloned and changeable later (**⇄ Switch**):

| Follows | Clone checks out | **⟳ Update** moves it to |
| --- | --- | --- |
| a branch (default: the default branch) | its newest commit | the branch's newest commit — fast-forward only |
| the latest release | the highest version tag (`v1.4.2`, `2.0`, `api/v1.4.0`…), pre-releases only if asked | a newer release, when there is one |
| a tag | that tag | nowhere — a fixed version |
| a commit | that commit | nowhere — a fixed version |

**Load branches & tags** reads the repository (without cloning) and offers its branches and tags,
newest version first. The choice is stored in the clone itself (`git config lrw.track`), so the app,
`lrw` and the next update all agree; clones made before this follow whatever they have checked out.

Updates are careful: when tracked files were changed on the server, or a branch diverged from origin
(rewritten history), nothing is touched and the app asks whether to discard that and reset to origin.

**Two copies of one repository** — clone it again under another folder name, at another version
(say `shop-prod` on the latest release and `shop-staging` on `develop`). Each clone is independent.

### ⚙ .NET Services
The deployment pipeline:

1. Install (or remove) the .NET SDK.
2. Pick a cloned repository, then a `.csproj` inside it.
3. **Deploy** — publishes the project to `/srv/lrw/apps/<app>` and writes a systemd unit that runs
   it as the `lrw` user, with `ASPNETCORE_URLS` and `ASPNETCORE_ENVIRONMENT` (default `Production`).
   A port that another deployed service is set up for, or that something already listens on, is
   refused with a free one suggested.
4. Start, stop, restart, or **redeploy** (republish what the clone has checked out + restart) from
   the service list. The list shows what each service runs: version, commit, clone, environment.
5. In **⚙ Manage**: **Update & redeploy** moves the clone to the newest version of what it follows,
   then rebuilds; **Switch & redeploy** checks out any branch, tag, release or commit first — which is
   also how you roll back to an older tag.
6. Edit the generated unit file in place, or read the service's journal — last hour, last day, or a
   live tail.

**Several instances of one app:** clone the repository once per instance (see Repositories), then
deploy each clone under its own app name and port — e.g. `shop-prod` on `http://127.0.0.1:5000` and
`shop-staging` on `http://127.0.0.1:5001` with environment `Staging`. They are separate services with
separate versions, logs and units.

### 🌐 Nginx & SSL
Install nginx, then run it the way a careful admin would. **Every change is a transaction:** the app
writes the change, runs `nginx -t` on the whole configuration, and reloads only if the test passes.
If it fails, the previous files are put back and nginx keeps serving the old configuration — a typo
never takes your sites down. The exact `nginx -t` output is shown on screen.

- **Service:** version and state, *Test config*, *Reload*, *Restart*, *Start*, *Stop*. Reload,
  restart and start refuse to run while the configuration test fails.
- **Sites:** every file in `sites-available` with its domains, ports, target and HTTPS state. Enable,
  disable (the file stays), edit in place, or delete. Links in `sites-enabled` are recognised even
  when they carry a different name.
- **New site:** a reverse proxy — pick one of your deployed .NET services and its port is filled in
  from `ASPNETCORE_URLS` — with forwarded headers and optional WebSocket/SignalR support, or a
  static site with an optional single-page-app fallback (its root must live in `/var/www`, `/srv` or
  `/opt`, so a typo can never publish `/`, `/etc` or the deploy key). The app warns when nothing listens on the
  upstream port yet (the classic *502 Bad Gateway*). In ASP.NET Core, call
  `app.UseForwardedHeaders()` so the app sees the real client IP and the `https` scheme.
- **nginx.conf:** the global configuration opens in the same safe editor.
- **SSL certificates (Let's Encrypt):** installs `certbot` with its nginx plugin, then *🔒 HTTPS* on
  a site does the standard `certbot --nginx` flow: certificate issued, TLS added to that site, HTTP
  redirected to HTTPS. Before contacting Let's Encrypt the app checks that every domain resolves
  (a failed validation counts against rate limits); a *Dry run* option uses the staging server.
- **Certificate list:** domains, expiry and days left (amber inside the 30-day renewal window, red
  when close or expired), which sites use each certificate, *Renew now*, *Renew all that are due*,
  *Test renewal (dry run)*, and delete — refused while a config still points at the certificate,
  because nginx would then fail to start.
- **Auto-renewal:** shows whether certbot's timer is active and when it runs next, and can enable it.
- **Firewall hint:** if `ufw` is active and blocks port 80 or 443, one click opens both.
- **Logs:** the last 200 lines of any file in `/var/log/nginx`.

### 🛡 Firewall
Enable or disable `ufw`, list the active rules, add a rule (port or range, protocol, allow/deny,
optional source address), and delete a rule. Deleting works by rule number, so source-limited rules,
app profiles and commented rules are removed too, together with their IPv6 twin. Every port `sshd`
listens on — starting with the one the app is connected through — is allowed before the firewall is
enabled, and rules that let SSH in cannot be deleted from the app.

### 🔐 Security
How the server lets users in, and the way to key-only logins without root:

- **This connection** — logged in with a key or a password, as root or through sudo, and the server's
  host key fingerprint.
- **SSH login settings** — the effective `PasswordAuthentication`, `KbdInteractiveAuthentication`,
  `PubkeyAuthentication` and `PermitRootLogin` (from `sshd -T`), and the keys that can log in as this user.
- **Use a key you already have** — reads the public half of your private key (OpenSSH, PEM or PuTTY), adds
  it to `authorized_keys` and logs in with it; the private key never leaves your PC.
- **Create an SSH key** — makes an Ed25519 key pair on the server, saves the private key on your PC
  (readable only by your Windows user), adds the public key to `authorized_keys`, deletes the key files
  from the server and logs in with the key to prove it works; on any failure nothing is left behind.
  *Use this key for this connection* then switches the profile to the key and reconnects.
- **Let another device in** — paste the public key of your Mac or another PC (`cat ~/.ssh/id_ed25519.pub`
  there, or PuTTYgen's SSH2 block), see its fingerprint before adding, choose the user it logs in as. Its
  private key never leaves that device. The key list shows every key that can log in; ✕ removes one (a lost
  device) — except the key this session uses, and never the last key while password login is off.
- **Disable password login** — only from a session that logged in with a key, so the key is proven
  before passwords stop working. The app writes `/etc/ssh/sshd_config.d/00-lrw-password-login.conf`, checks
  it with `sshd -t`, confirms it took effect, reloads sshd (open sessions stay connected) and tests a
  fresh key login — and undoes everything if any step fails. *Allow password login again* removes the file.
- **Admin user instead of root** — creates a Linux user with passwordless sudo (`/etc/sudoers.d/lrw-<user>`,
  checked by `visudo`) and no usable password, makes it an SSH key (saved as
  `lrw_<server>_<user>_ed25519` in your `.ssh` folder), optionally lets in the keys that log in as you now,
  then logs in as it and runs `sudo` to prove both work — or removes the user again. *Switch this connection
  to …* moves the profile to the new user.
- **Disable root login** — `PermitRootLogin no` in `/etc/ssh/sshd_config.d/00-lrw-root-login.conf`, allowed
  only from a key login of another user whose sudo works, with the same checks, test login and rollback.

**Working as a user with sudo.** When the login is not root, the app runs every command through
`sudo -n` (no password prompt — it fails instead) and moves files through commands rather than SFTP, so
every screen works the same as with root. A user without passwordless sudo can connect, but changes
fail; the connection screen and Security say so.

### 📋 Server Logs
For any `lrw-*` unit: `journalctl` for the last hour, the last day, or live. Also lists the file
logs under `/srv/lrw/logs/<app>/` — view the last 1000 lines or download a file to your PC. The
screen shows the exact log path to point Serilog/NLog at, with a copy button, and makes sure the
directory exists and is writable by the service user.

---

## Command line & AI agents

`lrw` is the command-line front end of the same code: every module above is a command group, it reads
the same saved servers, and it keeps the same safety rules (nginx changes tested and rolled back,
SSH never firewalled off, destructive commands refused without `--yes`).

```powershell
# Install (or update) lrw and register it in Claude Code; macOS / Linux: scripts/install-cli.sh, see docs/cli.md
irm https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.ps1 | iex

lrw profiles add prod --host 203.0.113.10 --key C:\keys\id_ed25519 --make-default
lrw connection test
lrw services deploy --repo shop --csproj src/Shop.Api/Shop.Api.csproj --name shop-api --urls http://127.0.0.1:5000
lrw nginx sites create --domains shop.example.com --service shop-api
lrw certs secure-site shop.example.com --email me@example.com
lrw logs journal shop-api --since "1 hour ago"
```

On a terminal it prints tables; piped, it prints one JSON result per command with stable exit codes —
the form agents need. For Claude:

- **Skill** — [`skill/`](skill/SKILL.md) teaches Claude the workflows and the safety rules (never confirm
  a destructive action on its own) and how to install, update and remove lrw. The install scripts put it
  into `~/.claude/skills/`.
- **MCP server** — `lrw mcp` exposes every command as a tool. The install scripts register it; by hand:
  `claude mcp add --scope user lrw -- lrw mcp --profile prod`.

Full documentation: [docs/cli.md](docs/cli.md).

---

## Where your data is stored

| Path | Contents |
| --- | --- |
| `%AppData%\LinuxRemoteWorker\profiles.json` | Saved servers: name, host, username, login method, key **path**, a remembered password encrypted with DPAPI (shared by the app and `lrw`) |
| `%AppData%\LinuxRemoteWorker\known_hosts.json` | Host key fingerprints of the servers you connected to (shared by the app and `lrw`) |
| `%AppData%\LinuxRemoteWorker\logs\*.log` | Logs of the app (`app-`), the CLI (`cli-`) and the MCP server (`mcp-`), kept for 14 days |
| `%AppData%\LinuxRemoteWorker\cli.json` | The CLI's default profile (`lrw profiles use`) |

Private keys are never copied into the app's data; only the path to the file is stored. Passphrases
are held in memory for the lifetime of the connection and are never written to disk; passwords too,
unless *Remember password* stores them encrypted for your Windows account.

Both paths are printed at the top of every log file, so they are recoverable even when the UI will
not start. In the app, the **SAVED SERVERS** panel on the connection screen shows the profiles path
and has a **📂** button that opens it in Explorer; the **🐞 Application Log** screen does the same
for the log folder.

`profiles.json` is plain JSON — safe to back up, copy between machines, or edit by hand:

```json
[
  {
    "Id": "8f1c...",
    "Name": "prod-01",
    "Host": "203.0.113.10",
    "Username": "root",
    "PrivateKeyPath": "C:\\keys\\id_rsa"
  }
]
```

Copying it to another machine carries the *paths*, not the keys — the key files have to travel
separately, and if they land somewhere else you will need to re-point each profile with *Browse*.
A damaged file will not stop the app: it is logged as an error and the list simply comes up empty.

---

## Releasing

*Actions → **Release** → Run workflow* (from `main`), choose **patch**, **minor** or **major**. The workflow takes
the version from `Directory.Build.props` (the one version of the app, `lrw` and the libraries), runs the unit
tests on Linux and Windows, builds `lrw` for six platforms and the app installer, signs the update — and only when
all of that worked commits the new version (`Release vX.Y.Z`, in your name), tags it and publishes the release. If anything
fails, the repository stays as it was. Pushing a tag `vX.Y.Z` by hand still works and releases that exact version.

**Once, before the first release with the app:** create the update signing key on your own computer —

```powershell
pwsh -File tools/new-update-signing-key.ps1
```

It writes the public key to `LinuxRemoteWorker/Update/update-signing.pub.pem` (commit it) and puts the private key
on the clipboard only: add it as the repository secret `UPDATE_SIGNING_KEY` (*Settings → Secrets and variables →
Actions*) and keep a copy in your password manager. The release refuses to run without both, or when they do not
belong together. Replacing the key later means apps installed before cannot verify new updates and must be
reinstalled once from a Setup.exe.

## Building from source

```powershell
git clone https://github.com/<you>/linux-remote-worker.git
cd linux-remote-worker
dotnet restore
dotnet build -c Release
dotnet run --project LinuxRemoteWorker
dotnet run --project LinuxRemoteWorker.Cli -- help      # the command line
dotnet test                                             # unit tests (no server needed)
```

The unit tests also run on every push (`.github/workflows/tests.yml`, Linux and Windows).

### Dependencies

| Package | Why |
| --- | --- |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH and SFTP transport |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Source-generated `ObservableObject` and `RelayCommand` |
| [Velopack](https://github.com/velopack/velopack) | Installer and automatic updates of the desktop app from GitHub releases |

---

## Project layout

```
LinuxRemoteWorker.Core/            net8.0 library, no UI — everything that talks to a server
├── Core/
│   ├── AppLog.cs                  Thread-safe file logger (app- / cli- / mcp- files), in-memory tail, retention
│   ├── ConnectionDiagnostics.cs   Preflight checks + exception → plain-language problem
│   ├── SshService.cs              SSH/SFTP session, command execution (with exit codes), streaming, transfers
│   ├── CommandResult.cs           Exit code + stdout/stderr of a remote command
│   ├── OperationException.cs      A failure with a user-facing message and a kind (invalid/refused/not found/failed)
│   ├── Shell.cs                   Shell quoting and verbatim file writes (quoted heredoc)
│   ├── BootstrapService.cs        Idempotent server-side setup
│   ├── DeployPaths.cs             The single source of truth for server paths
│   └── ProfileService.cs          Saved connection profiles (JSON)
└── Modules/                       One service per module: the server logic both front ends share
    ├── SystemInfo/  Postgres/  Repositories/  Services/  Firewall/  Logs/
    └── Nginx/                     sites, safe config edits, certbot certificates
LinuxRemoteWorker/                 WPF desktop app
├── Program.cs                     Entry point: Velopack's install/update hooks, then the WPF app
├── App.xaml(.cs)                  Startup, global exception handlers, log bootstrap
├── MainWindow.xaml(.cs)           Shell: sidebar navigation + content host
├── Assets/                        app.ico (exe, window, installer) and app.png (sidebar)
├── Update/UpdateService.cs        Signed automatic updates from the GitHub releases
├── Core/IModule.cs                Contract every module view-model implements
├── ViewModels/
│   ├── BaseViewModel.cs           IsBusy / StatusMessage / HasError + RunSafeAsync
│   ├── ConnectViewModel.cs        Connection form, profiles, failure reporting
│   └── MainViewModel.cs           Owns the SSH session and every module
├── Views/                         ConnectView, InfoCard, GitSourcePickerView (branch / tag / release / commit)
├── Modules/                       <Name>View.xaml + <Name>ViewModel.cs per screen (AppLogs: the log viewer)
├── Converters/                    Value converters used across the XAML
└── Behaviors/AutoScroll.cs        Keeps log views pinned to the bottom
LinuxRemoteWorker.Cli/             lrw.exe — command line and MCP server (see docs/cli.md)
├── Framework/                     Command catalog, parser, invoker, help/schema, text output
├── Session/                       Server selection, lazy SSH connection, cli.json
├── Mcp/McpServer.cs               MCP over stdio, generated from the same catalog
└── Commands/                      One class per command group, thin adapters over the Core services
LinuxRemoteWorker.Tests/           xUnit tests: git sources and releases, generated scripts, parsing, ports, CLI options
skill/                             Claude skill for operating servers with lrw (SKILL.md, reference.md)
.github/workflows/release.yml      Release: bump version, test, build lrw + the app installer, sign the update, publish
.github/workflows/tests.yml        Unit tests on every push and pull request (Linux and Windows)
.github/workflows/install-scripts.yml  Tries the install scripts on Ubuntu, macOS and Windows when they change
scripts/                           Install, update, status and removal of lrw + MCP + skill: install-cli.ps1 (Windows), install-cli.sh (macOS, Linux)
tools/                             Release helpers: next-version.sh, sign-update.sh, new-update-signing-key.ps1
```

**Architecture in one paragraph.** The server logic lives in `LinuxRemoteWorker.Core` as one service
per module (`PostgresService`, `NginxService`, `AppServiceManager`, …); the desktop app and `lrw`
are two front ends over it. In the app, one `SshService` instance lives in `MainViewModel` and is
handed to every module view-model, so all screens share a single SSH session. Views are matched to
view-models by WPF `DataTemplate`, so navigation is just `ActiveModule = someViewModel`. Module
view-models implement `IModule` and get a `LoadAsync` call when their screen opens. Long-running
work goes through `BaseViewModel.RunSafeAsync`, which flips `IsBusy`, catches everything, logs it,
and puts the message on screen instead of crashing the app.

### Adding a module

1. Put the server logic in `LinuxRemoteWorker.Core/Modules/<Name>/<Name>Service.cs`; report failures
   with `OperationException` so both front ends explain them the same way.
2. Create `Modules/<Name>/<Name>ViewModel.cs` deriving from `BaseViewModel` and implementing
   `IModule`, and `<Name>View.xaml` next to it.
3. Register the pairing in `MainWindow.xaml`:
   `<DataTemplate DataType="{x:Type ns:<Name>ViewModel}"><ns:<Name>View/></DataTemplate>`
4. Add the property to `MainViewModel` and a nav `Button` bound to `NavigateToCommand`.
5. Add `LinuxRemoteWorker.Cli/Commands/<Name>Commands.cs` with a `[CommandGroup]` — it becomes CLI
   commands and MCP tools automatically — and list the commands in the skill's `reference.md`.

---

## Security notes

- **Keys or passwords.** Passwords are kept only when you ask, encrypted with Windows DPAPI for your
  account; the Security screen moves a server to key-only logins without risking a lock-out.
- **Passphrases stay in RAM.** They are never written to `profiles.json` or the log; neither are passwords
  in plain text.
- **Root access is real access.** This app installs packages, edits `/etc`, and manages systemd
  units - as root or through sudo. Point it only at servers you are responsible for.
- **The deploy key is server-side.** `/srv/lrw/keys/git_deploy` never leaves the server; only its
  public half is shown for you to paste into your git host.
- **Updates are signed.** The app installs an update only when its hash is signed with the release key (see
  *Install*); `--no-update` turns updating off.
- **Host keys are checked.** A server's key is remembered on the first login (trust on first use, like
  OpenSSH's `accept-new`); a different key later is refused until you accept it.
- **Logs may contain command text**, including database and user names (never passwords). Review
  before sharing.
- **Input reaching the server is validated and quoted** in every module: app and folder names cannot
  contain `/` or `..`, URLs, branches, ports and addresses are checked, and values are single-quoted
  for the shell.
- **Nginx input is whitelisted.** Domains, site names, upstream URLs, paths and e-mail addresses are
  validated before they reach a config file or a shell command, and every value is shell-quoted.

---

## Troubleshooting

**"I press Connect and nothing happens."**
It is happening — the connection has a 20 second timeout. If it fails, the reason appears in a red
panel right below the button.

**"It says the server rejected the key."**
The network is fine and the key was read; the server refused the login. Check that the public key is
in `~/.ssh/authorized_keys` for that exact user, that the file is `chmod 600` and its directory
`chmod 700`, and that `PubkeyAuthentication yes` is set in `/etc/ssh/sshd_config`.

**"This computer has no IPv6 route to the server."**
The host is an IPv6 address (something like `2001:db8::1`) and your network is IPv4-only —
common on home and office connections. The same server will work from a machine that has IPv6, which
is what makes this confusing. Put the server's **IPv4** address in HOST / IP instead; hosting panels
list both.

**"Connection timed out."**
Nothing answered on port 22. Check the cloud security group / firewall allows your IP, that the
machine is running, and that your VPN is connected. `ssh user@host` from a terminal is the fastest
confirmation.

**"Invalid private key file."**
The file is not a private key in a supported format (OpenSSH, PEM or PuTTY `.ppk`; RSA, ECDSA or
Ed25519). `.pub` files are the wrong half of the pair, and DSA keys are no longer supported — create a
new Ed25519 key with `ssh-keygen -t ed25519`.

**"A module says 'Not connected'."**
The SSH session dropped. Go back to **🔗 Connection** and reconnect — the **🐞 Application Log**
shows when and why the session ended.

**"The site shows 502 Bad Gateway."**
nginx is fine; nothing answers on the upstream port. Start the service in **⚙ .NET Services** and
check that its `ASPNETCORE_URLS` port matches the site's `proxy_pass`.

**"The certificate request failed."**
Read the command output under the Nginx screen — certbot names the reason. Almost always the domain
does not point to this server yet, or port 80 is blocked by `ufw` or a cloud security group. Fix it,
then tick *Dry run* and try again before making a real request.

**"Saving a site says nginx -t failed."**
Nothing was changed on the server. The **NGINX OUTPUT** box shows the file and line nginx rejected.

**Anything else** — open **🐞 Application Log**, filter for `ERROR`, and read the last few lines.
That file is designed to answer this question.

---

## Roadmap

- [ ] Non-standard SSH ports and jump hosts
- [x] Host key verification with a known-hosts store
- [x] Password logins and a guided switch to key-only logins
- [x] Nginx / reverse proxy module
- [x] Let's Encrypt certificate management
- [x] Command line (`lrw`), Claude skill and MCP server
- [ ] Docker container module
- [ ] Scheduled backups for PostgreSQL databases

---

## License

[MIT](LICENSE): use, change and share it freely, commercially too; keep the copyright notice. No warranty.
