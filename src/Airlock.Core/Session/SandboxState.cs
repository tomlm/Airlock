using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airlock.Session;

/// <summary>A host folder currently mapped into the running sandbox.</summary>
public sealed record AttachedFolder(string HostPath, string SandboxPath);

/// <summary>
/// What Airlock remembers about the sandbox between invocations.
/// </summary>
/// <remarks>
/// The sandbox outlives any single <c>airlock</c> command, so its id, address and attached folders
/// have to survive on disk. Windows Sandbox allows only one instance, so there is at most one of
/// these at a time.
/// </remarks>
public sealed class SandboxState
{
    public required string Id { get; set; }

    public required string IpAddress { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>
    /// Every read-write folder attached so far. It only grows: Windows Sandbox has no unshare, so
    /// a folder stays writable until the sandbox is stopped.
    /// </summary>
    public List<AttachedFolder> Folders { get; set; } = [];

    /// <summary>
    /// True when this record was rebuilt by proving ownership over SSH rather than read from disk.
    /// </summary>
    /// <remarks>
    /// Nothing on the host records what a sandbox has mounted, so an adopted record cannot recover
    /// <see cref="Folders"/>. Anything already attached is still attached and still writable - it
    /// just will not be listed, which is worth saying out loud rather than showing an empty table.
    /// </remarks>
    public bool Adopted { get; set; }
}

/// <summary>Reads and writes <see cref="SandboxState"/> next to the rest of Airlock's data.</summary>
public sealed class SandboxStateStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airlock",
        "sandbox.json");

    public SandboxState? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SandboxState>(File.ReadAllText(Path), Options);
        }
        catch (JsonException)
        {
            // A corrupt state file should not wedge the tool; treat it as "nothing running" and
            // let reconciliation against `wsb list` decide what is actually true.
            return null;
        }
    }

    public void Save(SandboxState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(state, Options));
    }

    public void Clear()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
