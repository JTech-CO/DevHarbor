# P2 검증

`./scripts/Validate.ps1 -WindowsIntegration`은 기존 정책/Windows 검사와 발견·집계 24개, WPF 6개 검사를 실행한다. 외부 테스트 패키지는 사용하지 않는 콘솔 runner다.

합성 저장소는 `artifacts/p2` 안에서만 생성한다. 일부 fixture에 hardlink/junction/ACL deny/offline을 사용하며 finally에서 ACL/속성을 복구한다. 파일을 자동 재귀 삭제하지 않는다. 프로세스 제한 시험은 이 runner의 `--worker` 모드만 시작/종료한다. 실제 도구 호출은 일반 suite에 포함하지 않는다.

선택적 설치 환경 확인: `dotnet run --project tests/DevHarbor.DiscoveryChecks --configuration Release -- --probe`. 현재 도구의 버전/경로 설정과 로컬 Docker 사용량을 조회하지만 캐시 파일 순회는 하지 않는다. 출력은 버전과 상태만 남기며 개인 경로를 포함하지 않는다.

WPF 검사 `tests/DevHarbor.DesktopChecks`는 제품 XAML을 소프트웨어 렌더링하고 바인딩/필터/명령 상태/Dispatcher 응답성/취소를 검증한다. 실제 사용자 UI 조작을 수행한 것으로 주장하지 않는다. 표본 성능은 결과 JSON에 보존하며 고정 성능 수치로 광고하지 않는다.
