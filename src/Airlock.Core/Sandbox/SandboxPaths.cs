namespace Airlock.Sandbox;

/// <summary>
/// Where everything lives inside the sandbox.
/// </summary>
/// <remarks>
/// Projects are mounted directly under <see cref="Root"/>, so <c>S:\github\foo</c> becomes
/// <c>C:\airlock\foo</c> and the path inside reads like the one outside. Airlock's own mounts share
/// that root, so they are wrapped in underscores - <c>_tools_</c>, <c>_session_</c> - to keep them
/// out of the namespace a project name could occupy.
/// </remarks>
public static class SandboxPaths
{
    /// <summary>Both the projects and Airlock's own folders live here.</summary>
    public const string Root = @"C:\airlock";

    /// <summary>Read-only: the agent CLI and the portable OpenSSH release.</summary>
    public const string Tools = Root + @"\_tools_";

    /// <summary>Read-only: setup script, public key, non-secret environment.</summary>
    public const string Session = Root + @"\_session_";

    /// <summary>Read-only: the host's .NET SDK.</summary>
    public const string Dotnet = Root + @"\_dotnet_";

    /// <summary>Writable, and attached only when provisioning fails, to retrieve the log.</summary>
    public const string Out = Root + @"\_out_";

    /// <summary>
    /// Where setup.ps1 writes its log and result. Kept out of <see cref="Out"/> because that one is
    /// a mount point, and mapping onto a folder that already has files in it invites trouble.
    /// </summary>
    public const string Setup = Root + @"\_setup_";

    public const string SetupLog = Setup + @"\setup.log";

    public const string SetupResult = Setup + @"\ready.json";

    public const string Claude = Tools + @"\claude";

    public const string ClaudeExe = Claude + @"\claude.exe";

    public const string OpenSsh = Tools + @"\OpenSSH-Win64";

    public const string SetupScript = Session + @"\setup.ps1";

    /// <summary>
    /// Whether a folder name belongs to Airlock rather than to a project.
    /// </summary>
    /// <remarks>
    /// The underscore wrapper exists precisely so this can never be ambiguous, but a project folder
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
    /// Where a tool mounts, one folder per tool under the tools root.
    /// </summary>
    /// <remarks>
    /// Giving each tool its own folder rather than a shared one keeps PATH entries predictable and
    /// means removing a tool cannot disturb another.
    /// </remarks>
    public static string ForTool(string id) => System.IO.Path.Combine(Tools, id);
}
