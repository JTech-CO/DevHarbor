# P4 — MCP와 모델 세션

상태: 대기. 조회 선행: P2. 변경 계획 연결 선행: P3. 담당: Mcp, Desktop, Discovery.

## 작업

1. stdio 서버에 `devharbor_overview`, `devharbor_items`, `devharbor_plan`, `devharbor_plan_status`를 제공한다.
2. schema validation, 페이지 처리, 요청 취소, stdout 프로토콜 전용, stderr 진단을 구현한다.
3. 에이전트는 item ID로 계획만 요청하고 앱 승인 후 상태를 조회한다. 클라이언트가 승인 권한을 발급하지 못하게 한다.
4. Claude/Codex/Cursor의 실제 지원 프로토콜·설정·버전을 고정해 연결을 확인한다.
5. Ollama 로드 모델 조회 및 사용자가 앱에서 요청하는 언로드를 검증한다. RAM/VRAM/working set은 구분한다.

## 산출물 및 게이트

MCP 호환성 표와 통합 테스트, 경로 메타데이터 공개 안내, 사용자 주도 언로드 결과. UI 부재·타임아웃이면 변경 없음, 임의 shell 인자 차단, 레벨/범위 확장 불가를 검증한다.

설정 자동 수정은 backup/dry-run/멱등성 검증 이후에만 추가한다. MCP 앱 종료·언로드, WSL 종료, standby RAM 비우기는 제외한다.
