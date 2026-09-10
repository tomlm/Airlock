namespace Airlock.Cli;

/// <summary>
/// Airlock's own verbs. A leading token in this set is handled by Airlock; anything else is a
/// command to run inside the sandbox.
/// </summary>
/// <remarks>
/// The set is deliberately small and made of words nobody runs as a program, so the collision
/// with a real command is rare — and when it happens, <c>airlock -- list</c> resolves it.
/// </remarks>
public static class ReservedVerbs
{
    public const string Run = "run";
    public const string Shell = "shell";
    public const string List = "list";
    public const string Stop = "stop";
    public const string Connect = "connect";
    public const string Doctor = "doctor";
    public const string Tools = "tools";
    public const string Config = "config";
    public const string Trust = "trust";

    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        Run, Shell, List, Stop, Connect, Doctor, Tools, Config, Trust,
    };

    public static bool Contains(string token) => Set.Contains(token);

    public static IReadOnlyCollection<string> All => Set;
}
