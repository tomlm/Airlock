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
            ReservedVerbs.Connect => await ConnectDesktopAsync(command, cancellationToken).ConfigureAwait(false),
            ReservedVerbs.Stop => await StopAsync(command, cancellationToken).ConfigureAwait(false),
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
    /// Opens the sandbox's desktop window, for looking at what the agent did or working out why a
    /// sandbox came up wrong.
    /// </summary>
    private static async Task<int> ConnectDesktopAsync(CommandLine command, CancellationToken cancellationToken)
    {
        var host = new SandboxHost();
        var state = await WithStatusAsync(host, command.MemoryInMB, cancellationToken).ConfigureAwait(false);

        await host.OpenDesktopAsync(state, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated($"Opening the desktop for sandbox {state.Id}.");

        if (state.Folders.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Attached folders are visible under C:\\work.[/]");
        }

        // Worth stating rather than letting someone discover it by trying to sign in: the window is
        // a different Windows session from the one the agent runs in.
        AnsiConsole.MarkupLine(
            "[dim]The window signs in as WDAGUtilityAccount, which is a different account from the " +
            "'airlock' user your agent sessions run as. Files are shared; sign-ins and per-user " +
            "installs are not.[/]");

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

        if (state.Adopted)
        {
            // Identified by key rather than by our records, so the folder list is genuinely unknown
            // rather than empty. Saying "none attached" here would be a lie.
            AnsiConsole.MarkupLine(
                "[yellow]![/] Recovered by signing in, after the local record was lost. Folders " +
                "attached before that are still attached and still writable, but cannot be listed. " +
                "Run '[bold]airlock stop[/]' to clear them.");

            if (state.Folders.Count == 0)
            {
                return (int)ExitCode.Ok;
            }
        }
        else if (state.Folders.Count == 0)
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

    private static async Task<int> StopAsync(CommandLine command, CancellationToken cancellationToken)
    {
        var parsed = CShellNet.Cli.For(command.Arguments)
            .Program("airlock stop")
            .Description("Destroy the running sandbox and detach every folder attached to it.")
            .Switch(out bool force, "stop a sandbox even if Airlock cannot prove it started it")
            .TryParse();

        if (parsed.ShouldExit)
        {
            return parsed.HelpRequested ? (int)ExitCode.Ok : (int)ExitCode.Usage;
        }

        var host = new SandboxHost();

        if (await host.StopAsync(cancellationToken).ConfigureAwait(false))
        {
            AnsiConsole.MarkupLine("Sandbox stopped; every attached folder is detached.");
            return (int)ExitCode.Ok;
        }

        // Nothing of ours. That is either genuinely nothing, or a sandbox we cannot claim - which
        // includes one that really was ours before its key went away.
        var running = await host.ListRunningAsync(cancellationToken).ConfigureAwait(false);

        if (running.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No Airlock sandbox is running.[/]");
            return (int)ExitCode.Ok;
        }

        if (!force)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]![/] A Windows Sandbox is running ({running[0]}), but Airlock cannot prove it started it.");
            AnsiConsole.MarkupLine(
                "It may be one you opened yourself, or one of Airlock's whose session key is gone. " +
                "Run '[bold]airlock stop --force[/]' to stop it anyway.");

            return (int)ExitCode.Ok;
        }

        foreach (var id in running)
        {
            if (!ConfirmForceStop(id))
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]Left {id} running.[/]");
                continue;
            }

            await host.ForceStopAsync(id, cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLineInterpolated($"Stopped {id}.");
        }

        return (int)ExitCode.Ok;
    }

    /// <summary>
    /// Asks before destroying a sandbox Airlock cannot claim, since it may be the user's own.
    /// </summary>
    /// <remarks>
    /// With input redirected there is nobody to ask and <c>AskYesNo</c> would throw at end of
    /// stream, so <c>--force</c> is taken as the answer - it was typed deliberately.
    /// </remarks>
    private static bool ConfirmForceStop(string id)
    {
        if (Console.IsInputRedirected)
        {
            return true;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"Sandbox {id} is running and Airlock cannot prove it started it.");
        AnsiConsole.MarkupLine("[dim]Anything unsaved inside it will be lost.[/]");

        return CShellNet.Globals.AskYesNo("Stop it anyway?", false);
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
