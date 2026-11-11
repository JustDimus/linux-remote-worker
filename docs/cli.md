# lrw — the command line and MCP server

`lrw` does everything the desktop app does, from a terminal, a script, or an AI agent. It uses the
same code (the `LinuxRemoteWorker.Core` library), the same saved servers (`profiles.json`), the same
server layout (`/srv/lrw`, `lrw-*.service`) and the same safety rules — a site edit that fails
`nginx -t` is rolled back, SSH is never firewalled off, the PostgreSQL rule the tool connects
through cannot be removed.

```
$ lrw --profile prod nginx sites create --domains shop.example.com --service shop-api
…
Site shop.example.com created and enabled
$ lrw --profile prod certs secure-site shop.example.com --email me@example.com
…
→ Checking DNS...
✓ shop.example.com → 203.0.113.10
→ Requesting the certificate from Let's Encrypt...
HTTPS is on for shop.example.com
```

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [Interactive mode](#interactive-mode)
- [Choosing the server](#choosing-the-server)
- [Output and exit codes](#output-and-exit-codes)
- [Safety](#safety)
- [Using it from Claude (skill and MCP)](#using-it-from-claude-skill-and-mcp)
- [Command groups](#command-groups)
- [How it is built](#how-it-is-built)
- [Limitations](#limitations)

## Install

`lrw` runs on Windows, Linux and macOS (x64 and arm64) as one self-contained file — no .NET needed. Two
scripts install it, one per system and each native to it: [`scripts/install-cli.ps1`](../scripts/install-cli.ps1)
for Windows PowerShell, [`scripts/install-cli.sh`](../scripts/install-cli.sh) for bash on macOS and Linux. They
do the same thing: download the release build for the OS and CPU, check it against the release's
`SHA256SUMS.txt`, put it on PATH and — with Claude Code installed — register the MCP server `lrw` and add the
[skill](#using-it-from-claude-skill-and-mcp). Nothing to clone, no administrator rights. Every release carries
both scripts, and the commands below run the newest release's copies. Later, [`lrw update`](#updating) updates
lrw; running a script again does the same and also connects lrw to a Claude Code installed later.

```powershell
# Windows (PowerShell)
$lrw = 'https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1'
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm $lrw | iex"                                # install
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm $lrw))) -Status"     # version, newest release, PATH, MCP, skill
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm $lrw))) -Uninstall"  # remove it all; saved servers and logs stay
```

On Windows the script runs in a fresh Windows PowerShell of its own (`powershell -NoProfile`), so nothing loaded in
your window - a profile, or modules such as PSReadLine with its own stand-ins for .NET types - can get in its way;
the same lines work from `cmd` with the address written out. Open a new terminal afterwards to find `lrw` on PATH.

```bash
# macOS, Ubuntu and other Linux (wget -qO- works in place of curl -fsSL)
lrw_sh=https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.sh
curl -fsSL "$lrw_sh" | bash                          # install, or update when installed
curl -fsSL "$lrw_sh" | bash -s -- --status
curl -fsSL "$lrw_sh" | bash -s -- --uninstall
```

| Option (Windows / bash) | Meaning |
|---|---|
| `-Version` / `--version <tag>` | a release such as `v0.1.0` instead of the newest |
| `-McpProfile` / `--mcp-profile <name>` | the saved profile the MCP server connects to by default |
| `-NoMcp` / `--no-mcp` | do not register the MCP server |
| `-NoSkill` / `--no-skill` | do not add the skill |
| `-NoPath` / `--no-path` | leave PATH alone |
| `-Force` / `--force` | download again even when that version is installed |

The program goes to `%LOCALAPPDATA%\Programs\lrw` (added to the user PATH) on Windows and to
`~/.local/share/lrw`, linked as `~/.local/bin/lrw` (with `~/.local/bin` added to the shell profile when it is
not on PATH), on Linux and macOS. Uninstalling on Windows asks to close Claude Code first. Registering the MCP
server needs the `claude` command on PATH — without it the script prints the `claude mcp add` command to run; a
registration that is already there stays as it is unless `-McpProfile` / `--mcp-profile` asks for a new one.

### Updating

```bash
lrw update --check               # is there a newer release? (--json: current, latest, updateAvailable)
lrw update                       # install it
lrw update --version v0.2.1      # a given release, also an older one
```

`lrw update` downloads the install script of the release it installs, checks it against that release's
`SHA256SUMS.txt` and runs it, so the program and the skill are replaced exactly as the install command does it;
`--no-skill`, `--no-mcp`, `--no-path` and `--mcp-profile` pass through. It updates the copy the install scripts put in place
and refuses any other, such as a development build. It works while lrw runs as an MCP server: open Claude
sessions keep the old version until they restart. `update` is a command of the command line, not an MCP tool.

Once a day, a command run at a terminal checks in the background whether a newer release exists and, if there is
one, ends with a line like `A new version of lrw is available: v0.2.2 (you have 0.2.1). Run: lrw update`.
Output for scripts, agents and MCP (JSON, pipes) never gets it, and `LRW_NO_UPDATE_CHECK=1` turns it off.

**Releases** are built by [`.github/workflows/release.yml`](../.github/workflows/release.yml) (see
*Releasing* in the README): self-contained single-file builds for win-x64, win-arm64, linux-x64,
linux-arm64, osx-x64 and osx-arm64, plus the install scripts, the skill package `lrw-skill.tar.gz` and
`version.txt`, all in `SHA256SUMS.txt`. [`install-scripts.yml`](../.github/workflows/install-scripts.yml) tries
both install scripts on Ubuntu, macOS and Windows whenever they change; for every release it installs the new
release (still a pre-release) and tries `lrw update` on it before the release becomes the latest one.

**Without installing:** `dotnet run --project LinuxRemoteWorker.Cli -- <arguments>`.

## Quick start

```bash
lrw profiles add prod --host 203.0.113.10 --key ~/.ssh/id_ed25519 --make-default
lrw connection test
lrw system info
lrw help                 # groups
lrw help nginx           # commands of a group
lrw help nginx sites create
```

Profiles added in the desktop app show up here and vice versa.

## Interactive mode

```bash
lrw interactive                  # or: lrw --profile prod interactive (connects right away)
```

The desktop app's screens in a terminal, for when you would rather not remember commands and options. On the left:
the server this session is connected to, and the modules. It starts on **Connection** - the saved servers; Enter (or
Connect) logs in to one and tests the connection, and only then do the server's modules appear: System, Services,
Repositories, .NET SDK, PostgreSQL, Nginx, Certificates, Firewall, Security and Logs. **All commands** and the
**App log** are always there.

On the right, the selected module shows what the server has - the machine's load, memory, disk and addresses, the
deployed services with their state and version, the nginx sites, the databases and roles, the firewall rules, the
keys that may log in. On top, the module's menu (Refresh, Deploy..., New site..., Add rule...); under a table, the
actions for its selected row (Status, Restart, Edit unit, Enable, Delete ...). The focus stays in the table: Up and
Down pick the row, Left and Right pick the action (it is lit), Enter runs it - the first action, the safe one, is
picked at the start. An action runs the same command as the command line, filled in with the selected row; when
the command needs more, its form opens first. After every action the screen reads the server again, and the status
line at the bottom says how it went.

**Editing in place.** Edit unit (Services), Edit and nginx.conf (Nginx) open the file of the server in an editor.
Save writes it with the command that tests and applies it - the unit is reloaded and the service restarted, an nginx
change is kept only when `nginx -t` passes. When that is refused, the reason shows under the text and the editor
stays open, so the mistake can be fixed and saved again.

**All commands** lists every command, searchable (typing anywhere in the window lands there). Enter opens a
command's form: one field per option (text, a switch, a choice, a few lines for config text and SQL), with what the
focused field is for and the command line the form adds up to (`$ lrw nginx sites create --domains
shop.example.com`), so the screens teach the commands too. Run checks every value the way the command line does
and keeps the form open, with the reason, while one is wrong. The result window shows the server's output as it
comes, then the same result the command line prints.

| Key | |
|---|---|
| Enter or Tab | from the modules into the screen (onto its table) |
| ↑ ↓ in a table | pick the row; Up on the first row goes to the menu on top |
| ← → in a table | pick the action for the row (it is lit); Enter runs it |
| ← → in the menu on top | along its buttons; Enter presses one, Down goes back to the table |
| Tab | the next table, menu button or field; the arrows never cross between the modules and the screen |
| Alt + underlined letter | press that button |
| / (or Ctrl+F) | search every command, from anywhere - in a text field / is just text; Ctrl+F does not reach lrw in VS Code. In All commands, Up from the first command goes to the search too |
| typing in the modules list | starts the same search |
| Esc | back: from a screen to the modules, out of a form or editor; stops a running command; from the modules it leaves |

Destructive commands ask before they run, with **No** focused and as the default, so Enter never runs one by
accident. A password or key passphrase the server asks for is typed into a dialog and kept for the session only.
One SSH connection serves the whole session and runs one command at a time; every action goes to the app log with
its command line (secrets as `***`).

It needs a terminal: with redirected input or output it refuses, exit code 2 - scripts and agents call the
commands themselves. It is built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) over the same catalog as
the commands, help and MCP tools. The frames and marks use characters every monospace font has, so it looks the same
in Windows Terminal, the classic console window, VS Code, macOS Terminal and Linux terminals.

## Choosing the server

In this order:

1. `--host <ip> --key <path> [--user root]` (or `LRW_HOST`, `LRW_KEY`, `LRW_USER`) — no profile needed
2. `--profile <name>` (or `LRW_PROFILE`)
3. the default profile, set with `lrw profiles use <name>` (stored in `%AppData%\LinuxRemoteWorker\cli.json`)
4. the only saved profile, when there is exactly one

An encrypted key's passphrase comes from `LRW_PASSPHRASE`, `--passphrase-env <VAR>` or
`--passphrase-file <path>`; on an interactive terminal you are asked for it. It is never stored.

**Password logins.** `--host` without `--key` logs in with a password, and so does a profile saved with
`profiles add … --password env:VAR` (stored encrypted with Windows DPAPI) or `--password-login` (not
stored). The password comes from `--password-file`, `--password-env <VAR>` or `LRW_PASSWORD`, then from
the profile; on an interactive terminal you are asked for it.

**Server identity.** Host keys are remembered on the first successful login in
`%AppData%\LinuxRemoteWorker\known_hosts.json`, shared with the desktop app; a different key later
refuses the connection (exit 3) until `lrw connection forget-host-key --yes`.

Put global options **before** the group when a command has an option of the same name — for example
`pg grant --user` is the database role, while `lrw --user ubuntu pg grant …` is the SSH user.

## Output and exit codes

On a terminal you get tables and plain text; when stdout is redirected (scripts, agents) you get
JSON. `--json` / `--output text` force either. Remote output of long operations (installs, builds,
certbot) streams to **stderr** while it runs, so stdout stays a single JSON document:

```json
{
  "ok": true,
  "command": "services deploy",
  "server": "root@203.0.113.10 (prod)",
  "message": "Service shop-api deployed and started",
  "data": { "appName": "shop-api", "unitName": "lrw-shop-api.service", "isActive": true, "isEnabled": true }
}
```

```json
{
  "ok": false,
  "command": "nginx sites edit",
  "server": "root@203.0.113.10 (prod)",
  "error": {
    "kind": "failed",
    "message": "/etc/nginx/sites-available/shop was not saved: nginx -t failed, so nothing was changed.",
    "output": "nginx: [emerg] unknown directive \"proxy_pas\" in /etc/nginx/sites-enabled/shop:14"
  }
}
```

| Exit | `error.kind` | Meaning |
|---|---|---|
| 0 | | success |
| 1 | `failed`, `error` | the operation failed on the server |
| 2 | `invalid` | bad command line or input — nothing was sent to the server |
| 3 | `connection` | could not connect, or the connection dropped (`error.hint` says why) |
| 4 | `refused` | a safety rule refused it, or a destructive command lacks `--yes` |
| 5 | `not-found` | the site, service, rule, file … does not exist |
| 130 | `cancelled` | Ctrl+C |

`lrw schema` prints every command with its parameters, types, defaults and allowed values as JSON.

## Safety

- **Destructive commands need `--yes`** — dropping databases or users, removing repositories,
  services, sites, certificates, pg_hba rules, firewall rules, uninstalling .NET, stopping nginx,
  disabling the firewall. Without it they exit with code 4 and change nothing.
- **Transactions where the server allows them** — nginx changes are tested with `nginx -t` and rolled
  back; `pg network` restores the previous settings if PostgreSQL does not come back within 15 s;
  `pg hba add` rolls back a rule PostgreSQL rejects; `firewall enable` allows every SSH port first.
- **Raw access is explicit** — `system exec`, `pg sql`, `files upload`, `nginx sites edit`, `nginx conf set`
  and `services unit set` can change anything; they are flagged as such in help and to MCP clients.
- **Logged locally** — every invocation and remote command goes to
  `%AppData%\LinuxRemoteWorker\logs\cli-<date>.log` (`mcp-<date>.log` for the MCP server); secret
  option values are written as `***`. `lrw applog read --source cli` shows it; the desktop app's
  Application Log screen lists these files too.

## Using it from Claude (skill and MCP)

There are two ways to give an agent this tool; they can be combined.

**Skill** — [`skill/`](../skill/SKILL.md) teaches Claude how to use the CLI safely: picking the server,
reading results, never adding `--yes` without the user's agreement, and step-by-step workflows (deploy
behind nginx with HTTPS, databases, logs, troubleshooting), and how to install, update and remove lrw. The
[install scripts](#install) put it into `~/.claude/skills/linux-remote-worker` from the same release as the
program, so its instructions match the installed version; a copy that is already there from elsewhere stays
untouched, and uninstalling removes only the copy the scripts added.

**MCP server** — `lrw mcp` speaks the Model Context Protocol on stdio. Every command becomes a tool
(`nginx sites create` → `nginx_sites_create`) with a JSON schema generated from the same catalog,
read-only/destructive annotations, and the same JSON envelope as the CLI. Destructive tools require
`confirm: true`. One extra tool, `connection_use`, switches the session to another server.

The install scripts register it; by hand:

```bash
claude mcp add --scope user lrw -- lrw mcp                       # Claude Code
claude mcp add --scope user lrw -- lrw mcp --profile prod        # pinned to one server
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "lrw": { "command": "C:\\Users\\you\\AppData\\Local\\Programs\\lrw\\lrw.exe", "args": ["mcp", "--profile", "prod"] }
  }
}
```

Installs and builds can take several minutes; if your MCP client gives up on long tool calls, raise
its tool timeout or run those steps with the CLI.

## Command groups

| Group | What it covers |
|---|---|
| `profiles` | saved servers: list, show, add, update, remove, use (default) |
| `connection` | `test` (with plain-language failure reasons), `show`, `forget-host-key` |
| `system` | `info`, `exec` |
| `files` | `list`, `read`, `download`, `upload` |
| `pg` | `status`, `install`, `restart`, `network`, `hba list/add/remove`, `users list/create/password/drop`, `db list/create/drop`, `grant`, `connstr`, `sql` |
| `repos` | `status`, `list`, `refs`, `install-git`, `deploy-key`, `clone`, `checkout`, `update` (alias `pull`), `remove` — each clone follows `--branch`, `--tag`, `--commit` or `--latest-release` |
| `dotnet` | `status`, `install`, `uninstall` |
| `services` | `list`, `show`, `projects`, `deploy [--environment] [--skip-port-check]`, `redeploy [--update \| --tag … \| --branch … \| --commit … \| --latest-release] [--force]`, `start/stop/restart`, `remove`, `unit show/set` |
| `logs` | `apps`, `journal` (any unit, `--follow`), `files`, `read`, `download`, `path` |
| `nginx` | `status`, `install`, `test`, `reload/restart/start/stop`, `sites list/show/create/enable/disable/delete/edit`, `conf show/set`, `upstreams`, `open-firewall`, `logs list/read` |
| `certs` | `status`, `install`, `list`, `issue`, `secure-site`, `renew`, `renew-due`, `test-renewal`, `delete`, `auto-renew` |
| `firewall` | `status`, `enable`, `disable`, `add`, `delete` |
| `security` | `status`, `add-my-key`, `add-key`, `remove-key`, `create-key`, `create-admin`, `disable-password-login`, `enable-password-login`, `disable-root-login`, `enable-root-login` — key-only logins as an admin user instead of root, without locking yourself out |
| `applog` | `list`, `read`, `path` — this tool's own logs, no server needed |
| `update` | `lrw update [--check]` — lrw itself to the newest release (see [Updating](#updating)); command line only |

Every option of every command: `lrw help <group> <command>`, or the
[skill's reference](../skill/reference.md).

### Versions and instances

```
lrw repos refs git@github.com:acme/shop.git                          # branches, tags, latest release
lrw repos clone --url git@github.com:acme/shop.git --name shop-prod --latest-release
lrw repos clone --url git@github.com:acme/shop.git --name shop-staging --branch develop
lrw services deploy --repo shop-prod --name shop-prod --urls http://127.0.0.1:5000
lrw services deploy --repo shop-staging --name shop-staging --urls http://127.0.0.1:5001 --environment Staging
lrw services redeploy shop-staging --update                          # newest develop commit, rebuild, restart
lrw services redeploy shop-prod --tag v1.4.1                         # roll back production to v1.4.1
```

"Latest release" is the highest version tag (`v1.4.2`, `1.4`, `api/v1.4.0` with `--pattern 'api/v*'`);
pre-releases such as `v2.0.0-rc.1` count only with `--prereleases`. A clone remembers what it follows
(`git config lrw.track`), so `--update` knows whether to move along a branch, to a newer release, or
nowhere (a tag or commit). Updates refuse to touch files changed on the server or a diverged branch
(exit 4) unless `--force` is given.

## How it is built

```
LinuxRemoteWorker.Core/        net10.0 library: SSH, server layout, one service per module
  Core/                        SshService, AppLog, Shell, DeployPaths, ConnectionDiagnostics, OperationException …
  Modules/<Module>/            NginxService, CertbotService, PostgresService, FirewallService,
                               RepositoryService, AppServiceManager, DotnetSdkService, ServerLogService, SystemInfoService
LinuxRemoteWorker/             WPF app — view-models call the Core services
LinuxRemoteWorker.Cli/         lrw.exe
  Framework/                   catalog (reflection), parser, invoker, help/schema, text rendering
  Session/                     server selection, lazy SSH connection, cli.json
  Interactive/                 lrw interactive: Terminal.Gui windows and forms over the same catalog
  Mcp/McpServer.cs             MCP over stdio, generated from the same catalog
  Commands/<Group>Commands.cs  the commands — thin adapters over the Core services
```

A command is a method:

```csharp
[CommandGroup("services", ".NET apps deployed as systemd units")]
public sealed class ServiceCommands : CommandSet
{
    [Command("restart", "Restart a service.")]
    public async Task<Outcome> Restart([Arg("App name", Positional = true)] string app) { … }
}
```

Its parameters become CLI options (`--max-body` from `maxBody`), MCP input properties, `lrw schema`
entries and the fields of its form in `lrw interactive`; `Destructive = true` adds the `--yes` / `confirm` gate; the return value becomes `data`
(or `Outcome` adds a message, warning or output). Services throw `OperationException` with a
`FailureKind` (Invalid / Refused / NotFound / Failed), which picks the exit code. The desktop app shows
the same exceptions' messages in its status line, so both front ends report failures identically.

**Adding a command:** add the method to the group's class (or a new `[CommandGroup]` class), put the
server logic in a Core service so the desktop app can use it too, and document it in the skill's
`reference.md`.

## Limitations

- SSH on port 22 with a key or a password, like the desktop app; no jump hosts.
- The SSH user is `root` or has passwordless sudo (commands then run through `sudo -n`); `security create-admin`
  makes such a user.
- `logs journal --follow` returns after `--seconds`; there is no long-running "watch" for agents.
- One SSH connection per CLI invocation (≈1 s overhead); the MCP server keeps its connection open.
