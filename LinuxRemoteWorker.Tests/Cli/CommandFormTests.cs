using LinuxRemoteWorker.Cli.Commands;
using LinuxRemoteWorker.Cli.Framework;
using LinuxRemoteWorker.Cli.Interactive;

namespace LinuxRemoteWorker.Tests.Cli;

public class CommandFormTests
{
    private static readonly CommandCatalog Real = CommandCatalog.Build(typeof(ServiceCommands).Assembly);

    private static CommandForm Demo(string path) =>
        new(InteractiveScript.Demo.Commands.Single(c => c.FullPath == $"demo {path}"));

    private static CommandForm Lrw(string path) => new(Real.Commands.Single(c => c.FullPath == path));

    private static FormField Field(CommandForm form, string name) => form.Fields.Single(f => f.Name == name);

    [Fact]
    public void Fields_start_at_their_defaults_and_a_required_one_must_be_filled()
    {
        var form = Demo("echo");

        Assert.Equal(["text", "times", "loud", "color"], form.Fields.Select(f => f.Name));
        Assert.Equal("1", Field(form, "times").Value);
        Assert.False(Field(form, "loud").Toggled);
        Assert.Equal("red", Field(form, "color").Value);

        Assert.False(form.TryBuild(out _));
        Assert.Equal("text is required.", Field(form, "text").Error);

        Field(form, "text").Value = "  hello  ";
        Assert.True(form.TryBuild(out var values));
        Assert.Null(form.FirstError);
        // Only what differs from the defaults goes to the command; text is trimmed as a shell would split it
        Assert.Equal(new Dictionary<string, object?> { ["text"] = "hello" }, values);
    }

    [Fact]
    public void Values_are_converted_and_refused_as_on_the_command_line()
    {
        var form = Demo("echo");
        Field(form, "text").Value = "hi";
        Field(form, "times").Value = "two";

        Assert.False(form.TryBuild(out _));
        Assert.Contains("--times expects a whole number, got 'two'", Field(form, "times").Error);

        Field(form, "times").Value = "3";
        Field(form, "loud").Toggled = true;
        Field(form, "color").Value = "green";
        Assert.True(form.TryBuild(out var values));
        Assert.Equal(3, values["times"]);
        Assert.Equal(true, values["loud"]);
        Assert.Equal("green", values["color"]);
    }

    [Fact]
    public void The_command_line_shows_what_the_form_adds_up_to()
    {
        var form = Demo("echo");
        Field(form, "text").Value = "hello world";
        Field(form, "times").Value = "2";
        Field(form, "loud").Toggled = true;
        Field(form, "color").Value = "green";

        Assert.Equal("lrw demo echo 'hello world' --times 2 --loud --color green", form.CommandLine(null, windows: false));
        Assert.Equal("lrw demo echo 'hello world' --times 2 --loud --color green --profile prod", form.CommandLine("prod", windows: false));
    }

    [Fact]
    public void A_switch_that_is_on_by_default_is_turned_off_with_no()
    {
        var form = Lrw("system info");
        Assert.Equal("lrw system info", form.CommandLine(null, false));

        Field(form, "probe").Toggled = false;
        Assert.True(form.TryBuild(out var values));
        Assert.Equal(false, values["probe"]);
        Assert.Equal("lrw system info --no-probe", form.CommandLine(null, false));
    }

    [Fact]
    public void After_a_skipped_positional_the_next_one_is_given_by_name()
    {
        var form = Demo("pair");
        Field(form, "second").Value = "two";
        Assert.Equal("lrw demo pair --second two", form.CommandLine(null, false));

        Field(form, "first").Value = "one";
        Assert.Equal("lrw demo pair one two", form.CommandLine(null, false));
    }

    [Fact]
    public void A_secret_reaches_the_command_but_never_the_command_line()
    {
        var form = Lrw("profiles add");
        Field(form, "name").Value = "prod";
        Field(form, "host").Value = "203.0.113.10";
        Field(form, "password").Value = "s3cret";

        Assert.Equal(FieldKind.Secret, Field(form, "password").Kind);
        Assert.True(form.TryBuild(out var values));
        Assert.Equal("s3cret", values["password"]);
        var line = form.CommandLine(null, false);
        Assert.Contains("--password ***", line);
        Assert.DoesNotContain("s3cret", line);
    }

    [Fact]
    public void A_destructive_command_has_no_confirm_field_and_its_command_line_says_yes()
    {
        var form = Demo("wipe");
        Field(form, "what").Value = "cache";

        Assert.DoesNotContain(form.Fields, f => f.Parameter.IsConfirm);
        Assert.True(form.TryBuild(out var values));
        Assert.False(values.ContainsKey(CommandCatalog.ConfirmName)); // only the confirmation adds it
        Assert.Equal("lrw demo wipe cache --yes", form.CommandLine(null, false));
    }

    [Fact]
    public void Several_lines_of_content_are_counted_and_standard_input_is_refused()
    {
        var form = Lrw("services unit set");
        Field(form, "app").Value = "shop";
        var content = Field(form, "content");
        Assert.Equal(FieldKind.Content, content.Kind);

        content.Value = "[Unit]\nDescription=shop\n[Service]\n";
        Assert.True(form.TryBuild(out var values));
        Assert.Equal("[Unit]\nDescription=shop\n[Service]\n", values["content"]);
        Assert.Equal("lrw services unit set shop --content <3 lines>", form.CommandLine(null, false));

        content.Value = "@-";
        Assert.False(form.TryBuild(out _));
        Assert.Contains("@-", content.Error);
    }

    [Fact]
    public void A_choice_without_a_default_can_be_left_out()
    {
        var form = Lrw("firewall delete");
        var proto = Field(form, "proto");

        Assert.Equal(FieldKind.Choice, proto.Kind);
        Assert.Equal("", proto.Options[0]);
        Assert.Null(proto.Given);
    }

    [Theory]
    [InlineData("plain-word_1.2:3", false, "plain-word_1.2:3")]
    [InlineData("two words", false, "'two words'")]
    [InlineData("it's", false, "'it'\\''s'")]
    [InlineData("it's", true, "'it''s'")]
    [InlineData("@unit.service", false, "@unit.service")]
    [InlineData("@unit.service", true, "'@unit.service'")]
    [InlineData("a,b", true, "'a,b'")]
    [InlineData("~/key", false, "'~/key'")]
    [InlineData("", false, "''")]
    public void Words_are_quoted_for_the_shell_only_when_needed(string value, bool windows, string expected)
    {
        Assert.Equal(expected, CommandForm.Quote(value, windows));
    }

    [Theory]
    [InlineData("\u001b[1;32mgreen\u001b[0m text", "green text")]
    [InlineData("50%\r75%\r100% done", "100% done")]
    [InlineData("a\tb", "a       b")]
    [InlineData("title\u001b]0;window\u0007 rest", "title rest")]
    [InlineData("bell\u0007 gone\r\n", "bell gone")]
    public void Remote_output_is_shown_as_a_terminal_would_leave_it(string raw, string shown)
    {
        Assert.Equal(shown, OutputText.Clean(raw));
    }
}

