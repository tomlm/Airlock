using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airlock.Sandbox;

/// <summary>
/// Shapes returned by <c>wsb --raw</c>, captured from the real CLI (app 0.8.107.0).
/// </summary>
/// <remarks>
/// <c>--raw</c> is a global flag on every verb. Parsing it beats scraping the human-readable
/// output, but the shapes are undocumented, so <see cref="WsbClient"/> falls back to a regex
/// scrape when a payload does not parse.
/// </remarks>
internal static class WsbJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary><c>wsb start --raw</c> → <c>{ "Id": "&lt;guid&gt;" }</c>.</summary>
internal sealed class WsbStartResponse
{
    public string? Id { get; set; }
}

/// <summary><c>wsb list --raw</c> → <c>{ "WindowsSandboxEnvironments": [ { "Id": "…" } ] }</c>.</summary>
internal sealed class WsbListResponse
{
    public List<WsbEnvironment> WindowsSandboxEnvironments { get; set; } = [];
}

internal sealed class WsbEnvironment
{
    public string? Id { get; set; }
}

/// <summary><c>wsb ip --raw</c> → <c>{ "Networks": [ { "IpV4Address": "172.22.157.230" } ] }</c>.</summary>
internal sealed class WsbIpResponse
{
    public List<WsbNetwork> Networks { get; set; } = [];
}

internal sealed class WsbNetwork
{
    [JsonPropertyName("IpV4Address")]
    public string? IpV4Address { get; set; }
}
