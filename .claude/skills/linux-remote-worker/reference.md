# lrw command reference

`lrw <group> <command> [positional] [--option value] [--flag | --no-flag]`. MCP tool names are the
path with `_` (`nginx sites create` → `nginx_sites_create`), parameters in camelCase (`--max-body` →
`maxBody`), and `--yes` is `confirm: true`. `lrw help <group> <command>` and `lrw schema` are the
authoritative source if this file and the tool ever disagree.

Markers: **RO** read-only · **D** destructive (needs `--yes`) · **L** local, no SSH · **S** streams output, may take minutes · **U** unrestricted (raw shell/SQL/config)

## Global options
`--profile <name>` · `--host <ip> --key <path> [--user root]` · `--passphrase-env <VAR>` (default `LRW_PASSPHRASE`) ·
`--passphrase-file <path>` · `--json` / `-o json|text` · `-q` (no streamed output) · `-h`.
Value tricks: `@file` / `@-` (stdin) for `--content`, `--sql` and `system exec`; secrets also take `env:VAR`.

## profiles (L)
| Command | Options | |
|---|---|---|
| `profiles list` | | RO |
| `profiles show <name>` | | RO |
| `profiles add <name>` | `--host` `--key` `[--user root]` `[--make-default]` | |
| `profiles update <name>` | `[--rename]` `[--host]` `[--user]` `[--key]` | |
| `profiles remove <name>` | | D |
| `profiles use <name>` | default for commands without --profile | |

## connection
| Command | Options | |
|---|---|---|
| `connection test` | connect; who/where, OS, latency; failures come with `error.hint` | RO |
| `connection show` | which server would be used, local file paths - no connection | RO L |
| `connection_use` (MCP only) | `profile` or `host`+`key` [`user`, `passphraseEnv`] - switch the session's server | |

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
| Command | Options | |
|---|---|---|
| `repos status` | gitVersion, deployPublicKey, repos | RO |
| `repos list` | | RO |
| `repos install-git` | | S |
| `repos deploy-key` | `[--generate]` - returns publicKey for the git host's Deploy keys | |
| `repos clone` | `--url` `[--branch]` `[--name]` | S |
| `repos pull <name>` | | |
| `repos remove <name>` | | D |

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
| `services show <app>` | state, sourceProject, paths, `systemctl status` + last journal lines | RO |
| `services projects` | `[--repo]` - deployable .csproj files with suggested names | RO |
| `services deploy` | `--repo` and/or `--csproj` (absolute, or relative to the repo) `[--name]` `[--urls http://0.0.0.0:5000]` | S |
| `services redeploy <app>` | `[--pull]` - re-publish from the recorded .csproj, restart | S |
| `services start\|stop\|restart <app>` | | |
| `services remove <app>` | app files stay in /srv/lrw/apps | D |
| `services unit show <app>` | | RO |
| `services unit set <app>` | `--content @unit.service` - reload + restart | U |

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
