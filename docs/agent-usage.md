# 코딩 에이전트용 DesktopTestDriver 사용 규약

> **신뢰된 네트워크와 PC에서만 사용한다. 연결은 암호화하지 않는다.**

이 문서는 Controller의 명령, 요청·응답 형식, 작업 목록, 오류 뒤의 행동을 정한다. 처음 쓰는 경우 [README](../README_kr.md)의 빠르게 시작하기를 먼저 본다.

## 1. 실행 방식

| 입구 | 쓰는 경우 |
|---|---|
| `Controller.exe --mcp` | Claude Code 등 MCP 클라이언트. 도구 6개(`gui_session`, `gui_window`, `gui_observe`, `gui_act`, `gui_wait`, `gui_capture`). Controller가 없으면 프로세스 안에서 띄운다 |
| `Controller.exe --json-lines` | 프로세스 하나를 유지하며 stdin 한 줄에 명령 하나, stdout 한 줄에 응답 하나. 요청 객체(`operation`이 있는 줄)를 그대로 보내도 된다 |
| `Controller.exe --request FILE`, `--request-stdin` | 일회성 호출. 호출마다 프로세스를 띄우므로 반복 호출에는 위 두 가지를 쓴다 |
| C# `DesktopClient` | 모델 없이 반복 실행하는 테스트 코드 |

모든 입구는 `Controller.exe --serve`(호스트)에 이름 있는 파이프로 붙는다. 호스트는 Agent와 평문 TCP로 연결된다.

## 2. 명령행

공통 옵션: `--state DIR`(상태 폴더), `--listen ADDR:PORT`(Controller 수신 주소), `--config FILE`(선택 사항인 JSON 설정, 인자가 우선).

| 명령 | 설명 |
|---|---|
| `--serve` | 호스트 실행. 기본 `127.0.0.1:25001` |
| `--status [--machine M]` | 연결된 Agent와 세션 요약. `--machine`이면 그 Agent의 상태(한도, 권한, 세션) |
| `--doctor [--machine M]` | Agent에서 실제 점검: UIA 작업자 왕복, 그래픽 캡처 지원, 입력 감시 훅, 저널·캡처 사용량, 권한 수준과 UAC, 차단된 세션과 권장 조치, 모니터·DPI, 버전 |
| `--session-open [옵션]` | 세션을 연다. 아래 표 |
| `--session-status [ID] [--all]` | 세션 상태. 기본은 닫히거나 만료된 세션을 빼고, `--all`이면 모두 |
| `--session-close ID` | 세션을 닫는다. 세션이 띄운 앱 종료, `open`으로 연 창 닫기, 입력 해제 |
| `--session-pause ID`, `--session-resume ID [--expected-revision N]` | 일시 중지·재개 |
| `--session-reconcile ID --request-id R --resolution X [--evidence-ref E] [--resume]` | 미확정 요청을 확정하고, `--resume`이면 바로 재개 |
| `--request FILE`, `--request-stdin` | 요청 하나 실행 |
| `--request-status SESSION REQUEST` | 변경 요청·캡처의 기록된 결과 조회(읽기 요청은 기록하지 않는다) |
| `--inspect --session ID --window W` | 창 트리를 compact 형식으로 |
| `--trace-export SESSION DIR` | 세션의 변경 요청을 시간순 `trace.jsonl`로(요청의 `note`, 캡처 경로 포함) |
| `--export-schemas DIR` | 요청·응답·선택자 등 JSON Schema 생성 |
| `--shutdown` | 호스트 종료(열린 세션을 닫는다) |

Agent: `Agent.exe [--controller HOST:PORT] [--machine NAME] [--state DIR]`, `Agent.exe --stop [--state DIR]`, `Agent.exe --environment`.

### 세션 열기 옵션

| 옵션 | 의미 |
|---|---|
| `--machine M` | 대상 Agent. 연결된 Agent가 하나면 생략한다 |
| `--input` | 실제 키·마우스 입력을 쓴다. Agent당 하나만 열 수 있다 |
| `--observe` | 읽기만 한다 |
| `--launch EXE [--arg A]...` | 세션을 열고 앱을 실행하고 첫 창까지 기다려 `data.launch.window`로 돌려준다. 앱은 세션과 함께 종료된다 |
| `--target-pid N` (여러 번) | 이미 실행 중인 프로세스 |
| `--target-exe PATH` (여러 번) | 그 경로로 실행 중인 프로세스 전부 |
| `--target-window-title T`, `--target-window-title-contains T`, `--target-window-class C` | 창 조건 |
| `--target-any` | 그 사용자 세션의 모든 창 |
| `--idle-seconds N` | 유휴 만료, 기본 900 |

범위를 주지 않은 세션은 그 세션이 `launch`·`open`한 것만 다룬다. 범위 안 창의 소유 창, 범위 안 프로세스의 자식 프로세스 창, 범위 안 프로세스의 팝업(메뉴, 드롭다운)도 범위에 든다.

## 3. 요청

```json
{
  "requestId": "save-1",
  "controlSessionId": "control-…",
  "operation": "invoke",
  "timeoutMilliseconds": 5000,
  "arguments": { "windowId": "w1", "elementId": "e4", "snapshotToken": "1d93122bc0be" },
  "note": "저장 버튼"
}
```

- `requestId`, `controlSessionId`: 영문·숫자·`-`·`_`, 80자 이하.
- `timeoutMilliseconds`: 기본 5000. `wait-condition`·`wait-window`·`launch`·`open`은 30000까지, 나머지는 5000까지.
- `note`: 사람이 읽을 설명, 120자 이하. trace에 남는다.
- 알 수 없는 필드, 중복 키, 범위를 벗어난 인자는 실행 전에 거부한다.
- 읽기 요청의 `requestId`는 응답에 돌려주는 상관 ID일 뿐이다. 변경 요청(과 `capture`)만 기록되고, 같은 ID·같은 내용을 다시 보내면 실행하지 않고 기록된 결과를 돌려준다. 같은 ID에 다른 내용이면 `DuplicateRequestMismatch`다.

## 4. 응답

```json
{ "status": "completed", "requestId": "save-1", "controlSessionId": "control-…", "data": { … },
  "operation": "invoke", "machineId": "test-pc", "elapsedMilliseconds": 38, "observedAt": "…" }
```

| status | 뜻 | 다음 행동 |
|---|---|---|
| `completed` | 요청이 끝났다. 제품 결과는 별도로 확인한다 | 다음 단계 |
| `rejected` | 실행 전에 거부했다. 아무 효과가 없다 | 원인을 고쳐 다시 보낸다 |
| `failed` | 실행 중 실패했다. 실제 입력은 `error.data.inputEventsSent`만큼 보냈다 | 화면을 다시 관측하고 판단한다 |
| `completionUnknown` | 디스패치를 시작했고 끝났는지 확인하지 못했다. 세션이 `blocked`가 된다 | 같은 조작을 반복하지 않는다. 관측으로 결과를 확인하고 `--session-reconcile … --resume` |

오류는 `error.code`, `error.message`, `error.data`로 온다. 한글은 이스케이프하지 않은 UTF-8이다.

## 5. 세션 상태

| state | 뜻 |
|---|---|
| `active` | 정상 |
| `blocked` | 미확정 변경(`completionUnknown`)이 있거나, 입력 해제에 실패했거나, 입력 lease가 회수됐거나, 명시적으로 일시 중지했다. 읽기는 계속된다 |
| `closed`, `expired` | 닫혔거나 유휴 만료됐다(차단된 세션도 만료된다). 24시간 뒤 기록을 지운다 |

읽기 요청의 시간 초과, 디스패치 전 거부는 세션 상태를 바꾸지 않는다. `release-input` 뒤에는 미확정 요청이 없으면 `active`로 돌아간다.

**입력 세션의 lease.** `--input` 세션은 열거나 재개할 때 30초 lease를 받고, 조작이 성공할 때마다 `지금 + 30초`로 연장된다. 상한은 연 시점부터 15분이다(`leaseHardLimitAt`). 사람의 키·마우스 입력이 감지되면 즉시 회수되어 세션이 `blocked`가 된다. `--session-resume`으로 새 lease를 받는다.

## 6. 작업 목록

| 분류 | 작업 |
|---|---|
| 창 | `windows`, `find-window`, `wait-window`, `window-state`, `focus`, `close-window` |
| 앱 | `launch`, `open`, `terminate-owned` |
| 관측 | `inspect`, `find-element`, `capabilities`, `wait-condition`, `find-item`, `read-grid`, `read-text`, `generate-selector`, `capture`, `clipboard-get` |
| UIA 조작 | `invoke`, `set-value`, `set-range-value`, `select-item`, `set-toggle`, `set-expanded`, `set-focus`, `legacy-default-action`, `realize-item`, `scroll-item-into-view`, `scroll-container`, `clipboard-set` |
| 실제 입력(`--input` 세션) | `click`, `type-text`, `key`, `chord`, `move`, `wheel`, `drag`, `release-input` |

UIA 조작이 실패해도 실제 입력으로 자동 대체하지 않는다. UIA 조작이 성공했다고 실제 키·마우스 경로가 검증된 것은 아니다.

### 창과 앱

| 작업 | 인자 | 결과 |
|---|---|---|
| `windows` | `kind?`(`main`·`owned`·`popup`), `includePopups?` | `windows[]`: `windowId`(`w1`…), `title`, `pid`, `processName`, `bounds`, `className`, `kind`, `ownerWindowId` |
| `find-window` | `title`·`titleContains`·`titleRegex`·`className`·`kind`·`processName`·`ownerWindowId` 중 하나 이상 | `window`. 여러 개면 `AmbiguousTarget`과 후보 |
| `wait-window` | 위와 같음 + `pollMilliseconds` | `satisfied`, `window` |
| `window-state` | `windowId`, `state`(`minimize`·`maximize`·`restore`·`close`) | UIA WindowPattern, 없으면 Win32 |
| `launch` | `executable`(절대 경로), `arguments[]`, `lifetime`(`session` 기본·`keep`), `workingDirectory`, `verb`(`open`·`runas`), `waitWindow{timeoutMilliseconds, title, titleContains, className}` | `processId`, `pid`, `window`, `waitSatisfied` |
| `open` | `target`(URI·`control.exe /name …`·`.msc`·`.cpl`·문서), `arguments[]`, `verb`, `workingDirectory`, `waitWindow` | 새로 나타난 창 `window`. 그 창은 세션 범위에 들고 세션을 닫을 때 닫힌다 |
| `terminate-owned` | `processIds[]` | 세션이 띄운 프로세스 종료 |

`verb: "runas"`는 Agent가 관리자로 실행 중일 때만 된다. 일반 권한 Agent에서는 UAC 보안 데스크톱을 조작할 수 없으므로 `ElevationRequired`로 미리 거부한다. 일반 권한 Agent가 관리자 권한 창을 조작하려 하면 Windows가 막는다.

### 관측

**`inspect`** — `windowId`, `format`(`compact` 기본·`json`), `maxDepth`(1–16), `rootElementId`+`rootSnapshotToken`(서브트리), `interactiveOnly`, `includeOffscreen`(기본 true), `includeBounds`, `treeView`(`control` 기본·`raw`·`content`).

```text
- Window "간단 메모" [e1 t=3fa9c1d20b7e]
  - Edit #MemoText "메모" [e2 t=91b04c77a2d1] value="테스트 메모입니다" patterns=Value
  - Button #SaveButton "저장" [e3 t=5d02e8b1c9f0] patterns=Invoke
  - Button #OpenButton "열기" [e4 t=0a7711e3bb52] disabled patterns=Invoke
```

한 줄이 요소 하나다: `ControlType #AutomationId "Name" [참조 t=토큰]` 뒤에 기본값이 아닌 속성(`disabled`, `offscreen`, `value=`, `password`, `toggle=`, `selected`, `expanded=`, `focused`, `range=`, `patterns=`, `box=x,y,w,h`)만 붙는다. 값은 200자로 자른다. 참조와 토큰은 그 창에서만 유효하다.

**선택자** — `automationId`, `automationIdRegex`, `controlType`, `name`, `nameContains`, `nameStartsWith`, `nameRegex`(.NET 정규식, 256자 이하), `className`, `frameworkId`, `ancestorAutomationId`, `parent`(선택자), `index`(0부터, 문서 순서). 이름 연산자는 하나만 쓴다.

**`find-element`** — `windowId`, `selector` 또는 `selectors[]`(16개까지, 한 번에 찾는다), `searchRootElementId`+`searchRootSnapshotToken`, `includePath`, `completeness`, `treeView`. 결과는 `target`, `matchCount`, `uniquenessScope`.

- 여러 개가 일치하면 `AmbiguousTarget`이고 `error.data.candidates`에 후보 5개(참조, `automationId`, `name`, `controlType`, 부모 이름)와 `matchCount`가 온다. 선택자를 좁히거나 `index`를 준다.
- 없으면 `ElementNotFound`이고 `error.data.nearest`에 비슷한 요소 3개, `error.data.context`에 창의 위 2단계 트리와 범위 안 창 목록이 온다(`errorContext: false`로 끈다).

**가상화 컨테이너.** 목록·그리드·콤보 상자처럼 `ItemContainer`를 노출하는 컨테이너가 창에 있으면, 실체화되지 않은 항목은 트리에 없다. 그래서 다음 규칙을 쓴다.

| 경우 | 결과 |
|---|---|
| 컨테이너가 없다 | 보통 검색. `uniquenessScope: "complete"` |
| 선택자에 `automationId`가 있고 일치가 하나이며 그 조상에 컨테이너가 없다 | 성공. `uniquenessScope: "outside-virtualized"` |
| 그 밖(이름만으로 찾기, 컨테이너 안의 요소) | `SearchIncomplete`. `completeness: "realized"`를 주면 실체화된 범위에서 찾고 `uniquenessScope: "realized-only"`와 `virtualizedContainers`(참조)를 붙인다 |

컨테이너 안의 항목은 `find-item`(이름으로 찾기) → `scroll-item-into-view` 또는 `realize-item` → `completeness: "realized"`로 찾는다. `absent` 조건은 `realized`를 줄 때만 "실체화된 범위에 없음"으로 판정한다.

**`capabilities`** — `elementId`+`snapshotToken`, `selector`, 또는 `elements[]`(16개) 중 하나. 현재 값·상태를 읽는다. `includeDescriptors: true`이면 작업 설명을 붙인다. `Value` 패턴이 없는 Win32 컨트롤은 `LegacyIAccessible` 값으로 대체하고 `value.source: "legacy"`를 표시한다.

**기타** — `find-item`(`itemName`), `read-grid`(`startRow`, `rowCount`, `startColumn`, `columnCount`, 셀 500개까지), `read-text`(`selectionOnly`, `maxLength`), `generate-selector`(그 창에서 유일한 가장 짧은 선택자와 대안. 반복 실행할 코드에는 참조 대신 이것을 저장한다), `clipboard-get`.

**`capture`** — `windowId`, `region{x,y,width,height}` 또는 `elementId`+`snapshotToken`, `scale`(0.25–1), `output`(절대 경로, 생략하면 Controller 상태 폴더의 `captures/`). 결과의 `path`에 PNG가 있다. 캡처 좌표는 30초 동안 `click`·`move`·`drag`의 `captureId`로 쓸 수 있다.

### 대기

**`wait-condition`** — `windowId`, `selector`+`condition`+`expected` 또는 `anyOf[]`(4개까지), `pollMilliseconds`(기본 100, 25–1000), `searchRoot…`, `completeness`.

| condition | expected |
|---|---|
| `exists`, `absent`, `enabled`, `disabled`, `onscreen`, `offscreen` | 없음 |
| `value-equals`, `value-not-equals` | 문자열 |
| `text-contains`, `name-equals`, `name-contains` | 문자열(포함은 1자 이상) |
| `toggle-equals` | `on`·`off`·`indeterminate` |
| `selected-equals` | `true`·`false` |
| `expanded-equals` | `expanded`·`collapsed`·`partially`·`leaf` |

WinForms `Label`, WPF `TextBlock`의 글자는 `Name`에 있으므로 `name-equals`·`name-contains`로 기다린다. 결과는 `satisfied`, `matchedIndex`(anyOf), `target`, `polls`. **시간 초과는 오류가 아니라 `completed` + `satisfied: false`**이며 마지막 관측값과 `lastError`를 담는다. 폴링 중 `StaleElement`나 일시적 읽기 실패는 다음 폴링으로 넘기고, `AmbiguousTarget`과 가상화로 인한 `SearchIncomplete`는 즉시 실패한다.

### 조작

UIA 조작은 `windowId`, `elementId`, `snapshotToken`과 작업별 인자를 받는다: `set-value`(`value`, `expectedValueHash`=현재 값 UTF-8의 SHA-256), `set-range-value`(`value`), `set-toggle`(`desiredState`: `on`·`off`·`indeterminate`, 3상태 컨트롤은 원하는 상태가 될 때까지 최대 3회 토글), `set-expanded`(`expanded`), `scroll-container`(`horizontal`, `vertical`: 0–100, 바꾸지 않으면 -1).

Agent는 한 번의 작업자 왕복에서 요소를 다시 확인하고(사라짐·변경 → `StaleElement`), 활성·패턴·읽기 전용·기대 값을 확인한 뒤 디스패치한다. 이 단계의 거부는 모두 `rejected`이며 세션에 영향이 없다.

모든 변경 작업은 선택 인자 두 개를 받는다. 실패해도 조작 결과는 바뀌지 않고 `data.afterError`에 코드만 남는다.

| 인자 | 의미 |
|---|---|
| `expect` | `{selector, condition, expected?, timeoutMilliseconds(≤5000), pollMilliseconds?}`. 디스패치 뒤 조건을 기다려 `data.expect`(`satisfied`, `target`)에 넣는다 |
| `observeAfter` | `{rootElementId?, rootSnapshotToken?, settleMilliseconds(0–1000), maxDepth?, includeNewWindows?}`. 조작 뒤 서브트리를 `data.after.tree`에, 새로 나타난 범위 안 창(팝업 포함)을 `data.after.newWindows`에 넣는다 |

한 요청에 여러 조작 단계를 담는 묶음 요청은 없다.

### 실제 입력

`--input` 세션에서만 된다. 키보드 작업은 대상 창이 전경이어야 한다(아니면 `ForegroundMismatch`, 먼저 `focus`).

| 작업 | 인자 |
|---|---|
| `click` | `elementId`+`snapshotToken` 또는 `position{x,y,captureId?}`(창 클라이언트 좌표, `captureId`가 있으면 캡처 픽셀), `button`, `count`(1–3), `modifiers`(`CTRL`·`SHIFT`·`ALT`) |
| `type-text` | `text`(4096자, 개행·탭 허용). 500개 이벤트 단위로 한 번에 보낸다 |
| `key` | `key`, `action`(`press` 기본·`down`·`up`) |
| `chord` | `keys[]`(8개까지) |
| `move`, `wheel` | `position`, `delta`(±1200) |
| `drag` | `start`, `end`, `captureId?`, `button`, `steps` |
| `release-input` | 이 세션이 누른 키·버튼을 모두 뗀다 |

키 이름: `A`–`Z`, `0`–`9`, `F1`–`F24`, `NUMPAD0`–`NUMPAD9`, `CTRL`, `ALT`, `SHIFT`, `WIN`, `ENTER`, `TAB`, `ESC`, `BACKSPACE`, `DELETE`, `INSERT`, `SPACE`, 방향키, `HOME`, `END`, `PAGEUP`, `PAGEDOWN`, `APPS`, `PRINTSCREEN`, `-` `=` `,` `.` `/` `;` `'` `[` `]` `\` `` ` ``.

요소 클릭은 먼저 좌표가 50ms 간격의 두 번 읽기에서 같은지 확인하고(최대 500ms), 그 좌표의 요소가 대상이나 그 자손인지 확인한다. 다르면 `TargetObscured`와 `data.blockedBy`(가리는 요소의 이름·종류·창 제목)로 거부한다.

## 7. 처음부터 한 번 조작하는 예

```powershell
$c = 'C:\DesktopTestDriver\Controller\Controller.exe'
$open = & $c --session-open --launch C:\GuiTest\SimpleMemo.exe | ConvertFrom-Json
$s = $open.controlSessionId; $w = $open.data.launch.window.windowId
function Req($op, $arguments, $id = [guid]::NewGuid().ToString('N'), $timeout = 5000) {
  @{ requestId = $id; controlSessionId = $s; operation = $op; timeoutMilliseconds = $timeout; arguments = $arguments } |
    ConvertTo-Json -Depth 10 -Compress | & $c --request-stdin | ConvertFrom-Json
}
$memo = (Req 'find-element' @{ windowId = $w; selector = @{ automationId = 'MemoText' } }).data.target
Req 'set-value' @{ windowId = $w; elementId = $memo.elementId; snapshotToken = $memo.snapshotToken; value = '첫 줄';
  expect = @{ selector = @{ automationId = 'MemoText' }; condition = 'value-equals'; expected = '첫 줄' } } 'memo-1'
& $c --session-close $s
```

## 8. 오류 뒤의 행동

| 코드 | 뜻 | 행동 |
|---|---|---|
| `StaleElement` | 참조가 만료됐거나 요소가 바뀌었다 | `find-element` 또는 `inspect`로 새 참조를 받는다 |
| `ElementNotFound` | 없다 | `error.data.nearest`, `context`를 보고 선택자를 고친다. 나타나길 기다리면 `wait-condition` |
| `AmbiguousTarget` | 여러 개가 일치한다 | 후보를 보고 선택자를 좁히거나 `index` |
| `SearchIncomplete` | 가상화 컨테이너 때문에 유일성을 판정할 수 없다 | `automationId`로 찾거나 `completeness: "realized"` |
| `ElementDisabled`, `ValueReadOnly`, `PatternNotSupported` | 지금 그 조작을 할 수 없다 | 앱 상태를 확인한다 |
| `WindowChanged`, `WindowNotFound` | 창이 닫히거나 다시 만들어졌다 | `windows`·`find-window`로 다시 찾는다 |
| `TargetScopeMismatch` | 세션 범위 밖 | 세션을 범위와 함께 연다 |
| `SessionBlocked` | 미확정 요청 또는 lease 회수 | `--session-status`로 원인을 보고 `--session-reconcile … --resume` 또는 `--session-resume` |
| `InputLeaseRevoked` | 사람의 입력이 감지됐거나 lease가 끝났다 | 사람이 작업을 마친 뒤 `--session-resume` |
| `InputSessionRequired` | 실제 입력은 `--input` 세션에서만 | 세션을 `--input`으로 연다 |
| `ElevationRequired`, `TargetElevated` | 권한 부족 | Agent를 관리자로 실행한다 |
| `RequestTimeout` | 제한 시간 안에 끝나지 않았다 | 읽기는 그대로 다시 보낸다. 변경은 상태를 확인한다 |
| `ResponseTooLarge` | 응답이 8MiB를 넘는다 | `maxDepth`, `rootElementId`, `interactiveOnly`로 범위를 줄인다 |
| `ControllerUnavailable` | 호스트가 없다 | `Controller.exe --serve` |
| `AgentDisconnected` | Agent가 연결되지 않았다 | Agent 실행과 주소를 확인한다 |

`completionUnknown`을 받은 조작은 같은 조작을 다시 보내지 않는다. 관측으로 결과를 확인한 뒤 `effect-confirmed`·`no-effect-confirmed`·`abandoned` 중 하나로 확정한다. 응답을 잃었으면 `--request-status`로 같은 요청의 결과를 조회한다.

## 9. 저장되는 것

| 파일 | 내용과 보관 |
|---|---|
| `request-*.json` | 변경 요청과 `capture`의 기록(평문 JSON). 완료 뒤 24시간, 미확정은 확정될 때까지. 합계 64MiB에 닿으면 변경 요청을 거부한다 |
| `session-*.json` | 세션 상태. 닫히거나 만료된 지 24시간 뒤 삭제 |
| Agent `cap-*.png` | 캡처 사본. Controller가 받으면 지우고, 아니면 24시간 뒤 지운다 |
| Controller `captures/` | `output`을 생략한 캡처 |
| `held-input.json` | 도구가 누르고 있는 키·버튼(watchdog이 Agent가 죽으면 해제한다) |

Agent가 죽거나 멈추면(3초간 생존 신호 없음) watchdog 프로세스가 누른 키·버튼을 떼고 세션이 띄운 앱을 종료한다.
