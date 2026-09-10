# Configuration layering and project trust

Supersedes the config section of the original plan. Spike results live in
[`spikes/FINDINGS.md`](../spikes/FINDINGS.md).

## Layers

Later layers win. Objects merge per-leaf; arrays replace, except `env.path` / `env.forward`,
where a `"..."` element splices the previous layer's values in at that position.

| # | Layer | Location | Trust |
|---|---|---|---|
| 1 | Built-in defaults | embedded resource | — |
| 2 | **User** | `%APPDATA%\Airlock\airlock.json` | fully trusted |
| 3 | **Project** | `<project>\.airlock.json` (alias: `.airlock`) | **partially trusted — see below** |
| 4 | Explicit file | `--config:<file>` | fully trusted |
| 5 | Command line | switches | fully trusted |

The project file is found by walking up from the resolved project path to the git repo root,
taking the first match. `.airlock.json` is preferred over `.airlock`; if both exist, that is an
error rather than a silent pick.

## Why the project layer is not fully trusted

`.airlock.json` lives **inside the one folder the agent can write to**. An agent — or a cloned
untrusted repo — can add to it and have it take effect on the *next* run. A file containing

```json
{ "readWrite": ["C:\\Users\\therm"] }
```

would defeat the entire tool. So project settings split into two tiers.

### Plain settings — applied silently

These only change what happens *inside* the disposable VM. They cannot reach host data.

| Key | Meaning |
|---|---|
| `agent` | default agent command for this project (e.g. `"claude"`) |
| `shell` | guest default shell (`powershell` \| `pwsh` \| `cmd`) |
| `memoryMb` | sandbox memory |
| `tools` | **selection only** from toolchains the *user* layer already defines, by id: `["dotnet","node","git","python"]`. Cannot introduce a new host path. |
| `env.set`, `env.path` | environment inside the guest |
| `setup` | commands run in the guest after provisioning, before the agent — e.g. `["npm ci"]`. Runs as the sandbox user on the ephemeral disk; no host reach. |

### Elevated settings — require trust

These change what host data is exposed, so they are refused until the user approves them.

| Key | Meaning |
|---|---|
| `readOnly` | extra host paths mapped read-only — *reads host data into the sandbox* |
| `readWrite` | extra host paths mapped read-write — **the real escape hatch** |
| `network.blockLan: false` | loosens the network posture |

### Never settable from a project file, at any trust level

- `env.forwardSecret` and `agents.*.secrets` — the list of **host env var names to forward**.
  Otherwise a repo could add `AWS_SECRET_ACCESS_KEY` and read it inside the sandbox. Which
  secrets leave the host is a user-level decision only.
- Anything that would write outside the declared mappings.

Elevated settings are additionally still subject to `ProjectValidator`'s deny rules — no drive
roots, no `%USERPROFILE%`, nothing under `C:\Windows` or `C:\Program Files`. **Trust does not
bypass those**, it only permits paths that were already legal.

## Trust model

Modelled on VS Code workspace trust / `direnv allow`.

1. On encountering a `.airlock.json` with elevated settings, print exactly which host paths would
   be mapped and at what access, then prompt (`CShell`'s `AskYesNo`).
2. Record the decision in `%LOCALAPPDATA%\Airlock\trust.json`, keyed by the **resolved real
   project path + SHA-256 of the file contents**. Editing the file re-prompts.
3. `--trust-project` approves non-interactively, for CI.
4. `airlock trust --show` lists trusted projects; `airlock trust --revoke` clears one.
5. Non-interactive (stdin redirected) **and** untrusted elevated settings → refuse, and print the
   exact command to run interactively. Never silently downgrade.
6. `--dry-run` always echoes elevated settings and their trust state, whether or not they applied.

## Worked example

```jsonc
// S:\github\SomeRepo\.airlock.json
{
  "agent": "claude",
  "shell": "powershell",
  "memoryMb": 12288,
  "tools": ["dotnet", "node", "git"],          // selection from the user layer
  "env": {
    "set": { "ASPNETCORE_ENVIRONMENT": "Development" },
    "path": ["...", "C:\\work\\SomeRepo\\.bin"]
  },
  "setup": ["npm ci"],

  // --- elevated: prompts on first use, and again whenever this file changes ---
  "readOnly":  ["S:\\shared\\test-fixtures"],
  "readWrite": ["S:\\scratch\\somerepo-artifacts"]
}
```

First run prints:

```
This project's .airlock.json requests extra host access:
  read-only   S:\shared\test-fixtures        -> C:\airlock\test-fixtures
  read-write  S:\scratch\somerepo-artifacts  -> C:irlock\somerepo-artifacts

The agent can read everything mapped read-only and modify everything mapped
read-write. Allow this for S:\github\SomeRepo?  [y/N]
```
