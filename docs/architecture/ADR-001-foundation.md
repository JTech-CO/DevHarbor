# ADR-001 — Windows 전용 기반과 변경 기본 차단

상태: 채택 · 2026-09-25

## 결정

- C#/.NET 10 LTS. WPF는 P2 화면, SQLite는 P3 원장에 사용한다. P0에는 Domain과 외부 패키지 없는 실행형 검증 프로젝트만 둔다.
- Windows 11 x64, 로컬 NTFS, 현재 사용자 계정이 첫 지원 목표다. 현재 검증 PC의 정확한 빌드는 실험 보고서에 별도 기록한다.
- `Domain`은 OS/CLI/UI/DB에 의존하지 않는다. Discovery/Evidence는 항목과 근거를 생산하고, Execution은 정책·승인·원장을 거쳐 Windows/벤더 adapter를 호출한다.
- Desktop/Mcp는 정책을 우회하지 않는다. P0의 모든 변경 capability는 false다.
- 읽기 전용 조회와 변경 가능한 adapter의 버전 지원은 분리한다. 도구 이름만으로 변경 권한을 부여하지 않는다.
- 외부 NuGet 의존성은 필요한 단계에서 명시적으로 고정한다. 초기 계약 테스트는 콘솔 runner를 사용하며 표준 `dotnet test` 프로젝트로 오인하지 않도록 실행 방법을 문서화한다.

## 근거와 대안

Windows 파일 핸들·Shell COM·사용자 세션 API를 직접 다루는 제품이므로 C#/.NET를 선택했다. 웹 UI 또는 공통 OS core가 현재 요구의 우선순위가 아니므로 Rust/Tauri/Electron을 이번 기반에 도입하지 않는다. UI와 Domain 분리로 추후 선택 변경의 범위를 제한한다.

.NET 10은 LTS이며 2028-11-14까지 지원되는 것으로 공식 정책에 명시되어 있다. 최신 패치 유지가 필요하다. 설치 SDK는 `global.json`에 기록하고, patch roll-forward만 허용한다. [Microsoft 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

## 남은 위험

Shell 이동 전 핸들 해제 경합, 휴지통 보장, 승인 broker와 MCP 신뢰 경계는 미해결이다. 기존 prototype 코드를 production executor로 복사하지 않는다. P1/P3 게이트까지 변경 기능은 비활성 상태를 유지한다.
