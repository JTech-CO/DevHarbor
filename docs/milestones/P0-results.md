# P0 결과 — 2026-09-25

상태: 로컬 완료. 후속 단계: P1 Windows 변경 안전성.

## 완료한 작업

- 지정 저장소 `JTech-CO/DevHarbor`의 main 이력과 MIT 라이선스를 보존하고 로컬 `chore/devharbor-p0-foundation` 브랜치를 구성했다.
- 이전 제품의 DMG·아이콘·폰트·번들 메타데이터·설명문 6개와 포팅 문서 1개를 제거했다. DevHarbor 기획과 자체 안전성 prototype은 보존했다.
- P0–P5 문서와 ADR/adapter 계약을 작성했다.
- .NET 10.0.401 SDK를 `.tools/dotnet`에 설치하고 `global.json`에 고정했다. 전역 PATH를 수정하지 않았다.
- Domain 정책 및 독립 executable contract checks, 공통 빌드 설정, 검증 스크립트, Windows GitHub Actions workflow를 추가했다.
- 관리 대상에 포함하지 않는 SDK·빌드 산출물·개인 경로가 있는 실험 결과의 제외 규칙을 추가했다.

## 검증 결과

`scripts/Validate.ps1` 실행: SDK 10.0.401, Release 빌드 경고 0/오류 0, 정책 검증 **12/12 통과**, Git whitespace 검사 통과.

검증 항목은 보호 자산, 증거 미확인·만료·미래 시각, busy/unknown, 원격/WSL, 크기 unknown/0/음수, 미지원 adapter, 비정상 enum, 복수 제외 이유 보존이다. 테스트는 실제 파일을 삭제하지 않는다.

기존 Windows API 실험 **22개 통과** 기록은 별도 보고서에 보존했다. 이번 P0에서 실험 코드를 변경하지 않아 휴지통 왕복은 재실행하지 않았다.

원격 CI는 구성만 했으며 아직 실행하지 않았다. WPF 사용자 화면·MCP·실제 도구 스캔·SQLite 원장·승인 broker는 구현 전이다. 후보 가능 판정은 실행 권한이 아니며 `ProductCapabilities.CleanupEnabled`는 false다.

## P1에 넘기는 위험

검증 핸들 해제와 Shell 이동 사이의 경합, 휴지통 비활성/용량 부족 상황, 복원 충돌 경합, 크래시 시 매니페스트 복구는 해결 전이다. 삭제 executor를 도입하기 전에 P1 게이트를 통과해야 한다. .NET 10 SDK 설치 시 CLI의 기본 첫 실행 초기화가 수행되었으며, 검증 스크립트는 이후 개발 인증서 자동 생성과 도구 PATH 추가를 비활성화한다.
