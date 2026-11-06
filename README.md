# DesktopTestDriver

[한국어 README](README_kr.md)

**DesktopTestDriver reduces repetitive UI searches, waiting, retry handling, and cleanup in Windows GUI E2E tests.** When a coding agent has to locate buttons from scratch, wait a fixed duration, and guess whether an action ran after losing its response, tests become slower and less reliable. DesktopTestDriver reuses validated UI references, waits for expected conditions, and retrieves original execution records to **reduce unnecessary work and support more accurate test verdicts**.

Target checks, duplicate prevention, remote requests, and owned-process cleanup become shared capabilities instead of infrastructure you rebuild for each test. Your coding agent or test runner can focus on **which user flows to test and what counts as success**. The same request interface works on local and remote Windows PCs.

![DesktopTestDriver benefits: six before-and-after comparisons, condition-based waiting, and shared test infrastructure](docs/assets/e2e-benefits-infographic-en.png)

[GUI test guide (Korean)](docs/gui-test-guide.md) · [Command, setup, and API reference (Korean)](docs/agent-usage.md)

## What changes when you use it?

This comparison assumes manual screen/coordinate operations or individual scripts without an established target-validation, waiting, and recovery layer. Other automation tools or custom code can provide these capabilities too. DesktopTestDriver helps reduce the effort of implementing and maintaining that shared foundation yourself.

| Test activity | Manual operations / individual scripts | With DesktopTestDriver |
|---|---|---|
| Find buttons and fields repeatedly | Interpret the screen or search the entire UI at each step | Validate and reuse element references; search only the relevant region |
| Wait for the app to finish | Long fixed waits waste time; short waits may run the next step too early | Check the expected value or window and continue when the condition is met |
| Act after the UI changes | Check whether saved coordinates or identifiers still point to the right target | Validate process, window, and element identity; reject stale references and ambiguous targets |
| Recover from a lost response | Track whether the action ran and whether retrying is appropriate | Query the original result by request ID; a retained request with the same ID and content is not executed again |
| Test another PC | Connect remote execution, screen retrieval, and result delivery yourself | Set up Controller and Agent, select a machineId, and retrieve UI data and screenshots |
| Prepare for the next test | Write cleanup for leftover apps, child processes, and held input | Stop session-owned apps and children, release tool-owned input, and report cleanup results |

For example, a script that always waits five seconds after clicking Save consumes that time even when saving finishes earlier. A condition wait can continue as soon as it observes the completion indicator. The test must still reopen the file and check its contents. **Reducing waiting does not mean skipping verification.**

The tool is especially useful for repeated runs, frequent UI changes or lost responses, and tests across multiple Windows PCs. If your automation already has equivalent protection and recovery, evaluate the benefit against that setup. Actual time savings depend on the app and environment. The caller defines the test sequence and final success criteria: `completed` means the tool request finished, so verify product outcomes through UI, files, or server results separately.

API 2 uses UI Automation (UIA) for semantic GUI actions. Tests of actual keyboard and mouse input use an explicit input policy. UIA success does not validate the real keyboard/mouse path. Unsupported UIA actions never silently fall back to global input.

## Recommended test workflow

1. **Define success.** Specify the expected UI, saved content, or server result and an overall test deadline.
2. **Check the environment and target.** Use `--status` and `--doctor` to inspect the Agent, user session, and supported operations. Specify the target process scope.
3. **Find the necessary elements.** Reuse the `elementId` and `snapshotToken` returned by API 2 `find-element`. Search for new items within the relevant panel.
4. **Act and wait for a condition.** Follow `set-value` or `invoke` with `wait-condition` or `wait-window`. Each request is limited to five seconds; longer readiness waits use new read requests within the caller's overall deadline.
5. **Verify product results.** Read UI values and, where needed, screenshots, files, and logs. Successful request delivery alone is not a passing test.
6. **Keep evidence and clean up.** Preserve failures and uncertain outcomes. Clean up files, apps, sessions, Agents, and Controllers started for this test. Preserve pre-existing processes.

Use screenshots when visual inspection is necessary and UIA for repeated value checks. Use existing product tools for file, server, and external API validation. DesktopTestDriver Core does not contain product-specific business APIs or a complete scenario runner.

## Local and remote use

| Program | Location | Role |
|---|---|---|
| `Controller.exe` | The coding agent's or test runner's PC | Route requests to the target Agent and collect results and images |
| `Agent.exe` | The Windows PC running the target app | Observe and operate the GUI in the logged-in user's desktop |

For local use, run both programs on the same PC and connect through `127.0.0.1`. For remote use, run an Agent on each target PC and select it by `machineId`. Certificate registration and target/session checks apply to both configurations.

![Example architecture connecting to a separate Windows test PC](docs/assets/architecture-en.svg)

This diagram illustrates remote use. A separate PC is optional.

| Environment | Requirements |
|---|---|
| PC shared with other work | Use the default `shared` policy. Background UIA mutations require an application profile and per-action non-interference qualification |
| Real keyboard/mouse tests | Use `foreground-exclusive` with a valid input lease. Run without competing user input |
| Dedicated test environment | Use Agent `machineUsage: dedicated` and `isolated-desktop`. The policy does not create a separate desktop or VM |
| Console / RDP | Run in a standard user's active console or connected active RDP session. Locked, disconnected, secure-desktop, and elevated execution are unsupported |

## Quick start

### 1. Build and deploy

On a Windows 10/11 x64 PC with the .NET 10 SDK:

```powershell
.\build.ps1 -Package
```

Output is created under `bin/live_Release`. Runtime PCs need the .NET 10 Desktop Runtime x64. The package includes `Controller/`, `Agent/`, `Client/`, and `test-apps/`; role folders include documentation, schemas, and standalone examples.

### 2. Connect and inspect

Follow [initial setup and persistent processes (Korean)](docs/agent-usage.md#최초-설정과-상주-실행) to create separate state directories, exchange/register public certificates, and start Controller and Agent. For remote use, also configure the Controller's listening address and TCP port access.

```powershell
.\Controller.exe --status
.\Controller.exe --doctor --machine test-pc
```

Replace `test-pc` with a registered `machineId`. To inspect an existing app, prepare a target-scope JSON array containing its PID, creation time, executable path, and SHA-256, then run the bundled example:

```powershell
.\examples\standalone\Inspect.ps1 -Controller .\Controller.exe -Machine test-pc -TargetScope C:\GuiTest\scope.json
```

The example opens an API 2 observation session, reads the target UI, and closes the session. See the [usage reference (Korean)](docs/agent-usage.md) for interaction policies and request examples, and the [GUI test guide (Korean)](docs/gui-test-guide.md) for a memo save-and-reopen walkthrough.

### 3. Give your coding agent a test

> The Controller is `C:\DesktopTestDriver\Controller\Controller.exe`; the target machineId is `test-pc`. Read the usage reference and test entering text, saving, closing, and reopening `C:\GuiTest\SimpleMemo.exe`. Pass only if the reopened content matches. Set an overall deadline and use supported UIA operations and condition waits. Explicitly select the policy for steps requiring real input. On failure, retain request IDs and observation evidence; do not repeat an action whose execution is uncertain. Clean up files, processes, and sessions created for this test. Report verified results separately from unverified items.

## Validation scope and performance

```powershell
# Build from source.
.\build.ps1 -Configuration Debug
# Build Release packages and run local contract and transport checks.
.\verify.ps1
```

Local checks do not establish that real keyboard/mouse input or a product E2E scenario across two physical PCs passes. UIA tests and real-input tests are separate. UIA does not support every app or control. Capture may reject occluded or minimized windows, and HDR color accuracy is not guaranteed.

Working-tree validation on 2026-11-06 passed 60 local tests and two real Windows UIA tests using FormsWorkbench and SimpleMemo. The UIA tests observed zero global input events and zero target foreground transitions. In one synthetic 4,001-node parent-selector measurement, the sequential reference took 100.54ms and indexed lookup took 0.40ms. This is a single measurement of a specific search operation, not an overall E2E speed guarantee. That validation did not include two physical PCs, real input, or concurrent human use.

Local evidence is stored in `artifacts/e2e-audit-local.txt`, `artifacts/e2e-audit-ui.txt`, and `artifacts/ui-tests/e934cab8f20448279a286cb47e4a909b/`. These generated artifacts are not included in a fresh clone. In your environment, measure setup, actions, condition waits, evidence collection, cleanup, failures, and retries separately.

<details>
<summary>Repository layout</summary>

| Path | Contents |
|---|---|
| `src/Controller/`, `src/Agent/` | Request routing and Windows GUI executables |
| `src/Client/` | C# client API |
| `src/Core/`, `src/Contracts/` | Transport, UIA, input, cleanup, and request/response contracts |
| `fixtures/`, `tests/` | Test apps and automated checks |
| `schemas/`, `examples/standalone/` | DTO schemas and standalone examples |
| `scripts/`, `docs/` | Operational scripts and documentation |
| `bin/`, `artifacts/` | Generated distribution output and validation evidence |

</details>

## License

[MIT License](LICENSE).
