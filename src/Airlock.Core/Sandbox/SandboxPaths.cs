namespace Airlock.Sandbox;

/// <summary>
/// Where everything lives inside the sandbox.
/// </summary>
/// <remarks>
/// <para>
/// Every folder Airlock owns is a top-level folder on the guest's C: drive. There is nothing to keep
/// tidy - the machine is thrown away when the sandbox stops - and putting them at the root keeps
/// paths short, which is the whole game once a build starts nesting under a workspace.
/// </para>
/// <para>
/// Workspaces live in <see cref="Airlocks"/> and nothing else does, so no name is reserved: an
/// airlock may be called <c>tools</c> or <c>session</c> without shadowing anything. Two earlier
/// layouts put workspaces beside Airlock's own folders and needed a naming convention - underscores,
/// then a dot prefix - to tell them apart; giving them a folder of their own removes the collision
/// instead of working around it.
/// </para>
/// <para>
/// <see cref="Drive"/> is substituted onto <see cref="Airlocks"/> during provisioning, so
/// <c>A:\foo</c> and <c>C:\airlocks\foo</c> are the same folder. The short form is the one to show
/// and to work in, but it is a veneer over the canonical path, and tools that resolve paths for
/// themselves will still report the long one.
/// </para>
/// </remarks>
public static class SandboxPaths
{
    /// <summary>Read-write: one folder per airlock, and nothing else.</summary>
    public const string Airlocks = @"C:\airlocks";

    /// <summary>
    /// The drive letter <see cref="Airlocks"/> is substituted onto, so every airlock is one letter
    /// and a name away.
    /// </summary>
    public const string Drive = "A:";

    /// <summary>Read-only: one folder per configured tool.</summary>
    public const string Tools = @"C:\tools";

    /// <summary>Read-only: the setup script and the non-secret environment it applies.</summary>
    public const string Session = @"C:\session";

    /// <summary>
    /// Read-write, and the only way the guest can tell the host anything.
    /// </summary>
    /// <remarks>
    /// <c>wsb exec</c> returns neither output nor the remote exit code, so provisioning reports its
    /// verdict here, the setup log comes home here on failure, and credentials go in this way.
    /// </remarks>
    public const string Out = @"C:\out";

    /// <summary>
    /// Where setup.ps1 keeps its own log. Deliberately not <see cref="Out"/>, which is a mount
    /// point - mapping onto a folder that already has files in it invites trouble.
    /// </summary>
    public const string Setup = @"C:\setup";

    public const string SetupLog = Setup + @"\setup.log";

    public const string SetupScript = Session + @"\setup.ps1";

    /// <summary>Every folder Airlock owns, which is every folder it may safely create or mount on.</summary>
    public static IReadOnlyList<string> OwnFolders => [Airlocks, Tools, Session, Out, Setup];

    /// <summary>
    /// The canonical path an airlock with this name gets inside the sandbox.
    /// </summary>
    /// <remarks>
    /// This is the path to mount on and the one every tool will resolve back to.
    /// <see cref="ForProjectOnDrive"/> is the same folder, spelled for a human.
    /// </remarks>
    public static string ForProject(string name) => System.IO.Path.Combine(Airlocks, name);

    /// <summary>The short spelling of <see cref="ForProject"/>, by way of <see cref="Drive"/>.</summary>
    public static string ForProjectOnDrive(string name) =>
        System.IO.Path.Combine(Drive + "\\", name);

    /// <summary>
    /// Where a tool mounts, one folder per tool.
    /// </summary>
    /// <remarks>
    /// Giving each tool its own folder rather than a shared one keeps PATH entries predictable and
    /// means removing a tool cannot disturb another.
    /// </remarks>
    public static string ForTool(string id) => System.IO.Path.Combine(Tools, id);
}
