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
- [Choosing the server](#choosing-the-server)
- [Output and exit codes](#output-and-exit-codes)
- [Safety](#safety)
- [Using it from Claude (skill and MCP)](#using-it-from-claude-skill-and-mcp)
- [Command groups](#command-groups)
- [How it is built](#how-it-is-built)
- [Limitations](#limitations)

## Install

`lrw` runs on Windows, Linux and macOS (x64 and arm64). The setup script of the Claude skill installs,
updates and removes it — PowerShell 7 on any OS, or Windows PowerShell 5.1:

```powershell
$setup = '.claude/skills/linux-remote-worker/scripts/lrw-setup.ps1'
pwsh -File $setup install                              # newest release build for this OS/CPU, no .NET needed
pwsh -File $setup install -Source build -Repo .        # compile this checkout (needs the .NET 8 SDK)
pwsh -File $setup install -RegisterMcp                 # … and register the MCP server in Claude Code
pwsh -File $setup status                               # version, source, update available?
pwsh -File $setup update                               # newest version from the same source
pwsh -File $setup uninstall                            # program, PATH entry/link and MCP registration
```

The program goes to `%LOCALAPPDATA%\Programs\lrw` (added to the user PATH) on Windows and to
`~/.local/share/lrw`, linked as `~/.local/bin/lrw`, on Linux and macOS — no administrator rights.
Release downloads are checked against the release's `SHA256SUMS.txt`.

**Releases** are built by [`.github/workflows/release.yml`](../.github/workflows/release.yml) when a tag
`v<version>` is pushed: self-contained single-file builds for win-x64, win-arm64, linux-x64,
linux-arm64, osx-x64 and osx-arm64.

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

## Choosing the server

In this order:

1. `--host <ip> --key <path> [--user root]` (or `LRW_HOST`, `LRW_KEY`, `LRW_USER`) — no profile needed
2. `--profile <name>` (or `LRW_PROFILE`)
3. the default profile, set with `lrw profiles use <name>` (stored in `%AppData%\LinuxRemoteWorker\cli.json`)
4. the only saved profile, when there is exactly one

An encrypted key's passphrase comes from `LRW_PASSPHRASE`, `--passphrase-env <VAR>` or
`--passphrase-file <path>`; on an interactive terminal you are asked for it. It is never stored.

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

**Skill** — `.claude/skills/linux-remote-worker/` teaches Claude how to use the CLI safely: picking
the server, reading results, never adding `--yes` without the user's agreement, and step-by-step
workflows (deploy behind nginx with HTTPS, databases, logs, troubleshooting). It also carries the
setup script, so an agent can install, update and remove lrw itself. It is active when Claude Code
runs in this repository; to use it in every project, copy the folder to `~/.claude/skills/` (or
install it from a skills pack — its `pack-hooks.json` makes the pack remove the program together with
the skill).

**MCP server** — `lrw mcp` speaks the Model Context Protocol on stdio. Every command becomes a tool
(`nginx sites create` → `nginx_sites_create`) with a JSON schema generated from the same catalog,
read-only/destructive annotations, and the same JSON envelope as the CLI. Destructive tools require
`confirm: true`. One extra tool, `connection_use`, switches the session to another server.

```bash
claude mcp add --scope user lrw -- lrw mcp                       # Claude Code
claude mcp add --scope user lrw -- lrw mcp --profile prod        # pinned to one server
pwsh -File <skill>/scripts/lrw-setup.ps1 install -RegisterMcp    # or let the setup script do it
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
| `connection` | `test` (with plain-language failure reasons), `show` |
| `system` | `info`, `exec` |
| `files` | `list`, `read`, `download`, `upload` |
| `pg` | `status`, `install`, `restart`, `network`, `hba list/add/remove`, `users list/create/password/drop`, `db list/create/drop`, `grant`, `connstr`, `sql` |
| `repos` | `status`, `list`, `install-git`, `deploy-key`, `clone`, `pull`, `remove` |
| `dotnet` | `status`, `install`, `uninstall` |
| `services` | `list`, `show`, `projects`, `deploy`, `redeploy [--pull]`, `start/stop/restart`, `remove`, `unit show/set` |
| `logs` | `apps`, `journal` (any unit, `--follow`), `files`, `read`, `download`, `path` |
| `nginx` | `status`, `install`, `test`, `reload/restart/start/stop`, `sites list/show/create/enable/disable/delete/edit`, `conf show/set`, `upstreams`, `open-firewall`, `logs list/read` |
| `certs` | `status`, `install`, `list`, `issue`, `secure-site`, `renew`, `renew-due`, `test-renewal`, `delete`, `auto-renew` |
| `firewall` | `status`, `enable`, `disable`, `add`, `delete` |
| `applog` | `list`, `read`, `path` — this tool's own logs, no server needed |

Every option of every command: `lrw help <group> <command>`, or the
[skill's reference](../.claude/skills/linux-remote-worker/reference.md).

## How it is built

```
LinuxRemoteWorker.Core/        net8.0 library: SSH, server layout, one service per module
  Core/                        SshService, AppLog, Shell, DeployPaths, ConnectionDiagnostics, OperationException …
  Modules/<Module>/            NginxService, CertbotService, PostgresService, FirewallService,
                               RepositoryService, AppServiceManager, DotnetSdkService, ServerLogService, SystemInfoService
LinuxRemoteWorker/             WPF app — view-models call the Core services
LinuxRemoteWorker.Cli/         lrw.exe
  Framework/                   catalog (reflection), parser, invoker, help/schema, text rendering
  Session/                     server selection, lazy SSH connection, cli.json
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

Its parameters become CLI options (`--max-body` from `maxBody`), MCP input properties and `lrw schema`
entries; `Destructive = true` adds the `--yes` / `confirm` gate; the return value becomes `data`
(or `Outcome` adds a message, warning or output). Services throw `OperationException` with a
`FailureKind` (Invalid / Refused / NotFound / Failed), which picks the exit code. The desktop app shows
the same exceptions' messages in its status line, so both front ends report failures identically.

**Adding a command:** add the method to the group's class (or a new `[CommandGroup]` class), put the
server logic in a Core service so the desktop app can use it too, and document it in the skill's
`reference.md`.

## Limitations

- SSH on port 22 with key authentication only, like the desktop app; no jump hosts; the server's
  host key is not verified.
- The SSH user needs root rights without sudo (typically `root`).
- `logs journal --follow` returns after `--seconds`; there is no long-running "watch" for agents.
- One SSH connection per CLI invocation (≈1 s overhead); the MCP server keeps its connection open.
