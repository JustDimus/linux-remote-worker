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

1. Check it is installed: `lrw version`. If it is not found, offer the install command for this system
   (see [Installing, updating and removing lrw](#installing-updating-and-removing-lrw)) and run it once
   the user agrees.
2. Pick the server. `lrw profiles list --json` shows saved servers and the default. Pass
   `--profile <name>` on every command unless the user named a default (`lrw profiles use <name>`).
   No profile yet: `lrw profiles add <name> --host <ip> --key <private key path>` (ask the user for
   these - never guess a key path). Encrypted key: the passphrase comes from the `LRW_PASSPHRASE`
   environment variable; ask the user to set it rather than putting it on a command line.
   **Password only (no key yet):** never ask the user to type a password into the chat. Ask them to save
   it themselves - in the desktop app (Password + *Remember password*), or by setting an environment
   variable and running `lrw profiles add <name> --host <ip> --user <user> --password env:<VAR>` - or
   to set `LRW_PASSWORD` for the session. Then offer to switch the server to a key (see *Harden SSH logins*).
3. Run `lrw connection test --profile <name> --json`. On failure `error.hint` says what to fix
   (DNS, port 22 blocked, key or password rejected, passphrase needed); relay it to the user.
   `data.privileges` is `root`, `sudo` (a user with passwordless sudo - every command runs through
   `sudo -n`, nothing else changes) or `none` (not root, no passwordless sudo: changing commands fail -
   tell the user).
   **"The server's identity changed"** (its SSH host key differs from the one remembered on the first
   login): stop and tell the user - it may be an interception. Run `lrw connection forget-host-key --yes`
   only after they confirm the server was reinstalled or its keys were regenerated.

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
  `profiles remove`, `security disable-password-login`, `security enable-password-login`,
  `security disable-root-login`, `security enable-root-login`, `security remove-key`, `connection forget-host-key`. Never add `--yes` on your own initiative: tell the user exactly what will be
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
lrw repos clone --url git@github.com:acme/shop.git --json   # default branch; versions: see below
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

**Versions: choose, update, roll back.** A clone follows a branch, the latest release (highest semver
tag) or a fixed tag/commit; the source options are `--branch` | `--tag` | `--commit` |
`--latest-release [--pattern 'v*'] [--prereleases]`.
```
lrw repos refs git@github.com:acme/shop.git --json     # default branch, branches, tags, latestRelease
lrw repos clone --url git@github.com:acme/shop.git --name shop --latest-release --json
lrw services redeploy shop-api --update --json          # newest commit of its branch / newest release, rebuild, restart
lrw services redeploy shop-api --tag v1.4.1 --json      # move to (roll back to) a version, rebuild, restart
lrw services show shop-api --json                       # data.deployment: version and commit running now
```
`--update` leaves a clone pinned to a tag or commit where it is. Exit 4 from `--update`, a version
option or `repos checkout`: files were changed on the server or the branch diverged from origin -
tell the user; `--force` discards that, so add it only with their agreement. Changing a clone's version
affects every service built from that clone on its next redeploy.

**Two instances of one app** (e.g. production on the latest release, staging on a branch): clone the
repository twice under different names and deploy each clone under its own app name and port.
```
lrw repos clone --url git@github.com:acme/shop.git --name shop-prod --latest-release --json
lrw repos clone --url git@github.com:acme/shop.git --name shop-staging --branch develop --json
lrw services deploy --repo shop-prod --csproj src/Shop.Api/Shop.Api.csproj --name shop-prod --urls http://127.0.0.1:5000 --json
lrw services deploy --repo shop-staging --csproj src/Shop.Api/Shop.Api.csproj --name shop-staging --urls http://127.0.0.1:5001 --environment Staging --json
```
A port another service or process already uses is refused (exit 4) with a free one suggested. Ask the
user whether the instances need separate databases or settings before they share one by accident.

**Harden SSH logins: password → key only** (the user has only a password, or wants passwords off)
```
lrw security status --json                        # sessionAuth, passwordLoginAllowed, authorizedKeys
lrw security add-my-key --key ~/.ssh/id_ed25519 --json   # preferred when this computer has a key: only the
                                                  # public half goes to the server; tested with a fresh login
lrw security create-key --json                    # otherwise: key made on the server, private half downloaded to
                                                  # ~/.ssh/lrw_<host>_ed25519 (only this user can read it),
                                                  # added to authorized_keys, removed from the server, tested
lrw profiles update <profile> --key "<data.privateKeyPath>" --json   # the profile now logs in with the key
lrw connection test --json                        # auth: "key"
lrw security disable-password-login --yes --json  # only after the user agreed to keys-only logins
```
`disable-password-login` refuses (exit 4) from a session that logged in with a password, so the key is
proven before passwords stop working; it checks sshd, reloads it, tests a fresh key login and undoes
everything when that fails. It affects every user of the server - say so before asking for agreement.
`security enable-password-login --yes` removes the app's setting again.

**Let another device in** (the user's Mac, a colleague's laptop): the device keeps its own private key; ask
the user for its public key (`cat ~/.ssh/id_ed25519.pub` there - never the private key), then
```
lrw security add-key --public-key "ssh-ed25519 AAAA… user@mac" [--user admin] --json   # or --public-key @file.pub
```
and ask the user to compare `data.fingerprint` with `ssh-keygen -lf ~/.ssh/id_ed25519.pub` on that device.
A lost device: `lrw security remove-key --fingerprint SHA256:… --yes` (fingerprints in `security status`); the key
this session logs in with cannot be removed.

**Admin user instead of root** (after the steps above, or when the user wants root logins off)
```
lrw security create-admin --user admin --json     # user with passwordless sudo + its own key, tested;
                                                  # the keys that log in as the current user work for it too
lrw profiles update <profile> --user admin --key "<data.privateKeyPath>" --json
lrw connection test --json                        # user: admin, privileges: "sudo"
lrw security disable-root-login --yes --json      # only after the user agreed; refused while logged in as root
```
Root login can only be turned off from the admin user's key login with working sudo; it is tested with a fresh
login and undone on failure. Profiles that log in as root stop working - say so before asking for agreement.

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
| "The server rejected the password" / "does not accept passwords" | wrong password or user, root password logins forbidden, or the server takes keys only - ask the user for a key or another user |
| "The server's identity changed" | stop: the host key differs from the remembered one; only the user can confirm it (then `connection forget-host-key --yes`) |
| App still runs the old version after `repos update` | redeploy it: `services redeploy <app>` (or use `services redeploy <app> --update` in one step) |

## Installing, updating and removing lrw

**Updating** is a command of lrw itself (command line only, not an MCP tool):

```
lrw update --check --json      # data.updateAvailable, data.current, data.latest
lrw update --json              # install the newest release: program and this skill
lrw update --version v0.2.1    # a given release, also an older one
```

It runs the install script of the release it installs, after checking it against that release's
SHA256SUMS.txt. It refuses a copy of lrw that the install scripts did not put in place (a development
build) - then say so instead of working around it.

**Installing and removing** use two scripts that every release carries: one for Windows (Windows
PowerShell or PowerShell 7) and one for macOS and Linux (bash), each native to its system and doing the
same. They download the release build for the OS and CPU, check it against the release's SHA256SUMS.txt,
put it on PATH and, with Claude Code installed, register the MCP server `lrw` and add this skill from the
same release. No administrator rights, no .NET.

```
# Windows - from PowerShell, cmd or Git Bash; the script runs in a fresh Windows PowerShell of its own
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1 | iex"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1))) -Status"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.ps1))) -Uninstall"
# (install - also connects a newly installed Claude Code; -Status - version, newest release, PATH, MCP, skill;
#  -Uninstall - program, PATH entry, MCP registration, skill)

# macOS, Linux (bash)
lrw_sh=https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.sh
curl -fsSL "$lrw_sh" | bash                          # install (wget -qO- works too)
curl -fsSL "$lrw_sh" | bash -s -- --status
curl -fsSL "$lrw_sh" | bash -s -- --uninstall
```

Options, the same in both scripts: `-Version`/`--version <tag>`, `-McpProfile`/`--mcp-profile <name>`,
`-NoMcp`/`--no-mcp`, `-NoSkill`/`--no-skill`, `-NoPath`/`--no-path`, `-Force`/`--force`. `lrw update`
takes `--no-mcp`, `--no-skill`, `--no-path`, `--mcp-profile` and `--force` too.

- Program location: `%LOCALAPPDATA%\Programs\lrw` (added to the user PATH) on Windows,
  `~/.local/share/lrw` linked as `~/.local/bin/lrw` on Linux and macOS.
- A PATH change reaches only terminals started afterwards. In the current session call the program by
  the path the script prints.
- The MCP registration needs the `claude` command on PATH (Claude Code used only inside an editor may
  not have it); without it the script prints the `claude mcp add` command to run. A registration that
  is already there stays as it is unless `--mcp-profile` asks for a new one.
- A skill folder that was not added by these scripts is left alone, also on uninstall.
- **Saved servers stay untouched.** `profiles.json` (its path is `data.profilesFile` in
  `lrw profiles list --json`) is shared with the desktop app. Uninstalling, reinstalling or updating lrw
  or this skill never deletes, edits, moves or resets it, and never runs `profiles remove` as part of a
  clean-up. Only a separate, explicit request from the user to remove profiles allows that. When
  reporting an uninstall, say that the saved servers were kept.
- Updating works while lrw runs; open Claude sessions keep the old MCP server until they restart. On
  Windows, uninstalling refuses while lrw runs (an MCP server of an open Claude session, this one
  included) - ask the user to close Claude Code and run the uninstall command themselves.

## More

- Every command, its options and examples: [reference.md](reference.md)
- Exact parameters straight from the tool: `lrw help <group> [command]`, or all of them as JSON: `lrw schema`
- Quoting: `*` must be quoted (`--listen '*'`); `system exec` takes the whole command as one quoted
  argument (or everything after `--`); long text goes in a file: `--content @site.conf`, `--sql @fix.sql`.
- Windows, Git Bash: arguments that start with `/` are rewritten into Windows paths before lrw sees
  them (`/etc/nginx/nginx.conf` arrives as `C:/Program Files/Git/etc/nginx/nginx.conf`). Commands taking
  a server path (`files *`, `nginx logs read`, `services deploy --csproj`, `nginx sites create --root`)
  refuse such values with exit code 2. Prefix those commands with `MSYS_NO_PATHCONV=1`
  (`MSYS_NO_PATHCONV=1 lrw files read /etc/nginx/nginx.conf --json`), or use PowerShell or the MCP tools.
  Paths inside a quoted `system exec "..."` command are not affected.
