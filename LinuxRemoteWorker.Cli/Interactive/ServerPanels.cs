using System.Text.Json.Nodes;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

// The screens of a connected server, one per module of the desktop app. Each reads the server through the same
// commands as the command line and acts through them, so a button does exactly what its command does.

/// <summary>
/// Panels with a table of things on the server: a menu of the module's own on top, the table, and under it the
/// actions for the selected row, picked with Left / Right in the table and run with Enter.
/// </summary>
internal abstract class TablePanel : Panel
{
    private DataTable? _main;

    protected TablePanel(InteractiveApp shell) : base(shell) { }

    public override void Enter()
    {
        if (_main != null) _main.View.SetFocus();
        else base.Enter();
    }

    /// <summary>
    /// The usual layout: the module's menu on top, facts (when there are any) under it, the table filling the rest,
    /// and the actions for the selected row at the bottom.
    /// </summary>
    protected void Layout(View top, Label? facts, string factsTitle, DataTable table, string tableTitle, View? rowButtons)
    {
        _main = table;
        top.Y = 0;
        Add(top);

        View above = top;
        var gap = 1;
        if (facts != null)
        {
            var factsFrame = Section(factsTitle, facts);
            factsFrame.Y = Pos.Bottom(top) + 1;
            factsFrame.Height = Dim.Auto();
            Add(factsFrame);
            above = factsFrame;
            gap = 0;
        }

        var tableFrame = Section(tableTitle, table.View);
        tableFrame.Y = Pos.Bottom(above) + gap;
        tableFrame.Height = Dim.Fill(1);
        Add(tableFrame);

        if (rowButtons != null)
        {
            // Over the status line, as many lines as its buttons need; the table ends where they start
            rowButtons.Y = Pos.AnchorEnd() - 1;
            tableFrame.Height = Dim.Fill(rowButtons);
            Add(rowButtons);
            if (rowButtons is ButtonFlow actions) table.UseActions(actions);
        }
    }

    protected Button RefreshButton() => Button("Refresh", async () =>
    {
        Say("Loading...", Mood.Busy);
        if (await LoadAsync()) Say("");
    });

    /// <summary>Runs the action on the selected row's value, or says that a row is needed.</summary>
    protected Func<Task> OnRow(DataTable table, string field, string what, Func<string, Task> action) => async () =>
    {
        if (table.Pick(field) is { Length: > 0 } value) await action(value);
        else Say($"Select {what} first.", Mood.Bad);
    };
}

internal sealed class SystemPanel : Panel
{
    private readonly Label _overview = FactsLabel();
    private readonly Label _network = FactsLabel();
    private bool _probe;

    public SystemPanel(InteractiveApp shell) : base(shell)
    {
        var buttons = Row(
            Button("Refresh", () => Reload(false)),
            Button("Check internet access", () => Reload(true)),
            Button("Run a command...", () => Act("system exec", style: ActStyle.Ask | ActStyle.Show)));
        buttons.Y = 0;

        var overview = Section("Machine", _overview);
        overview.Y = Pos.Bottom(buttons) + 1;
        overview.Width = Dim.Percent(50);
        overview.Height = Dim.Fill(1);
        var network = Section("Network", _network);
        network.X = Pos.Right(overview);
        network.Y = Pos.Bottom(buttons) + 1;
        network.Height = Dim.Fill(1);
        Add(buttons, overview, network);
    }

    private async Task Reload(bool probe)
    {
        _probe = probe;
        Say(probe ? "Checking outbound IPv4 and IPv6 (up to 8 seconds)..." : "Loading...", Mood.Busy);
        if (await LoadAsync()) Say("");
        _probe = false;
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("system info", ("probe", _probe)) is not { } data) return false;
        var o = data["overview"];
        _overview.Text = Facts.Format(
            ("Hostname", J.Str(o?["hostname"])),
            ("System", J.Str(o?["os"])),
            ("Kernel", J.Str(o?["kernel"])),
            ("Uptime", J.Str(o?["uptime"])),
            ("CPU busy", o?["cpuBusyPercent"] is JsonValue cpu ? $"{cpu.GetValue<double>():0.#} %" : null),
            ("Memory", J.Str(o?["memory"])),
            ("Disk", J.Str(o?["disk"])),
            ("Address", J.Str(o?["primaryIp"])));

        var n = data["network"];
        string Outbound(JsonNode? works) => works is JsonValue v ? v.GetValue<bool>() ? "works" : "blocked" : "not checked";
        _network.Text = Facts.Format(
            ("IPv4", J.Lines(n?["ipv4Addresses"])),
            ("IPv6", J.Lines(n?["ipv6Addresses"])),
            ("Gateway", J.Str(n?["gatewayV4"])),
            ("Gateway v6", J.Str(n?["gatewayV6"])),
            ("DNS", J.Str(n?["dnsServers"])),
            ("Outbound IPv4", Outbound(n?["ipv4Outbound"])),
            ("Outbound IPv6", Outbound(n?["ipv6Outbound"])));
        return true;
    }
}

internal sealed class ServicesPanel : TablePanel
{
    private readonly DataTable _services = new(
        ("Service", r => J.Str(r["appName"])),
        ("State", r => J.Str(r["statusText"])),
        ("Version", r => J.Str(r["versionText"])),
        ("Unit", r => J.Str(r["unitName"])));

    public ServicesPanel(InteractiveApp shell) : base(shell)
    {
        Func<string, Task> OnApp(string command, ActStyle style = ActStyle.Quick) => app => Act(command, With("app", app), style);
        Func<Task> OnService(Func<string, Task> action) => OnRow(_services, "appName", "a service", action);

        Layout(
            Row(RefreshButton(), Button("Deploy...", () => Act("services deploy", style: ActStyle.Ask))),
            null, "", _services, "Deployed services",
            Row(
                Button("Status", OnService(OnApp("services show", ActStyle.Show))),
                Button("Logs", OnService(OnApp("logs journal", ActStyle.Log))),
                Button("Restart", OnService(OnApp("services restart"))),
                Button("Start", OnService(OnApp("services start"))),
                Button("Stop", OnService(OnApp("services stop"))),
                Button("Edit unit", OnService(EditUnit)),
                Button("Redeploy...", OnService(OnApp("services redeploy", ActStyle.Ask))),
                Button("Remove", OnService(OnApp("services remove")))));
    }

    private Task EditUnit(string app) =>
        Edit($"Unit of {app}", "services unit show", "services unit set",
            "Saved, then systemd reloads and the service restarts.", ("app", app));

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("services list") is not { } data) return false;
        _services.Show(data);
        if (_services.Rows.Count == 0) Say("No services deployed yet - Deploy... publishes one from a clone.");
        return true;
    }
}

internal sealed class RepositoriesPanel : TablePanel
{
    private readonly DataTable _repos = new(
        ("Clone", r => J.Str(r["name"])),
        ("Follows", r => J.Str(r["sourceText"])),
        ("Commit", r => J.Str(r["shortCommit"])),
        ("Changes", r => J.Bool(r["hasLocalChanges"]) ? "changed files" : ""),
        ("Remote", r => J.Str(r["remote"])));

    public RepositoriesPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnClone(Func<string, Task> action) => OnRow(_repos, "name", "a clone", action);

        Layout(
            Row(RefreshButton(),
                Button("Clone...", () => Act("repos clone", style: ActStyle.Ask)),
                Button("Deploy key", () => Act("repos deploy-key", style: ActStyle.Show)),
                Button("Install git", () => Act("repos install-git"))),
            null, "", _repos, "Clones in /srv/lrw/repos",
            Row(
                Button("Versions", OnClone(name => Act("repos refs", With("repository", name), ActStyle.Show))),
                Button("Update", OnClone(name => Act("repos update", With("name", name)))),
                Button("Switch version...", OnClone(name => Act("repos checkout", With("name", name), ActStyle.Ask))),
                Button("Remove", OnClone(name => Act("repos remove", With("name", name))))));
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("repos list") is not { } data) return false;
        _repos.Show(data);
        if (_repos.Rows.Count == 0) Say("No clones yet - Clone... adds one.");
        return true;
    }
}

internal sealed class DotnetPanel : Panel
{
    private readonly Label _facts = FactsLabel();

    public DotnetPanel(InteractiveApp shell) : base(shell)
    {
        var buttons = Row(
            Button("Refresh", async () =>
            {
                if (await LoadAsync()) Say("");
            }),
            Button("Install...", () => Act("dotnet install", style: ActStyle.Ask)),
            Button("Uninstall...", () => Act("dotnet uninstall", style: ActStyle.Ask)));
        buttons.Y = 0;
        var frame = Section(".NET on the server", _facts);
        frame.Y = Pos.Bottom(buttons) + 1;
        frame.Height = Dim.Fill(1);
        Add(buttons, frame);
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("dotnet status") is not { } data) return false;
        _facts.Text = J.Bool(data["installed"])
            ? Facts.Format(
                ("Default SDK", J.Str(data["defaultSdk"])),
                ("SDKs", J.Lines(data["sdks"])),
                ("Runtimes", J.Lines(data["runtimes"])))
            : "The .NET SDK is not installed - Install... sets it up (needed to publish services).";
        return true;
    }
}

internal sealed class PostgresPanel : Panel
{
    private readonly Label _facts = FactsLabel();
    private readonly DataTable _databases = new(("Database", r => J.Str(r["value"])));
    private readonly DataTable _users = new(
        ("Role", r => J.Str(r["name"])),
        ("Superuser", r => J.Bool(r["isSuperuser"]) ? "yes" : ""),
        ("Creates databases", r => J.Bool(r["canCreateDb"]) ? "yes" : ""));

    public PostgresPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnDb(Func<string, Task> action) => OnTable(_databases, "value", "a database", action);
        Func<Task> OnUser(Func<string, Task> action) => OnTable(_users, "name", "a role", action);

        var buttons = Row(
            Button("Refresh", async () =>
            {
                if (await LoadAsync()) Say("");
            }),
            Button("Install", () => Act("pg install")),
            Button("Restart", () => Act("pg restart")),
            Button("Network...", () => Act("pg network", style: ActStyle.Ask)),
            Button("Access rules", () => Act("pg hba list", style: ActStyle.Show)),
            Button("SQL...", () => Act("pg sql", Pick(("db", _databases.Pick("value"))), ActStyle.Ask | ActStyle.Show)),
            Button("New database...", () => Act("pg db create", style: ActStyle.Ask)),
            Button("New role...", () => Act("pg users create", style: ActStyle.Ask | ActStyle.Show)));
        buttons.Y = 0;

        var server = Section("Server", _facts);
        server.Y = Pos.Bottom(buttons) + 1;
        server.Height = Dim.Auto();

        var dbRow = Row(
            Button("Connection string...", () => Act("pg connstr",
                Pick(("db", _databases.Pick("value")), ("user", _users.Pick("name"))), ActStyle.Ask | ActStyle.Show)),
            Button("Drop", OnDb(db => Act("pg db drop", With("name", db)))));
        dbRow.Y = Pos.AnchorEnd();
        var databases = Section("Databases", _databases.View, dbRow);
        _databases.View.Height = Dim.Fill(dbRow);
        databases.Y = Pos.Bottom(server);
        databases.Width = Dim.Percent(45);
        databases.Height = Dim.Fill(1);

        var userRow = Row(
            Button("Password...", OnUser(user => Act("pg users password", With("name", user), ActStyle.Ask | ActStyle.Show))),
            Button("Grant...", () => Act("pg grant",
                Pick(("db", _databases.Pick("value")), ("user", _users.Pick("name"))), ActStyle.Ask)),
            Button("Drop", OnUser(user => Act("pg users drop", With("name", user)))));
        userRow.Y = Pos.AnchorEnd();
        var users = Section("Roles", _users.View, userRow);
        _users.View.Height = Dim.Fill(userRow);
        users.X = Pos.Right(databases);
        users.Y = Pos.Bottom(server);
        users.Height = Dim.Fill(1);

        Add(buttons, server, databases, users);
        _databases.UseActions((ButtonFlow)dbRow);
        _users.UseActions((ButtonFlow)userRow);
    }

    public override void Enter() => _databases.View.SetFocus();

    private Func<Task> OnTable(DataTable table, string field, string what, Func<string, Task> action) => async () =>
    {
        if (table.Pick(field) is { Length: > 0 } value) await action(value);
        else Say($"Select {what} first.", Mood.Bad);
    };

    private static Dictionary<string, string> Pick(params (string Name, string? Value)[] values) =>
        values.Where(v => !string.IsNullOrEmpty(v.Value)).ToDictionary(v => v.Name, v => v.Value!);

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("pg status") is not { } status) return false;
        if (!J.Bool(status["installed"]))
        {
            _facts.Text = "PostgreSQL is not installed - Install sets it up.";
            _databases.Show(null);
            _users.Show(null);
            return true;
        }

        var settings = status["settings"];
        _facts.Text = Facts.Format(
            ("Version", J.Str(status["version"])),
            ("State", J.Bool(status["online"]) ? "online" : "offline"),
            ("Listens on", settings == null ? null : $"{J.Str(settings["listenAddresses"])} port {J.Str(settings["port"])}"),
            ("Data", J.Str(settings?["dataDirectory"])),
            ("Problem", J.Str(status["problem"])));
        if (!J.Bool(status["online"])) return true;

        if (await Read("pg db list") is not { } databases || await Read("pg users list") is not { } users) return false;
        _databases.Show(databases);
        _users.Show(users);
        return true;
    }
}

internal sealed class NginxPanel : TablePanel
{
    private readonly Label _facts = FactsLabel();
    private readonly DataTable _sites = new(
        ("Site", r => J.Str(r["name"])),
        ("State", r => J.Bool(r["isEnabled"]) ? "on" : "off"),
        ("Domains", r => J.Str(r["serverNames"])),
        ("Serves", r => J.Str(r["target"])),
        ("HTTPS", r => J.Bool(r["isHttps"]) ? "yes" : ""),
        ("Ports", r => J.Str(r["listenPorts"])));

    public NginxPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnSite(Func<string, Task> action) => OnRow(_sites, "name", "a site", action);

        Layout(
            Row(RefreshButton(),
                Button("New site...", () => Act("nginx sites create", style: ActStyle.Ask)),
                Button("nginx.conf", () => Edit("/etc/nginx/nginx.conf", "nginx conf show", "nginx conf set",
                    "Kept only if nginx -t passes; otherwise the old file is put back.")),
                Button("Test", () => Act("nginx test", style: ActStyle.Show)),
                Button("Reload", () => Act("nginx reload")),
                Button("Error log", () => Act("nginx logs read", With("file", "error.log"), ActStyle.Log)),
                Button("Install", () => Act("nginx install")),
                Button("Open ports", () => Act("nginx open-firewall"))),
            _facts, "nginx", _sites, "Sites",
            Row(
                Button("Edit", OnSite(EditSite)),
                Button("Enable", OnSite(name => Act("nginx sites enable", With("name", name)))),
                Button("Disable", OnSite(name => Act("nginx sites disable", With("name", name)))),
                Button("HTTPS...", OnSite(name => Act("certs secure-site", With("site", name), ActStyle.Ask))),
                Button("Delete", OnSite(name => Act("nginx sites delete", With("name", name))))));

    }

    private Task EditSite(string name) =>
        Edit($"Site {name}", "nginx sites show", "nginx sites edit",
            "Saved only if the whole configuration passes nginx -t; otherwise the old file is put back.", ("name", name));

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("nginx status") is not { } status) return false;
        if (!J.Bool(status["installed"]))
        {
            _facts.Text = "nginx is not installed - Install sets it up.";
            _sites.Show(null);
            return true;
        }

        var firewall = status["firewall"];
        _facts.Text = Facts.Format(
            ("Version", J.Str(status["version"])),
            ("State", J.Str(status["state"])),
            ("Firewall", firewall == null || !J.Bool(firewall["ufwActive"]) ? "ufw off"
                : J.Bool(firewall["blocksWeb"]) ? "ufw blocks 80/443 - Open ports allows them" : "ufw allows 80/443"),
            ("Warning", J.Str(status["layoutWarning"])));

        if (await Read("nginx sites list") is not { } sites) return false;
        _sites.Show(sites);
        if (_sites.Rows.Count == 0) Say("No sites yet - New site... adds one.");
        return true;
    }
}

internal sealed class CertificatesPanel : TablePanel
{
    private readonly Label _facts = FactsLabel();
    private readonly DataTable _certs = new(
        ("Certificate", r => J.Str(r["name"])),
        ("Domains", r => J.Str(r["domains"])),
        ("Expires", r => J.Date(r["expiry"])),
        ("Days left", r => J.Str(r["daysLeft"])),
        ("Health", r => J.Str(r["health"])),
        ("Used by", r => J.Str(r["usedBy"])));

    public CertificatesPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnCert(Func<string, Task> action) => OnRow(_certs, "name", "a certificate", action);

        Layout(
            Row(RefreshButton(),
                Button("Secure a site...", () => Act("certs secure-site", style: ActStyle.Ask)),
                Button("New certificate...", () => Act("certs issue", style: ActStyle.Ask)),
                Button("Renew due", () => Act("certs renew-due")),
                Button("Test renewal", () => Act("certs test-renewal", style: ActStyle.Show)),
                Button("Install certbot", () => Act("certs install")),
                Button("Auto-renew", () => Act("certs auto-renew"))),
            _facts, "certbot", _certs, "Let's Encrypt certificates",
            Row(
                Button("Renew", OnCert(name => Act("certs renew", With("name", name)))),
                Button("Delete", OnCert(name => Act("certs delete", With("name", name))))));
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("certs status") is not { } status) return false;
        _facts.Text = J.Bool(status["installed"])
            ? Facts.Format(
                ("certbot", J.Str(status["version"])),
                ("nginx plugin", J.YesNo(status["hasNginxPlugin"], "installed", "missing - Install certbot adds it")),
                ("Renewal", J.Str(status["autoRenew"])))
            : "certbot is not installed - Install certbot sets it up with its nginx plugin.";

        if (!J.Bool(status["installed"]))
        {
            _certs.Show(null);
            return true;
        }
        if (await Read("certs list") is not { } certs) return false;
        _certs.Show(certs);
        return true;
    }
}

internal sealed class FirewallPanel : TablePanel
{
    private readonly Label _facts = FactsLabel();
    private readonly DataTable _rules = new(
        ("Port", r => J.Str(r["port"])),
        ("Protocol", r => J.Str(r["proto"])),
        ("From", r => J.Str(r["from"])),
        ("Action", r => J.Str(r["action"])),
        ("", r => J.Bool(r["isProtected"]) ? "keeps SSH open - cannot be deleted" : ""));

    public FirewallPanel(InteractiveApp shell) : base(shell)
    {
        Layout(
            Row(RefreshButton(),
                Button("Turn on", () => Act("firewall enable")),
                Button("Turn off", () => Act("firewall disable")),
                Button("Add rule...", () => Act("firewall add", style: ActStyle.Ask))),
            _facts, "ufw", _rules, "Rules",
            Row(Button("Delete rule", DeleteRule)));
    }

    private async Task DeleteRule()
    {
        if (_rules.Selected is not { } rule)
        {
            Say("Select a rule first.", Mood.Bad);
            return;
        }

        // The rule as the delete command matches it: "Anywhere" is any, a comment after the source is dropped
        var from = J.Str(rule["from"]).Replace("(v6)", "").Trim().Split(' ')[0];
        var given = With(("port", J.Str(rule["port"])), ("from", from.Equals("Anywhere", StringComparison.OrdinalIgnoreCase) ? "any" : from));
        if (J.Str(rule["proto"]) is { Length: > 0 } proto) given["proto"] = proto;
        if (J.Str(rule["action"]) is { Length: > 0 } action) given["action"] = action;
        await Act("firewall delete", given);
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("firewall status") is not { } status) return false;
        _facts.Text = Facts.Format(
            ("State", J.Bool(status["enabled"]) ? "on" : "off - every port is open"),
            ("SSH", $"port {J.Str(status["sshPorts"])} - always kept open"));
        _rules.Show(status["rules"]);
        return true;
    }
}

internal sealed class SecurityPanel : TablePanel
{
    private readonly Label _facts = FactsLabel();
    private readonly DataTable _keys = new(
        ("Key", r => J.Str(r["type"])),
        ("Bits", r => J.Str(r["bits"])),
        ("Fingerprint", r => J.Str(r["fingerprint"])),
        ("Comment", r => J.Str(r["comment"])));

    public SecurityPanel(InteractiveApp shell) : base(shell)
    {
        Layout(
            Row(RefreshButton(),
                Button("New key...", () => Act("security create-key", style: ActStyle.Ask)),
                Button("Add a key...", () => Act("security add-key", style: ActStyle.Ask)),
                Button("Add my key...", () => Act("security add-my-key", style: ActStyle.Ask)),
                Button("New admin...", () => Act("security create-admin", style: ActStyle.Ask)),
                Button("Passwords off", () => Act("security disable-password-login")),
                Button("Passwords on", () => Act("security enable-password-login")),
                Button("Root off", () => Act("security disable-root-login")),
                Button("Root on", () => Act("security enable-root-login"))),
            _facts, "Logins", _keys, "Keys that may log in",
            Row(Button("Remove key", OnRow(_keys, "fingerprint", "a key", f => Act("security remove-key", With("fingerprint", f))))));
    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("security status") is not { } s) return false;
        _facts.Text = Facts.Format(
            ("This session", $"{J.Str(s["user"])} with a {J.Str(s["sessionAuth"])} ({J.Str(s["privileges"])})"),
            ("Password logins", J.YesNo(s["passwordLoginAllowed"], "allowed", "off") + (J.Bool(s["passwordLoginDisabledByApp"]) ? " (turned off here)" : "")),
            ("Root logins", J.YesNo(s["rootLoginAllowed"], "allowed", "off") + (J.Bool(s["rootLoginDisabledByApp"]) ? " (turned off here)" : "")),
            ("Why not now", J.Str(s["cannotDisableBecause"])),
            ("Host key", J.Str(s["hostKey"])));
        _keys.Show(s["authorizedKeys"]);
        return true;
    }
}

internal sealed class LogsPanel : TablePanel
{
    private readonly DataTable _apps = new(("App", r => J.Str(r["value"])));

    public LogsPanel(InteractiveApp shell) : base(shell)
    {
        Func<Task> OnApp(Func<string, Task> action) => OnRow(_apps, "value", "an app", action);

        Layout(
            Row(RefreshButton(),
                Button("Journal of a unit...", () => Act("logs journal", With("app", ""), ActStyle.Ask | ActStyle.Log))),
            null, "", _apps, "Apps with a journal",
            Row(
                Button("Journal", OnApp(app => Act("logs journal", With("app", app), ActStyle.Log))),
                Button("Journal since...", OnApp(app => Act("logs journal", With("app", app), ActStyle.Ask | ActStyle.Log))),
                Button("Log files", OnApp(app => Act("logs files", With("app", app), ActStyle.Show))),
                Button("Read a log file...", OnApp(app => Act("logs read", With("app", app), ActStyle.Ask | ActStyle.Log)))));

    }

    protected override async Task<bool> LoadAsync()
    {
        if (await Read("logs apps") is not { } apps) return false;
        _apps.Show(apps);
        if (_apps.Rows.Count == 0) Say("No apps deployed yet.");
        return true;
    }
}
