# mock-rdp

A hand-rolled **mock RDP server** in C#/.NET 10, built from the Microsoft Open
Specifications (MS-RDPBCGR et al.) as a **local test fixture** — so tooling that connects
to RDP servers can be exercised without a real Windows box. Acceptance clients: **mstsc**
and **FreeRDP**. Built incrementally, milestone by milestone; see
`../../.claude/plans/witty-mapping-magpie.md` for the full plan.

## Status

| Milestone | Scope | State |
|-----------|-------|-------|
| M1 | TCP + TPKT + X.224 negotiation + TLS | ✅ done |
| M2 | MCS connect + channel join | ✅ done |
| M3 | Licensing + capabilities + finalization → blank desktop | ✅ done |
| M4 | Graphics (bitmap updates) | ✅ done |
| M5 | Keyboard/mouse input | ✅ done |
| M6 | Clipboard virtual channel (CLIPRDR) | ✅ done |
| M7 | Dynamic virtual channels (DRDYNVC / MS-RDPEDYC) | ✅ done |
| M8 | Drive redirection read/list/write (rdpdr / MS-RDPEFS) + scripted/fault DVCs | ✅ done |

All originally planned milestones are complete: a real RDP client connects end-to-end,
sees rendered graphics, drives the screen with keyboard/mouse, and exchanges clipboard
text over the `cliprdr` channel. Verified against **FreeRDP** and against **mstscax** —
the ActiveX control that is `mstsc.exe`'s own engine — so the mock is mstsc-grade. See
`tools/RdpAxClient/` for the mstscax-based test client.

The mock also speaks the **dynamic virtual channel** layer (`drdynvc`): it advertises
capabilities, opens one or more named DVCs (server-initiated create), and **echoes** any
data sent back on the same channel. This makes it a target for tooling that rides DVCs —
e.g. [RDPeek](https://github.com/guscatalano/RDPeek), whose plugin/agent open
`dvc::diag::inspector`. Choose the channels with `--dvc` (default `ECHO`).

Security: **TLS-only** for now (advertises `PROTOCOL_SSL`); NLA/CredSSP deferred.

## Layout

- `src/MockRdp/` — the server. `Framing/` (TPKT), `X224/` (COTP + negotiation, class `Cotp`),
  `Transport/` (self-signed cert), `Server/` (listener + per-connection state machine), `Util/`.
- `tests/MockRdp.Tests/` — xUnit. `Harness/RdpTestClient.cs` is the growing in-process
  conformance client; `Harness/MockServerFixture.cs` spins up a loopback server per test.
- `scripts/` — real-client checkpoint automation (see below).

## Quick demo

```pwsh
pwsh scripts/demo.ps1
```

Builds everything, runs the per-feature conformance checks, then opens a **live session
with the mstscax client** (mstsc.exe's own engine) — a window shows the mock's colour test
pattern; move the mouse over it to draw markers. Auto-closes after a few seconds. Nothing is
persisted (no cert-store or registry changes).

## Build & run

```pwsh
dotnet test                                   # unit + in-process end-to-end (Tier 1)
dotnet run --project src/MockRdp -- --port 3389 --log-level trace
dotnet run --project src/MockRdp -- --dvc "dvc::diag::inspector"   # open a specific DVC
```

Server flags: `--port <n>` (default 3389), `--bind <ip>`, `--log-level trace|debug|info|warn|error`.

Dynamic virtual channels (`drdynvc`):
- `--dvc <name[,name...]>` — channels to open (default `ECHO`; each echoes by default).
- `--dvc-reply <channel>=<file>` — reply with a file's bytes instead of echoing (feed a
  recorded response so a client plugin gets a valid answer).
- `--dvc-fault <channel>=<fragment|drop|close|truncate|delay>` — inject a fault to exercise
  a client plugin's resilience.

Drive redirection (`rdpdr` / `\\tsclient`), each takes client paths:
- `--rdpdr-read <path[,...]>` — read files back from the client, logging bytes + sha256
  (capped per file).
- `--rdpdr-list <dir[,...]>` — enumerate a redirected directory.
- `--rdpdr-write <path[,...]>` — write a small marker file to the client.

Fake desktop (experimental — a software-rendered, interactive Windows-like session):
- `--desktop` — render a fake desktop instead of the colour test pattern, with a tiny window
  manager: the **Start** menu launches cascading windows, windows are **draggable** by their
  title bar (with z-order) and closable via **×**, and only changed tiles are resent
  (dirty-rect). **File Explorer** browses an in-memory filesystem (`C:\Windows`, `C:\Users`,
  …) — click folders to navigate, `..` to go up, a file to open it in **Notepad**. Press
  **Ctrl+Alt+End** (the remote secure-attention sequence) to switch to the **secure /
  Winlogon desktop**; **Esc** returns. Mirrors Windows' Default vs. Secure desktops.
- `--screenshot <path.png> [--secure] [--start-menu] [--demo]` — render one frame of a chosen
  state to a PNG and exit (no server); `--demo` launches a couple of windows first.

## CI & prebuilt binary

`.github/workflows/ci.yml` builds and tests on every push/PR and publishes a **self-contained
single-file `MockRdp.exe`** (win-x64) as the `mockrdp-win-x64` workflow artifact — runnable
with no .NET runtime installed. Tagging `v*` (or running the Release workflow) attaches the
same exe to a GitHub Release, so consumers can fetch a stable URL:

```
https://github.com/guscatalano/MockRDPServer/releases/latest/download/MockRdp.exe
```

This is what a downstream project (e.g. RDPeek) downloads to spin up a real DVC-capable RDP
target in its own integration tests.

## Real-client checkpoints (automation)

Two complementary tiers verify each milestone:

- **Tier 1 — conformance client** (`RdpTestClient`): runs in `dotnet test`, decodes the
  server's PDUs and asserts. Fast, headless, no external deps.
- **Tier 2 — FreeRDP** (`scripts/`): drives real `wfreerdp` against the mock and asserts how
  far the connection sequence got by parsing its TRACE log.

```pwsh
pwsh scripts/run-checkpoint.ps1 -ExpectStage tls   # build + start + FreeRDP + teardown
```

`-ExpectStage` is one of `tcp|x224|tls|mcs|capabilities|active`; each milestone raises the
bar. FreeRDP portable is installed via `choco install freerdp.portable`.
