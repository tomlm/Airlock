using System.Xml.Linq;

namespace Airlock.Sandbox;

/// <summary>Tri-state used by most Windows Sandbox settings.</summary>
public enum SandboxToggle
{
    Default,
    Enable,
    Disable,
}

/// <summary>One host folder projected into the sandbox.</summary>
/// <param name="HostFolder">Absolute host path. Must already exist or the sandbox fails to start.</param>
/// <param name="SandboxFolder">Absolute path inside the sandbox. Created if missing.</param>
/// <param name="ReadOnly">When true the sandbox cannot write through this mapping.</param>
public sealed record MappedFolder(string HostFolder, string SandboxFolder, bool ReadOnly);

/// <summary>
/// The object model for a Windows Sandbox configuration.
/// </summary>
/// <remarks>
/// Windows Sandbox <b>silently ignores elements it does not recognise</b> — a miscased
/// <c>&lt;vGPU&gt;</c> starts happily and leaves vGPU enabled, which is exactly the setting whose
/// absence produces a black screen on some GPUs. So the element names in <see cref="SandboxConfigWriter"/>
/// are asserted by unit test rather than trusted, and "it started" is never taken as evidence
/// that a setting applied.
/// </remarks>
public sealed class SandboxConfig
{
    /// <summary>vGPU is off by default: Airlock is headless, and vGPU blackscreens some adapters.</summary>
    public SandboxToggle VGpu { get; init; } = SandboxToggle.Disable;

    /// <summary>Networking is required — the agent needs to reach its API, and we connect over SSH.</summary>
    public SandboxToggle Networking { get; init; } = SandboxToggle.Enable;

    /// <summary>Off, or the agent can read the host clipboard, which quietly undoes the isolation.</summary>
    public SandboxToggle ClipboardRedirection { get; init; } = SandboxToggle.Disable;

    public SandboxToggle AudioInput { get; init; } = SandboxToggle.Disable;

    public SandboxToggle VideoInput { get; init; } = SandboxToggle.Disable;

    public SandboxToggle PrinterRedirection { get; init; } = SandboxToggle.Disable;

    public SandboxToggle ProtectedClient { get; init; } = SandboxToggle.Default;

    public int? MemoryInMB { get; init; }

    public IReadOnlyList<MappedFolder> MappedFolders { get; init; } = [];

    /// <summary>
    /// Always null for Airlock. Provisioning runs as SYSTEM via <c>wsb exec</c>, which needs no
    /// interactive session; a LogonCommand would require one.
    /// </summary>
    public string? LogonCommand { get; init; }
}
