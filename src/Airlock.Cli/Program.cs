using System.Reflection;
using Airlock.Cli;
using Airlock.Sandbox;
using Spectre.Console;

namespace Airlock.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (WsbException ex)
        {
            WriteWsbFailure(ex);
            return (int)ExitCode.Sandbox;
        }
        catch (OperationCanceledException)
        {
            return (int)ExitCode.Interrupted;
        }
#pragma warning disable CA1031 // Top-level handler: an unhandled exception must not print a raw stack trace.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AnsiConsole.MarkupLineInterpolated($"[red]airlock:[/] {ex.Message}");
            return (int)ExitCode.Internal;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var command = CommandLine.Parse(args);

        switch (command.Kind)
        {
            // CShell's TryParse has already written the usage text or the error itself, so these
            // only choose an exit code. Printing again would duplicate the whole block.
            case InvocationKind.Usage:
                return (int)ExitCode.Usage;

            case InvocationKind.Help:
                if (!command.AlreadyReported)
                {
                    Console.WriteLine(command.UsageText);
                }

                return (int)ExitCode.Ok;

            case InvocationKind.Version:
                Console.WriteLine(Version);
                return (int)ExitCode.Ok;

            case InvocationKind.Verb:
                return await DispatchVerbAsync(command).ConfigureAwait(false);

            case InvocationKind.Passthrough:
                return await NotYetAsync(
                    $"run [bold]{string.Join(' ', command.Arguments)}[/] in a sandbox").ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unhandled invocation kind '{command.Kind}'.");
        }
    }

    private static async Task<int> DispatchVerbAsync(CommandLine command) =>
        command.Verb switch
        {
            ReservedVerbs.List => await ListAsync().ConfigureAwait(false),
            _ => await NotYetAsync($"the [bold]{command.Verb}[/] verb").ConfigureAwait(false),
        };

    private static async Task<int> ListAsync()
    {
        var wsb = new WsbClient();
        var ids = await wsb.ListAsync().ConfigureAwait(false);

        if (ids.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No sandbox is running.[/]");
            return (int)ExitCode.Ok;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Sandbox");
        table.AddColumn("Owner");

        foreach (var id in ids)
        {
            // Until the session store lands, anything running is "not ours" as far as we know.
            table.AddRow(id, "[yellow]untracked[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[dim]Windows Sandbox allows only one instance, so Airlock cannot start a session " +
            "while another sandbox is running.[/]");

        return (int)ExitCode.Ok;
    }

    private static Task<int> NotYetAsync(string what)
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]airlock:[/] not implemented yet - {what}.");
        return Task.FromResult((int)ExitCode.Internal);
    }

    private static void WriteWsbFailure(WsbException ex)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]airlock:[/] {ex.Message}");

        if (ex.IsAlreadyRunning)
        {
            AnsiConsole.MarkupLine(
                "A Windows Sandbox is already running, and Windows allows only one at a time. " +
                "Close it (or run '[bold]airlock stop[/]' if Airlock started it) and try again.");
        }
        else if (ex.IsStaleBaseImage)
        {
            AnsiConsole.MarkupLine("This usually means a stale Sandbox base image. From an [bold]elevated[/] prompt:");
            AnsiConsole.MarkupLine("""
                [dim]  Stop-Service CmService
                  Rename-Item 'C:\ProgramData\Microsoft\Windows\Containers' Containers.old
                  Start-Service CmService[/]
                """);
        }
        else if (!string.IsNullOrWhiteSpace(ex.StandardError))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]{ex.StandardError.Trim()}[/]");
        }
    }

    private static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}

/// <summary>
/// Process exit codes. A passthrough session instead returns the wrapped command's own exit code,
/// so these deliberately avoid the low numbers a wrapped program is likely to use.
/// </summary>
internal enum ExitCode
{
    Ok = 0,
    Usage = 2,
    Preflight = 3,
    Sandbox = 4,
    Provisioning = 5,
    Ssh = 6,
    Tools = 7,
    Internal = 70,
    Interrupted = 130,
}
