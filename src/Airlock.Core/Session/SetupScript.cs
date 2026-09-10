using System.Reflection;
using System.Text;
using System.Text.Json;
using Airlock.Configuration;

namespace Airlock.Session;

/// <summary>
/// Produces the provisioning script that runs inside the sandbox as SYSTEM, and the files it reads.
/// </summary>
/// <remarks>
/// The script is an embedded resource rather than a generated string so it stays readable, testable
/// and diffable; only a handful of values are substituted.
/// </remarks>
public static class SetupScript
{
    /// <summary>Writes <c>setup.ps1</c> and the non-secret env file into the read-only share.</summary>
    public static void Write(
        SandboxLayout layout,
        IReadOnlyList<string> pathPrepend,
        IDictionary<string, string> machineEnv,
        NetworkConfig network)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pathPrepend);
        ArgumentNullException.ThrowIfNull(machineEnv);
        ArgumentNullException.ThrowIfNull(network);

        // This folder is read-only inside the sandbox but lives for its whole life, so a credential
        // here would sit there the entire time. They go through the handoff file instead.
        SecretGuard.AssertNoSecrets(machineEnv);

        var script = ReadTemplate()
            .Replace("{{PATH_PREPEND}}", string.Join(';', pathPrepend), StringComparison.Ordinal)
            .Replace("{{BLOCK_LAN}}", network.BlockLan ? "true" : "false", StringComparison.Ordinal)
            .Replace("{{ALLOW_LIST}}", FormatAllowList(network.Allow), StringComparison.Ordinal);

        if (script.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("setup.ps1 still contains an unsubstituted token.");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        File.WriteAllText(layout.SetupScriptPath, script, utf8);
        File.WriteAllText(layout.EnvFilePath, JsonSerializer.Serialize(machineEnv, SetupJson.Options), utf8);
    }

    /// <summary>
    /// Writes the credentials the guest should pick up, into the writable handoff folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values are read from the host environment here and stored nowhere else. The file is deleted
    /// by <c>setup.ps1</c> as soon as it has been read, and again by the host once provisioning
    /// reports back - belt and braces, because a script that died between the two would otherwise
    /// leave a key on disk.
    /// </para>
    /// <para>
    /// The alternative was a <c>wsb exec</c> command line, which would put the value in the host's
    /// process list for the duration of the call. A file both sides delete is the smaller exposure.
    /// </para>
    /// </remarks>
    /// <returns>The names actually found on the host, for reporting.</returns>
    public static IReadOnlyList<string> WriteSecrets(SandboxLayout layout, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                found[name] = value;
            }
        }

        if (found.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(layout.OutDirectory);
        File.WriteAllText(
            layout.SecretsPath,
            JsonSerializer.Serialize(found, SetupJson.Options),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return [.. found.Keys];
    }

    /// <summary>Removes the handoff file, whether or not the guest managed to.</summary>
    public static void DeleteSecrets(SandboxLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            if (File.Exists(layout.SecretsPath))
            {
                File.Delete(layout.SecretsPath);
            }
        }
        catch (IOException)
        {
            // Better to carry on than to fail a working session over a file the guest already read.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A PowerShell array literal, or nothing when there is nothing to allow.</summary>
    private static string FormatAllowList(List<string> allow) =>
        allow.Count == 0
            ? string.Empty
            : string.Join(", ", allow.Select(a => "'" + a.Replace("'", "''", StringComparison.Ordinal) + "'"));

    private static string ReadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("setup.ps1", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded setup.ps1 is missing from Airlock.Core.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}

internal static class SetupJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
