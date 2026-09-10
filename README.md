# Airlock

Run an AI coding agent inside a disposable Windows Sandbox, where **the project you point it at is
the only writable folder on your machine**.

```powershell
cd S:\src\MyProject
airlock claude
```

That boots a sandbox, provisions it, and drops you into an interactive Claude Code session running
in `C:\work\MyProject` — in your own terminal, with your fonts, colours, and scrollback. When you
exit, the VM and everything installed in it is destroyed.

> **Status: in development.** The command line, the sandbox layer, and the provisioning script work
> and are covered by tests and by live spikes against Windows Sandbox. Wiring them into a full
> session is in progress. See [`spikes/FINDINGS.md`](spikes/FINDINGS.md) for what has been verified
> against the real thing.

## Why

Running an agent locally hands it your whole user profile: SSH keys, browser data, other repos,
cloud credentials. A container loses the Windows toolchain. A full VM loses the "just cd and run it"
feel. Airlock keeps the ergonomics and takes away the blast radius.

| Host path | In the sandbox | Access |
|---|---|---|
| your project | `C:\work\<name>` | **read/write** |
| `C:\Program Files\dotnet` | `C:\airlock\dotnet` | read-only |
| Node, Git, Python, the agent CLI | `C:\airlock\…` | read-only |
| session scripts and the public key | `C:\airlock\session` | read-only |

The sandbox's own `C:` is writable but ephemeral, so installs, caches, and scratch files vanish with
the VM.

## Commands

```
airlock <command...>        run a command in the sandbox, in the current project
airlock shell              open an interactive shell in the sandbox
airlock connect            open the sandbox desktop window (browser logins, debugging)
airlock list | stop        show or tear down the running session
airlock doctor             check Sandbox, CmService, wsb.exe, toolchains, orphans
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
- **The agent can rewrite your git history.** The project is mapped read-write, `.git` included.
  Airlock warns on a dirty working tree; it cannot stop `git reset --hard`.
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
dotnet pack  src\Airlock.Cli -c Release
```

## License

MIT — see [LICENSE](LICENSE).
