namespace Airlock.Tools;

/// <summary>
/// Airlock's own staging folder, mounted read-only into the sandbox.
/// </summary>
/// <remarks>
/// This is separate from the tools in <c>airlock.json</c>: those are folders the user already has,
/// mounted where they sit. This one holds things Airlock puts there itself - currently just the
/// agent CLI, copied rather than installed so a fresh sandbox needs no download.
/// </remarks>
public sealed class ToolsCache(string? root = null)
{
    public string Root { get; } = root ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airlock",
        "tools");

    public string ClaudeDirectory => System.IO.Path.Combine(Root, "claude");

    public bool HasClaude => File.Exists(System.IO.Path.Combine(ClaudeDirectory, "claude.exe"));

    /// <summary>
    /// Stages the agent CLI from the host install.
    /// </summary>
    /// <remarks>
    /// Claude Code ships as one self-contained executable, so this is a copy rather than an install
    /// - no Node, no network, no per-session setup inside the sandbox. Staged on first use rather
    /// than at startup, since the folder is a live mount: a file added now shows up in a sandbox
    /// that is already running.
    /// </remarks>
    public void EnsureClaude(IProgress<string>? progress)
    {
        if (HasClaude)
        {
            return;
        }

        var source = FindHostClaude()
            ?? throw new InvalidOperationException(
                "Could not find claude.exe on this machine. Install Claude Code on the host " +
                "(winget install Anthropic.ClaudeCode) and try again.");

        Directory.CreateDirectory(ClaudeDirectory);
        var destination = System.IO.Path.Combine(ClaudeDirectory, "claude.exe");

        var mb = new FileInfo(source).Length / (1024 * 1024);
        progress?.Report($"Staging claude.exe ({mb} MB, one time)");

        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>Looks where the supported installers actually put it.</summary>
    public static string? FindHostClaude()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var direct = System.IO.Path.Combine(userProfile, ".local", "bin", "claude.exe");

        if (File.Exists(direct))
        {
            return direct;
        }

        // winget installs under a package folder whose name carries a hash suffix.
        var wingetPackages = System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");

        if (Directory.Exists(wingetPackages))
        {
            var match = Directory
                .EnumerateDirectories(wingetPackages, "Anthropic.ClaudeCode*")
                .Select(d => System.IO.Path.Combine(d, "claude.exe"))
                .FirstOrDefault(File.Exists);

            if (match is not null)
            {
                return match;
            }
        }

        // Fall back to PATH, skipping the WinGet shim, which is a zero-length symlink.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir) || dir.Contains("WinGet\\Links", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = System.IO.Path.Combine(dir.Trim(), "claude.exe");

            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }
}
