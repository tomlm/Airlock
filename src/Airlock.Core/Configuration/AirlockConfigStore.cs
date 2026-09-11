using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airlock.Configuration;

/// <summary>
/// Reads and writes <c>%APPDATA%\Airlock\airlock.json</c>.
/// </summary>
/// <remarks>
/// The file is meant to be edited by hand as well as by the CLI, so it is written indented, tolerates
/// comments and trailing commas, and is matched case-insensitively.
/// </remarks>
public sealed class AirlockConfigStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Airlock",
        "airlock.json");

    /// <summary>
    /// Loads the config, creating it with whatever toolchains are on this machine the first time.
    /// </summary>
    /// <remarks>
    /// Seeding on first run is what makes <c>airlock start</c> work immediately after install,
    /// rather than requiring a handful of <c>tools add</c> calls before anything is usable.
    /// </remarks>
    public AirlockConfig LoadOrCreate()
    {
        if (Load() is { } existing)
        {
            return existing;
        }

        var config = new AirlockConfig { Tools = [.. ToolDetector.DetectAll()] };
        Save(config);

        return config;
    }

    public AirlockConfig? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AirlockConfig>(File.ReadAllText(Path), Options);
        }
        catch (JsonException ex)
        {
            // A hand-edited file with a typo in it should say so, not be silently replaced - the
            // list of writable folders is not something to quietly reset.
            throw new InvalidOperationException(
                $"'{Path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Save(AirlockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        SecretGuard.AssertNoSecretValues(config);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Options));
    }

    /// <summary>
    /// Re-probes the machine, so a moved or upgraded toolchain repairs itself and a newly installed
    /// one is picked up.
    /// </summary>
    /// <remarks>
    /// Adding as well as updating is what lets an existing config benefit from a detector written
    /// after it was created - otherwise detection would only ever help a fresh install, and everyone
    /// already using Airlock would have to add the tool by hand. A detected tool is skipped when its
    /// folder is already mounted under some other id, so a hand-added entry pointing at the same
    /// place keeps its name rather than being shadowed by a second mount of the same files.
    /// </remarks>
    /// <returns>True when anything changed and the config was rewritten.</returns>
    public bool RefreshDetected(AirlockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var changed = false;

        foreach (var fresh in ToolDetector.DetectAll())
        {
            var existing = config.Tools.FindIndex(
                t => t.Id.Equals(fresh.Id, StringComparison.OrdinalIgnoreCase));

            if (existing < 0)
            {
                if (!config.Tools.Any(t => SamePath(t.Host, fresh.Host)))
                {
                    config.Tools.Add(fresh);
                    changed = true;
                }

                continue;
            }

            // Only entries Airlock found are re-probed; a hand-added one is taken literally, even
            // when it shares an id with something detectable.
            if (config.Tools[existing].Detected && !SamePath(config.Tools[existing].Host, fresh.Host))
            {
                config.Tools[existing] = fresh;
                changed = true;
            }
        }

        if (changed)
        {
            Save(config);
        }

        return changed;
    }

    /// <summary>Two spellings of the same folder - a trailing separator is the usual difference.</summary>
    private static bool SamePath(string a, string b) =>
        a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Keeps credential values out of anything Airlock writes down.
/// </summary>
/// <remarks>
/// <para>
/// Secrets are named in the config and read from the host environment at start; the value itself
/// should never be persisted. This turns that intention into something the code enforces rather than
/// something a future edit can quietly undo.
/// </para>
/// <para>
/// Note this is about the value, not the name: <c>secrets: ["ANTHROPIC_API_KEY"]</c> is the point of
/// the feature. What must not appear is a mount or an environment entry carrying the actual key.
/// </para>
/// </remarks>
public static class SecretGuard
{
    private static readonly string[] Suffixes =
        ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "CREDENTIALS", "PAT"];

    public static bool LooksLikeSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var upper = name.ToUpperInvariant();

        return Suffixes.Any(s => upper.EndsWith(s, StringComparison.Ordinal))
            || Suffixes.Any(s => upper.Contains('_' + s, StringComparison.Ordinal));
    }

    /// <summary>Rejects a config that carries a credential value in a tool's environment block.</summary>
    public static void AssertNoSecretValues(AirlockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        foreach (var tool in config.Tools)
        {
            var offender = tool.EnvEntries.Keys.FirstOrDefault(LooksLikeSecret);

            if (offender is not null)
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.Id}' sets '{offender}', which looks like a credential. Name it " +
                    "under \"secrets\" instead, so the value is read from your environment at start " +
                    "rather than written into this file.");
            }
        }
    }

    /// <summary>
    /// Rejects secrets in the read-only session folder, which is mapped into the sandbox for the
    /// whole of its life.
    /// </summary>
    /// <remarks>
    /// Credentials reach the guest through the read-write handoff folder instead, which both sides
    /// delete as soon as it has been read.
    /// </remarks>
    public static void AssertNoSecrets(IDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var offender = values.Keys.FirstOrDefault(LooksLikeSecret);

        if (offender is not null)
        {
            throw new InvalidOperationException(
                $"'{offender}' looks like a secret and would be written to the read-only session " +
                "folder, which stays mapped for the sandbox's life. Pass it through the handoff " +
                "folder instead.");
        }
    }
}
