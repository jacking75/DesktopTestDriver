# 코딩 에이전트용 DesktopTestDriver 사용 규약

이 문서는 Windows GUI E2E 테스트를 정확하고 빠르게 끝내기 위한 API 1·2 사용 규약이다. 코딩 에이전트 또는 테스트 실행기가 성공 조건과 전체 마감 시간을 정하고, DesktopTestDriver로 대상 선택·조작·조건 대기·결과 조회·정리를 수행한다. `completed`는 도구 요청 완료이며 제품 성공은 최종 UI·파일·서버 결과로 별도 판정한다.

## API 1·2와 같은 PC에서의 실행

Controller와 Agent는 같은 Windows PC에서도 함께 실행할 수 있다. Agent의 machineId를 로컬 PC 식별자로 등록하고 Controller의 주소를 `127.0.0.1`로 사용한다. 인증서 등록·대상 프로세스 신원·세션 정책은 원격 구성과 동일하게 적용한다. 같은 PC라는 이유로 입력 보호를 해제하지 않는다.

API 1과 API 2를 함께 지원한다. Controller와 Agent는 wire 2 빌드를 함께 배포한다. API 1의 클릭·키 입력 의미는 유지하며 UIA로 몰래 바꾸지 않는다. `machineUsage` 기본값은 `shared`이다. 공유 화면에서는 기본적으로 전역 입력을 거부하고, 동작별 비간섭 측정을 등록한 프로필에 한해 UIA 의미 조작을 허용한다. 실제 키·마우스 입력에는 `foreground-exclusive`를 명시하며 30초 lease와 사용자 활동 감지를 적용한다. 전용 테스트 환경에는 `dedicated`와 `isolated-desktop`을 사용할 수 있다.

Agent는 로그인한 일반 사용자의 **활성 콘솔 또는 활성 RDP 세션**에서 실행한다. Controller와 Agent는 같은 머신에서도 동작한다. RDP는 Agent가 실행되는 사용자 세션에 계속 연결돼 있어야 하며, 연결 끊김·잠금·보안 데스크톱·관리자 실행은 거부한다. `--doctor`의 `sessionKind`, `desktop`, `inputAvailable`로 현재 상태를 확인한다.

```powershell
Controller.exe --doctor --machine local-test
Controller.exe --profile-validate examples/standalone/forms.profile.json
# scope.json은 실제 PID·시작 시각·EXE 경로·SHA-256의 DesktopProcess 배열이다.
Controller.exe --session-open --machine local-test --api-version 2 --mode observe --target-scope scope.json
Controller.exe --inspect --session CONTROL_ID --window WINDOW_ID
Controller.exe --session-close CONTROL_ID
```

앱 프로필의 `application`에는 기계별 경로를 직접 넣지 않고 binding 이름을 지정한다. Agent의 `--register-profile PROFILE.json BINDINGS.json`은 EXE·관리 DLL 해시와 작업 폴더를 검증한다. `--qualify-profile QUALIFICATION.json`은 프로필·바이너리·OS·측정 시각·증거 해시·허용 동작을 연결한다. 전역 입력 또는 대상 창 전경 전환이 있던 증거는 인증하지 않는다. 바이너리·프로필·OS·증거 변경이나 인증 만료는 신규 mutation을 차단한다. 인증된 프로필도 실행 직전에 대상 신원·요소 snapshot·패턴·disabled 상태와 대상 앱 활동을 다시 검사한다.

| 기능 | 지원 범위와 제한 |
|---|---|
| 대상 범위 | PID·시작 시각·경로·해시를 확인한다. API 2의 빈 scope는 다른 앱을 열거하지 않는다. 새 창은 같은 소유 프로세스 또는 허용된 Job 자식 관계로 검증한다. |
| 요소 선택 | ID·정확한 type/name/class/framework·부모/조상 조건을 지원한다. 중복과 불완전 탐색을 성공으로 처리하지 않는다. 암호·제외 필드는 반환하지 않는다. |
| UIA | 세션별 MTA 헬퍼와 사용자 전용 인증 파이프를 사용한다. Value/Invoke/선택/토글/확장/스크롤은 네이티브 UIA3로 호출한다. 미지원 패턴에서 키·마우스로 대체하지 않는다. |
| 가상 목록 | inspect의 컨테이너 ID와 snapshot으로 `find-item`을 요청한다. 가상 목록은 불완전 관측으로 표시한다. Realize/Scroll은 별도 mutation이며 자동으로 실행하지 않는다. |
| 창·대화상자 | owner·class·title로 창을 찾고 기다린다. 모달·파일 대화상자는 활성화할 수 있어 기본 공유 화면 인증에 포함하지 않는다. |
| 앱 수명 | attach한 앱은 종료 소유권을 얻지 않는다. session launch는 시작 전 원장과 suspended Job 편입으로 자식을 포함한다. 정상 닫기 후 필요하면 소유 Job을 종료하고 멤버 0을 확인한다. keep 앱은 유지한다. |
| 실제 측정 | 기본 WinForms/WPF Value와 WinForms Invoke는 전역 입력 0·전경 전환 0으로 확인했다. 다중 창·가려진 창·동적 교체·WPF 가상 목록도 별도 검증했다. 모든 앱과 모든 동작의 비간섭을 보장하지 않는다. |
| 제품 연결 | Core는 제품 API를 참조하지 않는다. 회의예약의 어댑터·스킬·실행기는 해당 제품 저장소에 둔다. |

`--request-stdin`은 EOF로 끝난 JSON 요청 하나를 읽고 stdout에 결과 JSON 하나를 반환한다. `examples/standalone/Inspect.ps1`, `Inspect.cs`는 관측과 세션 정리를 보여준다. 배포 폴더의 Client.dll·Core.dll·Contracts.dll로 C# 예제를 독립 빌드할 수 있다. `schemas/`는 DTO 구조 Schema이며 의미·길이·범위의 최종 검사는 런타임 parser가 수행한다.

`completionUnknown` mutation은 자동 재전송하지 않는다. Agent가 미확정 변경을 기록한 경우에는 세션을 차단하고 읽기와 정리만 허용한다. 클라이언트에서만 응답 유실을 관측한 경우에는 Agent 차단을 단정하지 않고 세션 상태를 조회한다. `--request-status`로 원래 요청을 확인한다. `--session-reconcile ID --request-id REQUEST --resolution effect-confirmed|no-effect-confirmed|abandoned --evidence-ref OPAQUE_ID --expected-revision N`은 호출자 근거를 별도로 남기며 원래 unknown을 보존한다. 이후 `--session-resume ID --expected-revision N`을 명시적으로 요청한다. SDK의 대기 취소도 실행 취소나 무효과를 뜻하지 않는다.

실제 GUI 접근은 DesktopTestDriver만으로 수행한다. 다른 컴퓨터 유스 도구로 클릭·입력·요소 조회를 대체해 이 도구의 통과로 보고하지 않는다. 테스트 전에 사용자에게 알리고, 이번 시험에서 시작한 앱·Agent·Controller를 종료 시 정리하며 기존 프로세스는 유지한다. 프로세스·포트·입력 해제·콜백·업무 결과는 각각 확인한다.

사용 전에 Controller.exe의 실제 경로, 대상 PC 정보, 수행할 작업을 확인한다. Controller와 대상 PC의 Agent가 기동·연결되어 있어야 한다. 최초 설정이 필요하면 [최초 설정과 상주 실행](#최초-설정과-상주-실행)을 따른다.

## 먼저 알아야 할 실행 방식

- 코딩 에이전트는 같은 PC의 작업에는 로컬 명령을, 원격 명령·파일 전송에는 기존 SSH를 사용한다.
- Controller.exe는 코딩 에이전트와 같은 머신에서 실행된다. 명령 진입점이 같은 머신의 상주 Controller에 요청하고, 상주 Controller가 연결된 Agent에 전달한다.
- Agent.exe는 대상 Windows PC의 로그인된 데스크톱에서 계속 실행하며 실제 GUI 관측·입력을 수행한다. 개별 Controller 명령 프로세스 종료는 상주 Controller·Agent·제어 세션의 종료가 아니다.
- 제어 세션은 여러 명령을 이어서 조작하는 문맥이다. 시험 순서·성공 조건을 담지 않는다.
- Agent는 대상 PC의 활성 콘솔 또는 활성 RDP 일반 사용자 바탕화면에서 실행한다. 잠금·연결 끊김·UAC 보안 화면에서는 중단하고 --doctor 결과를 따른다.

대상 PC는 작업 PC 자체와 원격 PC를 모두 포함한다. 작업 PC의 GUI도 조작하려면 그 PC에 Agent를 함께 실행한다. 여러 PC를 사용할 때는 PC별로 조작 세션과 관측 전용 세션을 선택한다. 원격 파일 작업에는 기존 SSH를 사용하고 작업 PC의 파일 작업에는 일반 로컬 명령을 사용한다.

여러 머신은 Controller에 연결된 machineId로 구분한다. 세션을 열 때 대상을 지정하고 이후 controlSessionId가 해당 머신에 묶인다. SSH 연결은 파일·로그·프로세스 등의 별도 작업에 사용한다. 도구에 여러 머신의 실행 계획이나 barrier를 보내지 않는다.

## 명령행 계약

아래 명령은 모두 코딩 에이전트와 같은 머신의 Controller.exe를 실행한다. 원격 머신에 Controller를 설치하거나 GUI 명령마다 SSH로 실행하지 않는다. 별도 SSH 클라이언트도 만들지 않는다. 상주 Controller와 각 PC의 Agent 기동·연결은 환경 준비에서 수행한다.

| 명령 | 용도 |
|---|---|
| `Controller.exe --help` | 사용법, API 버전, 동봉된 사용 문서 경로 표시 |
| `Controller.exe --status` | 연결된 Agent의 machineId·연결·세션 목록 조회 |
| `Controller.exe --status --machine TARGET` | 특정 Agent의 데스크톱·지원 작업·제한 조회 |
| `Controller.exe --session-open --machine TARGET --mode control --interaction-policy foreground-exclusive --idle-seconds 900` | 입력 가능한 제어 세션 생성 |
| `Controller.exe --session-open --machine TARGET --mode observe --idle-seconds 900` | 입력 없는 관측 세션 생성 |
| `Controller.exe --session-status CONTROL_ID` | 세션 상태·유휴 만료·입력 해제 상태 조회 |
| `Controller.exe --session-status` | 현재 계정의 세션 목록 조회. 세션 생성 응답 유실 시 사용 |
| `Controller.exe --request REQUEST_JSON` | 파일에 담긴 GUI 명령 하나 수행 |
| `Controller.exe --request-status CONTROL_ID REQUEST_ID` | 저장된 원래 요청의 결과 또는 실행 상태 조회 |
| `Controller.exe --session-close CONTROL_ID` | 도구 입력 해제 후 제어 세션 닫기 |

--session-open의 --machine은 필수이며 --status의 실제 machineId를 사용한다. --mode의 기본값은 control, --idle-seconds의 기본값은 900이다. 유휴 시간은 GUI 명령 수락 시 갱신한다. 상태·결과 조회만으로 유휴 만료를 연장하지 않는다. 지원 범위와 실제 만료 시각은 세션 응답으로 반환한다. 세션이 만료되어도 보관 기간 안의 요청 결과는 조회할 수 있어야 한다.

같은 데스크톱의 control 세션은 하나만 허용하고 observe 세션은 별도로 둘 수 있다. observe 세션은 창·UI·화면 읽기와 상태 조회만 수행한다. focus·입력·앱 실행·앱 종료 요청은 GUI 변경 전에 거부한다.

세션 상태는 active·closing·closed·expired·blocked로 구분한다. 종료할 때는 새 변경 요청을 차단하고 대기 요청을 취소한 뒤, 실행 중 입력의 중단·도구 소유 입력 해제·session 수명의 앱 종료를 확인한다. 종료 중이거나 중단·해제를 확인하지 못한 세션은 새 조작 세션을 허용하지 않는다. 관측 세션 종료는 다른 세션이나 제품 입력을 해제하지 않는다.

개별 요청의 제한 시간에는 상주 Controller의 수락 이후 대기 시간이 포함된다. 대기 중 기한이 지나면 실행하지 않는다. 이미 전달한 요청의 중단을 확인하지 못했으면 completionUnknown을 반환하고 해당 조작 세션의 새 변경을 막는다. CLI 프로세스 종료만으로 수락된 작업을 취소하지 않으며, 요청 자체의 제한 시간은 그대로 적용한다. 결과 조회로 원래 수행 여부를 확인하고 자동으로 재입력하지 않는다.

읽기 요청은 전경 창·선택 상태·창 위치를 바꾸지 않는다. 관측 전용 세션은 제품이 전달한 입력을 도구 소유 입력으로 처리하거나 그 입력을 방해하지 않는다. observe 모드의 release-input도 변경 명령으로 거부한다. 창이 가려지거나 최소화되어 정확한 화면을 얻을 수 없으면 임의로 활성화하지 않고 ObservationUnavailable을 반환한다.

단일 호출 명령은 --help를 제외하고 표준 출력에 UTF-8 JSON 객체 하나를 출력한다. --json-lines는 입력 한 줄마다 응답 한 줄을 출력하며 상주 --serve는 별도 실행 모드다. 진행 로그는 표준 오류에 출력한다. 정상 JSON 앞뒤에 안내 문구를 섞지 않는다. --request-status는 저장된 원래 요청의 응답 형식을 그대로 사용한다.

| 종료 코드 | 의미 |
|---|---|
| `0` | status가 completed. 요청한 도구 동작 완료 |
| `1` | status가 rejected 또는 failed. 오류 내용 확인 필요 |
| `2` | status가 completionUnknown. 실행 여부가 불명확함 |
| `3` | 결과 조회의 status가 running. 아직 최종 결과 없음 |

명령 프로세스가 강제 종료되거나 Controller↔Agent 연결이 끊겨 변경 요청의 결과를 받지 못한 경우도 실행 미확정으로 다룬다. 종료 코드만으로 제품 기능 성공을 판단하지 않는다.

## 지원 여부 확인

처음에는 다음을 실행한다. Controller 경로는 코딩 에이전트와 같은 머신의 실제 경로를 사용한다. target-01은 --status에서 확인한 실제 machineId로 바꾼다.

```powershell
& 'C:\DesktopTestDriver\Controller\Controller.exe' --help
& 'C:\DesktopTestDriver\Controller\Controller.exe' --status
& 'C:\DesktopTestDriver\Controller\Controller.exe' --status --machine target-01
```

--status는 data.agents에 machineId·connected·interactiveSessionId·activeControlSessionId를 가진 배열을 반환한다. --status --machine TARGET의 data에는 다음 상세 정보를 반환한다.

| 필드 | 의미 |
|---|---|
| `agentRunning`, `interactiveSessionId`, `desktop` | GUI 실행부와 실제 Windows 데스크톱 세션 |
| `supportedOperations` | 지원하는 작업 이름 배열 |
| `captureTargets` | 지원하는 캡처 범위. 예: window |
| `maxRequestBytes` | 한 요청의 최대 UTF-8 바이트 수 |
| `maxCommandMilliseconds` | 개별 GUI 요청의 최대 실행 시간 |
| `maxStopMilliseconds` | 실행 중인 입력 작업의 중단 확인 한도. 기본값 3000밀리초 |
| `requestRetentionSeconds` | 완료·실패·미확정 요청 기록의 최소 보관 기간 |
| `maxHoldMilliseconds` | 도구가 키·버튼을 계속 누를 수 있는 최대 시간 |

요청할 작업이 supportedOperations에 있는지 확인한다. ControllerUnavailable·AgentDisconnected·AgentUnavailable·NoInteractiveSession·UnsupportedDesktop 등 실제 원인을 읽고 환경 준비 또는 지원 범위를 확인한다. 이 문서만 보고 잠금 해제·권한 상승 같은 별도 동작을 시도하지 않는다.

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
| `apiVersion` | GUI 요청 API 버전. 정수 1 또는 2다. UIA는 API 2 세션과 요청을 사용한다 |
| `requestId` | 세션 안에서 새 작업마다 생성하는 ID. 영문·숫자·하이픈·밑줄 1~80자. 같은 작업의 결과 조회·중복 요청에는 같은 ID 사용 |
| `controlSessionId` | --session-open에서 받은 실제 ID. Controller가 이 ID로 대상 machineId를 결정하며 요청마다 머신 이름을 다시 쓰지 않음 |
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
  "machineId": "target-01",
  "status": "completed",
  "data": { "windowId": "win-007", "inputEventsSent": 3, "inputReleased": true },
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

상태·세션 명령도 같은 응답 형태를 사용한다. 대상이 있는 응답은 실제 machineId를 반환하며, 전체 목록 조회처럼 대상이 없는 경우 machineId는 null이다. 해당 명령에 없는 requestId·controlSessionId도 null이다. 세션 생성은 data.controlSessionId·machineId·mode·idleExpiresAt을 반환한다. 세션 조회는 세션 상태·idleExpiresAt·inputReleased를 반환한다. ID 없는 세션 조회는 data.sessions에 controlSessionId·machineId·mode·createdAt·idleExpiresAt·state를 가진 배열을 반환한다. 세션 종료는 inputReleased·terminatedProcessIds·retainedProcessIds를 반환한다. 정상 종료를 반복 요청해도 다시 GUI 조작을 하지 않고 종료 상태를 반환한다.

상주 Controller가 공개 응답을 보관하고 --request-status로 반환한다. Agent의 실제 수행 기록과 Controller의 공개 응답을 구분한다. 확정된 completed·failed·rejected는 재조회로 바꾸지 않는다. completionUnknown은 원래 기록으로 확인한 경우에만 확정 결과로 갱신할 수 있다. running은 실제 작업 또는 이미지 전송이 진행 중임을 확인한 상태다.

inputReleased는 해당 세션의 도구 소유 입력에 관한 값이다. true는 남은 소유 입력이 없음을 확인한 것, false는 미해제 입력이 확인된 것, null은 확인하지 못한 것이다. 세션 닫기 응답은 기본 5초 안에 확인한 결과를 반환한다. 정리가 미완료이면 failed 또는 completionUnknown과 세션 상태를 반환하고 상주 실행부가 정리를 이어간다. 중복 닫기는 같은 종료 처리를 조회·이어받는다.

차단된 조작 세션도 상태·결과 조회, 명시적 세션 종료, 소유 입력만 해제하는 release-input은 사용할 수 있다. release-input에는 전경 창이나 유효 캡처를 요구하지 않는다. 관측 세션은 이 변경 명령을 계속 거부한다. 실패 결과가 있더라도 미해제 입력이 남으면 새 변경 요청을 허용하지 않는다. 재시작·재연결·중복 조회로 원래 요청·정리의 마감 시간을 새로 시작하지 않는다.

명시적 release-input은 대기 입력 취소·실행 중 입력의 중단 확인 후 소유 입력을 해제하며 앱이나 세션은 닫지 않는다. API 2는 해제 후 blocked를 유지하므로 원인을 확인하고 현재 revision으로 명시적으로 재개한다. API 1도 다른 차단 원인이나 종료 처리가 없고 원래 입력의 종료와 해제를 확인한 경우에만 새 입력을 허용한다.

## 정확하고 빠른 E2E 실행 기준

- 새 시험 전에 성공 조건·전체 마감 시간·정리 대상을 정한다. 제품·서버 실패, 테스트 방법·판정 오류, 환경·외부 API 실패, 원인 미확정을 구분한다.
- 반복 읽기는 보관한 요소 참조를 사용하고 새 항목은 검색 루트를 제한한다. 현재 값은 매번 읽으며 오래된 snapshot을 억지로 사용하지 않는다.
- `wait-condition`과 `wait-window`는 조건이 충족되면 반환한다. 기본 폴링은 200ms이며 100~1000ms를 지정한다. 개별 요청은 최대 5초다. 더 긴 대기는 전체 시험 마감 시간 안에서 새 읽기 요청으로 이어 간다.
- 반복 호출은 C# Client 또는 `--json-lines`를 활용한다. 캡처는 시각적 확인·실패 근거에 사용하고 값 확인은 UIA를 활용한다.
- 시험용 앱은 `launch`에 `lifetime: session`을 명시한다. 결과를 수집한 뒤 세션을 닫고 `inputReleased`, `applicationsStopped`와 종료·유지 프로세스 정보를 확인한다.

## API 2 주요 작업

API 2 세션에 API 2 요청을 보내며 `windowId`는 해당 세션에서 조회한 값을 사용한다. UIA 변경에는 조회한 `elementId`·`snapshotToken`이 필요하다. 지원 패턴과 부작용 가능성은 `capabilities`의 대상 정보와 `operationDescriptors`로 확인한다.

| 작업 | 주요 인자와 의미 |
|---|---|
| `find-window`, `wait-window` | title·className·ownerWindowId로 창을 선택한다. wait-window는 pollMilliseconds를 받을 수 있다 |
| `inspect` | windowId, 선택적인 treeView·rootOnly로 관측 범위를 정한다 |
| `find-element` | windowId·selector로 유일한 대상을 찾는다. searchRootElementId·searchRootSnapshotToken으로 영역을 한정한다 |
| `capabilities` | windowId로 관측하거나 elementId·snapshotToken으로 보관한 요소의 현재 값·패턴을 읽는다 |
| `wait-condition` | windowId·selector·condition, 필요에 따라 expected·pollMilliseconds·검색 루트를 지정한다 |
| `set-value`, `invoke` | 요소 참조에 값을 설정하거나 Invoke 패턴을 실행한다. set-value에는 value가 필요하다 |
| `select-item`, `set-toggle`, `set-expanded` | 요소 참조로 선택·토글·확장을 수행한다. set-toggle은 desiredState(on/off), set-expanded는 expanded를 지정한다 |
| `find-item` | 컨테이너 참조와 itemName으로 가상 항목을 찾는다. 화면 이동은 자동 수행하지 않는다 |
| `realize-item`, `scroll-item-into-view`, `scroll-container` | 명시적인 변경 작업이다. scroll-container는 horizontal·vertical 값을 받는다 |

조건은 `exists`, `absent`, `enabled`, `value-equals`, `text-contains`, `toggle-equals`를 지원한다. 읽기 불가나 불완전 검색을 빈 값·부재·성공으로 처리하지 않는다. 다음은 실제 세션·창 ID로 바꿔 보내는 예시다.

```json
{
  "apiVersion": 2,
  "requestId": "wait-editor-001",
  "controlSessionId": "control-001",
  "operation": "wait-condition",
  "timeoutMilliseconds": 5000,
  "arguments": {
    "windowId": "win-007",
    "selector": { "automationId": "Editor" },
    "condition": "value-equals",
    "expected": "테스트 메모입니다",
    "pollMilliseconds": 200
  }
}
```

`foreground-exclusive`는 30초 입력 lease를 사용한다. `InputLeaseRevoked` 또는 차단 상태이면 입력을 멈추고 사용자 활동·환경·미확정 요청을 확인한다. 필요하면 pause와 상태 조회를 거쳐 현재 revision으로 resume한다. 미확정 변경은 먼저 원래 결과와 근거를 확인하고 reconcile한다. lease 갱신을 위해 조작 자체를 반복하지 않는다.

## 창과 요소를 선택하는 방법

API 2의 반복 작업은 **처음 찾은 요소 참조를 재사용**한다. `find-element`를 매번 호출하면 새 탐색이다. `elementId`와 `snapshotToken`을 보관하고 `capabilities`에 `windowId`, `elementId`, `snapshotToken`을 보내면 보관한 대상의 현재 값·활성 상태·패턴을 읽는다. C#에서는 `ReadElementAsync`를 사용한다. 같은 참조로 `SetValueAsync`, `InvokeAsync`도 호출할 수 있다. 현재 값은 캐시하지 않는다.

창의 식별만 필요하면 `inspect`에 `rootOnly:true`를 지정한다. 처음에 목록·패널을 찾은 뒤, `find-element` 또는 `wait-condition`에 `searchRootElementId`, `searchRootSnapshotToken`을 지정하면 해당 영역만 탐색한다. C#의 `FindInElementAsync`도 같은 계약이다. 동적 목록의 새 메시지는 이 범위에서 찾는다. 입력란·상태란·고정 버튼을 찾기 위해 동적 목록 전체를 다시 탐색하지 않는다.

참조를 사용할 때마다 프로세스·창·RuntimeId·부모 관계와 선택자 조건을 다시 검증한다. 이름을 선택자에 지정하지 않았다면 창 제목·알림 표시 변경만으로 참조를 폐기하지 않는다. 창 재생성·요소 교체·선택자 불일치는 `StaleElement`로 거부하며, 필요한 영역을 다시 조회한다. `completionUnknown` 변경 동작을 자동 재전송하지 않는다.

부분 경로 응답의 `observation.complete:false`는 창 전체를 반환하지 않았다는 뜻이다. 해당 조회가 끝났는지는 `queryComplete`로 구분한다. 각 노드의 `detailState`가 `metadata-only`이면 값·패턴은 읽지 않은 상태이다. `lookupKind`는 `root-only`, `selector-path`, `scoped-selector`, `bound-reference`, `full-tree` 등 실제 수행 경로를 표시한다. 가상 목록의 미열거 항목을 없다고 판단하지 않는다.

언어 중립 클라이언트는 `Controller.exe --config FILE --json-lines` 프로세스 하나를 유지할 수 있다. 표준 입력 한 줄에 `LocalCommand` JSON 하나를 보내면 표준 출력 한 줄에 응답 하나를 받는다. `{"command":"status"}`와 `{"command":"session-close","sessionId":"실제 세션 ID"}`를 순차 전송한다. 요청은 `{"command":"request","request":"직렬화한 GUI 요청 JSON 문자열"}`이다. stdin EOF로 프로세스를 종료하며 GUI 세션은 명시적으로 닫는다. 요청 ID·저널·권한 검사는 기존 Controller/Agent와 같다.

아래 1~5는 API 1의 실제 입력 경로다. API 2 백그라운드 UIA에는 focus를 추가하지 않는다.

1. windows로 창을 조회한다. data.windows의 실제 windowId·제목·PID·실행 경로·영역을 읽는다. 인덱스 0이나 추측 HWND로 선택하지 않는다.
2. 선택한 windowId로 controls를 요청한다. data.elements에서 실제 elementId·AutomationId·이름·종류·활성 상태를 읽는다.
3. 요소 조작은 elementId를 우선 사용한다. AutomationId를 쓰면 그 창에서 활성 대상 하나만 일치해야 한다. 여러 요소가 맞으면 AmbiguousTarget으로 거부한다.
4. 좌표가 필요한 경우 먼저 capture를 받아 이미지를 열어 본다. 이미지의 좌표를 그대로 창 내부 픽셀 좌표로 지정하고 captureId를 함께 보낸다.
5. 변경 입력 전에는 focus를 명시적으로 요청한다. 읽기 명령과 click·type-text가 몰래 다른 창을 활성화하지 않는다. 전경 창이 다르면 ForegroundMismatch로 거부한다.

windowId와 elementId는 불투명한 도구 식별값이다. 값의 내부 구조를 해석하지 않는다. 창이 재생성되거나 요소가 바뀌면 다시 조회한다. 실제 제목·실행 경로를 모르면 창 목록을 보고 결정하며, 임의로 만들어 넣지 않는다.

## 작업별 인자와 결과

아래는 API 1 작업 계약이다. 지원하지 않는 작업은 UnsupportedOperation으로 거부한다. arguments에는 표에 정의한 인자만 사용한다.

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

좌표 입력은 캡처된 창 내부 범위 안의 정수만 허용한다. output은 Controller 머신의 절대 파일 경로이며, 새 capture 요청마다 다른 파일 경로를 사용한다. 이미 파일이 있으면 OutputExists로 거부하고 덮어쓰지 않는다. 같은 요청 ID의 재조회는 원래 파일 정보만 반환하며 새 캡처를 하지 않는다. 이미지 data.coordinateSpace는 `window-client-pixels`이고 expiresAt은 이 캡처를 좌표 입력에 사용할 수 있는 마지막 시각이다.

capture는 UI 요소 조회와 독립적으로 수행한다. 이미지 전달까지 요청 제한 시간 안에 끝나고 작업 PC에 완성된 파일이 저장되어야 completed와 data.path를 반환한다. 실패하면 부분 파일을 최종 이미지로 남기지 않는다. 캡처 좌표의 유효 기간과 작업 PC에 저장한 이미지의 보관 기간은 별개다.

캡처는 Agent 촬영 완료·상주 Controller의 수신/검증·로컬 최종 파일 확정·공개 응답 저장 순서로 처리한다. 촬영만 끝났거나 전송에 실패한 결과는 completed가 아니다. CLI가 종료되어도 상주 Controller가 원래 제한 시간 안에서 처리·결과 저장을 맡는다. 실패가 확정된 캡처를 재조회해 새로 촬영하거나 전송하지 않는다. 재시작 후 이미 저장된 파일은 원래 요청·경로·해시를 검증한 경우에만 성공 결과로 복원한다.

text는 UTF-16 코드 단위로 1~4096자의 유효한 Unicode 문자열이며 Enter·Tab 같은 제어 키는 key 또는 chord로 보낸다. keys는 중복 없는 키 이름 배열 1~8개다. 키 이름은 CTRL·ALT·SHIFT·WIN·ENTER·TAB·ESC·BACKSPACE·DELETE·SPACE·LEFT·RIGHT·UP·DOWN·HOME·END·PAGEUP·PAGEDOWN·A~Z·0~9·F1~F24를 사용한다. F01·F+1 같은 별칭은 거부한다. 다른 키는 지원한다고 추정하지 않는다. 길게 누르는 down은 maxHoldMilliseconds 안에 해제되므로 일반 조합은 chord를 사용한다.

문자 수 상한은 요청 시간 안에 전부 입력된다는 보장이 아니다. 입력 소유 기록을 영속화하므로 긴 경로나 문장은 PC의 저장 장치 속도에 따라 5초를 넘을 수 있다. 정상 입력을 준비할 때는 짧은 문자열로 나누고 각 요청 완료를 확인한 뒤 다음 부분을 보낸다. 시간 초과가 발생한 요청은 원래 ID로 결과와 알려진 입력 수를 먼저 조회하고 실제 입력칸 값을 확인한다. 전체 문자열이나 같은 부분을 무조건 다시 보내지 않는다.

launch의 executable은 대상 Agent PC의 실행 파일 절대 경로, arguments는 문자열 배열이다. 셸 명령으로 결합하지 않는다. lifetime이 keep이면 세션을 닫아도 앱을 유지한다. session이면 세션 종료 시 도구 소유 앱을 종료한다. 이미 실행 중이던 앱은 terminate-owned의 대상으로 취급하지 않는다.

## 처음부터 한 번 조작하는 예

다음 ID와 버튼 이름은 설명용이다. **실제 호출에서는 각 응답을 읽고 그 값으로 다음 요청을 작성한다.** 시나리오처럼 여러 단계를 한 번에 도구에 전달하지 않는다.

1. --help·--status로 연결된 Agent를 조회하고, --status --machine TARGET으로 목표 머신의 API와 windows·controls·focus·click·capture 지원을 확인한다.
2. --session-open --machine TARGET --mode control --interaction-policy foreground-exclusive로 해당 대상 PC의 세션을 열고 data.controlSessionId·machineId를 확인한다.
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

7. capture를 요청한다. 저장 경로는 Controller 머신에 미리 준비한 쓰기 가능한 폴더를 쓴다. Agent의 임시 캡처 파일은 Controller가 수집한다.

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

각 요청 파일은 코딩 에이전트와 같은 머신에 작성하고 `Controller.exe --request 요청파일경로`로 실행한다. Controller가 대상 PC의 이미지를 수집한 뒤 반환하는 data.path·sha256을 읽고 같은 머신의 실제 파일을 열어 본다. 단순 파일 생성·완료 응답을 앱 기능 성공으로 판단하지 않는다. 필요하면 UI 값·화면·SSH 로그를 더 읽고 다음 요청을 정한다.

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
| ControllerUnavailable·ControllerResponseUnavailable·AgentDisconnected | 상주 Controller와 원격 Agent의 연결을 확인. 변경 요청의 응답 유실이면 요청 결과 조회부터 수행 |
| SessionExpired | 제품 상태를 다시 관측할 새 세션을 열고 새로운 요청 ID 사용 |
| UnsupportedOperation·UnsupportedApiVersion | --help·--status와 실제 배포 버전 확인. 지원하는 것으로 가장하지 않음 |
| DuplicateRequestMismatch | 같은 ID를 다른 내용에 재사용한 오류. 원래 요청 상태부터 확인 |
| RequestNotFound | 보관 기간·실행부 상태 확인. 입력이 실행되지 않았다는 증거로 취급하지 않음 |
| completionUnknown 또는 응답 유실 | --request-status로 먼저 조회하고 필요하면 화면을 다시 관측. 자동 재입력 금지 |

결과 조회가 running이면 같은 요청의 결과를 다시 조회한다. 완료 기록이 있으면 그 결과를 사용한다. 보관된 요청과 같은 ID·내용을 다시 보내면 기존 결과를 반환하며 입력을 반복하지 않는다. 기록이 없거나 실행부가 중간에 종료됐다면 실제 제품 상태를 다시 보고 다음 행동을 결정한다.

읽기 관측은 새 requestId로 반복할 수 있다. 제품 준비 완료를 기다릴 때도 에이전트가 자신의 마감 시간을 두고 새 관측을 요청한다. 한 번의 클릭이 오류 없이 끝났다는 이유로 화면 결과를 확인하지 않거나 같은 클릭을 반복하지 않는다.

## 최초 설정과 상주 실행

각 역할의 배포 폴더에서 PowerShell을 실행한다. `scripts/manage.ps1`는 .NET 10 Desktop Runtime을 확인한다. 상태 폴더는 역할별로 분리하며, 현재 사용자만 접근할 수 있도록 프로그램이 만든다. 비공개 개인키가 든 상태 폴더를 다른 PC에 복사하지 않는다. 상대에게 전달하는 파일은 공개 인증서 `identity.cer`뿐이다.

같은 PC의 예는 다음과 같다. 경로는 실제 배포 위치로 바꾼다. 초기 설정은 기존 JSON을 날짜가 붙은 `.bak`으로 보존한다. 주소·포트·머신 ID 등 설정값은 인증서를 만들기 전에 검사하며, 잘못된 값이면 새 설정·인증서를 생성하지 않는다. 이미 초기화한 상태 폴더에는 새 인증서를 덮어쓰지 않는다.

```powershell
# Controller 폴더
.\scripts\manage.ps1 -Action Initialize -StateDirectory C:\DesktopTestDriver\ControllerState -HostAddress 127.0.0.1 -Port 32550
# Agent 폴더. Controller의 공개 인증서를 전달받은 뒤 실행한다.
.\scripts\manage.ps1 -Action Initialize -StateDirectory C:\DesktopTestDriver\AgentState -MachineId test-pc -HostAddress 127.0.0.1 -Port 32550 -Certificate C:\DesktopTestDriver\ControllerState\identity.cer
# Controller 폴더. Agent의 공개 인증서를 전달받은 뒤 등록한다.
.\scripts\manage.ps1 -Action Register -MachineId test-pc -Certificate C:\DesktopTestDriver\AgentState\identity.cer
```

원격 Agent는 Controller PC의 도달 가능한 주소를 HostAddress로 지정한다. Controller의 ListenAddress는 해당 네트워크 주소 또는 `0.0.0.0`으로 설정하고 TCP 포트 접근을 환경에서 준비한다. 기존 SSH/SFTP 또는 수동 복사로 배포 폴더·공개 인증서를 전달한다. 개인키와 사용자 비밀번호를 명령 인자나 공유 로그에 넣지 않는다. 등록 변경 뒤에는 Controller를 재기동한다.

```powershell
# 각 역할의 배포 폴더에서 실행한다.
.\scripts\manage.ps1 -Action Start
.\scripts\manage.ps1 -Action Status
.\scripts\manage.ps1 -Action Stop
.\scripts\manage.ps1 -Action Restart
```

Agent 시작은 로그인한 일반 사용자의 바탕화면에서 수행한다. SSH 프로세스 시작만으로 GUI 실행 성공을 주장하지 않는다. SSH로 배치할 때는 현장에서 시작하거나 `-Action InstallLogonTask`로 현재 사용자의 대화형 로그온 작업을 준비한다. 이 작업은 사용자가 로그인할 때만 제한된 권한으로 시작한다. `RemoveLogonTask`는 같은 설정 경로로 생성한 작업만 제거한다. 다른 예약 작업·서비스를 건드리지 않는다. 현재 세션이 없거나 관리자 실행이면 Agent는 `NoInteractiveSession`을 반환한다.

Controller.json은 `stateDirectory`, `listenAddress`, `port`를 사용한다. Agent.json은 `stateDirectory`, `machineId`, `controllerHost`, `controllerPort`, `controllerCertificate`(등록한 공개 인증서의 SHA-256), `interactiveSessionId`(0이면 시작한 사용자 세션), `maxHoldMilliseconds`(기본·최대 1500), `maxStopMilliseconds`(기본·최대 3000), `machineUsage`(기본 shared, 전용 환경은 dedicated)를 사용한다. 기본 배포 JSON은 설정 예시이며 Initialize로 실제 상태 경로와 인증서를 생성해야 한다. `Controller.exe --config FILE --status`처럼 별도 설정을 쓸 수도 있다. `--config FILE`은 첫 옵션으로 지정한다.

설정·인증서 갱신 전에는 `-Action Backup`을 사용한다. 프로그램을 중단하고 비공개 상태 폴더 안의 새 backup 폴더에 설정과 인증서·등록 정보를 보존한다. `-Action Restore -BackupDirectory ...`는 필수 인증서 파일을 확인하고 프로그램을 중단한 뒤 이 정보를 복원한다. 백업에 peers.json이 없으면 등록이 없던 상태로 복원하므로 이후 추가한 등록도 제거한다. 기존 요청 기록·세션을 지워 재실행을 허용하지 않는다. 복원 뒤 Start는 명시적으로 수행한다. 보관 기록과 개인키는 현재 사용자 권한으로 유지한다.

Controller 상태 폴더에는 공개 최종 응답 `request-*.json`과 세션 기록을, Agent 상태 폴더에는 실제 실행 결과·`held-input.json`·`applications.json`·원본 PNG를 보관한다. 중간 수락·실행 결과는 일반 로그가 아닌 이 제한된 저장소에 남는다. 일반 최종 저널은 기본 24시간이 지난 뒤 새 mutation을 수락할 때 정리할 수 있다. unknown 원본은 보존한다. API 2 저널 내용은 현재 사용자 DPAPI로 암호화한다. 기본 한도는 저널 64MiB·원본 캡처 256MiB이며, 한도 도달 시 신규 mutation/캡처를 거부한다. 캡처를 임의 삭제해 공간을 확보하지 않는다. 용량 부족 시 수락 기록을 기록하지 못하면 새 입력을 시작하지 않는다. 작업 PC에 저장한 최종 PNG의 보관은 호출자가 관리한다.

캡처는 대상 창의 클라이언트 표면을 1:1로 얻으며 출력은 SDR PNG다. HDR 원본의 색 정확도는 보장하지 않는다. 다른 앱의 픽셀이나 Windows 필기용 마우스 통과 레이어를 이미지에 합성하지 않는다. 일반 불투명 창의 가림·최소화·숨김·모니터 밖 상태는 거부하며 창을 몰래 앞으로 가져오지 않는다. 캡처 알림 테두리가 잠시 나타날 수 있다. 캡처 대기 한도는 2200ms이며 원래 요청의 남은 시간도 함께 적용한다.

요소 목록은 최대 1024개·깊이 16으로 제한하며 UI 조회 대기 한도는 3000ms다. 원래 요청의 남은 시간이 더 짧으면 그 시간을 적용한다. 값은 최대 65536자로 읽으며 암호는 반환하지 않는다. 동적으로 변한 화면은 새 관측으로 확인한다.

앱 실행 직전에는 보호된 launch-intent 기록을 남긴다. 프로세스 생성 직후 장애로 session 앱의 실제 소유 정보를 확인하지 못하면 종료 성공을 추정하지 않으며 세션을 blocked로 유지한다. keep 앱은 유지하고, 알 수 없는 PID를 추측해 종료하지 않는다. 남은 소유 정보는 상태 폴더의 launch-intent 기록과 세션의 retainedProcessIds로 확인한다.

### 클라이언트 응답 유실과 취소

Controller 연결 전에 실패하면 `ControllerUnavailable`을 반환한다. 유효한 요청을 전송하기 시작한 뒤 응답을 잃으면 `ControllerResponseUnavailable`과 `completionUnknown`, 원래 `requestId`·`controlSessionId`·`operation`을 반환한다. 읽기 요청도 완료 여부를 확인하지 못했으므로 `running`으로 단정하지 않는다. 이 응답은 클라이언트의 관측이며 Agent 세션이 차단됐다는 증거가 아니다. 새 변경 요청을 멈추고 원래 요청의 결과와 세션 상태를 조회한다. 변경 요청을 새 ID로 재전송하지 않는다.

C# SDK의 세션 열기·닫기·결과 조회는 호출 전에 취소된 토큰이면 연결을 시작하지 않는다. 전송 후 대기 취소는 서버 작업 취소를 보장하지 않는다.

부모·조상 선택자 검색은 관측마다 요소 ID 인덱스를 한 번 만들어 부모를 찾는다. 반복적인 전체 배열 탐색을 줄이면서 중복 대상과 불완전한 관측의 거부 규칙을 유지한다.
