---
name: desktop-test-driver
description: Test Windows desktop GUIs (WinForms, WPF, WinUI, Win32, Settings, Control Panel) end to end through UI Automation with the DesktopTestDriver MCP tools (gui_session, gui_window, gui_observe, gui_act, gui_wait, gui_capture). Use when asked to run, verify or explore a Windows GUI, instead of screenshot-and-coordinate computer use.
---

# DesktopTestDriver

Drive Windows GUIs by UI element. Trusted machines only: the connection is not encrypted.

## Flow

1. `gui_session` `{action:"open", launch:"C:\path\App.exe"}` starts the app and returns `data.launch.window.windowId`.
   For running apps pass `targets:{pids:[…]}` or `targets:{windowTitleContains:"…"}`. For Settings/Control Panel open a
   session and use `gui_window` `open` (`ms-settings:about`, `control.exe`) with `waitWindow`.
2. `gui_observe` `inspect` `{windowId, maxDepth:6, interactiveOnly:true}` returns a compact tree:
   `- Button #SaveButton "저장" [e4 t=1d93122bc0be] patterns=Invoke`. `e4` is `elementId`, `t=` is `snapshotToken`.
3. `gui_act` with `{windowId, elementId, snapshotToken, …}`: `invoke`, `set-value {value}`, `set-toggle {desiredState}`,
   `select-item`, `set-expanded {expanded}`, `set-range-value {value}`. Add
   `expect:{selector:{automationId:"Result"}, condition:"value-equals", expected:"…"}` to verify in the same call, and
   `observeAfter:{includeNewWindows:true}` to see dialogs that opened.
4. `gui_wait` `wait-condition` `{windowId, selector, condition, expected}` (up to 30 s; `anyOf` for success-or-error).
   A timeout returns `satisfied:false`, not an error. Text of labels is in the Name: use `name-equals`/`name-contains`.
5. Judge the product result (re-read values, reopen files). `completed` only means the request finished.
6. `gui_session` `{action:"close"}`: stops launched apps, closes opened windows, releases input.

## Rules

- Prefer `selector:{automationId}`. Use `generate-selector` to get a stable selector for code that replays the flow.
- `StaleElement`: find again. `AmbiguousTarget`: read `error.data.candidates`, narrow the selector or add `index`.
- `SearchIncomplete`: the window has virtualized lists. Use an `automationId` outside them or `completeness:"realized"`;
  for list items use `find-item` then `scroll-item-into-view`.
- Never repeat an action that returned `completionUnknown`. Observe the UI, then
  `gui_session {action:"reconcile", requestId, resolution:"effect-confirmed"|"no-effect-confirmed", resume:true}`.
- Real keyboard/mouse (`click`, `type-text`, `key`, `chord`) needs `input:true` at open and the window in the foreground
  (`gui_window focus`). UIA success does not prove the keyboard path works, and UIA failures are never replaced by input.
- `gui_capture` `{arguments:{windowId, scale:0.5}, image:true}` only when the look matters; values are read as text.
