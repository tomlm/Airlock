using Airlock.Configuration;

namespace Airlock.Tests;

/// <summary>
/// Finding an airlock to act on. <c>airlock list</c> shows names, so a name is what someone types
/// next - and for a while the argument was ignored entirely, leaving `airlock remove spikes` to
/// operate on the current directory and report that the current directory was not an airlock.
/// </summary>
public class AirlockLookupTests
{
    private static AirlockConfig Config() => new()
    {
        Airlocks =
        [
            new AirlockDefinition(@"S:\github\Airlock", "Airlock"),
            new AirlockDefinition(@"S:\github\Airlock\spikes", "spikes"),
        ],
    };

    [Fact]
    public void ByHostPath_IsFound()
    {
        Assert.Equal("spikes", Config().FindByHost(@"S:\github\Airlock\spikes")?.Name);
    }

    [Fact]
    public void HostPathMatching_IgnoresCase()
    {
        // Windows paths are case-insensitive, and `list` prints them however they were recorded.
        Assert.Equal("Airlock", Config().FindByHost(@"s:\GITHUB\airlock")?.Name);
    }

    [Fact]
    public void AnUnknownPath_IsNotFound()
    {
        Assert.Null(Config().FindByHost(@"S:\somewhere\else"));
    }

    [Fact]
    public void AFolderInsideAnAirlock_ResolvesToItRatherThanANewOne()
    {
        // Otherwise `airlock open` in a subfolder mounts the same files a second time, under a
        // second name - which is how 'spikes' ended up mounted both as C:\airlock\Airlock\spikes
        // and as C:\airlock\spikes.
        var found = Config().FindContaining(@"S:\github\Airlock\src\Airlock");

        Assert.NotNull(found);
        Assert.Equal("Airlock", found.Value.Airlock.Name);
        Assert.Equal(@"src\Airlock", found.Value.Relative);
    }

    [Fact]
    public void TheAirlockItself_HasNothingBelowIt()
    {
        var found = Config().FindContaining(@"S:\github\Airlock");

        Assert.NotNull(found);
        Assert.Equal(string.Empty, found.Value.Relative);
    }

    [Fact]
    public void TheNearestAirlockWins()
    {
        // spikes sits inside Airlock, so a path under spikes belongs to spikes.
        var found = Config().FindContaining(@"S:\github\Airlock\spikes\logs");

        Assert.NotNull(found);
        Assert.Equal("spikes", found.Value.Airlock.Name);
        Assert.Equal("logs", found.Value.Relative);
    }

    [Fact]
    public void AFolderOutsideEveryAirlock_IsNotCovered()
    {
        Assert.Null(Config().FindContaining(@"S:\github\Other"));
    }

    [Fact]
    public void ASiblingWithASharedPrefix_IsNotMistakenForAChild()
    {
        // "S:\github\Airlock2" starts with "S:\github\Airlock" as a string but is not inside it.
        Assert.Null(Config().FindContaining(@"S:\github\Airlock2"));
    }

    [Fact]
    public void NamesAreUniqueAcrossAirlocks()
    {
        // The name is what identifies one on the command line, so two sharing it would be ambiguous.
        var config = Config();

        Assert.Equal(
            config.Airlocks.Count,
            config.AirlockNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ASecondCheckoutWithTheSameLeafName_GetsADistinctName()
    {
        var config = Config();

        var allocated = config.AllocateName("spikes");

        Assert.NotEqual("spikes", allocated);
        Assert.DoesNotContain(allocated, config.AirlockNames, StringComparer.OrdinalIgnoreCase);
    }
}
