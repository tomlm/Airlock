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
    /// Confirms the sandbox is genuinely ready, and that it is <i>ours</i>: an open port only proves
    /// sshd is listening, while a successful non-interactive command proves key auth, the user
    /// account and the shell all work. This is the success signal for provisioning, because
    /// <c>wsb exec</c> reports no status at all.
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

        var result = await Run(opt => opt.CancellationToken(cancellationToken), SshKeys.SshExe, [.. args])
            .AsResult()
            .ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>
    /// Hands the terminal to the sandbox and returns the remote command's exit code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one launch in Airlock that does not go through CShell, and the reason is
    /// measured rather than theoretical. The child must inherit the real console handles - that is
    /// what gives both ends a ConPTY, so a full-screen TUI draws and a prompt can be answered.
    /// MedallionShell, underneath CShell, attaches its own readers to the child's streams, which is
    /// not compatible with leaving them unredirected: routed that way the session produced
    /// <b>no output at all</b> and reported exit code 1 for a remote <c>exit 42</c>. Everything
    /// else - wsb, ssh-keygen, icacls, the non-interactive ssh probe - uses CShell.
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

        var args = new List<string>();

        // -t forces a TTY, but only when we have one to give; without this, redirecting airlock's
        // output ("airlock dotnet build > log.txt") would break.
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            args.Add("-t");
        }

        args.AddRange(BaseOptions(layout.PrivateKeyPath));

        foreach (var name in secrets.Keys)
        {
            args.Add("-o");
            args.Add($"SendEnv={name}");
        }

        args.Add($"{SetupScript.SandboxUser}@{ip}");

        // Three layers of quoting sit between here and the guest shell (our argv, ssh's
        // remote-command concatenation, then DefaultShell -Command). Base64 removes all of them;
        // sending the script text directly is not merely fragile, it is broken.
        args.Add("powershell.exe");
        args.Add("-NoLogo");
        args.Add("-NoProfile");
        args.Add("-EncodedCommand");
        args.Add(Encode(remoteCommand));

        // sshd runs the remote command as `DefaultShell -Command "<all of the above>"`, and that
        // wrapper exits 0 or 1 for success or failure rather than passing on what it ran. Without
        // this, a remote `exit 42` reaches the caller as 1. ssh joins these with spaces, so the
        // guest shell sees them as a second statement.
        args.Add(";");
        args.Add("exit");
        args.Add("$LASTEXITCODE");

        var psi = new ProcessStartInfo(SshKeys.SshExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in secrets)
        {
            psi.Environment[name] = value;
        }

        using var guard = ConsoleModeGuard.Capture();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {SshKeys.SshExe}.");

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
