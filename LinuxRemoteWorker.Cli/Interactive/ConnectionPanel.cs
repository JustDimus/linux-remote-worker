using System.Text.Json.Nodes;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LinuxRemoteWorker.Cli.Interactive;

/// <summary>
/// The first screen: the saved servers and the one this session is connected to. Connect (or Enter on a server)
/// logs in and tests the connection; only then do the server's own screens appear on the left.
/// </summary>
internal sealed class ConnectionPanel : Panel
{
    private readonly Label _session;
    private readonly DataTable _servers = new(
        ("Server", r => J.Str(r["name"])),
        ("Address", r => J.Str(r["address"])),
        ("Login", r => J.Str(r["login"])),
        ("", r => J.Str(r["notes"])));
    private string? _lastFailure;

    public ConnectionPanel(InteractiveApp shell) : base(shell)
    {
        var buttons = Row(
            Button("Add server...", () => Act("profiles add", style: ActStyle.Ask)),
            Button("Disconnect", Disconnect));
        buttons.Y = 0;
        var actions = Row(
            Button("Connect", Connect),
            Button("Edit...", () => OnServer(name => Act("profiles update", With("name", name), ActStyle.Ask))),
            Button("Make default", () => OnServer(name => Act("profiles use", With("name", name)))),
            Button("Remove", () => OnServer(name => Act("profiles remove", With("name", name)))));
        actions.Y = Pos.AnchorEnd() - 1;

        _session = FactsLabel();
        var session = Section("This session", _session);
        session.Y = Pos.Bottom(buttons) + 1;
        session.Height = Dim.Auto();

        var servers = Section("Saved servers", _servers.View);
        servers.Y = Pos.Bottom(session);
        servers.Height = Dim.Fill(actions);

        Add(buttons, session, servers, actions);
        _servers.UseActions((ButtonFlow)actions);
    }

    public override void Enter() => _servers.View.SetFocus();

    /// <summary>Connects as the command line asked for (--profile, --host ...).</summary>
    public void ConnectTo(ConnectionOptions options) => Run(() => ConnectAsync(options, options.Profile ?? options.Host ?? "the server"));

    protected override Task<bool> LoadAsync()
    {
        ShowSession();
        try
        {
            var defaultName = Shell.DefaultProfile();
            var current = Shell.Server?.ProfileName;
            _servers.Show(new JsonArray(Shell.LoadProfiles().Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["address"] = $"{p.Username}@{p.Host}",
                ["login"] = p.AuthMethod == SshAuthMethod.Password ? p.HasSavedPassword ? "password (saved)" : "password" : "key",
                ["notes"] = string.Join(" · ", new[]
                {
                    p.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? "connected" : null,
                    p.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase) ? "default" : null
                }.Where(n => n != null))
            }).ToArray()));

            // Start on the default server, so Enter connects to it
            if (current == null && defaultName != null && _servers.Rows.ToList().FindIndex(r => J.Str(r["name"]) == defaultName) is >= 0 and var i)
                _servers.View.Value = new Terminal.Gui.Views.TableSelection(new System.Drawing.Point(0, i));

            if (_servers.Rows.Count == 0) Say("No saved servers yet - Add server... saves one.");
            return Task.FromResult(true);
        }
        catch (OperationException ex)
        {
            Say(ex.Message, Mood.Bad);
            return Task.FromResult(false);
        }
    }

    private void ShowSession()
    {
        if (Shell.ServerFacts is not { } f)
        {
            _session.Text = _lastFailure ?? "Not connected. Pick a saved server below and press Enter (or Connect).";
            return;
        }

        _session.Text = Facts.Format(
            ("Connected to", Shell.ServerLabel),
            ("Hostname", J.Str(f["hostname"])),
            ("System", J.Str(f["os"])),
            ("Kernel", J.Str(f["kernel"])),
            ("Logged in as", $"{J.Str(f["remoteUser"])} with a {J.Str(f["auth"])} ({J.Str(f["privileges"])})"),
            ("Host key", J.Str(f["hostKey"]) + (J.Bool(f["hostKeyNew"]) ? "  (new - remembered now)" : "")),
            ("Round trip", $"{J.Str(f["roundTripMs"])} ms"));
    }

    private async Task Connect()
    {
        if (_servers.Pick("name") is not { } name)
        {
            Say("Pick a saved server first, or add one with Add server...", Mood.Bad);
            return;
        }
        await ConnectAsync(new ConnectionOptions { Profile = name }, name);
    }

    private async Task ConnectAsync(ConnectionOptions options, string name)
    {
        Say($"Connecting to {name}...", Mood.Busy);
        var result = await Shell.ConnectAsync(options);
        _lastFailure = result.Ok ? null : $"Could not connect to {name}.\n\n{ResultText.Render(result)}";
        await LoadAsync();
        Say(result.Ok ? $"Connected to {Shell.ServerLabel}." : ResultText.Summary(result), result.Ok ? Mood.Ok : Mood.Bad);
    }

    private async Task Disconnect()
    {
        if (!Shell.IsConnected)
        {
            Say("Not connected.");
            return;
        }
        var server = Shell.ServerLabel;
        Shell.Disconnect();
        await LoadAsync();
        Say($"Disconnected from {server}.");
    }

    private async Task OnServer(Func<string, Task> action)
    {
        if (_servers.Pick("name") is { } name) await action(name);
        else Say("Pick a saved server first.", Mood.Bad);
    }
}
