---
name: linux-remote-worker
description: Operate Linux servers through `lrw`, the command line of Linux Remote Worker - inspect a server, deploy .NET apps as systemd services, manage PostgreSQL, git repositories, nginx sites with Let's Encrypt HTTPS and the ufw firewall, and read or download server and app logs. Use when the user asks to deploy, configure, check, troubleshoot or read logs on a server managed with Linux Remote Worker, or mentions lrw, Linux Remote Worker, or one of its saved server profiles.
---

# Linux Remote Worker (`lrw`)

`lrw` runs the same operations as the Linux Remote Worker desktop app, over SSH, on a server the user
saved as a profile. Every change is done the careful way: input is validated and shell-quoted, nginx
changes are kept only if `nginx -t` passes, PostgreSQL network/pg_hba changes roll back when the server
rejects them, and SSH can never be firewalled off.

If MCP tools named like `nginx_sites_create` / `connection_test` are available, they are the same
commands (tool = CLI path with `_`), take the same parameters (camelCase) and return the same JSON.
Use whichever is at hand; this guide shows CLI syntax.

## Before the first command

1. Check it is installed: `lrw version`. If it is not found, install it with this skill's setup script
   (see [Installing, updating and removing lrw](#installing-updating-and-removing-lrw)) - ask the user
   first whether to download the release build or build from source, and whether to register the MCP server.
2. Pick the server. `lrw profiles list --json` shows saved servers and the default. Pass
   `--profile <name>` on every command unless the user named a default (`lrw profiles use <name>`).
   No profile yet: `lrw profiles add <name> --host <ip> --key <private key path>` (ask the user for
   these - never guess a key path). Encrypted key: the passphrase comes from the `LRW_PASSPHRASE`
   environment variable; ask the user to set it rather than putting it on a command line.
3. Run `lrw connection test --profile <name> --json`. On failure `error.hint` says what to fix
   (DNS, port 22 blocked, key not in authorized_keys, passphrase needed); relay it to the user.

## Reading results

Always pass `--json` (it is the default when stdout is piped, but be explicit). Every command prints
one JSON object:

```json
{ "ok": true,  "command": "nginx sites create", "server": "root@203.0.113.10 (prod-01)",
  "message": "Site shop.example.com created and enabled", "warning": "...", "data": { }, "output": "nginx -t output" }
{ "ok": false, "command": "...", "server": "...",
  "error": { "kind": "refused", "message": "...", "hint": "...", "detail": "...", "output": "remote output" } }
```

- Check `server` - it is the machine that was touched.
- Read `warning` even when `ok` is true (e.g. "nothing listens on port 5000 yet - nginx answers 502").
- `error.output` holds the tail of the remote output (build errors, certbot reasons, `nginx -t` lines).
- Long operations (`install`, `deploy`, `clone`, `certs issue`) stream their output to stderr while they
  run - that is progress, not an error. They can take minutes; let them finish.

| Exit | Meaning | What to do |
|---|---|---|
| 0 | success | |
| 1 | failed on the server | read `error.message` and `error.output` |
| 2 | invalid input - nothing was sent | fix the arguments (`lrw help <command>`) |
| 3 | connection failed / dropped | follow `error.hint`, re-test the connection |
| 4 | refused by a safety rule, or destructive without `--yes` | see "Safety" |
| 5 | not found | list first (`... list`), check names |

## Safety - non-negotiable

- **Destructive commands refuse to run without `--yes`** (exit 4): `pg db drop`, `pg users drop`,
  `pg hba remove`, `repos remove`, `services remove`, `dotnet uninstall`, `nginx stop`,
  `nginx sites delete`, `certs delete`, `certs renew`, `firewall disable`, `firewall delete`,
  `profiles remove`. Never add `--yes` on your own initiative: tell the user exactly what will be
  deleted or taken offline, on which server, and add `--yes` only after they agree to that action.
- **Prefer dedicated commands over `system exec` and `pg sql`.** The dedicated ones validate input and
  roll back failed changes; the raw ones do neither. Use the raw ones for reading (`ss -tlnp`,
  `df -h`, `cat`), or when no command exists - and say what you are about to run if it changes state.
- **Before replacing a file** (`nginx sites edit`, `nginx conf set`, `services unit set`) read the
  current content (`sites show`, `conf show`, `unit show`) and change only what is needed.
- **Secrets:** create database passwords with `--generate-password` (returned once in `data.password`)
  or pass them as `--password env:VAR` / `@file`. Do not echo passwords back unless the user needs them.
- **Verify after changing**: `services show`, `nginx test`, `certs list`, `firewall status`, `pg status`.

## Common workflows

**First look at a server**
```
lrw system info --json
lrw services list --json; lrw nginx status --json; lrw pg status --json; lrw firewall status --json
```

**Deploy a .NET app behind nginx with HTTPS** (each step's output tells you if a prerequisite is missing)
```
lrw repos status --json                       # git installed? deploy key?
lrw repos install-git --json                  # if gitVersion is null
lrw repos deploy-key --generate --json        # give data.publicKey to the user: add it as a read-only
                                              # Deploy key in GitHub/GitLab, wait for them to confirm
lrw repos clone --url git@github.com:acme/shop.git --json
lrw dotnet status --json; lrw dotnet install --version 8.0 --json    # match the project's TargetFramework
lrw services projects --repo shop --json
lrw services deploy --repo shop --csproj src/Shop.Api/Shop.Api.csproj --name shop-api --urls http://127.0.0.1:5000 --json
lrw nginx install --json                      # if nginx status says installed: false
lrw nginx sites create --domains "shop.example.com" --service shop-api --json
lrw firewall status --json                    # if active and 80/443 closed: lrw nginx open-firewall
lrw certs install --json                      # if certs status says ready: false
lrw certs secure-site shop.example.com --email <user's e-mail> --json
```
The domain must already point to the server (DNS A/AAAA record) before `certs secure-site`; it checks
and stops early otherwise. Use `--dry-run` first when unsure - real failures count against
Let's Encrypt rate limits. Tell the user to call `app.UseForwardedHeaders()` in ASP.NET Core.

**Update a deployed app to the latest commit**: `lrw services redeploy shop-api --pull --json`

**Database for an app**
```
lrw pg install --json                                         # if pg status says installed: false
lrw pg users create --name shop --generate-password --json    # keep data.password for the connection string
lrw pg db create --name shop --owner shop --json
lrw pg connstr --db shop --user shop --password env:SHOP_PW --json   # or without --password: placeholder
```
Remote access (only if the user asks): `lrw pg network --listen '*'`, `lrw pg hba add --address <client ip/cidr> --db shop --user shop`,
`lrw firewall add --port 5432 --from <client ip/cidr>`.

**Logs**
```
lrw logs journal shop-api --since "1 hour ago" --lines 200 --json     # systemd journal of an app
lrw logs journal --unit nginx --since today --json                    # any unit: nginx, postgresql, ssh
lrw logs journal shop-api --follow --seconds 30 --json                # watch live for 30 s
lrw logs files shop-api --json; lrw logs read shop-api <file> --lines 500 --json
lrw logs download shop-api <file> --to ./logs/ --json                 # copy to this computer
lrw nginx logs list --json; lrw nginx logs read <site>.error.log --json
lrw files download /var/log/syslog --to ./logs/ --json                # any file
lrw applog read --filter ERROR --json                                 # this tool's own log (local)
```

## Troubleshooting map

| Symptom | Look at |
|---|---|
| Service will not start / keeps restarting | `services show <app>` (status + last journal lines), then `logs journal <app> --since "10 min ago"` |
| Site answers 502 Bad Gateway | the app is not listening: `services show <app>`, `nginx upstreams`, `system exec "ss -tlnp"` |
| `nginx test` fails | `data.output` names the file and line; fix with `sites show` + `sites edit` |
| Certificate request fails | `error.output` (certbot's reason): DNS not pointing here, port 80 blocked (`firewall status`, cloud security group) |
| Cannot connect | `connection test` → `error.hint`; `applog read --source cli --filter ERROR` |
| A change "did nothing" | check `server` in the result - wrong profile? |

## Installing, updating and removing lrw

`scripts/lrw-setup.ps1` in this skill's folder (`~/.claude/skills/linux-remote-worker/` when installed
for the user, `.claude/skills/linux-remote-worker/` inside the Linux Remote Worker repository) manages
the program. It runs on PowerShell 7 (`pwsh -File …`) on every OS, and on Windows PowerShell 5.1
(`powershell -ExecutionPolicy Bypass -File …`). Add `-Json` to get a JSON result on stdout.

```
lrw-setup.ps1 status                          # installed version, source, on PATH?, update available?
lrw-setup.ps1 install                         # newest release build for this OS and CPU (no .NET needed)
lrw-setup.ps1 install -Source build           # compile from GitHub (needs git + .NET 8 SDK)
lrw-setup.ps1 install -Source build -Repo <folder with the sources>   # compile a local checkout
lrw-setup.ps1 install -RegisterMcp [-McpProfile <name>]               # also: claude mcp add lrw
lrw-setup.ps1 update                          # newest version from the same source as before
lrw-setup.ps1 uninstall [-Purge]              # remove program, PATH entry/link, MCP registration
```

- Program location: `%LOCALAPPDATA%\Programs\lrw` (added to the user PATH) on Windows,
  `~/.local/share/lrw` linked as `~/.local/bin/lrw` on Linux and macOS. No administrator rights.
- Release downloads are verified against the release's SHA256SUMS.txt. If the repository has no
  release yet, `install` says so - offer `-Source build` instead.
- A PATH change reaches only terminals started afterwards. In the current session call the program by
  the `exe` path that `install` / `status` print.
- `uninstall -Purge` also deletes the CLI's own settings and logs; saved servers (`profiles.json`) are
  shared with the desktop app and always stay. On Windows, uninstall fails while lrw is running (an MCP
  server of an open Claude session) - ask the user to close it.
- When this skill was installed from a skills pack, uninstalling the skill through the pack runs
  `lrw-setup.ps1 uninstall` first (`pack-hooks.json`), so the program goes with it.

## More

- Every command, its options and examples: [reference.md](reference.md)
- Exact parameters straight from the tool: `lrw help <group> [command]`, or all of them as JSON: `lrw schema`
- Quoting: `*` must be quoted (`--listen '*'`); `system exec` takes the whole command as one quoted
  argument (or everything after `--`); long text goes in a file: `--content @site.conf`, `--sql @fix.sql`.
