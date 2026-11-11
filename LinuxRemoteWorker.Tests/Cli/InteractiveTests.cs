using LinuxRemoteWorker.Cli.Commands;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Interactive;
using LinuxRemoteWorker.Cli.Session;
using LinuxRemoteWorker.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace LinuxRemoteWorker.Tests.Cli;

/// <summary>All commands: any command through its form, with the demo commands of the test kit.</summary>
public class InteractiveCommandsTests
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public void A_command_is_found_by_typing_filled_in_and_run()
    {
        var text = Unique("hello");
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("echo").WaitFor("demo echo  Return")
            .Press(Key.Enter).WaitFor("text *")
            .Type(text).WaitFor($"$ lrw demo echo {text}")
            .Press(Key.Enter).WaitFor("Done in")
            .WaitFor($"said {text} in red")
            .Run();

        Assert.Contains($"echo {text}", DemoCommands.Calls);
    }

    [Fact]
    public void Up_from_the_first_command_goes_to_the_search_and_Down_comes_back()
    {
        var focus = new List<string>();
        void Focused(Terminal.Gui.App.IApplication app) => focus.Add(app.TopRunnableView?.MostFocused?.GetType().Name ?? "");

        new InteractiveScript()
            .WaitFor("Search:")
            .Type("demo").WaitFor("demo echo")
            .Press(Key.CursorDown).Check(Focused)    // from the search into the list
            .Press(Key.CursorDown).Press(Key.CursorUp).Check(Focused) // second command, back to the first: still the list
            .Press(Key.CursorUp).Check(Focused)      // first command, up: the search
            .Press(Key.CursorDown).Check(Focused)    // and back into the list
            .Run();

        Assert.Equal(["ListView", "ListView", "TextField", "ListView"], focus);
    }

    [Fact]
    public void In_a_form_Up_and_Down_go_from_field_to_field_and_a_choice_takes_Left_and_Right()
    {
        var focus = new List<string>();
        void Focused(Terminal.Gui.App.IApplication app)
        {
            var view = app.TopRunnableView?.MostFocused;
            focus.Add(view?.SuperView is Terminal.Gui.Views.OptionSelector ? "choice" : view?.GetType().Name ?? "");
        }

        new InteractiveScript()
            .WaitFor("Search:")
            .Type("echo").WaitFor("demo echo")
            .Press(Key.Enter).WaitFor("text *")
            .Type("hi").Check(Focused)               // text
            .Press(Key.CursorDown).Check(Focused)    // times
            .Press(Key.CursorDown).Check(Focused)    // loud
            .Press(Key.CursorDown).Check(Focused)    // color: Down reaches it without changing it
            .Press(Key.CursorDown).Check(Focused)    // past the last field: Run
            .Press(Key.CursorUp).Check(Focused)      // back on the choice
            .Press(Key.CursorUp).Press(Key.CursorDown).WaitFor("$ lrw demo echo hi") // through it again: still red
            .Press(Key.CursorRight).WaitFor("$ lrw demo echo hi --color green")       // Right picks the next colour
            .Run();

        Assert.Equal(["TextField", "TextField", "CheckBox", "choice", "Button", "choice"], focus);
    }

    [Fact]
    public void A_slash_typed_into_a_field_is_text()
    {
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("echo").WaitFor("demo echo")
            .Press(Key.Enter).WaitFor("text *")
            .Type("a/b").WaitFor("$ lrw demo echo a/b")
            .Run();
    }

    [Fact]
    public void A_command_without_parameters_runs_at_once()
    {
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("status").WaitFor("demo status")
            .Press(Key.Enter).WaitFor("Done in")
            .WaitFor("state  fine")
            .Run();
    }

    [Fact]
    public void A_wrong_value_keeps_the_form_open_with_the_reason()
    {
        var text = Unique("never");
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("echo").WaitFor("demo echo")
            .Press(Key.Enter).WaitFor("text *")
            .Type(text)
            .Press(Key.Tab).Type("x").WaitFor("--times x") // a field reached with Tab is selected: typing replaces it
            .Press(Key.Enter).WaitFor("--times expects a whole number, got 'x'")
            .Run();

        Assert.DoesNotContain($"echo {text}", DemoCommands.Calls);
    }

    [Fact]
    public void A_destructive_command_runs_only_after_yes()
    {
        var what = Unique("data");
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("wipe").WaitFor("demo wipe")
            .Press(Key.Enter).WaitFor("what *")
            .Type(what).Press(Key.Enter).WaitFor("Destructive command")
            .Press(Key.Enter).WaitFor("Run...") // No is focused and the default: back to the form, nothing ran
            .Press(Key.Enter).WaitFor("Destructive command")
            .Press(Key.Y.WithAlt).WaitFor($"wiped {what}")
            .Run();

        Assert.Single(DemoCommands.Calls, $"wipe {what}");
    }

    [Fact]
    public void A_running_command_shows_its_output_and_Esc_cancels_it()
    {
        var line = Unique("line");
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("stream").WaitFor("demo stream")
            .Press(Key.Enter).WaitFor("line *")
            .Type(line).Press(Key.Enter).WaitFor("Running")
            .WaitFor(line)
            .Press(Key.Esc).WaitFor("Failed (cancelled)")
            .Run();
    }

    [Fact]
    public void A_command_that_does_not_stop_is_left_only_on_purpose()
    {
        var script = new InteractiveScript()
            .WaitFor("Search:")
            .Type("demo hang").WaitFor("demo hang")
            .Press(Key.Enter).WaitFor("Running")
            .Press(Key.Esc).WaitFor("Cancelling")
            .Press(Key.Esc).WaitFor("Still running")
            .Press(Key.Enter).WaitFor("Cancelling") // Wait is focused and the default
            .Press(Key.Esc).WaitFor("Still running")
            .Press(Key.L.WithAlt);
        try
        {
            script.Run();
        }
        finally
        {
            DemoCommands.HangRelease.Release();
        }

        Assert.Equal(ExitCodes.Cancelled, script.ExitCode);
    }

    [Fact]
    public void A_password_the_server_asks_for_is_typed_into_a_dialog()
    {
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("demo ask").WaitFor("demo ask")
            .Press(Key.Enter).WaitFor("Password for demo@host")
            .Type("pw123").WaitFor("∙∙∙∙∙") // masked
            .Press(Key.Enter).WaitFor("got 5 characters")
            .Press(Key.Esc)
            .Press(Key.Enter).WaitFor("Password for demo@host")
            .Press(Key.Esc).WaitFor("no answer")
            .Run();
    }

    [Fact]
    public void What_was_typed_in_a_form_stays_for_the_session()
    {
        var text = Unique("again");
        new InteractiveScript()
            .WaitFor("Search:")
            .Type("echo").WaitFor("demo echo")
            .Press(Key.Enter).WaitFor("text *")
            .Type(text).Press(Key.Esc)
            .Press(Key.Enter).WaitFor($"$ lrw demo echo {text}")
            .Run();
    }

    [Fact]
    public void The_screen_is_laid_out_again_when_the_terminal_changes_size()
    {
        static string[] Rows(string screen) => screen.Replace("\r", "").TrimEnd('\n').Split('\n');
        var script = new InteractiveScript()
            .WaitFor("Search:")
            .Resize(80, 24).WaitFor("Back / quit")
            .Resize(140, 36).WaitFor("Back / quit");
        script.Run();

        foreach (var (screen, width, height) in new[] { (script.Screens[1], 80, 24), (script.Screens[2], 140, 36) })
        {
            var rows = Rows(screen);
            Assert.Equal(height, rows.Length);
            Assert.All(rows, row => Assert.Equal(width, row.Length));
            Assert.Contains("Back / quit", rows[^2]); // the status bar stays on the last line inside the frame
        }
    }
}

/// <summary>The module screens over a server the test answers for.</summary>
public class InteractiveScreensTests
{
    private static readonly CommandCatalog Real = CommandCatalog.Build(typeof(ServiceCommands).Assembly);

    private static readonly List<ConnectionProfile> Profiles =
    [
        new() { Name = "prod", Host = "203.0.113.10", Username = "root", PrivateKeyPath = "/keys/prod" },
        new() { Name = "staging", Host = "203.0.113.20", Username = "deploy", PrivateKeyPath = "/keys/staging" }
    ];

    private static FakeServer Server() => new FakeServer()
        .Answer("connection test", new
        {
            hostname = "web-01", os = "Ubuntu 24.04 LTS", kernel = "Linux 6.8", remoteUser = "root", auth = "key",
            privileges = "root", hostKey = "ssh-ed25519 SHA256:test", roundTripMs = 12
        })
        .Answer("system info", new
        {
            overview = new
            {
                hostname = "web-01", os = "Ubuntu 24.04 LTS", kernel = "Linux 6.8", uptime = "3 days", cpuBusyPercent = 12.5,
                memory = "1.2 GiB of 4 GiB", disk = "10 GiB of 40 GiB", primaryIp = "203.0.113.10"
            },
            network = new { ipv4Addresses = new[] { "eth0  203.0.113.10/24" }, gatewayV4 = "203.0.113.1", dnsServers = new[] { "1.1.1.1" } }
        })
        .Answer("services list", new[]
        {
            new { appName = "shop-api", unitName = "lrw-shop-api.service", statusText = "active · enabled", versionText = "main @ abc1234" }
        })
        .Answer("services restart", values => FakeServer.Ok("services restart", null, $"{values["app"]}: restart done"));

    private static InteractiveScript Script(FakeServer server) => new()
    {
        Catalog = Real,
        Options = new InteractiveOptions
        {
            Resolve = options =>
            {
                var p = Profiles.Single(x => x.Name == options.Profile);
                return new ConnectionTarget(p.Host, p.Username, p.PrivateKeyPath, null, p.Name);
            },
            LoadProfiles = () => Profiles,
            DefaultProfile = () => "prod",
            Invoke = server.Invoke,
            Windows = false
        }
    };

    private static string? FocusedFrame(IApplication app) => app.TopRunnableView?.MostFocused?.SuperView?.Title;

    [Fact]
    public void Before_connecting_only_the_screens_of_this_computer_are_there()
    {
        var screen = "";
        Script(Server())
            .WaitFor("○ not connected").WaitFor("deploy@203.0.113.20")
            .Check(app => screen = app.Driver!.ToString() ?? "")
            .Run();

        Assert.Contains("All commands", screen);
        Assert.Contains("prod  ", screen);
        Assert.DoesNotContain("Services", screen);
        Assert.DoesNotContain("PostgreSQL", screen);
    }

    [Fact]
    public void Connecting_shows_the_server_on_top_and_its_screens()
    {
        var server = Server();
        var screen = "";
        Script(server)
            .WaitFor("prod")
            .Click("Connect").WaitFor("● prod").WaitFor("root@203.0.113.10")
            .WaitFor("Hostname  web-01")
            .Check(app => screen = app.Driver!.ToString() ?? "")
            .Run();

        Assert.Single(server.CallsOf("connection test"));
        foreach (var module in new[] { "System", "Services", "PostgreSQL", "Nginx", "Firewall", "Security", "Logs" })
            Assert.Contains(module, screen);
        Assert.Contains("lrw 0.2.3 - prod (root@203.0.113.10)", screen);
    }

    [Fact]
    public void A_button_acts_on_the_selected_row()
    {
        var server = Server();
        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Click("Restart").WaitFor("shop-api: restart done")
            .Run();

        Assert.Equal("shop-api", Assert.Single(server.CallsOf("services restart"))["app"]);
    }

    [Fact]
    public void A_screen_shows_what_the_server_has_in_its_tables()
    {
        var server = Server()
            .Answer("pg status", new { installed = true, version = "16.4", online = true, settings = new { listenAddresses = "localhost", port = 5432 } })
            .Answer("pg db list", new[] { "postgres", "shopdb" })
            .Answer("pg users list", new[] { new { name = "shopuser", isSuperuser = false, canCreateDb = true } });
        var screen = "";
        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown, 4).WaitFor("shopdb").WaitFor("shopuser")
            .Check(app => screen = app.Driver!.ToString() ?? "")
            .Run();

        Assert.Contains("localhost port 5432", screen);
        Assert.Contains("postgres", screen);
    }

    [Fact]
    public void A_destructive_button_asks_first_with_No_as_the_default()
    {
        var server = Server();
        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Click("Remove").WaitFor("Destructive command")
            .Press(Key.Enter)
            .Run();

        Assert.Empty(server.CallsOf("services remove"));
    }

    [Fact]
    public void A_unit_file_is_edited_in_place_and_stays_open_while_saving_fails()
    {
        var saves = 0;
        var server = Server()
            .Answer("services unit show", new { path = "/etc/systemd/system/lrw-shop-api.service", content = "[Unit]\nDescription=shop\n" })
            .Answer("services unit set", _ => ++saves == 1
                ? FakeServer.Fail("services unit set", "Job for lrw-shop-api.service failed")
                : FakeServer.Ok("services unit set", null, "Unit saved, systemd reloaded, service restarted"));

        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Click("Edit unit").WaitFor("Description=shop")
            .Type("#ok").Press(Key.S.WithAlt).WaitFor("Job for lrw-shop-api.service failed")
            .Press(Key.S.WithAlt).WaitFor("Unit saved, systemd reloaded, service restarted")
            .Run();

        var saved = server.CallsOf("services unit set").Select(v => (string)v["content"]!).ToList();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, content => Assert.Equal("#ok[Unit]\nDescription=shop\n", content));
        Assert.All(server.CallsOf("services unit set"), v => Assert.Equal("shop-api", v["app"]));
    }

    [Fact]
    public void Slash_searches_every_command_from_inside_a_screen()
    {
        var focus = "";
        Script(Server())
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Press(Key.Enter) // into the Services screen
            .Press(new Key('/')).WaitFor("Search:")
            .Type("redeploy").WaitFor("services redeploy")
            .Check(app => focus = app.TopRunnableView?.MostFocused?.GetType().Name ?? "")
            .Run();

        Assert.Equal("TextField", focus);
    }

    [Fact]
    public void A_journal_opens_at_its_newest_lines()
    {
        var journal = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"entry {i:000}"));
        var server = Server().Answer("logs journal", new { unit = "lrw-shop-api.service", since = "1 hour ago", text = journal });
        var screen = "";
        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Click("Logs").WaitFor("entry 120")
            .Check(app => screen = app.Driver!.ToString() ?? "")
            .Run();

        Assert.DoesNotContain("entry 001", screen);
        Assert.Equal("shop-api", Assert.Single(server.CallsOf("logs journal"))["app"]);
    }

    [Fact]
    public void In_a_table_Up_and_Down_pick_the_row_Left_and_Right_the_action_and_Enter_runs_it()
    {
        var server = Server().Answer("services show", new { appName = "shop-api", status = "active (running)" });
        var focus = new List<string>();
        void Focused(IApplication app) => focus.Add(app.TopRunnableView?.MostFocused switch
        {
            Terminal.Gui.Views.Button b => b.Text.Replace("_", ""),
            var v => v?.GetType().Name ?? ""
        });

        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorDown).WaitFor("shop-api")
            .Press(Key.Enter).Check(Focused)        // into the screen: the table
            .Press(Key.Enter).WaitFor("active (running)").Press(Key.Esc).WaitForMainWindow() // Enter runs the first action, Status
            .Press(Key.CursorRight, 2).Check(Focused) // Logs, then Restart - the focus stays in the table
            .Press(Key.Enter).WaitFor("shop-api: restart done").WaitForMainWindow()
            .Press(Key.CursorDown).Check(Focused)   // below the last row there is nothing to go to: it stays
            .Press(Key.CursorUp).Check(Focused)     // above the first: the menu on top
            .Press(Key.CursorRight).Check(Focused)  // along the menu
            .Press(Key.CursorDown).Check(Focused)   // and down into the table again
            .Run();

        Assert.Equal(["TableView", "TableView", "TableView", "Refresh", "Deploy...", "TableView"], focus);
        Assert.Equal("shop-api", Assert.Single(server.CallsOf("services show"))["app"]);
        Assert.Equal("shop-api", Assert.Single(server.CallsOf("services restart"))["app"]);
    }

    [Fact]
    public void A_log_file_is_read_whole_or_its_errors_only_from_the_table()
    {
        var server = new FakeServer()
            .Answer("applog list", new { directory = "/logs", files = new[] { new { name = "cli-2026-11-11.log", source = "cli", size = 2048 } } })
            .Answer("applog read", values => FakeServer.Ok("applog read",
                new { text = values.ContainsKey("filter") ? "only the errors" : "the whole file" }));
        var script = Script(server);

        script
            .WaitFor("App log")
            .Press(Key.CursorDown, 2).WaitFor("cli-2026-11-11.log")
            .Press(Key.Enter)                        // into the screen: the files
            .Press(Key.Enter).WaitFor("the whole file").Press(Key.Esc).WaitForMainWindow() // the first action: Read
            .Press(Key.CursorRight).Press(Key.Enter).WaitFor("only the errors") // Errors only
            .Run();

        var reads = server.CallsOf("applog read").ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read => Assert.Equal("cli-2026-11-11.log", read["file"]));
        Assert.Equal("ERROR", reads[1]["filter"]);
    }

    [Fact]
    public void Facts_are_no_stops_and_Tab_goes_from_one_table_to_the_next()
    {
        var server = Server()
            .Answer("pg status", new { installed = true, version = "16.4", online = true, settings = new { listenAddresses = "localhost", port = 5432 } })
            .Answer("pg db list", new[] { "shopdb" })
            .Answer("pg users list", new[] { new { name = "shopuser", isSuperuser = false, canCreateDb = true } });
        var focus = new List<string>();
        void Focused(IApplication app) => focus.Add(app.TopRunnableView?.MostFocused switch
        {
            Terminal.Gui.Views.Button b => b.Text.Replace("_", ""),
            var v => $"{v?.GetType().Name} in {v?.SuperView?.Title}"
        });

        Script(server)
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.Enter).Check(Focused)        // System: its first button
            .Press(Key.CursorDown).Check(Focused)   // Machine and Network below are facts, not stops: it stays
            .Press(Key.Esc).Press(Key.CursorDown, 4).WaitFor("shopuser")
            .Press(Key.Enter).Check(Focused)        // PostgreSQL: the databases
            .Press(Key.Tab).Check(Focused)          // the roles beside them
            .Press(Key.Tab.WithShift).Check(Focused) // and back
            .Run();

        Assert.Equal(["Refresh", "Refresh", "TableView in Databases", "TableView in Roles", "TableView in Databases"], focus);
    }

    [Fact]
    public void Disconnecting_takes_the_server_screens_away()
    {
        var screen = "";
        Script(Server())
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorUp).WaitFor("This session")
            .Click("Disconnect").WaitFor("○ not connected")
            .Check(app => screen = app.Driver!.ToString() ?? "")
            .Run();

        Assert.DoesNotContain("PostgreSQL", screen);
    }

    [Fact]
    public void Esc_goes_back_from_a_screen_to_the_modules_and_the_arrows_stay_in_them()
    {
        var focus = new List<string?>();
        void Frame(IApplication app) => focus.Add(FocusedFrame(app));

        Script(Server())
            .WaitFor("prod").Click("Connect").WaitFor("web-01")
            .Press(Key.CursorUp, 20).Check(Frame)   // past the first module
            .Press(Key.CursorDown, 30).Check(Frame) // past the last one
            .Press(Key.CursorRight).Check(Frame)    // the arrows do not cross into the screen
            .Press(Key.Tab).Check(Frame)            // Tab (or Enter) does
            .Press(Key.Esc).Check(Frame)            // and Esc comes back
            .Run();

        Assert.Equal("lrw", focus[0]);
        Assert.Equal("lrw", focus[1]);
        Assert.Equal("lrw", focus[2]);
        Assert.NotEqual("lrw", focus[3]);
        Assert.Equal("lrw", focus[4]);
    }
}
