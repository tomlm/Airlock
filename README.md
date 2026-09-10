# Airlock

Run AI coding agents inside a disposable Windows Sandbox, where **the only writable folders on your
machine are the projects you explicitly attach**.

```powershell
cd S:\src\MyProject
airlock claude
```

That brings up a sandbox, attaches this project to it read-write, and drops you into an interactive
Claude Code session running in `C:\work\MyProject` — in your own terminal, with your fonts, colours,
and scrollback. Run `airlock` from another project and it joins the same sandbox. `airlock stop`
destroys the VM and everything installed in it.

> **Status: in development.** Sessions work end to end and are covered by tests plus live spikes
> against Windows Sandbox. `doctor`, `connect`, `tools` and the per-project `.airlock.json` layer are
> not implemented yet. See [`spikes/FINDINGS.md`](spikes/FINDINGS.md) for what has been verified
> against the real thing.

## Why

Running an agent locally hands it your whole user profile: SSH keys, browser data, other repos,
cloud credentials. A container loses the Windows toolchain. A full VM loses the "just cd and run it"
feel. Airlock keeps the ergonomics and takes away the blast radius.

| Host path | In the sandbox | Access |
|---|---|---|
| each attached project | `C:\work\<name>` | **read/write** |
| `C:\Program Files\dotnet` | `C:\airlock\dotnet` | read-only |
| the agent CLI and OpenSSH | `C:\airlock\tools` | read-only |
| setup script and the **public** key | `C:\airlock\session` | read-only |

The sandbox's own `C:` is writable but ephemeral, so installs, caches, and scratch files vanish with
the VM. The session's **private** key is kept in a folder that is never mapped, and is deleted by
`airlock stop`.

## One sandbox, many projects

The sandbox is a long-running machine, not a per-command VM. The first `airlock` you run boots and
provisions it; every later one finds it already up and just attaches whatever folder it needs, which
takes about a second.

```powershell
cd S:\src\A ; airlock claude     # boots the sandbox, attaches A, starts Claude
cd S:\src\B ; airlock shell      # same sandbox, attaches B, opens a shell
airlock list                     # shows both folders as writable
airlock stop                     # destroys the VM and detaches everything
```

`airlock start` brings the sandbox up with **no** folders attached, so you can pay the boot cost up
front.

Worth knowing: attached folders accumulate. Windows Sandbox has no unshare, so once A and B are both
attached they are writable *at the same time*, and an agent working in A can reach B. `airlock list`
is the honest picture of what is currently exposed; `airlock stop` is what resets it.

## Commands

```
airlock <command...>       attach the current project and run a command in the sandbox
airlock shell              attach the current project and open an interactive shell
airlock start              start the sandbox with no folders attached, and leave it running
airlock list               show the sandbox and every folder currently attached
airlock stop               destroy the sandbox and detach everything
airlock connect            open the sandbox desktop window (browser logins, debugging)
airlock doctor             check Sandbox, CmService, wsb.exe, toolchains
airlock tools update       refresh the host-side tools folder
airlock trust              manage per-project .airlock.json approvals
```

Options are Airlock's only *before* the command starts, so `airlock claude --resume` forwards
`--resume` to Claude. Use `--` when a command collides with one of Airlock's verbs:

```powershell
airlock -- list            # runs `list` inside the sandbox
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
- **Attached projects are not isolated from each other.** They share one sandbox, so an agent
  working in one attached folder can read and write every other attached folder. Windows Sandbox has
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
into the *same* session are just `airlock shell` in another tab.

## Building

```powershell
dotnet build Airlock.slnx
dotnet test  Airlock.slnx
dotnet pack  src\Airlock -c Release
```

## License

MIT — see [LICENSE](LICENSE).
