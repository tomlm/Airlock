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
                return await OpenSessionAsync(command, command.Arguments, cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unhandled invocation kind '{command.Kind}'.");
        }
    }

    private static async Task<int> DispatchVerbAsync(CommandLine command, CancellationToken cancellationToken) =>
        command.Verb switch
        {
            ReservedVerbs.Start => await StartAsync(cancellationToken).ConfigureAwait(false),
            ReservedVerbs.Stop => await StopAsync(cancellationToken).ConfigureAwait(false),
            ReservedVerbs.List => await ListAsync(cancellationToken).ConfigureAwait(false),
            ReservedVerbs.Run => await OpenSessionAsync(command, command.Arguments, cancellationToken)
                .ConfigureAwait(false),
            ReservedVerbs.Shell => await OpenSessionAsync(command, [], cancellationToken).ConfigureAwait(false),
            _ => NotYet(command.Verb!),
        };

    /// <summary>
    /// Brings the sandbox up with no project attached, and leaves it running. Useful for paying the
    /// boot cost once, up front, before attaching anything to it.
    /// </summary>
    private static async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        var host = new SandboxHost();

        if (await host.GetStateAsync(cancellationToken).ConfigureAwait(false) is { } already)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Already running ({already.Id}) since {already.StartedUtc.ToLocalTime():t}.[/]");

            return (int)ExitCode.Ok;
        }

        var state = await WithStatusAsync(host, 0, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated($"Sandbox running at {state.IpAddress} with no folders attached.");
        AnsiConsole.MarkupLine("[dim]Attach one by running 'airlock <command>' in a project; stop it with 'airlock stop'.[/]");

        return (int)ExitCode.Ok;
    }

    /// <summary>
    /// The default path: make sure the sandbox is up, attach this project to it read-write, and
    /// hand over the terminal.
    /// </summary>
    private static async Task<int> OpenSessionAsync(
        CommandLine command,
        IReadOnlyList<string> agentCommand,
        CancellationToken cancellationToken)
    {
        var project = ProjectResolver.Resolve(command.ProjectPath);

        foreach (var warning in project.Warnings)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {warning}");
        }

        var host = new SandboxHost();

        if (command.DryRun)
        {
            return DryRun(project, agentCommand);
        }

        // The status display has to finish before ssh takes the terminal, or its spinner competes
        // with the agent's own full-screen rendering.
        var state = await WithStatusAsync(host, command.MemoryInMB, cancellationToken).ConfigureAwait(false);
        var sandboxPath = await host.AttachAsync(state, project, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated($"[dim]{project.HostPath} -> {sandboxPath} (read-write)[/]");

        var staging = new Progress<string>(m => AnsiConsole.MarkupLineInterpolated($"[dim]{m}[/]"));

        return await host.ConnectAsync(state, sandboxPath, agentCommand, staging, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SandboxState> WithStatusAsync(
        SandboxHost host,
        int memoryInMB,
        CancellationToken cancellationToken)
    {
        // Nothing to show when the sandbox is already up: this is the common case and should feel
        // instant rather than flashing a spinner.
        if (await host.GetStateAsync(cancellationToken).ConfigureAwait(false) is { } running)
        {
            return running;
        }

        SandboxState? state = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting", async ctx =>
            {
                var progress = new Progress<(SandboxPhase Phase, string Detail)>(update =>
                    ctx.Status(Markup.Escape(update.Detail)));

                state = await host
                    .EnsureRunningAsync(progress, memoryInMB > 0 ? memoryInMB : 8192, cancellationToken)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        return state!;
    }

    private static int DryRun(ResolvedProject project, IReadOnlyList<string> command)
    {
        AnsiConsole.MarkupLine("[bold]Project[/]");
        AnsiConsole.MarkupLineInterpolated($"  {project.HostPath} -> C:\\work\\{project.Name} (read-write)");

        AnsiConsole.MarkupLine("[bold]Command[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"  {(command.Count == 0 ? "(interactive shell)" : string.Join(' ', command))}");

        AnsiConsole.MarkupLine("[dim]Nothing was started.[/]");

        return (int)ExitCode.Ok;
    }

    private static async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var host = new SandboxHost();
        var state = await host.GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            var others = await new WsbClient().ListAsync(cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(others.Count == 0
                ? "[dim]No sandbox is running.[/]"
                : $"[yellow]A Windows Sandbox that Airlock did not start is running ({others[0]}).[/]");

            return (int)ExitCode.Ok;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"Sandbox [bold]{state.Id}[/] at {state.IpAddress}, up since {state.StartedUtc.ToLocalTime():g}");

        if (state.Folders.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No folders attached.[/]");
            return (int)ExitCode.Ok;
        }

        // Every attached folder is writable from inside the sandbox, and stays that way until it is
        // stopped, so listing them is the honest picture of what is currently exposed.
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Host folder (read-write)");
        table.AddColumn("In the sandbox");

        foreach (var folder in state.Folders)
        {
            table.AddRow(Markup.Escape(folder.HostPath), Markup.Escape(folder.SandboxPath));
        }

        AnsiConsole.Write(table);

        return (int)ExitCode.Ok;
    }

    private static async Task<int> StopAsync(CancellationToken cancellationToken)
    {
        var host = new SandboxHost();

        if (await host.StopAsync(cancellationToken).ConfigureAwait(false))
        {
            AnsiConsole.MarkupLine("Sandbox stopped; every attached folder is detached.");
            return (int)ExitCode.Ok;
        }

        AnsiConsole.MarkupLine("[dim]No Airlock sandbox is running.[/]");

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
