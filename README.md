# Airlock

A Windows Sandbox you configure once and work in. Your toolchains are mounted read-only onto its
PATH; the projects you open — the *airlocks* — are the only writable folders on your machine.

```powershell
airlock start                 # bring up the sandbox and show its desktop

cd S:\src\MyProject
airlock open claude           # opens this project as an airlock, starts Claude in it
```

The agent runs in a window you can see, in `C:\airlock\MyProject`. The desktop is right there for
the things a terminal cannot do — a browser sign-in, a UI test, a visual build. `airlock stop`
destroys the VM and everything installed in it.

> **Status: in development.** Configuration, `start`/`stop`/`list`/`open`/`add`/`tools`, provisioning
> and the GUI launch all work and are verified live against Windows Sandbox. `doctor` is not
> implemented. See [`spikes/FINDINGS.md`](spikes/FINDINGS.md) for what has been established by
> experiment rather than assumption.

## Why

Running an AI coding agent locally hands it your whole user profile: SSH keys, browser data, other
repos, cloud credentials. A container loses the Windows toolchain. A full VM loses the "just open it
and go" feel. Airlock keeps the ergonomics and takes away the blast radius.

## Two kinds of mount

That is the whole model.

| | Mounted | Where |
|---|---|---|
| **tools** | read-only, and put on PATH | `C:\airlock\_tools_\<id>` |
| **airlocks** | read-write | `C:\airlock\<name>` |

Airlocks sit directly under `C:\airlock`, so the path inside reads like the one outside:
`S:\github\foo` becomes `C:\airlock\foo`. Airlock's own folders share that root and are wrapped in
underscores — `_tools_`, `_session_`, `_out_` — to stay out of the way of anything you open.

The sandbox's own `C:` is writable but ephemeral: installs, caches and scratch files vanish with it.

## Commands

```
airlock start               boot with every configured tool and airlock mounted, show the desktop
airlock stop [--force]      destroy the sandbox
airlock list                the sandbox, and every airlock currently open in it
airlock open [tool ...]     open this folder as an airlock and launch a tool in it, or a shell
airlock add [-p:<path>]     register a folder as an airlock without opening it
airlock remove              unregister one
airlock tools               list the read-only mounts that land on PATH
airlock tools add <path> [id]
airlock tools remove <id>
airlock tools refresh       re-probe the auto-detected toolchains
airlock connect             reopen the desktop window
```

`airlock open` in a folder that is not yet an airlock adds it and mounts it, so a new checkout is one
command rather than two.

Options belong to Airlock only *before* the verb, so `airlock open claude --resume` forwards
`--resume` to Claude while `airlock --dry-run open claude` is Airlock's own flag. Because the command
always follows `open`, nothing is ambiguous — `airlock open list` runs `list` in the sandbox.

Following [CShell](https://github.com/tomlm/CShell)'s convention, option values **attach**:

```powershell
airlock --project:S:\src\Other open claude
```

## Configuration

`%APPDATA%\Airlock\airlock.json`, seeded on first run with whatever toolchains it finds, and meant to
be edited by hand.

```jsonc
{
  "memoryMb": 8192,
  "network": { "blockLan": true, "allow": [] },

  "airlocks": [
    { "host": "S:\\github\\foo", "name": "foo" }
  ],

  "tools": [
    { "id": "dotnet", "host": "C:\\Program Files\\dotnet", "detected": true,
      "env": { "DOTNET_ROOT": "{mount}" } },
    { "id": "git", "host": "C:\\Program Files\\Git", "detected": true, "path": ["cmd"] },
    { "id": "mytools", "host": "S:\\bin\\mytools" }
  ],

  "secrets": ["ANTHROPIC_API_KEY"]
}
```

`path` lists subfolders of the mount to put on PATH (omitted means the mount root). `{mount}` in
`env` expands to the tool's path inside the sandbox. `detected` tools are re-probed at every start,
so a moved or upgraded toolchain repairs itself; hand-added ones are taken literally.

`secrets` lists **names**, never values. They are read from your host environment at start and handed
to the guest through a file that both sides delete, so a credential is never written into config and
never appears in a command line.

## What Airlock does *not* protect against

Worth stating plainly, because it is easy to assume otherwise:

- **The boundary is host versus sandbox, not project versus project.** Every airlock is writable at
  the same time, by design — it is one configured machine with several workspaces. An agent working
  in one can read and write the others. Keep projects apart by not opening them together.
- **The network is not a jail.** The sandbox has full internet access. Airlock adds in-guest firewall
  rules blocking private ranges, but the guest is an administrator and can remove them. That is
  protection against accident, not against a determined agent.
- **Agents can rewrite your git history.** Airlocks are mounted read-write, `.git` included.
- **Read-only means read.** Everything mounted as a tool is fully readable by whatever runs in the
  sandbox. Do not mount anything you would not hand over.

## Requirements

- Windows 11 with the Windows Sandbox optional feature enabled
- .NET 10 SDK

Windows Sandbox allows one instance at a time, so Airlock runs one sandbox — which is the point:
everything you open shares it.

## Building

```powershell
dotnet build Airlock.slnx
dotnet test  Airlock.slnx
dotnet pack  src\Airlock -c Release
```

## License

MIT — see [LICENSE](LICENSE).
