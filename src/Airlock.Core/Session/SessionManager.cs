using System.Net.Sockets;
using Airlock.Paths;
using Airlock.Sandbox;
using Airlock.Tools;

namespace Airlock.Session;

/// <summary>What the user asked for, resolved enough to run.</summary>
public sealed class SessionRequest
{
    public required ResolvedProject Project { get; init; }

    /// <summary>The command to run in the sandbox, verbatim. Empty means an interactive shell.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Leave the sandbox running after the command exits.</summary>
    public bool Keep { get; init; }

    public int MemoryInMB { get; init; } = 8192;
}

/// <summary>The phases a session moves through, in order.</summary>
public enum SessionPhase
{
    Preflight,
    Staging,
    Starting,
    Booting,
    Provisioning,
    Networking,
    WaitingForSsh,
    Connected,
    TearingDown,
}

/// <summary>
/// Runs one session end to end: start a sandbox, provision it, hand over the terminal, tear it down.
/// </summary>
public sealed class SessionManager(IWsbClient? wsb = null, ToolsCache? tools = null)
{
    private readonly IWsbClient _wsb = wsb ?? new WsbClient();
    private readonly ToolsCache _tools = tools ?? new ToolsCache();

    /// <summary>
    /// Boots and provisions a sandbox, and returns it ready to connect to. The caller connects
    /// separately so it can close any progress display before the terminal is handed over.
    /// </summary>
    public async Task<SandboxSession> StartAsync(
        SessionRequest request,
        IProgress<(SessionPhase Phase, string Detail)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Report(progress, SessionPhase.Preflight, "Checking host");

        // Windows Sandbox is single-instance, so a sandbox we did not start still blocks us - and
        // it may well be one the user opened themselves, which we must not kill.
        var running = await _wsb.ListAsync(cancellationToken).ConfigureAwait(false);
        if (running.Count > 0)
        {
            throw new SessionException(
                $"A Windows Sandbox is already running ({running[0]}), and Windows allows only one " +
                "at a time. Close it, or run 'airlock stop' if Airlock started it.");
        }

        await _tools.EnsureOpenSshAsync(
            new Progress<string>(m => Report(progress, SessionPhase.Preflight, m)),
            cancellationToken).ConfigureAwait(false);

        if (IsClaude(request.Command))
        {
            _tools.EnsureClaude(new Progress<string>(m => Report(progress, SessionPhase.Preflight, m)));
        }

        Report(progress, SessionPhase.Staging, "Preparing session");

        // The id is chosen here, so the session is on record before anything exists to leak.
        var sandboxId = Guid.NewGuid().ToString();
        var layout = SessionLayout.Create(sandboxId);
        var started = false;

        try
        {
            await SshKeys.GenerateAsync(layout, cancellationToken).ConfigureAwait(false);

            SetupScript.Write(layout, BuildPathPrepend(), BuildMachineEnv(request));

            var config = new SandboxConfig
            {
                MemoryInMB = request.MemoryInMB,
                MappedFolders = BuildMounts(request, layout),
            };

            var inlineXml = SandboxConfigWriter.ToInlineXml(config);
            await File.WriteAllTextAsync(
                layout.ConfigPath, SandboxConfigWriter.ToPrettyXml(config), cancellationToken)
                .ConfigureAwait(false);

            Report(progress, SessionPhase.Starting, "Starting sandbox");
            sandboxId = await _wsb.StartAsync(sandboxId, inlineXml, cancellationToken).ConfigureAwait(false);
            started = true;

            Report(progress, SessionPhase.Booting, "Waiting for the sandbox to boot");
            await WaitForBootAsync(sandboxId, progress, cancellationToken).ConfigureAwait(false);

            // wsb exec blocks until the script exits, but tells us nothing about how it went.
            Report(progress, SessionPhase.Provisioning, "Installing SSH and toolchain (about a minute)");
            await _wsb.ExecAsync(
                sandboxId,
                @"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\airlock\session\setup.ps1",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(progress, SessionPhase.Networking, "Waiting for an IP address");
            var ip = await WaitForIpAsync(sandboxId, cancellationToken).ConfigureAwait(false);

            Report(progress, SessionPhase.WaitingForSsh, "Connecting");
            await WaitForSshAsync(layout, sandboxId, ip, cancellationToken).ConfigureAwait(false);

            Report(progress, SessionPhase.Connected, "Connected");

            return new SandboxSession(
                _wsb, layout, sandboxId, ip, ResolveSandboxProjectPath(request), request.Keep);
        }
        catch
        {
            // Nothing owns the sandbox yet, so anything half-built has to be cleaned up here.
            if (started)
            {
                await _wsb.StopAsync(sandboxId, CancellationToken.None).ConfigureAwait(false);
            }

            layout.Dispose();
            throw;
        }
    }

    private static string ResolveSandboxProjectPath(SessionRequest request) => request.Project.SandboxPath;

    /// <summary>Points a bare agent name at the staged binary; anything else runs off the guest PATH.</summary>
    public static IReadOnlyList<string> ResolveCommand(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsClaude(command))
        {
            return command;
        }

        var resolved = new List<string>(command) { [0] = @"C:\airlock\tools\claude\claude.exe" };

        return resolved;
    }

    /// <summary>
    /// The only read-write mapping is the project. Everything else the sandbox needs is read-only,
    /// and the private key directory is not mapped at all.
    /// </summary>
    private List<MappedFolder> BuildMounts(SessionRequest request, SessionLayout layout)
    {
        var mounts = new List<MappedFolder>
        {
            new(request.Project.HostPath, request.Project.SandboxPath, ReadOnly: false),
            new(_tools.Root, @"C:\airlock\tools", ReadOnly: true),
            new(layout.ShareDirectory, @"C:\airlock\session", ReadOnly: true),
        };

        var dotnet = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        if (Directory.Exists(dotnet))
        {
            mounts.Add(new MappedFolder(dotnet, @"C:\airlock\dotnet", ReadOnly: true));
        }

        return mounts;
    }

    private static List<string> BuildPathPrepend() =>
    [
        @"C:\airlock\dotnet",
        @"C:\airlock\tools\claude",
        @"C:\airlock\tools\OpenSSH-Win64",
    ];

    private static Dictionary<string, string> BuildMachineEnv(SessionRequest request) => new(StringComparer.Ordinal)
    {
        ["DOTNET_ROOT"] = @"C:\airlock\dotnet",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",
        ["AIRLOCK"] = "1",
        ["AIRLOCK_PROJECT"] = request.Project.SandboxPath,
    };

    private static bool IsClaude(IReadOnlyList<string> command) =>
        command.Count > 0 &&
        System.IO.Path.GetFileNameWithoutExtension(command[0])
            .Equals("claude", StringComparison.OrdinalIgnoreCase);

    private async Task WaitForBootAsync(
        string id,
        IProgress<(SessionPhase, string)>? progress,
        CancellationToken cancellationToken)
    {
        // A cold boot after a Windows update rebuilds the base image and really can take ten
        // minutes; a warm one is a second or two.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
        var announced = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _wsb.ExecAsync(id, "cmd.exe /c exit 0", WsbRunAs.System, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            if (!announced && DateTimeOffset.UtcNow > deadline.AddMinutes(-11))
            {
                announced = true;
                Report(progress, SessionPhase.Booting, "Still booting - the first launch after a Windows update is slow");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        throw new SessionException("The sandbox never finished booting.");
    }

    private async Task<string> WaitForIpAsync(string id, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _wsb.GetIpAsync(id, cancellationToken).ConfigureAwait(false) is { Length: > 0 } ip)
            {
                return ip;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new SessionException("The sandbox never reported an IP address.");
    }

    /// <summary>
    /// Waits for the port, then proves the whole auth path works. On failure this is where the
    /// provisioning log gets retrieved, since that is the only channel the sandbox has.
    /// </summary>
    private async Task WaitForSshAsync(
        SessionLayout layout,
        string id,
        string ip,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsPortOpenAsync(ip, 22, cancellationToken).ConfigureAwait(false)
                && await SshLauncher.ProbeAsync(layout, ip, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        var log = await TryRetrieveSetupLogAsync(layout, id, cancellationToken).ConfigureAwait(false);

        throw new SessionException(
            "The sandbox booted but never accepted an SSH connection." +
            (log is null ? string.Empty : Environment.NewLine + Environment.NewLine + log));
    }

    private static async Task<bool> IsPortOpenAsync(string ip, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);

            return client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Attaches a writable folder <i>after the fact</i> to copy the log out. Doing it only on
    /// failure is what keeps the project the sole writable host folder during a normal session.
    /// </summary>
    private async Task<string?> TryRetrieveSetupLogAsync(
        SessionLayout layout,
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            await _wsb.ShareAsync(id, layout.OutDirectory, @"C:\airlock\out", allowWrite: true, cancellationToken)
                .ConfigureAwait(false);

            await _wsb.ExecAsync(
                id,
                @"cmd.exe /c copy C:\airlock\setup.log C:\airlock\out\ & copy C:\airlock\ready.json C:\airlock\out\",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var ready = System.IO.Path.Combine(layout.OutDirectory, "ready.json");
            var log = System.IO.Path.Combine(layout.OutDirectory, "setup.log");

            if (File.Exists(ready))
            {
                return await File.ReadAllTextAsync(ready, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(log))
            {
                var lines = await File.ReadAllLinesAsync(log, cancellationToken).ConfigureAwait(false);

                return string.Join(Environment.NewLine, lines.TakeLast(20));
            }
        }
#pragma warning disable CA1031 // Diagnostics are best-effort; a failure here must not replace the real error.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    private static void Report(
        IProgress<(SessionPhase, string)>? progress,
        SessionPhase phase,
        string detail) => progress?.Report((phase, detail));
}

/// <summary>A session could not be established, with a message meant for the user.</summary>
public sealed class SessionException(string message) : Exception(message);
