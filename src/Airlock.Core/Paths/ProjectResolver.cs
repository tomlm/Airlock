namespace Airlock.Paths;

/// <summary>A project folder, resolved to something Windows Sandbox can actually map.</summary>
/// <param name="HostPath">The real host path, with links followed to their target.</param>
/// <param name="SandboxPath">Where it will appear inside the sandbox.</param>
/// <param name="Name">The sanitised leaf name.</param>
/// <param name="Warnings">Non-fatal things the user should know before the agent starts writing.</param>
public sealed record ResolvedProject(
    string HostPath,
    string SandboxPath,
    string Name,
    IReadOnlyList<string> Warnings);

/// <summary>Raised when a folder cannot safely be handed to an agent.</summary>
public sealed class ProjectValidationException(string message) : Exception(message);

/// <summary>
/// Turns whatever the user typed into a real, mappable, safe-to-write project folder.
/// </summary>
/// <remarks>
/// Windows Sandbox needs a real local path, and refuses to map anything else. Links are followed
/// and network drives rejected up front, so the failure arrives as a sentence the user can act on
/// rather than as a sandbox with a mysteriously empty project folder.
/// </remarks>
public static class ProjectResolver
{
    public static ResolvedProject Resolve(string? requestedPath)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? Directory.GetCurrentDirectory()
            : requestedPath;

        if (!Directory.Exists(path))
        {
            throw new ProjectValidationException($"Project folder not found: '{path}'.");
        }

        var real = GetRealPath(path);

        RejectNetworkDrive(real);

        if (real.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ProjectValidationException(
                $"'{path}' resolves to the network path '{real}'. Windows Sandbox cannot map UNC " +
                "paths, so the project has to live on a local drive.");
        }

        Validate(real);

        var name = SanitiseName(new DirectoryInfo(real).Name);

        return new ResolvedProject(
            real,
            Airlock.Sandbox.SandboxPaths.ForProject(name),
            name,
            CollectWarnings(real, path));
    }

    /// <summary>
    /// Resolves a path to what Windows Sandbox will actually be asked to map, following junctions
    /// and symlinks to their final target.
    /// </summary>
    /// <remarks>
    /// A <c>subst</c> drive is a DOS device alias rather than a reparse point, so nothing here will
    /// unwrap one. That is deliberate: unwrapping it needs <c>QueryDosDevice</c>, and a subst path
    /// fails at <c>wsb start</c> with a clear error anyway, which is a better trade than carrying
    /// interop for a case this rare.
    /// </remarks>
    public static string GetRealPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);

        // ResolveLinkTarget returns null when the path is not a link, which is the common case.
        var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);

        return target?.FullName ?? full;
    }

    /// <summary>
    /// A mapped network drive resolves to a perfectly ordinary-looking local path, but Sandbox
    /// cannot map it. DriveInfo tells us without needing interop.
    /// </summary>
    private static void RejectNetworkDrive(string real)
    {
        var root = System.IO.Path.GetPathRoot(real);

        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return;
        }

        if (new DriveInfo(root).DriveType is DriveType.Network)
        {
            throw new ProjectValidationException(
                $"'{root}' is a mapped network drive. Windows Sandbox can only map local folders, " +
                "so the project needs to live on a local disk.");
        }
    }

    private static void Validate(string real)
    {
        var root = System.IO.Path.GetPathRoot(real);
        if (string.Equals(real.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectValidationException(
                $"'{real}' is a drive root. Mapping a whole drive read-write would defeat the point; " +
                "point Airlock at a project folder.");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsAtOrAbove(real, profile))
        {
            throw new ProjectValidationException(
                $"'{real}' is your user profile (or contains it). That is exactly the data Airlock " +
                "exists to keep away from the agent.");
        }

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var system = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(system) && IsUnder(real, system))
            {
                throw new ProjectValidationException($"'{real}' is inside '{system}'. Refusing to map it read-write.");
            }
        }
    }

    private static List<string> CollectWarnings(string real, string requested)
    {
        var warnings = new List<string>();

        if (!string.Equals(
                System.IO.Path.GetFullPath(requested).TrimEnd('\\'),
                real.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"'{requested}' resolved to '{real}'.");
        }

        if (FindGitDirectory(real) is null)
        {
            warnings.Add(
                "This is not a git repository. The agent has full write access to the folder, and " +
                "without version control there is no way to review or undo what it changes.");
        }

        return warnings;
    }

    /// <summary>Walks up looking for <c>.git</c>, which may be a directory or a worktree pointer file.</summary>
    public static string? FindGitDirectory(string start)
    {
        var dir = new DirectoryInfo(start);

        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Keeps the sandbox path predictable and free of characters that would need quoting in the
    /// remote command. Projects share a root with Airlock's own mounts, which are wrapped in
    /// underscores, so a name shaped like one of those gets nudged out of the way.
    /// </summary>
    private static string SanitiseName(string name)
    {
        var cleaned = new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')]);

        if (cleaned.Length == 0)
        {
            cleaned = "project";
        }

        cleaned = cleaned.Length > 64 ? cleaned[..64] : cleaned;

        // _tools_ and friends belong to Airlock; a project called that would shadow one.
        return Airlock.Sandbox.SandboxPaths.IsReservedName(cleaned) ? cleaned.Trim('_') : cleaned;
    }

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsAtOrAbove(string path, string other) =>
        string.Equals(path.TrimEnd('\\'), other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || IsUnder(other, path);
}
