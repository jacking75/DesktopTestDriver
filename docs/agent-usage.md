# 코딩 에이전트용 DesktopTestDriver 사용 규약

**문서 상태: 새 GUI 요청 인터페이스의 구현 목표 규약이다. 현재 Agent.exe에는 아래 명령이 아직 구현되지 않았다.** 구현자는 이 규약에 맞춰 개발하고, 실제 SSH·데스크톱 연동 확인이 끝난 배포본부터 사용 가능하다고 표시한다. 기존 Controller·시나리오 명령으로 이 예시를 임의 변환하지 않는다.

이 문서는 도구를 호출하는 코딩 에이전트가 읽는 문서다. 구조 변경과 구현 순서는 [설계·구현 계획](agent-driven-desktop-control.md)을 따른다. 두 문서가 다르면 구현 중 임의로 해석하지 말고 계약과 예시를 함께 수정한다.

## 코딩 에이전트에게 전달할 지시

> DesktopTestDriver의 `docs/agent-usage.md`를 읽고 원격 GUI를 조작하라. 이미 접속한 SSH에서 원격 Agent.exe를 실행한다. 먼저 --help와 --status로 설치된 버전·지원 기능을 확인하라. 창·요소·세션 ID는 실제 응답에서 얻고, 한 번의 GUI 명령씩 요청하라. 응답의 completed는 제품 성공이 아니다. 화면·UI·로그를 관측해 다음 행동을 결정하라. 변경 요청의 응답을 잃으면 같은 입력을 자동 재실행하지 말고 요청 결과를 조회하라. 시나리오·expect·bindings·run 파일은 만들지 마라.

사용 환경에서 Agent.exe의 원격 절대 경로와 이 문서를 전달한다. GUI 실행부의 설치·기동은 환경 준비 작업이다. 에이전트가 GUI 명령을 보낼 때마다 실행부를 재설치하거나 새로운 SSH 연결 도구를 만들지 않는다.

## 먼저 알아야 할 실행 방식

- 코딩 에이전트는 기존 SSH로 명령을 실행하고 파일을 전송한다.
- Agent.exe 요청 모드는 같은 머신의 로그인된 데스크톱 실행부에 로컬 IPC로 요청한다. 요청 프로세스는 응답 후 종료된다.
- 데스크톱 실행부는 계속 실행한다. 요청 프로세스 종료는 제어 세션 종료가 아니다.
- 제어 세션은 여러 명령을 이어서 조작하는 문맥이다. 시험 순서·성공 조건을 담지 않는다.
- 첫 지원 범위는 같은 Windows 계정이 접근하는 활성 콘솔의 일반 사용자 바탕화면이다. 로그인 세션·UAC·잠금·RDP 지원 여부를 추정하지 않고 --status의 결과를 따른다.

여러 머신에서는 각 머신의 SSH 연결과 제어 세션 ID를 따로 관리한다. 이 도구에 여러 머신의 계획이나 barrier를 보내지 않는다.

## 명령행 계약

아래 명령은 모두 원격 머신의 Agent.exe를 실행하는 것이다. 새 SSH 클라이언트는 없다.

| 명령 | 용도 |
|---|---|
| `Agent.exe --help` | 사용법, API 버전, 동봉된 사용 문서 경로 표시 |
| `Agent.exe --status` | GUI 실행부·데스크톱·지원 작업·제한 조회 |
| `Agent.exe --session-open --mode control --idle-seconds 900` | 입력 가능한 제어 세션 생성 |
| `Agent.exe --session-open --mode observe --idle-seconds 900` | 입력 없는 관측 세션 생성 |
| `Agent.exe --session-status CONTROL_ID` | 세션 상태·유휴 만료·입력 해제 상태 조회 |
| `Agent.exe --session-status` | 현재 계정의 세션 목록 조회. 세션 생성 응답 유실 시 사용 |
| `Agent.exe --request REQUEST_JSON` | 파일에 담긴 GUI 명령 하나 수행 |
| `Agent.exe --request-status CONTROL_ID REQUEST_ID` | 저장된 원래 요청의 결과 또는 실행 상태 조회 |
| `Agent.exe --session-close CONTROL_ID` | 도구 입력 해제 후 제어 세션 닫기 |

--mode의 기본값은 control, --idle-seconds의 기본값은 900이다. 유휴 시간은 GUI 명령 수락 시 갱신한다. 상태·결과 조회만으로 유휴 만료를 연장하지 않는다. 지원 범위와 실제 만료 시각은 세션 응답으로 반환한다. 세션이 만료되어도 보관 기간 안의 요청 결과는 조회할 수 있어야 한다.

같은 데스크톱의 control 세션은 하나만 허용하고 observe 세션은 별도로 둘 수 있다. observe 세션은 창·UI·화면 읽기와 상태 조회만 수행한다. focus·입력·앱 실행·앱 종료 요청은 GUI 변경 전에 거부한다.

--help를 제외한 모든 명령은 표준 출력에 UTF-8 JSON 객체 하나를 출력한다. 진행 로그는 표준 오류에 출력한다. 정상 JSON 앞뒤에 안내 문구를 섞지 않는다. --request-status는 저장된 원래 요청의 응답 형식을 그대로 사용한다.

| 종료 코드 | 의미 |
|---|---|
| `0` | status가 completed. 요청한 도구 동작 완료 |
| `1` | status가 rejected 또는 failed. 오류 내용 확인 필요 |
| `2` | status가 completionUnknown. 실행 여부가 불명확함 |
| `3` | 결과 조회의 status가 running. 아직 최종 결과 없음 |

CLI 프로세스 자체를 강제 종료하거나 SSH가 끊겨 JSON을 받지 못한 경우도 실행 미확정으로 다룬다. 종료 코드만으로 제품 기능 성공을 판단하지 않는다.

## 지원 여부 확인

처음에는 다음을 실행한다. 경로는 환경에서 제공한 실제 원격 경로를 사용한다.

```powershell
& 'C:\DesktopTestDriver\Client\Agent.exe' --help
& 'C:\DesktopTestDriver\Client\Agent.exe' --status
```

상태 조회의 data에는 다음을 반환한다.

| 필드 | 의미 |
|---|---|
| `hostRunning`, `interactiveSessionId`, `desktop` | GUI 실행부와 실제 Windows 데스크톱 세션 |
| `supportedOperations` | 이 배포본에서 실제 구현·지원하는 작업 이름 배열 |
| `captureTargets` | 지원하는 캡처 범위. 예: window |
| `maxRequestBytes` | 한 요청의 최대 UTF-8 바이트 수 |
| `maxCommandMilliseconds` | 개별 GUI 요청의 최대 실행 시간 |
| `requestRetentionSeconds` | 완료·실패·미확정 요청 기록의 최소 보관 기간 |
| `maxHoldMilliseconds` | 도구가 키·버튼을 계속 누를 수 있는 최대 시간 |

구현 전인 작업을 supportedOperations에 넣지 않는다. 문서에 있는 작업이어도 현재 배포본이 지원하지 않으면 호출하지 않는다. HostUnavailable·NoInteractiveSession·UnsupportedDesktop 등 실제 원인을 읽고 환경 준비 또는 지원 범위를 확인한다. 이 문서만 보고 잠금 해제·권한 상승 같은 별도 동작을 시도하지 않는다.

## 단일 요청의 형식

```json
{
  "apiVersion": 1,
  "requestId": "windows-001",
  "controlSessionId": "control-001",
  "operation": "windows",
  "timeoutMilliseconds": 5000,
  "arguments": {}
}
```

| 필드 | 규칙 |
|---|---|
| `apiVersion` | 새 로컬 GUI 요청 API 버전. 정수 1. 기존 통신 코드의 Wire.Version과 별개 |
| `requestId` | 세션 안에서 새 작업마다 생성하는 ID. 영문·숫자·하이픈·밑줄 1~80자. 같은 작업의 결과 조회·중복 요청에는 같은 ID 사용 |
| `controlSessionId` | --session-open에서 받은 실제 ID |
| `operation` | --status가 지원한다고 반환한 작업 이름 |
| `timeoutMilliseconds` | 선택 필드. 기본 5000, 범위 1~5000. 전체 제품 준비 대기 시간이 아닌 이번 명령의 수행 상한 |
| `arguments` | 해당 작업의 인자 객체. 인자가 없으면 빈 객체 |

UTF-8 JSON 객체 하나만 받는다. 단계 배열·조건문·expect·$ref·스크립트·셸 문자열은 해석하지 않는다. 알 수 없는 필드·중복 키·타입 오류는 GUI 변경 전에 거부한다. 요청의 기본 최대 크기는 1MiB이며 실제 배포본의 제한도 --status에서 확인한다.

새 요청마다 requestId를 생성한다. 관측을 반복할 때도 새 조회 ID를 사용한다. 같은 ID를 단순 반복에 쓰면 새로운 관측 대신 저장된 응답을 받을 수 있다. 요청 ID는 파일 이름과 같을 필요가 없다.

중복 비교는 기본값을 적용한 요청 내용을 기준으로 하며 JSON 속성 순서·공백 차이는 무시한다. 완료 결과가 보관되어 있으면 같은 ID의 같은 요청은 세션 종료 후에도 기존 결과만 반환한다. 종료·만료된 세션에 새 ID로 GUI 작업을 요청하면 SessionExpired 또는 SessionClosed로 거부한다.

## 응답 형식과 실행 상태

```json
{
  "apiVersion": 1,
  "requestId": "click-001",
  "controlSessionId": "control-001",
  "operation": "click",
  "status": "completed",
  "data": { "windowId": "win-007", "inputEventsSent": 2 },
  "error": null,
  "observedAt": "2026-10-20T06:30:00Z"
}
```

| status | 의미와 다음 행동 |
|---|---|
| `completed` | 요청한 관측 또는 GUI 동작이 끝남. 반환값·화면을 읽고 제품 결과는 별도로 판단 |
| `rejected` | GUI 변경 전에 거부됨. 오류를 수정하거나 새로 관측 |
| `failed` | 요청 처리 중 오류. data에 알려진 수행 범위·입력 해제 결과를 반환. 입력을 그대로 반복하지 않음 |
| `running` | 결과 조회 시 아직 실행 중. 결과를 다시 조회 |
| `completionUnknown` | 수행 여부 또는 수행 범위를 확정할 수 없음. 결과·화면을 다시 확인하고 다음 행동 결정 |

실패 응답의 error는 `{ "code": "오류코드", "message": "구체적인 원인" }`이다. data가 없으면 null이다. 입력 전달 중 일부만 수행됐다면 보낸 이벤트 수와 입력 해제 결과를 가능한 범위에서 data로 반환한다. 앱이 실제로 처리했는지는 보장하지 않는다.

상태·세션 명령도 같은 응답 형태를 사용한다. 해당 명령에 없는 requestId·controlSessionId는 null이다. 세션 생성은 data.controlSessionId·mode·idleExpiresAt을 반환한다. 세션 조회는 세션 상태·idleExpiresAt·inputReleased를 반환한다. ID 없는 세션 조회는 data.sessions에 controlSessionId·mode·createdAt·idleExpiresAt·state를 가진 배열을 반환한다. 세션 종료는 inputReleased·terminatedProcessIds·retainedProcessIds를 반환한다. 정상 종료를 반복 요청해도 다시 GUI 조작을 하지 않고 종료 상태를 반환한다.

## 창과 요소를 선택하는 방법

1. windows로 창을 조회한다. data.windows의 실제 windowId·제목·PID·실행 경로·영역을 읽는다. 인덱스 0이나 추측 HWND로 선택하지 않는다.
2. 선택한 windowId로 controls를 요청한다. data.elements에서 실제 elementId·AutomationId·이름·종류·활성 상태를 읽는다.
3. 요소 조작은 elementId를 우선 사용한다. AutomationId를 쓰면 그 창에서 활성 대상 하나만 일치해야 한다. 여러 요소가 맞으면 AmbiguousTarget으로 거부한다.
4. 좌표가 필요한 경우 먼저 capture를 받아 이미지를 열어 본다. 이미지의 좌표를 그대로 창 내부 픽셀 좌표로 지정하고 captureId를 함께 보낸다.
5. 변경 입력 전에는 focus를 명시적으로 요청한다. 읽기 명령과 click·type-text가 몰래 다른 창을 활성화하지 않는다. 전경 창이 다르면 ForegroundMismatch로 거부한다.

windowId와 elementId는 불투명한 도구 식별값이다. 값의 내부 구조를 해석하지 않는다. 창이 재생성되거나 요소가 바뀌면 다시 조회한다. 실제 제목·실행 경로를 모르면 창 목록을 보고 결정하며, 임의로 만들어 넣지 않는다.

## 작업별 인자와 결과

아래는 목표 작업 계약이다. 초기 배포에서 빠진 작업은 지원 목록에서 제외하고 UnsupportedOperation으로 거부한다. arguments에 표에서 정하지 않은 실행 문법을 추가하지 않는다.

| operation | arguments | data의 핵심 |
|---|---|---|
| `windows` | `{}` | windows 배열. 각 항목의 windowId, title, pid, createdAt, imagePath, bounds, foreground |
| `controls` | windowId | windowId, elements 배열. 각 요소의 elementId, automationId, name, controlType, bounds, enabled, value |
| `capture` | windowId, output | captureId, windowId, path, bytes, sha256, imageWidth, imageHeight, coordinateSpace, expiresAt |
| `focus` | windowId | windowId, foreground |
| `click` | windowId와 elementId·automationId·position 중 정확히 하나. button 기본 left | windowId, inputEventsSent |
| `type-text` | windowId, text | windowId, inputEventsSent. text는 현재 포커스된 입력 대상에 전달 |
| `chord` | windowId, keys | windowId, inputEventsSent. 한 요청에서 누름·해제 완료 |
| `key` | windowId, key, action. action은 press·down·up | 입력 전달 수·소유 입력 상태 |
| `move` | windowId, position | 입력 전달 수 |
| `wheel` | windowId, position, delta | 입력 전달 수 |
| `drag` | windowId, captureId, start, end. button 기본 left, steps 기본 10 | 입력 전달 수·입력 해제 결과 |
| `launch` | executable, arguments 배열. lifetime은 keep 기본값 또는 session | processId, pid, createdAt, imagePath, lifetime |
| `close-window` | windowId | closeRequested. 종료 요청 전달이며 실제 창 종료는 windows로 다시 확인 |
| `terminate-owned` | processIds 배열 | 해당 세션에서 도구가 실행한 프로세스의 실제 종료 결과 |
| `release-input` | `{}` | inputReleased. 도구가 누른 입력만 해제 |

controls의 value는 `{ "available": true, "text": "읽은 값", "reason": null }` 형태다. 미지원·조회 불가·암호 필드는 available이 false이고 text는 null이며 reason을 반환한다. 빈 문자열과 읽기 불가를 구분한다.

windows의 bounds는 화면상의 창 클라이언트 영역으로 x·y·width·height를 사용한다. 다중 모니터의 화면 x·y는 음수일 수 있다. controls의 bounds와 position·start·end는 창 클라이언트 영역 기준 물리 픽셀이며 원점은 왼쪽 위의 0,0이다. capture는 같은 클라이언트 영역을 1:1 픽셀로 저장한다. 화면 좌표를 창 내부 좌표로 혼용하지 않는다.

position은 `{ "x": 120, "y": 60, "captureId": "cap-001" }`, drag의 start·end는 각각 `{ "x": 120, "y": 60 }` 형태다. button은 left·right·middle, steps는 1~100, wheel delta는 -1200~1200이다. 캡처 유효 시각을 넘겼거나 창·배치가 바뀌면 StaleCapture로 거부하고 새로 캡처한다. 창 내용까지 항상 동일하다는 보장은 없으므로 변하는 화면의 조작 결과는 다시 관측한다.

좌표 입력은 캡처된 창 내부 범위 안의 정수만 허용한다. output은 원격 절대 파일 경로이며, 새 capture 요청마다 다른 파일 경로를 사용한다. 이미 파일이 있으면 OutputExists로 거부하고 덮어쓰지 않는다. 같은 요청 ID의 재조회는 원래 파일 정보만 반환하며 새 캡처를 하지 않는다. 이미지 data.coordinateSpace는 `window-client-pixels`이고 expiresAt은 이 캡처를 좌표 입력에 사용할 수 있는 마지막 시각이다.

text는 1~4096자의 Unicode 문자열이며 Enter·Tab 같은 제어 키는 key 또는 chord로 보낸다. keys는 중복 없는 키 이름 배열 1~8개다. 키 이름은 CTRL·ALT·SHIFT·WIN·ENTER·TAB·ESC·BACKSPACE·DELETE·SPACE·LEFT·RIGHT·UP·DOWN·HOME·END·PAGEUP·PAGEDOWN·A~Z·0~9·F1~F24를 사용한다. 다른 키는 지원한다고 추정하지 않는다. 길게 누르는 down은 maxHoldMilliseconds 안에 해제되므로 일반 조합은 chord를 사용한다.

launch의 executable은 원격 실행 파일의 절대 경로, arguments는 문자열 배열이다. 셸 명령으로 결합하지 않는다. lifetime이 keep이면 세션을 닫아도 앱을 유지한다. session이면 세션 종료 시 도구 소유 앱을 종료한다. 이미 실행 중이던 앱은 terminate-owned의 대상으로 취급하지 않는다.

## 처음부터 한 번 조작하는 예

다음 ID와 버튼 이름은 설명용이다. **실제 호출에서는 각 응답을 읽고 그 값으로 다음 요청을 작성한다.** 시나리오처럼 여러 단계를 한 번에 도구에 전달하지 않는다.

1. --help·--status로 새 API와 windows·controls·focus·click·capture 지원을 확인한다.
2. --session-open으로 세션을 열고 data.controlSessionId를 받는다.
3. 아래 windows 요청을 실행해 목표 창을 찾는다.

```json
{
  "apiVersion": 1,
  "requestId": "windows-001",
  "controlSessionId": "control-001",
  "operation": "windows",
  "arguments": {}
}
```

4. 실제 windowId로 controls를 요청하고 목표 요소를 찾는다.

```json
{
  "apiVersion": 1,
  "requestId": "controls-001",
  "controlSessionId": "control-001",
  "operation": "controls",
  "arguments": { "windowId": "win-007" }
}
```

5. focus를 요청해 foreground가 true인지 확인한다.

```json
{
  "apiVersion": 1,
  "requestId": "focus-001",
  "controlSessionId": "control-001",
  "operation": "focus",
  "arguments": { "windowId": "win-007" }
}
```

6. 실제 elementId로 클릭한다. AutomationId를 쓰는 경우에는 유일한 대상인지 확인한다.

```json
{
  "apiVersion": 1,
  "requestId": "click-001",
  "controlSessionId": "control-001",
  "operation": "click",
  "arguments": { "windowId": "win-007", "elementId": "element-012" }
}
```

7. capture를 요청한다. 저장 경로는 원격 머신에 미리 준비한 쓰기 가능한 폴더를 쓴다.

```json
{
  "apiVersion": 1,
  "requestId": "capture-001",
  "controlSessionId": "control-001",
  "operation": "capture",
  "arguments": {
    "windowId": "win-007",
    "output": "C:\\Temp\\connection.png"
  }
}
```

각 파일은 `Agent.exe --request 원격파일경로`로 실행한다. 응답에서 data.path·sha256을 읽고 기존 SSH 파일 전송으로 이미지를 가져와 실제로 열어 본다. 단순 파일 생성·완료 응답을 앱 기능 성공으로 판단하지 않는다. 필요하면 UI 값·화면·SSH 로그를 더 읽고 다음 요청을 정한다.

8. 작업이 끝나면 필요한 제품 연결 종료·앱 종료·설정 복원을 요청하고 --session-close로 세션을 닫는다. 기본 keep 앱은 유지되며 도구가 누른 입력은 해제된다.

## 오류 뒤의 행동

| 오류·상태 | 에이전트의 행동 |
|---|---|
| WindowNotFound·ElementNotFound·StaleElement | 창·요소를 다시 조회하고 새 ID로 새 작업 요청 |
| AmbiguousTarget | 후보 정보를 읽고 더 구체적인 실제 대상을 선택. 첫 항목을 임의 선택하지 않음 |
| ForegroundMismatch | 창을 재확인하고 명시적 focus 후 새 입력 요청 |
| StaleCapture | 새 캡처를 받아 이미지 좌표를 다시 정함 |
| ObservationUnavailable | 다른 지원 관측 사용. 읽지 못한 값을 성공·빈 값으로 처리하지 않음 |
| ReadOnlySession | 입력 없는 세션임을 확인. 기존 세션의 권한을 몰래 바꾸지 않음 |
| SessionExpired | 제품 상태를 다시 관측할 새 세션을 열고 새로운 요청 ID 사용 |
| UnsupportedOperation·UnsupportedApiVersion | --help·--status와 실제 배포 버전 확인. 지원하는 것으로 가장하지 않음 |
| DuplicateRequestMismatch | 같은 ID를 다른 내용에 재사용한 오류. 원래 요청 상태부터 확인 |
| RequestNotFound | 보관 기간·실행부 상태 확인. 입력이 실행되지 않았다는 증거로 취급하지 않음 |
| completionUnknown 또는 응답 유실 | --request-status로 먼저 조회하고 필요하면 화면을 다시 관측. 자동 재입력 금지 |

결과 조회가 running이면 같은 요청의 결과를 다시 조회한다. 완료 기록이 있으면 그 결과를 사용한다. 기존 ID의 같은 변경 요청을 다시 보내도 도구는 두 번째 입력을 수행하지 않아야 한다. 기록이 없거나 실행부가 중간에 종료됐다면 실제 제품 상태를 다시 보고 다음 행동을 결정한다.

읽기 관측은 새 requestId로 반복할 수 있다. 제품 준비 완료를 기다릴 때도 에이전트가 자신의 마감 시간을 두고 새 관측을 요청한다. 한 번의 클릭이 오류 없이 끝났다는 이유로 화면 결과를 확인하지 않거나 같은 클릭을 반복하지 않는다.

## 문서만 보고 사용할 수 있는지 확인하는 기준

문서를 동봉했다는 사실만으로 사용 가능하다고 선언하지 않는다. 구현 완료 전에는 이 문서를 따라도 실제 명령이 동작하지 않는다. 배포 시 다음을 확인한다.

1. 이 문서를 Agent.exe 배포 폴더의 docs에 동봉하고 --help에서 위치를 안내한다. 저장소 전체를 읽지 않아도 사용할 수 있게 한다.
2. 문서의 요청 예시를 GUI 요청 계약 검사에 사용한다. 실제 필드·타입·상태·종료 코드가 문서와 같아야 한다.
3. 기존 코드와 개발 이력을 모르는 호출자가 이 문서·Agent.exe 경로·시험 앱 정보만으로 실제 SSH에서 창 조회 → 요소 확인 → focus → 클릭 → 이미지 획득 → 세션 종료를 수행한다. ID는 매번 실제 응답에서 받는다.
4. 없는 요소·관측 미지원·응답 유실·세션 만료를 넣어 문서의 복구 절차로 다음 행동을 결정할 수 있는지 확인한다.
5. 읽기 요청에 부수 입력이 없고, GUI 오류 후에도 관측이 가능하며, 입력 해제와 앱 유지 정책이 문서와 같은지 확인한다.

실패하면 사용 문서 또는 구현을 고쳐 다시 확인한다. 제품별 E2E 계획·제품 성공 판정은 이 확인 기준에 포함하지 않는다.
