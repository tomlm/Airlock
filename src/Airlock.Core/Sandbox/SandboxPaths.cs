namespace Airlock.Sandbox;

/// <summary>
/// Where everything lives inside the sandbox.
/// </summary>
/// <remarks>
/// Airlocks are mounted directly under <see cref="Root"/>, so <c>S:\github\foo</c> becomes
/// <c>C:\airlock\foo</c> and the path inside reads like the one outside. Airlock's own folders share
/// that root, so they are wrapped in underscores - <c>_tools_</c>, <c>_session_</c> - to keep them
/// out of the namespace an airlock name could occupy.
/// </remarks>
public static class SandboxPaths
{
    /// <summary>Both the airlocks and Airlock's own folders live here.</summary>
    public const string Root = @"C:\airlock";

    /// <summary>Read-only: one folder per configured tool.</summary>
    public const string Tools = Root + @"\_tools_";

    /// <summary>Read-only: the setup script and the non-secret environment it applies.</summary>
    public const string Session = Root + @"\_session_";

    /// <summary>
    /// Read-write, and the only way the guest can tell the host anything.
    /// </summary>
    /// <remarks>
    /// <c>wsb exec</c> returns neither output nor the remote exit code, so provisioning reports its
    /// verdict here, the setup log comes home here on failure, and credentials go in this way.
    /// </remarks>
    public const string Out = Root + @"\_out_";

    /// <summary>
    /// Where setup.ps1 keeps its own log. Deliberately not <see cref="Out"/>, which is a mount
    /// point - mapping onto a folder that already has files in it invites trouble.
    /// </summary>
    public const string Setup = Root + @"\_setup_";

    public const string SetupLog = Setup + @"\setup.log";

    public const string SetupScript = Session + @"\setup.ps1";

    /// <summary>
    /// Whether a folder name belongs to Airlock rather than to an airlock.
    /// </summary>
    /// <remarks>
    /// The underscore wrapper exists precisely so this can never be ambiguous, but a workspace
    /// really could be called <c>_tools_</c>, so the check is real rather than assumed.
    /// </remarks>
    public static bool IsReservedName(string name) =>
        !string.IsNullOrEmpty(name) &&
        name.Length > 1 &&
        name[0] == '_' &&
        name[^1] == '_';

    /// <summary>The path an airlock with this name gets inside the sandbox.</summary>
    public static string ForProject(string name) => System.IO.Path.Combine(Root, name);

    /// <summary>
    /// Where a tool mounts, one folder per tool.
    /// </summary>
    /// <remarks>
    /// Giving each tool its own folder rather than a shared one keeps PATH entries predictable and
    /// means removing a tool cannot disturb another.
    /// </remarks>
    public static string ForTool(string id) => System.IO.Path.Combine(Tools, id);
}
