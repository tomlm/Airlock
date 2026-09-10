using Airlock.Session;

namespace Airlock.Tests;

/// <summary>
/// The sandbox outlives any single command, so its identity and attached folders live on disk and
/// have to survive being reloaded - and has to cope with the state file being stale or corrupt.
/// </summary>
public class SandboxStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N"), "sandbox.json");

    [Fact]
    public void NothingRecorded_LoadsAsNull()
    {
        var store = new SandboxStateStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void State_SurvivesARoundTrip()
    {
        var store = new SandboxStateStore(_path);
        var saved = new SandboxState
        {
            Id = "1ec5f0a4-0000-0000-0000-000000000001",
            StartedUtc = DateTimeOffset.UtcNow,
            Folders = [new AttachedFolder(@"S:\src\A", @"C:\work\A")],
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal(@"S:\src\A", loaded.Folders[0].HostPath);
        Assert.Equal(@"C:\work\A", loaded.Folders[0].SandboxPath);
    }

    [Fact]
    public void CorruptStateFile_IsTreatedAsNothingRunning()
    {
        // Reconciliation against `wsb list` decides what is really true, so a damaged file should
        // never wedge the tool.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");

        Assert.Null(new SandboxStateStore(_path).Load());
    }

    [Fact]
    public void Clear_RemovesTheFile()
    {
        var store = new SandboxStateStore(_path);
        store.Save(new SandboxState { Id = "x" });

        store.Clear();

        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
