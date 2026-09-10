using Airlock.Sandbox;

namespace Airlock.Tests;

/// <summary>
/// Projects and Airlock's own mounts now share one root, so the underscore convention is the only
/// thing keeping them apart. These pin it down rather than trusting it.
/// </summary>
public class SandboxPathsTests
{
    [Fact]
    public void Projects_LandDirectlyUnderTheRoot()
    {
        // The point of the layout: the path inside reads like the one outside.
        Assert.Equal(@"C:\airlock\foo", SandboxPaths.ForProject("foo"));
    }

    [Theory]
    [InlineData(SandboxPaths.Tools)]
    [InlineData(SandboxPaths.Session)]
    [InlineData(SandboxPaths.Dotnet)]
    [InlineData(SandboxPaths.Out)]
    [InlineData(SandboxPaths.Setup)]
    public void AirlocksOwnFolders_AreWrappedInUnderscores(string path)
    {
        var leaf = path[(SandboxPaths.Root.Length + 1)..];

        Assert.True(SandboxPaths.IsReservedName(leaf), $"'{leaf}' should be underscore-wrapped");
    }

    [Theory]
    [InlineData("_tools_")]
    [InlineData("_session_")]
    [InlineData("_anything_")]
    public void UnderscoreWrappedNames_AreReserved(string name) =>
        Assert.True(SandboxPaths.IsReservedName(name));

    [Theory]
    [InlineData("foo")]
    [InlineData("_foo")]
    [InlineData("foo_")]
    [InlineData("my_project")]
    [InlineData("_")]
    [InlineData("")]
    public void OrdinaryNames_AreNotReserved(string name) =>
        Assert.False(SandboxPaths.IsReservedName(name));

    [Fact]
    public void EveryOwnFolder_LivesUnderTheRoot()
    {
        foreach (var path in new[]
                 {
                     SandboxPaths.Tools, SandboxPaths.Session, SandboxPaths.Dotnet,
                     SandboxPaths.Out, SandboxPaths.Setup, SandboxPaths.Claude, SandboxPaths.OpenSsh,
                 })
        {
            Assert.StartsWith(SandboxPaths.Root + "\\", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OwnFolders_DoNotCollideWithEachOther()
    {
        string[] all =
        [
            SandboxPaths.Tools, SandboxPaths.Session, SandboxPaths.Dotnet,
            SandboxPaths.Out, SandboxPaths.Setup,
        ];

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
