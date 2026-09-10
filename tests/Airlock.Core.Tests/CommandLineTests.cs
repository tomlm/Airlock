using Airlock.Cli;

namespace Airlock.Tests;

/// <summary>
/// The top-level grammar. The behaviour that matters most is the boundary: everything from the
/// first positional token onward belongs to the wrapped command, so a switch Airlock also happens
/// to define is still forwarded rather than swallowed.
/// </summary>
public class CommandLineTests
{
    [Fact]
    public void NoArguments_PrintsHelpRatherThanBootingASandbox()
    {
        var result = CommandLine.Parse([]);

        Assert.Equal(InvocationKind.Help, result.Kind);
    }

    [Fact]
    public void BareCommand_IsAPassthrough()
    {
        var result = CommandLine.Parse(["claude"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["claude"], result.Arguments);
    }

    [Fact]
    public void SwitchesAfterTheCommand_BelongToTheCommand()
    {
        var result = CommandLine.Parse(["claude", "--resume"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["claude", "--resume"], result.Arguments);
    }

    [Fact]
    public void AirlocksOwnSwitchAfterTheCommand_IsStillForwarded()
    {
        // --project is ours, but only before the command starts. After it, it is Claude's problem.
        var result = CommandLine.Parse(["claude", "--project:x"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["claude", "--project:x"], result.Arguments);
        Assert.Null(result.ProjectPath);
    }

    [Fact]
    public void OptionsBeforeTheCommand_AreAirlocks()
    {
        var result = CommandLine.Parse(["--project:S:\\src\\Foo", "claude", "--resume"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal("S:\\src\\Foo", result.ProjectPath);
        Assert.Equal(["claude", "--resume"], result.Arguments);
    }

    [Fact]
    public void ShortOptionAlias_Works()
    {
        var result = CommandLine.Parse(["-p:S:\\src\\Foo", "shell"]);

        Assert.Equal("S:\\src\\Foo", result.ProjectPath);
        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Shell, result.Verb);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("stop")]
    [InlineData("doctor")]
    [InlineData("connect")]
    [InlineData("shell")]
    [InlineData("trust")]
    public void ReservedWord_DispatchesToAirlock(string verb)
    {
        var result = CommandLine.Parse([verb]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(verb, result.Verb);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void VerbArguments_ArePassedOnVerbatim()
    {
        var result = CommandLine.Parse(["tools", "update", "--force"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Tools, result.Verb);
        Assert.Equal(["update", "--force"], result.Arguments);
    }

    [Fact]
    public void RunVerb_TreatsTheRestAsTheCommand()
    {
        var result = CommandLine.Parse(["run", "dotnet", "test"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Run, result.Verb);
        Assert.Equal(["dotnet", "test"], result.Arguments);
    }

    [Fact]
    public void DoubleDash_RunsAReservedWordInsideTheSandboxInstead()
    {
        // The escape hatch for the one real ambiguity in the grammar.
        var result = CommandLine.Parse(["--", "list"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["list"], result.Arguments);
        Assert.Null(result.Verb);
    }

    [Fact]
    public void DoubleDash_AfterAirlockOptions_StillForces()
    {
        var result = CommandLine.Parse(["--verbose", "--", "doctor", "--all"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.True(result.Verbose);
        Assert.Equal(["doctor", "--all"], result.Arguments);
    }

    [Fact]
    public void DoubleDash_AfterTheCommandStarted_IsNotATerminator()
    {
        // Here the command is already 'claude', so '--' is just one of its arguments.
        var result = CommandLine.Parse(["claude", "--", "list"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal("claude", result.Arguments[0]);
    }

    [Fact]
    public void UnknownAirlockSwitch_IsRejectedRatherThanForwarded()
    {
        // Quietly handing a mistyped Airlock flag to the agent would be worse than failing.
        var result = CommandLine.Parse(["--bogus", "claude"]);

        Assert.Equal(InvocationKind.Usage, result.Kind);
    }

    [Fact]
    public void Flags_AreCollected()
    {
        var result = CommandLine.Parse(["-v", "--dry-run", "--keep", "--trust-project", "claude"]);

        Assert.True(result.Verbose);
        Assert.True(result.DryRun);
        Assert.True(result.Keep);
        Assert.True(result.TrustProject);
        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["claude"], result.Arguments);
    }

    [Fact]
    public void ReservedWordAsAnArgumentOfACommand_IsNotAVerb()
    {
        var result = CommandLine.Parse(["dotnet", "list", "package"]);

        Assert.Equal(InvocationKind.Passthrough, result.Kind);
        Assert.Equal(["dotnet", "list", "package"], result.Arguments);
    }
}
