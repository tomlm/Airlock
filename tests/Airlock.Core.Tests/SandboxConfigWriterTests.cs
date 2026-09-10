using System.Xml.Linq;
using Airlock.Sandbox;

namespace Airlock.Tests;

/// <summary>
/// Windows Sandbox ignores elements it does not recognise <b>without any error</b>: a miscased
/// <c>&lt;VGpu&gt;</c> starts happily and silently leaves vGPU enabled. "It launched" is therefore
/// no evidence that a setting applied, so the exact documented names are asserted here.
/// </summary>
public class SandboxConfigWriterTests
{
    private static readonly MappedFolder Project =
        new("S:\\src\\Foo", "C:\\work\\Foo", ReadOnly: false);

    private static readonly MappedFolder Tools =
        new("C:\\Users\\me\\AppData\\Local\\Airlock\\tools", "C:\\airlock\\tools", ReadOnly: true);

    [Theory]
    [InlineData("vGPU")]
    [InlineData("Networking")]
    [InlineData("MappedFolders")]
    [InlineData("MappedFolder")]
    [InlineData("HostFolder")]
    [InlineData("SandboxFolder")]
    [InlineData("ReadOnly")]
    [InlineData("AudioInput")]
    [InlineData("VideoInput")]
    [InlineData("PrinterRedirection")]
    [InlineData("ClipboardRedirection")]
    [InlineData("MemoryInMB")]
    public void ElementNames_MatchTheDocumentedCasingExactly(string elementName)
    {
        var xml = SandboxConfigWriter.ToXDocument(Config());

        var names = xml.Descendants().Select(e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(elementName, names);
    }

    [Fact]
    public void RootElement_IsConfiguration()
    {
        var xml = SandboxConfigWriter.ToXDocument(Config());

        Assert.Equal("Configuration", xml.Root!.Name.LocalName);
    }

    [Fact]
    public void VGpu_IsDisabledByDefault()
    {
        // Not cosmetic: vGPU produces a black screen on some adapters, and Airlock is headless.
        var xml = SandboxConfigWriter.ToXDocument(new SandboxConfig());

        Assert.Equal("Disable", xml.Root!.Element("vGPU")!.Value);
    }

    [Fact]
    public void ClipboardRedirection_IsDisabledByDefault()
    {
        // Left on, the agent can read the host clipboard, which quietly undoes the isolation.
        var xml = SandboxConfigWriter.ToXDocument(new SandboxConfig());

        Assert.Equal("Disable", xml.Root!.Element("ClipboardRedirection")!.Value);
    }

    [Fact]
    public void Networking_IsEnabledByDefault()
    {
        // The agent needs its API, and the session itself arrives over SSH.
        var xml = SandboxConfigWriter.ToXDocument(new SandboxConfig());

        Assert.Equal("Enable", xml.Root!.Element("Networking")!.Value);
    }

    [Fact]
    public void NoLogonCommand_IsEmitted()
    {
        // Provisioning runs as SYSTEM through wsb exec; a LogonCommand would need an interactive
        // session, which a headless sandbox does not have.
        var xml = SandboxConfigWriter.ToXDocument(Config());

        Assert.Null(xml.Root!.Element("LogonCommand"));
    }

    [Fact]
    public void DefaultToggle_EmitsNoElementAtAll()
    {
        var xml = SandboxConfigWriter.ToXDocument(new SandboxConfig
        {
            ProtectedClient = SandboxToggle.Default,
        });

        Assert.Null(xml.Root!.Element("ProtectedClient"));
    }

    [Fact]
    public void ReadOnly_IsWrittenAsLowercaseBoolean()
    {
        var xml = SandboxConfigWriter.ToXDocument(Config());

        var values = xml.Descendants("ReadOnly").Select(e => e.Value).ToList();

        Assert.Contains("false", values, StringComparer.Ordinal);
        Assert.Contains("true", values, StringComparer.Ordinal);
    }

    [Fact]
    public void ExactlyOneMapping_IsWritable_AndItIsTheProject()
    {
        // The core promise of the tool, asserted on the artifact that actually enforces it.
        var xml = SandboxConfigWriter.ToXDocument(Config());

        var writable = xml.Descendants("MappedFolder")
            .Where(m => m.Element("ReadOnly")!.Value == "false")
            .ToList();

        Assert.Single(writable);
        Assert.Equal("S:\\src\\Foo", writable[0].Element("HostFolder")!.Value);
    }

    [Fact]
    public void InlineXml_IsASingleLine()
    {
        // wsb start --config takes the XML itself as one argv element; a file path is rejected.
        var inline = SandboxConfigWriter.ToInlineXml(Config());

        Assert.DoesNotContain('\n', inline);
        Assert.DoesNotContain('\r', inline);
        Assert.StartsWith("<Configuration>", inline, StringComparison.Ordinal);
    }

    [Fact]
    public void PrettyXml_IsIndentedForDiagnostics()
    {
        var pretty = SandboxConfigWriter.ToPrettyXml(Config());

        Assert.Contains('\n', pretty);
        Assert.DoesNotContain("<?xml", pretty, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingSandboxPaths_AreRejected()
    {
        // A nested mapping would silently shadow another - worst of all, a read-only one hiding
        // under a read-write one.
        var config = new SandboxConfig
        {
            MappedFolders =
            [
                new MappedFolder("S:\\a", "C:\\work", ReadOnly: false),
                new MappedFolder("S:\\b", "C:\\work\\nested", ReadOnly: true),
            ],
        };

        Assert.Throws<ArgumentException>(() => SandboxConfigWriter.ToXDocument(config));
    }

    [Fact]
    public void DuplicateSandboxPaths_AreRejected()
    {
        var config = new SandboxConfig
        {
            MappedFolders =
            [
                new MappedFolder("S:\\a", "C:\\airlock\\tools", ReadOnly: true),
                new MappedFolder("S:\\b", "C:\\airlock\\tools\\", ReadOnly: true),
            ],
        };

        Assert.Throws<ArgumentException>(() => SandboxConfigWriter.ToXDocument(config));
    }

    [Fact]
    public void MemoryInMB_IsOmittedWhenNotSet()
    {
        var xml = SandboxConfigWriter.ToXDocument(new SandboxConfig());

        Assert.Null(xml.Root!.Element("MemoryInMB"));
    }

    private static SandboxConfig Config() => new()
    {
        MemoryInMB = 8192,
        MappedFolders = [Project, Tools],
    };
}
