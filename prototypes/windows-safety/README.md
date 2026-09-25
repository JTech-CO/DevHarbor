# DevHarbor Windows safety experiments

Production deletion is disabled. This is a Windows-only API experiment, not a cleaner application or a security boundary against malicious local code.

Run from PowerShell 7 on Windows:

```powershell
./prototypes/windows-safety/Run-Probes.ps1
./prototypes/windows-safety/Run-Probes.ps1 -IncludeRecycle
```

The second command creates a small synthetic payload, verifies a pre-delete veto, sends that payload to the Windows Recycle Bin, and restores it in a fresh STA process. It does not empty the bin or enumerate/delete existing user items. SHA-256 verifies restored bytes. A collision fixture remains intact and recovery goes to a separate filename. Generated files are intentionally retained under `runs/` for inspection; no recursive cleanup is performed.

`latest-results.json` is the most recent report, including failures. Each run also has its own immutable-location report. Do not count negative tests alone as proof: the valid-path positive controls must also pass.

The shell callback rejects deletion without `TSF_DELETE_RECYCLE_IF_POSSIBLE`; that flag is not a universal guarantee for every filesystem or recycle policy. Unsupported-policy and disk-full tests remain required.

For a manual, non-destructive approval dialog:

```powershell
./prototypes/windows-safety/Show-ApprovalDemo.ps1
```

The automated tests invoke `DecideFromTestUI` as a test double. This public method is deliberately unsuitable for production authorization. The manual dialog never deletes anything. Human interaction, named-pipe ACLs, MCP integration and crash-persistent approvals are not validated by these tests.

`BoundaryLease` holds ancestor handles without delete sharing and a read-only leaf handle without write/delete sharing. It rejects reparse/offline/recall paths, hardlinks, directories and non-local paths. It supports single regular files only. The leaf handle must be closed for a Shell recycle operation; the resulting check-to-use race remains unresolved. **Do not connect this prototype to real cache cleanup.**

The JSON recycle manifest is experimental: it has no transactional crash recovery or tamper protection. Restore currently checks an existing destination before calling Shell, not atomically. Manifest and payload validation prevent accidental mixups in a controlled run; they are not a production security mechanism.

If a sandbox denies opening an ancestor directory, valid-path probes fail rather than weakening validation. An authorized run outside that sandbox may be needed; this does not imply the product requires an administrator account.
