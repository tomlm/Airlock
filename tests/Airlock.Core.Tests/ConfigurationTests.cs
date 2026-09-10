using Airlock.Configuration;

namespace Airlock.Tests;

/// <summary>
/// The config is the sandbox's definition now - which folders are writable and what lands on PATH -
/// so it has to survive a round trip and refuse to record a credential.
/// </summary>
public class AirlockConfigStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N"), "airlock.json");

    [Fact]
    public void NothingWritten_LoadsAsNull() => Assert.Null(new AirlockConfigStore(_path).Load());

    [Fact]
    public void Config_SurvivesARoundTrip()
    {
        var store = new AirlockConfigStore(_path);
        var saved = new AirlockConfig
        {
            MemoryMb = 12288,
            Airlocks = [new AirlockDefinition(@"S:\src\foo", "foo")],
            Tools = [new ToolDefinition("git", @"C:\Program Files\Git", Detected: true, Path: ["cmd"])],
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(12288, loaded.MemoryMb);
        Assert.Equal(@"S:\src\foo", loaded.Airlocks[0].Host);
        Assert.Equal("foo", loaded.Airlocks[0].Name);
        Assert.Equal(["cmd"], loaded.Tools[0].PathEntries);
        Assert.True(loaded.Tools[0].Detected);
    }

    [Fact]
    public void FirstRun_SeedsWithWhateverIsInstalled()
    {
        // Without this, a fresh install needs several `tools add` calls before anything works.
        var config = new AirlockConfigStore(_path).LoadOrCreate();

        Assert.True(File.Exists(_path));
        Assert.NotEmpty(config.Tools);
    }

    [Fact]
    public void HandEditedFileWithATypo_SaysSoRatherThanResetting()
    {
        // Silently replacing it would silently drop the list of writable folders.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        var ex = Assert.Throws<InvalidOperationException>(() => new AirlockConfigStore(_path).Load());

        Assert.Contains(_path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolCarryingACredential_IsRefused()
    {
        var config = new AirlockConfig
        {
            Tools =
            [
                new ToolDefinition(
                    "bad",
                    @"S:\bin",
                    Env: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ANTHROPIC_API_KEY"] = "sk-should-never-be-persisted",
                    }),
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new AirlockConfigStore(_path).Save(config));

        Assert.Contains("ANTHROPIC_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingASecretIsFine_ItIsTheValueThatMustNotBeStored()
    {
        var config = new AirlockConfig { Secrets = ["ANTHROPIC_API_KEY"] };

        new AirlockConfigStore(_path).Save(config);

        Assert.Equal(["ANTHROPIC_API_KEY"], new AirlockConfigStore(_path).Load()!.Secrets);
    }

    [Fact]
    public void TwoCheckoutsWithTheSameLeafName_GetDistinctFolders()
    {
        var config = new AirlockConfig { Airlocks = [new AirlockDefinition(@"S:\src\foo", "foo")] };

        Assert.Equal("foo-2", config.AllocateName("foo"));

        config.Airlocks.Add(new AirlockDefinition(@"S:\work\foo", "foo-2"));

        Assert.Equal("foo-3", config.AllocateName("foo"));
        Assert.Equal("bar", config.AllocateName("bar"));
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

/// <summary>
/// Detection has to check what is in a folder rather than what it is called: this machine has a
/// Python folder containing nothing but <c>Lib\</c>, and a Store stub on PATH that Sandbox cannot
/// map at all.
/// </summary>
public class ToolDetectorTests
{
    [Fact]
    public void StorePython_IsNeverUsable()
    {
        var storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");

        Assert.False(ToolDetector.IsUsablePython(storePath));
    }

    [Fact]
    public void AFolderWithoutAnInterpreter_IsNotPython()
    {
        var decoy = Path.Combine(Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N"), "Python314");
        Directory.CreateDirectory(Path.Combine(decoy, "Lib"));

        try
        {
            // Exactly the shape of the decoy on this machine: the name matches, nothing else does.
            Assert.False(ToolDetector.IsUsablePython(decoy));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(decoy)!, recursive: true);
        }
    }

    [Fact]
    public void DetectedTools_AreMarkedAsSuch()
    {
        // So a moved or upgraded toolchain can be re-probed, while hand-added ones are left alone.
        foreach (var tool in ToolDetector.DetectAll())
        {
            Assert.True(tool.Detected, $"'{tool.Id}' should be marked detected");
            Assert.True(Directory.Exists(tool.Host), $"'{tool.Id}' points at a folder that exists");
        }
    }

    [Fact]
    public void Dotnet_CarriesItsRootAsAMountPlaceholder()
    {
        var dotnet = ToolDetector.DetectDotnet();

        if (dotnet is null)
        {
            return;
        }

        // The sandbox path is not known until start, so the value is substituted then.
        Assert.Equal("{mount}", dotnet.EnvEntries["DOTNET_ROOT"]);
    }

    [Fact]
    public void Git_PutsOnlyItsCmdFolderOnPath()
    {
        var git = ToolDetector.DetectGit();

        if (git is null)
        {
            return;
        }

        Assert.Equal(["cmd"], git.PathEntries);
    }
}
