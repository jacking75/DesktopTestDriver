# Controller / Agent 실행 계약

## 구성

Controller.exe는 조작 PC의 상시 프로세스다. 실행 파일 옆 Controller.json으로 기동하며 연결된 머신들을 machineId로 관리한다. 동일 실행 파일의 list/begin/request/end/exit/shutdown 명령은 잠시 실행되는 로컬 관리 클라이언트다.

Agent.exe는 시험 머신당 하나의 상시 프로세스다. 실행 파일 옆 Agent.json으로 기동한다. 직접 실행과 SSH 배포 후 기동을 모두 지원한다. 머신 전역 이름 객체로 설정 경로·machineId·로그인 세션이 달라도 두 번째 상시 Agent 기동을 거부한다. 작업 실행부와 watchdog은 별도 자식 프로세스이며 상시 Agent 개수에 포함하지 않는다.

## 설정

Controller.json: host(Agent가 접속할 주소), port(25001~25199), stateRoot(내부 상태 경로). 상대 경로는 실행 파일 폴더 기준이다. Agent.json: machineId, host, port, targetSession(0이면 현재 로그인 세션), stateRoot. 상태 폴더는 없으면 자동 생성하고 기존 폴더는 소유권을 확인한다. Controller 상태에는 내부 IPC 이름과 작업 기록을 보존한다. 사용자가 초기화 명령이나 IPC 설정을 작성할 필요가 없다.

## 제어

자동화는 외부 프로젝트가 소유하는 schemaVersion 1 JSON 시나리오로 수행한다. 작성 에이전트의 기준 문서는 [시나리오 작성 규약](scenario-authoring.md)이다. `scenario-check SCENARIO [BINDINGS]`는 접속 없는 정적 검증, `scenario-run SCENARIO BINDINGS`는 상시 Controller가 수행하는 실행 요청이다. 역할별 run 설정을 bindings로 연결하고, 실행기에서 결과 참조·대상 유일성·읽기 조건 대기·expect 판정·첫 실패 중단·역순 작업 정리를 처리한다. 시나리오 추가에 Controller/Agent 변경이나 앱별 어댑터가 필요하지 않다.

시나리오는 모든 대상 머신을 실행 전에 예약한다. 동일 머신/작업의 외부 begin/request/exit는 거부하고, end는 시나리오 전체에 취소를 요청한다. end의 cancellationRequested 응답 자체는 정리 완료가 아니며 report.json의 역할별 정리 영수증으로 확인한다. 시나리오 요청 CLI가 종료되면 로컬 IPC 단절로 취소하고, Controller 종료 시에도 예약 작업을 정리한다. 전체 상한에서 역할당 8초 + 결과 기록 2초를 예약한다. 통신 단절 시 정리 확인 실패를 PASS로 처리하지 않는다.

Controller.exe를 인자 없이 실행하면 수신을 시작한다. list, begin MACHINE RUN_JSON, request MACHINE COMMAND_JSON, end MACHINE, exit MACHINE, result RUN_ID, shutdown 명령은 같은 폴더의 설정을 사용한다. 요청 JSON 파일은 명령 인자로 지정하므로 PowerShell 파이프가 필요하지 않다. 각 머신은 동시에 한 작업을 실행하고 서로 다른 머신의 작업은 독립적으로 관리한다. 현재 수신 한도는 Agent와 작업 연결을 합쳐 128개다.

일반 TCP/UTF-8 JSON Lines를 사용하며 TLS·HTTPS·연결 인증은 없다. 최대 메시지는 1MiB다. SSH/Python은 선택적인 배포·제거 수단이고 작업 제어에는 사용하지 않는다. 작업 설정의 명령·창·복귀·시간 범위는 연결 인증과 별개다.

연결이 끊기면 진행 작업을 중단하고 입력·소유 앱을 정리한다. Agent만 재접속하고 작업은 자동 재시작하지 않는다. end는 작업만 종료하고 exit는 Agent도 종료한다. 정리가 확인되지 않으면 다음 작업을 거부한다.

현재 변경은 개발 단계다. 시나리오 계약·실행기의 회귀 검사를 작성했으며 실행하지 않았다. 런타임 검증은 사용자의 계획 검토 이후에 진행한다.
