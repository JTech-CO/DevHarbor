# DevHarbor

Windows 개발·로컬 AI 환경을 위한 독립적인 저장소 관리 제품을 기획하고 검증하는 작업 폴더입니다. 출시 가능한 클리너는 아직 아닙니다.

- [제품·브랜드·구성 요소 기획](docs/DEVHARBOR_PRODUCT_PLAN.md)
- [Windows 안전성 실험 결과](docs/WINDOWS_SAFETY_REPORT.md)
- [실행 가능한 검증 프로젝트](prototypes/windows-safety/README.md)
- [마일스톤 로드맵](docs/milestones/README.md)
- [P0 작업 및 완료 기준](docs/milestones/P0-foundation.md)
- [P1 결과와 삭제 게이트](docs/milestones/P1-results.md)
- [아키텍처 결정](docs/architecture/ADR-001-foundation.md)
- [도구 지원 계약](docs/architecture/ADAPTER_CONTRACT.md)

저장소: [JTech-CO/DevHarbor](https://github.com/JTech-CO/DevHarbor). MIT 라이선스. 이름의 상표·도메인 가용성은 별도 검토 대상입니다.

현재 코드는 정책 계약, 읽기 전용 Windows 경계 검사, 테스트 전용 internal 격리·복원 primitive를 포함합니다. 실제 제품 삭제·Shell 휴지통·MCP 서버·데스크톱 앱은 비활성/미구현 상태입니다.

```powershell
./scripts/Validate.ps1
# Windows synthetic fixture의 실제 핸들 이동·복원까지 검증
./scripts/Validate.ps1 -WindowsIntegration
```

.NET 10 SDK가 필요하며 `.tools/dotnet/dotnet.exe`가 있으면 우선 사용합니다. 세부 진행 상태는 각 마일스톤 문서를 기준으로 합니다.
