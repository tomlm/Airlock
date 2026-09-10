using Airlock.Cli;

namespace Airlock.Tests;

/// <summary>
/// The top-level grammar. Every invocation starts with a verb, and the boundary that matters is
/// that everything after it belongs to the verb - so a switch Airlock also happens to define is
/// still forwarded to the command being wrapped rather than swallowed.
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
    public void Open_WithNoCommand_IsAShell()
    {
        var result = CommandLine.Parse(["open"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Open, result.Verb);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void Open_CarriesTheCommand()
    {
        var result = CommandLine.Parse(["open", "claude"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Open, result.Verb);
        Assert.Equal(["claude"], result.Arguments);
    }

    [Fact]
    public void SwitchesAfterTheCommand_BelongToTheCommand()
    {
        var result = CommandLine.Parse(["open", "claude", "--resume"]);

        Assert.Equal(["claude", "--resume"], result.Arguments);
    }

    [Fact]
    public void AirlocksOwnSwitchAfterTheVerb_IsStillForwarded()
    {
        // --project is ours, but only before the verb. After it, it is Claude's problem.
        var result = CommandLine.Parse(["open", "claude", "--project:x"]);

        Assert.Equal(["claude", "--project:x"], result.Arguments);
        Assert.Null(result.ProjectPath);
    }

    [Fact]
    public void OptionsBeforeTheVerb_AreAirlocks()
    {
        var result = CommandLine.Parse(["--project:S:\\src\\Foo", "open", "claude", "--resume"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal("S:\\src\\Foo", result.ProjectPath);
        Assert.Equal(["claude", "--resume"], result.Arguments);
    }

    [Fact]
    public void ShortOptionAlias_Works()
    {
        var result = CommandLine.Parse(["-p:S:\\src\\Foo", "open"]);

        Assert.Equal("S:\\src\\Foo", result.ProjectPath);
        Assert.Equal(ReservedVerbs.Open, result.Verb);
    }

    [Fact]
    public void Run_IsAnAliasForOpen()
    {
        var result = CommandLine.Parse(["run", "dotnet", "test"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Run, result.Verb);
        Assert.Equal(["dotnet", "test"], result.Arguments);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("stop")]
    [InlineData("doctor")]
    [InlineData("connect")]
    [InlineData("start")]
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

        Assert.Equal(ReservedVerbs.Tools, result.Verb);
        Assert.Equal(["update", "--force"], result.Arguments);
    }

    [Fact]
    public void AVerbNameAfterOpen_IsJustACommand()
    {
        // The reason the grammar requires a verb: this used to need `airlock -- list` to
        // disambiguate, and now it cannot be ambiguous at all.
        var result = CommandLine.Parse(["open", "list"]);

        Assert.Equal(InvocationKind.Verb, result.Kind);
        Assert.Equal(ReservedVerbs.Open, result.Verb);
        Assert.Equal(["list"], result.Arguments);
    }

    [Fact]
    public void BareCommandWithoutAVerb_IsRejectedWithTheFix()
    {
        var result = CommandLine.Parse(["claude", "--resume"]);

        Assert.Equal(InvocationKind.Usage, result.Kind);
        Assert.Contains("airlock open claude --resume", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAirlockSwitch_IsRejectedRatherThanForwarded()
    {
        // Quietly handing a mistyped Airlock flag to the agent would be worse than failing.
        var result = CommandLine.Parse(["--bogus", "open", "claude"]);

        Assert.Equal(InvocationKind.Usage, result.Kind);
    }

    [Fact]
    public void Flags_AreCollected()
    {
        var result = CommandLine.Parse(["-v", "--dry-run", "--keep", "--trust-project", "open", "claude"]);

        Assert.True(result.Verbose);
        Assert.True(result.DryRun);
        Assert.True(result.Keep);
        Assert.True(result.TrustProject);
        Assert.Equal(["claude"], result.Arguments);
    }

    [Fact]
    public void Memory_IsParsedAsANumber()
    {
        var result = CommandLine.Parse(["--memory:12288", "start"]);

        Assert.Equal(12288, result.MemoryInMB);
    }
}
