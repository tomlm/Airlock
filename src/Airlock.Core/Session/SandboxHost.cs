using Airlock.Configuration;
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
    Ready,
    Attaching,
}

/// <summary>A sandbox could not be started or reached, with a message meant for the user.</summary>
public sealed class SessionException(string message) : Exception(message);

/// <summary>
/// Owns the one long-running sandbox: starting it with everything configured mounted, opening
/// airlocks in it, and stopping it.
/// </summary>
/// <remarks>
/// <para>
/// The sandbox is a configured machine rather than something assembled per command. <c>start</c>
/// mounts every tool read-only onto PATH and every airlock read-write, then shows the desktop.
/// Later commands find it already running and launch into it.
/// </para>
/// <para>
/// Everything the guest tells the host comes back through one writable folder, because
/// <c>wsb exec</c> returns neither output nor the remote exit code.
/// </para>
/// </remarks>
public sealed class SandboxHost(
    IWsbClient? wsb = null,
    ToolsCache? tools = null,
    SandboxStateStore? store = null,
    SandboxLayout? layout = null,
    AirlockConfigStore? config = null)
{
    private readonly IWsbClient _wsb = wsb ?? new WsbClient();
    private readonly ToolsCache _tools = tools ?? new ToolsCache();
    private readonly SandboxStateStore _store = store ?? new SandboxStateStore();
    private readonly SandboxLayout _layout = layout ?? SandboxLayout.Default;
    private readonly AirlockConfigStore _config = config ?? new AirlockConfigStore();

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
    /// Works out whether a running sandbox is one of ours by writing into it and seeing where it
    /// lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no way to ask <c>wsb</c> what a sandbox has mounted - <c>list</c> reports only ids,
    /// and <c>exec</c> returns neither output nor the remote exit code. But our handoff folder is
    /// mapped read-write from a folder we own, so telling the guest to write a nonce into it and
    /// finding that nonce on our side proves the sandbox has our folder mapped, and is therefore
    /// ours.
    /// </para>
    /// <para>
    /// This is what lets Airlock recover from a lost state file instead of being locked out of its
    /// own sandbox, and what stops it touching a Windows Sandbox the user opened themselves.
    /// </para>
    /// </remarks>
    private async Task<SandboxState?> TryAdoptAsync(
        IReadOnlyList<string> running,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_layout.OutDirectory))
        {
            return null;
        }

        foreach (var id in running)
        {
            var nonce = Guid.NewGuid().ToString("N");

            TryDelete(_layout.ProbePath);

            await _wsb.ExecAsync(
                id,
                $"cmd.exe /c echo {nonce}> {SandboxPaths.Out}\\probe.txt",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!File.Exists(_layout.ProbePath))
            {
                continue;
            }

            var written = (await File.ReadAllTextAsync(_layout.ProbePath, cancellationToken)
                .ConfigureAwait(false)).Trim();

            TryDelete(_layout.ProbePath);

            if (!written.Equals(nonce, StringComparison.Ordinal))
            {
                continue;
            }

            // Airlocks cannot be recovered - nothing on the host records which of the configured
            // ones this sandbox actually mounted - so the rebuilt record says so.
            var state = new SandboxState
            {
                Id = id,
                StartedUtc = DateTimeOffset.UtcNow,
                Adopted = true,
            };

            _store.Save(state);

            return state;
        }

        return null;
    }

    /// <summary>
    /// Starts the sandbox with every configured tool and airlock mounted, and provisions it.
    /// </summary>
    /// <remarks>
    /// The desktop is deliberately <i>not</i> opened here. It inherits machine environment at logon
    /// and never re-reads it, so the window has to come up after provisioning has finished setting
    /// PATH - which is the caller's job, once this returns.
    /// </remarks>
    public async Task<SandboxState> EnsureRunningAsync(
        IProgress<(SandboxPhase Phase, string Detail)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await GetStateAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        Report(progress, SandboxPhase.Preflight, "Checking host");

        // Windows Sandbox is single-instance. Anything running that we could not claim is someone
        // else's - very likely the user's own Windows Sandbox window - and must not be touched.
        var running = await _wsb.ListAsync(cancellationToken).ConfigureAwait(false);
        if (running.Count > 0)
        {
            throw new SessionException(
                $"A Windows Sandbox that Airlock did not start is already running ({running[0]}), " +
                "and Windows allows only one at a time. Close it and try again.");
        }

        var config = _config.LoadOrCreate();
        _config.RefreshDetected(config);

        Report(progress, SandboxPhase.Staging, "Preparing sandbox");

        // Choosing the id here means the sandbox is on record before it exists, so a crash during
        // start cannot leave a VM that nothing knows about.
        var sandboxId = Guid.NewGuid().ToString();

        _layout.Reset();

        var started = false;

        try
        {
            var mounts = BuildMounts(config, out var missing);

            foreach (var gone in missing)
            {
                Report(progress, SandboxPhase.Staging, $"Skipping '{gone}': its folder is gone");
            }

            SetupScript.Write(_layout, BuildPathPrepend(config), BuildMachineEnv(config), config.Network);
            SetupScript.WriteSecrets(_layout, config.Secrets);

            var sandbox = new SandboxConfig { MemoryInMB = config.MemoryMb, MappedFolders = mounts };

            await File.WriteAllTextAsync(
                _layout.ConfigPath, SandboxConfigWriter.ToPrettyXml(sandbox), cancellationToken)
                .ConfigureAwait(false);

            Report(progress, SandboxPhase.Starting, "Starting sandbox");
            sandboxId = await StartWithRetryAsync(
                sandboxId, SandboxConfigWriter.ToInlineXml(sandbox), progress, cancellationToken)
                .ConfigureAwait(false);
            started = true;

            Report(progress, SandboxPhase.Booting, "Waiting for the sandbox to boot");
            await WaitForBootAsync(sandboxId, progress, cancellationToken).ConfigureAwait(false);

            Report(progress, SandboxPhase.Provisioning, "Setting up tools and paths");
            await ProvisionAsync(sandboxId, progress, cancellationToken).ConfigureAwait(false);

            var state = new SandboxState
            {
                Id = sandboxId,
                StartedUtc = DateTimeOffset.UtcNow,
                Folders = [.. config.Airlocks.Select(a => new AttachedFolder(a.Host, SandboxPaths.ForProject(a.Name)))],
            };

            _store.Save(state);
            Report(progress, SandboxPhase.Ready, "Ready");

            return state;
        }
        catch
        {
            if (started)
            {
                await _wsb.StopAsync(sandboxId, CancellationToken.None).ConfigureAwait(false);
            }

            _store.Clear();
            _layout.Delete();
            throw;
        }
        finally
        {
            // Whatever happened, the credential handoff must not be left lying about.
            SetupScript.DeleteSecrets(_layout);
        }
    }

    /// <summary>
    /// Starts the sandbox, retrying while the previous one is still letting go of its files.
    /// </summary>
    /// <remarks>
    /// <c>wsb stop</c> returns before teardown finishes, so a start soon after one fails with
    /// <c>0x80070020</c> - which stopping and immediately restarting makes easy to hit. Waiting and
    /// trying again is the whole fix; the condition clears within a few seconds.
    /// </remarks>
    private async Task<string> StartWithRetryAsync(
        string requestedId,
        string inlineXml,
        IProgress<(SandboxPhase, string)>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var announced = false;

        while (true)
        {
            try
            {
                return await _wsb.StartAsync(requestedId, inlineXml, cancellationToken).ConfigureAwait(false);
            }
            catch (WsbException ex) when (ex.IsResourceBusy && DateTimeOffset.UtcNow < deadline)
            {
                if (!announced)
                {
                    announced = true;
                    Report(progress, SandboxPhase.Starting, "Waiting for the previous sandbox to finish closing");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs setup.ps1 and waits for its verdict.
    /// </summary>
    /// <remarks>
    /// <c>wsb exec</c> blocks until the script exits but says nothing about how it went, so the
    /// answer comes from the file the script writes into the shared folder.
    /// </remarks>
    private async Task ProvisionAsync(
        string sandboxId,
        IProgress<(SandboxPhase, string)>? progress,
        CancellationToken cancellationToken)
    {
        TryDelete(_layout.ReadyPath);
        TryDelete(_layout.PhasePath);

        // Deliberately not awaited yet. `wsb exec` blocks until the script exits, so awaiting it
        // first would mean a hung script hangs here with no timeout and nothing on screen - the
        // deadline below would not even start counting. Watching for the verdict while the script
        // runs is what makes a stuck step reportable.
        var exec = _wsb.ExecAsync(
            sandboxId,
            $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {SandboxPaths.SetupScript}",
            WsbRunAs.System,
            cancellationToken: cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        string? step = null;

        while (!File.Exists(_layout.ReadyPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReadStep() is { } current && current != step)
            {
                step = current;
                Report(progress, SandboxPhase.Provisioning, $"Setting up tools and paths - {step}");
            }

            if (exec.IsCompleted)
            {
                // The script has exited. Give the file a moment to land before calling it a failure.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                break;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new SessionException(
                    $"Provisioning has been running for five minutes{Where(step)} and has not " +
                    "reported back." + await FetchSetupLogAsync(sandboxId, cancellationToken).ConfigureAwait(false));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        // Surfaces anything wsb itself threw, now that we are no longer racing it.
        await exec.ConfigureAwait(false);

        if (!File.Exists(_layout.ReadyPath))
        {
            throw new SessionException(
                $"Provisioning stopped{Where(step)} without reporting back." +
                await FetchSetupLogAsync(sandboxId, cancellationToken).ConfigureAwait(false));
        }

        var verdict = await File.ReadAllTextAsync(_layout.ReadyPath, cancellationToken).ConfigureAwait(false);

        if (!verdict.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException(
                $"Provisioning failed{Where(step)}: {verdict.Trim()}" +
                await FetchSetupLogAsync(sandboxId, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The step the guest last reported, if it got far enough to report one.</summary>
    private string? ReadStep()
    {
        try
        {
            return File.Exists(_layout.PhasePath)
                ? File.ReadAllText(_layout.PhasePath).Trim() is { Length: > 0 } s ? s : null
                : null;
        }
        catch (IOException)
        {
            // Caught mid-write by the guest; the next poll will get it.
            return null;
        }
    }

    private static string Where(string? step) => step is null ? string.Empty : $" at '{step}'";

    /// <summary>
    /// Brings the setup log back from the guest.
    /// </summary>
    /// <remarks>
    /// The log lives outside the shared folder, so on a hang it has to be fetched deliberately -
    /// which still works, because a wedged script does not stop <c>wsb exec</c> starting another
    /// process. On a clean failure setup.ps1 has already copied it, and this is a no-op.
    /// </remarks>
    private async Task<string> FetchSetupLogAsync(string sandboxId, CancellationToken cancellationToken)
    {
        try
        {
            await _wsb.ExecAsync(
                sandboxId,
                $"cmd.exe /c copy /y {SandboxPaths.SetupLog} {SandboxPaths.Out}\\",
                WsbRunAs.System,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Diagnostics are best-effort; failing here must not replace the real error.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return ReadSetupLog();
    }

    private string ReadSetupLog()
    {
        var log = System.IO.Path.Combine(_layout.OutDirectory, "setup.log");

        if (!File.Exists(log))
        {
            return string.Empty;
        }

        var lines = File.ReadAllLines(log).TakeLast(20);

        return Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Every mount the sandbox starts with: tools read-only, airlocks read-write, plus Airlock's own
    /// two folders.
    /// </summary>
    /// <param name="missing">Configured entries whose host folder no longer exists.</param>
    private List<MappedFolder> BuildMounts(AirlockConfig config, out List<string> missing)
    {
        missing = [];

        var mounts = new List<MappedFolder>
        {
            new(_layout.ShareDirectory, SandboxPaths.Session, ReadOnly: true),
            new(_layout.OutDirectory, SandboxPaths.Out, ReadOnly: false),
        };

        // The staged agent CLI, which is Airlock's own rather than a configured tool.
        if (Directory.Exists(_tools.Root))
        {
            mounts.Add(new MappedFolder(_tools.Root, SandboxPaths.ForTool("airlock"), ReadOnly: true));
        }

        foreach (var tool in config.Tools)
        {
            if (!Directory.Exists(tool.Host))
            {
                missing.Add(tool.Id);
                continue;
            }

            mounts.Add(new MappedFolder(tool.Host, SandboxPaths.ForTool(tool.Id), ReadOnly: true));
        }

        foreach (var airlock in config.Airlocks)
        {
            if (!Directory.Exists(airlock.Host))
            {
                missing.Add(airlock.Name);
                continue;
            }

            mounts.Add(new MappedFolder(airlock.Host, SandboxPaths.ForProject(airlock.Name), ReadOnly: false));
        }

        return mounts;
    }

    /// <summary>PATH entries for every configured tool, in configuration order.</summary>
    private List<string> BuildPathPrepend(AirlockConfig config)
    {
        var path = new List<string>();

        foreach (var tool in config.Tools.Where(t => Directory.Exists(t.Host)))
        {
            var mount = SandboxPaths.ForTool(tool.Id);

            path.AddRange(tool.PathEntries.Select(entry =>
                string.IsNullOrEmpty(entry) ? mount : System.IO.Path.Combine(mount, entry)));
        }

        if (Directory.Exists(_tools.ClaudeDirectory))
        {
            path.Add(System.IO.Path.Combine(SandboxPaths.ForTool("airlock"), "claude"));
        }

        return path;
    }

    private static Dictionary<string, string> BuildMachineEnv(AirlockConfig config)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AIRLOCK"] = "1",
        };

        foreach (var tool in config.Tools.Where(t => Directory.Exists(t.Host)))
        {
            var mount = SandboxPaths.ForTool(tool.Id);

            foreach (var (name, value) in tool.EnvEntries)
            {
                // {mount} is the only substitution: a tool cannot know its sandbox path until now.
                env[name] = value.Replace("{mount}", mount, StringComparison.Ordinal);
            }
        }

        return env;
    }

    /// <summary>
    /// Mounts an airlock into a sandbox that is already running, for one added after start.
    /// </summary>
    public async Task<string> AttachAsync(
        SandboxState state,
        AirlockDefinition airlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(airlock);

        var sandboxPath = SandboxPaths.ForProject(airlock.Name);

        if (state.Folders.Any(f => f.HostPath.Equals(airlock.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return sandboxPath;
        }

        await _wsb.ShareAsync(state.Id, airlock.Host, sandboxPath, allowWrite: true, cancellationToken)
            .ConfigureAwait(false);

        state.Folders.Add(new AttachedFolder(airlock.Host, sandboxPath));
        _store.Save(state);

        return sandboxPath;
    }

    /// <summary>
    /// Launches a tool in the sandbox's desktop, in the given airlock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-r ExistingLogin</c> runs in the interactive session, which is what puts a real window on
    /// screen - but it needs the desktop to be logged on, and that only happens once a client is
    /// attached. So a failure here is treated as "no window yet": open one, wait for the session,
    /// and try once more.
    /// </para>
    /// <para>
    /// The launch goes through <c>cmd /c start</c> because <c>wsb exec</c> blocks until its command
    /// exits, and we want the prompt back rather than to wait on a window the user is working in.
    /// </para>
    /// </remarks>
    public async Task OpenAsync(
        SandboxState state,
        string sandboxPath,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var title = System.IO.Path.GetFileName(sandboxPath.TrimEnd('\\'));
        var tool = command.Count == 0 ? string.Empty : string.Join(' ', command);
        var launch = $"cmd.exe /c start \"{title}\" cmd.exe /k {tool}".TrimEnd();

        if (await _wsb.ExecAsync(state.Id, launch, WsbRunAs.ExistingLogin, sandboxPath, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        // No interactive session yet. Opening the window creates one.
        await _wsb.OpenDesktopAsync(state.Id, cancellationToken).ConfigureAwait(false);
        await WaitForDesktopAsync(state.Id, cancellationToken).ConfigureAwait(false);

        if (!await _wsb.ExecAsync(state.Id, launch, WsbRunAs.ExistingLogin, sandboxPath, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new SessionException(
                "The sandbox is running but would not launch anything in its desktop. " +
                "Try 'airlock connect' and see what state the window is in.");
        }
    }

    /// <summary>Waits for the desktop session to exist, by probing the thing that needs it.</summary>
    private async Task WaitForDesktopAsync(string sandboxId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _wsb.ExecAsync(sandboxId, "cmd.exe /c exit 0", WsbRunAs.ExistingLogin,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new SessionException("The sandbox desktop never finished signing in.");
    }

    /// <summary>Opens the sandbox's desktop window.</summary>
    public Task OpenDesktopAsync(SandboxState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        return _wsb.OpenDesktopAsync(state.Id, cancellationToken);
    }

    /// <summary>Destroys the sandbox and clears what we recorded about it.</summary>
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
    /// The escape hatch for a sandbox that really is ours but can no longer be proven so - an
    /// interrupted run that removed the handoff folder while the VM survived. Without this, Airlock
    /// refuses to start <i>and</i> refuses to stop.
    /// </remarks>
    public async Task ForceStopAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _wsb.StopAsync(id, cancellationToken).ConfigureAwait(false);

        // wsb stop returns before teardown finishes, and the next start fails with 0x80070020 while
        // the old VM still holds its files. Waiting for it to leave the list closes that window here
        // as well as at the start end, so `airlock stop; airlock start` behaves.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var running = await _wsb.ListAsync(cancellationToken).ConfigureAwait(false);

            if (!running.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        _store.Clear();
        _layout.Delete();
    }

    /// <summary>Stages the agent CLI, so `airlock open claude` has something to launch.</summary>
    public void EnsureAgent(IProgress<string>? progress) => _tools.EnsureClaude(progress);

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Report(
        IProgress<(SandboxPhase, string)>? progress,
        SandboxPhase phase,
        string detail) => progress?.Report((phase, detail));
}
