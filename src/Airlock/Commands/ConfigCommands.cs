using Airlock.Configuration;
using Airlock.Paths;
using Airlock.Sandbox;
using Spectre.Console;

namespace Airlock;

/// <summary>
/// The verbs that manage what the sandbox is made of: which folders are writable, and what lands on
/// PATH. None of these touch a running sandbox - changes take effect at the next <c>start</c>, or
/// immediately for an airlock, which can be mounted into a live sandbox.
/// </summary>
internal static class ConfigCommands
{
    /// <summary>Registers a folder as an airlock without opening it.</summary>
    internal static int Add(AirlockConfigStore store, string? requestedPath)
    {
        var config = store.LoadOrCreate();
        var project = ProjectResolver.Resolve(requestedPath);

        foreach (var warning in project.Warnings)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {warning}");
        }

        if (config.FindByHost(project.HostPath) is { } existing)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Already an airlock: {existing.Host} -> {SandboxPaths.ForProject(existing.Name)}[/]");

            return (int)ExitCode.Ok;
        }

        var name = config.AllocateName(project.Name);
        config.Airlocks.Add(new AirlockDefinition(project.HostPath, name));
        store.Save(config);

        AnsiConsole.MarkupLineInterpolated(
            $"Added {project.HostPath} -> {SandboxPaths.ForProject(name)} (read-write)");

        if (name != project.Name)
        {
            // Worth saying: the folder inside will not be the name they expect.
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]'{project.Name}' was taken by another airlock, so this one is '{name}'.[/]");
        }

        return (int)ExitCode.Ok;
    }

    /// <summary>
    /// Unregisters an airlock so the next start leaves it out.
    /// </summary>
    /// <remarks>
    /// A running sandbox keeps it mounted: Windows Sandbox has no unshare, so the only way to
    /// withdraw write access from a live sandbox is to stop it.
    /// </remarks>
    internal static int Remove(AirlockConfigStore store, string? requestedPath, bool sandboxRunning)
    {
        var config = store.LoadOrCreate();
        var host = ProjectResolver.Resolve(requestedPath).HostPath;
        var existing = config.FindByHost(host);

        if (existing is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]{host} is not an airlock.[/]");
            return (int)ExitCode.Ok;
        }

        config.Airlocks.Remove(existing);
        store.Save(config);

        AnsiConsole.MarkupLineInterpolated($"Removed {existing.Host}");

        if (sandboxRunning)
        {
            AnsiConsole.MarkupLine(
                "[yellow]![/] The running sandbox still has it mounted and writable. Windows Sandbox " +
                "cannot unshare a folder, so that lasts until '[bold]airlock stop[/]'.");
        }

        return (int)ExitCode.Ok;
    }

    /// <summary>Lists, adds or removes the read-only mounts that go on PATH.</summary>
    internal static int Tools(AirlockConfigStore store, IReadOnlyList<string> args)
    {
        var config = store.LoadOrCreate();

        return args.Count == 0
            ? ShowTools(config)
            : args[0].ToLowerInvariant() switch
            {
                "add" => AddTool(store, config, args),
                "remove" or "rm" => RemoveTool(store, config, args),
                "list" => ShowTools(config),
                "refresh" => RefreshTools(store, config),
                _ => Unknown(args[0]),
            };
    }

    private static int ShowTools(AirlockConfig config)
    {
        if (config.Tools.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No tools configured. Add one with 'airlock tools add <path>'.[/]");
            return (int)ExitCode.Ok;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Tool");
        table.AddColumn("Host folder");
        table.AddColumn("In the sandbox");
        table.AddColumn("Found");

        foreach (var tool in config.Tools.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
        {
            var missing = !Directory.Exists(tool.Host);

            table.AddRow(
                Markup.Escape(tool.Id),
                missing ? $"[red]{Markup.Escape(tool.Host)}[/]" : Markup.Escape(tool.Host),
                Markup.Escape(SandboxPaths.ForTool(tool.Id)),
                tool.Detected ? "auto" : "added");
        }

        AnsiConsole.Write(table);

        if (config.Tools.Any(t => !Directory.Exists(t.Host)))
        {
            AnsiConsole.MarkupLine(
                "[yellow]![/] A folder in red no longer exists; the sandbox will start without it. " +
                "'[bold]airlock tools refresh[/]' re-probes the auto-detected ones.");
        }

        return (int)ExitCode.Ok;
    }

    private static int AddTool(AirlockConfigStore store, AirlockConfig config, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]airlock:[/] usage: airlock tools add <path> [[id]]");
            return (int)ExitCode.Usage;
        }

        var host = System.IO.Path.GetFullPath(args[1]);

        if (!Directory.Exists(host))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]airlock:[/] '{host}' does not exist.");
            return (int)ExitCode.Preflight;
        }

        var id = args.Count > 2 ? args[2] : SanitiseId(new DirectoryInfo(host).Name);

        if (config.Tools.Any(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]airlock:[/] a tool called '{id}' is already configured. Give this one a name, e.g. airlock tools add \"{host}\" {id}2");

            return (int)ExitCode.Usage;
        }

        config.Tools.Add(new ToolDefinition(id, host));
        store.Save(config);

        AnsiConsole.MarkupLineInterpolated(
            $"Added tool '{id}': {host} -> {SandboxPaths.ForTool(id)} (read-only, on PATH)");

        return (int)ExitCode.Ok;
    }

    private static int RemoveTool(AirlockConfigStore store, AirlockConfig config, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]airlock:[/] usage: airlock tools remove <id>");
            return (int)ExitCode.Usage;
        }

        var tool = config.Tools.FirstOrDefault(t => t.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]No tool called '{args[1]}'.[/]");
            return (int)ExitCode.Ok;
        }

        config.Tools.Remove(tool);
        store.Save(config);

        AnsiConsole.MarkupLineInterpolated($"Removed tool '{tool.Id}'.");

        return (int)ExitCode.Ok;
    }

    private static int RefreshTools(AirlockConfigStore store, AirlockConfig config)
    {
        AnsiConsole.MarkupLine(
            store.RefreshDetected(config)
                ? "Re-probed the auto-detected tools; some moved."
                : "[dim]Auto-detected tools are all where they were.[/]");

        return ShowTools(config);
    }

    private static int Unknown(string what)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[red]airlock:[/] '{what}' is not a tools command. Try add, remove, list or refresh.");

        return (int)ExitCode.Usage;
    }

    /// <summary>A tool id becomes a folder name and a PATH entry, so keep it plain.</summary>
    private static string SanitiseId(string name)
    {
        var cleaned = new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')]);

        return cleaned.Length == 0 ? "tool" : cleaned.ToLowerInvariant();
    }
}
