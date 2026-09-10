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
    /// <summary>Attach this project and work in it - with a command, or a shell without one.</summary>
    public const string Open = "open";

    /// <summary>Alias for <see cref="Open"/>, for when "run this command" reads better.</summary>
    public const string Run = "run";

    public const string Start = "start";
    public const string Stop = "stop";
    public const string List = "list";
    public const string Connect = "connect";
    public const string Doctor = "doctor";
    public const string Tools = "tools";
    public const string Config = "config";
    public const string Trust = "trust";

    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        Open, Run, Start, Stop, List, Connect, Doctor, Tools, Config, Trust,
    };

    public static bool Contains(string token) => Set.Contains(token);

    public static IReadOnlyCollection<string> All => Set;
}
