using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Airlock.Sandbox;

/// <summary>
/// Renders a <see cref="SandboxConfig"/> to the XML Windows Sandbox expects.
/// </summary>
/// <remarks>
/// <para>
/// <c>wsb start --config</c> takes the configuration as <b>inline XML</b>. A file path is rejected
/// outright with "The configuration file was invalid.", so <see cref="ToInlineXml"/> — a single
/// line, passed as one argv element — is the form that actually launches a sandbox.
/// <see cref="ToPrettyXml"/> exists only for <c>--dry-run</c> output and failure reports.
/// </para>
/// <para>
/// Element names and casing follow the documented schema exactly. Sandbox ignores anything it does
/// not recognise without complaint, so a typo here is a silently dropped setting, not an error.
/// </para>
/// </remarks>
public static class SandboxConfigWriter
{
    /// <summary>The documented element order. Some builds are order-sensitive.</summary>
    public static XDocument ToXDocument(SandboxConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var root = new XElement("Configuration");

        Add(root, "vGPU", config.VGpu);
        Add(root, "Networking", config.Networking);

        if (config.MappedFolders.Count > 0)
        {
            AssertNoOverlap(config.MappedFolders);

            var folders = new XElement("MappedFolders");
            foreach (var f in config.MappedFolders)
            {
                folders.Add(new XElement(
                    "MappedFolder",
                    new XElement("HostFolder", f.HostFolder),
                    new XElement("SandboxFolder", f.SandboxFolder),
                    new XElement("ReadOnly", f.ReadOnly ? "true" : "false")));
            }

            root.Add(folders);
        }

        if (config.LogonCommand is { Length: > 0 } cmd)
        {
            root.Add(new XElement("LogonCommand", new XElement("Command", cmd)));
        }

        Add(root, "AudioInput", config.AudioInput);
        Add(root, "VideoInput", config.VideoInput);
        Add(root, "ProtectedClient", config.ProtectedClient);
        Add(root, "PrinterRedirection", config.PrinterRedirection);
        Add(root, "ClipboardRedirection", config.ClipboardRedirection);

        if (config.MemoryInMB is int mb)
        {
            root.Add(new XElement("MemoryInMB", mb.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new XDocument(root);
    }

    /// <summary>The single-line form to hand to <c>wsb start --config</c>.</summary>
    public static string ToInlineXml(SandboxConfig config) =>
        ToXDocument(config).ToString(SaveOptions.DisableFormatting);

    /// <summary>Indented form, for <c>--dry-run</c> and failure diagnostics only.</summary>
    public static string ToPrettyXml(SandboxConfig config)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
        };

        using (var writer = XmlWriter.Create(sb, settings))
        {
            ToXDocument(config).Save(writer);
        }

        return sb.ToString();
    }

    private static void Add(XElement root, string name, SandboxToggle value)
    {
        // "Default" means "say nothing and let Sandbox decide", so it emits no element.
        if (value is SandboxToggle.Default)
        {
            return;
        }

        root.Add(new XElement(name, value.ToString()));
    }

    /// <summary>
    /// Two mappings whose sandbox paths nest would let one silently shadow the other — most
    /// dangerously, a read-only mapping hiding under a read-write one.
    /// </summary>
    private static void AssertNoOverlap(IReadOnlyList<MappedFolder> folders)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            for (var j = i + 1; j < folders.Count; j++)
            {
                var a = Normalize(folders[i].SandboxFolder);
                var b = Normalize(folders[j].SandboxFolder);

                if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || IsUnder(a, b) || IsUnder(b, a))
                {
                    throw new ArgumentException(
                        $"Mapped sandbox paths overlap: '{folders[i].SandboxFolder}' and '{folders[j].SandboxFolder}'.",
                        nameof(folders));
                }
            }
        }

        static string Normalize(string p) => p.TrimEnd('\\', '/');

        static bool IsUnder(string parent, string child) =>
            child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
