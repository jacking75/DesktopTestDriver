# DesktopTestDriver 개선 계획

2026-11-11 검토, 같은 날 사용자 결정을 모두 반영해 다시 썼다. 저장소 `main`(커밋 `22b3fd9`) 기준의 작업 지시서였다. **2026-11-12에 구현했다. 결과와 하지 못한 항목은 [7장](#7-구현-결과-2026-11-12)에 있다.** 아래 본문의 `파일:줄` 위치는 구현 전 코드를 가리킨다.

## 0. 읽는 법

- 1장은 확정된 방침, 2장은 구현이 끝난 뒤의 모습, 3장은 단계별 작업, 4장은 하지 않는 것이다.
- 위치는 `파일:줄` 형식이고 현재 코드를 가리킨다. 이 저장소는 한 줄이 매우 길어서, 줄 번호와 함께 적은 코드 조각으로 찾는다.
- 문제마다 확신도를 붙였다.
  - **확실**: 코드 경로가 하나뿐이라 정독만으로 결론이 난다.
  - **재현 필요**: 타이밍이나 외부 동작에 달려 있다. 고치기 전에 실패하는 테스트를 먼저 만든다.
- `src/Core/Core.csproj`는 `EnableDefaultCompileItems=false`이고 `<Compile Include>`를 파일마다 나열한다. **파일을 추가·삭제하면 csproj도 고친다.**
- 빌드·테스트는 단계 끝에 한 번씩만 돌린다(`.\build.ps1 -Configuration Debug`, 마지막에 `.\verify.ps1`).

**검토 범위**

| | |
|---|---|
| 읽은 것 | `src/Contracts`, `src/Client`, `src/Controller`, `src/Agent` 전체, `src/Core` 전체(`Native.cs` 제외), `README_kr.md`, `docs/agent-usage.md`, `docs/gui-test-guide.md` |
| 읽지 않은 것 | `tests/Driver.Tests` 대부분, `scripts/`, `build.ps1`, `verify.ps1` |
| 실행 | 빌드·테스트를 실행하지 않았다. 문제는 모두 코드 정독으로 찾았다 |
| 오픈소스 조사 | 각 프로젝트의 README·문서를 웹에서 확인했다. 파라미터 철자는 요약 도구를 거쳤으므로 원문을 다시 본다 |

## 1. 확정된 방침

사용자가 정한 것이다. 본문과 충돌하면 이 장이 우선한다.

| 번호 | 방침 |
|---|---|
| P1 | **대상**: Windows 전용. 데스크톱 앱과 Windows 자체 GUI(제어판, 설정 등) 같은 GUI 도구. 그래픽스 게임 화면은 대상이 아니다 |
| P2 | **목적**: Claude·Codex의 computer use(스크린숏 + 좌표)보다 빠르고 정확하게 GUI를 테스트한다 |
| P3 | **호환 없음**: 과거 버전 호환이 필요 없다. API 1·2 구분을 없애고 하나로 합친다. 쓰지 않는 것은 삭제한다 |
| P4 | **보안 없음**: 신뢰된 환경에서만 쓴다. 인증·암호화·권한 검사·접근 제한을 **전부** 뺀다. 단순하고 빠르게 동작하는 것이 우선이다 |
| P5 | **속도 우선**: 효율과 속도에 반하는 설계는 바꾼다 |
| P6 | 읽기 전용 대기(`wait-condition`, `wait-window`)의 요청당 상한은 30초다. 다른 작업은 5초다 |
| P7 | 실제 입력 세션의 lease는 조작 성공 시 자동 연장하고 상한은 15분 고정이다 |
| P8 | 가상화 컨테이너 밖의 대상은 기본으로 찾을 수 있다. 컨테이너 안은 `completeness: "realized"`를 명시한다 |
| P9 | Agent의 캡처 사본은 Controller가 받은 것을 확인하면 지우고, 그렇지 않아도 24시간 뒤 지운다 |
| P10 | 한 요청에 여러 조작 단계를 담는 묶음 요청은 만들지 않는다 |

**보안과 정확성의 구분.** P4로 빼는 것은 "누가 접속하고 무엇을 볼 수 있는가"를 제한하는 장치다. 아래는 테스트 결과가 틀리지 않게 하는 장치이므로 유지한다. 캐시를 적용하면 실행 시간에 거의 영향이 없다.

- 프로세스·창·요소 신원 재검증, 모호한 대상 거부
- 변경 요청의 중복 실행 방지(같은 요청 ID를 다시 보내도 재실행하지 않음)
- 실제 입력 중 사람의 입력 감지와 즉시 중단(섞이면 테스트 결과가 틀린다)
- 도구가 누른 키·버튼의 해제 보장, 세션이 띄운 프로세스의 정리

## 2. 구현 후의 모습

### 2.1 구성과 연결

- `Controller.exe`(에이전트 쪽 PC)와 `Agent.exe`(테스트 대상 PC) 두 프로그램은 그대로다. 같은 PC에서 둘 다 실행해도 된다.
- 연결은 **평문 TCP**다. 인증서, 토큰, 등록 절차가 없다. Agent가 Controller에 접속하고 `machineId`를 알린다.
- 설정 파일 없이 실행 인자만으로 뜬다.

```powershell
# 에이전트 쪽 PC
.\Controller.exe --serve --listen 0.0.0.0:25001
# 테스트 대상 PC (로그인된 바탕화면에서)
.\Agent.exe --controller 192.168.0.10:25001 --machine test-pc
```

- 기본값: Controller는 `127.0.0.1:25001`에서 듣는다. 포트를 인자로 주지 않으면 이 값을 쓴다. Agent의 상태 폴더는 `%LOCALAPPDATA%\DesktopTestDriver\Agent`, Controller는 `...\Controller`다.
- 주 사용 경로는 `Controller.exe --mcp`(MCP 서버) 또는 `--json-lines`(프로세스 하나 유지)다. `--request FILE`은 일회성 용도다.

### 2.2 세션

```powershell
.\Controller.exe --session-open --machine test-pc --launch C:\Tools\MyApp.exe
```

| 옵션 | 의미 |
|---|---|
| `--input` | 실제 키·마우스 입력을 쓴다. 없으면 UIA 조작만 한다 |
| `--observe` | 읽기만 한다 |
| `--launch EXE [--arg …]` | 세션을 열면서 앱을 실행하고 첫 창까지 기다려 돌려준다 |
| `--target-pid N` | 이미 실행 중인 프로세스를 대상으로 한다. 여러 번 줄 수 있다 |
| `--target-exe PATH` | 그 경로로 실행 중인 프로세스 전부 |
| `--target-window-title T`, `--target-window-title-contains T`, `--target-window-class C` | 창 조건으로 대상을 정한다 |
| `--target-any` | 그 사용자 세션의 모든 창 |
| `--idle-seconds N` | 유휴 만료. 기본 900 |

- 범위를 주지 않은 세션의 대상은 "이 세션이 `launch`·`open`한 것"이다.
- `--input` 세션은 Agent당 하나만 열 수 있다. 입력을 쓰지 않는 세션은 여러 개 열 수 있다.
- 세션 상태는 `active`, `blocked`, `closed`, `expired`다. `blocked`는 실제 미확정 변경(`completionUnknown`)이 있거나 입력 해제에 실패했을 때만 된다.

### 2.3 요청과 응답

- `apiVersion` 필드가 없다. 작업 집합은 하나다.
- 요청: `requestId`, `controlSessionId`, `operation`, `timeoutMilliseconds`(선택), `arguments`, `note`(선택, 사람이 읽을 설명 120자 이하).
- 읽기 요청의 `requestId`는 응답에 그대로 돌려주는 상관 ID일 뿐이다. 변경 요청의 `requestId`만 중복 방지와 결과 조회에 쓰인다.
- 응답 `status`: `completed`, `rejected`(실행 전 거부, 효과 없음), `failed`(실행 중 오류), `completionUnknown`(디스패치를 시작했고 끝났는지 모름), `running`.
- 모든 응답에 `elapsedMilliseconds`가 붙는다.
- 한글은 이스케이프하지 않고 UTF-8 그대로 내보낸다.

### 2.4 작업 목록

| 분류 | 작업 |
|---|---|
| 창 | `windows`, `find-window`, `wait-window`, `window-state`, `focus`, `close-window` |
| 앱 | `launch`, `open`, `terminate-owned` |
| 관측 | `inspect`, `find-element`, `capabilities`, `wait-condition`, `find-item`, `read-grid`, `read-text`, `generate-selector`, `capture`, `clipboard-get` |
| UIA 조작 | `invoke`, `set-value`, `set-range-value`, `select-item`, `set-toggle`, `set-expanded`, `set-focus`, `legacy-default-action`, `realize-item`, `scroll-item-into-view`, `scroll-container`, `clipboard-set` |
| 실제 입력 (`--input` 세션) | `click`, `type-text`, `key`, `chord`, `move`, `wheel`, `drag`, `release-input` |

삭제되는 작업: `controls`(`inspect`와 겹친다).

### 2.5 관측 응답

`inspect`의 기본 형식은 compact다. `data.tree`에 문자열 하나로 들어간다.

```
- Window "간단 메모" [e1 t=3fa9c1d20b7e]
  - Edit #MemoText "메모" [e2 t=91b04c77a2d1] value="테스트 메모입니다" patterns=Value
  - Button #SaveButton "저장" [e3 t=5d02e8b1c9f0] patterns=Invoke
  - Button #OpenButton "열기" [e4 t=0a7711e3bb52] patterns=Invoke disabled
```

- 한 줄이 한 노드다: `ControlType #AutomationId "Name" [참조 t=토큰]` 뒤에 기본값이 아닌 속성만 붙는다.
- 참조는 세션 단위 카운터(`e1`, `e2`, …), 토큰은 12자다.
- `format: "json"`을 주면 구조화된 노드 배열로 받는다.

### 2.6 준비 절차 비교

| | 현재 | 구현 후 |
|---|---|---|
| 연결 | 양쪽 초기화 → 인증서 복사 → 등록 → 재시작 | 실행 인자에 주소 |
| 기존 앱 관측 | PID·시작 시각·경로·SHA-256을 담은 `scope.json` | `--target-pid` |
| 백그라운드 UIA 조작 | 프로필 작성 → 바인딩 → 등록 → 증거 파일로 인증(30일마다) | 바로 가능 |
| 캡처 | `captureAllowed` 프로필 등록 | 바로 가능 |
| 관리자 권한 앱 | 불가 | Agent를 관리자로 실행하면 가능 |
| 제어판·설정 앱 | 범위 밖으로 거부 | `open` 작업으로 열고 조작 |

## 3. 작업

단계는 순서대로 한다. 각 단계는 독립적으로 병합할 수 있다.

| 단계 | 내용 |
|---|---|
| 0 | 기준 측정과 재현 테스트 |
| 1 | 걷어내기: 보안 장치, API 버전 구분, `controls` |
| 2 | 저널과 저장소 |
| 3 | 요청 처리의 결과 분류와 세션 상태 |
| 4 | 전송과 연결 |
| 5 | 실행 속도 |
| 6 | 대상 범위와 창 |
| 7 | 관측 형식 |
| 8 | 찾기와 대기 |
| 9 | 조작 |
| 10 | 도구와 SDK |
| 11 | 문서, 스키마, 검증용 대상 |

---

### 단계 0. 기준 측정과 재현 테스트

**0.1 벤치마크.** `tests/`에 합성 프로바이더 기준 측정을 추가하고 변경 전 값을 `artifacts/perf-baseline.txt`에 남긴다. 이후 단계마다 다시 재서 비교한다.

- `find-element` 1,000회, `set-value` 200회, `type-text` 500자, `capture` 20회
- CLI `--request` 20회와 `--json-lines` 20회

**0.2 재현 테스트.** 아래는 고치기 전에 실패하는 테스트를 먼저 만든다.

| 대상 | 테스트 |
|---|---|
| 3.1 | Controller → Agent 경로로 없는 요소에 `wait-condition`(`timeoutMilliseconds: 300`). 응답 `status`와 이후 세션 `state`를 확인한다 |
| 3.2 | 합성 프로바이더에 "디스패치 전 거부" 스위치를 추가한다. `status`와 세션 `state`를 확인한다 |
| 2.1 | 읽기 요청 5,000회 뒤 변경 요청 한 건의 `status`와 소요 시간 |
| 4.1 | 응답이 프레임 한도를 넘는 `inspect`. 연결과 다른 세션이 유지되는지 확인한다 |

**0.3 computer use 비교.** 같은 시나리오(SimpleMemo 저장·재열기)를 이 도구와 computer use로 각각 10회 돌려 소요 시간, 토큰 수, 성공률, 오조작 횟수를 기록한다. 단계 11이 끝난 뒤 다시 잰다. P2가 달성됐는지 확인하는 근거다.

---

### 단계 1. 걷어내기

코드가 크게 줄어 이후 단계의 수정 범위가 작아진다. 삭제가 대부분이므로 먼저 한다.

#### 1.1 보안 장치를 전부 뺀다 (P4)

**A. 연결 인증과 암호화**

- mTLS, 인증서 생성·등록, 인증서 고정을 삭제한다. `TcpClient`/`TcpListener`의 스트림을 `RpcPeer`에 직접 넘긴다.
- 삭제: `ConnectionSettings.Initialize`/`Register`/`Certificate`/`Hash`, `peers.json`, `identity.pfx`·`identity.cer`, `PeerRegistration`, `AgentSettings.ControllerCertificate`, `SslStream` 사용부(`AgentHost.cs:163-165`, `ControllerHost.cs:236-242`), `CertificateDates`(`AgentHost.cs:207-210`), 오류 `MachineCertificateMismatch`, `JournalEntry.CertificateHash`와 그 비교(`ControllerHost.cs:146`), Controller `--initialize`·`--register`, Agent `--initialize`.
- `AgentHello`에서 `User`를 뺀다. `machineId`와 `WireVersion`만 남긴다.
- `scripts/manage.ps1`에서 Initialize·Register·Backup·Restore를 삭제하고 Start·Stop·Status·Restart·로그온 작업 등록만 남긴다. `scripts/check-package.ps1`이 인증서 파일을 검사하면 그 부분을 뺀다(이 스크립트는 읽지 않았다).

**B. 설정 없이 실행**

- Agent: `--controller HOST:PORT --machine NAME [--state DIR]`. Controller: `--serve [--listen ADDR:PORT] [--state DIR]`. 기본값은 2.1과 같다.
- 설정 파일(`Agent.json`, `Controller.json`)은 선택 사항으로 남긴다. 있으면 읽고 인자가 우선한다.
- `machineUsage`, `interactiveSessionId`, `maxHoldMilliseconds`, `maxStopMilliseconds`는 인자·설정에서 받되 기본값으로 충분하다.
- 기본 포트를 32550에서 **25001**로 바꾼다. 테스트 코드가 고정 포트를 쓰면 25001~25199 범위로 옮긴다.

**C. 앱 프로필과 비간섭 인증**

- 삭제: `ProfileRegistry.cs`, `ProfileValidator.cs`, `AutomationContracts.cs`의 `ApplicationBindings`·`ProfileAction`·`ApplicationProfile`·`MachineBindings`, `BackgroundQualification`·`QualifiedAction`, Agent `--register-profile`·`--qualify-profile`, Controller `--profile-validate`·`--application-profile`, `SessionInfo.ApplicationProfileId`, `examples/standalone/forms.profile.json`, `schemas/application-profile.schema.json`·`machine-bindings.schema.json`.
- 삭제할 검사: `BackgroundQualificationRequired`(`GuiRequestHandler.cs:36-37`), `ProfileRegistry.Check`(`:211`), 프로필 기반 값 가림(`:239-246`), `InScope`·`Scope`의 프로필 분기(`:260-263`).

**D. 캡처 권한**

- `CaptureNotAuthorized` 검사(`GuiRequestHandler.cs:322-327`)를 삭제한다.
- `capture`의 `output`을 생략하면 Controller 상태 폴더 아래 `captures/`에 저장하고 경로를 돌려준다.
- 저장 경로의 재분석 지점 검사(`ControllerHost.cs:209`의 `UnsafeOutputPath`)를 삭제한다.

**E. 상호작용 정책**

- `machineUsage`, 정책 `background-only`·`isolated-desktop`·`foreground-exclusive`, `SessionInfo.InteractionPolicy`, 오류 `DedicatedMachineRequired`·`UnsupportedInteractionPolicy`를 삭제한다.
- 세션에 `bool Input`만 둔다(2.2). `Input`이 없는 세션에서 실제 입력 작업은 `InputSessionRequired`로 거부한다.
- 입력을 쓰지 않는 세션에 걸리던 전경 검사를 삭제한다: 대상이 전경이면 세션을 못 여는 것(`InteractionGuard.cs:82`의 `UserInputConflict`), 대상이 전경이 되거나 대상에 사람 입력이 가면 취소하는 것(`:98-101`), 디스패치 후 전경 검사(`GuiRequestHandler.cs:222`의 `InterferenceDetected`). 같은 PC에서 방금 띄운 앱은 전경이라 지금은 이 검사가 테스트를 막는다.
- `InteractionGuard`는 `Input` 세션의 lease와 사람 입력 감지만 담당한다. `backgroundEpochs`와 `ForegroundChangeObserved`를 삭제한다.
- `ControlSessionBusy`(`ControlSessions.cs:52`)는 `Input` 세션에만 적용한다.

**F. 대상 신원의 해시**

- `DesktopProcess.ImageHash`를 삭제한다. 신원은 (PID, 시작 시각, 경로)다. PID 재사용은 시작 시각으로 구분된다.
- `Processes.Identify`의 `File.ReadAllBytes(path)`(`Processes.cs:21`)를 삭제한다. 지금은 창을 확인할 때마다 EXE 전체를 읽어 해시하며, 요청 하나에서 여러 번 불린다. 큰 EXE에서는 요청마다 수백 ms가 든다. 이 삭제로 그 비용이 없어진다.
- `(pid) → DesktopProcess` 캐시를 두어 `Process.StartTime`과 경로 조회도 프로세스당 한 번만 한다. 캐시 항목은 PID와 시작 시각이 일치할 때만 쓴다.

**G. 권한 수준 검사**

- Agent를 관리자로 실행하면 거부하는 검사(`WindowsDesktop.cs:47`의 `!Elevated(p.Handle)`)와 관리자 권한 대상 창을 `AccessDenied`로 거부하는 검사(`:60`, `GraphicsCapture.cs:49`, `WindowsDesktop.cs:88`)를 삭제한다.
- Windows 자체 제약은 남는다. 일반 권한 Agent는 관리자 권한 창을 조작할 수 없다. 대상이 Agent보다 높은 권한이면 `TargetElevated`로 알려 준다. 세부는 6.4.

**H. 암호 입력칸**

- `set-value`의 `PasswordTargetDenied`(`UiaAutomation.cs:306`)를 삭제한다. 로그인 화면 테스트에 필요하다.
- 암호 칸의 `Name`을 `[password]`로 바꾸는 처리를 삭제한다. 값은 UIA 프로바이더가 내주지 않으므로 `value.available: false`, `reason: "password"`로 돌려준다.

**I. 저장소 보호**

- 저널의 DPAPI 암호화를 삭제한다: `JournalProtection.cs`, `RequestJournal.cs:22`(`protectedJournal` 분기), `:27`. 평문 JSON이 되어 문제를 조사할 때 바로 열어 볼 수 있다.
- `StateStore`의 소유자·ACL 검사와 재분석 지점 검사(`StateStore.cs:23-31`, `:36`)를 삭제한다. 폴더는 일반 `Directory.CreateDirectory`로 만든다.
- UIA 작업자의 부모 신원·토큰 확인(`UiaWorker.cs:19-37`, `:66-70`)을 삭제한다. 파이프 이름만 넘긴다.
- 파이프의 `PipeOptions.CurrentUserOnly`도 뺀다. Agent를 관리자로, 호출자를 일반 권한으로 실행하는 구성에서 걸림돌이 되지 않게 한다.

**J. 그대로 두는 것**

- 요청 크기 상한, 알 수 없는 필드·중복 키 거부, 인자 범위 검사: 잘못 쓴 요청을 일찍 알려 주는 용도다.
- Controller의 기본 수신 주소 `127.0.0.1`: 원격 Agent를 받을 때만 `--listen`으로 바꾼다.

#### 1.2 API 버전 구분을 없앤다 (P3)

**나뉘어 있던 이유.** API 1과 2는 신·구 버전이 아니라 서로 다른 기능 묶음이다. API 1은 실제 입력과 창·앱 관리, API 2는 UIA 관측·조작이다. UIA를 나중에 추가하면서 번호를 나눴고, 그 결과 요소 ID 체계가 둘이 되고 버전별 분기가 생겼다.

- 요청·응답·세션에서 `apiVersion`을 삭제한다. `SessionInfo.ApiVersion`, `--api-version`, `SessionApiVersionMismatch`, `supportedApiVersions`도 삭제한다.
- `GuiProtocol.Parse`의 인자 표 두 개(입력용 `:71-86`, `ValidateAutomation` `:126-168`)를 하나로 합친다.
- 버전 분기를 한 가지 동작으로 정한다.

| 위치 | 변경 |
|---|---|
| `GuiRequestHandler.cs:103-110` 응답 부가 필드 | 항상 넣는다. `driverOutcome`은 `status`와 같으므로 삭제한다. `interactionPolicy`·`foregroundChangeObserved`는 1.1-E로 삭제한다 |
| `:91` 실패 시 상태 판정 | 3.2의 규칙 하나 |
| `:161` 해제 후 상태 | 3.4의 규칙 하나 |
| `:269`, `:315` 범위 검사 | 항상 적용한다(6.1) |

- `DesktopClient`가 `ApiVersion: 2`를 넣는 곳을 지운다.
- `Directory.Build.props` 버전을 0.4.0, `GuiProtocol.WireVersion`을 3으로 올린다.

#### 1.3 `controls`를 삭제하고 요소 ID를 하나로 한다

- 삭제: 작업 `controls`, `WindowsDesktop.Controls`/`ReadControls`(`WindowsDesktop.cs:157-192`), `IGuiDesktop.Controls`, `WindowRegistry.Controls`/`Element`와 `elements` 사전, `InternalDesktopCommands`의 `--uia`, `DesktopElement`·`PublicElement`.
- `click`의 대상은 UIA 요소 참조(`elementId` + `snapshotToken`) 또는 `position`이다. `automationId` 인자는 없앤다. 요소 좌표는 7.2의 `bounds` 중심이다. 7.2 전까지는 `click`의 요소 지정을 막아 두지 말고, 단계 1에서 `bounds`만 먼저 추가한다.

#### 1.4 테스트 정리

API 1 전용 기대값, 인증서·프로필·정책 관련 테스트(`ProfileTests`, `SemanticProfileProbe`, `IntegrationTests`의 인증서 부분)를 삭제하거나 새 모델로 옮긴다.

---

### 단계 2. 저널과 저장소

#### 2.1 읽기 요청은 디스크에 쓰지 않는다 [확실]

**위치**: `GuiRequestHandler.cs:56`, `:57`, `:80`, `:114`, `EvidenceBudget.cs:5-20`, `ControllerHost.cs:119`

**현상**

- 모든 요청의 최종 응답이 `data` 전체와 함께 `request-*.json`으로 저장된다. `inspect` 한 번이 수백 KB다.
- `EvidenceBudget.Admit`은 완료 항목을 24시간 뒤에야 지우고, 합계가 64MiB 이상이면 **모든 변경 요청을 거부**한다. 전체 `inspect` 160~200회 정도면 닿는다.
- `Admit`은 변경 요청마다 저널 파일을 전부 열어 파싱한다. 항목이 수천 개면 변경 요청 하나에 초 단위가 걸린다.
- 읽기 요청 하나에 디스크 동기 쓰기가 Controller 2~3회, Agent 3~4회 든다.

**변경**

- 읽기 전용 작업은 Controller와 Agent 모두 저널 수락·단계 기록·최종 기록을 건너뛴다. 중복 검사와 `--request-status` 대상이 아니다.
- `capture`는 파일 전달 복원이 저널에 의존하므로 지금처럼 기록한다.
- 예산 합산과 보관 정책은 변경 요청과 `capture`에만 남는다.

#### 2.2 변경 요청의 저널 쓰기를 줄인다

- Agent: 수락 기록 1회(fsync, 디스패치 전에 내구성 확보) + 최종 기록 1회(fsync 없이 rename). `executing`·`dispatching` 단계 기록(`GuiRequestHandler.cs:80-81`)을 삭제한다. 복구 코드(`RequestJournal.Recover`)는 단계와 무관하게 `running`이면 미확정으로 처리하므로 잃는 정보가 없다. 최종 기록이 유실되면 미확정으로 보이는데 안전한 방향의 오차다.
- Controller: 수락 1회 + 최종 1회. Agent 응답 직후의 중간 저장(`ControllerHost.cs:182`)은 `capture`에만 남긴다.
- `EvidenceBudget.Admit`을 요청 경로에서 뺀다. 사용량은 메모리 누적 값으로 검사하고, 정리는 `AgentHost`의 maintenance 루프와 Controller의 타이머에서 60초마다 한다. 정리할 때 파일 내용을 열지 않도록 `journal-index.json`에 상태·시각·크기를 둔다.
- 변경 요청마다 `evidence-policy.json`을 읽는 것(`GuiRequestHandler.cs:56`)을 시작 시 1회로 바꾼다.
- `StateStore.Write`의 이름 있는 뮤텍스(`IoGuard`)는 Agent와 watchdog이 함께 쓰는 파일(`held-input.json`, `applications.json`, `launch-intent-*`)에만 쓰고 나머지는 프로세스 안의 `lock`으로 바꾼다.

#### 2.3 쌓이기만 하는 파일을 정리한다 [확실]

| 대상 | 위치 | 문제 | 변경 |
|---|---|---|---|
| Agent `session-*.json` | `ControlSessions.cs:82-89`, `AgentHost.cs:55` | 닫힌 세션도 영구 보관하고 `--status --machine` 응답에 전부 나온다 | 닫히거나 만료된 지 24시간 뒤 삭제 |
| Controller `session-*.json` | `ControllerHost.cs:30-34`, `:97` | 같은 문제 | 같은 규칙 |
| Controller `request-*.json` | `ControllerHost.cs:29` | 정리 코드가 없고 시작할 때마다 전부 읽는다 | Agent와 같은 보관 정책 |
| Agent `cap-*.png`·`cap-*.json` | `CaptureStore.cs`, `EvidenceBudget.cs:21-26` | 삭제 경로가 없어 256MiB에 닿으면 캡처가 거부된다 | P9 |

- 상태·세션 목록 응답은 기본으로 `closed`·`expired`를 뺀다. `--session-status --all`로 전부 본다.
- 캡처(P9): Controller가 파일을 받아 해시를 확인하면 `capture-ack`(captureId)를 보낸다. Agent는 PNG를 지우고 `cap-*.json`은 좌표 유효 시간(30초) 동안 둔다. 전달되지 않은 사본도 24시간 뒤 지운다. `completionUnknown`인 `capture` 요청에 연결된 사본은 그 요청이 확정될 때까지 지우지 않는다.

#### 2.4 생존 신호를 파일로 쓰지 않는다 [확실]

**위치**: `AgentHost.cs:121-123`, `WatchdogRunner.cs:18-20`

**현상**: heartbeat를 50ms마다 임시 파일 생성 + fsync + rename으로 쓴다. Agent가 떠 있는 동안 초당 20회 계속된다. `agent-stop.json`도 50ms마다 폴링한다.

**변경**: watchdog은 Agent 프로세스 핸들 대기로 종료를 감지한다. 멈춤은 Agent가 주기적으로 신호하는 이름 있는 이벤트로 감지한다. 정지 요청도 이름 있는 이벤트로 바꾼다.

#### 2.5 부팅 식별자 [확실]

**위치**: `StateStore.cs:98`

레지스트리의 `BootId`가 없으면 `Deadline`의 정적 초기화가 실패해 모든 요청이 원인 불명 오류가 된다. 값이 없으면 `Environment.TickCount64`로 계산한 부팅 시각(초 단위)을 대신 쓴다.

---

### 단계 3. 요청 처리의 결과 분류와 세션 상태

#### 3.1 대기가 시간 초과되면 세션이 차단된다 [재현 필요]

**위치**: `ControllerHost.cs:172-201`(`Forward`), `GuiRequestHandler.cs:57`, `:161`

**추론**

- Controller는 수락 시각에 마감을 잡고 Agent에 남은 시간을 보낸다. Agent는 그 값을 받은 **뒤에** 자기 마감을 새로 시작한다. 전송 시간과 Agent의 사전 처리만큼 Agent 마감이 항상 늦다.
- 따라서 제한 시간을 끝까지 쓰는 요청은 Controller의 마감이 먼저 터진다. `Forward`의 catch는 읽기 요청이어도 `completionUnknown`을 돌려주고 Agent에 `cancel`을 보낸다.
- Agent의 `cancel`은 `Release(session, false, "RequestTimeout")`이고 해제 뒤 세션이 `blocked`가 된다.
- 문서는 "더 긴 대기는 새 읽기 요청으로 이어 간다"고 안내하므로 정상 사용에서 반복해서 일어난다.

기존 테스트(`AutomationFlowTests.cs:35-36`)는 핸들러를 직접 호출하므로 이 경로를 검증하지 않는다. 0.2의 테스트로 먼저 확인한다.

**변경**

1. Agent에 넘기는 시간을 Controller 마감보다 150ms 짧게 한다(최소 1).
2. `Forward`의 catch에서 읽기 전용 작업은 `rejected` + `RequestTimeout`으로 돌려주고 `cancel`을 보내지 않으며 세션 상태를 건드리지 않는다.
3. 대기 작업의 시간 초과는 8.3대로 관측값을 담아 돌려준다.

#### 3.2 디스패치 전 거부가 `completionUnknown`이 된다 [확실]

**위치**: `GuiRequestHandler.cs:216`(preflight 끝의 `effect()`), `:91`, `UiaAutomation.cs:287-316`, `UiaWorker.cs:87`

**현상**: 핸들러는 preflight가 통과하면 `effect()`로 "효과 발생 가능"을 표시한 뒤 작업자에 명령을 보낸다. 작업자는 네이티브 패턴 호출 전에 `ValueReadOnly`, `TriStateNotSupported`, `ElementDisabled`, `WindowChanged`, `PatternNotSupported`, `StaleElement`로 거부할 수 있다. 이 거부가 `completionUnknown`이 되어 세션을 차단한다. 예: 읽기 전용 입력칸에 `set-value`. 아무 일도 없었는데 미확정으로 보고된다.

**변경**

1. 작업자에 `dispatchStarted` 플래그를 둔다. `NativeAutomation.WithPattern`이 `action(pattern)`을 부르기 직전과 `Realize()`·`ScrollIntoView()` 직전에만 `true`로 한다.
2. 작업자의 거부 응답에 이 값을 싣고, `UiaWorker.Call`은 `dispatchStarted == false`인 거부를 `GuiException.NoEffect = true`로 던진다.
3. 핸들러 catch는 `NoEffect`이면 `rejected`로 응답한다.
4. `completionUnknown`은 "디스패치를 시작했고 끝났는지 확인하지 못함"에만 쓴다.

#### 3.3 그 밖의 결과 분류 [확실]

| 위치 | 현상 | 변경 |
|---|---|---|
| `GuiRequestHandler.cs:81-82` | `Perform`이 정상 반환한 뒤 마감을 다시 검사해, 끝난 변경이 `completionUnknown`으로, 끝난 읽기가 `RequestTimeout`으로 바뀔 수 있다 | 예외 없이 반환했으면 결과를 쓴다 |
| `:89` | 읽기 요청이 실패해도 `input.Release`를 불러 누르고 있던 키가 풀린다 | 읽기 실패에서는 `input.Empty`로 상태만 보고한다 |

#### 3.4 차단·재개 절차를 줄인다

| 상황 | 현재 | 변경 |
|---|---|---|
| 읽기 요청 시간 초과 | 세션 차단 | 세션을 건드리지 않는다(3.1) |
| 디스패치 전 거부 | `completionUnknown` + 차단 | `rejected`(3.2) |
| `release-input` 뒤 | 항상 `blocked`(`GuiRequestHandler.cs:161`) | 입력이 모두 해제됐고 미확정 요청이 없으면 `active` |
| 변경 요청 시간 초과 | `blocked` | 디스패치가 시작되지 않았으면 `active`, 시작됐으면 `completionUnknown` + 차단 |
| 미확정 뒤 재개 | reconcile 후 별도 resume | `--session-reconcile … --resume`으로 한 번에. 응답에 새 revision |
| lease 만료 | 차단 후 resume | 9.5의 자동 연장 |

#### 3.5 차단된 세션도 유휴 만료된다 [확실]

**위치**: `AgentHost.cs:144`

유휴 만료 검사가 `active`에만 적용되어, 차단된 `--input` 세션이 닫힐 때까지 새 `--input` 세션을 막는다. 조건을 `active` 또는 `blocked`로 넓힌다. 미확정 요청 기록은 만료 뒤에도 저널에 남긴다.

#### 3.6 파서 검증 [확실]

**위치**: `GuiProtocol.cs:163-167`, `GuiRequestHandler.cs:225`

`value-equals`·`text-contains`·`toggle-equals`에 `expected`가 없으면 빈 문자열로 비교한다. `text-contains`는 항상 참이 된다. 다음을 강제한다.

- 위 세 조건은 `expected`가 문자열이어야 한다. `text-contains`는 길이 1 이상.
- `toggle-equals`의 `expected`는 `on`·`off`·`indeterminate`.
- `exists`·`absent`·`enabled`에 `expected`가 있으면 거부한다. `null`은 허용한다.

---

### 단계 4. 전송과 연결

#### 4.1 응답이 프레임 한도를 넘으면 연결이 끊긴다 [확실]

**위치**: `GuiWire.cs:11`, `:23-24`, `:53-60`, `AgentHost.cs:183`

**현상**: `GuiWire.Write`는 2MiB를 넘으면 한 바이트도 쓰기 전에 예외를 던진다. `RpcPeer.Send`의 catch는 원인을 가리지 않고 연결을 버린다. Agent는 연결이 끊기면 모든 세션을 닫고 세션이 띄운 앱을 종료한다. 노드 `Name`에 길이 제한이 없어 글이 많은 창에서 일어날 수 있다. 작업자 파이프도 같은 코드를 쓴다.

**변경**

1. `RpcPeer.Send`에서 직렬화와 크기 검사를 세마포어 진입 전에 한다. 크기 초과는 연결을 유지한 채 던진다.
2. 응답 전송이 크기 초과로 실패하면 `ResponseTooLarge` 거부를 대신 보낸다. 작업자도 같다.
3. `Name`을 1,024자로 자르고 `nameTruncated`를 표시한다. 스냅숏 토큰은 자르기 전 이름으로 계산한다.
4. 프레임 한도를 8MiB로 올린다.

#### 4.2 반쯤 끊긴 연결이 재접속을 막는다 [재현 필요]

**위치**: `ControllerHost.cs:248`

Controller는 heartbeat에 응답만 하고 끊겼는지 감시하지 않는다. FIN/RST 없이 끊기면 옛 항목이 남아 재접속이 `DuplicateMachineConnection`으로 거부되고 `--status`는 `connected: true`를 보여 준다.

- heartbeat가 3초간 없으면 그 peer를 버린다.
- 같은 `machineId`로 새 연결이 오면 옛 연결을 닫고 교체한다.
- 소켓에 TCP keepalive와 `NoDelay`를 켠다.

#### 4.3 JSON 인코더

`GuiProtocol.Json`(`GuiProtocol.cs:26-31`)에 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`을 지정한다. 지금은 한글이 `\uXXXX`로 나가 바이트와 토큰이 몇 배로 든다. `Fingerprint`는 파싱된 값을 정규화하므로 영향이 없다.

---

### 단계 5. 실행 속도

#### 5.1 변경 요청의 작업자 왕복을 한 번으로

**위치**: `GuiRequestHandler.cs:207-221`, `UiaAutomation.cs:26-31`, `:74`, `:103-120`

지금은 핸들러가 `capabilities`로 preflight를 한 뒤 같은 요소에 실제 명령을 다시 보낸다. 작업자는 이를 위해 `preparedAction`을 1.5초간 들고 있다.

- 작업자 명령 하나에 "재검증 → enabled·패턴·읽기 전용 확인 → `expectedValueHash` 비교 → 디스패치"를 넣는다. `UiaCommand`에 `ExpectedValueHash`를 추가한다.
- 핸들러 쪽 디스패치 직전 검사(세션 상태, `Input` 세션의 lease)는 명령을 보내기 직전에 한 번 한다.
- `preparedAction`과 관련 분기를 삭제한다.

#### 5.2 문자 입력을 한 번에 보낸다

**위치**: `GuiInput.cs:19-29`, `GuiRequestHandler.cs:350`, `WindowsInput.cs`

지금 `type-text`는 글자마다 `held-input.json`을 두 번 동기 기록하고 `SendInput`을 두 번 부른다. 100자면 디스크 쓰기 200회다.

- 유니코드 문자는 누름 상태가 남지 않으므로 원장에 기록하지 않는다. 문자열 전체의 누름·뗌 쌍을 `INPUT` 배열로 만들어 `SendInput`을 한 번 호출한다. 500개 이벤트 단위로 나누고 사이에 세션 취소를 확인한다.
- 원장은 눌린 채 유지될 수 있는 것(`key down`, `chord`의 키, 마우스 버튼)에만 쓴다. `chord`와 `click`은 누름 전 1회, 전부 뗀 뒤 1회로 줄인다.

#### 5.3 도우미 프로세스를 호출마다 띄우지 않는다

**위치**: `WindowsDesktop.cs:77`(`--focus`), `:141`(`--capture`)

- 상주 캡처 작업자를 두고 파이프로 요청한다. D3D 장치를 재사용한다. 2.2초 제한과 강제 종료는 유지한다.
- `focus`도 같은 작업자가 처리한다.
- 세션을 열 때 UIA 작업자를 미리 띄운다.

#### 5.4 트리 순회 [확실]

**위치**: `UiaAutomation.cs:198-226`

- 스냅숏 토큰을 만들 때 부모 토큰을 `nodes.FirstOrDefault`로 선형 탐색한다(`:213`). 사전으로 바꾼다.
- `Name`, `Value`, `ToggleState`를 노드마다 프로세스 간 호출로 읽는다. `CacheRequest`에 `NameProperty`, `ValuePattern.ValueProperty`, `TogglePattern.ToggleStateProperty`와 7.2의 속성을 넣어 한 번에 가져온다.

#### 5.5 직렬화 왕복

응답 `data`를 사전으로 바꾸려고 직렬화 → 역직렬화하는 것(`GuiRequestHandler.cs:102`)과 재파싱을 위한 인코딩 → 파싱(`:32`)을 `JsonNode`로 직접 다룬다.

#### 5.6 메모리 상한 [확실]

`observed`·`observedAliases`(`GuiRequestHandler.cs:21-22`)가 세션이 닫힐 때까지 무제한으로 늘어난다. 세션당 8,192개로 제한하고 오래된 것을 버린다. 버려진 참조는 `StaleElement`로 응답한다.

#### 5.7 CLI 기동

- `Controller.exe`와 `Agent.exe`를 `PublishReadyToRun=true`로 배포한다.
- `Controller.exe`의 CLI 경로는 UIA·WPF가 필요 없는데 `Core`가 `UseWPF=true`라 함께 로드된다. 통신·저널·설정을 WPF 의존 없는 프로젝트로 분리하는 것은 0.1의 측정에서 기동 시간이 문제로 확인될 때만 한다.

---

### 단계 6. 대상 범위와 창

P1의 대상(Windows 자체 GUI)은 일반 앱과 구조가 다르다. 아래는 코드에서 읽은 제약과 Windows 구조에 대한 지식으로 쓴 것이며 실제 제어판·설정 앱으로 확인하지 않았다. **각 항목은 구현 전에 대표 대상으로 동작을 먼저 확인한다.**

#### 6.1 범위 지정

- 범위는 2.2의 옵션으로 정한다. `scope.json`과 `--target-scope`를 삭제한다.
- 창 조건(`--target-window-*`)에 맞는 최상위 창과 그 소유 창을 범위에 넣는다.
- 범위 안 프로세스의 자식 프로세스 창도 범위에 넣는다(Job 구성원 또는 부모 PID로 판정).

#### 6.2 `open` 작업

**문제**: 제어판은 `control.exe`가 `explorer.exe`에 넘기고 끝난다. 설정 앱은 `ms-settings:` URI로 열리고 창 틀은 `ApplicationFrameHost.exe`, 내용은 `SystemSettings.exe`가 가진다. `.msc`, `.cpl`, 파일 탐색기 창도 비슷하다. 현재 `launch`는 `.exe` 절대 경로만 받고(`GuiApplications.cs:26`), 범위는 "띄운 프로세스와 그 Job"뿐이다.

**구현**

- `open`: `ShellExecuteEx`로 URI, 제어판 항목(`control.exe /name Microsoft.Sound`), `.msc`, `.cpl`, 문서 파일을 연다. 인자는 `target`, `arguments`, `verb`(`open`·`runas`), `waitWindow`.
- 실행 전의 최상위 창 목록을 기억하고, 새로 나타난 창 중 `waitWindow` 조건에 맞는 것을 돌려주며 세션 범위에 넣는다.
- 정리: `open`으로 연 것은 Job에 넣을 수 없다. 세션을 닫을 때 그 세션이 연 **창**에 닫기를 요청하고(UIA `WindowPattern.Close`, 실패 시 `WM_CLOSE`) `closedWindowIds`·`retainedWindowIds`로 보고한다. `explorer.exe` 같은 공유 프로세스는 종료하지 않는다. `launch`는 지금처럼 Job으로 정리한다.
- 창 정보에 `processName`을 추가한다.

#### 6.3 제목 없는 팝업 창 [확실]

**위치**: `WindowsDesktop.cs:72`(`w.Title.Length > 0`)

창 열거가 제목이 빈 창을 버린다. 팝업 메뉴와 컨텍스트 메뉴(클래스 `#32768`), 콤보 상자 드롭다운(`ComboLBox`), WPF·WinUI의 Popup·플라이아웃, 자동 완성 목록은 제목 없는 별도 최상위 창이고 주 창의 UIA 트리에도 없다. 메뉴를 열어도 항목을 찾을 수 없다.

- 범위 안이면 제목이 없어도 열거한다. 크기 0, 숨김, 클로킹 창은 뺀다.
- 창 정보에 `kind`를 추가한다: `main`, `owned`, `popup`. `windows`는 기본으로 `main`·`owned`만 주고 `includePopups: true`일 때 팝업을 포함한다.
- `find-window`·`wait-window`에 `kind` 조건을 추가한다.
- 9.6의 `observeAfter`에 `includeNewWindows`를 둔다.

#### 6.4 관리자 권한 도구

`regedit`, 서비스, 장치 관리자, 여러 `.msc`는 관리자 권한으로 뜬다.

- `open`·`launch`에 `verb: "runas"`를 허용한다. Agent가 관리자이면 UAC 창 없이 뜬다. Agent가 일반 권한이면 UAC 보안 데스크톱이 떠서 누를 수 없으므로 `ElevationRequired`로 미리 거부한다.
- 대상이 Agent보다 높은 권한이면 `TargetElevated`로 응답한다.
- `manage.ps1`의 로그온 작업 등록에 "가장 높은 권한으로 실행" 옵션을 추가한다.

#### 6.5 `launch`와 세션 열기

- `launch`·`open`에 `waitWindow`(`{ timeoutMilliseconds, title?, titleContains?, className? }`)를 추가한다. 첫 창이 나타날 때까지 기다려 `window`를 함께 돌려준다. 이 경우 제한 시간 상한은 30초다.
- `--session-open --launch EXE`는 세션 열기, 실행, 첫 창 대기를 한 번에 한다.

#### 6.6 창 찾기

`WindowQuery`(`WindowQuery.cs`)에 `titleContains`, `titleRegex`를 추가한다. 제목에 파일 이름이나 `*`가 붙는 창을 찾는다.

---

### 단계 7. 관측 형식

#### 7.1 compact 형식과 짧은 참조

**현재**: 노드마다 `element-` + 32자, 64자 토큰, 빈 필드, 배열과 객체를 모두 내보낸다. 노드 하나가 대략 150토큰, 전체 트리 한 번이 15만 토큰 규모다(추정). `capabilities`는 호출마다 작업 디스크립터 15개를 붙인다(`GuiRequestHandler.cs:254`).

- 참조를 세션 단위 카운터(`e1`, …)로 바꾼다. 토큰은 SHA-256 앞 12자를 내보내고, 내부 비교는 전체 해시로 하되 요청의 토큰은 접두사 일치로 검증한다.
- `inspect` 인자:

| 인자 | 의미 |
|---|---|
| `format` | `"compact"`(기본) 또는 `"json"` |
| `maxDepth` | 1~16 |
| `rootElementId` + `rootSnapshotToken` | 서브트리만 |
| `interactiveOnly` | 패턴이 있거나 이름이 있는 노드만. 구조에 필요한 조상은 남긴다 |
| `includeOffscreen` | 기본 `true` |
| `includeBounds` | 좌표를 붙인다 |
| `treeView` | `control`(기본), `raw`, `content` |

- compact 줄의 형식은 2.5와 같다. 붙는 속성: `disabled`, `offscreen`, `value=`, `toggle=`, `selected`, `expanded=`, `focused`, `patterns=`, `box=x,y,w,h`. 값은 200자로 자르고 `…(+N)`을 붙인다.
- `operationDescriptors`는 `capabilities`에 `includeDescriptors: true`일 때만 붙인다. `find-element`는 기본으로 `target`만 돌려주고 `includePath: true`일 때 조상 경로를 붙인다.

#### 7.2 노드에 좌표와 상태를 추가한다

`ObservationNode`에 선택 필드를 추가한다(`init` 속성). 모두 `CacheRequest`에 넣어 추가 왕복 없이 읽는다. 스냅숏 토큰 계산에는 넣지 않는다.

| 필드 | 출처 |
|---|---|
| `bounds` | `BoundingRectangle`. 창 클라이언트 기준 물리 픽셀. 비어 있으면 `null` |
| `selected` | `SelectionItemPattern.IsSelected` |
| `expandState` | `ExpandCollapsePattern`. `collapsed`·`expanded`·`partially`·`leaf` |
| `focused` | `HasKeyboardFocus` |
| `rangeValue` | `RangeValuePattern`의 값·최소·최대 |
| `helpText`, `itemStatus` | 비어 있으면 생략 |

`Patterns` 목록(`UiaAutomation.cs:65-70`)에 `Window`, `Text`, `LegacyIAccessible`을 추가한다.

#### 7.3 `raw`·`content` 뷰의 요소를 조작에 쓸 수 있게 한다 [확실]

**위치**: `UiaAutomation.cs:33`(`BoundKey`에 `TreeView` 포함), `GuiProtocol.cs:128-141`

`inspect`에 `treeView: "raw"`를 주면 참조가 `raw` 키로 저장되는데, 변경 작업은 항상 `control`로 찾는다. 키가 달라 캐시를 못 쓰고 부모가 뷰마다 달라 토큰이 맞지 않아 `StaleElement`가 난다. 핸들러의 `observed` 항목에 관측 당시 `treeView`를 저장하고 참조를 쓰는 요청에 그 값을 넘긴다. `find-element`·`wait-condition`에도 `treeView`를 허용한다.

---

### 단계 8. 찾기와 대기

#### 8.1 가상화 컨테이너 (P8)

**현재**: 창이나 검색 루트 안에 `ItemContainer`·`VirtualizedItem` 패턴을 가진 요소가 하나라도 있으면 `find-element`가 `SearchIncomplete`로 거부된다(`UiaAutomation.cs:133`, `:229`, `NativeAutomation.cs:53`, `ElementQuery.cs:39`). WPF의 `ItemsControl` 계열과 WinUI의 목록이 이 패턴을 노출하는 것으로 알고 있다. 그렇다면 평범한 WPF 창과 설정 앱 대부분에서 창 단위 검색이 막힌다. **WPF 픽스처(ComboBox + 버튼)로 먼저 확인한다.**

**변경**

- 다음을 모두 만족하면 성공한다: 선택자에 `automationId`가 있다, 일치한 요소가 하나다, 그 요소의 조상 중 가상화 컨테이너가 없다. 응답에 `uniquenessScope: "outside-virtualized"`를 붙인다.
- 그 밖에는 지금처럼 `SearchIncomplete`다. `completeness: "realized"`를 명시하면 실체화된 범위에서 찾고 `uniquenessScope: "realized-only"`와 `virtualizedContainers`(컨테이너 참조 목록)를 붙인다.
- `absent` 조건은 기본에서 완화하지 않는다. `realized`를 명시했을 때만 "실체화된 범위에 없음"으로 판정한다.
- 조상 검사는 일치 요소에서 창 루트까지 올라가며 패턴 가용 속성을 읽는다.
- `VirtualUiTests`의 기대값을 새 규칙에 맞춘다.

#### 8.2 선택자

- `ElementSelector`에 `nameContains`, `nameStartsWith`, `nameRegex`(.NET 정규식, `RegexOptions.NonBacktracking`, 256자 이하), `automationIdRegex`를 추가한다. `name`과 동시 지정은 거부한다.
- `index`(0부터)를 추가한다. 지정하면 일치 항목을 문서 순서로 정렬해 그 번호를 택한다. 지정하지 않으면 지금처럼 모호함을 거부한다. 응답에 `matchCount`를 넣는다.
- `AmbiguousTarget` 오류의 `data.candidates`에 후보 최대 5개(참조, `automationId`, `name`, `controlType`, 부모 이름)와 `matchCount`를 싣는다. `GuiException`에 `object? Data`를 추가하고 핸들러 catch에서 응답 `data`로 옮긴다.
- `ElementNotFound`에 `data.nearest`로 `automationId`나 `name`이 비슷한 후보 3개를 붙인다.
- `find-element`의 `selectors`(배열, 최대 16개): 여러 요소를 한 번의 순회로 찾아 각각의 `target` 또는 오류를 돌려준다.
- `capabilities`의 `elements`(참조 배열, 최대 16개): 여러 요소의 현재 값을 한 번에 읽는다.

#### 8.3 대기

**현재 조건**: `exists`, `absent`, `enabled`, `value-equals`, `text-contains`, `toggle-equals`. 텍스트 비교는 `Value`만 본다. WinForms `Label`·WPF `TextBlock`의 글자는 `Name`에 있어 "상태 표시줄에 '저장 완료'가 포함되면"을 표현할 수 없다.

- 조건 추가: `disabled`, `name-equals`, `name-contains`, `value-not-equals`, `selected-equals`, `expanded-equals`, `onscreen`, `offscreen`.
- `anyOf`(최대 4개의 `{selector, condition, expected}`): 먼저 충족된 조건의 번호를 `matchedIndex`로 돌려준다. "성공 표시 또는 오류 대화상자"를 한 번에 기다린다.
- 시간 초과는 오류가 아니라 `completed` + `data.satisfied: false`로 돌려준다. 마지막 폴링의 `target`, `polls`, `lastError`를 담는다.
- 폴링 중 `StaleElement`, 프로바이더 읽기 실패로 인한 `SearchIncomplete`는 다음 폴링으로 넘긴다. `AmbiguousTarget`과 가상화로 인한 `SearchIncomplete`는 즉시 실패한다.
- 상한(P6): `wait-condition`·`wait-window`는 1~30,000ms, 기본 5,000. 상태 응답에 `maxWaitMilliseconds`를 추가한다. `ControllerHost.Call`의 클라이언트 마감(`ControllerHost.cs:310`, 현재 10초)은 요청 제한 시간 + 5초로 계산한다.
- `pollMilliseconds` 기본을 100, 최솟값을 25로 낮춘다.

---

### 단계 9. 조작

#### 9.1 3상태 체크박스 [확실]

**위치**: `NativeAutomation.cs:71-78`

`Toggle()`을 한 번만 호출한다. on → indeterminate → off 순서로 도는 컨트롤은 `off` 요청 시 `indeterminate`에서 멈추고 `PostconditionFailed`가 난다. 원하는 상태가 될 때까지 최대 3회 호출한다. `TriStateNotSupported`를 없애고 `desiredState`에 `indeterminate`를 추가한다.

#### 9.2 새 UIA 작업

| 작업 | 내용 |
|---|---|
| `set-range-value` | `RangeValuePattern.SetValue`. 슬라이더·스핀 |
| `window-state` | `WindowPattern`으로 최소화·최대화·복원·닫기 |
| `set-focus` | UIA `SetFocus` |
| `legacy-default-action` | `LegacyIAccessiblePattern.DoDefaultAction` |
| `read-grid` | `GridPattern`의 행·열 수와 지정 범위 셀의 이름·값. 셀 최대 500개 |
| `read-text` | `TextPattern` 전체 또는 선택 영역 |
| `generate-selector` | 10.3 |
| `clipboard-get`, `clipboard-set` | 텍스트만 |

새 작업은 `AutomationOperations.All`에 디스크립터를, `GuiProtocol`에 인자 검증을 추가한다.

#### 9.3 클래식 Win32 컨트롤 (P1 대상에 필요)

제어판 애플릿과 오래된 도구는 UIA 프록시로 노출되는 Win32 공용 컨트롤을 쓴다.

- `Value` 패턴이 없으면 `LegacyIAccessible` 값으로 대체해 읽고 `valueSource: "legacy"`를 표시한다.
- 아래가 프록시에서 동작하는지 실제 대상으로 확인하고, 안 되면 `legacy-default-action`으로 대체한다.

| 컨트롤 | 확인할 작업 |
|---|---|
| `SysListView32` 상세 보기 | `read-grid`, `select-item` |
| `SysTreeView32` | `set-expanded`, `select-item` |
| 탭 컨트롤, 속성 시트(`#32770`) | `select-item`으로 탭 전환 |
| 메뉴 막대 | `set-expanded`로 연 뒤 6.3의 팝업 창에서 `invoke` |

#### 9.4 실제 입력

| 항목 | 내용 |
|---|---|
| `click`의 `count` | 1~3. 지금은 더블클릭이 요청 두 번이라 간격을 맞추기 어렵다 |
| `click`의 `modifiers` | `["CTRL","SHIFT","ALT"]` |
| 키 이름 | `INSERT`, `APPS`, `PRINTSCREEN`, `NUMPAD0~9`, 구두점 키(`-`, `=`, `,`, `.`, `/`, `;`, `'`, `[`, `]`, `\`, `` ` ``). 지금은 `Ctrl`+`+` 같은 조합을 보낼 수 없다(`GuiProtocol.cs:190-195`) |
| 가림 검사 | 클릭 전에 대상 좌표의 `ElementFromPoint`가 대상이나 그 자손인지 확인한다. 다르면 `TargetObscured` + `data.blockedBy`(가리는 요소의 이름·종류·창 제목) |
| 안정성 검사 | 좌표를 50ms 간격으로 두 번 읽어 다르면 최대 500ms 기다린다 |

#### 9.5 입력 lease 자동 연장 (P7)

**위치**: `InteractionGuard.cs:88-90`, `AgentHost.cs:139-143`

지금은 lease가 세션을 열거나 재개할 때만 30초로 잡히고, 30초가 지나면 maintenance 루프가 세션을 차단한다.

- 변경 요청이 성공했고 그 사이 사람의 입력이 없었으면 만료 시각을 `지금 + 30초`로 민다.
- 상한은 세션을 열거나 재개한 시점부터 900초다(상수). 닿으면 `InputLeaseRevoked`로 차단하고 resume으로 새 lease를 받는다.
- 세션 정보에 `leaseExpiresAt`과 `leaseHardLimitAt`을 둔다.
- 사람의 입력을 감지하면 즉시 취소하는 규칙은 그대로다.

#### 9.6 조작 응답에 결과를 붙인다

변경 작업에 선택 인자 두 개를 추가한다. 조작은 요청당 하나이므로 P10과 충돌하지 않는다.

- `expect`: 8.3의 조건 하나 + `timeoutMilliseconds`. 디스패치 후 조건을 기다려 `data.expect.satisfied`와 마지막 관측값을 돌려준다.
- `observeAfter`: `{ rootElementId?, rootSnapshotToken?, settleMilliseconds(0~1000), maxDepth?, includeNewWindows? }`. 디스패치 후 지정한 서브트리를 compact로 읽어 `data.after.tree`에, 새로 나타난 범위 안 창(팝업 포함)을 `data.after.newWindows`에 넣는다.
- 두 인자의 실패는 조작 결과를 바꾸지 않는다. `data.afterError`에 코드만 남긴다.

#### 9.7 실패 시 문맥

`ElementNotFound`·`StaleElement`·`ElementDisabled`·`TargetObscured` 응답의 `data.context`에 창의 최상위 2단계 compact 트리와 범위 안 창 목록(제목·클래스·종류)을 붙인다. `errorContext: false`로 끈다.

#### 9.8 캡처

`capture`에 `region`(창 내부 좌표), `elementId`(그 요소의 영역만), `scale`(0.25~1.0)을 추가한다. 이미지 토큰을 줄인다.

---

### 단계 10. 도구와 SDK

#### 10.1 MCP 서버

`Controller.exe --mcp`: stdio MCP 서버. 요청 파일을 쓰고 프로세스를 띄우는 비용 없이 도구로 호출한다. 도구는 묶어서 적게 둔다: `gui_session`, `gui_window`, `gui_observe`, `gui_act`, `gui_wait`, `gui_capture`. `agent-usage.md`의 핵심 규칙을 도구 설명에 요약한다. 함께 배포할 Claude Code 스킬 파일도 만든다.

#### 10.2 실제 진단을 하는 `--doctor`

지금 `--doctor --machine X`는 `--status --machine X`와 같다(`Controller/Program.cs:55`). Agent에 `doctor` RPC를 추가해 다음을 실제로 점검한다.

- UIA 작업자 기동과 왕복
- `GraphicsCaptureSession.IsSupported()`
- 입력 감시 훅 상태
- 저널·캡처 사용량과 한도
- Agent의 권한 수준과 UAC 설정
- 차단되었거나 오래된 세션과 권장 조치
- 모니터 배치와 DPI
- Controller와 Agent의 빌드·wire 버전

#### 10.3 선택자 생성

`generate-selector`: 요소 참조를 받아 그 창에서 유일한 가장 짧은 선택자와 대안 목록을 돌려준다. 우선순위는 `automationId` → `controlType` + `name` → 부모 선택자 조합이다. 각 후보가 정확히 하나만 일치하는지 실제로 확인한다. 참조(`e12`)는 세션이 끝나면 사라지므로 반복 실행할 테스트 코드에는 이 선택자를 저장한다.

#### 10.4 C# Client

**모델 없이 반복 실행하는 테스트**가 computer use에 대한 가장 큰 이점이다. 한 번 탐색한 흐름을 C# 코드로 옮겨 이후에는 LLM 호출 없이 돌린다.

- `DesktopClient`에 없는 것을 추가한다: 창 찾기·대기, `inspect`, `launch`, `open`, `capture`, 선택·토글·확장·스크롤, 실제 입력, 세션 재개.
- 응답을 형식 있는 결과로 바꾸는 도우미(`response.Target()`, `response.Tree()`)를 추가한다. 지금은 `Data`가 `object?`다.
- `WaitAsync`에 폴링 간격, 검색 루트, `anyOf`를 추가한다.
- `examples/standalone`에 "선택자로 찾기 → 값 설정 → 조건 대기 → 검증"의 완결된 예제를 둔다.

#### 10.5 추적 기록

`--trace-export CONTROL_ID OUTPUT_DIR`: 세션의 변경 요청·응답을 시간순 `trace.jsonl`로 쓰고 캡처 경로를 연결한다. 요청의 `note`가 여기에 사람이 읽을 설명으로 남는다.

#### 10.6 CLI

`--help`에 빠진 옵션(`Controller/Program.cs:11`)을 2장 기준으로 다시 쓴다.

---

### 단계 11. 문서, 스키마, 검증용 대상

- `README.md`, `README_kr.md`, `docs/agent-usage.md`, `docs/gui-test-guide.md`를 2장 기준으로 다시 쓴다. "API 1·2" 구분, 프로필, 인증서, 정책 표, 캡처 권한을 삭제한다. 맨 앞에 "신뢰된 네트워크와 PC에서만 사용한다. 연결은 암호화하지 않는다"를 한 줄로 적는다.
- `schemas/`를 `--export-schemas`로 다시 만든다.
- 실제 UI 테스트 대상을 추가한다. Windows 버전에 따라 화면이 다르므로 통과 기준은 "지정한 요소를 찾고 값을 읽는다"로 좁게 잡는다.

| 대상 | 여는 방법 | 확인할 것 |
|---|---|---|
| 설정 앱 | `open ms-settings:about` | 6.2, 8.1 |
| 제어판 | `open control.exe` | 6.2, 9.3 |
| 메모장의 메뉴 | `launch notepad.exe` | 6.3 |
| 서비스 콘솔 | `open services.msc`, Agent 관리자 실행 | 6.4, 9.3 |
| WPF 픽스처(ComboBox + 버튼, 새로 추가) | `launch` | 8.1, 6.3 |

- 0.1의 벤치마크와 0.3의 computer use 비교를 다시 재서 `README`의 수치를 갱신한다.

## 4. 하지 않는 것

| 항목 | 이유 |
|---|---|
| 묶음 요청(한 요청에 여러 조작 단계) | P10. 왕복 절감은 `expect`·`observeAfter`·`selectors`·30초 대기로 얻는다 |
| OCR·비전 대체 경로 | P1. 게임 화면이 대상이 아니다 |
| UIA 이벤트 기반 대기 | 이벤트 핸들러 제거 시 교착 위험이 알려져 있다. 8.3이 끝난 뒤 폴링으로 놓치는 사례가 실제로 나오면 다시 본다 |
| 화면 녹화 | ffmpeg 의존 |
| WebDriver(Appium) 호환 | 이 도구의 호출자는 코딩 에이전트와 C# 테스트다 |
| 과거 버전 호환 | P3 |

## 5. 참고한 오픈소스

| 출처 | 가져온 것 | 반영 |
|---|---|---|
| Playwright MCP | 짧은 ref가 붙은 들여쓴 스냅숏, 깊이·서브트리 제한, ref → 선택자 생성, 조작마다 설명 인자 | 7.1, 10.3, `note` |
| Playwright | 조작 전 가능 여부 검사(가림·안정성), 여러 개 일치 시 오류와 명시적 `nth`, 실패 시 스냅숏 첨부, trace | 9.4, 8.2, 9.7, 10.5 |
| FlaUI | 재시도 결과(횟수·마지막 값), CacheRequest 활용 | 8.3, 5.4 |
| pywinauto | `wait`/`wait_not`의 상태 어휘, 컨트롤 식별자 출력 | 8.3, 10.3 |
| Power Automate Desktop | 선택자 연산자(contains·정규식) | 8.2 |
| Terminator | 조작 후 트리 반환, compact 기본 형식 | 9.6, 7.1 |
| Appium / NovaWindows | 클릭 횟수·수정 키, 클립보드 | 9.4, 9.2 |
| Windows-MCP | 영역·축소 스크린숏 | 9.8 |

WinAppDriver는 유지보수가 중단됐고 Appium Windows Driver도 README에서 NovaWindows로 옮기라고 안내한다.

## 6. 확인하지 못한 것

- 3.1, 4.2는 타이밍에 달려 있어 재현 테스트가 먼저다.
- 8.1의 전제(WPF `ItemsControl` 계열과 WinUI 목록이 `ItemContainer` 패턴을 노출한다)는 기억에 의존했다.
- 단계 6과 9.3은 실제 제어판·설정 앱·메뉴 팝업에 돌려 보지 않았다.
- `scripts/`, `build.ps1`, `verify.ps1`, `tests/` 대부분은 읽지 않았다. 1.1-A의 스크립트 수정 범위는 열어 보고 정한다.
- 7.1의 토큰 수(노드당 150, 전체 15만)는 응답 형태에서 추정한 값이다.
- 이 도구가 computer use보다 낫다는 것은 구조에서 나온 판단이며 측정하지 않았다. 0.3이 그 측정이다.

## 7. 구현 결과 (2026-11-12)

P3에 따라 호환을 두지 않고 Core를 새 구조로 다시 썼다. 버전 0.4.0, wire 3.

### 검증

| 묶음 | 결과 | 내용 |
|---|---|---|
| 계약·처리 테스트 | 31개 통과 | 가짜 데스크톱. 3.1~3.6, 2.1, 4.3, 5.2, 5.6, 7.1, 8.2, 8.3, 9.6, 9.7, 작업별 인자 참조와 오류 메시지 |
| TCP 테스트 | 8개 통과 | 같은 프로세스의 Controller·Agent. 3.1, 3.2, 4.1, 4.2, P9, 10.5, 미확정 차단 |
| 실제 UI 테스트 | 7개 통과 | SimpleMemo, FormsWorkbench, WpfWorkbench(새 픽스처, 닫기 전 확인창 포함), 설정 앱(`ms-settings:about`), 제어판(`control.exe`), `runas` 거부, 모달 확인창을 여는 대화상자 버튼(pending-modal) |
| 패키지 검사 | 통과 | 배포본에서 Start → `--session-open --launch` → inspect → SDK 예제 → 닫기 → 재시작 → 정지 |
| 종단 간 수동 확인 | 통과 | 실제 `Controller.exe`·`Agent.exe`(watchdog 포함)로 CLI, `--doctor`, MCP(`initialize`, `tools/list`, 세션·관측·이미지 캡처·닫기) |

`.\verify.ps1 -UiTests`로 다시 돌린다.

### 기준 측정 (0.1)

`Driver.Tests.exe --bench artifacts/perf-baseline.txt`, Release, 가짜 프로바이더.

| 항목 | 평균 |
|---|---|
| find-element (처리기) | 0.12 ms |
| set-value (저널 fsync 포함) | 20.2 ms |
| type-text 500자 | 31 ms (SendInput 1회) |
| capture (가짜 PNG) | 24 ms |
| 파이프+TCP find-element (json-lines 경로) | 0.56 ms |
| CLI `--request` (호출마다 프로세스) | 136 ms |

변경 요청 비용은 수락 기록의 fsync가 대부분이다(2.2의 설계대로 유지).

### 계획과 다르게 한 것

| 항목 | 내용 |
|---|---|
| 스냅숏 토큰 | 부모 토큰 대신 부모 runtime id를 해시에 넣는다. 재검증이 요소 하나와 부모 한 번 조회로 끝난다 |
| 5.3 | UIA 작업자는 세션마다, 캡처·포커스는 Agent당 상주 작업자 하나가 맡는다 |
| 1.1-B 설정 파일 | `Agent.json`·`Controller.json`을 배포에서 뺐다. `--config`로 줄 때만 읽는다 |
| `launch` 기본 수명 | `keep` → `session`. 세션을 닫으면 종료된다 |
| `--machine` | 연결된 Agent가 하나면 생략할 수 있다 |
| 진단 스크립트 | `scripts/Test-DesktopEnvironment.ps1`, `tests/DesktopHelpers.ps1`를 지웠다. `--doctor`와 C# UI 테스트가 대신한다 |

### 하지 못한 것

| 항목 | 이유 |
|---|---|
| 0.1 변경 전 값 | 구 코드에서 같은 측정을 돌리지 않았다. 위 표는 변경 후 기준이다 |
| 0.2 실패 먼저 | 재현 테스트를 새 코드에 대해 작성했다. 구 코드에서 실패하는 것은 확인하지 않았다 |
| 0.3 computer use 비교 | 이 환경에서 computer use를 10회 돌릴 수 없었다. P2 달성 근거는 아직 없다 |
| 5.7 Core 분리 | 조건부 항목이다. CLI 기동 136 ms를 문제로 보지 않았다 |
| 6.4 관리자 경로 | Agent를 관리자로 실행한 `services.msc` 조작은 돌리지 않았다(일반 권한의 `ElevationRequired` 거부만 확인) |
| 9.3 Win32 공용 컨트롤 | `LegacyIAccessible` 값 대체와 `legacy-default-action`은 구현했다. `SysListView32`·`SysTreeView32`·속성 시트를 실제 대상으로 확인하지 않았다 |
| 실제 입력 | `click`(가림·안정성 검사, 횟수·수정 키), `type-text` 일괄 전송, lease 연장은 가짜 입력으로만 검증했다. 실제 키·마우스로는 돌리지 않았다 |
| 메모장 메뉴 | 6.3은 WPF 메뉴 팝업으로 확인했다. Windows 11 메모장은 실행 파일이 저장소 앱으로 넘기므로 `launch`가 아닌 `open`으로 다뤄야 한다 |
| README 수치 갱신 | 0.3 측정이 없어 computer use 대비 수치는 적지 않았다 |
