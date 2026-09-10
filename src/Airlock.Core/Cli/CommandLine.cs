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

    /// <summary>One of Airlock's own verbs — <c>list</c>, <c>doctor</c>, <c>tools</c>, …</summary>
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

    /// <summary>An extra config file layered on top of the user and project layers.</summary>
    public string? ConfigPath { get; private init; }

    public bool Verbose { get; private init; }

    /// <summary>Show the resolved plan — config XML, mounts, ssh argv — and start nothing.</summary>
    public bool DryRun { get; private init; }

    /// <summary>Leave the sandbox running when the session ends.</summary>
    public bool Keep { get; private init; }

    /// <summary>Approve this project's elevated <c>.airlock.json</c> settings without prompting.</summary>
    public bool TrustProject { get; private init; }

    /// <summary>Sandbox memory in MB. Only meaningful when it is this call that starts it.</summary>
    public int MemoryInMB { get; private init; }

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
            .Example("airlock open claude", "open this project and start Claude Code in it")
            .Example("airlock open", "open this project and get a shell in it")
            .Example("airlock open dotnet test", "run one command in this project and come back")
            .Example("airlock --project:S:\\src\\Foo open claude --resume", "open a different project")
            .Example("airlock list", "show every project currently open in the sandbox")
            .Rest("verb", "open | start | stop | list | connect | doctor | tools")
            .Option(out string? project, "project folder; defaults to the current directory", "p")
            .Option(out string? config, "an extra config file layered on top of the usual ones")
            .Switch(out bool verbose, "log every wsb and ssh invocation", "v")
            .Switch(out bool dryRun, "show the resolved plan and exit without starting anything")
            .Switch(out bool keep, "leave the sandbox running when the session ends")
            .Switch(out bool trustProject, "accept this project's .airlock.json elevated settings")
            .Option(out int? memory, "sandbox memory in MB, used only when starting it")
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
            ConfigPath = config,
            Verbose = verbose,
            DryRun = dryRun,
            Keep = keep,
            TrustProject = trustProject,
            MemoryInMB = memory ?? 0,
            UsageText = parsed.UsageText,
            Verb = verb,
            Arguments = arguments,
            Error = error,
        };
    }
}
