# ADR-005 — MCP 메타데이터 공유와 로컬 모델 세션

- 날짜: 2026-09-27
- 상태: P4 기반 구현·자동 검증. 실제 캐시 변경, Cursor 연결, 사람 UI 수용 및 실제 모델 언로드 수용은 미완료.
- 이전 결정: [ADR-004](ADR-004-approved-workflow.md)

## 경계

`Desktop → AgentBridge ← Mcp`는 저장소 조사 결과와 실행 불가능한 계획 메타데이터만 교환한다. `Desktop → Models → 127.0.0.1:11434`는 별도의 모델 조회·승인 흐름이다. MCP는 Models를 참조하지 않으며 Execution의 내부 승인·실행 broker에도 접근할 수 없다. MCP에 shell, 파일 경로 실행, 승인 발급, 언로드, 앱 종료 도구는 없다.

공식 `ModelContextProtocol.Core` 2.2.0을 정확한 버전과 NuGet lock으로 고정했다. 해당 SDK 라이선스는 Apache-2.0이며 DevHarbor 자체 MIT와 구분한다. 전이 의존성은 각 packages.lock.json에 기록한다. P5 배포에서 SDK 및 전이 의존성의 라이선스 고지를 포함해야 한다.

## 명시적 공유

앱의 공유 기본값은 꺼짐이다. 사용자가 안내를 읽고 공유를 시작하면 현재 스캔과 이후 완료한 스캔의 도구 이름, 저장소 경로, 용량, 상태, 관측 시각을 같은 Windows 사용자·로그온 세션의 MCP 프로세스에 제공한다. 파일 내용과 인증 정보는 읽거나 공유하지 않는다. 경로는 사용자 이름·프로젝트 이름을 포함할 수 있고 연결한 AI 서비스로 전달될 수 있다. 경로 문자열은 신뢰되지 않는 데이터이며 지시문으로 취급하지 않는다.

동일 사용자 SID와 Windows 세션 ID에서 유도한 named pipe에 `CurrentUserOnly`, `FirstPipeInstance`를 적용한다. 원격 HTTP MCP listener는 열지 않는다. pipe 소유 프로세스가 이미 있으면 두 번째 앱의 공유는 실패한다. 이 경계는 다른 사용자·세션을 제한하는 것으로, 같은 사용자 권한의 악성 프로세스나 관리자에 대한 방어 보장은 아니다.

연결 창을 닫아도 공유는 유지된다. 공유 중지나 주 앱 종료는 snapshot·item ID·계획을 폐기한다. 이미 클라이언트에 전달한 정보까지 회수할 수는 없다. MCP 프로세스는 UI를 자동 실행하거나 스캔하지 않는다. 앱 부재·공유 꺼짐·스캔 없음·오래된 결과는 오류로 반환한다.

## 요청 계약

| 도구 | 인자 | 반환/행동 |
|---|---|---|
| `devharbor_overview` | `{}` | snapshot, 관측/만료 시각, 고유 논리·할당 용량, 부분/미측정 상태 |
| `devharbor_items` | `limit` 선택 1–50, `cursor` 선택 | 기본 20개, snapshot에 묶인 다음 cursor |
| `devharbor_plan` | `itemIds` 1–16개, `requestId` | 현재 item ID로만 계획 생성, 항상 `Blocked` |
| `devharbor_plan_status` | `planId` | `Blocked`/`Expired`, `reviewed` 등 메타데이터 |

ID는 32자리 소문자 hex다. requestId도 클라이언트가 생성한 같은 형식의 고유값이다. 동일 requestId·동일 순서 itemIds 요청은 기존 계획을 반환한다. 같은 requestId에 다른 항목을 넣으면 충돌한다. 임의 경로, command, approval, level, scope 및 추가 속성은 거절한다. MCP schema와 앱 bridge 양쪽에서 검증한다.

스캔은 완료 시각부터 10분 동안 유효하며 공유를 다시 켜도 오래된 스캔의 수명이 연장되지 않는다. 새 스캔은 item ID/cursor를 교체한다. 최대 1,000개 저장소 행, 세션당 100개 계획, 계획 유효기간 2분이다. 만료 계획은 같은 공유 세션에서 조회할 수 있으며 한도에 도달하면 공유 중지/재시작으로 초기화한다. 실제 캐시 정리 capability가 검증되지 않았으므로 `canExecute=false`, `cleanupEnabled=false`다. 앱의 ‘차단 근거 확인’은 `reviewed`만 변경하며 실행 승인이 아니다. P3의 앱 샘플 보관·복원에도 MCP로 접근하지 않는다.

stdio는 JSON-RPC 전용이고 오류 진단은 stderr로 보낸다. SDK가 initialize/tools/cancellation을 처리한다. stdio 입력 한 줄 64 KiB, 앱 요청 16 KiB, 인자 JSON 8,192자, 응답 2 MiB, JSON 깊이 16으로 제한한다. MCP 호출은 동시 4개까지, pipe는 한 번에 한 연결과 3초 deadline을 사용한다. 취소·타임아웃은 변경 실행을 유발하지 않는다. 이미 생성된 계획 메타데이터는 클라이언트 취소 후 남을 수 있다.

## Ollama 언로드

사용자가 모델 세션 창에서 조회를 누를 때만 `/api/version`, `/api/ps`를 읽는다. 대상은 기본 로컬 `http://127.0.0.1:11434/`로 고정한다. 다른 `OLLAMA_HOST`, 프록시, HTTP redirect는 허용하지 않는다. 버전 0.34.4만 언로드 대상으로 제한하며 나머지는 조회 전용이다. 각 HTTP 요청은 5초와 64 KiB 응답 제한을 갖는다.

1. 선택된 모델의 이름·digest를 다시 조회하여 30초짜리 내부 계획을 만든다.
2. 사용자에게 대상·digest·버전·다른 앱 영향·자동 재로드 가능성을 표시한다. 기본 해제된 체크박스와 별도 버튼으로 한 번만 쓰는 내부 승인 객체를 발급한다.
3. POST 직전에 버전·digest·만료·취소를 다시 확인한다. 사라진 모델에는 POST하지 않는다.
4. `/api/generate`에 `model=<name>:local`, `keep_alive=0`, `stream=false`만 전송한다. prompt·options는 없다. `:local`, `:cloud`, `-cloud`로 끝나는 모호한 이름은 사전에 차단한다. 0.34.4 소스에서 명시적 local 선택은 remote 모델 설정을 거부한다.
5. `done=true`, `done_reason=unload` 응답과 후속 조회의 부재를 확인한 경우에만 ‘언로드 후 부재 확인’으로 표시한다. 재로드는 StillLoaded, 응답 유실·POST 이후 취소는 OutcomeUnknown이며 자동 재시도하지 않는다.

Ollama API는 digest를 조건으로 한 원자적 언로드를 제공하지 않는다. 재검증 직후 동일 로컬 이름의 모델이 교체되는 경쟁과 다른 앱의 재로드를 완전히 막지는 못한다. 사용자는 다른 모델 작업과 동시에 실행하지 않아야 한다. 모델 파일 삭제·프로세스 종료·WSL 종료·standby RAM 비우기는 하지 않는다. 결과 이력은 창 메모리에만 존재하며 영속 원장/크래시 후 성공 복원은 제공하지 않는다. 앱 재시작 후에는 새로 조회해야 한다.

`/api/ps.size`, `size_vram`, 프로세스 working set, RAM을 서로 대체하지 않는다. RAM은 미측정이며 VRAM 누락은 null이다. working set은 `ollama` 이름의 접근 가능한 프로세스 합계로 공유 페이지 중복을 포함할 수 있다. 어느 값도 실제 회수량으로 표시하지 않는다.

## 검증 근거

- [P4 결과와 남은 게이트](../milestones/P4-results.md)
- [공식 C# SDK 2.2.0](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.2.0)
- [MCP lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)
- [Ollama loaded models](https://docs.ollama.com/api/ps), [generate](https://docs.ollama.com/api/generate)
- [Ollama 0.34.4 model source parser](https://github.com/ollama/ollama/blob/v0.34.4/internal/modelref/modelref.go), [generate handler](https://github.com/ollama/ollama/blob/v0.34.4/server/routes.go)
