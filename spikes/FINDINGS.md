# Spike findings

Host: Windows 11 Pro 26300 · Windows Sandbox app 0.8.107.0 · CmService Running

## wsb CLI behaviour (confirmed)

| Finding | Detail |
|---|---|
| **`--config` takes INLINE XML ONLY** | A file path is rejected with `The configuration file was invalid.` (exit `-2146232000`). The brief was right and the first plan draft was wrong: there is no `.wsb` file path form for `wsb start`. |
| `--raw` on `start` | Returns `{ "Id": "<guid>" }`. No need to scrape the `Id:` line. |
| `--raw` on `list` | Returns `{ "WindowsSandboxEnvironments": [ ... ] }` — empty array when none running. |
| Unknown/miscased elements are ignored | `<VGpu>` (wrong case) was accepted with exit 0, as was the documented `<vGPU>`. Silent acceptance means a typo'd element is **silently dropped**, so a miscased `vGPU` would leave vGPU *enabled* and trigger the black-screen bug. **Always emit the documented casing** and treat "it started" as no evidence the setting applied. |

### Consequence for the plan

`SandboxConfigWriter` must produce a **single-line XML string** passed as one argv element to
`wsb start --config`, not a file. Keep writing the `.wsb` to the session dir anyway — purely as a
debugging artifact for `--dry-run` and failure reports.

Because unknown elements are silently ignored, add a `SandboxConfigWriter` unit test that asserts
exact documented element names and casing:
`vGPU, Networking, MappedFolders/MappedFolder/{HostFolder,SandboxFolder,ReadOnly}, LogonCommand/Command,
AudioInput, VideoInput, ProtectedClient, PrinterRedirection, ClipboardRedirection, MemoryInMB`.

## Volumes on this host

| Drive | Label | FS |
|---|---|---|
| C | Windows-SSD | NTFS |
| S | Source | **ReFS** ← repo + `s:\packages\nuget` live here |
| H | Games | NTFS |

## Sandbox lifecycle spikes - all resolved

| # | Question | Result | Impact |
|---|---|---|---|
| **S1** | Does a **ReFS** `HostFolder` map? | **YES.** `S:\github\Airlock` (ReFS) mapped to `C:\work\Airlock`; `LICENSE` visible inside. NTFS control also fine. | **The plan's #1 risk is dead.** No copy-in/sync-back mode needed. Demote the non-NTFS warning to informational. |
| **S3** | Is `wsb start --id <guid>` honored? | **YES.** The caller-chosen GUID came back from `start --raw` and `list --raw`. | Write the session state file **before** `wsb start`. The orphan crash-window disappears entirely. |
| **S4** | Does `wsb exec` propagate the remote exit code? | **NO.** `cmd.exe /c exit 7` -> wsb exit code `0`. | **The exit-code status channel is dead.** wsb's exit code reports only whether *wsb* could dispatch, not what the command did. Remove it as a fallback. |
| **S4b** | Does `wsb exec` block until the command exits? | **YES.** An 11-second ping took 11.5 s. | We know when `setup.ps1` *finished* - just not whether it *succeeded*. Combine with S9 for the verdict. |
| **S9** | Does `wsb share -w` work on a **running** sandbox? | **YES.** Attached `C:irlock\out` mid-session; the sandbox wrote `probe.txt`, the host read `hello-from-sandbox` back. | The provisioning-status and log-retrieval channel - attachable **on demand**, so "the project is the only writable host folder" holds for the whole normal session. |
| **S8** | Can two sandboxes run at once? | **NO.** Second `wsb start` -> `Application cannot be run more than once (0x800401F6 CO_E_APPSINGLEUSE)`. | **Windows Sandbox is single-instance.** See below. |

### Timings (warm snapshot)

`wsb start` blocks ~7.4 s and returns once the sandbox is already `exec`-ready (the readiness poll
succeeded 1.2 s later). The 10-minute budget applies only to a cold first-boot-after-Windows-update.

## Consequences for the design

**1. Single-instance changes the concurrency story.**
- `airlock list` degrades to "the one session, if any" plus orphan reaping.
- `airlock` must **fail fast** when a sandbox is already running - *including one the user started
  themselves* from the Windows Sandbox app, which Airlock did not create and must not kill.
  Message: name the id, explain Airlock needs the single Sandbox instance, offer `airlock stop`.
- Multiple *terminals* into one session still work over SSH (`airlock shell` in another tab), which
  recovers most of the multi-session UX and is a further point for SSH over a GUI window.

**2. Provisioning status.** exec blocks but returns no status, so the verdict sequence is:
`wsb exec` returns -> poll TCP 22 -> `ssh -o BatchMode=yes ... exit` returns 0 => success.
On timeout, *then* `wsb share -w` an out dir, copy `setup.log` + `ready.json` out, report the phase.

**3. `SandboxConfigWriter` emits a single-line string** passed as one argv element to
`wsb start --config`. Still write the `.wsb` into the session dir as a `--dry-run`/diagnostic artifact.

## Provisioning + SSH spikes — all resolved

Driver: `s2-ssh.ps1`, provisioning script `setup.ps1` (both in this folder; setup.ps1 is essentially
the shippable version).

| # | Question | Result |
|---|---|---|
| **S2** | Portable OpenSSH + `administrators_authorized_keys` key auth | **WORKS.** `ssh airlock@<ip> whoami` -> `<host>\airlock`, exit 0. The release zip's `install-sshd.ps1`, an `icacls /inheritance:r /grant *S-1-5-32-544:F /grant *S-1-5-18:F` on the key file, and starting sshd last are all load-bearing. |
| **S5** | `SendEnv` / `AcceptEnv` secret forwarding | **WORKS.** Value set into `ssh.exe`'s own environment arrived intact in the guest. The secret touches no file and no command line. |
| — | Read-only mapped .NET SDK usable | **YES.** `dotnet --version` -> `10.0.300` over SSH, with `DOTNET_ROOT=C:\airlock\dotnet` from machine env. |
| — | `-EncodedCommand` through ssh | **WORKS.** Round-tripped a prologue that set and printed the working directory -> `C:\work\Airlock`. |
| — | **Writability invariant** | **HOLDS.** `C:\work\Airlock` WRITABLE; `C:\airlock\tools`, `C:\airlock\session`, `C:\airlock\dotnet` all DENIED. |

### Provisioning cost

74–133 s wall clock for `setup.ps1` on a warm snapshot (the spread is mostly `install-sshd.ps1` and
service start). Boot before it is ~1 s warm. So a warm session is roughly **1.5–2.5 minutes** to
first prompt, and the progress UI needs to carry that even in the common case — not just the cold
10-minute one.

### A raw remote command does not survive the quoting layers

Sending `try { ... } catch { ... }` as the ssh remote command produced
`DENIED : The term 'DENIED' is not recognized` — the guest shell re-parsed it. Host argv -> ssh's
remote-command concatenation -> `DefaultShell` + `DefaultShellCommandOption` is three layers of
quoting. **Always send `-EncodedCommand` (base64 UTF-16LE).** This is not a nicety; the naive form
is actively broken.

### Network posture — the RFC1918 block needs an exemption

| | |
|---|---|
| Sandbox IP | `172.22.159.234` |
| Default gateway (the host) | `172.22.144.1` |
| Internet from the guest | reachable |
| **Host reachable from the guest** | **yes** |

The sandbox's own subnet and gateway sit **inside `172.16.0.0/12`**, so the planned blanket
outbound block of `10/8, 172.16/12, 192.168/16` would sever the sandbox's own gateway, DNS, and the
SSH return path. The rule must:

1. resolve the guest's own interface prefix and default gateway at provisioning time, and
2. emit `-Action Allow` rules for those **before** the block rules,

or scope the block to the host's real LAN prefixes only. `blockLan` must be verified from inside the
guest after the rules are applied (internet still reachable, host LAN not), not assumed.

## `wsb connect` (S11)

| Question | Result |
|---|---|
| Does `wsb connect --id <id>` open the sandbox desktop? | **Yes.** `WindowsSandboxRemoteSession` appears with the window title "Windows Sandbox". |
| Which account does the desktop session run as? | **`WDAGUtilityAccount`** - Windows Sandbox's own default user, *not* the `airlock` account SSH sessions use. |
| Does the call block until the window closes? | No, it returns while the window stays open. |

### The desktop is a different Windows session from the agent's

`whoami` in the desktop session returns `<host>\wdagutilityaccount`; SSH sessions return
`<host>\airlock`. Two separate accounts with separate profiles, so **a browser sign-in or a
per-user install done in the window does not carry into the agent's session**. Mapped folders are
shared between them, which is what makes the window useful for inspecting files and debugging a
sandbox that came up wrong - but it rules out the "log in via the GUI so the agent is authenticated"
idea. Credentials still have to arrive over SSH via `SendEnv`.

### Do not redirect the launcher's streams

Launching `wsb connect` with `RedirectStandardOutput`/`RedirectStandardError` kept it attached to
Airlock and made `airlock connect` take **~32 s** to return. Launching it detached
(`UseShellExecute = true`, no redirection) returns promptly. A GUI launcher has nothing useful to
say on stdout, so nothing is lost.

### `-r ExistingLogin` depends on uptime, not just on a client

The brief states `ExistingLogin` fails without an attached client. On a **freshly provisioned**
sandbox that is exactly what happens (`Failed to start process in Windows Sandbox environment`), and
it starts working within seconds of `connect`. But on a sandbox that had been up ~15 minutes it
**succeeded with no client ever attached**. So the rule is weaker than "needs a client". Airlock
uses `-r System` for everything and depends on `ExistingLogin` nowhere.


## The remote exit code is eaten by sshd's shell wrapper (S12)

`airlock -- powershell -Command "exit 42"` returned **1**. The cause is not ssh and not Airlock:
sshd runs a remote command as `DefaultShell -Command "<the command>"`, and that outer PowerShell
exits **0 or 1** for success or failure rather than passing on the exit code of what it ran. The
success case looks correct by coincidence, which is what makes this easy to miss.

Confirmed by isolating it: the exact base64 script, run locally under Windows PowerShell 5.1,
returns 42. Only the ssh path lost it.

**Fix.** Append a second statement to the remote command so the wrapper exits deliberately:

    powershell.exe -NoLogo -NoProfile -EncodedCommand <b64> ; exit $LASTEXITCODE

ssh joins remote-command arguments with spaces and does no quoting of its own, so passing `;`,
`exit` and `$LASTEXITCODE` as separate arguments arrives at the guest shell as a second statement.
Verified: `exit 42` now returns 42, and 0 still returns 0.

## CShell cannot make the interactive hand-off (S12)

Everything Airlock launches goes through CShell - `wsb` and `icacls` and `ssh-keygen` via `Run`,
`wsb connect` via `Start` - with exactly one exception, established by measurement rather than
theory.

The interactive ssh session must inherit the real console handles, since that is what gives both
ends a ConPTY and lets a full-screen TUI draw. MedallionShell, underneath CShell, attaches its own
readers to the child's streams, and that is incompatible with leaving them unredirected. Routed
through `Run(opt => opt.StartInfo(psi => psi.RedirectStandardOutput = false ...))` the session
produced **no output at all** and reported **exit code 1** for a remote `exit 42`. The same call via
`Process.Start` with all three streams unredirected produced the output and the correct code.

`Start()` is not an alternative: it forces `UseShellExecute = true`, which spawns a *new* console
rather than inheriting the caller's.

## Identifying our own sandbox (S12)

A sandbox's mounts cannot be inspected from the host - `wsb list` returns only ids, and `wsb exec`
returns neither output nor the remote exit code, so "does `C:irlock	ools` exist" is
unanswerable from outside. The **SSH key is the identifier instead**, and a better one: the keypair
is generated per sandbox and its public half is written only into that sandbox's
`administrators_authorized_keys`, so a successful login proves *this* Airlock install provisioned
*that* sandbox.

Verified: with `sandbox.json` deleted while a sandbox was running, `airlock list` recovered the same
id by signing in. Attached folders cannot be recovered - nothing on the host records them - so the
rebuilt record is flagged and `airlock list` says so instead of showing an empty table.
