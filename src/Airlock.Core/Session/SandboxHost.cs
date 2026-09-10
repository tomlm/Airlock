using System.Net.Sockets;
using Airlock.Paths;
using Airlock.Sandbox;
using Airlock.Tools;

namespace Airlock.Session;

/// <summary>The phases starting a sandbox moves through, in order.</summary>
public enum SandboxPhase
{
    Preflight,
    Staging,
    Starting,
    Booting,
    Provisioning,
    Networking,
    WaitingForSsh,
    Ready,
    Attaching,
}

/// <summary>A sandbox could not be started or reached, with a message meant for the user.</summary>
public sealed class SessionException(string message) : Exception(message);

/// <summary>
/// Owns the one long-running sandbox: starting it, attaching project folders to it, connecting to
/// it, and stopping it.
/// </summary>
/// <remarks>
/// <para>
/// The sandbox is a shared machine that outlives any single command. The first <c>airlock</c>
/// invocation boots and provisions it; later ones find it already running and only attach whatever
/// folder they need. That is possible because <c>wsb share</c> works against a live sandbox.
/// </para>
/// <para>
/// Attached folders accumulate. Windows Sandbox offers no unshare, so every project attached during
/// the sandbox's life stays writable from inside it until <c>airlock stop</c>. <c>airlock list</c>
/// shows what is currently exposed.
/// </para>
/// </remarks>
public sealed class SandboxHost(
    IWsbClient? wsb = null,
    ToolsCache? tools = null,
    SandboxStateStore? store = null,
    SandboxLayout? layout = null)
{
    private readonly IWsbClient _wsb = wsb ?? new WsbClient();
    private readonly ToolsCache _tools = tools ?? new ToolsCache();
    private readonly SandboxStateStore _store = store ?? new SandboxStateStore();
    private readonly SandboxLayout _layout = layout ?? SandboxLayout.Default;

    public SandboxLayout Layout => _layout;

    /// <summary>
    /// Returns the running sandbox's state, or null if none is running.
    /// </summary>
    /// <remarks>
    /// Reconciles what we recorded against what <c>wsb</c> reports, because the sandbox can go away
    /// without Airlock being involved - a reboot, a manual close, or a crash.
    /// </remarks>
    public async Task<SandboxState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var running = await _wsb.ListAsync(cancellationToken).ConfigureAwait(false);
        var state = _store.Load();

        if (running.Count == 0)
        {
            // Nothing is running, so whatever we recorded is history and the key with it.
            if (state is not null)
            {
                _store.Clear();
                _layout.Delete();
            }

            return null;
        }

        if (state is not null && running.Contains(state.Id, StringComparer.OrdinalIgnoreCase))
        {
            return state;
        }

        // Our record is missing or stale, but something is running. Ask the sandbox itself who it
        // belongs to rather than guessing from a file we may have lost.
        _store.Clear();

        return await TryAdoptAsync(running, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out whether a running sandbox is one of ours by trying to log into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no way to ask <c>wsb</c> what a sandbox has mounted - <c>list</c> reports only ids,
    /// and <c>exec</c> returns neither output nor the remote exit code, so "does the tools folder
    /// exist" cannot be answered from the host. The SSH key can, and it is better evidence: the
    /// keypair is generated per sandbox and its public half is written only into that sandbox's
    /// <c>administrators_authorized_keys</c>. If it authenticates, this Airlock install provisioned
    /// that sandbox.
    /// </para>
    /// <para>
    /// This is what lets Airlock recover from a lost or corrupt state file instead of being locked
    /// out of its own sandbox. If the key is gone too, ownership genuinely cannot be proven and the
    /// sandbox is left alone.
    /// </para>
    /// </remarks>
    private async Task<SandboxState?> TryAdoptAsync(
        IReadOnlyList<string> running,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.PrivateKeyPath))
        {
            return null;
        }

        foreach (var id in running)
        {
            var ip = await _wsb.GetIpAsync(id, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(ip))
            {
                continue;
            }

            if (!await SshLauncher.ProbeAsync(_layout, ip, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // Folders cannot be recovered - nothing on the host records what was attached once the
            // state file is gone - so the rebuilt record says so rather than claiming none.
            var state = new SandboxState
            {
                Id = id,
                IpAddress = ip,
                StartedUtc = DateTimeOffset.UtcNow,
                Adopted = true,
            };

            _store.Save(state);

            return state;
        }

        return null;
    }

    /// <summary>
    /// Starts and provisions the sandbox if it is not already running, and returns its state.
    /// </summary>
    public async Task<SandboxState> EnsureRunningAsync(
        IProgress<(SandboxPhase Phase, string Detail)>? progress = null,
        int memoryInMB = 8192,
        CancellationToken cancellationToken = default)
    {
        if (await GetStateAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        Report(progress, SandboxPhase.Preflight, "Checking host");

        // Windows Sandbox is single-instance. Anything running that we did not record is someone
        // else's - very likely the user's own Windows Sandbox window - and must not be touched.
        var running = await _wsb.ListAsync(cancellationToken).ConfigureAwait(false);
        if (running.Count > 0)
        {
            throw new SessionException(
                $"A Windows Sandbox that Airlock did not start is already running ({running[0]}), " +
                "and Windows allows only one at a time. Close it and try again.");
        }

        await _tools.EnsureOpenSshAsync(
            new Progress<string>(m => Report(progress, SandboxPhase.Preflight, m)),
            cancellationToken).ConfigureAwait(false);

        Report(progress, SandboxPhase.Staging, "Preparing sandbox");

        // Choosing the id here means the sandbox is on record before it exists, so a crash during
        // start cannot leave a VM that nothing knows about.
        var sandboxId = Guid.NewGuid().ToString();

        _layout.Reset();

        var started = false;

        try
        {
            await SshKeys.GenerateAsync(_layout, cancellationToken).ConfigureAwait(false);
            SetupScript.Write(_layout, BuildPathPrepend(), BuildMachineEnv());

            // No project is mapped at start. Folders are attached later, on demand, which is what
            // lets one sandbox serve several projects.
            var config = new SandboxConfig
            {
                MemoryInMB = memoryInMB,
                MappedFolders =
                [
                    new MappedFolder(_tools.Root, SandboxPaths.Tools, ReadOnly: true),
                    new MappedFolder(_layout.ShareDirectory, SandboxPaths.Session, ReadOnly: true),
                    .. DotnetMount(),
                ],
            };

            await File.WriteAllTextAsync(
                _layout.ConfigPath, SandboxConfigWriter.ToPrettyXml(config), cancellationToken)
                .ConfigureAwait(false);

            Report(progress, SandboxPhase.Starting, "Starting sandbox");
            sandboxId = await _wsb
                .StartAsync(sandboxId, SandboxConfigWriter.ToInlineXml(config), cancellationToken)
                .ConfigureAwait(false);
            started = true;

            Report(progress, SandboxPhase.Booting, "Waiting for the sandbox to boot");
            await WaitForBootAsync(sandboxId, progress, cancellationToken).ConfigureAwait(false);

            // wsb exec blocks until the script exits, but reports nothing about how it went.
            Report(progress, SandboxPhase.Provisioning, "Installing SSH and toolchain (about a minute)");
            await _wsb.ExecAsync(
                sandboxId,
                $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {SandboxPaths.SetupScript}",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(progress, SandboxPhase.Networking, "Waiting for an IP address");
            var ip = await WaitForIpAsync(sandboxId, cancellationToken).ConfigureAwait(false);

            Report(progress, SandboxPhase.WaitingForSsh, "Verifying the connection");
            await WaitForSshAsync(sandboxId, ip, cancellationToken).ConfigureAwait(false);

            var state = new SandboxState
            {
                Id = sandboxId,
                IpAddress = ip,
                StartedUtc = DateTimeOffset.UtcNow,
            };

            _store.Save(state);
            Report(progress, SandboxPhase.Ready, "Ready");

            return state;
        }
        catch
        {
            // Nothing owns the sandbox yet, so a half-built one has to be cleaned up here.
            if (started)
            {
                await _wsb.StopAsync(sandboxId, CancellationToken.None).ConfigureAwait(false);
            }

            _store.Clear();
            _layout.Delete();
            throw;
        }
    }

    /// <summary>
    /// Maps a project into the running sandbox read-write, and returns its path inside.
    /// </summary>
    /// <remarks>
    /// Re-attaching a folder that is already mapped is a no-op, so running <c>airlock</c> twice in
    /// the same place costs nothing.
    /// </remarks>
    public async Task<string> AttachAsync(
        SandboxState state,
        ResolvedProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(project);

        var already = state.Folders
            .FirstOrDefault(f => f.HostPath.Equals(project.HostPath, StringComparison.OrdinalIgnoreCase));

        if (already is not null)
        {
            return already.SandboxPath;
        }

        var sandboxPath = AllocateSandboxPath(state, project.Name);

        await _wsb.ShareAsync(state.Id, project.HostPath, sandboxPath, allowWrite: true, cancellationToken)
            .ConfigureAwait(false);

        state.Folders.Add(new AttachedFolder(project.HostPath, sandboxPath));
        _store.Save(state);

        return sandboxPath;
    }

    /// <summary>
    /// Two projects can share a leaf name, and they cannot share a path inside the sandbox, so the
    /// second one gets a suffix.
    /// </summary>
    private static string AllocateSandboxPath(SandboxState state, string name)
    {
        var candidate = SandboxPaths.ForProject(name);
        var suffix = 2;

        while (state.Folders.Any(f => f.SandboxPath.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = SandboxPaths.ForProject($"{name}-{suffix}");
            suffix++;
        }

        return candidate;
    }

    /// <summary>Runs a command in the sandbox and returns its exit code.</summary>
    public Task<int> ConnectAsync(
        SandboxState state,
        string sandboxPath,
        IReadOnlyList<string> command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        // Staged on first use rather than at startup, so someone who never runs Claude never pays
        // for the 216 MB copy. The tools folder is a live mapping, so a file added now is visible
        // inside the already-running sandbox immediately.
        if (IsClaude(command))
        {
            _tools.EnsureClaude(progress);
        }

        var effective = command.Count > 0 ? ResolveCommand(command) : ["powershell.exe", "-NoLogo", "-NoExit"];

        return SshLauncher.ConnectAsync(
            _layout,
            state.IpAddress,
            SshLauncher.BuildRemoteCommand(sandboxPath, effective),
            CollectSecrets(),
            cancellationToken);
    }

    /// <summary>
    /// Opens the sandbox's desktop window.
    /// </summary>
    /// <remarks>
    /// The desktop signs in as Windows Sandbox's own default user, <c>WDAGUtilityAccount</c>, not as
    /// the <c>airlock</c> account that SSH sessions use. The two are separate Windows sessions with
    /// separate profiles, so anything done in the window - a browser sign-in, a tool installed into
    /// the user profile - does not carry into the agent's session. Mapped folders are shared, so it
    /// is genuinely useful for looking at files, watching what the agent did, or debugging a sandbox
    /// that came up wrong.
    /// </remarks>
    public async Task OpenDesktopAsync(SandboxState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _wsb.OpenDesktopAsync(state.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Destroys the sandbox and removes the key that went with it.</summary>
    /// <returns>False when there is no sandbox Airlock can prove is its own.</returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            return false;
        }

        await ForceStopAsync(state.Id, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Every running sandbox, whether or not Airlock started it.</summary>
    public Task<IReadOnlyList<string>> ListRunningAsync(CancellationToken cancellationToken = default) =>
        _wsb.ListAsync(cancellationToken);

    /// <summary>
    /// Stops a sandbox by id without asking whether it is ours, and clears our own state either way.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the case where a sandbox really is ours but ownership can no longer be
    /// proven - an interrupted run that removed the key while the VM survived. Without this, Airlock
    /// refuses to start <i>and</i> refuses to stop, and the only way out is <c>wsb stop --id</c>.
    /// Deciding to use it is the caller's, since the sandbox may belong to the user instead.
    /// </remarks>
    public async Task ForceStopAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _wsb.StopAsync(id, cancellationToken).ConfigureAwait(false);

        _store.Clear();
        _layout.Delete();
    }

    /// <summary>Points a bare agent name at the staged binary; anything else runs off the guest PATH.</summary>
    public static IReadOnlyList<string> ResolveCommand(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Count == 0 || !IsClaude(command))
        {
            return command;
        }

        return new List<string>(command) { [0] = SandboxPaths.ClaudeExe };
    }

    private static bool IsClaude(IReadOnlyList<string> command) =>
        command.Count > 0 &&
        System.IO.Path.GetFileNameWithoutExtension(command[0])
            .Equals("claude", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<MappedFolder> DotnetMount()
    {
        var dotnet = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        if (Directory.Exists(dotnet))
        {
            yield return new MappedFolder(dotnet, SandboxPaths.Dotnet, ReadOnly: true);
        }
    }

    private static List<string> BuildPathPrepend() =>
    [
        SandboxPaths.Dotnet,
        SandboxPaths.Claude,
        SandboxPaths.OpenSsh,
    ];

    private static Dictionary<string, string> BuildMachineEnv() => new(StringComparer.Ordinal)
    {
        ["DOTNET_ROOT"] = SandboxPaths.Dotnet,
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",
        ["AIRLOCK"] = "1",
    };

    /// <summary>
    /// Picks up the host's agent credentials so the session starts already authenticated. They
    /// travel in ssh.exe's own environment via SendEnv, so they reach no file and no argument list.
    /// </summary>
    private static Dictionary<string, string> CollectSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in SetupScript.AcceptedEnvNames)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                secrets[name] = value;
            }
        }

        return secrets;
    }

    private async Task WaitForBootAsync(
        string id,
        IProgress<(SandboxPhase, string)>? progress,
        CancellationToken cancellationToken)
    {
        // A cold boot after a Windows update rebuilds the base image and really can take ten
        // minutes; a warm one is a second or two.
        var start = DateTimeOffset.UtcNow;
        var deadline = start.AddMinutes(12);
        var announced = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _wsb.ExecAsync(id, "cmd.exe /c exit 0", WsbRunAs.System, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            if (!announced && DateTimeOffset.UtcNow - start > TimeSpan.FromSeconds(60))
            {
                announced = true;
                Report(progress, SandboxPhase.Booting,
                    "Still booting - the first launch after a Windows update is slow");
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
    private async Task WaitForSshAsync(string id, string ip, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsPortOpenAsync(ip, 22, cancellationToken).ConfigureAwait(false)
                && await SshLauncher.ProbeAsync(_layout, ip, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        var log = await TryRetrieveSetupLogAsync(id, cancellationToken).ConfigureAwait(false);

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
    }

    /// <summary>
    /// Attaches a writable folder <i>after the fact</i> to copy the log out, because
    /// <c>wsb exec</c> returns neither output nor the remote exit code.
    /// </summary>
    private async Task<string?> TryRetrieveSetupLogAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await _wsb.ShareAsync(id, _layout.OutDirectory, SandboxPaths.Out, allowWrite: true, cancellationToken)
                .ConfigureAwait(false);

            await _wsb.ExecAsync(
                id,
                $"cmd.exe /c copy {SandboxPaths.SetupLog} {SandboxPaths.Out}\\ & "
                + $"copy {SandboxPaths.SetupResult} {SandboxPaths.Out}\\",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var ready = System.IO.Path.Combine(_layout.OutDirectory, "ready.json");
            if (File.Exists(ready))
            {
                return await File.ReadAllTextAsync(ready, cancellationToken).ConfigureAwait(false);
            }

            var log = System.IO.Path.Combine(_layout.OutDirectory, "setup.log");
            if (File.Exists(log))
            {
                var lines = await File.ReadAllLinesAsync(log, cancellationToken).ConfigureAwait(false);

                return string.Join(Environment.NewLine, lines.TakeLast(20));
            }
        }
#pragma warning disable CA1031 // Diagnostics are best-effort; failing here must not replace the real error.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    private static void Report(
        IProgress<(SandboxPhase, string)>? progress,
        SandboxPhase phase,
        string detail) => progress?.Report((phase, detail));
}
