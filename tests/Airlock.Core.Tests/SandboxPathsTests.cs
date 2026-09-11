using Airlock.Sandbox;

namespace Airlock.Tests;

/// <summary>
/// The layout gives airlocks a folder of their own, which is what makes the names safe: earlier
/// versions put them beside Airlock's own mounts and needed a naming convention to tell the two
/// apart. These pin down the separation rather than trusting it.
/// </summary>
public class SandboxPathsTests
{
    [Fact]
    public void EachTool_GetsItsOwnFolder()
    {
        // Per-tool folders keep PATH entries predictable and stop one removal disturbing another.
        Assert.Equal(@"C:\tools\dotnet", SandboxPaths.ForTool("dotnet"));
        Assert.NotEqual(SandboxPaths.ForTool("git"), SandboxPaths.ForTool("node"));
    }

    [Fact]
    public void Airlocks_LandInTheirOwnFolder()
    {
        Assert.Equal(@"C:\airlocks\foo", SandboxPaths.ForProject("foo"));
    }

    [Fact]
    public void TheDrive_IsTheSameFolderSpeltShort()
    {
        // A: is substituted onto the airlocks folder during provisioning, so these are one place.
        Assert.Equal(@"A:\foo", SandboxPaths.ForProjectOnDrive("foo"));
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("session")]
    [InlineData("out")]
    [InlineData("setup")]
    [InlineData("airlocks")]
    public void AnAirlockMayTakeTheNameOfOneOfAirlocksOwnFolders(string name)
    {
        // The whole point of the airlocks folder: no name is reserved, because there is nothing
        // left to collide with. Checking out a repo called `tools` used to need special handling.
        Assert.DoesNotContain(SandboxPaths.ForProject(name), SandboxPaths.OwnFolders);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Users")]
    [InlineData("ProgramData")]
    [InlineData("Program Files")]
    public void NoOwnFolder_ShadowsAFolderWindowsAlreadyHas(string windows)
    {
        // Airlock's folders sit at the root of the guest's C: drive, which is only safe while none
        // of them is a name Windows has already taken.
        Assert.DoesNotContain(
            @"C:\" + windows,
            SandboxPaths.OwnFolders,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryOwnFolder_IsTopLevelOnC()
    {
        foreach (var path in SandboxPaths.OwnFolders.Append(SandboxPaths.ForTool("dotnet")))
        {
            Assert.StartsWith(@"C:\", path, StringComparison.Ordinal);
        }

        // One separator each: the folders themselves are at the root, not nested under anything.
        Assert.All(SandboxPaths.OwnFolders, p => Assert.Equal(1, p.Count(c => c == '\\')));
    }

    [Fact]
    public void OwnFolders_DoNotCollideWithEachOther()
    {
        Assert.Equal(
            SandboxPaths.OwnFolders.Count,
            SandboxPaths.OwnFolders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NoOwnFolder_SitsInsideTheAirlocksFolder()
    {
        // Anything of Airlock's under here would be mounted over by an airlock of the same name,
        // and would show up on A: as though it were a workspace.
        foreach (var own in SandboxPaths.OwnFolders.Where(f => f != SandboxPaths.Airlocks))
        {
            Assert.False(
                own.StartsWith(SandboxPaths.Airlocks + "\\", StringComparison.OrdinalIgnoreCase),
                $"'{own}' should be a sibling of the airlocks folder, not inside it");
        }
    }
}
