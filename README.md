# Linux Remote Worker

**A Windows desktop console for the Linux servers you actually run.**
Connect over SSH with a key, then install PostgreSQL, clone repositories, deploy .NET services as
systemd units, open firewall ports and read logs — from one window, without memorising a single
`systemctl` incantation.

<p align="left">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF%20(MVVM)-0078D4">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-blue">
  <img alt="SSH" src="https://img.shields.io/badge/transport-SSH%20key%20auth-6CCF97">
</p>

---

## Table of contents

- [What it does](#what-it-does)
- [Screens](#screens)
- [Getting started](#getting-started)
- [Connecting to a server](#connecting-to-a-server)
- [When a connection fails](#when-a-connection-fails)
- [The application log](#the-application-log)
- [What it puts on your server](#what-it-puts-on-your-server)
- [Modules in detail](#modules-in-detail)
- [Where your data is stored](#where-your-data-is-stored)
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
| 📁 **Repositories** | Install git, generate a deploy key, clone private repos, pull updates, remove clones |
| ⚙ **.NET Services** | Install the .NET SDK, publish a project from a cloned repo, write a systemd unit, then start / stop / restart / redeploy it |
| 🛡 **Firewall** | Enable or disable `ufw`, list rules, open and close ports |
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

- Windows 10 or newer
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — not needed if you use
  the self-contained build
- An SSH **private key** (the app does not do password authentication)

**On the Linux server**

- A `systemd` distribution — Debian/Ubuntu family is the best-tested
- SSH reachable on port 22
- A user with `sudo`-free root privileges (typically `root`) — the modules write to `/etc` and
  `/srv`, and manage systemd units

### Run it

```powershell
git clone https://github.com/<you>/linux-remote-worker.git
cd linux-remote-worker
dotnet run --project LinuxRemoteWorker
```

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
2. Fill in **HOST / IP**, **USERNAME** and **PRIVATE KEY PATH** (use *Browse* to pick the key file).
3. Add the key's **PASSPHRASE** if it has one.
4. Press **Connect**.

Press **💾 Save** to store the host, user and key path as a named profile. Profiles appear in the
**SAVED SERVERS** list on the left of the connection screen; clicking one fills the form.
The passphrase is never saved.

**Key format:** an OpenSSH private key. A PuTTY `.ppk` file will not work — convert it first with
PuTTYgen (*Conversions → Export OpenSSH key*). If a modern
`-----BEGIN OPENSSH PRIVATE KEY-----` key gives trouble, convert it to PEM:

```bash
ssh-keygen -p -m PEM -f ~/.ssh/id_rsa
```

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
| Server refuses the key | *The server rejected the key* — check `~/.ssh/authorized_keys` for that user |
| Encrypted key, no passphrase | *The private key is encrypted and needs a passphrase* |
| Wrong passphrase or odd format | *Wrong key passphrase, or an unsupported key format* — with the `ssh-keygen -m PEM` fix |
| A `.ppk` or a non-key file | *The private key file could not be read* — with the PuTTYgen conversion steps |

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

```
2026-09-01 12:32:33.657 [INFO ] App started - v1.0.0.0
2026-09-01 12:32:33.659 [INFO ] OS: Microsoft Windows NT 10.0.19045.0 | 64-bit: True | user: Legion
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
> names — check a log before sharing it publicly. Passphrases and key contents are never written.

---

## What it puts on your server

The app keeps everything it owns under one root, so you can always see — and remove — what it did.

```
/srv/lrw/
├── repos/                 git clones
├── apps/                  published .NET output
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

To remove the app's footprint entirely:

```bash
systemctl disable --now 'lrw-*.service'
rm -f /etc/systemd/system/lrw-*.service && systemctl daemon-reload
rm -rf /srv/lrw
userdel lrw
```

---

## Modules in detail

### 🖥 System Info
Hostname, distribution, kernel, uptime, load, CPU model and core count, memory and disk usage, and
every network interface with its addresses.

### 🐘 PostgreSQL
Install the server package, then manage it:

- edit `listen_addresses` and restart the service
- view, add and remove `pg_hba.conf` rules
- create and drop databases, create users, change passwords, drop users
- grant a user access to a database
- generate a connection string from the current selection and copy it to the clipboard

### 📁 Repositories
Install git if missing, generate an SSH **deploy key** and copy the public half to add on
GitHub/GitLab, clone private repositories into `/srv/lrw/repos`, pull updates, and delete clones.
Git operations use the deploy key explicitly, so they never depend on the root user's own SSH
configuration.

### ⚙ .NET Services
The deployment pipeline:

1. Install (or remove) the .NET SDK.
2. Pick a cloned repository, then a `.csproj` inside it.
3. **Deploy** — publishes the project to `/srv/lrw/apps/<app>` and writes a systemd unit that runs
   it as the `lrw` user.
4. Start, stop, restart, or **redeploy** (pull + republish + restart) from the service list.
5. Edit the generated unit file in place, or read the service's journal — last hour, last day, or a
   live tail.

### 🛡 Firewall
Enable or disable `ufw`, list the active rules, add a rule (port, protocol, allow/deny), and delete
a rule.

### 📋 Server Logs
For any `lrw-*` unit: `journalctl` for the last hour, the last day, or live. Also lists the file
logs under `/srv/lrw/logs/<app>/` — view the last 1000 lines or download a file to your PC. The
screen shows the exact log path to point Serilog/NLog at, with a copy button, and makes sure the
directory exists and is writable by the service user.

---

## Where your data is stored

| Path | Contents |
| --- | --- |
| `%AppData%\LinuxRemoteWorker\profiles.json` | Saved servers: name, host, username, key **path** |
| `%AppData%\LinuxRemoteWorker\logs\app-*.log` | Application logs, kept for 14 days |

Private keys are never copied into the app's data; only the path to the file is stored. Passphrases
are held in memory for the lifetime of the connection and are never written to disk.

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

## Building from source

```powershell
git clone https://github.com/<you>/linux-remote-worker.git
cd linux-remote-worker
dotnet restore
dotnet build -c Release
dotnet run --project LinuxRemoteWorker
```

### Dependencies

| Package | Why |
| --- | --- |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH and SFTP transport |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Source-generated `ObservableObject` and `RelayCommand` |

---

## Project layout

```
LinuxRemoteWorker/
├── App.xaml(.cs)              Startup, global exception handlers, log bootstrap
├── MainWindow.xaml(.cs)       Shell: sidebar navigation + content host
├── Core/
│   ├── AppLog.cs              Thread-safe file logger, in-memory tail, retention
│   ├── ConnectionDiagnostics.cs  Preflight checks + exception → plain-language problem
│   ├── SshService.cs          SSH/SFTP session, command execution, streaming
│   ├── BootstrapService.cs    Idempotent server-side setup
│   ├── DeployPaths.cs         The single source of truth for server paths
│   ├── ProfileService.cs      Saved connection profiles (JSON)
│   └── IModule.cs             Contract every module view-model implements
├── ViewModels/
│   ├── BaseViewModel.cs       IsBusy / StatusMessage / HasError + RunSafeAsync
│   ├── ConnectViewModel.cs    Connection form, profiles, failure reporting
│   └── MainViewModel.cs       Owns the SSH session and every module
├── Views/                     ConnectView, InfoCard
├── Modules/
│   ├── AppLogs/               🐞 the app's own log viewer
│   ├── SystemInfo/  Postgres/  Repositories/  Services/  Firewall/  Logs/
├── Converters/                Value converters used across the XAML
└── Behaviors/AutoScroll.cs    Keeps log views pinned to the bottom
```

**Architecture in one paragraph.** One `SshService` instance lives in `MainViewModel` and is handed
to every module view-model, so all screens share a single SSH session. Views are matched to
view-models by WPF `DataTemplate`, so navigation is just `ActiveModule = someViewModel`. Module
view-models implement `IModule` and get a `LoadAsync` call when their screen opens. Long-running
work goes through `BaseViewModel.RunSafeAsync`, which flips `IsBusy`, catches everything, logs it,
and puts the message on screen instead of crashing the app.

### Adding a module

1. Create `Modules/<Name>/<Name>ViewModel.cs` deriving from `BaseViewModel` and implementing
   `IModule`.
2. Create `<Name>View.xaml` next to it.
3. Register the pairing in `MainWindow.xaml`:
   `<DataTemplate DataType="{x:Type ns:<Name>ViewModel}"><ns:<Name>View/></DataTemplate>`
4. Add the property to `MainViewModel` and a nav `Button` bound to `NavigateToCommand`.

---

## Security notes

- **Key authentication only.** There is no password login path.
- **Passphrases stay in RAM.** They are never written to `profiles.json` or the log.
- **Root access is real access.** This app installs packages, edits `/etc`, and manages systemd
  units. Point it only at servers you are responsible for.
- **The deploy key is server-side.** `/srv/lrw/keys/git_deploy` never leaves the server; only its
  public half is shown for you to paste into your git host.
- **Host key checking** is not enforced on the initial SSH connection — use it on networks you
  trust.
- **Logs may contain command text**, including database and user names. Review before sharing.

---

## Troubleshooting

**"I press Connect and nothing happens."**
It is happening — the connection has a 20 second timeout. If it fails, the reason appears in a red
panel right below the button.

**"It says the server rejected the key."**
The network is fine and the key was read; the server refused the login. Check that the public key is
in `~/.ssh/authorized_keys` for that exact user, that the file is `chmod 600` and its directory
`chmod 700`, and that `PubkeyAuthentication yes` is set in `/etc/ssh/sshd_config`.

**"Connection timed out."**
Nothing answered on port 22. Check the cloud security group / firewall allows your IP, that the
machine is running, and that your VPN is connected. `ssh user@host` from a terminal is the fastest
confirmation.

**"Invalid private key file."**
The file is not an OpenSSH private key. `.ppk` files need converting with PuTTYgen, and `.pub` files
are the wrong half of the pair.

**"A module says 'Not connected'."**
The SSH session dropped. Go back to **🔗 Connection** and reconnect — the **🐞 Application Log**
shows when and why the session ended.

**Anything else** — open **🐞 Application Log**, filter for `ERROR`, and read the last few lines.
That file is designed to answer this question.

---

## Roadmap

- [ ] Non-standard SSH ports and jump hosts
- [ ] Host key verification with a known-hosts store
- [ ] Nginx / reverse proxy module
- [ ] Let's Encrypt certificate management
- [ ] Docker container module
- [ ] Scheduled backups for PostgreSQL databases

---

## License

Not yet specified — add a `LICENSE` file before publishing.
