namespace Airlock.Configuration;

/// <summary>
/// Finds the toolchains already installed on the host, so a fresh Airlock works without being told
/// where everything is.
/// </summary>
/// <remarks>
/// Detection is a convenience over the same mechanism <c>airlock tools add</c> uses - it produces
/// ordinary <see cref="ToolDefinition"/> entries, marked <c>Detected</c> so they can be re-probed
/// later. Every probe validates the folder's contents rather than trusting its name, because at
/// least one installer on this machine leaves a convincing decoy behind.
/// </remarks>
public static class ToolDetector
{
    /// <summary>Everything found on this machine, in a stable order.</summary>
    public static IReadOnlyList<ToolDefinition> DetectAll() =>
        [.. new[] { DetectDotnet(), DetectNode(), DetectGit(), DetectPython() }.OfType<ToolDefinition>()];

    /// <summary>
    /// The .NET SDK. Mapped read-only, which is enough to build, run, test and publish - but not to
    /// install a workload or an SDK-folder global tool.
    /// </summary>
    public static ToolDefinition? DetectDotnet()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        };

        var root = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && HasFile(c, "dotnet.exe"));

        return root is null
            ? null
            : new ToolDefinition(
                "dotnet",
                root,
                Detected: true,
                Env: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // {mount} is the path inside the sandbox, which is not known until start.
                    ["DOTNET_ROOT"] = "{mount}",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                });
    }

    public static ToolDefinition? DetectNode()
    {
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");

        return HasFile(root, "node.exe") ? new ToolDefinition("node", root, Detected: true) : null;
    }

    /// <summary>Git lives a level down, in <c>cmd</c>, so only that goes on PATH.</summary>
    public static ToolDefinition? DetectGit()
    {
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");

        return HasFile(System.IO.Path.Combine(root, "cmd"), "git.exe")
            ? new ToolDefinition("git", root, Detected: true, Path: ["cmd"])
            : null;
    }

    /// <summary>
    /// A real python.org installation - never the Store build, and never a leftover folder that only
    /// looks like one.
    /// </summary>
    /// <remarks>
    /// Two traps here, both real on this machine. The Store's <c>python3.exe</c> under
    /// <c>WindowsApps</c> is an execution-alias stub that Windows Sandbox cannot map at all. And
    /// <c>%LOCALAPPDATA%\Programs\Python\Python314</c> contains nothing but <c>Lib\</c> - a name
    /// match with no interpreter - so the check is for the files, not the folder.
    /// </remarks>
    public static ToolDefinition? DetectPython()
    {
        var roots = new List<string>();

        foreach (var parent in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     System.IO.Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs",
                         "Python"),
                     "C:\\",
                 })
        {
            if (Directory.Exists(parent))
            {
                roots.AddRange(Directory.EnumerateDirectories(parent, "Python3*"));
            }
        }

        var found = roots
            .Where(IsUsablePython)
            .OrderByDescending(r => r, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return found is null
            ? null
            : new ToolDefinition("python", found, Detected: true, Path: [string.Empty, "Scripts"]);
    }

    /// <summary>
    /// A mountable Python: a real interpreter with its library and scripts beside it, outside the
    /// Store's app folder.
    /// </summary>
    public static bool IsUsablePython(string root) =>
        !root.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
        HasFile(root, "python.exe") &&
        Directory.Exists(System.IO.Path.Combine(root, "Lib")) &&
        Directory.Exists(System.IO.Path.Combine(root, "Scripts"));

    private static bool HasFile(string folder, string fileName) =>
        !string.IsNullOrWhiteSpace(folder) &&
        File.Exists(System.IO.Path.Combine(folder, fileName));
}
