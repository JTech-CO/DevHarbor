# DevHarbor

Windows 개발·로컬 AI 환경의 저장 공간을 조사하는 읽기 전용 WPF 앱입니다. P2 구현과 자동 검증을 마쳤으며, 실제 삭제 기능은 아직 제공하지 않습니다.

- [제품·브랜드·구성 요소 기획](docs/DEVHARBOR_PRODUCT_PLAN.md)
- [Windows 안전성 실험 결과](docs/WINDOWS_SAFETY_REPORT.md)
- [실행 가능한 검증 프로젝트](prototypes/windows-safety/README.md)
- [마일스톤 로드맵](docs/milestones/README.md)
- [P0 작업 및 완료 기준](docs/milestones/P0-foundation.md)
- [P1 종료·P2 착수 조건](docs/milestones/P1-closeout.md)
- [P2 구현 결과·실행·검증 범위](docs/milestones/P2-results.md)
- [읽기 전용 발견·집계 설계](docs/architecture/ADR-003-readonly-discovery.md)
- [P2 읽기 API 계약](docs/architecture/READONLY_BOUNDARY.md)
- [P1 결과와 삭제 게이트](docs/milestones/P1-results.md)
- [아키텍처 결정](docs/architecture/ADR-001-foundation.md)
- [도구 지원 계약](docs/architecture/ADAPTER_CONTRACT.md)

저장소: [JTech-CO/DevHarbor](https://github.com/JTech-CO/DevHarbor). MIT 라이선스. 이름의 상표·도메인 가용성은 별도 검토 대상입니다.

pip/uv/npm/Ollama/Hugging Face의 저장소 경로와 로컬 Docker 사용량을 조회합니다. 파일 ID로 중복을 제거하고 논리/할당/공유/미측정 용량을 구분합니다. WPF에서 필터·근거·사용자 경로·취소/부분 결과를 확인할 수 있습니다. 사람의 수동 UI 수용 확인은 아직 남아 있습니다. 실제 제품 삭제·Shell 휴지통·MCP 서버는 비활성/미구현 상태입니다.

```powershell
# 읽기 전용 앱 실행 (스캔 버튼으로 시작)
./scripts/Start-DevHarbor.ps1

./scripts/Validate.ps1
# 정책·Windows 경계·발견/집계·WPF 검증
./scripts/Validate.ps1 -WindowsIntegration
```

.NET 10 SDK가 필요하며 `.tools/dotnet/dotnet.exe`가 있으면 우선 사용합니다. 세부 진행 상태는 각 마일스톤 문서를 기준으로 합니다.
