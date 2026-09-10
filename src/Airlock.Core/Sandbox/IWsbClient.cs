namespace Airlock.Sandbox;

/// <summary>The <c>wsb.exe</c> verbs Airlock uses. Seam for testing without a real sandbox.</summary>
public interface IWsbClient
{
    /// <summary>
    /// Starts a sandbox and returns its id.
    /// </summary>
    /// <param name="requestedId">
    /// A caller-chosen id. Windows Sandbox honours this, which lets the session state file be
    /// written <i>before</i> the sandbox exists — so a crash during start cannot orphan a VM
    /// that nothing has a record of.
    /// </param>
    /// <param name="inlineXml">
    /// The configuration as inline XML. <c>--config</c> does not accept a file path.
    /// </param>
    Task<string> StartAsync(string requestedId, string inlineXml, CancellationToken cancellationToken = default);

    /// <summary>Ids of every running sandbox, including ones Airlock did not create.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The sandbox's IPv4 address, or null if it does not have one yet.</summary>
    Task<string?> GetIpAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Terminates the sandbox. Safe to call when it is already gone.</summary>
    Task StopAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the sandbox's desktop window.
    /// </summary>
    /// <remarks>
    /// Returns once the window has been launched, not when the user closes it, so the shell comes
    /// back straight away. A failure that happens immediately is still reported.
    /// </remarks>
    Task OpenDesktopAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a command in the sandbox and blocks until it exits.
    /// </summary>
    /// <returns>
    /// Whether <c>wsb</c> could dispatch the command — <b>not</b> whether the command succeeded.
    /// <c>wsb exec</c> returns neither the remote exit code nor its output: a remote
    /// <c>exit 7</c> still yields 0. Any real status has to come back through a shared folder.
    /// </returns>
    Task<bool> ExecAsync(
        string id,
        string command,
        WsbRunAs runAs = WsbRunAs.System,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps a host folder into an already-running sandbox. Airlock uses this only on failure, to
    /// attach a writable folder and retrieve the provisioning log — so the "one writable host
    /// folder" invariant holds for the whole of a normal session.
    /// </summary>
    Task ShareAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken = default);
}

/// <summary>The security context a <c>wsb exec</c> command runs in.</summary>
public enum WsbRunAs
{
    /// <summary>
    /// The only usable option for automation. <c>ExistingLogin</c> fails with <c>0x80070520</c>
    /// unless a sandbox client window is attached, because the user session is created by that
    /// connection.
    /// </summary>
    System,

    /// <summary>Requires an attached client window. Not used by Airlock.</summary>
    ExistingLogin,
}
