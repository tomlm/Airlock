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
    /// There are two quite different jobs here, and which one applies depends on whether Airlock
    /// owns a real console.
    /// </para>
    /// <para>
    /// <b>Piped</b> - <c>airlock dotnet build &gt; log.txt</c>. This is ordinary command running, so
    /// it goes through CShell: streams redirected as the library intends, forwarded to our own
    /// stdout and stderr as they arrive.
    /// </para>
    /// <para>
    /// <b>Interactive</b> - <c>airlock claude</c>. Here the child has to <i>inherit</i> the console
    /// rather than be piped, and redirection is not a matter of taste: with a pipe on stdin, ssh has
    /// no local terminal to read, so it cannot discover the window size or notice a resize, and the
    /// remote pty is stuck at a default while the agent draws into it. Raw-mode keystrokes would
    /// have to be pumped by hand as well. Inheriting the handles gives both ends a ConPTY for free,
    /// which is what CShell's <c>ExecAsync</c> does - attach rather than capture.
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

        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var args = new List<string>();

        // -t forces a remote TTY, but only when we have a local one to size it from.
        if (interactive)
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

        return interactive
            ? await InheritConsoleAsync(args, secrets, cancellationToken).ConfigureAwait(false)
            : await PipeAsync(args, secrets, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs ssh as an ordinary command, streaming its output through to ours as it arrives.
    /// </summary>
    private static async Task<int> PipeAsync(
        List<string> args,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var result = await Run(
                opt =>
                {
                    opt.CancellationToken(cancellationToken);
                    opt.EnvironmentVariables(secrets);
                },
                SshKeys.SshExe,
                [.. args])
            .RedirectTo(Console.Out)
            .RedirectStandardErrorTo(Console.Error)
            .AsResult()
            .ConfigureAwait(false);

        return result.ExitCode;
    }

    /// <summary>
    /// Hands the real console to ssh, so both ends get a ConPTY and the remote pty tracks the
    /// window.
    /// </summary>
    /// <remarks>
    /// Nothing is redirected, which is the whole point and why <c>Run</c> is the wrong verb here.
    /// CShell's <c>ExecAsync</c> is the one that attaches rather than captures, and it also puts
    /// the console modes back afterwards, so a killed agent cannot leave the terminal with echo
    /// switched off.
    /// </remarks>
    private static Task<int> InheritConsoleAsync(
        List<string> args,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken) =>
        ExecAsync(
            opt =>
            {
                opt.CancellationToken(cancellationToken);
                opt.EnvironmentVariables(secrets);
            },
            SshKeys.SshExe,
            [.. args]);

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
