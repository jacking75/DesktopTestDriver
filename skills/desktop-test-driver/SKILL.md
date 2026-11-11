---
name: desktop-test-driver
description: Test or operate Windows desktop GUIs (WinForms, WPF, WinUI, Win32 apps, Settings, Control Panel, .msc consoles) end to end through UI Automation with DesktopTestDriver — MCP tools gui_session/gui_window/gui_observe/gui_act/gui_wait/gui_capture, or Controller.exe on the command line. Use when asked to run, click through, verify, regression-test or explore a Windows GUI; prefer it over screenshot-and-coordinate computer use.
---

# DesktopTestDriver

Find UI elements by ID or name, act on them, and read values as strings. No screenshots or coordinates are needed.
The connection is not encrypted: use it only on trusted PCs and networks.

## 0. Check the setup first

1. The `gui_*` tools exist: use them (sections 1-6). They do not exist: use the CLI (section 7).
   To register the MCP server: `claude mcp add desktop-test-driver -- <path>\Controller.exe --mcp`
   (it starts its own controller host if none is running).
2. `gui_session {action:"agents"}` (CLI: `Controller.exe --status`) must list a connected agent.
   If none is connected, `Agent.exe` is not running. It must run inside the logged-on desktop of the target PC.
   On the same PC you may start it yourself: `Start-Process <path>\Agent.exe`. On a remote PC, ask the user to run
   `Agent.exe --controller <controller-ip>:25001 --machine <name>` there.
3. When something odd happens, run `gui_session {action:"doctor"}`. It checks UIA, capture, the input hook, storage,
   elevation, and blocked sessions.

## 1. Request shape (common mistake)

The operation fields go in `arguments`. `timeoutMilliseconds`, `requestId` and `note` are tool parameters, not arguments.

```json
{"sessionId":"control-…","operation":"wait-condition","timeoutMilliseconds":15000,
 "arguments":{"windowId":"w1","selector":{"automationId":"Status"},"condition":"name-contains","expected":"완료"}}
```

Each tool description lists the arguments of its operations. An argument error repeats the expected arguments of
the operation, so read the error message and fix the request.

## 2. Core loop

1. **Open.** `gui_session {action:"open", launch:"C:\\path\\App.exe", launchArguments:[…]}`.
   It returns `controlSessionId` and `data.launch.window.windowId`, and the app is stopped when the session closes.
   - Already running apps: `targets:{pids:[…]}`, `targets:{windowTitleContains:"…"}` or `targets:{executables:["C:\\…exe"]}`.
   - Windows' own GUI: open a plain session, then `gui_window open {target:"ms-settings:about"|"control.exe"|"services.msc",
     waitWindow:{className:"ApplicationFrameWindow"|"CabinetWClass", timeoutMilliseconds:15000}}`.
2. **Observe.** `gui_observe inspect {windowId, maxDepth:6, interactiveOnly:true}`:
   ```
   - Window "간단 메모" [e1 t=167cdf32baf7]
     - Button #SaveButton "저장" [e4 t=1d93122bc0be] patterns=Invoke
     - Edit #MemoText [e5 t=b2d40c2611bd] value="" patterns=Value
   ```
   - `#` is the automationId, `e5` is the elementId and `t=` is the snapshotToken.
   - `patterns` tells you what the element supports: Invoke→invoke, Value→set-value, Toggle→set-toggle,
     SelectionItem→select-item, ExpandCollapse→set-expanded, RangeValue→set-range-value.
   - Label text (WinForms Label, WPF TextBlock) is in the quoted Name.
3. **Find.** For a known target, `find-element {windowId, selector:{automationId:"MemoText"}}` is cheaper than inspect.
   - Use `selectors:[…]` to find up to 16 elements in one call.
   - The selector also takes `name`, `nameContains`, `nameRegex`, `controlType`, `parent`, `ancestorAutomationId` and `index`.
4. **Act.** `gui_act set-value {windowId, elementId:"e5", snapshotToken:"b2d40c2611bd", value:"text",
   expect:{selector:{automationId:"MemoText"}, condition:"value-equals", expected:"text"}}`.
   - Check `data.expect.satisfied`.
   - Add `observeAfter:{includeNewWindows:true, maxDepth:3}` when the action may open a dialog. New windows come back in `data.after.newWindows`.
5. **Wait.** `gui_wait wait-condition` (up to 30 s; poll is 100 ms).
   - Use `anyOf:[…]` to wait for success or an error dialog at once; `matchedIndex` tells which one matched.
   - A timeout is `completed` with `satisfied:false`: that is a result, not a tool failure.
6. **Judge** the product outcome: re-read values, reopen files, check other systems. `completed` only means the request ran.
7. **Close.** `gui_session {action:"close", sessionId}`. It stops launched apps, closes windows opened by `open`, and releases input.
   Report `terminatedProcessIds`, `closedWindowIds` and `retainedWindowIds` when they matter.

## 3. Statuses and what to do

| Response | Meaning | Do |
|---|---|---|
| `completed` | The request ran | Continue; verify the product result yourself |
| `rejected` | Refused before anything happened | Fix the request using `error.code`/`error.message`; safe to retry |
| `failed` | Real input was partly sent | Observe again before deciding |
| `completionUnknown` | A dispatch started; its end is unknown. The session becomes `blocked` | **Never repeat the action.** Observe the UI, then `gui_session {action:"reconcile", sessionId, requestId, resolution:"effect-confirmed"|"no-effect-confirmed", resume:true}` |

| error.code | Do |
|---|---|
| `StaleElement` | The element changed or the ref expired: run find-element or inspect again |
| `ElementNotFound` | Read `error.data.nearest` and the tree in `error.data.context`; fix the selector, or wait with `exists` |
| `AmbiguousTarget` | Pick from `error.data.candidates` (they carry refs), narrow the selector, or add `index` |
| `SearchIncomplete` | The window has virtualized lists: use an automationId outside them or `completeness:"realized"`; for list items use `find-item` → `scroll-item-into-view` → find with `completeness:"realized"` |
| `TargetScopeMismatch` | The window is outside the session: open the session with matching `targets` (or `any:true`) |
| `WindowNotFound`, `WindowChanged` | The window closed or was recreated: `gui_window windows` / `find-window` |
| `ElementDisabled`, `ValueReadOnly`, `PatternNotSupported` | The app state does not allow it: check the app, do not force it |
| `SessionBlocked` | `gui_session status` shows `pendingUnknownRequestIds` or `pauseReason`: reconcile or resume |
| `InputSessionRequired`, `ForegroundMismatch`, `InputLeaseRevoked` | See section 5 |
| `ElevationRequired`, `TargetElevated` | The target needs an elevated agent: tell the user |
| `ResponseTooLarge` | Use `maxDepth`, `rootElementId`+`rootSnapshotToken` or `interactiveOnly` |
| `AgentDisconnected`, `ControllerUnavailable` | See section 0 |

To recover a lost response, use `gui_session {action:"request-status", sessionId, requestId}`. Pass your own `requestId`
for actions you might need to look up. Repeating the same `requestId` with the same content never runs the action twice.

## 4. Efficiency

- Use `inspect` once with `interactiveOnly:true` and a `maxDepth`, then `find-element` by automationId.
  Use `rootElementId` to re-read only a panel.
- Use `expect` and `observeAfter` instead of separate find/wait calls after an action.
- Use `capabilities {elements:[…]}` to re-read several values in one call.
- Use `gui_capture` only when the look matters (layout, colors). Use `scale:0.5` or an `elementId` region, and
  `image:true` to see it.
- For replayable tests, store `generate-selector` output (selectors), not refs. Refs are per session and per window.

## 5. Real keyboard and mouse

Use these only when the input path itself is under test.

- Open with `input:true`. Only one input session per agent is allowed.
- Bring the window forward first: `gui_window focus {windowId}`. Keyboard operations need the foreground window.
- Operations: `click {elementId, snapshotToken, count?, modifiers?}`, `type-text {text}`, `key {key, action?}` and
  `chord {keys:["CTRL","S"]}`. A click is refused with `TargetObscured` when something covers the element.
- Any human keyboard or mouse activity revokes the lease (`InputLeaseRevoked`) and the session blocks. So does 30 s
  without a successful action. Resume with `gui_session {action:"resume"}`.
- UIA failures are never replaced by real input automatically. Decide explicitly.

## 6. Reporting

List what you verified (with the observed values), what you could not verify, and the `requestId` of any failed or
unknown action. Do not report a step as passed only because its status was `completed`.

## 7. Without MCP (CLI)

Run one controller host: `Controller.exe --serve`. Then send requests in either of two ways:

- **Many requests (fastest):** keep `Controller.exe --json-lines` running and write one request JSON per line.
- **One request:** `Controller.exe --request-stdin` per request.

Other commands:

- Sessions: `Controller.exe --session-open --launch C:\path\App.exe`, then `--session-close ID`, `--session-status ID`, and
  `--session-reconcile ID --request-id R --resolution no-effect-confirmed --resume`.
- Argument reference: `Controller.exe --operations [operation]`. Full reference: `docs/agent-usage.md`.
- If the host was started with `--state DIR`, every CLI call needs the same `--state DIR`.
- In the CLI, timeoutMilliseconds, requestId and note are fields of the request object itself:

```json
{"requestId":"r1","controlSessionId":"control-…","operation":"invoke","timeoutMilliseconds":5000,
 "arguments":{"windowId":"w1","elementId":"e4","snapshotToken":"1d93122bc0be"}}
```
