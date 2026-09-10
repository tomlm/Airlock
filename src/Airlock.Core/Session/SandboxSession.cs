using Airlock.Sandbox;

namespace Airlock.Session;

/// <summary>
/// A provisioned sandbox, ready to accept connections.
/// </summary>
/// <remarks>
/// Establishing the session and connecting to it are separate steps on purpose. A progress spinner
/// has to be torn down <i>before</i> the terminal is handed to the agent, or it competes with the
/// agent's own full-screen rendering. Splitting them lets the caller close its display in between.
/// </remarks>
public sealed class SandboxSession(
    IWsbClient wsb,
    SessionLayout layout,
    string sandboxId,
    string ipAddress,
    string sandboxProjectPath,
    bool keep) : IAsyncDisposable
{
    public string SandboxId { get; } = sandboxId;

    public string IpAddress { get; } = ipAddress;

    /// <summary>Runs a command in the sandbox and returns its exit code.</summary>
    /// <param name="command">Empty for an interactive shell.</param>
    public Task<int> ConnectAsync(IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var effective = command.Count > 0 ? command : ["powershell.exe", "-NoLogo", "-NoExit"];
        var remote = SshLauncher.BuildRemoteCommand(sandboxProjectPath, effective);

        return SshLauncher.ConnectAsync(layout, IpAddress, remote, CollectSecrets(), cancellationToken);
    }

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

    /// <summary>
    /// Destroys the sandbox and removes the session directory, which is what finally gets the
    /// private key off disk. Runs even when the session ended badly.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!keep)
        {
            // Deliberately not cancellable: teardown must still happen when the session was
            // cancelled, and leaking a VM is worse than blocking briefly.
            await wsb.StopAsync(SandboxId, CancellationToken.None).ConfigureAwait(false);
        }

        layout.Dispose();
    }
}
