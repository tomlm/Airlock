![Icon](https://raw.githubusercontent.com/tomlm/airlock/main/icon.png)

# Airlock

[![Build Status](https://github.com/tomlm/airlock/actions/workflows/BuildAndRunTests.yml/badge.svg)](https://github.com/tomlm/airlock/actions/workflows/BuildAndRunTests.yml) [![NuGet Version](https://img.shields.io/nuget/v/airlock.svg)](https://www.nuget.org/packages/airlock/)  [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)


A Windows Sandbox you configure once and work in. Your toolchains are mounted read-only onto its
PATH; the projects you open — the *airlocks* — are the only writable folders on your machine.

```powershell
airlock start                 # bring up the sandbox and show its desktop

cd S:\src\MyProject
airlock open claude           # opens this project as an airlock, starts Claude in it
```

The agent runs in a window you can see, in `A:\MyProject`. The desktop is right there for
the things a terminal cannot do — a browser sign-in, a UI test, a visual build. `airlock stop`
destroys the VM and everything installed in it.

> **Status: in development.** Every command works and is verified live against Windows Sandbox. See
> [`spikes/FINDINGS.md`](spikes/FINDINGS.md) for what has been established by experiment rather than
> assumption.

## Why

Running an AI coding agent locally hands it your whole user profile: SSH keys, browser data, other
repos, cloud credentials. A container loses the Windows toolchain. A full VM loses the "just open it
and go" feel. Airlock keeps the ergonomics and takes away the blast radius.

## Two kinds of mount

That is the whole model.

| | Mounted | Where |
|---|---|---|
| **tools** | read-only, and put on PATH | `C:\tools\<id>` |
| **airlocks** | read-write | `A:\<name>` |

Every airlock is on **`A:`** — one letter and a name, which keeps working paths well clear of
MAX_PATH once a build starts nesting. `S:\github\foo` becomes `A:\foo`. The drive is substituted
onto `C:\airlocks` while the sandbox provisions, so `A:\foo` and `C:\airlocks\foo` are the same
folder; the short spelling is the one to use, though a tool that resolves paths for itself will
report the long one.

Airlock's other folders are top-level too — `C:\tools`, `C:\session`, `C:\out`, `C:\setup`. There
is no tidiness to protect in a machine that is thrown away, and giving the airlocks a folder of their
own means no name is reserved: a repo called `tools` is just `A:\tools`.

The sandbox's own `C:` is writable but ephemeral: installs, caches and scratch files vanish with it.

## Commands

```
airlock start               boot with every configured tool and airlock mounted, show the desktop
airlock stop [--force]      destroy the sandbox
airlock list                every airlock, and whether it is open right now
airlock open [tool ...]     open this folder as an airlock and launch a tool in it, or a shell
airlock create [folder]     create an airlock without opening it
airlock remove [name|folder] unregister one
airlock tools               list the read-only mounts that land on PATH
airlock tools add <path> [id]
airlock tools remove <id>
airlock tools refresh       re-probe the auto-detected toolchains
airlock connect             reopen the desktop window
```

`airlock open` needs the sandbox already running — starting one takes a minute and a half and puts a
window on screen, so that stays something you ask for with `airlock start`.

In a folder that is not yet an airlock, `open` creates one, so a new checkout is one command rather
than two. In a folder *inside* an existing airlock it does not — that folder is already in the
sandbox, so it simply opens there: from `S:\src\foo\tests` you land in `A:\foo\tests`.

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
    { "id": "dotnet-tools", "host": "C:\\Users\\you\\.dotnet\\tools", "detected": true },
    { "id": "coreutils", "host": "C:\\Program Files\\coreutils\\bin", "detected": true },
    { "id": "mytools", "host": "S:\\bin\\mytools" }
  ],

  "secrets": ["ANTHROPIC_API_KEY"]
}
```

`path` lists subfolders of the mount to put on PATH (omitted means the mount root). `{mount}` in
`env` expands to the tool's path inside the sandbox. `detected` tools are re-probed by
`airlock tools refresh`, so a moved or upgraded toolchain repairs itself and a newly installed one is
picked up; hand-added ones are taken literally.

Detected out of the box: .NET, your .NET global tools, Node, Git, Python, and
[Coreutils for Windows](https://github.com/microsoft/coreutils) if you have it
(`winget install Microsoft.Coreutils`), which puts `ls`, `cat`, `head` and the rest on the sandbox's
PATH.

`dotnet-tools` means anything installed with `dotnet tool install -g` on the host is on PATH in the
sandbox too. The whole `.dotnet\tools` folder is mounted rather than just the shims, because a shim
is an apphost that finds its payload relative to itself, in the `.store` folder beside it. It is
read-only like every other tool, so `dotnet tool install -g` *inside* the sandbox will fail - install
on the host and restart the sandbox instead.

Changing tools takes effect the next time the sandbox starts. Unlike an airlock, which `open` can
share into a running sandbox, a tool's mount and its PATH entry are both fixed at boot.

`secrets` lists **names**, never values. They are read from your host environment at start and handed
to the guest through a file that both sides delete, so a credential is never written into config and
never appears in a command line.

## What Airlock does *not* protect against

Worth stating plainly, because it is easy to assume otherwise:

- **The boundary is host versus sandbox, not project versus project.** Every airlock is writable at
  the same time, by design — it is one configured machine with several workspaces. An agent working
  in one can read and write the others. Keep projects apart by not opening them together.
- **The network is not a jail.** The sandbox has full internet access. Airlock adds in-guest firewall
  rules blocking private ranges, so the agent cannot reach your NAS or router — but the guest
  keeps its own NAT segment, gateway and DNS, or it would not be a working machine, and the guest
  is an administrator and can remove the rules. That is protection against accident, not against a
  determined agent.
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
dotnet build src\Airlock.slnx
dotnet test  src\Airlock.slnx
dotnet pack  src\Airlock -c Release
```

## License

MIT — see [LICENSE](LICENSE).
