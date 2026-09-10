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

    public const string Doctor = "doctor";

    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        Open, Start, Stop, List, Add, Remove, Tools, Connect, Doctor,
    };

    public static bool Contains(string token) => Set.Contains(token);

    public static IReadOnlyCollection<string> All => Set;
}
