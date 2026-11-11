# 코딩 에이전트용 DesktopTestDriver 사용 규약

> **신뢰된 네트워크와 PC에서만 사용한다. 연결은 암호화하지 않는다.**

이 문서는 Controller의 명령, 요청·응답 형식, 작업 목록, 오류 뒤의 행동을 정한다. 처음 쓰는 경우 [README](../README_kr.md)의 빠르게 시작하기를 먼저 본다.

## 1. 실행 방식

| 입구 | 쓰는 경우 |
|---|---|
| `Controller.exe --mcp` | Claude Code 등 MCP 클라이언트. 도구 6개(`gui_session`, `gui_window`, `gui_observe`, `gui_act`, `gui_wait`, `gui_capture`). Controller가 없으면 프로세스 안에서 띄운다 |
| `Controller.exe --json-lines` | 프로세스 하나를 유지하며 stdin 한 줄에 명령 하나, stdout 한 줄에 응답 하나. 요청 객체(`operation`이 있는 줄)를 그대로 보내도 된다 |
| `Controller.exe --request FILE`, `--request-stdin` | 일회성 호출. 호출마다 프로세스를 띄우므로 반복 호출에는 위 두 가지를 쓴다 |
| `Controller.exe --operations [작업]` | 작업별 인자 목록. MCP 도구 설명, 인자 오류 메시지와 같은 내용이다 |
| `scripts\Invoke-DtdRequest.ps1` | PowerShell 도우미. `-Session -Operation -Arguments @{…}`를 받아 요청을 BOM 없는 UTF-8 파일로 써서 보내고, 파싱한 응답을 돌려준다. 한글이 깨지지 않는다 |
| C# `DesktopClient` | 모델 없이 반복 실행하는 테스트 코드 |

모든 입구는 `Controller.exe --serve`(호스트)에 이름 있는 파이프로 붙는다. 호스트는 Agent와 평문 TCP로 연결된다.

## 2. 명령행

공통 옵션: `--state DIR`(상태 폴더), `--listen ADDR:PORT`(호스트의 수신 주소), `--config FILE`(선택 사항인 JSON 설정, 인자가 우선).

**CLI와 호스트는 같은 상태 폴더를 써야 한다.** 로컬 파이프 이름이 상태 폴더 경로로 정해지므로, 호스트를 `--state D:\dtd`로 띄웠다면 다른 모든 명령에도 `--state D:\dtd`를 준다. 둘 다 생략하면 기본 폴더(`%LOCALAPPDATA%\DesktopTestDriver\Controller`)를 쓴다. 호스트가 없거나 폴더가 다르면 `ControllerUnavailable`이다.

| 명령 | 설명 |
|---|---|
| `--serve` | 호스트 실행. 기본 `127.0.0.1:25001`. 원격 Agent를 받을 때만 `--listen 0.0.0.0:25001`처럼 바꾼다 |
| `--status` | 호스트 주소와 연결된 Agent별 열린 세션 요약 |
| `--status --machine M` | 그 Agent의 상태: 버전, 권한 수준, 입력 감시 상태, 요청 한도, 저널·캡처 사용량, 세션 |
| `--doctor [--machine M]` | Agent에서 실제 점검: UIA 작업자 왕복, 그래픽 캡처 지원, 입력 감시 훅, 저널·캡처 사용량, 권한 수준과 UAC, 차단된 세션과 권장 조치, 모니터·DPI, 버전 |
| `--session-open [옵션]` | 세션을 연다. 아래 표 |
| `--session-status` | 열린 세션 목록. `--session-status --all`이면 닫히거나 만료된 세션까지 |
| `--session-status ID` | 세션 하나의 상태 |
| `--session-close ID` | 세션을 닫는다. 세션이 띄운 앱 종료, `open`으로 연 창 닫기, 입력 해제 |
| `--session-pause ID` | 입력을 해제하고 세션을 `blocked`로 둔다 |
| `--session-resume ID [--expected-revision N]` | `blocked`에서 `active`로. 미확정 요청이 남아 있으면 거부한다 |
| `--session-reconcile ID --request-id R --resolution X [--evidence-ref E] [--expected-revision N] [--resume]` | 미확정 요청을 확정하고, `--resume`이면 바로 재개 |
| `--request FILE`, `--request-stdin` | 요청 하나 실행. 파일·입력 앞의 BOM은 무시한다 |
| `--request-status SESSION REQUEST` | 변경 요청·캡처의 기록된 결과 조회(읽기 요청은 기록하지 않는다) |
| `--inspect --session ID --window W` | 창 트리를 compact 형식으로 |
| `--trace-export SESSION DIR` | 세션의 변경 요청과 캡처를 시간순 `trace.jsonl`로(요청의 `note`, 인자, 결과, 캡처 경로 포함) |
| `--export-schemas DIR` | 요청·응답·선택자 등 JSON Schema 생성 |
| `--shutdown` | 호스트 종료(열린 세션을 닫는다) |

종료 코드: `completed` 0, `completionUnknown` 2, 그 밖의 거부·실패 1.

Agent:

| 명령 | 설명 |
|---|---|
| `Agent.exe [--serve]` | 실행. `--controller HOST:PORT`(기본 `127.0.0.1:25001`), `--machine NAME`(기본은 컴퓨터 이름을 소문자로), `--state DIR`(기본 `%LOCALAPPDATA%\DesktopTestDriver\Agent`) |
| 추가 옵션 | `--session N`(대상 대화형 세션, 기본은 현재), `--max-hold-ms N`(도구가 누른 키를 붙잡아 둘 수 있는 시간, 기본 1500), `--max-stop-ms N`(정리 대기, 기본 3000) |
| `Agent.exe --stop [--state DIR]` | 같은 상태 폴더로 실행 중인 Agent를 멈춘다 |
| `Agent.exe --environment` | 현재 세션이 Agent를 실행할 수 있는지(대화형 세션, Default 데스크톱, 권한 수준) |

Agent는 로그인된 사용자의 바탕화면(콘솔 또는 연결된 RDP)에서 실행해야 한다. 서비스·잠긴 화면·연결이 끊긴 세션에서는 `NoInteractiveSession`으로 시작하지 않는다. Controller와 연결이 끊기면 0.5초마다 다시 접속하며, 끊길 때 열려 있던 세션은 닫는다.

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
- 알 수 없는 필드, 중복 키, 범위를 벗어난 인자는 실행 전에 거부한다. 오류 메시지는 무엇이 틀렸는지(허용 필드 목록 포함)와 그 작업의 인자 목록(`--operations`와 같은 내용)을 함께 알려 준다.
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
| `failed` | 실제 입력을 일부 보낸 뒤 실패했다(보낸 이벤트 수는 `error.data.inputEventsSent`, 해제 여부는 `error.data.inputReleased`). 또는 `release-input`이 입력을 모두 떼지 못했다 | 화면을 다시 관측하고 판단한다 |
| `completionUnknown` | 앱에 영향을 줄 수 있는 호출(UIA 패턴 호출, 앱 실행·창 닫기 등)을 시작했고 끝났는지 확인하지 못했다. UIA 조작 뒤 원하는 상태가 되지 않은 `PostconditionFailed`도 여기에 든다. 세션이 `blocked`가 된다 | 같은 조작을 반복하지 않는다. 관측으로 결과를 확인하고 `--session-reconcile … --resume` |

오류는 `error.code`, `error.message`, `error.data`로 온다. 한글은 이스케이프하지 않은 UTF-8이다. 값이 `null`인 필드는 응답에서 빠진다.

`RequestTimeout`은 시점에 따라 갈린다. 읽기 요청이나 디스패치 전의 변경 요청이면 `rejected`이고 세션은 그대로다. 디스패치 중이었으면 `completionUnknown`이다. Controller는 Agent에 제한 시간보다 150ms 짧은 시간을 넘겨 Agent의 응답이 먼저 오게 한다.

## 5. 세션 상태

| state | 뜻 |
|---|---|
| `active` | 정상 |
| `blocked` | 미확정 변경(`completionUnknown`)이 있다, 입력 해제에 실패했다, 입력 lease가 회수됐다, 도구가 누른 키가 `--max-hold-ms`(기본 1.5초)를 넘어 자동으로 떼졌다, 명시적으로 일시 중지했다. 읽기는 계속된다. 원인은 `pauseReason`·`stopReason`·`pendingUnknownRequestIds`에 있다 |
| `closed`, `expired` | 닫혔거나 유휴 만료됐다(차단된 세션도 만료된다). 24시간 뒤 기록을 지운다 |

읽기 요청의 시간 초과, 디스패치 전 거부는 세션 상태를 바꾸지 않는다. `release-input` 뒤에는 미확정 요청과 일시 중지가 없으면 `active`로 돌아간다.

`blocked` 세션에서는 변경 요청만 `SessionBlocked`로 거부된다. 읽기 작업(`windows`, `find-window`, `wait-window`, `inspect`, `find-element`, `capabilities`, `wait-condition`, `read-text`, `capture` 등)으로 실제 결과를 확인한 뒤 reconcile한다. reconcile은 판단을 기록하고 차단을 푸는 것일 뿐이다. 무언가를 취소하거나 다시 실행하지 않는다. 앱 안에서 아직 진행 중인 조작은 스스로 끝난다.

세션 정보의 `inputReleased`·`applicationsStopped`는 정리 결과다. 세션이 열려 있는 동안에는 없고, 해제·닫기 뒤에 채워진다. `terminatedProcessIds`는 세션이 띄운 앱 중 이제 실행 중이 아닌 것(먼저 스스로 끝난 앱 포함), `closedWindowIds`는 `close-window`로 닫았거나 세션을 닫을 때 닫은 창이다.

**입력 세션의 lease.** `--input` 세션은 열 때, 재개할 때, `release-input`으로 `active`가 될 때 30초 lease를 받고, 변경 요청이 성공할 때마다 `지금 + 30초`로 연장된다. 상한은 받은 시점부터 15분이다(`leaseExpiresAt`, `leaseHardLimitAt`). 30초 동안 성공한 조작이 없거나, 상한에 닿거나, 사람의 키·마우스 입력이 감지되면 회수되고 세션이 `blocked`가 된다. `--session-resume`으로 새 lease를 받는다. lease는 실제 입력 작업에만 필요하다. 같은 세션의 UIA 조작과 읽기는 lease와 무관하다.

**세션 수명.** 세션의 참조(`e…`)는 세션마다 최근 8,192개까지 유지된다. 오래된 참조는 `StaleElement`가 된다. Agent가 재시작되면 이전 세션은 정리되어 닫힌다.

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
| `windows` | `kind?`(`main`·`owned`·`popup`), `includePopups?` | `windows[]`: `windowId`(`w1`…, 같은 창이면 같은 ID), `title`, `pid`, `processName`, `bounds`(화면 좌표의 클라이언트 영역), `foreground`, `className`, `kind`, `ownerWindowId`, `minimized`. 기본은 팝업을 빼고, `includePopups: true` 또는 `kind: "popup"`이면 넣는다 |
| `find-window` | `title`·`titleContains`·`titleRegex`·`className`·`kind`·`processName`·`ownerWindowId` 중 하나 이상 | `window`. 없으면 `WindowNotFound`, 여러 개면 `AmbiguousTarget`과 후보 |
| `wait-window` | 위와 같음 + `pollMilliseconds`(기본 100), `absent`(true면 일치하는 창이 하나도 없을 때까지 기다린다: 대화상자 닫힘, 앱 종료 확인) | `satisfied`, `window`, `polls`. `absent`가 시간 초과면 남은 창 `windows` |
| `window-state` | `windowId`, `state`(`minimize`·`maximize`·`restore`·`close`) | UIA WindowPattern, 없으면 Win32 |
| `focus` | `windowId` | 창을 전경으로 가져온다. 실패하면 `FocusDenied` |
| `close-window` | `windowId`, `waitMilliseconds`(0–5000, 기본 2000) | 창에 닫기(`WM_CLOSE`)를 요청하고 닫힐 때까지 기다린다. `closed`, `processExited`, `newWindows`. `closed: false`이고 `newWindows`가 있으면 앱이 확인창("저장할까요?")을 띄운 것이다. 그 창의 버튼을 `invoke`한 뒤 `wait-window absent`로 확인한다. 닫힌 창은 세션의 `closedWindowIds`에 기록된다 |
| `launch` | `executable`(절대 경로의 실행 파일), `arguments[]`, `lifetime`(`session` 기본·`keep`), `workingDirectory`, `verb`(`open`·`runas`), `waitWindow{title, titleContains, className, timeoutMilliseconds(생략하면 요청의 남은 시간)}` | `processId`, `pid`, `imagePath`, `lifetime`, `window`, `waitSatisfied`. `waitWindow`가 없으면 창을 기다리지 않는다 |
| `open` | `target`(URI·`control.exe /name …`·`.msc`·`.cpl`·문서), `arguments[]`, `verb`, `workingDirectory`, `waitWindow` | `pid`, `processName`(실행된 프로세스를 알 수 있을 때), `window`, `waitSatisfied` |
| `terminate-owned` | `processIds[]`(`launch` 결과의 `processId`) | 세션이 띄운 프로세스 종료 |

`launch`의 `session` 수명 앱은 job object에 넣어 자식 프로세스와 함께 세션 종료 때 멈춘다. `keep`은 세션을 닫아도 남는다(`retainedProcessIds`).

`open`은 실행 전후의 최상위 창 목록을 비교해 새로 나타난 창을 찾는다. `waitWindow`가 없으면 2초 동안 아무 새 창이나 기다린다. 조건(`title` 등)을 주면 이미 열려 있던 창도 일치하면 돌려주는데, 그 창은 세션 범위에만 들고 세션을 닫을 때 닫지 않는다. 새로 연 창은 세션을 닫을 때 WindowPattern, 안 되면 `WM_CLOSE`로 닫고 `closedWindowIds`·`retainedWindowIds`로 보고한다. `explorer.exe` 같은 공유 프로세스는 종료하지 않는다.

`verb: "runas"`는 Agent가 관리자로 실행 중일 때만 된다. 일반 권한 Agent에서는 UAC 보안 데스크톱을 조작할 수 없으므로 `ElevationRequired`로 미리 거부한다. 일반 권한 Agent가 관리자 권한 창을 다루려 하면 `TargetElevated`로 거부한다.

### 관측

**`inspect`** — `windowId`, `format`(`compact` 기본·`json`), `maxDepth`(1–16), `rootElementId`+`rootSnapshotToken`(서브트리), `interactiveOnly`, `includeOffscreen`(기본 true), `includeBounds`, `treeView`(`control` 기본·`raw`·`content`).

```text
- Window "간단 메모" [e1 t=3fa9c1d20b7e]
  - Edit #MemoText "메모" [e2 t=91b04c77a2d1] value="테스트 메모입니다" patterns=Value
  - Button #SaveButton "저장" [e3 t=5d02e8b1c9f0] patterns=Invoke
  - Button #OpenButton "열기" [e4 t=0a7711e3bb52] disabled patterns=Invoke
```

한 줄이 요소 하나이고 자식은 부모 바로 아래에 들여 쓴다: `ControlType #AutomationId "Name" [참조 t=토큰]` 뒤에 기본값이 아닌 속성(`disabled`, `offscreen`, `value=`, `password`, `toggle=`, `selected`, `expanded=`, `focused`, `range=`, `patterns=`, `box=x,y,w,h`)만 붙는다. 이름과 값은 200자로 자르고 `…(+N)`을 붙인다. 참조와 토큰은 그 세션의 그 창에서만 유효하다. 토큰은 12자 앞부분이며 8자 이상의 앞부분을 보내도 된다.

결과: `tree`(compact 문자열, `json`이면 노드 배열), `nodeCount`, `complete`, `truncationReason`(`node-limit`·`depth-limit`·`virtualized-items-not-enumerated`·`provider-errors`), `virtualizedContainers`(참조). 한 번에 2,048개 노드까지 읽는다. JSON 노드에는 `bounds`(창 클라이언트 기준 물리 픽셀), `selected`, `expandState`, `focused`, `rangeValue`, `helpText`, `itemStatus`, `nameTruncated`가 값이 있을 때만 붙는다.

**선택자** — `automationId`, `automationIdRegex`, `controlType`(`Button`, `Edit`, `ListItem` 같은 UIA 컨트롤 형식 이름), `name`, `nameContains`, `nameStartsWith`, `nameRegex`(.NET 정규식, 256자 이하), `className`, `frameworkId`, `ancestorAutomationId`, `parent`(선택자, 8단계까지), `index`(0부터, 문서 순서, 최상위 선택자에만). 이름 연산자와 automationId 연산자는 각각 하나만 쓴다. 문자열 비교는 대소문자를 구분한다.

**`find-element`** — `windowId`, `selector` 또는 `selectors[]`(16개까지, 한 번에 찾는다), `searchRootElementId`+`searchRootSnapshotToken`, `includePath`, `completeness`, `treeView`. 결과는 `target`, `matchCount`, `uniquenessScope`, `path`(`includePath`일 때 조상 노드). `selectors[]`이면 `results[]`에 선택자마다 `{target, matchCount, uniquenessScope}` 또는 `{error}`가 온다.

- 여러 개가 일치하면 `AmbiguousTarget`이고 `error.data.candidates`에 후보 5개(참조, `automationId`, `name`, `controlType`, 부모 이름)와 `matchCount`가 온다. 선택자를 좁히거나 `index`를 준다.
- 없으면 `ElementNotFound`이고 `error.data.nearest`에 비슷한 요소 3개, `error.data.context`에 창의 위 2단계 트리와 범위 안 창 목록이 온다(`errorContext: false`로 끈다).

**가상화 컨테이너.** 목록·그리드·콤보 상자처럼 `ItemContainer`를 노출하는 컨테이너가 창에 있으면, 실체화되지 않은 항목은 트리에 없다. 그래서 다음 규칙을 쓴다.

| 경우 | 결과 |
|---|---|
| 컨테이너가 없다 | 보통 검색. `uniquenessScope: "complete"` |
| 선택자에 `automationId`가 있고 일치가 하나이며 그 조상에 컨테이너가 없다 | 성공. `uniquenessScope: "outside-virtualized"` |
| 그 밖(이름만으로 찾기, 컨테이너 안의 요소) | `SearchIncomplete`. `completeness: "realized"`를 주면 실체화된 범위에서 찾고 `uniquenessScope: "realized-only"`와 `virtualizedContainers`(참조)를 붙인다 |

컨테이너 안의 항목은 `find-item`(이름으로 찾기) → `scroll-item-into-view` 또는 `realize-item` → `completeness: "realized"`로 찾는다. `absent` 조건은 `realized`를 줄 때만 "실체화된 범위에 없음"으로 판정한다.

**`capabilities`** — `elementId`+`snapshotToken`, `selector`, 또는 `elements[]`(`{elementId, snapshotToken}` 16개) 중 하나. 현재 값·상태를 다시 읽어 `target`(또는 `results[]`)으로 돌려준다. `includeDescriptors: true`이면 작업 설명(`operationDescriptors`)을 붙인다. `Value` 패턴이 없는 Win32 컨트롤은 `LegacyIAccessible` 값으로 대체하고 `value.source: "legacy"`를 표시한다.

| 작업 | 인자 | 결과 |
|---|---|---|
| `find-item` | 컨테이너의 `elementId`+`snapshotToken`, `itemName` | 실체화되지 않은 항목도 이름으로 찾아 `target`으로. 이 참조는 `scroll-item-into-view`·`realize-item`·`select-item`에 쓴다 |
| `read-grid` | 그리드 요소, `startRow`(0), `rowCount`(기본 50), `startColumn`(0), `columnCount`(기본 전부) | `rowCount`, `columnCount`(전체 크기), `columnHeaders`, `rows`(셀 값 또는 이름). 셀 500개까지 |
| `read-text` | 요소, `selectionOnly`, `maxLength`(기본 65,536) | `text`, `source`(`text`·`value`·`legacy`), `truncated` |
| `generate-selector` | 요소 | `selector`(그 창에서 유일한 가장 짧은 선택자), `uniquenessScope`, `alternatives[]`. 각 후보가 정확히 그 요소 하나에 일치하는지 실제로 확인한다. 반복 실행할 코드에는 참조 대신 이것을 저장한다 |
| `clipboard-get` | 없음 | `text`(텍스트가 없으면 생략) |

**`capture`** — `windowId`, `region{x,y,width,height}`(창 클라이언트 좌표) 또는 `elementId`+`snapshotToken`(그 요소의 영역), `scale`(0.25–1), `output`(절대 경로, 생략하면 Controller 상태 폴더의 `captures/`). 결과는 `path`, `captureId`, `imageWidth`, `imageHeight`, `bytes`, `sha256`, `region`, `scale`. `output`에 파일이 이미 있으면 `OutputExists`로 거부하고, 최소화된 창은 `WindowMinimized`다. 가려진 창도 창 내용을 캡처한다(Windows.Graphics.Capture). 캡처 좌표는 30초 동안 `click`·`move`·`drag`의 `captureId`로 쓸 수 있고, 그 사이 창이 움직이거나 크기가 바뀌면 `StaleCapture`다.

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

`value-…`, `text-contains`는 `Value`를 본다. WinForms `Label`, WPF `TextBlock`의 글자는 `Name`에 있으므로 `name-equals`·`name-contains`로 기다린다. 결과는 `satisfied`, `matchedIndex`(anyOf일 때 먼저 충족된 번호), `target`, `polls`. **시간 초과는 오류가 아니라 `completed` + `satisfied: false`**이며 마지막 관측값(`target`, anyOf면 `targets`)과 `lastError`를 담는다. 대기 시간은 요청의 `timeoutMilliseconds`(기본 5000, 최대 30000)다.

폴링 중 `StaleElement`나 일시적 읽기 실패는 다음 폴링으로 넘긴다. `AmbiguousTarget`과 가상화로 인한 `SearchIncomplete`는 즉시 실패한다. 단, 가상화 컨테이너가 있는 창에서 `automationId`로 기다리는 요소가 아직 없을 때는 "아직 없음"으로 보고 계속 기다린다(`absent`는 이 경우 판정할 수 없어 즉시 실패한다).

### 조작

UIA 조작은 `windowId`, `elementId`, `snapshotToken`과 작업별 인자를 받는다: `set-value`(`value`, `expectedValueHash`=현재 값 UTF-8의 SHA-256), `set-range-value`(`value`), `set-toggle`(`desiredState`: `on`·`off`·`indeterminate`, 3상태 컨트롤은 원하는 상태가 될 때까지 최대 3회 토글), `set-expanded`(`expanded`), `scroll-container`(`horizontal`, `vertical`: 0–100, 바꾸지 않으면 -1).

Agent는 한 번의 작업자 왕복에서 요소를 다시 확인하고(사라짐·변경 → `StaleElement`), 활성·패턴·읽기 전용·기대 값을 확인한 뒤 디스패치한다(`ElementDisabled`, `PatternNotSupported`, `ValueReadOnly`, `ValueConflict`, `InvalidRangeValue`). 이 단계의 거부는 모두 `rejected`이며 세션에 영향이 없다. 결과는 `target`(디스패치 직전의 노드)과 `dispatchKind`(어떤 경로로 패턴을 호출했는지)다. `clipboard-set`은 `text`만 받는다.

**모달 창을 여는 조작.** Win32 대화상자 버튼이나 많은 WinForms 버튼은 자신이 연 모달 창(확인창, 대화상자)이 닫힐 때까지 UIA 호출이 돌아오지 않는다. Agent는 패턴 호출을 별도 스레드에서 실행하고, 400ms가 지나도 끝나지 않았는데 대상 프로세스에 새 창이 생겼으면 기다리지 않고 응답한다.
- 응답은 `completed`이고 `data.dispatchState: "pending-modal"`과 `data.modalWindows`(바로 쓸 수 있는 windowId)가 붙는다.
- 세션은 `active`로 남는다.
- 그 창의 버튼을 `invoke`하거나 `close-window`로 답하면 원래 조작이 스스로 끝난다.
- 이 경우 `expect`는 건너뛰고 `observeAfter`는 실행한다.

**자기 창을 닫는 조작.** 대화상자의 확인·저장·예 버튼처럼 조작이 그 창을 닫으면 오류가 아니다.
- 응답은 `completed`이고 `data.windowClosed: true`가 붙는다.
- `observeAfter`는 `data.after.newWindows`만 돌려주고, `expect`는 `skipped: "WindowClosed"`로 표시된다.
- 결과는 `wait-window`나 소유자 창에서 확인한다.

창 ID는 세션 안에서 다시 쓰지 않는다. 앱을 다시 실행하면 창 ID와 참조가 모두 새로 나온다.

UIA 조작과 `click`·`type-text`·`key`·`chord`는 선택 인자 두 개를 받는다. 실패해도 조작 결과는 바뀌지 않고 `data.afterError`에 코드만 남는다.

| 인자 | 의미 |
|---|---|
| `expect` | `{selector, condition, expected?, timeoutMilliseconds(기본 2000, ≤5000), pollMilliseconds?(기본 50)}`. 디스패치 뒤 조건을 기다려 `data.expect`(`satisfied`, `target`, `polls`)에 넣는다. 요청의 남은 시간 안에서 기다린다 |
| `observeAfter` | `{rootElementId?, rootSnapshotToken?, settleMilliseconds(기본 100, 0–1000), maxDepth?(기본 6), interactiveOnly?(기본 true), includeNewWindows?}`. 조작 뒤 서브트리를 `data.after.tree`에(기본은 상호작용 요소와 그 조상만), 조작 전에 없던 범위 안 창(팝업 포함)을 `data.after.newWindows`에 넣는다. `data.after.tree`의 참조는 조작한 창의 windowId로 쓴다 |

한 요청에 여러 조작 단계를 담는 묶음 요청은 없다.

### 실제 입력

`--input` 세션에서만 된다. 키보드 작업은 대상 창이 전경이어야 한다(아니면 `ForegroundMismatch`, 먼저 `focus`).

| 작업 | 인자 |
|---|---|
| `click` | `elementId`+`snapshotToken` 또는 `position{x,y,captureId?}`(창 클라이언트 좌표, `captureId`가 있으면 캡처 픽셀), `button`, `count`(1–3), `modifiers`(`CTRL`·`SHIFT`·`ALT`) |
| `type-text` | `text`(4096자, 개행·탭 허용). 500개 이벤트 단위로 한 번에 보낸다 |
| `key` | `key`, `action`(`press` 기본·`down`·`up`). `down`으로 누른 키는 `--max-hold-ms`(기본 1.5초) 안에 떼지 않으면 자동으로 떼지고 세션이 `blocked`가 된다 |
| `chord` | `keys[]`(8개까지) |
| `move`, `wheel` | `position`, `delta`(±1200) |
| `drag` | `start`, `end`, `captureId?`, `button`, `steps` |
| `release-input` | 이 세션이 누른 키·버튼을 모두 뗀다 |

키 이름: `A`–`Z`, `0`–`9`, `F1`–`F24`, `NUMPAD0`–`NUMPAD9`, `CTRL`, `ALT`, `SHIFT`, `WIN`, `ENTER`, `TAB`, `ESC`, `BACKSPACE`, `DELETE`, `INSERT`, `SPACE`, 방향키, `HOME`, `END`, `PAGEUP`, `PAGEDOWN`, `APPS`, `PRINTSCREEN`, `-` `=` `,` `.` `/` `;` `'` `[` `]` `\` `` ` ``.

요소 클릭은 먼저 요소 영역이 50ms 간격의 두 번 읽기에서 같은지 확인하고(최대 500ms, 아니면 `ElementNotStable`, 영역이 없으면 `ElementNotVisible`), 그 중심점의 요소가 대상이나 그 자손인지 확인한다. 다르면 `TargetObscured`와 `error.data.blockedBy`(가리는 요소의 이름·종류·창 제목)로 거부한다. 포인터를 옮긴 뒤 그 아래 창이 대상 창이 아니면 `PointerTargetMismatch`다. 결과에는 `inputEventsSent`와 클릭한 화면 좌표 `screen`이 온다. 최소화된 창에는 입력을 보내지 않는다(`WindowMinimized`).

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
| `ElementDisabled`, `ValueReadOnly`, `PatternNotSupported`, `InvalidRangeValue` | 지금 그 조작을 할 수 없다 | 앱 상태를 확인한다 |
| `ValueConflict` | `expectedValueHash`와 현재 값이 다르다 | 값을 다시 읽고 판단한다 |
| `PostconditionFailed` | 조작 뒤 원하는 상태가 되지 않았다(`completionUnknown`) | 관측으로 확인하고 reconcile |
| `WindowChanged`, `WindowNotFound`, `WindowMinimized` | 창이 닫혔거나 다시 만들어졌거나 최소화됐다 | `windows`·`find-window`로 다시 찾거나 `window-state restore` |
| `TargetScopeMismatch` | 세션 범위 밖이거나 다른 창의 참조 | 세션을 범위와 함께 열거나 그 창의 참조를 쓴다 |
| `SessionBlocked` | 미확정 요청, lease 회수, 일시 중지 | `--session-status`로 원인을 보고 `--session-reconcile … --resume` 또는 `--session-resume` |
| `SessionNotFound`, `SessionClosed`, `SessionExpired` | 세션이 없거나 끝났다 | 새 세션을 연다 |
| `ReadOnlySession` | `--observe` 세션에서 변경 요청 | 제어 세션을 연다 |
| `InputLeaseRevoked` | 사람의 입력이 감지됐거나 lease가 끝났다 | 사람이 작업을 마친 뒤 `--session-resume` |
| `InputSessionRequired`, `ControlSessionBusy` | 실제 입력은 `--input` 세션에서만, 그 세션은 Agent당 하나 | 세션을 `--input`으로 열거나 기존 입력 세션을 닫는다 |
| `ForegroundMismatch`, `PointerTargetMismatch`, `TargetObscured`, `ElementNotStable`, `ElementNotVisible` | 실제 입력의 전제가 맞지 않는다 | `focus`, 가리는 창 정리, 잠시 뒤 재시도 |
| `ElevationRequired`, `TargetElevated` | 권한 부족 | Agent를 관리자로 실행한다 |
| `RequestTimeout` | 제한 시간 안에 끝나지 않았다 | 읽기는 그대로 다시 보낸다. 변경은 `status`로 효과 여부를 본다 |
| `ProviderTimeout`, `ProviderUnavailable`, `ProviderError` | 대상 앱의 UIA 제공자가 응답하지 않거나 오류를 냈다. 작업자 프로세스는 다시 시작된다 | 앱이 멈췄는지 확인하고 다시 보낸다 |
| `ResponseTooLarge` | 응답이 8MiB를 넘는다 | `maxDepth`, `rootElementId`, `interactiveOnly`로 범위를 줄인다 |
| `DuplicateRequestMismatch` | 같은 `requestId`를 다른 내용에 썼다 | 새 `requestId`를 쓴다 |
| `OutputExists` | 캡처 출력 파일이 이미 있다 | 다른 경로를 준다 |
| `EvidenceBudgetExceeded` | 저널 64MiB 또는 캡처 256MiB에 닿았다 | 24시간 정리를 기다리거나 상태 폴더를 비운다 |
| `ControllerUnavailable` | 호스트가 없거나 다른 `--state`를 쓰고 있다 | `Controller.exe --serve`, 같은 `--state` |
| `AgentDisconnected`, `MachineRequired` | Agent가 연결되지 않았거나, 여럿이라 `--machine`이 필요하다 | Agent 실행과 주소를 확인한다 |
| `NoInteractiveSession` | Agent가 대화형 바탕화면에서 실행되지 않았다 | 로그인된 세션에서 Agent를 실행한다 |

`completionUnknown`을 받은 조작은 같은 조작을 다시 보내지 않는다. 관측으로 결과를 확인한 뒤 `effect-confirmed`·`no-effect-confirmed`·`abandoned` 중 하나로 확정한다. 응답을 잃었으면 `--request-status`로 같은 요청의 결과를 조회한다.

## 9. 저장되는 것

| 파일 | 내용과 보관 |
|---|---|
| `request-*.json`, `journal-index.json` | 변경 요청과 `capture`의 기록(평문 JSON)과 그 색인. 완료 뒤 24시간, 미확정은 확정될 때까지 둔다. 정리는 60초마다 색인만 보고 한다. 합계 64MiB에 닿으면 변경 요청을 거부한다 |
| `session-*.json` | 세션 상태. 닫히거나 만료된 지 24시간 뒤 삭제 |
| `reconciliation-*.json` | reconcile 기록 |
| Agent `cap-*.png`, `cap-*.json` | 캡처 사본과 좌표 기록. PNG는 Controller가 받으면 지우고, 아니면 24시간 뒤 지운다(미확정 캡처 요청에 딸린 것은 확정될 때까지). 좌표 기록은 30초 뒤 지운다. 합계 256MiB에 닿으면 캡처를 거부한다 |
| Controller `captures/` | `output`을 생략한 캡처(`세션-요청.png`). 자동으로 지우지 않는다 |
| `applications.json`, `held-input.json` | 세션이 띄운 앱, 도구가 누르고 있는 키·버튼(watchdog이 읽는다) |
| `watchdog-recovery.json` | watchdog이 정리한 결과 |

Agent가 죽거나 멈추면(3초간 생존 신호 없음) watchdog 프로세스가 누른 키·버튼을 떼고 세션이 띄운 앱을 종료한다. 생존 신호와 정지 요청은 이름 있는 이벤트로 주고받으며 디스크에 쓰지 않는다.

## 10. MCP 도구

`Controller.exe --mcp [--state DIR] [--listen ADDR:PORT]`. 같은 상태 폴더의 호스트가 떠 있으면 그것을 쓰고, 없으면 MCP 서버 프로세스 안에서 호스트를 띄운다(MCP 서버가 끝나면 같이 끝난다).

| 도구 | 받는 것 | 작업 |
|---|---|---|
| `gui_session` | `action`과 `machineId`, `sessionId`, `input`, `observe`, `launch`, `launchArguments`, `targets{pids, executables, windowTitle, windowTitleContains, windowClass, any}`, `idleSeconds`, `requestId`, `resolution`, `resume` | `open`, `close`, `status`, `resume`, `pause`, `reconcile`, `request-status`, `agents`(=`--status`), `doctor` |
| `gui_window` | `sessionId`, `operation`, `arguments`, `timeoutMilliseconds?`, `requestId?`, `note?` | `windows`, `find-window`, `window-state`, `focus`, `close-window`, `launch`, `open`, `terminate-owned` |
| `gui_observe` | 위와 같음 | `inspect`, `find-element`, `capabilities`, `find-item`, `read-grid`, `read-text`, `generate-selector`, `clipboard-get` |
| `gui_act` | 위와 같음 | UIA 조작, 실제 입력, `release-input`, `clipboard-set` |
| `gui_wait` | 위와 같음 | `wait-condition`, `wait-window` |
| `gui_capture` | `sessionId`, `arguments`, `image?` | `capture`. `image: true`이면 PNG도 이미지 콘텐츠로 돌려준다 |

도구 결과는 응답 JSON 한 개이며, `status`가 `completed`가 아니면 `isError: true`다. `requestId`를 생략하면 자동으로 만든다. 같은 변경을 안전하게 다시 보내려면 `requestId`를 직접 정한다.

## 11. C# Client

`src/Client`의 `DesktopClient`는 같은 파이프로 호스트를 부른다. 탐색한 흐름을 모델 없이 반복 실행하는 테스트 코드에 쓴다. 전체 예제는 `examples/standalone/Inspect.cs`에 있다.

```csharp
var client = new DesktopClient(new ControllerSettings(@"C:\dtd\controller-state"));   // 호스트와 같은 상태 폴더
var opened = (await client.OpenSessionAsync(launch: @"C:\GuiTest\FormsWorkbench.exe")).EnsureCompleted();
var session = opened.ControlSessionId();
var window = opened.Json().GetProperty("launch").GetProperty("window").GetProperty("windowId").GetString()!;
var editor = (await client.FindElementAsync(session, window, new(AutomationId: "Editor"))).EnsureCompleted().Ref(window);
await client.SetValueAsync(session, editor, "입력값");
var apply = (await client.FindElementAsync(session, window, new(AutomationId: "Apply"))).EnsureCompleted().Ref(window);
await client.InvokeAsync(session, apply);
var waited = await client.WaitAsync(session, window, new(AutomationId: "Result"), "value-equals", "입력값");
if (!waited.EnsureCompleted().Satisfied()) throw new Exception("결과가 바뀌지 않았다");
await client.CloseSessionAsync(session);
```

`EnsureCompleted()`는 `completed`가 아니면 예외를 던지고, `Target()`, `Ref(window)`, `Tree()`, `Window()`, `Windows()`, `Satisfied()`가 응답의 `data`를 형식 있는 값으로 꺼낸다. `WaitAsync`는 `pollMilliseconds`, `searchRoot`를 받고 `WaitAnyAsync`는 `anyOf`를 보낸다. 세션·창·관측·UIA 조작·실제 입력·재개·reconcile 메서드가 모두 있다. 취소 토큰은 기다림만 멈추며, 변경 요청이 취소되면 결과는 `completionUnknown`으로 본다.
