using System.Reflection;
using Airlock.Cli;
using Airlock.Configuration;
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
            case InvocationKind.Usage:
                // CShell prints its own parse errors, but the ones Airlock raises still need saying.
                if (!command.AlreadyReported)
                {
                    Fail(command.Error ?? "Unusable command line.");
                }

                return (int)ExitCode.Usage;

            case InvocationKind.Help:
                // CShell's TryParse already printed the usage text when --help asked for it.
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
            ReservedVerbs.Add => ConfigCommands.Add(Config, command.ProjectPath),
            ReservedVerbs.Remove =>
                ConfigCommands.Remove(Config, command.ProjectPath, await IsRunningAsync(cancellationToken).ConfigureAwait(false)),
            ReservedVerbs.Tools => ConfigCommands.Tools(Config, command.Arguments),
            // No arguments after the verb means a shell: you have opened the airlock and stepped in.
            ReservedVerbs.Open =>
                await OpenSessionAsync(command, command.Arguments, cancellationToken).ConfigureAwait(false),
            _ => NotYet(command.Verb!),
        };

    private static AirlockConfigStore Config { get; } = new();

    /// <summary>Whether a sandbox Airlock can claim is up, for advice that depends on it.</summary>
    private static async Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        await new SandboxHost().GetStateAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Brings the sandbox up with everything configured mounted, and shows the desktop.
    /// </summary>
    /// <remarks>
    /// The window comes up last on purpose: the desktop inherits machine environment at logon and
    /// never re-reads it, so opening it before provisioning has set PATH gives the first shell a
    /// stale one.
    /// </remarks>
    private static async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        var host = new SandboxHost();

        if (await host.GetStateAsync(cancellationToken).ConfigureAwait(false) is { } already)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Already running ({already.Id}) since {already.StartedUtc.ToLocalTime():t}.[/]");

            await host.OpenDesktopAsync(already, cancellationToken).ConfigureAwait(false);

            return (int)ExitCode.Ok;
        }

        var config = Config.LoadOrCreate();
        var state = await WithStatusAsync(host, cancellationToken).ConfigureAwait(false);

        await host.OpenDesktopAsync(state, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated(
            $"Sandbox up with {config.Tools.Count} tool(s) on PATH and {config.Airlocks.Count} airlock(s) mounted.");
        AnsiConsole.MarkupLine("[dim]Open one with 'airlock open' in its folder; stop with 'airlock stop'.[/]");

        return (int)ExitCode.Ok;
    }

    /// <summary>Reopens the sandbox desktop window.</summary>
    private static async Task<int> ConnectDesktopAsync(CommandLine command, CancellationToken cancellationToken)
    {
        var host = new SandboxHost();
        var state = await WithStatusAsync(host, cancellationToken).ConfigureAwait(false);

        await host.OpenDesktopAsync(state, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated($"Opening the desktop for sandbox {state.Id}.");

        return (int)ExitCode.Ok;
    }

    /// <summary>
    /// Opens this folder as an airlock and launches a tool in it, inside the sandbox's desktop.
    /// </summary>
    /// <remarks>
    /// A folder that is not yet an airlock is added and mounted here, so working in a new checkout
    /// is one command rather than two.
    /// </remarks>
    private static async Task<int> OpenSessionAsync(
        CommandLine command,
        IReadOnlyList<string> toolCommand,
        CancellationToken cancellationToken)
    {
        var project = ProjectResolver.Resolve(command.ProjectPath);

        foreach (var warning in project.Warnings)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {warning}");
        }

        if (command.DryRun)
        {
            return DryRun(project, toolCommand);
        }

        var config = Config.LoadOrCreate();
        var airlock = config.FindByHost(project.HostPath);
        var isNew = airlock is null;

        if (airlock is null)
        {
            airlock = new AirlockDefinition(project.HostPath, config.AllocateName(project.Name));
            config.Airlocks.Add(airlock);
            Config.Save(config);
        }

        var host = new SandboxHost();

        if (IsAgent(toolCommand))
        {
            host.EnsureAgent(new Progress<string>(m => AnsiConsole.MarkupLineInterpolated($"[dim]{m}[/]")));
        }

        var state = await WithStatusAsync(host, cancellationToken).ConfigureAwait(false);
        var sandboxPath = await host.AttachAsync(state, airlock, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{(isNew ? "Added " : string.Empty)}{project.HostPath} -> {sandboxPath} (read-write)[/]");

        await host.OpenAsync(state, sandboxPath, toolCommand, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated(
            $"Opened {(toolCommand.Count == 0 ? "a shell" : string.Join(' ', toolCommand))} in the sandbox.");

        return (int)ExitCode.Ok;
    }

    /// <summary>The agent CLI is staged on first use rather than shipped in every sandbox.</summary>
    private static bool IsAgent(IReadOnlyList<string> command) =>
        command.Count > 0 &&
        System.IO.Path.GetFileNameWithoutExtension(command[0])
            .Equals("claude", StringComparison.OrdinalIgnoreCase);

    private static async Task<SandboxState> WithStatusAsync(
        SandboxHost host,
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

                state = await host.EnsureRunningAsync(progress, cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        return state!;
    }

    private static int DryRun(ResolvedProject project, IReadOnlyList<string> command)
    {
        AnsiConsole.MarkupLine("[bold]Project[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"  {project.HostPath} -> {SandboxPaths.ForProject(project.Name)} (read-write)");

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
            $"Sandbox [bold]{state.Id}[/], up since {state.StartedUtc.ToLocalTime():g}");

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
/// Process exit codes.
/// </summary>
/// <remarks>
/// Airlock is a manager now: it launches a tool into the sandbox's desktop and returns, so these
/// report how the launch went rather than what the tool eventually did. A tool's own exit code
/// stays inside the sandbox, where its window is.
/// </remarks>
internal enum ExitCode
{
    Ok = 0,
    Usage = 2,
    Preflight = 3,
    Sandbox = 4,
    Provisioning = 5,
    Tools = 7,
    Internal = 70,
    Interrupted = 130,
}
