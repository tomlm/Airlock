using System.Diagnostics;
using System.Text;

namespace Airlock.Session;

/// <summary>Connects the user's terminal to the sandbox.</summary>
public static class SshLauncher
{
    /// <summary>
    /// Options shared by every connection. <c>IdentitiesOnly</c> and <c>IdentityAgent=none</c> stop
    /// ssh offering every key in the user's agent, which both leaks what keys they have and can
    /// exhaust MaxAuthTries before the session key is tried. The host key is deliberately not
    /// verified or recorded: a fresh sandbox generates a new one every launch, so pinning it would
    /// only pollute known_hosts.
    /// </summary>
    public static IReadOnlyList<string> BaseOptions(string privateKeyPath) =>
    [
        "-i", privateKeyPath,
        "-o", "IdentitiesOnly=yes",
        "-o", "IdentityAgent=none",
        "-o", "PreferredAuthentications=publickey",
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=NUL",
        "-o", "GlobalKnownHostsFile=NUL",
        "-o", "LogLevel=ERROR",
        "-o", "ConnectTimeout=10",
    ];

    /// <summary>
    /// Confirms the sandbox is genuinely ready: an open port only proves sshd is listening, while a
    /// successful non-interactive command proves key auth, the user account and the shell all work.
    /// This is the success signal for provisioning, because <c>wsb exec</c> reports no status.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        SandboxLayout layout,
        string ip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var args = new List<string>(BaseOptions(layout.PrivateKeyPath))
        {
            "-o", "BatchMode=yes",
            $"{SetupScript.SandboxUser}@{ip}",
            "exit",
        };

        var psi = new ProcessStartInfo(SshKeys.SshExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode == 0;
    }

    /// <summary>
    /// Hands the terminal to the sandbox and returns the remote command's exit code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place Airlock does not use CShell's <c>Run</c>. CShell redirects all three
    /// standard streams, which is exactly wrong here: a redirected child gets no console, so a
    /// full-screen TUI draws nothing and anything that prompts hangs on a pipe nobody will write
    /// to. Leaving every handle inherited is what gives both ends a real ConPTY, and it means
    /// <c>CommandResult.StandardOutput</c> would be meaningless anyway.
    /// </para>
    /// <para>
    /// Secrets are placed in ssh.exe's own environment and named in <c>SendEnv</c>, so they never
    /// appear in an argument list on either side, nor in any file.
    /// </para>
    /// </remarks>
    public static async Task<int> ConnectAsync(
        SandboxLayout layout,
        string ip,
        string remoteCommand,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(secrets);

        var psi = new ProcessStartInfo(SshKeys.SshExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        // -t forces a TTY, but only when we have one to give; without this, redirecting airlock's
        // output ("airlock dotnet build > log.txt") would break.
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (interactive)
        {
            psi.ArgumentList.Add("-t");
        }

        foreach (var option in BaseOptions(layout.PrivateKeyPath))
        {
            psi.ArgumentList.Add(option);
        }

        foreach (var name in secrets.Keys)
        {
            psi.Environment[name] = secrets[name];
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add($"SendEnv={name}");
        }

        psi.ArgumentList.Add($"{SetupScript.SandboxUser}@{ip}");

        // Three layers of quoting sit between here and the guest shell (our argv, ssh's
        // remote-command concatenation, then DefaultShell -Command). Base64 removes all of them;
        // sending the script text directly is not merely fragile, it is broken.
        psi.ArgumentList.Add("powershell.exe");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Encode(remoteCommand));

        using var guard = ConsoleModeGuard.Capture();
        using var process = Process.Start(psi)!;

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>PowerShell's -EncodedCommand expects base64 of UTF-16LE, not UTF-8.</summary>
    public static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// Builds the prologue that runs in the sandbox: move to the project, pick up any forwarded
    /// secrets, then become the requested command.
    /// </summary>
    public static string BuildRemoteCommand(string sandboxProjectPath, IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"Set-Location -LiteralPath {Quote(sandboxProjectPath)}");

        if (command.Count == 0)
        {
            return sb.ToString();
        }

        var arguments = command.Count > 1
            ? string.Join(", ", command.Skip(1).Select(Quote))
            : null;

        sb.Append("& ").Append(Quote(command[0]));
        if (arguments is not null)
        {
            sb.Append(" @(").Append(arguments).Append(')');
        }

        sb.AppendLine();
        sb.AppendLine("exit $LASTEXITCODE");

        return sb.ToString();
    }

    /// <summary>Single-quoting is what makes a PowerShell literal safe; the escape is a doubled quote.</summary>
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
