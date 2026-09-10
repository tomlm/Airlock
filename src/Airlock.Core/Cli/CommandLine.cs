// Aliased because this namespace is itself called Cli, which would otherwise shadow the type.
using CShellCli = CShellNet.Cli;

namespace Airlock.Cli;

/// <summary>What the user asked for, after the top-level split.</summary>
public enum InvocationKind
{
    /// <summary>Print usage and exit 0. Also what a bare <c>airlock</c> does.</summary>
    Help,

    /// <summary>Print the version and exit 0.</summary>
    Version,

    /// <summary>One of Airlock's own verbs — <c>open</c>, <c>list</c>, <c>tools</c>, …</summary>
    Verb,

    /// <summary>The command line was not usable. <see cref="CommandLine.Error"/> says why.</summary>
    Usage,
}

/// <summary>
/// Splits <c>argv</c> into Airlock's own options, an optional verb, and a verbatim passthrough.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>airlock [options] &lt;verb&gt; [verb args]</c>. Options are recognised only
/// <i>before</i> the verb; from there on everything belongs to the verb, so
/// <c>airlock open claude --resume</c> forwards <c>--resume</c> to Claude rather than rejecting it.
/// </para>
/// <para>
/// That boundary, and the <c>--</c> terminator, come from <see cref="CShellCli"/>'s
/// <c>Rest</c>. Following its convention, option values <b>attach</b>:
/// <c>--project:C:\src</c>, never <c>--project C:\src</c>. A separated value would make
/// <c>airlock --project claude</c> ambiguous between a path and the agent to run.
/// </para>
/// </remarks>
public sealed class CommandLine
{
    private CommandLine(InvocationKind kind)
    {
        Kind = kind;
    }

    public InvocationKind Kind { get; private init; }

    /// <summary>Explicit project folder, or null to use the current directory.</summary>
    public string? ProjectPath { get; private init; }

    /// <summary>Show what would be mounted and start nothing.</summary>
    public bool DryRun { get; private init; }

    /// <summary>The verb, when <see cref="Kind"/> is <see cref="InvocationKind.Verb"/>.</summary>
    public string? Verb { get; private init; }

    /// <summary>Arguments after the verb, or the whole command for a passthrough. Verbatim.</summary>
    public IReadOnlyList<string> Arguments { get; private init; } = [];

    /// <summary>Usage text produced by the parser, for <see cref="InvocationKind.Help"/>.</summary>
    public string? UsageText { get; private init; }

    /// <summary>Why the command line was rejected.</summary>
    public string? Error { get; private init; }

    /// <summary>
    /// True when the parser has already written the help or error text to the console itself.
    /// CShell's <c>TryParse</c> reports as it parses, so the caller must not print it a second time.
    /// </summary>
    public bool AlreadyReported { get; private init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsed = CShellCli.For(args)
            .Program("airlock")
            .Description(
                "Open an airlock into a disposable Windows Sandbox and work in it. The projects you " +
                "open are the only writable folders on your machine.")
            .Example("airlock start", "bring the sandbox up with everything configured, and show it")
            .Example("airlock open claude", "open this folder as an airlock and start Claude in it")
            .Example("airlock open", "open this folder as an airlock and get a shell in it")
            .Example("airlock --project:S:\\src\\Foo open", "open a folder other than this one")
            .Example("airlock tools add S:\\bin\\mytools", "mount a folder read-only onto the PATH")
            .Example("airlock list", "every airlock, and whether it is open right now")
            .Rest("verb", ReservedVerbs.Usage)
            .Option(out string? project, "the folder to open; defaults to the current directory", "p")
            .Switch(out bool dryRun, "show what would be mounted and exit without starting anything")
            .TryParse();

        if (parsed.ShouldExit)
        {
            return parsed.HelpRequested
                ? new CommandLine(InvocationKind.Help) { UsageText = parsed.UsageText, AlreadyReported = true }
                : new CommandLine(InvocationKind.Usage) { Error = parsed.Error, AlreadyReported = true };
        }

        var rest = parsed.Rest;

        // Bare `airlock` prints help: booting a VM should always be something you asked for.
        var kind = InvocationKind.Help;
        string? verb = null;
        string? error = null;
        IReadOnlyList<string> arguments = [];

        if (rest.Count > 0)
        {
            if (ReservedVerbs.Contains(rest[0]))
            {
                kind = InvocationKind.Verb;
                verb = rest[0];
                arguments = [.. rest.Skip(1)];
            }
            else
            {
                // Everything runs through a verb, so a bare command is a mistake with an obvious
                // correction rather than something to guess at.
                kind = InvocationKind.Usage;
                error = $"'{rest[0]}' is not an airlock command. " +
                        $"To run it in the sandbox: airlock open {string.Join(' ', rest)}";
            }
        }

        return new CommandLine(kind)
        {
            ProjectPath = project,
            DryRun = dryRun,
            UsageText = parsed.UsageText,
            Verb = verb,
            Arguments = arguments,
            Error = error,
        };
    }
}
