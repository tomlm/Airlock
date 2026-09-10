using System.IO.Compression;

namespace Airlock.Tools;

/// <summary>
/// The host-side folder of things the sandbox needs, mapped in read-only at <c>C:\airlock\tools</c>.
/// </summary>
/// <remarks>
/// A fresh sandbox has no Git, no Node, no modern .NET and no package manager, and downloading the
/// agent CLI on every launch would add minutes to every session. So everything is staged once on
/// the host and mapped in.
/// </remarks>
public sealed class ToolsCache(string? root = null)
{
    /// <summary>
    /// Pinned deliberately. The repo's "latest release" is a Preview tag, and silently following it
    /// would change the SSH server under a working setup.
    /// </summary>
    public const string OpenSshVersion = "10.0.0.0p2-Preview";

    private const string OpenSshUrl =
        $"https://github.com/PowerShell/Win32-OpenSSH/releases/download/{OpenSshVersion}/OpenSSH-Win64.zip";

    public string Root { get; } = root ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airlock",
        "tools");

    public string OpenSshDirectory => System.IO.Path.Combine(Root, "OpenSSH-Win64");

    public string ClaudeDirectory => System.IO.Path.Combine(Root, "claude");

    /// <summary>The host's own OpenSSH is not usable here: it ships without <c>install-sshd.ps1</c>.</summary>
    public bool HasOpenSsh =>
        File.Exists(System.IO.Path.Combine(OpenSshDirectory, "sshd.exe")) &&
        File.Exists(System.IO.Path.Combine(OpenSshDirectory, "install-sshd.ps1"));

    public bool HasClaude => File.Exists(System.IO.Path.Combine(ClaudeDirectory, "claude.exe"));

    public async Task EnsureOpenSshAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (HasOpenSsh)
        {
            return;
        }

        Directory.CreateDirectory(Root);
        var zip = System.IO.Path.Combine(Root, "OpenSSH-Win64.zip");

        if (!File.Exists(zip))
        {
            progress?.Report($"Downloading OpenSSH {OpenSshVersion} (~5 MB)");

            using var http = new HttpClient();
            await using var stream = await http.GetStreamAsync(new Uri(OpenSshUrl), cancellationToken)
                .ConfigureAwait(false);
            await using var file = File.Create(zip);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("Extracting OpenSSH");

        if (Directory.Exists(OpenSshDirectory))
        {
            Directory.Delete(OpenSshDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zip, Root, overwriteFiles: true);

        if (!HasOpenSsh)
        {
            throw new InvalidOperationException(
                $"OpenSSH extracted to '{OpenSshDirectory}' but install-sshd.ps1 or sshd.exe is missing.");
        }
    }

    /// <summary>
    /// Stages the agent CLI from the host install. Claude Code ships as one self-contained
    /// executable, so this is a copy rather than an install - no Node, no network, no per-session
    /// setup inside the sandbox.
    /// </summary>
    public void EnsureClaude(IProgress<string>? progress)
    {
        if (HasClaude)
        {
            return;
        }

        var source = FindHostClaude()
            ?? throw new InvalidOperationException(
                "Could not find claude.exe on this machine. Install Claude Code on the host " +
                "(winget install Anthropic.ClaudeCode), then run 'airlock tools update'.");

        Directory.CreateDirectory(ClaudeDirectory);
        var destination = System.IO.Path.Combine(ClaudeDirectory, "claude.exe");

        var mb = new FileInfo(source).Length / (1024 * 1024);
        progress?.Report($"Staging claude.exe ({mb} MB, one time)");

        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>Looks where the supported installers actually put it, newest first.</summary>
    public static string? FindHostClaude()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var direct = new[]
        {
            System.IO.Path.Combine(userProfile, ".local", "bin", "claude.exe"),
        };

        foreach (var candidate in direct.Where(File.Exists))
        {
            return candidate;
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
