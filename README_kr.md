# DesktopTestDriver

> **신뢰된 네트워크와 PC에서만 사용한다. 연결은 암호화하지 않으며 인증·권한 검사가 없다.**

**DesktopTestDriver는 코딩 에이전트(Claude Code, Codex 등)와 C# 테스트 코드가 Windows GUI를 화면 좌표가 아니라 UI 요소 단위로 찾고, 조작하고, 결과를 읽게 해 주는 도구다.** Windows UI Automation(UIA)으로 버튼·입력칸·목록을 ID와 이름으로 다루고, 필요할 때만 실제 키보드·마우스 입력을 쓴다. 스크린숏과 좌표에 의존하는 computer use보다 빠르고 정확하게 GUI를 시험하는 것이 목적이다.

[GUI 테스트 가이드](docs/gui-test-guide.md) · [명령·요청 규약](docs/agent-usage.md) · [English README](README.md)

## 한눈에 보기

**어디에 쓰는가.**

| 대상 | 예 |
|---|---|
| Windows 데스크톱 앱 | WinForms, WPF, WinUI, Win32로 만든 사내 툴, 런처, 관리 프로그램, 설치 프로그램 |
| Windows 자체 GUI | 설정 앱(`ms-settings:`), 제어판, `.msc` 관리 콘솔, 파일 탐색기 창 |
| 쓰는 상황 | 에이전트가 만든 기능을 실제 화면에서 E2E로 확인할 때, 같은 흐름을 반복해서 회귀 시험할 때, 여러 Windows PC를 한곳에서 시험할 때 |

**무엇이 특징인가.**

| 특징 | 내용 |
|---|---|
| 요소 단위 조작 | `automationId`·이름·종류(정규식·포함·순번 포함)로 대상을 찾고 값을 문자열 그대로 읽는다. 창 위치·해상도·DPI와 무관하다 |
| 짧은 관측 형식 | `inspect`는 한 줄에 요소 하나인 compact 트리를 돌려준다. 참조는 `e12`, 토큰은 12자다 |
| 조건 대기 | "이 값이 나타날 때까지"를 도구가 기다린다(최대 30초, `anyOf`로 성공·오류 동시 대기). 시간 초과는 `satisfied: false`로 돌려준다 |
| 한 번의 왕복 | 조작에 `expect`(결과 조건)와 `observeAfter`(조작 후 트리·새 창)를 붙여 같은 응답으로 받는다 |
| 틀린 대상 거부 | 창이 재생성되거나 요소가 바뀌면 오래된 참조를 거부하고, 대상이 여러 개면 후보 목록과 함께 거부한다 |
| 중복 실행 방지 | 변경 요청은 요청 ID로 기록해 같은 ID를 다시 보내도 재실행하지 않는다 |
| 뒷정리 | 세션이 띄운 앱(자식 프로세스 포함)과 `open`으로 연 창을 닫고, 도구가 누른 키·버튼을 해제한다 |
| 모델 없이 재실행 | `generate-selector`로 얻은 선택자를 C# Client 코드에 저장하면 이후에는 LLM 호출 없이 돌린다 |
| 도구 연동 | `Controller.exe --mcp`로 MCP 서버를 띄워 Claude Code 등에서 도구로 바로 호출한다 |

**computer use(스크린숏 + 좌표)와 무엇이 다른가.**

| | computer use | DesktopTestDriver |
|---|---|---|
| 대상 찾기 | 스크린숏을 모델이 보고 좌표를 추정한다 | 요소 ID·이름으로 조회한다 |
| 값 확인 | 화면의 글자를 모델이 읽는다 | 문자열을 그대로 받는다 |
| 완료 대기 | 스크린숏을 반복해서 찍고 판단한다 | 조건이 충족되면 반환한다 |
| 반복 실행 | 매번 모델이 화면을 본다 | 저장한 선택자로 모델 없이 실행할 수 있다 |

이 표는 동작 구조의 비교이며 측정 결과가 아니다. 같은 시나리오의 소요 시간·토큰 비교는 아직 측정하지 않았다.

**맞지 않는 경우.** 그래픽스로 직접 그리는 화면(게임 클라이언트, 캔버스 UI)은 UIA 트리가 없어 이점이 없다. 레이아웃 깨짐·색상 같은 시각적 결함은 UIA로 보이지 않으므로 캡처를 사람이나 모델이 봐야 한다. Windows 이외의 OS는 지원하지 않는다.

![DesktopTestDriver 사용 전후 비교와 조건 대기, 공통 테스트 기능](docs/assets/e2e-benefits-infographic.png)

## 구성

| 프로그램 | 배치 위치 | 역할 |
|---|---|---|
| `Controller.exe` | 코딩 에이전트 또는 테스트 실행기의 PC | 요청을 받아 대상 Agent로 전달하고 결과와 이미지를 모은다. CLI·json-lines·MCP 입구를 제공한다 |
| `Agent.exe` | 테스트할 앱이 있는 Windows PC | 로그인된 사용자 데스크톱에서 GUI를 관측·조작한다 |

같은 PC에서 둘 다 실행해도 된다. 원격에서는 대상 PC마다 Agent를 실행하고 `machineId`로 고른다. 연결은 평문 TCP이며 인증서·토큰·등록 절차가 없다. Agent가 Controller에 접속한다.

![별도 테스트 PC에 연결하는 구성 예시](docs/assets/architecture.svg)

## 빠르게 시작하기

### 1. 빌드한다

Windows 10/11 x64와 .NET 10 SDK가 있는 PC에서 실행한다.

```powershell
.\build.ps1 -Package
```

결과는 `bin/live_Release`에 생긴다. 실행 PC에는 .NET 10 Desktop Runtime x64가 필요하다.

### 2. 실행한다

설정 파일 없이 실행 인자만으로 뜬다. 기본 포트는 25001이다.

```powershell
# 같은 PC
.\Controller\Controller.exe --serve
.\Agent\Agent.exe                     # 로그인된 바탕화면에서 실행한다

# 원격: 에이전트 쪽 PC
.\Controller.exe --serve --listen 0.0.0.0:25001
# 원격: 테스트 대상 PC
.\Agent.exe --controller 192.168.0.10:25001 --machine test-pc
```

| 기본값 | 값 |
|---|---|
| Controller 수신 주소 | `127.0.0.1:25001` (원격 Agent를 받을 때만 `--listen`으로 바꾼다) |
| 상태 폴더 | `%LOCALAPPDATA%\DesktopTestDriver\Controller`, `...\Agent` (`--state`로 바꾼다) |
| Agent의 machineId | 컴퓨터 이름 (`--machine`으로 바꾼다) |

상주 실행은 `scripts\manage.ps1 -Action Start|Stop|Status|Restart|InstallLogonTask`를 쓴다. 관리자 권한 도구(regedit, 서비스)를 다루려면 Agent를 관리자로 실행한다(`InstallLogonTask -Highest`).

```powershell
.\Controller.exe --status
.\Controller.exe --doctor          # UIA 작업자, 캡처, 입력 감시, 저장소, 권한, 모니터를 실제로 점검한다
```

### 3. 세션을 열고 시험한다

```powershell
.\Controller.exe --session-open --launch C:\Tools\MyApp.exe
```

세션을 열고, 앱을 실행하고, 첫 창이 뜰 때까지 기다려 `windowId`를 돌려준다. 이미 실행 중인 앱은 `--target-pid`·`--target-exe`·`--target-window-title`로, 설정 앱·제어판은 세션 안에서 `open` 작업(`ms-settings:about`, `control.exe`)으로 연다. 실제 키·마우스 입력이 필요하면 `--input`을 붙인다.

```text
- Window "간단 메모" [e1 t=167cdf32baf7]
  - Button #OpenButton "열기" [e3 t=fffaf470023a] patterns=Invoke
  - Button #SaveButton "저장" [e4 t=1d93122bc0be] patterns=Invoke
  - Edit #MemoText [e5 t=b2d40c2611bd] value="" patterns=Value,Scroll
```

요청 형식, 작업 목록, 오류 처리는 [명령·요청 규약](docs/agent-usage.md), 저장·재열기 시험 절차는 [GUI 테스트 가이드](docs/gui-test-guide.md)를 따른다.

### 4. 코딩 에이전트에 연결한다

Claude Code에는 MCP 서버로 붙인다. Controller가 떠 있지 않으면 MCP 서버가 직접 띄운다.

```powershell
claude mcp add desktop-test-driver -- C:\DesktopTestDriver\Controller\Controller.exe --mcp
```

도구는 `gui_session`, `gui_window`, `gui_observe`, `gui_act`, `gui_wait`, `gui_capture` 여섯 개다. 함께 쓸 스킬 파일은 [skills/desktop-test-driver/SKILL.md](skills/desktop-test-driver/SKILL.md)에 있다. CLI만 쓰는 에이전트에는 아래처럼 맡긴다.

> Controller는 `C:\DesktopTestDriver\Controller\Controller.exe`다. 사용 규약을 읽고 `C:\GuiTest\SimpleMemo.exe`에서 문장 입력·저장·종료·재열기를 시험한다. 다시 읽은 내용이 일치해야 통과다. UIA 조작과 조건 대기를 쓰고, 실패하면 요청 ID와 관측 근거를 남긴다. 결과가 `completionUnknown`인 조작은 반복하지 않는다. 이번 시험에서 연 세션을 닫고, 확인한 결과와 미확인 항목을 구분해 보고한다.

## 검증

```powershell
.\build.ps1 -Configuration Debug   # 소스 빌드
.\verify.ps1                       # Release 빌드, 패키지, 계약·처리·TCP 테스트, 패키지 수명주기 검사
.\verify.ps1 -UiTests              # 위 + 실제 데스크톱 UIA 테스트(픽스처, 설정 앱, 제어판)
```

테스트는 세 묶음이다. 계약·처리 테스트는 가짜 데스크톱으로 결과 분류와 세션 상태를, TCP 테스트는 같은 프로세스 안의 Controller와 Agent로 시간 초과·큰 응답·재접속·캡처 전달을, UI 테스트는 WinForms·WPF 픽스처와 설정 앱·제어판을 실제로 다룬다. 실제 키·마우스 입력과 두 물리 PC 구성은 자동 검증에 포함하지 않는다. UIA는 모든 앱·컨트롤을 지원하지 않으며, 캡처는 최소화된 창에서 거부된다.

`Driver.Tests.exe --bench FILE`은 가짜 프로바이더로 Driver 자체의 처리 시간(요소 찾기, 값 설정, 문자 입력, 캡처, CLI·파이프 왕복)을 잰다.

<details>
<summary>저장소 구성</summary>

| 경로 | 내용 |
|---|---|
| `src/Controller/`, `src/Agent/` | 실행 프로그램(Controller는 CLI·json-lines·MCP 입구 포함) |
| `src/Client/` | C# 호출 API |
| `src/Core/`, `src/Contracts/` | 통신·UIA·입력·정리 구현과 요청·응답 계약 |
| `fixtures/`, `tests/` | 테스트 앱(SimpleMemo, FormsWorkbench, WpfWorkbench, InputWindow)과 자동 검증 |
| `schemas/`, `examples/standalone/` | DTO Schema(`--export-schemas`로 생성)와 독립 사용 예제 |
| `scripts/`, `docs/`, `skills/` | 실행 스크립트, 사용 문서, Claude Code 스킬 |

</details>

## 라이선스

[MIT License](LICENSE)를 따른다.
