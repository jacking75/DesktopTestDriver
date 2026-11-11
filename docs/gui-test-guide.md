# 간단한 GUI 프로그램을 테스트하는 사용 매뉴얼

> **신뢰된 네트워크와 PC에서만 사용한다. 연결은 암호화하지 않는다.**

이 문서는 메모 프로그램(`fixtures/SimpleMemo`)에 글을 쓰고, 저장하고, 닫았다가 다시 열어 내용을 확인하는 시험을 예로 든다. 명령과 요청 형식의 전체 규칙은 [사용 규약](agent-usage.md)에 있다.

## 먼저 시험 방식과 성공 기준을 정한다

| 정할 것 | 예 |
|---|---|
| 성공 기준 | 다시 연 메모의 내용이 입력한 글과 같다. 요청이 `completed`인 것만으로는 통과가 아니다 |
| 조작 방식 | 기본은 UIA 조작(`set-value`, `invoke`)이다. 키보드 입력 경로 자체를 시험할 때만 `--input` 세션과 `type-text`를 쓴다 |
| 전체 마감 | 예: 2분. 요청 하나는 최대 5초, 대기는 최대 30초다 |
| 남길 근거 | 실패한 요청의 `requestId`, 응답, 필요하면 캡처 |

## 1. 실행한다

같은 PC라면 배포 폴더(`bin\live_Release`)에서 호스트 두 개를 띄운다. `--serve`와 Agent는 끝날 때까지 실행되므로 각각 별도 터미널에서 실행하거나 `scripts\manage.ps1 -Action Start`로 백그라운드에 띄운다.

```powershell
.\Controller\Controller.exe --serve      # 터미널 1
.\Agent\Agent.exe                        # 터미널 2
.\Controller\Controller.exe --status     # 터미널 3: 이후 명령은 모두 여기서
```

아래 예시는 `Controller` 폴더 안에서 실행한다고 보고 `.\Controller.exe`로 적는다. 호스트를 `--state`로 띄웠다면 모든 명령에 같은 `--state`를 준다.

테스트 PC가 따로 있으면 Controller를 `--listen 0.0.0.0:25001`로, Agent를 `--controller 주소:25001 --machine test-pc`로 실행한다. 설정 파일·인증서·등록 절차는 없다. `--doctor`로 Agent가 실제로 UIA·캡처·입력 감시를 쓸 수 있는지 확인한다.

## 2. 세션을 열면서 프로그램을 띄운다

```powershell
$open = .\Controller.exe --session-open --launch C:\GuiTest\SimpleMemo.exe | ConvertFrom-Json
$session = $open.controlSessionId
$window = $open.data.launch.window.windowId      # 예: w1
```

`--launch`는 세션 열기, 실행, 첫 창 대기를 한 번에 한다. 이 프로그램은 세션을 닫을 때 함께 종료된다. 이미 떠 있는 프로그램이면 `--target-pid` 또는 `--target-window-title`로 연다.

## 3. 화면을 읽는다

```powershell
.\Controller.exe --inspect --session $session --window $window
```

```text
- Window "간단 메모" [e1 t=167cdf32baf7]
  - Button #OpenButton "열기" [e3 t=fffaf470023a] patterns=Invoke
  - Button #SaveButton "저장" [e4 t=1d93122bc0be] patterns=Invoke
  - Edit #MemoText [e5 t=b2d40c2611bd] value="" patterns=Value,Scroll
```

`e5`와 `t=…`가 조작에 쓰는 참조와 토큰이다. 화면 전체 대신 일부만 필요하면 `maxDepth`, `rootElementId`, `interactiveOnly`를 쓴다. 반복 실행할 시험이라면 참조 대신 `#MemoText` 같은 `automationId` 선택자를 쓰고, ID가 없는 요소는 `generate-selector`로 선택자를 얻는다.

## 4. 한 단계씩 조작하고 확인한다

요청은 json-lines나 MCP로 보내면 프로세스를 매번 띄우지 않아 빠르다. 아래는 요청 본문이다.

**글 입력과 확인을 한 번에:**

```json
{"requestId":"memo-1","controlSessionId":"…","operation":"set-value","note":"메모 입력",
 "arguments":{"windowId":"w1","elementId":"e5","snapshotToken":"b2d40c2611bd","value":"테스트 메모",
   "expect":{"selector":{"automationId":"MemoText"},"condition":"value-equals","expected":"테스트 메모"}}}
```

응답의 `data.expect.satisfied`가 `true`면 값이 들어갔다.

**저장 버튼:**

```json
{"requestId":"memo-save","controlSessionId":"…","operation":"invoke",
 "arguments":{"windowId":"w1","elementId":"e4","snapshotToken":"1d93122bc0be","observeAfter":{"includeNewWindows":true,"maxDepth":2}}}
```

`data.after.newWindows`에 저장 대화상자가 나온다. 대화상자도 같은 방식으로 `find-element`(`selector: {"controlType":"Edit"}` 등) → `set-value` → `invoke`로 다룬다. 대화상자가 뜨기를 따로 기다릴 때는 `wait-window`를 쓴다.

**완료 표시를 기다린다:**

```json
{"requestId":"r-7","controlSessionId":"…","operation":"wait-condition","timeoutMilliseconds":10000,
 "arguments":{"windowId":"w1","anyOf":[
   {"selector":{"automationId":"TitleBar"},"condition":"name-contains","expected":"memo.txt"},
   {"selector":{"controlType":"Window","name":"오류"},"condition":"exists"}]}}
```

`satisfied: true`와 `matchedIndex`로 어느 쪽이 먼저 나타났는지 안다. 시간 초과는 `satisfied: false`로 온다(오류가 아니다).

## 5. 닫고 다시 열어 확인한다

창을 닫고(`close-window`), 같은 파일로 다시 실행한다.

```json
{"requestId":"memo-reopen","controlSessionId":"…","operation":"launch","timeoutMilliseconds":15000,
 "arguments":{"executable":"C:\\GuiTest\\SimpleMemo.exe","arguments":["C:\\GuiTest\\memo.txt"],
   "waitWindow":{"titleContains":"memo.txt","timeoutMilliseconds":10000}}}
```

응답의 `data.window.windowId`가 새 창이다. 창 ID와 요소 참조는 창마다 다르므로 앞 창의 `e5`를 쓰지 않는다. 새 창에서 `find-element`로 `MemoText`를 찾아 `value.text`가 입력한 글과 같은지 본다. 같아야 통과다.

## 6. 실패하거나 응답이 끊기면

| 상황 | 행동 |
|---|---|
| `rejected` | 아무 일도 없었다. 오류 코드를 보고 고쳐 다시 보낸다 |
| `StaleElement` | 화면이 바뀌었다. 다시 찾는다 |
| `AmbiguousTarget` | `error.data.candidates`를 보고 선택자를 좁힌다 |
| `completionUnknown` | 같은 조작을 다시 보내지 않는다. 화면을 읽어 실제 결과를 확인하고 `--session-reconcile 세션 --request-id ID --resolution effect-confirmed --resume`처럼 확정한다 |
| 응답을 잃었다 | `--request-status 세션 요청ID`로 기록된 결과를 본다 |

화면 모양까지 확인해야 하면 `capture`(`scale: 0.5`, `elementId`로 한 요소만)로 이미지를 남긴다.

## 7. 정리하고 보고한다

```powershell
.\Controller.exe --trace-export $session C:\GuiTest\trace     # 조작 기록(trace.jsonl)
.\Controller.exe --session-close $session
```

세션을 닫으면 세션이 띄운 프로그램이 종료되고 `open`으로 연 창이 닫히며 도구가 누른 키가 해제된다. 응답의 `terminatedProcessIds`, `closedWindowIds`, `retainedWindowIds`로 정리 결과를 확인한다. 시험에서 만든 파일은 직접 지운다. 보고에는 확인한 항목과 확인하지 못한 항목을 나눠 적는다.

## Windows 자체 GUI를 시험할 때

| 대상 | 여는 방법 | 참고 |
|---|---|---|
| 설정 앱 | `open` `{"target":"ms-settings:about","waitWindow":{"className":"ApplicationFrameWindow","timeoutMilliseconds":15000}}` | 프레임 창(`ApplicationFrameWindow`)을 돌려주며, 그 창의 UIA 트리에 설정 화면 내용이 함께 나온다. 화면을 그리는 데 시간이 걸리므로 `inspect`를 짧게 반복하거나 `wait-condition`으로 기다린다. 이미 설정 앱이 열려 있었다면 그 창을 돌려주고, 세션을 닫아도 닫지 않는다. 목록 컨트롤이 있어 `SearchIncomplete`가 나면 `automationId`로 찾거나 `completeness: "realized"`를 쓴다 |
| 제어판 | `open` `{"target":"control.exe","waitWindow":{"className":"CabinetWClass"}}` | `explorer.exe`가 창을 가진다. 세션을 닫으면 창만 닫고 explorer는 종료하지 않는다 |
| 관리 콘솔(.msc) | `open` `{"target":"services.msc","verb":"runas"}` | `runas`는 Agent를 관리자로 실행해야 한다. 아니면 `ElevationRequired`. 관리자 권한으로 뜬 창은 일반 권한 Agent에서 `TargetElevated`로 거부된다 |
| 메뉴·드롭다운 | `set-expanded` 뒤 `windows`에 `kind: "popup"` | 팝업은 제목 없는 별도 창이다 |
| Win32 목록·트리 | `read-grid`, `select-item`, `set-expanded` | `Value`가 없으면 `LegacyIAccessible` 값으로 읽는다(`value.source: "legacy"`). 패턴 조작이 동작하지 않으면 `legacy-default-action`. `SysListView32`·`SysTreeView32`·속성 시트는 아직 실제 대상으로 확인하지 않았다 |

Windows 버전에 따라 화면 구성이 다르므로 이런 대상의 시험은 "지정한 요소를 찾고 값을 읽는다"처럼 좁게 판정한다.
