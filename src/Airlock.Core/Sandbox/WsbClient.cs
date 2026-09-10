using System.Text.Json;
using System.Text.RegularExpressions;
using Medallion.Shell;

namespace Airlock.Sandbox;

/// <summary>
/// Drives <c>wsb.exe</c>, the Windows Sandbox CLI.
/// </summary>
/// <remarks>
/// Every call passes <c>--raw</c> and parses JSON. The shapes are undocumented, so each parse
/// falls back to scraping a GUID or an IPv4 address out of the text — a wsb update that changes
/// the JSON should degrade rather than break.
/// </remarks>
public sealed partial class WsbClient : IWsbClient
{
    private const string Executable = "wsb.exe";

    public async Task<string> StartAsync(
        string requestedId,
        string inlineXml,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inlineXml);

        // --config takes the XML itself. A file path is rejected as "configuration file was invalid".
        var result = await RunAsync(
            cancellationToken,
            "start", "--id", requestedId, "--config", inlineXml, "--raw").ConfigureAwait(false);

        Throw(result, "start", $"Failed to start the sandbox.");

        var id = Parse<WsbStartResponse>(result.StandardOutput)?.Id
                 ?? GuidPattern().Match(result.StandardOutput).Value;

        return string.IsNullOrEmpty(id)
            ? throw new WsbException(
                "wsb start reported success but returned no sandbox id.",
                "start", result.ExitCode, result.StandardOutput, result.StandardError)
            : id;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(cancellationToken, "list", "--raw").ConfigureAwait(false);
        Throw(result, "list", "Failed to list running sandboxes.");

        var parsed = Parse<WsbListResponse>(result.StandardOutput);
        if (parsed is not null)
        {
            return parsed.WindowsSandboxEnvironments
                .Select(e => e.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
        }

        return GuidPattern().Matches(result.StandardOutput).Select(m => m.Value).ToList();
    }

    public async Task<string?> GetIpAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var result = await RunAsync(cancellationToken, "ip", "--id", id, "--raw").ConfigureAwait(false);
        if (!result.Success)
        {
            // No IP yet is an ordinary state while the sandbox is still coming up.
            return null;
        }

        var ip = Parse<WsbIpResponse>(result.StandardOutput)?
            .Networks.Select(n => n.IpV4Address)
            .FirstOrDefault(a => !string.IsNullOrEmpty(a));

        if (!string.IsNullOrEmpty(ip))
        {
            return ip;
        }

        var match = IpV4Pattern().Match(result.StandardOutput);
        return match.Success ? match.Value : null;
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Teardown is best-effort by design: it runs from finally blocks and shutdown handlers,
        // where throwing would mask the original failure or blow the ~5s close-event budget.
        await RunAsync(cancellationToken, "stop", "--id", id).ConfigureAwait(false);
    }

    public async Task OpenDesktopAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Start rather than Run: `wsb connect` owns a window for as long as the user keeps it open,
        // so it is launched detached and unredirected. Holding its pipes keeps it attached to us
        // and delayed the call by ~30s, and a GUI launcher has nothing useful to say on stdout.
        var command = Start(Executable, "connect", "--id", id);

        try
        {
            // A grace period only to catch a failure that is immediate; still running afterwards is
            // exactly what success looks like here.
            await command.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }

        if (!command.Result.Success)
        {
            throw new WsbException(
                "Could not open the sandbox window.",
                "connect",
                command.Result.ExitCode,
                string.Empty,
                string.Empty);
        }
    }

    public async Task<bool> ExecAsync(
        string id,
        string command,
        WsbRunAs runAs = WsbRunAs.System,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var args = new List<string> { "exec", "--id", id, "-r", runAs.ToString(), "-c", command };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            args.Add("-d");
            args.Add(workingDirectory);
        }

        var result = await RunAsync(cancellationToken, [.. args]).ConfigureAwait(false);

        // Deliberately only the dispatch result: wsb exec does not surface the remote exit code.
        return result.Success;
    }

    public async Task ShareAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var args = new List<string> { "share", "--id", id, "-f", hostPath, "-s", sandboxPath };
        if (allowWrite)
        {
            args.Add("-w");
        }

        var result = await RunAsync(cancellationToken, [.. args]).ConfigureAwait(false);
        Throw(result, "share", $"Failed to map '{hostPath}' into the running sandbox.");
    }

    private static Task<CommandResult> RunAsync(CancellationToken cancellationToken, params string[] args) =>
        Run(
            opt => opt.CancellationToken(cancellationToken),
            Executable,
            args.Cast<object>().ToArray()).AsResult();

    private static T? Parse<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, WsbJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Throw(CommandResult result, string verb, string message)
    {
        if (!result.Success)
        {
            throw new WsbException(message, verb, result.ExitCode, result.StandardOutput, result.StandardError);
        }
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex IpV4Pattern();
}
