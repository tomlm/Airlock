using System.Reflection;
using Airlock.Cli;
using Airlock.Paths;
using Airlock.Sandbox;
using Airlock.Session;
using Spectre.Console;

namespace Airlock;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        try
        {
            return await RunAsync(args, cts.Token).ConfigureAwait(false);
        }
        catch (ProjectValidationException ex)
        {
            Fail(ex.Message);
            return (int)ExitCode.Preflight;
        }
        catch (SessionException ex)
        {
            Fail(ex.Message);
            return (int)ExitCode.Provisioning;
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
#pragma warning disable CA1031 // Top-level handler: never show the user a raw stack trace.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fail(ex.Message);
            return (int)ExitCode.Internal;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var command = CommandLine.Parse(args);

        switch (command.Kind)
        {
            // CShell's parser has already printed the usage text or the error, so these only pick
            // an exit code; printing again would duplicate the whole block.
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
                return await DispatchVerbAsync(command, cancellationToken).ConfigureAwait(false);

            case InvocationKind.Passthrough:
                return await StartSessionAsync(command, command.Arguments, cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unhandled invocation kind '{command.Kind}'.");
        }
    }

    private static async Task<int> DispatchVerbAsync(CommandLine command, CancellationToken cancellationToken) =>
        command.Verb switch
        {
            ReservedVerbs.List => await ListAsync(cancellationToken).ConfigureAwait(false),
            ReservedVerbs.Stop => await StopAsync(cancellationToken).ConfigureAwait(false),
            ReservedVerbs.Run => await StartSessionAsync(command, command.Arguments, cancellationToken)
                .ConfigureAwait(false),
            ReservedVerbs.Shell => await StartSessionAsync(command, [], cancellationToken).ConfigureAwait(false),
            _ => NotYet(command.Verb!),
        };

    private static async Task<int> StartSessionAsync(
        CommandLine command,
        IReadOnlyList<string> agentCommand,
        CancellationToken cancellationToken)
    {
        var project = ProjectResolver.Resolve(command.ProjectPath);

        foreach (var warning in project.Warnings)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {warning}");
        }

        var request = new SessionRequest
        {
            Project = project,
            Command = agentCommand,
            Keep = command.Keep,
        };

        if (command.DryRun)
        {
            return DryRun(request);
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{project.HostPath} -> {project.SandboxPath} (read-write; everything else is read-only)[/]");

        var manager = new SessionManager();
        SandboxSession? session = null;

        // The status display must be finished before ssh takes the terminal, or its spinner
        // competes with the agent's own full-screen rendering. That is why starting the sandbox
        // and connecting to it are two calls rather than one.
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting", async ctx =>
            {
                var progress = new Progress<(SessionPhase Phase, string Detail)>(update =>
                    ctx.Status(Markup.Escape(update.Detail)));

                session = await manager.StartAsync(request, progress, cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        await using var live = session!;

        return await live
            .ConnectAsync(SessionManager.ResolveCommand(agentCommand), cancellationToken)
            .ConfigureAwait(false);
    }

    private static int DryRun(SessionRequest request)
    {
        AnsiConsole.MarkupLine("[bold]Project[/]");
        AnsiConsole.MarkupLineInterpolated($"  {request.Project.HostPath} -> {request.Project.SandboxPath}");

        AnsiConsole.MarkupLine("[bold]Command[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"  {(request.Command.Count == 0 ? "(interactive shell)" : string.Join(' ', request.Command))}");

        AnsiConsole.MarkupLine("[dim]Nothing was started.[/]");

        return (int)ExitCode.Ok;
    }

    private static async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var ids = await new WsbClient().ListAsync(cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No sandbox is running.[/]");
            return (int)ExitCode.Ok;
        }

        foreach (var id in ids)
        {
            AnsiConsole.MarkupLineInterpolated($"{id}");
        }

        return (int)ExitCode.Ok;
    }

    private static async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var wsb = new WsbClient();
        var ids = await wsb.ListAsync(cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No sandbox is running.[/]");
            return (int)ExitCode.Ok;
        }

        foreach (var id in ids)
        {
            await wsb.StopAsync(id, cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLineInterpolated($"Stopped {id}");
        }

        return (int)ExitCode.Ok;
    }

    private static int NotYet(string what)
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]airlock:[/] '{what}' is not implemented yet.");
        return (int)ExitCode.Internal;
    }

    private static void Fail(string message) =>
        AnsiConsole.MarkupLineInterpolated($"[red]airlock:[/] {message}");

    private static void WriteWsbFailure(WsbException ex)
    {
        Fail(ex.Message);

        if (ex.IsAlreadyRunning)
        {
            AnsiConsole.MarkupLine(
                "A Windows Sandbox is already running, and Windows allows only one at a time. " +
                "Close it, or run '[bold]airlock stop[/]' if Airlock started it.");
        }
        else if (ex.IsStaleBaseImage)
        {
            AnsiConsole.MarkupLine("This usually means a stale Sandbox base image. From an [bold]elevated[/] prompt:");
            AnsiConsole.WriteLine(@"  Stop-Service CmService");
            AnsiConsole.WriteLine(@"  Rename-Item 'C:\ProgramData\Microsoft\Windows\Containers' Containers.old");
            AnsiConsole.WriteLine(@"  Start-Service CmService");
        }
        else if (!string.IsNullOrWhiteSpace(ex.StandardError))
        {
            AnsiConsole.WriteLine(ex.StandardError.Trim());
        }
    }

    private static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}

/// <summary>
/// Process exit codes. A session instead returns the wrapped command's own exit code, so these
/// avoid the low numbers a wrapped program is likely to use.
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
