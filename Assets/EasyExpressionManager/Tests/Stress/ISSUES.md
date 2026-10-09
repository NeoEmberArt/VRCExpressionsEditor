# EEM adversarial Unity MCP test log

Tested 2026-10-08 in the running Unity 2022.3 editor, using Unity MCP `execute_code` and the installed VRChat/VRCFury assemblies. Tests used isolated preview scenes, synthetic menu assets, and disposable prefab instances. Production code was not changed during this test pass.

## Confirmed issues

### EEM-STRESS-001: Reused submenus produce exponential scan work

Severity: high for pathological graphs. Status: open.

Create a chain of menu assets where every level contains two submenu controls pointing at the same next asset. The last asset contains one toggle. Scan the first menu with EEM.

| Depth | Unique assets | Displayed rows | Scan time |
| --- | ---: | ---: | ---: |
| 10 | 11 | 3,070 | 72 ms |
| 12 | 13 | 12,286 | 286 ms |
| 14 | 15 | 49,150 | 1,521 ms |
| 16 | 17 | 196,606 | 6,311 ms |

At depth 16 the measured managed memory increase during scanning was approximately 257 MiB. This is an approximate GC measurement, not a retained-memory or leak measurement.

`BuildVRCMenu` in `Editor/Window/Scan/EEM_Scan.Native.cs` correctly terminates cycles using a per-branch visited set, but expands every acyclic reuse. A very small asset graph can therefore stall the editor. Scanning, stable-key assignment, path calculation and tree analysis all run synchronously. Further expansion was stopped after demonstrating the stall; there is no tested guarantee at greater depth.

Suggested change: a displayed-node/depth budget with a clear warning, and lazy expansion or cached graph traversal that preserves distinct paths.

Evidence: `adversarial-results.json`. Reproduction: `adversarial-stress.json`.

### EEM-STRESS-002: Repeated moves accumulate expensive processing

Severity: medium. Status: open.

Create four native folders with five uniquely named toggles each. Duplicate the first folder, producing five folders and 25 toggles. With random seed 1847, repeatedly move a random toggle into a random folder, scanning before and after each operation.

| Completed requests | Cumulative time | Time for latest 10 |
| ---: | ---: | ---: |
| 10 | 188 ms | 188 ms |
| 30 | 1,258 ms | 668 ms |
| 50 | 4,195 ms | 1,630 ms |
| 70 | 10,965 ms | 4,283 ms |
| 80 | 16,493 ms | 5,528 ms |
| 90 | 27,413 ms | 10,920 ms |
| 100 | 38,152 ms | 10,739 ms |

These are operation-plus-scan timings, not isolated move timings. Some requests are moves to the current folder and become no-ops. The window remains correct, but the same small menu becomes substantially slower after editing. A separate 100-request run also took approximately 38 seconds. Subsequent Dissolve operations completed and preserved every toggle, but kept the editor busy long enough for the MCP connection to time out.

The implementation repeatedly simulates moves, scans source trees and orders records (`CommitMove`, `BuildMoveSourceTree`, `OrderMovesByDependency`). Growing history is a likely contributor based on code inspection; this pass did not profile individual functions or prove a specific complexity bound.

Suggested change: profile and reduce repeated tree simulations; cache reusable source state and safely compact equivalent move histories while preserving sequential moves and merged-folder semantics.

Evidence and reproduction: `ramp-results.txt`, `ramp-stress.json`, `prepare_ramp.py`.

### TEST-INFRA-001: Long synchronous editor work can lose the MCP response

Severity: medium for automation. This is an integration observation, not a confirmed EEM data-loss bug.

Long test commands returned `success:false` with no message while their C# code continued running. The editor log recorded WebSocket disconnections. MCP execution history confirmed the first 100-move command completed, and `ramp-results.txt` confirmed the later 100-move plus five-Dissolve run finished successfully.

Treat a missing response as an unknown outcome. Wait for editor recovery and inspect checkpoints before retrying mutations. Test scripts that write checkpoints must remain inside the test directory. Shorter batches avoid this ambiguity.

## Passed checks

- Native shared menu rename, icon assignment and deletion isolate another avatar's source asset.
- Repeated submenu references still point at the same private submenu after cloning; both displayed aliases reflect the rename.
- Self-referencing menus terminate scanning and preserve their cycle when cloned.
- A 2,048-control menu scanned in 11 ms; a depth-128 chain scanned in 23 ms.
- A stale action is refused; its requested mutation is not applied.
- An injected mid-operation exception returns failure and restores the recorded edit through Undo.
- Restore after a simulated package update restores old values into a private copy and leaves the updated shared menu unchanged.
- Full Controller rename leaves its shared source menu unchanged.
- Duplicating a native folder creates an independent submenu graph.
- A deterministic 100-move sequence preserves the requested destination at every iteration; the corrected prefab harness also passed a separate 25-move run.
- Dissolving all five folders after 100 moves removes each folder while retaining all 25 toggles.
- A toggle rename preserves the outer avatar prefab and its nested clothing prefab; the new name persists after rescanning.
- Twenty-five automatic snapshots rotate down to ten; baseline and named saves remain (12 files total).
- Null control entries and null names do not break scanning or snapshot capture.
- Upload preprocessing returns true for a null avatar, an avatar without a manager, and cyclic move records.
- With `DebugJSON` disabled in the test helper's settings cache, rename succeeds and no developer verification entries are recorded. The project's settings file was not edited; its current value is `true`.

## Scope and cleanup

This pass exercises live editor code and its underlying mutation methods through reflection. Confirmation dialogs are bypassed in the harness. It does not perform a real upload, a complete VRCFury build, Gesture Manager play-mode entry, graphics/icon rendering, a domain reload, or every bulk-operation permutation. It cannot prove the plugin impossible to break.

A first prefab-harness check assumed the nested prefab instance retained the name `NestedClothing`. Unity named the saved instance differently, causing a harness-only null reference. The check was corrected to inspect the actual child instance; the revised run passed all prefab assertions. The null reference is not counted as an EEM defect.

Preview scenes were closed in `finally` blocks. Disposable generated assets, test snapshots and remaining test avatar-state assets were removed through MCP. Test definitions and result files remain for reproduction. No production menu asset was edited by these simulations.

To rerun, use the JSON code requests with Unity MCP `execute_code`, or adjust `mcp_runner.py` to a current MCP session. Run the `prepare*.py` scripts to regenerate requests. Requests intentionally allow deletion only to clean up the synthetic test assets; review the fixed test paths before running in another project.
