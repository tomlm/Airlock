# Airlock

Run AI coding agents inside a disposable Windows Sandbox, where **the only writable folders on your
machine are the projects you explicitly attach**.

```powershell
cd S:\src\MyProject
airlock open claude
```

That brings up a sandbox, attaches this project to it read-write, and drops you into an interactive
Claude Code session running in `C:irlock\MyProject` — in your own terminal, with your fonts, colours,
and scrollback. Run `airlock` from another project and it joins the same sandbox. `airlock stop`
destroys the VM and everything installed in it.

> **Status: in development.** Sessions, `start`/`stop`/`list`/`connect` work end to end and are
> covered by tests plus live spikes against Windows Sandbox. `doctor`, `tools` and the per-project
> `.airlock.json` layer are not implemented yet. See [`spikes/FINDINGS.md`](spikes/FINDINGS.md) for
> what has been verified against the real thing.
>
> Building currently needs a [CShell](https://github.com/tomlm/CShell) checkout beside this one:
> Airlock uses `ExecAsync`, which is not in a released CShell yet. See **Building**.

## Why

Running an agent locally hands it your whole user profile: SSH keys, browser data, other repos,
cloud credentials. A container loses the Windows toolchain. A full VM loses the "just cd and run it"
feel. Airlock keeps the ergonomics and takes away the blast radius.

| Host path | In the sandbox | Access |
|---|---|---|
| each open project | `C:\airlock\<name>` | **read/write** |
| `C:\Program Files\dotnet` | `C:\airlock\_dotnet_` | read-only |
| the agent CLI and OpenSSH | `C:\airlock\_tools_` | read-only |
| setup script and the **public** key | `C:\airlock\_session_` | read-only |

Projects sit directly under `C:\airlock`, so the path inside reads like the one outside:
`S:\github\foo` becomes `C:\airlock\foo`. Airlock's own folders share that root, so they are wrapped
in underscores to stay out of the way of anything you might open.

The sandbox's own `C:` is writable but ephemeral, so installs, caches, and scratch files vanish with
the VM. The session's **private** key is kept in a folder that is never mapped, and is deleted by
`airlock stop`.

## One sandbox, many projects

The sandbox is a long-running machine, not a per-command VM. The first `airlock` you run boots and
provisions it; every later one finds it already up and just attaches whatever folder it needs, which
takes about a second.

```powershell
cd S:\src\A ; airlock open claude   # boots the sandbox, opens A, starts Claude
cd S:\src\B ; airlock open          # same sandbox, opens B, gives you a shell
airlock list                         # shows both projects as writable
airlock stop                         # destroys the VM and closes everything
```

`airlock start` brings the sandbox up with **nothing** open, so you can pay the boot cost up front.

If Airlock ever refuses to start *and* refuses to stop - which happens when a sandbox really was
its own but the session key is gone, so ownership can no longer be proven - `airlock stop --force`
is the way out. It names the sandbox and asks before destroying it, since it might be one you opened
yourself.

`airlock connect` opens the sandbox's own desktop window, which is useful for looking at what an
agent did or working out why a sandbox came up wrong. Note that the window signs in as
`WDAGUtilityAccount`, a different Windows account from the `airlock` user your agent sessions run
as: mapped folders are shared between them, but sign-ins and per-user installs are not.

Worth knowing: open projects accumulate. Windows Sandbox has no unshare, so once A and B are both
open they are writable *at the same time*, and an agent working in A can reach B. `airlock list`
is the honest picture of what is currently exposed; `airlock stop` is what resets it.

## Commands

```
airlock open [command...]  open this project in the sandbox and run a command, or a shell
airlock start              start the sandbox with nothing open, and leave it running
airlock list               show the sandbox and every project currently open
airlock stop [--force]     destroy the sandbox and detach everything
airlock connect            open the sandbox desktop window (looking around, debugging)
airlock doctor             check Sandbox, CmService, wsb.exe, toolchains
airlock tools update       refresh the host-side tools folder
airlock trust              manage per-project .airlock.json approvals
```

Options are Airlock's only *before* the verb, so `airlock open claude --resume` forwards `--resume`
to Claude, and `airlock --dry-run open claude` is Airlock's own flag. Because the command always
follows `open`, there is nothing to disambiguate:

```powershell
airlock open list          # runs `list` inside the sandbox
```

Following [CShell](https://github.com/tomlm/CShell)'s convention, option values **attach**:

```powershell
airlock --project:S:\src\Other claude
```

## Configuration

Layered, later wins: built-in defaults → `%APPDATA%\Airlock\airlock.json` → the project's
`.airlock.json` → `--config:<file>` → command line.

A project's `.airlock.json` lives inside the one folder the agent can write to, so settings that
would expose more of the host — extra `readOnly`/`readWrite` mappings, loosening the network — are
**gated behind a per-project trust prompt** keyed to the file's hash. Which host secrets get
forwarded is never settable from a project file. See
[`docs/DESIGN-config.md`](docs/DESIGN-config.md).

## What Airlock does *not* protect against

Worth stating plainly, because it is easy to assume otherwise:

- **The network is not a jail.** The sandbox has full internet access, and can reach your host and
  LAN. Airlock adds in-guest firewall rules to block private ranges, but the sandbox user is an
  administrator and could remove them. This is protection against accident, not against a
  determined agent.
- **Open projects are not isolated from each other.** They share one sandbox, so an agent
  working in one open project can read and write every other open project. Windows Sandbox has
  no unshare, so this lasts until `airlock stop`. Run `airlock list` to see what is exposed, and stop
  the sandbox between projects you want kept apart.
- **The agent can rewrite your git history.** Projects are mapped read-write, `.git` included.
  Airlock warns when a folder is not a git repository; it cannot stop `git reset --hard`.
- **Read-only means read.** Everything mapped read-only is fully readable by the agent. Do not map
  anything you would not hand it.

## Requirements

- Windows 11 with the Windows Sandbox optional feature enabled
- .NET 10 SDK (also what gets mapped into the sandbox)
- The OpenSSH client (`ssh.exe`, `ssh-keygen.exe`), shipped with Windows

Only one Windows Sandbox can run at a time, so Airlock runs one session at a time. Extra terminals
into the *same* session are just `airlock open` in another tab.

## Building

Airlock references CShell by project path while `Exec()`/`ExecAsync()` are unreleased, so clone the
two side by side:

```
S:\src\Airlock
S:\src\CShell
```

```powershell
dotnet build Airlock.slnx
dotnet test  Airlock.slnx
dotnet pack  src\Airlock -c Release
```

Once a CShell containing `ExecAsync` ships, `src\Airlock.Core\Airlock.Core.csproj` should go back to
a `PackageReference`; until then `dotnet pack` would resolve CShell to a version without it.

## License

MIT — see [LICENSE](LICENSE).
