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

`timeoutMilliseconds` is 1-5000 for every operation except `wait-condition`, `wait-window`, `launch` and `open`, which
allow up to 30000. Wait longer than 30 s by sending another wait.

Each tool description lists the arguments of its operations. An argument error repeats the expected arguments of
the operation, so read the error message and fix the request. Using the CLI? The request object differs; see section 7.

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
   - `data.target` in an action response is the element **before** the action. Read the outcome through `expect`
     or a new find.
   - Add `observeAfter:{includeNewWindows:true, maxDepth:3}` when the action may open a dialog. New windows come back
     in `data.after.newWindows`.
     - `observeAfter` lists interactive elements only by default. Pass `interactiveOnly:false` for every node.
     - Dialogs are large, so keep `maxDepth` at 3–4.
   - A modal dialog appears both as its own window (`w2`) and inside its owner's tree (`data.after.tree`). A ref works
     only with the windowId of the tree it came from:
     - Refs from `data.after.tree` go with the acting window (`w1`).
     - To use `w2`, read it first: `find-element` or `inspect` with `windowId:"w2"`.
5. **Wait.** `gui_wait wait-condition` (up to 30 s; poll is 100 ms).
   - Use `anyOf:[…]` to wait for success or an error dialog at once; `matchedIndex` tells which one matched.
   - A timeout is `completed` with `satisfied:false`: that is a result, not a tool failure.
6. **Judge** the product outcome: re-read values, reopen files, check other systems. `completed` only means the request ran.
7. **Close.** `gui_session {action:"close", sessionId}`. It stops launched apps, closes windows opened by `open`, and releases input.
   - Report `terminatedProcessIds`, `closedWindowIds` (including windows you closed with `close-window`) and
     `retainedWindowIds` when they matter.
   - `inputReleased`/`applicationsStopped` are cleanup results and stay absent while the session is open.

**Make the path deterministic.** Before the test, decide what to do with files the test writes. Delete them first
(or use a new file name) so the app does not take a different path, such as a "replace?" prompt, only on some runs.

**Close and restart within a session.**

1. `gui_window close-window {windowId}` waits up to 2 s (`waitMilliseconds`, up to 5000) and returns `closed` and
   `processExited`.
2. If `closed` is false and `newWindows` lists a window, the app is asking something ("save changes?").
   `find-element` its button (e.g. `{controlType:"Button", nameStartsWith:"예"}` / `"Yes"`) in that windowId and
   `invoke` it.
3. Then confirm with `gui_wait wait-window {title:"…", absent:true}`.
4. Start the app again with `gui_window launch {executable, arguments:[…], waitWindow:{titleContains:"…"}}` and a
   request `timeoutMilliseconds` such as 15000.
   - `waitWindow` waits for the rest of that time.
   - The new window gets a new windowId and new refs. Window ids are never reused within a session.

**Testing "reopen".** Pick the path that matches what you are asked to verify:
- Persisted content: relaunch with the file path (`launch … arguments:[path]`). This is the default.
- The app's own Open feature: use its Open button and the Open dialog (below).
- If unsure, do the relaunch and report the Open dialog as not tested.

**An action that closes its own window** (a dialog's OK/Save/Yes button) is not an error.
- The response is `completed` with `data.windowClosed:true`.
- `observeAfter` returns only `data.after.newWindows`. `expect` is marked `skipped:"WindowClosed"`.
- Check the result with `wait-window` or in the owner window.

**Confirming window-level results.** `expect` checks elements inside the acting window. For window-level results, use
`gui_wait wait-window`:

- A title change: `{titleContains:"memo.txt"}`.
- A dialog that closed: `{title:"다른 이름으로 저장", absent:true}`.
- An app that exited: `{processName:"App.exe", absent:true}`.

**Standard Open/Save file dialogs.**

1. Invoke the app's Open/Save button with `observeAfter:{includeNewWindows:true, maxDepth:3}`.
   `data.after.newWindows` gives the dialog's windowId and title only, no elements.
2. Read the two elements you need from the dialog window in one call:
   - Save dialog: `find-element {windowId:"<dialog>", selectors:[{automationId:"FileNameControlHost"}, {automationId:"1"}]}`.
     The file name field is a ComboBox that supports Value.
   - Open dialog: use `{automationId:"1148", controlType:"Edit"}` instead of `FileNameControlHost`.
   - Labels follow the Windows language, so use these automationIds.
   - The dialog opens in the folder it used last, so always set the full path.
3. `set-value` the full path with `expect` value-equals, then `invoke` the button `#1` (Save/Open).
   - If the dialog closes, the response carries `windowClosed:true`.
   - If a prompt appears, it carries `dispatchState:"pending-modal"` (next step).
4. If the file exists, Save asks to replace it. The invoke then returns `completed` with
   `data.dispatchState:"pending-modal"` and the prompt in `data.modalWindows` (see below). Invoke the prompt's Yes
   button.
   - Find it by name in the prompt's windowId: `{controlType:"Button", nameStartsWith:"예"}` (`"Yes"` in English).
   - Its automationId depends on the dialog type: `6` in a classic message box, `CommandButton_6` in a task dialog.
5. Confirm with `gui_wait wait-window {title:"<dialog title>", absent:true}`, then check the app (title or content).
   Read the saved file yourself to verify its content.

**An action that opens a modal window.** Some buttons (Win32 dialog buttons, many WinForms buttons) do not return
until the modal window they open is closed. The driver detects this:

- The response is `completed` with `data.dispatchState:"pending-modal"` and `data.modalWindows` (windowIds you can
  use right away).
- The session stays `active`.
- Answer the modal window: `find-element` + `invoke` its button, or `close-window`. The original action then finishes
  on its own.
- `expect` is skipped in this case. Check the result after answering.

## 3. Statuses and what to do

| Response | Meaning | Do |
|---|---|---|
| `completed` | The request ran | Continue; verify the product result yourself |
| `rejected` | Refused before anything happened | Fix the request using `error.code`/`error.message`; safe to retry |
| `failed` | Real input was partly sent | Observe again before deciding |
| `completionUnknown` | A dispatch started; its end is unknown. The session becomes `blocked` | **Never repeat the action.** Observe the UI, then `gui_session {action:"reconcile", sessionId, requestId, resolution:"effect-confirmed"|"no-effect-confirmed", resume:true}` |

**While a session is `blocked`, reads still work and mutations are refused (`SessionBlocked`).**
- Reads: `windows`, `find-window`, `wait-window`, `inspect`, `find-element`, `capabilities`, `wait-condition`,
  `read-text`, `capture`.
- Use them to see what really happened, then reconcile.
- Reconciling only records your judgement and unblocks the session. It does not cancel or repeat anything: an action
  still running in the app finishes on its own.

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

- **From PowerShell (recommended):** use the helper script `Invoke-DtdRequest.ps1`. It writes the request as UTF-8,
  sends it with `--request FILE`, and returns the parsed response. Request and response encoding are handled for you.
  - Packaged: `<package>\Controller\scripts\Invoke-DtdRequest.ps1`, in the `scripts` folder next to Controller.exe.
    It finds `..\Controller.exe` itself, so `-Controller` is not needed.
  - Repository: `scripts\Invoke-DtdRequest.ps1`. Pass `-Controller <path>\Controller.exe`.
  ```powershell
  $dtd = 'C:\…\Controller\scripts\Invoke-DtdRequest.ps1'
  $r = & $dtd -State $state -Session $s -Operation set-value -Arguments @{windowId='w1'; elementId='e5'; snapshotToken='b2d40c2611bd'; value='에이전트 시험'; expect=@{selector=@{automationId='MemoText'}; condition='value-equals'; expected='에이전트 시험'}}
  $r.status; $r.data.expect.satisfied
  ```
  - It also takes `-TimeoutMilliseconds`, `-RequestId` and `-Note`.
  - The only extra step is for printing Korean in your own console: set
    `[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)`.
- **A program that keeps a process open:** start `Controller.exe --json-lines` once, write one request JSON per line
  to its stdin (UTF-8), and read one response line each. This is the fastest path, but it pays off only when your
  process stays alive between requests. Each separate shell command would start a new one.
- **One raw request:** `Controller.exe --request FILE` or `--request-stdin`.

Other commands:

- Sessions:
  - Open: `Controller.exe --session-open --launch C:\path\App.exe --arg --quiet --arg C:\data\file.txt`
    (`--arg` repeats, one per argument).
  - Then `--session-close ID`, `--session-status ID`, and
    `--session-reconcile ID --request-id R --resolution no-effect-confirmed --resume`.
  - `Controller.exe --help` lists every command.
- Argument reference: `Controller.exe --operations [operation]` (it includes the timeout limit). Full reference:
  `docs/agent-usage.md`.
- If the host was started with `--state DIR`, every CLI call needs the same `--state DIR`.
- **Non-ASCII text without the helper:** Windows PowerShell 5.1 may re-encode piped text and break it. `Set-Content`
  writes ANSI, so do not use it for request files. Write the file like this (a BOM is accepted too) and use `--request FILE`:
  ```powershell
  [IO.File]::WriteAllText("$env:TEMP\r.json", ($request | ConvertTo-Json -Depth 10 -Compress), [Text.UTF8Encoding]::new($false))
  & $controller --state $state --request "$env:TEMP\r.json" | ConvertFrom-Json
  ```
  Or set `$OutputEncoding = [Text.UTF8Encoding]::new($false)` once before piping to `--request-stdin` / `--json-lines`.
  Responses are UTF-8. If Korean prints garbled, also set `[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)`.
- The CLI request uses `controlSessionId` (not `sessionId`). `timeoutMilliseconds`, `requestId` and `note` are fields
  of the request object itself:

```json
{"requestId":"r1","controlSessionId":"control-…","operation":"invoke","timeoutMilliseconds":5000,
 "arguments":{"windowId":"w1","elementId":"e4","snapshotToken":"1d93122bc0be"}}
```

- **Names in this skill → CLI.** Every `gui_window`/`gui_observe`/`gui_act`/`gui_wait`/`gui_capture` operation name is
  the CLI `operation` as is: `gui_window windows` → `"operation":"windows"`, `gui_wait wait-window` →
  `"operation":"wait-window"`, `gui_capture` → `"operation":"capture"`. `gui_session` actions are commands:

| gui_session action | CLI |
|---|---|
| `agents` | `--status` |
| `doctor` | `--doctor` |
| `open` | `--session-open [--launch EXE --arg A …] [--input] [--observe] [--target-pid N] [--target-window-title-contains T] [--target-any]` |
| `status` | `--session-status ID` |
| `close` | `--session-close ID` |
| `resume` | `--session-resume ID` |
| `pause` | `--session-pause ID` |
| `reconcile` | `--session-reconcile ID --request-id R --resolution effect-confirmed --resume` |
| `request-status` | `--request-status ID R` |

- **Exit codes:** 0 `completed`, 2 `completionUnknown`, 1 everything else (`rejected`, `failed`, command errors).
  The JSON response is always printed, so read `status` rather than relying on the exit code alone.
- `--session-close` reports `terminatedProcessIds`. These are the applications the session launched that are no longer
  running, including ones that had already exited (e.g. after `close-window`). `retainedProcessIds` were left running
  (`lifetime:"keep"`).
- `closedWindowIds` lists windows closed by `close-window` and windows opened with `open` that the session closed.
  Windows that disappeared because their launched process was stopped are covered by `terminatedProcessIds` instead.
- `launch` + `waitWindow` matches only windows of the launched process (and its child processes). `open` + `waitWindow`
  matches new windows of any process, plus an already open window that fits the title or class conditions.
