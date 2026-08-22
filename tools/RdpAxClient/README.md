# RdpAxClient — an mstsc-equivalent built on mstscax

A minimal RDP **client** that hosts the **mstscax** ActiveX control (`MsTscAx.MsTscAx`, i.e.
"Microsoft RDP Client Control") — the exact protocol/rendering engine `mstsc.exe` uses. It
connects to a target, reports whether the session becomes active, and exits 0/1 accordingly.

Its purpose here is to test the mock RDP server against an **mstsc-grade** client, which is
stricter than FreeRDP on capability negotiation. When this connects, the mock is validated
against Microsoft's own RDP stack — mstscax completes MCS, licensing, capability exchange
(it accepts the mock's Demand Active), and finalization.

## Run

```pwsh
# Build + start the mock + connect the mstscax client (the full acceptance test):
pwsh tools/RdpAxClient/test-against-mock.ps1

# Or run the client directly against any RDP server:
tools/RdpAxClient/bin/Debug/net10.0-windows/RdpAxClient.exe --server localhost --port 3389 --user u --pass p
```

Flags: `--server`, `--port`, `--user`, `--pass`, `--timeout <sec>`, `--auth-level <0|1|2>`.

## How it works

- **Hosting**: `AxHost` sites the ActiveX control in a WinForms window; the control is driven
  by **late binding (`dynamic`)**, so no generated COM interop assembly is needed. (`dotnet
  build` can't run the `ResolveComReference` task — MSB4803 — so a `COMReference`/`tlbimp`
  approach won't build; late binding sidesteps that.)
- **Security**: it requests the negotiated security layer (`AuthenticationLevel = 2`), so the
  control offers `PROTOCOL_SSL` and completes TLS with the mock. `AuthenticationLevel = 0`
  would skip TLS entirely and request only standard RDP security — which the TLS-only mock rejects.
- **Self-signed cert**: the mock uses a self-signed cert. The tool auto-accepts the RDP client's
  transient "certificate not trusted" warning by clicking its Yes button (see `Native.cs`).
  This is UI handling only — **nothing is persisted**: no certificate-store or registry changes
  (analogous to FreeRDP's `/cert:ignore`).

## On "trusting" the cert (research notes)

For a self-signed **server** cert, "trust" means placing it in a **Trusted Root** store —
SChannel/mstscax won't trust it anywhere else. The relevant knobs, from least to most intrusive:

| Mechanism | Location | Notes |
|-----------|----------|-------|
| Transient warning dismiss | (none) | What this tool does. Persists nothing. |
| `AuthenticationLevelOverride` | `HKCU\Software\Microsoft\Terminal Server Client` (DWORD) | `0/1/2`; HKCU overrides HKLM. **But `0` = "no auth" disables TLS** (requests standard RDP), so it does *not* give "TLS + ignore cert". |
| `CertHash` per-server | `HKCU\…\Terminal Server Client\Servers\<name>` | mstsc.exe's "don't ask again" thumbprint. The **raw ActiveX control ignores it**; only mstsc.exe honors it. |
| GPO "Configure server authentication for client" | `…\Software\Policies\Microsoft\Windows NT\Terminal Services` → `AuthenticationLevel` | Policy-based equivalent of the per-connection level. |
| CurrentUser Root store | `Cert:\CurrentUser\Root` | Real trust, per-user, reversible — but the *add* triggers Windows' security prompt (by design; can't be suppressed for CurrentUser Root). |
| LocalMachine Root store | `Cert:\LocalMachine\Root` | No prompt, but needs admin and is system-wide. |

Conclusion: there is **no registry/GPO setting that means "use TLS but skip validation of a
self-signed cert"** — with TLS on, the cert is validated, so it's either trusted (Root store,
which prompts or needs admin) or the per-connection warning is accepted. This tool takes the
latter, zero-persistence route.
