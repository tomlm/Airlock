using System.Reflection;
using System.Text;
using System.Text.Json;
using Airlock.Configuration;

namespace Airlock.Session;

/// <summary>
/// Produces the provisioning script that runs inside the sandbox as SYSTEM.
/// </summary>
/// <remarks>
/// The script is an embedded resource rather than a generated string so it stays readable, testable
/// and diffable; only a handful of values are substituted.
/// </remarks>
public static class SetupScript
{
    public const string SandboxUser = "airlock";

    public const string DefaultShellPath =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>Environment variable names sshd will accept from the client.</summary>
    /// <remarks>
    /// This is how secrets reach the agent: the value is set into ssh.exe's own environment and
    /// forwarded over the encrypted channel, so it appears in no file and on no command line.
    /// </remarks>
    public static readonly string[] AcceptedEnvNames =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_MODEL",
        "TERM",
        "COLORTERM",
    ];

    /// <summary>Writes <c>setup.ps1</c> and the non-secret env file into the mapped share folder.</summary>
    public static void Write(SandboxLayout layout, IReadOnlyList<string> pathPrepend, IDictionary<string, string> machineEnv)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pathPrepend);
        ArgumentNullException.ThrowIfNull(machineEnv);

        SecretGuard.AssertNoSecrets(machineEnv);

        var script = ReadTemplate()
            .Replace("{{SANDBOX_USER}}", SandboxUser, StringComparison.Ordinal)
            .Replace("{{ACCEPT_ENV}}", string.Join(' ', AcceptedEnvNames), StringComparison.Ordinal)
            .Replace("{{PATH_PREPEND}}", string.Join(';', pathPrepend), StringComparison.Ordinal)
            .Replace("{{DEFAULT_SHELL}}", DefaultShellPath, StringComparison.Ordinal);

        if (script.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("setup.ps1 still contains an unsubstituted token.");
        }

        File.WriteAllText(layout.SetupScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // This file is mapped into the sandbox and is readable by the agent: non-secrets only.
        File.WriteAllText(
            layout.EnvFilePath,
            JsonSerializer.Serialize(machineEnv, SetupJson.Options),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

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
