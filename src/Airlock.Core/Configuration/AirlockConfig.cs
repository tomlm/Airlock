using System.Text.Json.Serialization;

namespace Airlock.Configuration;

/// <summary>A workspace mounted read-write into the sandbox. This is what "an airlock" means.</summary>
/// <param name="Host">The real host folder.</param>
/// <param name="Name">Its folder name inside the sandbox, unique across all airlocks.</param>
public sealed record AirlockDefinition(string Host, string Name);

/// <summary>
/// A host folder mounted read-only and put on the sandbox's PATH.
/// </summary>
/// <param name="Id">Short name; also the folder it mounts as under the tools root.</param>
/// <param name="Host">The host folder to mount.</param>
/// <param name="Detected">
/// True when Airlock found this itself. Detected tools are re-probed on start, so moving or
/// upgrading a toolchain fixes itself; hand-added ones are taken literally and left alone.
/// </param>
/// <param name="Path">
/// Subfolders of the mount to prepend to PATH. An empty string means the mount root, which is the
/// usual case; Git needs <c>cmd</c>, Python wants its root and <c>Scripts</c>.
/// </param>
/// <param name="Env">
/// Environment variables to set, where <c>{mount}</c> expands to the tool's path inside the sandbox.
/// </param>
public sealed record ToolDefinition(
    string Id,
    string Host,
    bool Detected = false,
    IReadOnlyList<string>? Path = null,
    IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>Defaults to the mount root, which is right for everything but Git and Python.</summary>
    /// <remarks>
    /// Ignored when serialising: this is a convenience over <see cref="Path"/>, and writing both
    /// would put a redundant copy into a file that is meant to be edited by hand.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> PathEntries => Path ?? [string.Empty];

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EnvEntries =>
        Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>What the sandbox may reach on the network.</summary>
public sealed class NetworkConfig
{
    /// <summary>
    /// Block outbound traffic to private ranges, so the agent cannot reach your NAS or router.
    /// </summary>
    /// <remarks>
    /// The guest's own subnet and gateway sit inside 172.16.0.0/12, so the rules must exempt them or
    /// the sandbox severs its own DNS and default route.
    /// </remarks>
    public bool BlockLan { get; set; } = true;

    /// <summary>Addresses to allow through despite <see cref="BlockLan"/>.</summary>
    public List<string> Allow { get; set; } = [];
}

/// <summary>
/// Everything Airlock knows about how the sandbox should be built.
/// </summary>
/// <remarks>
/// The sandbox is a configured machine rather than something assembled per command: <c>start</c>
/// mounts every tool and every airlock listed here. Editing this file and restarting is the way to
/// change what the sandbox contains.
/// </remarks>
public sealed class AirlockConfig
{
    public int MemoryMb { get; set; } = 8192;

    public NetworkConfig Network { get; set; } = new();

    /// <summary>Read-write workspaces.</summary>
    public List<AirlockDefinition> Airlocks { get; set; } = [];

    /// <summary>Read-only mounts placed on PATH.</summary>
    public List<ToolDefinition> Tools { get; set; } = [];

    /// <summary>
    /// Host environment variables to carry into the sandbox, by name.
    /// </summary>
    /// <remarks>
    /// Names only - the values are read from the host environment at start. Keeping the list here
    /// rather than the values is what stops a credential ever being written into this file.
    /// </remarks>
    public List<string> Secrets { get; set; } =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
    ];

    [JsonIgnore]
    public IEnumerable<string> AirlockNames => Airlocks.Select(a => a.Name);

    /// <summary>Finds the airlock covering a host path, if one is configured.</summary>
    public AirlockDefinition? FindByHost(string hostPath) =>
        Airlocks.FirstOrDefault(a => a.Host.Equals(hostPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Picks a sandbox folder name that no other airlock is using.
    /// </summary>
    /// <remarks>
    /// Two checkouts can easily share a leaf name - <c>src\foo</c> and <c>work\foo</c> - but they
    /// cannot share a folder inside the sandbox, so the second one gets a suffix.
    /// </remarks>
    public string AllocateName(string preferred)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferred);

        if (!AirlockNames.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            return preferred;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{preferred}-{suffix}";

            if (!AirlockNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
