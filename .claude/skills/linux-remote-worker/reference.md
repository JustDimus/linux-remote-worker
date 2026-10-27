# lrw command reference

`lrw <group> <command> [positional] [--option value] [--flag | --no-flag]`. MCP tool names are the
path with `_` (`nginx sites create` → `nginx_sites_create`), parameters in camelCase (`--max-body` →
`maxBody`), and `--yes` is `confirm: true`. `lrw help <group> <command>` and `lrw schema` are the
authoritative source if this file and the tool ever disagree.

Markers: **RO** read-only · **D** destructive (needs `--yes`) · **L** local, no SSH · **S** streams output, may take minutes · **U** unrestricted (raw shell/SQL/config)

## Global options
`--profile <name>` · `--host <ip> --key <path> [--user root]` (without `--key`: password login) · `--passphrase-env <VAR>` (default `LRW_PASSPHRASE`) ·
`--passphrase-file <path>` · `--password-env <VAR>` (default `LRW_PASSWORD`) · `--password-file <path>` · `--json` / `-o json|text` · `-q` (no streamed output) · `-h`.
Server host keys are remembered on the first login (`known_hosts.json`); a changed key refuses the connection.
Value tricks: `@file` / `@-` (stdin) for `--content`, `--sql` and `system exec`; secrets also take `env:VAR`.

## profiles (L)
| Command | Options | |
|---|---|---|
| `profiles list` | | RO |
| `profiles show <name>` | | RO |
| `profiles add <name>` | `--host` and `--key <path>` or `--password <env:VAR\|@file>` (saved encrypted, Windows) or `--password-login` (not saved) `[--user root]` `[--make-default]` | |
| `profiles update <name>` | `[--rename]` `[--host]` `[--user]` and one of `--key` (switches to key login, drops a saved password), `--password`, `--password-login`, `--forget-password` | |
| `profiles remove <name>` | | D |
| `profiles use <name>` | default for commands without --profile | |

## connection
| Command | Options | |
|---|---|---|
| `connection test` | connect; who/where, auth (key/password), hostKey (+ hostKeyNew), OS, latency; failures come with `error.hint` | RO |
| `connection show` | which server would be used, how it logs in, its remembered host key, local file paths - no connection | RO L |
| `connection forget-host-key` | `[--host]` - forget the remembered host key (only after the user confirmed a changed key) | D L |
| `connection_use` (MCP only) | `profile` or `host`+`key` / `host`+`passwordEnv` [`user`, `passphraseEnv`] - switch the session's server | |

## system
| Command | Options | |
|---|---|---|
| `system info` | `[--no-probe]` (skip the 8 s outbound IPv4/IPv6 probe) | RO |
| `system exec "<cmd>"` | `[--timeout 300]`; data: exitCode, stdout, stderr; process exit 1 if exitCode ≠ 0 | U |

## files
| Command | Options | |
|---|---|---|
| `files list <dir>` | absolute path | RO |
| `files read <file>` | `[--lines 200]` `[--head]` (default: from the end) | RO |
| `files download <file>` | `[--to <local file or folder>]` | RO |
| `files upload <local> <remote>` | remote ending in `/` = into that dir; `[--overwrite]` | U |

## pg (PostgreSQL)
| Command | Options | |
|---|---|---|
| `pg status` | installed, version, online, listenAddresses, port, hbaFile, dataDirectory | RO |
| `pg install` | | S |
| `pg restart` | | |
| `pg network` | `--listen '*'` `[--port 5432]`; auto-rollback if it does not come back in 15 s | |
| `pg hba list` | | RO |
| `pg hba add` | `--address <ip/cidr/all>` `[--db all]` `[--user all]` `[--method scram-sha-256\|md5\|trust\|reject\|peer]` | |
| `pg hba remove` | `--line <n>` `[--file <path>]` | D |
| `pg users list` | | RO |
| `pg users create` | `--name` (`--password <p\|env:VAR\|@file>` or `--generate-password`) `[--superuser]` | |
| `pg users password` | `--name` (`--password` or `--generate-password`) | |
| `pg users drop` | `--name` | D |
| `pg db list` | | RO |
| `pg db create` | `--name` `[--owner postgres]` | |
| `pg db drop` | `--name` | D |
| `pg grant` | `--db` `--user` `[--level owner\|read-write\|read-only]` (default read-write) | |
| `pg connstr` | `--db` `--user` `[--password]` `[--address localhost\|server-ipv4\|server-ipv6]` | RO |
| `pg sql` | `--sql "<sql>"\|@file` `[--db postgres]` | U |

## repos (git clones in /srv/lrw/repos)
Each clone follows a **source**: a branch (updates bring its newest commit), the latest release (the
highest semver tag, e.g. v1.4.2; updates move to newer releases), or a fixed tag or commit (updates leave
it alone). The source options are the same wherever they appear: `--branch <name>` | `--tag <name>` |
`--commit <hash>` | `--latest-release [--pattern 'v*'] [--prereleases]` - at most one of them.

| Command | Options | |
|---|---|---|
| `repos status` | gitVersion, deployPublicKey, repos | RO |
| `repos list` | per clone: source, version (branch, tag or short commit), commit, hasLocalChanges | RO |
| `repos refs <url\|clone>` | defaultBranch, branches, tags (highest version first), latestRelease, latestPrerelease | RO |
| `repos install-git` | | S |
| `repos deploy-key` | `[--generate]` - returns publicKey for the git host's Deploy keys | |
| `repos clone` | `--url` `[--name]` `[source option]` (default: the default branch) | S |
| `repos checkout <name>` | source option (required) `[--force]` - move the clone and make it follow that | |
| `repos update <name>` | `[--force]` - newest commit of its branch (fast-forward) or newest release | |
| `repos pull <name>` | same as `repos update` | |
| `repos remove <name>` | | D |

`checkout` and `update` refuse (exit 4) when tracked files were changed on the server or the branch
diverged from origin (rewritten history); `--force` discards those changes and resets to origin - only
with the user's agreement. A missing branch, tag or release is exit 5 and the message lists what exists.

## dotnet (SDKs in /usr/lib/dotnet)
| Command | Options | |
|---|---|---|
| `dotnet status` | | RO |
| `dotnet install` | `[--version 10.0]` (major.minor) | S |
| `dotnet uninstall` | `--version 9.0` | D S |

## services (systemd units lrw-&lt;app&gt;.service)
| Command | Options | |
|---|---|---|
| `services list` | | RO |
| `services show <app>` | state, sourceProject, deployment (clone, version, commit, environment, urls), paths, `systemctl status` + last journal lines | RO |
| `services projects` | `[--repo]` - deployable .csproj files with suggested names | RO |
| `services deploy` | `--repo` and/or `--csproj` (absolute, or relative to the repo) `[--name]` `[--urls http://0.0.0.0:5000]` `[--environment Production]` `[--skip-port-check]` - a port another service or process uses is refused (exit 4) with a free one suggested | S |
| `services redeploy <app>` | `[--update]` or `[source option]`, `[--force]` - optionally move the clone first, then re-publish from the recorded .csproj and restart | S |
| `services start\|stop\|restart <app>` | | |
| `services remove <app>` | app files stay in /srv/lrw/apps | D |
| `services unit show <app>` | | RO |
| `services unit set <app>` | `--content @unit.service` - reload + restart | U |

## security (SSH logins)
| Command | Options | |
|---|---|---|
| `security status` | user, privileges (root/sudo/none), sessionAuth, hostKey, passwordLoginAllowed, rootLoginAllowed, *DisabledByApp, canDisable… (+ reasons), sshd settings, authorizedKeys | RO |
| `security create-key` | `[--to <local path>]` `[--overwrite]` - Ed25519 key made on the server, downloaded, authorized, removed from the server, tested | S |
| `security add-my-key` | `--key <private key path>` `[--passphrase env:VAR]` - this computer's existing key: public half authorized, tested | |
| `security add-key` | `--public-key "<.pub line>"\|@file` `[--user]` - another device's key; compare data.fingerprint on that device | |
| `security remove-key` | `--fingerprint SHA256:…` - a lost device; never the key of this session, never the last key while passwords are off | D |
| `security create-admin` | `--user <name>` `[--to <local path>]` `[--no-copy-keys]` `[--overwrite]` - user with passwordless sudo and its own key, tested (login + sudo), removed again on failure | S |
| `security disable-password-login` | keys only; refused from a password session; tested and rolled back on failure | D S |
| `security enable-password-login` | removes the app's drop-in (`/etc/ssh/sshd_config.d/00-lrw-password-login.conf`) | D S |
| `security disable-root-login` | PermitRootLogin no; only from another user's key login with working sudo; tested and rolled back on failure | D S |
| `security enable-root-login` | removes the app's drop-in (`/etc/ssh/sshd_config.d/00-lrw-root-login.conf`) | D S |

A login that is not root runs every command through `sudo -n` (passwordless sudo) and transfers files through commands instead of SFTP.

## logs
| Command | Options | |
|---|---|---|
| `logs apps` | apps with an lrw unit | RO |
| `logs journal [<app>]` | `[--unit nginx]` `[--since "1 hour ago"]` `[--lines 500]` `[--follow --seconds 30]` | RO |
| `logs files <app>` | files in /srv/lrw/logs/&lt;app&gt; with size and time | RO |
| `logs read <app> <file>` | `[--lines 1000]` | RO |
| `logs download <app> <file>` | `[--to <local>]` | RO |
| `logs path <app>` | directory + suggestedFile for Serilog/NLog (creates the dir) | |

## nginx
| Command | Options | |
|---|---|---|
| `nginx status` | installed, version, state, layoutWarning, firewall, ipv6, site count | RO |
| `nginx install` | | S |
| `nginx test` | data.passed + output; exit 1 when it fails | RO |
| `nginx reload\|restart\|start` | refused while nginx -t fails | |
| `nginx stop` | all sites go offline | D |
| `nginx sites list` | name, enabled, serverNames, ports, https, certificates, target, createdByApp | RO |
| `nginx sites show <name>` | file content | RO |
| `nginx sites create` | `--domains "a.com www.a.com"` `[--kind proxy\|static]` `[--name]` `[--upstream http://127.0.0.1:5000 \| --service <app>]` `[--root /var/www/x]` `[--no-websockets]` `[--spa]` `[--max-body 10m]` | |
| `nginx sites enable\|disable <name>` | | |
| `nginx sites delete <name>` | `[--delete-unused-certs]` | D |
| `nginx sites edit <name>` | `--content @site.conf` - kept only if nginx -t passes | U |
| `nginx conf show` | /etc/nginx/nginx.conf | RO |
| `nginx conf set` | `--content @nginx.conf` - kept only if nginx -t passes | U |
| `nginx upstreams` | deployed services and their local URLs | RO |
| `nginx open-firewall` | ufw allow 80, 443 | |
| `nginx logs list` | | RO |
| `nginx logs read <file>` | `[--lines 200]` | RO |

## certs (Let's Encrypt via certbot --nginx)
| Command | Options | |
|---|---|---|
| `certs status` | installed, nginx plugin, auto-renewal | RO |
| `certs install` | certbot + python3-certbot-nginx | S |
| `certs list` | name, domains, expiry, daysLeft, health ok/soon/bad, usedBy | RO |
| `certs issue` | `--domains "a.com www.a.com"` `[--name]` `[--email]` `[--no-redirect]` `[--dry-run]` | S |
| `certs secure-site <site>` | `[--email]` `[--no-redirect]` `[--dry-run]` | S |
| `certs renew <name>` | force renewal now (rate limited) | D S |
| `certs renew-due` | renew what is due | S |
| `certs test-renewal` | dry run of every renewal | RO S |
| `certs delete <name>` | refused while a config uses it | D |
| `certs auto-renew` | enable certbot.timer | |

## firewall (ufw)
| Command | Options | |
|---|---|---|
| `firewall status` | enabled, sshPorts, rules (first = protected SSH) | RO |
| `firewall enable` | SSH ports are allowed first | |
| `firewall disable` | | D |
| `firewall add` | `--port 5432\|8000:8100` `[--proto tcp\|udp\|any]` `[--from any\|ip\|cidr]` `[--action allow\|deny]` | |
| `firewall delete` | `--port` `[--proto]` `[--from]` `[--action]` - must match exactly one rule | D |

## applog (L - this tool's own log on this computer)
| Command | Options | |
|---|---|---|
| `applog list` | app-, cli-, mcp-&lt;date&gt;.log files | RO |
| `applog read` | `[--file]` `[--source app\|cli\|mcp\|all]` `[--lines 200]` `[--filter ERROR]` | RO |
| `applog path` | log, profiles and settings locations | RO |

## Built-ins
`lrw help [group [command]]` · `lrw schema [group]` (JSON of everything above) · `lrw mcp [--profile x]` (MCP server on stdio) · `lrw version`
