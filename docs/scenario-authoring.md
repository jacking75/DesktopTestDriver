# 코딩 에이전트를 위한 DesktopTestDriver 시나리오 작성 규약

이 문서는 외부 프로젝트의 코딩 에이전트가 DesktopTestDriver 시나리오를 작성할 때 전달하는 기준 문서다. 시나리오와 대상 앱의 준비·관측 코드는 사용하는 프로젝트가 소유한다. DesktopTestDriver는 앱과 무관한 문법·명령·실행·판정·정리를 담당한다. 시나리오를 추가할 때 Controller/Agent 수정, 재빌드, 앱별 C# 어댑터 등록을 하지 않는다.

## 코딩 에이전트에게 전달할 지시

> DesktopTestDriver의 `docs/scenario-authoring.md`를 읽고 schemaVersion 1 시나리오와 bindings 파일을 이 프로젝트에 작성하라. Controller/Agent 소스는 수정하지 마라. 실제 실행 파일·세션·창·UIA·결과 관측을 근거로 작성하고 추측 HWND·좌표·고정 sleep을 사용하지 마라. 입력 접수와 앱 기능 성공을 구분하고 마지막 관측으로 기대 결과를 판정하라. `scenario-check`로 정적 검증할 수 있게 작성하되 실제 화면 조작은 승인된 실행 계획에 따라 별도로 수행하라. 지원하지 않는 동작은 구현했다고 가정하지 말고 필요한 관측/기능을 보고하라.

## 파일과 책임

사용하는 프로젝트에 다음 파일을 둔다. DesktopTestDriver 설치 폴더에 복사할 필요가 없다.

```text
my-project/tests/desktop/
  click-button.scenario.json    # 재사용할 절차·조건·판정
  lab.bindings.json             # 이번 환경의 파라미터와 역할 → run 파일
  target.run.json               # 이번 작업의 머신·세션·경로·hash·허용 범위
```

시나리오의 `target` 같은 역할 이름은 논리 이름이다. 머신 주소·machineId·사용자 세션·실행 파일 경로/hash·만료 시각·복귀 증거는 run 파일로 분리한다. bindings의 run 파일 경로는 **bindings 파일의 폴더 기준**이다. 절대 경로도 허용한다. parameters에 필요한 앱 ID·창 제목·텍스트·UIA ID를 전달한다. 주소/세션 등이 바뀌어도 시나리오와 도구 코드는 바뀌지 않는다.

Controller/Agent는 미리 실행돼 있어야 한다. 시나리오 실행은 선언한 역할의 작업을 시작하고, 단계들을 배열 순서대로 실행하고, 성공·실패·취소 모두에서 시작한 작업을 종료한다. 상시 Agent는 유지한다. 하나의 시나리오가 여러 역할을 조작할 수 있으며 단계는 병렬 실행하지 않는다.

## 명령행 계약

```text
Controller.exe scenario-check C:\my-project\tests\desktop\click-button.scenario.json
Controller.exe scenario-check C:\my-project\tests\desktop\click-button.scenario.json C:\my-project\tests\desktop\lab.bindings.json
Controller.exe scenario-run C:\my-project\tests\desktop\click-button.scenario.json C:\my-project\tests\desktop\lab.bindings.json
```

check는 Agent 접속·앱 실행·입력을 하지 않는다. bindings까지 지정하면 역할·파라미터·run 설정과 필요한 명령 허용 범위를 함께 검사한다. 실행 시에만 현재 접속·작업 충돌·만료와 실제 환경을 검사한다. 정적 검증 통과는 기능 PASS가 아니다.

## 시나리오 JSON

UTF-8 JSON을 사용한다. 주석·후행 쉼표·중복 키·알 수 없는 필드는 허용하지 않는다. 필드 이름은 아래 camelCase로 작성한다.

| 필드 | 규칙 |
|---|---|
| `schemaVersion` | 정수 `1`. 알 수 없는 버전은 거부한다 |
| `id` | 1~80자 영문·숫자·`-`·`_` |
| `purpose` | 검증 목적과 성공의 의미를 설명하는 문자열 |
| `roles` | 중복 없는 역할 ID 1~8개. 서로 다른 머신에 연결한다 |
| `parameters` | 필요한 파라미터 ID 목록. bindings에 정확히 같은 키를 제공한다 |
| `maximumSeconds` | 시작·단계·정리를 포함한 총 상한 20~900초. 정리 예약 시간은 역할당 8초 + 2초다 |
| `steps` | 순서대로 실행하는 단계 1~128개. ID는 중복 불가 |

각 단계는 다음 필드를 사용한다.

| 필드 | 규칙 |
|---|---|
| `id`, `role` | 단계 ID, 선언된 역할 ID |
| `command` | 아래 지원 명령의 JSON 객체. `name`은 고정 문자열 |
| `select` | 선택 사항. 응답 배열에서 항목 하나를 고르는 `{ "path": JSON_POINTER, "where": [조건] }` |
| `expect` | 선택 사항. 선택 값에 적용할 조건 배열. 모두 만족해야 한다 |
| `waitFor` | 기본 false. true이면 읽기 명령을 조건이 충족될 때까지 관측한다 |
| `timeoutMilliseconds` | 기본 5000, 1~5000. 대기 반복을 포함한 단계 전체 상한 |
| `pollMilliseconds` | 기본 100, 100~1000. waitFor 관측 사이 간격 |

시나리오에는 최소 한 개의 `expect` 조건이 있어야 한다. 실제 기능 PASS를 주장하려면 입력 이후 앱의 상태·UIA·결과 파일을 확인하는 조건을 둔다. `launch` 성공, `executed: true`, `injectionAccepted: true`만으로 앱 기능을 통과 처리하지 않는다.

## 결과 참조와 선택

각 명령의 응답은 `{ commandId, accepted, executed, verified, errorCode, observedAt, durationMilliseconds, evidence }`다. `errorCode != "None"` 또는 accepted/executed가 false이면 즉시 실패한다. `verified`는 명령 자체의 관측 결과이며 입력 명령은 정상이어도 false일 수 있다.

단계 결과는 `{ "response": 원본응답, "value": 선택값 }`으로 보존한다. select가 없으면 value는 response.evidence다. select가 있으면 response 전체에서 path로 찾은 배열을 where 조건으로 거르고, 정확히 한 항목만 허용한다. 0개는 관측 미충족, 2개 이상은 모호한 대상이므로 즉시 실패한다. 첫 항목이나 인덱스 0을 임의로 고르지 않는다.

command와 조건의 value에서 다음 **객체 전체**를 사용하면 JSON 타입을 유지해 참조한다.

```json
{"$ref":"/parameters/text"}
```

```json
{"$ref":"/steps/window/value/window"}
```

루트에는 `/parameters`와 이미 완료한 `/steps/단계ID`만 존재한다. 미래 단계·현재 단계·없는 값·잘못된 JSON Pointer는 거부한다. 객체·배열·숫자·bool도 문자열 변환 없이 전달한다. 문자열 내부 보간, `${...}`, 표현식, 셸, C#, Python 코드, 조건 분기·입력 재시도는 지원하지 않는다. `$ref` 객체에는 다른 키를 넣지 않는다.

경로는 JSON Pointer다. `""`는 현재 값, `/window/pid`는 속성, `/controls/0/name`은 배열 항목이다. 속성 이름의 `~`와 `/`는 `~0`, `~1`로 적는다. 경로 대소문자는 구분한다. 배열 인덱스는 실제 개수·식별성을 확인한 경우만 사용한다.

## 조건과 대기

조건은 `{ "path": "/name", "op": "eq", "value": "expected" }`다. select.where의 path는 각 배열 항목 기준이고, expect.path는 선택값 기준이다. 비교 value도 `$ref`를 지원한다.

| op | 의미 |
|---|---|
| `eq`, `ne` | JSON 타입을 포함한 동등/비동등 비교. 문자열 대소문자 구분 |
| `gt`, `ge`, `lt`, `le` | 유한 숫자 비교 |
| `contains` | 문자열의 부분 문자열 포함 또는 배열의 JSON 값 포함 |
| `count-eq`, `count-ge`, `count-le` | 배열 길이와 0 이상의 정수 비교 |
| `exists` | 경로 존재 여부. value는 true/false. null은 존재하는 값이다 |

exists 외의 조건에서 없는 경로는 불충족이다. waitFor false에서 선택 0개 또는 조건 불충족은 즉시 FAIL이다. waitFor true는 **inspect/controls/read-artifact만** 허용하며, 선택 0개 또는 조건 불충족일 때만 같은 읽기 명령을 다시 보낸다. 명령 오류·모호한 선택·대상 변경·단절은 재시도하지 않는다. 입력·launch를 반복하지 않는다. 고정 sleep을 준비 신호로 대체하지 않는다. 응답이 나오기 전 결과 파일 읽기 자체가 실패하는 상황은 앱/fixture 준비 단계에서 해결해야 한다.

## 지원 명령

| name | 필드와 결과 evidence |
|---|---|
| `launch` | `applicationId`. run.applications에 등록한 프로그램을 고정 인자로 실행. 결과 `{pid, createdAt, imagePath, imageHash}` |
| `inspect` | 인자 없음. 결과 `{observation, frames}`. frame은 `{id, window, observation, controls, ...}` |
| `controls` | `expectedWindow`. UIA 조회. 결과 frame, `controls` 배열에 name/automationId/controlType/bounds/enabled/toggle |
| `focus` | `expectedWindow`, `frameId`. 결과 새 frame |
| `snapshot` | `expectedWindow`, `frameId`. foreground와 이미지 권한 필요. 결과 frame의 imagePath/imageHash는 Agent 측 증거 |
| `type-window` | 실제 관측 `hwnd` 또는 유일한 `title`, `text`. 내부에서 inspect/focus/text 수행 |
| `click-control` | 실제 관측 `hwnd` 또는 유일한 `title`, `automationId`. 내부에서 inspect/focus/controls/click 수행 |
| `move-window`, `click-window` | 실제 관측 hwnd/title, 창 좌상단 기준 픽셀 `x`, `y`. 내부에서 inspect/focus 후 이동/클릭 |
| `move`, `click`, `wheel`, `drag` | `expectedWindow`, `frameId`, 창 기준 픽셀 x/y. wheel은 `delta`, drag는 `endX`, `endY`, `steps`. 버튼은 left/right/middle |
| `key-down`, `key-up`, `chord` | `expectedWindow`, `frameId`, 가상키 정수 배열 `keys`. down/up은 한 개, chord는 1~8개 |
| `text` | `expectedWindow`, `frameId`, `text` 1~256자. 제어 문자 불가 |
| `read-artifact` | `condition`: run.observationFiles에 등록한 ID. json/ndjson은 `{id, hash, value, source}`, file은 `{id, hash, size, source}` |
| `release-all` | 입력 해제. 결과 `{released}` |

원시 command의 timeoutMilliseconds는 1~5000이며 생략 시 1000이다. type-window/click-control/move-window/click-window의 command에는 이 필드를 넣지 않고 단계의 timeoutMilliseconds로 전체 시간을 제한한다. 런타임 헤더(runId/sequence/deadline/manifestHash 등)는 Controller가 생성하므로 작성하지 않는다. 범위 밖 필드·명령은 거부한다. UIA로 텍스트 설정을 대신하거나 임의 프로세스/서비스/셸을 실행하는 명령은 없다.

raw 입력 명령의 frameId/expectedWindow는 **같은 최신 frame**에서 가져온다. 입력 직전에 foreground와 창 동일성을 확인한다. 오래된 frame을 다음 단계들에 계속 쓰지 않는다. 단순 텍스트/버튼 클릭은 최신 관측을 내부 수행하는 type-window/click-control을 우선 사용한다. 좌표를 쓸 경우 UIA 또는 이미지 관측에서 실제 좌표를 얻고 bounds·DPI·foreground를 확인한다.

expectedWindow는 관측한 `{hwnd, pid, createdAt, imagePath, imageHash, title, bounds}` 전체다. 임의로 재구성하지 않는다. x/y/endX/endY는 0 이상이며 창의 width/height보다 작아야 한다. drag.steps는 1~100(기본 10), wheel.delta는 -1200~1200이다. keys는 중복 없이 지정하며 run.allowedKeys 범위 안이어야 한다.

## bindings와 run 설정

```json
{
  "parameters": {
    "applicationId": "sample-app",
    "text": "aaa",
    "resultFileId": "app-result"
  },
  "roles": { "target": "target.run.json" }
}
```

run 파일은 DesktopTestDriver의 `inventory/run.template.json` 형식을 사용한다. 매 회차 새 runId와 현재의 expiresAt을 지정한다. role은 시나리오 역할과 같아야 한다. maxSeconds와 expiresAt은 시나리오 전체 상한을 수용해야 한다. 사용하는 머신·앱 경로/hash·창 범위·허용 키·관측 파일을 실제 환경에 맞게 제한한다. 시나리오 JSON은 이 범위를 넓힐 수 없다.

delegation.actions에는 실제 기본 명령과 heartbeat를 허용한다. type-window는 inspect/focus/text, click-control은 inspect/focus/controls/click, move-window는 inspect/focus/move, click-window는 inspect/focus/click이 필요하다. release-all은 항상 허용한다. 현재 개발용 상시 연결은 서명을 검사하지 않는다. 과거 어댑터용 ScenarioPlan/plan-check/simulate 형식과 이 문서의 schemaVersion 1 형식은 별개다.

앱 실행·입력에는 allowInput, R1 이상의 준비·복귀 증거가 필요하다. run.applications의 ID는 별칭이며 실행 파일과 인자는 run 파일에 미리 고정한다. 기존 사용자 앱을 시험 소유 프로세스로 가정하지 않는다. 기존 앱의 상태 복원과 외부 서비스/데이터 복원은 사용하는 프로젝트가 계획한다. 기본 종료는 입력 해제·작업 소유 프로세스 정리·정상 desktop 확인이다.

## 완전한 절차 예제

아래 예제는 사용하는 프로젝트의 앱이 시작 시 입력 칸에 포커스를 두며, 미리 생성한 결과 JSON에 `{ "text": "aaa" }`를 기록한다는 계약을 가정한다. 결과 파일은 이번 run 전용이며 준비 단계에서 생성하고 run.observationFiles에 등록한다. 앱의 실제 관측 계약이 다르면 입력 칸 선택과 마지막 read-artifact/expect를 그 계약에 맞게 작성한다. 시나리오 자체가 앱에 관측 기능을 만들어 주지는 않는다.

```json
{
  "schemaVersion": 1,
  "id": "type-and-observe",
  "purpose": "시험 앱에 텍스트를 입력하고 앱 결과 파일로 반영을 확인한다",
  "roles": ["target"],
  "parameters": ["applicationId", "text", "resultFileId"],
  "maximumSeconds": 60,
  "steps": [
    {
      "id": "launch", "role": "target",
      "command": {"name": "launch", "applicationId": {"$ref": "/parameters/applicationId"}}
    },
    {
      "id": "window", "role": "target",
      "command": {"name": "inspect"},
      "select": {
        "path": "/evidence/frames",
        "where": [{"path": "/window/pid", "op": "eq", "value": {"$ref": "/steps/launch/value/pid"}}]
      },
      "waitFor": true, "timeoutMilliseconds": 3000
    },
    {
      "id": "type", "role": "target",
      "command": {
        "name": "type-window",
        "hwnd": {"$ref": "/steps/window/value/window/hwnd"},
        "text": {"$ref": "/parameters/text"}
      }
    },
    {
      "id": "observed", "role": "target",
      "command": {"name": "read-artifact", "condition": {"$ref": "/parameters/resultFileId"}},
      "expect": [{"path": "/value/text", "op": "eq", "value": {"$ref": "/parameters/text"}}],
      "waitFor": true, "timeoutMilliseconds": 3000
    }
  ]
}
```

launch 후 다른 프로세스로 실행을 넘기는 앱에서는 이 PID 조건을 그대로 사용하지 않는다. 실제 imagePath/imageHash/title 등으로 유일하게 식별한다. 최상위 창이 여러 개인 앱도 조건을 추가한다.

## 실패·정리·증거

첫 실패에서 뒤 단계는 실행하지 않는다. 단계 시간 초과·전체 시간 초과·사용자 end·Ctrl+C·Controller 종료도 정리 경로로 들어간다. 재접속·재기동 후 시나리오와 입력은 자동 재실행하지 않는다. 실행 중인 역할에 외부 request/begin을 섞을 수 없다. `Controller.exe end MACHINE`으로 해당 시나리오 전체를 중단할 수 있다.

전체 상한 안에서 역할당 8초의 종료 예산과 2초의 결과 기록 여유를 예약한다. 남은 실행 예산이 없으면 새 단계를 시작하지 않는다. Agent 정리 영수증의 clean/normalDesktop과 runId를 확인해야 PASS다. 정리를 확인하지 못하면 FAIL이고 해당 머신의 새 작업은 기존 정리 게이트를 따른다. Controller 장애 시 Agent의 단절 처리와 독립 watchdog이 별도로 작동한다.

Controller stateRoot의 `scenarios/실행ID/`에 plan.json, execution.json(환경 설정 포함), events.jsonl(각 관측·선택·판정), report.json을 기록한다. 최종 CLI 응답은 실행ID·시나리오 hash·판정·단계별 상태·정리 결과·증거 폴더다. plan hash는 정규 직렬화한 JSON의 SHA-256이며 소스 파일 공백과 무관하다. 역할별 run 설정과 parameters는 execution.json에 보존한다. 한 실행의 명령 요청은 최대 1024회, events 기록은 최대 16MiB이며 초과 시 중단·정리한다. 보고서의 PASS는 작성한 expect 조건의 충족을 뜻한다. 이미지 의미를 자동 판독하거나 제품 전체 수용을 선언하지 않는다.

현재 계약은 구현·빌드 검토 대상이다. 실제 Controller/Agent/입력/장애 정리 수용은 별도 실행 검증 후 기록한다. unsupported 기능을 표현식이나 셸로 우회하지 않는다. 관측이 UIA·이미지·등록한 파일로 제공되지 않으면 사용하는 프로젝트에서 관측 수단을 마련하거나 준비 차단으로 보고한다.
