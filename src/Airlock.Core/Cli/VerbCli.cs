using CShellCli = CShellNet.Cli;

namespace Airlock.Cli;

/// <summary>
/// The parser for one verb's own arguments.
/// </summary>
/// <remarks>
/// <para>
/// Every verb parses through CShell rather than reading <c>args[0]</c> by hand, so
/// <c>airlock &lt;verb&gt; --help</c> answers the same way everywhere and the help is generated from
/// the declaration instead of being written out again beside it. Doing this for only some verbs is
/// how <c>airlock tools --help</c> came to reply that <c>--help</c> was not a tools command, and
/// <c>airlock remove --help</c> to go looking for an airlock called <c>--help</c>.
/// </para>
/// <para>
/// The boundary is the same one the top-level grammar uses: switches count until the first
/// positional, after which everything belongs to whatever is being wrapped. So
/// <c>airlock open --help</c> is Airlock's question and <c>airlock open claude --help</c> is
/// Claude's.
/// </para>
/// </remarks>
public static class VerbCli
{
    /// <summary>Starts a parser for a verb, named so its help says <c>airlock &lt;verb&gt;</c>.</summary>
    public static CShellCli For(string verb, IReadOnlyList<string> args, string description) =>
        CShellCli.For(args).Program($"airlock {verb}").Description(description);
}
