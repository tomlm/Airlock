using Airlock.Cli;
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
    /// <summary>
    /// Registers a folder as an airlock without opening it.
    /// </summary>
    /// <param name="target">
    /// A folder, or nothing for the current directory. Takes precedence over <c>--project</c>.
    /// </param>
    internal static int Add(AirlockConfigStore store, string? projectOption, IReadOnlyList<string> args)
    {
        var parsed = VerbCli
            .For(ReservedVerbs.Add, args, "Register a folder as an airlock, without opening it.")
            .Example("airlock add", "register this folder")
            .Example("airlock add S:\\src\\foo", "register another one")
            .Rest("folder", "the folder to register; defaults to the current directory")
            .TryParse();

        if (parsed.ShouldExit)
        {
            return parsed.HelpRequested ? (int)ExitCode.Ok : (int)ExitCode.Usage;
        }

        var config = store.LoadOrCreate();
        var target = Target(projectOption, args);
        var project = ProjectResolver.Resolve(target);

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
    /// <param name="args">
    /// An airlock name as <c>airlock list</c> shows it, or a folder, or nothing for the current
    /// directory.
    /// </param>
    /// <remarks>
    /// A running sandbox keeps it mounted: Windows Sandbox has no unshare, so the only way to
    /// withdraw write access from a live sandbox is to stop it.
    /// </remarks>
    internal static int Remove(
        AirlockConfigStore store,
        string? projectOption,
        IReadOnlyList<string> args,
        bool sandboxRunning)
    {
        var parsed = VerbCli
            .For(ReservedVerbs.Remove, args,
                "Unregister an airlock. A running sandbox keeps it mounted until it is stopped.")
            .Example("airlock remove spikes", "by the name 'airlock list' shows")
            .Example("airlock remove S:\\src\\foo", "by folder")
            .Example("airlock remove", "this folder")
            .Rest("name", "an airlock name or folder; defaults to the current directory")
            .TryParse();

        if (parsed.ShouldExit)
        {
            return parsed.HelpRequested ? (int)ExitCode.Ok : (int)ExitCode.Usage;
        }

        var config = store.LoadOrCreate();
        var target = Target(projectOption, args);
        var existing = Find(config, target);

        if (existing is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]{target ?? Directory.GetCurrentDirectory()} is not an airlock.[/]");
            AnsiConsole.MarkupLine("[dim]'airlock list' shows them; remove one by name or by folder.[/]");

            return (int)ExitCode.Ok;
        }

        config.Airlocks.Remove(existing);
        store.Save(config);

        AnsiConsole.MarkupLineInterpolated($"Removed {existing.Name} ({existing.Host})");

        if (sandboxRunning)
        {
            AnsiConsole.MarkupLine(
                "[yellow]![/] The running sandbox still has it mounted and writable. Windows Sandbox " +
                "cannot unshare a folder, so that lasts until '[bold]airlock stop[/]'.");
        }

        return (int)ExitCode.Ok;
    }

    /// <summary>
    /// What the user meant to act on: the positional argument, else <c>--project</c>, else here.
    /// </summary>
    /// <remarks>
    /// The argument wins because it is what someone types after reading <c>airlock list</c>. It was
    /// previously ignored outright, so <c>airlock remove spikes</c> silently operated on the current
    /// directory and reported that the current directory was not an airlock.
    /// </remarks>
    private static string? Target(string? projectOption, IReadOnlyList<string> args) =>
        args.Count > 0 ? args[0] : projectOption;

    /// <summary>
    /// Finds an airlock by the name <c>list</c> shows, or failing that by folder.
    /// </summary>
    /// <remarks>
    /// Name first, because that is the short thing on screen and cannot be confused with a relative
    /// path that happens to exist.
    /// </remarks>
    private static AirlockDefinition? Find(AirlockConfig config, string? target)
    {
        if (!string.IsNullOrWhiteSpace(target) &&
            config.Airlocks.FirstOrDefault(
                a => a.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            return byName;
        }

        return config.FindByHost(ResolveForRemoval(target));
    }

    /// <summary>
    /// The path to unregister, without requiring that it still exists.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectResolver"/> refuses a folder that is gone, which is right when opening one
    /// and wrong when removing it: <c>airlock list</c> shows a deleted folder in red and says it
    /// will be skipped, so there has to be a way to clear it. Falling back to the plain full path
    /// matches how it was recorded in the first place.
    /// </remarks>
    private static string ResolveForRemoval(string? requestedPath)
    {
        try
        {
            return ProjectResolver.Resolve(requestedPath).HostPath;
        }
        catch (ProjectValidationException)
        {
            return System.IO.Path.GetFullPath(
                string.IsNullOrWhiteSpace(requestedPath) ? Directory.GetCurrentDirectory() : requestedPath);
        }
    }

    /// <summary>Lists, adds or removes the read-only mounts that go on PATH.</summary>
    /// <remarks>
    /// Parsed through CShell like the other verbs rather than by hand, which is what makes
    /// <c>airlock tools --help</c> answer instead of complaining that <c>--help</c> is not a tools
    /// command. CShell has no notion of subcommands, so the actions are examples and the dispatch
    /// below is still ours - but the help is generated, and so cannot drift.
    /// </remarks>
    internal static int Tools(AirlockConfigStore store, IReadOnlyList<string> args)
    {
        var parsed = VerbCli
            .For(ReservedVerbs.Tools, args,
                "Manage the read-only mounts that land on the sandbox's PATH. Changes take effect " +
                "the next time the sandbox starts.")
            .Example("airlock tools", "list them")
            .Example("airlock tools add S:\\bin\\mytools", "mount a folder read-only and put it on PATH")
            .Example("airlock tools add \"C:\\Program Files\\Foo\" foo", "the same, with an explicit id")
            .Example("airlock tools remove foo", "stop mounting it")
            .Example("airlock tools refresh", "re-probe the auto-detected toolchains")
            .Rest("action", "list | add | remove | refresh")
            .TryParse();

        if (parsed.ShouldExit)
        {
            return parsed.HelpRequested ? (int)ExitCode.Ok : (int)ExitCode.Usage;
        }

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
            $"[red]airlock:[/] '{what}' is not a tools command. Try 'airlock tools --help'.");

        return (int)ExitCode.Usage;
    }

    /// <summary>A tool id becomes a folder name and a PATH entry, so keep it plain.</summary>
    private static string SanitiseId(string name)
    {
        var cleaned = new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')]);

        return cleaned.Length == 0 ? "tool" : cleaned.ToLowerInvariant();
    }
}
