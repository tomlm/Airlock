using System.Text;

namespace Airlock.Cli;

/// <summary>
/// Airlock's verbs. Every invocation starts with one.
/// </summary>
/// <remarks>
/// Requiring a verb is what removes the one real ambiguity the grammar used to have. When
/// <c>airlock &lt;anything&gt;</c> ran that thing in the sandbox, a command that shared a name with
/// a verb needed <c>--</c> to disambiguate. Now the command always follows <c>open</c>, so
/// <c>airlock open list</c> means exactly what it looks like.
/// </remarks>
public static class ReservedVerbs
{
    /// <summary>Open this folder as an airlock and launch a tool in it - or a shell, given none.</summary>
    public const string Open = "open";

    /// <summary>Boot the sandbox with every configured tool and airlock mounted.</summary>
    public const string Start = "start";

    public const string Stop = "stop";

    /// <summary>Sandbox status, the configured airlocks, and the configured tools.</summary>
    public const string List = "list";

    /// <summary>Register a folder as an airlock without opening it.</summary>
    public const string Add = "add";

    /// <summary>Unregister an airlock. It stays mounted until the sandbox is stopped.</summary>
    public const string Remove = "remove";

    /// <summary>Manage the read-only mounts that land on PATH.</summary>
    public const string Tools = "tools";

    /// <summary>Reopen the sandbox desktop window.</summary>
    public const string Connect = "connect";


    /// <summary>
    /// Every verb with its one-line summary, in the order help should list them: the ones you reach
    /// for first, then the ones that manage what the sandbox is made of.
    /// </summary>
    /// <remarks>
    /// Help is generated from this rather than repeating it in prose, because the two drifted apart
    /// the moment the verb set changed - the usage line went on advertising verbs that no longer
    /// existed while omitting ones that did.
    /// </remarks>
    private static readonly (string Verb, string Summary)[] Described =
    [
        (Open, "open this folder as an airlock and run a tool in it, or a shell"),
        (Start, "bring the sandbox up with every configured tool and airlock"),
        (Stop, "destroy the sandbox; --force stops one it cannot prove is its own"),
        (List, "every airlock, and whether it is open right now"),
        (Add, "register a folder as an airlock without opening it"),
        (Remove, "unregister one by name or folder; it stays mounted until stop"),
        (Tools, "list, add or remove the read-only mounts that land on PATH"),
        (Connect, "reopen the sandbox desktop window"),
    ];

    public static IReadOnlyList<string> All { get; } = [.. Described.Select(d => d.Verb)];

    private static readonly HashSet<string> Set = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string token) => Set.Contains(token);

    /// <summary>The verbs on one line, for the usage summary.</summary>
    public static string Usage => string.Join(" | ", All);

    /// <summary>The verbs as an aligned block, to print under the generated usage text.</summary>
    public static string Describe()
    {
        var width = Described.Max(d => d.Verb.Length);
        var sb = new StringBuilder();

        sb.AppendLine("Commands:");

        foreach (var (verb, summary) in Described)
        {
            sb.Append("  ").Append(verb.PadRight(width + 2)).AppendLine(summary);
        }

        return sb.ToString();
    }
}
