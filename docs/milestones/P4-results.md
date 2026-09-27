# P4 구현 결과 — MCP와 모델 세션

2026-09-27 · **P4 기반 구현 및 자동 검증 완료. 3개 실제 클라이언트 전체 수용 및 실제 언로드/사람 UI 수용 게이트는 미완료.** 실제 캐시 정리·Shell 휴지통·영구 삭제는 계속 비활성이다.

## 구현

- 공식 C# MCP SDK 2.2.0 기반 stdio 서버: overview/items/plan/plan_status 네 도구, schema 검증, 페이지 처리, 취소, 요청/응답 크기·시간 제한.
- 앱에서 켜는 스캔 메타데이터 공유와 같은 사용자·세션의 named pipe. 기본 공유 꺼짐, 주 앱 종료/공유 중지 시 폐기, 앱 자동 실행 없음.
- item ID 기반 실행 불가능한 계획, 앱 차단 근거 검토, 상태 조회. 에이전트가 승인·shell·언로드 기능을 호출할 수 없다.
- Ollama 0.34.4 로컬 로드 모델 조회, RAM/VRAM/working set 구분, 30초짜리 단회 사용자 승인과 재검증을 거치는 언로드. local 참조 강제, 클라우드 참조 차단, 모호한 결과는 자동 재시도 없이 표시.
- 연결·모델 세션·언로드 승인 WPF 화면. JSON/TOML 연결 설정을 표시하며 기존 클라이언트 설정은 자동 수정하지 않는다.

설계: [ADR-005](../architecture/ADR-005-agent-model-sessions.md). 실행·설정: [MCP 연결 안내](../MCP_SETUP.md).

## 검증

`./scripts/Validate.ps1 -WindowsIntegration`에서 Release 빌드 경고/오류 0개, **145/145** 통과: 정책 12, Windows 경계 34, 발견/집계 24, 기존 WPF 6, P3 승인/복구 38, P4 31개. P4 테스트는 실제 프로세스 stdio와 실제 CurrentUserOnly pipe를 통과하며 모델 변경 요청은 가짜 HTTP handler에서만 실행한다.

P4는 공유 기본 차단·만료·재스캔, cursor/item 유효성, 인자 변조, 멱등성/계획 한도, 앱 부재, 취소, 두 번째 pipe 소유, 대형 메시지, 5개 버전의 initialize 협상과 네 도구 호출, 언로드 승인 위조·재사용·만료·digest 변경·원격 endpoint·클라우드 참조·응답 유실·재로드·동시 실행, UI 기본 거부·만료·WPF binding을 검사했다.

SDK 협상 검증 버전은 `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, `2026-07-28`이다. 이것이 각 실제 클라이언트가 해당 버전 모두를 사용했다는 뜻은 아니다. Windows 공백/한글 실행 경로를 정확히 보존하기 위해 테스트 클라이언트는 ProcessStartInfo.ArgumentList와 SDK StreamClientTransport를 쓴다.

[자동 검사 증거](../evidence/p4-windows-2026-09-27.json)에는 humanUiVerified=false와 liveUnloadPerformed=false를 명시했다. RenderTargetBitmap으로 연결·모델·승인 화면 PNG를 생성하고 확인했지만 사람의 클릭·키보드·스크린리더 수용 검사를 대체하지 않는다. 그림과 원시 로그는 개인정보/fixture 혼입을 피하려고 ignored artifacts/p4에만 보관한다.

## 실제 클라이언트·로컬 서비스

| 대상 | 관측 버전 | 실제 확인 | 남은 범위 |
|---|---|---|---|
| Claude Code | 2.1.233 | 격리한 CLAUDE_CONFIG_DIR에서 stdio 등록 후 mcp get Connected | 사용자 실제 대화에서 네 도구 작업 흐름 |
| Codex CLI | 0.158.0-alpha.2.1 | app-server initialize 및 mcpServerStatus/list, DevHarbor 0.4.0의 네 도구 조회 | 실제 대화·제품 UI 수용 |
| Cursor | 미확인 | 확인한 PATH/설치 경로에서 실행 파일을 찾지 못함 | 설치 버전 고정·실제 연결·네 도구 호출 |
| Ollama | 0.34.4 | 실제 로컬 version/ps 조회, 로드 모델 0개 | 실제 모델을 대상으로 사용자 승인·언로드·재로드 수용 |

[클라이언트 증거](../evidence/p4-clients-2026-09-27.json), [로컬 Ollama 조회 증거](../evidence/p4-ollama-2026-09-27.json). Codex 검사 프로세스는 다른 MCP 서버와 apps/plugins를 프로세스 옵션으로 끄고 DevHarbor 서버만 등록했다. 기존 사용자 설정을 수정하거나 대화/추론을 시작하지 않았다. Claude도 임시 설정만 사용했다. 실제 클라이언트 검사에 사용된 협상 버전은 별도로 수집하지 않았으므로 추정하지 않는다.

## P5 인계 및 출시 게이트

1. Cursor 설치 환경에서 실제 연결·목록·네 도구 호출을 기록한다. Claude/Codex 실제 사용자 작업 흐름도 수용 검사한다.
2. 사람이 공유 켜기/중지, 계획 차단 설명, 기본 미선택 승인, 취소/만료, 창 닫기, 키보드·스크린리더를 확인한다.
3. 사용자가 지정한 로컬 모델로 다른 작업에 미치는 영향을 확인한 뒤 실제 언로드와 재로드를 검사한다. 이번 작업에서는 모델을 로드하거나 언로드하지 않았다.
4. Ollama 이름과 digest 사이에는 서버가 제공하는 원자적 조건부 실행 기능이 없다. 다른 앱과의 경쟁·POST 후 앱 종료 시 결과 미확정, 세션 이력 비영속성을 제품 수용에서 확인한다.
5. P5 패키지에 MCP DLL/런타임/의존성과 라이선스 고지를 포함하고 설치 경로의 실행 설정을 검사한다. 현재 설정은 개발 저장소 빌드 기준이다.
6. 실제 캐시 정리는 P1/P3의 안전·복구·어댑터·사람 승인 게이트를 별도로 통과해야 한다. MCP 구현으로 삭제 capability를 승격하지 않는다.

P4 원래 완료 기준의 ‘3개 실제 클라이언트’ 및 ‘사용자 주도 실제 언로드’는 위 미검증 항목이 해소되기 전까지 완료로 표시하지 않는다. 이번 커밋은 검증 가능한 구현 기반과 남은 게이트를 함께 인계한다.
