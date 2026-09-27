# P2 결과 — 읽기 전용 저장소 지도

2026-09-27 · 구현·자동 검증 완료. **사용자 수동 UI 수용 확인은 아직 하지 않았으며 제품 삭제는 계속 NO-GO다.**

## 구현

- pip/uv/npm/Ollama/Hugging Face/Docker 읽기 전용 발견과 실행 파일 미설치·설정 오류·명령 실패 상태.
- 핸들 기반 디렉터리 열거와 root/항목 ID 재검사. reparse·offline·경계 밖 경로 차단.
- 파일 ID 기반 hardlink 및 중첩 저장소 중복 제거, 논리/할당/공유/미측정 구분.
- 한국어 WPF 저장소 표, 드라이브/환경 필터, 경로·근거·버전·시각 상세, 사용자 경로, 취소·부분 결과.
- 엔진 사용량을 호스트 합계와 분리하고 Docker 원격 endpoint를 호출하지 않음.

결정과 한계는 [ADR-003](../architecture/ADR-003-readonly-discovery.md), 버전별 근거는 [adapter catalog](../architecture/adapter-catalog.json)를 따른다.

## 검증

`./scripts/Validate.ps1 -WindowsIntegration`으로 Release 빌드 경고 0, 오류 0. **정책 12/12 + Windows 34/34 + 발견/집계 24/24 + WPF 6/6 = 76개 통과**.

도구별 CLI fixture, 사용자 지정 한글/공백 경로, 미설치, 손상 JSON/누락 필드, timeout·출력 제한·취소, shell 메타문자 인자 보존, 원격 Docker 제외를 검증했다. 파일 fixture에서는 hardlink와 부모/자식 중복, 손상 인덱스가 있어도 내용 해석 없는 크기 조회, HF 링크 없는 snapshot, 접근 거부, 사라진 root, 빈 폴더, junction 외부 sentinel, offline, root/leaf 교체, 디렉터리 pin, 예산·중간 취소의 부분합 보존을 확인했다.

로컬 Windows NT 10.0.26200.0, x64, SDK 10.0.401, NTFS 측정값:

| 항목 | 관측값 |
|---|---:|
| 1,500개 파일 단일 root 조사 | 1967ms |
| WPF 1,500개 파일을 두 중첩 저장소로 조사 | 3441ms |
| 20ms DispatcherTimer의 최대 갱신 간격 | 45ms |
| 중지 요청부터 부분 결과 반환 | 14ms |

반복 성능 보증이나 모든 디스크의 최악값이 아니다. 취소 시험은 항목마다 4ms 지연을 넣은 파일시스템 wrapper와 실제 경계 조회를 사용했다. 실제 사용자 캐시 전체를 스캔하거나 변경하지 않았다.

증거: [발견/집계](../evidence/p2-discovery-2026-09-27.json), [WPF·응답성](../evidence/p2-desktop-2026-09-27.json), [설치 도구 조회](../evidence/p2-installed-tools-2026-09-27.json). 증거에는 개인 절대 경로·환경 값·raw 도구 출력을 포함하지 않는다.

## 실제 설치 환경 확인

pip 25.2, uv 0.11.19, npm 11.12.1의 버전·캐시 경로 조회가 성공했다. Ollama 실행 파일과 설정/기본 경로 발견은 성공했지만 PE 버전은 미확인이다. 현재 Python에는 huggingface-hub가 확인되지 않았다. Docker CLI 29.6.2와 로컬 endpoint는 확인했지만 엔진 사용량 호출은 실패했다. HF 정상 발견과 Docker 정상 사용량은 합성 응답으로 검증했으며 실제 환경 성공으로 표시하지 않는다. 임의 버전/레이아웃의 재생성·정리 안전성은 검증하지 않았다.

## 화면 검토

동일한 제품 XAML/리소스를 WPF로 렌더링했다. 빈 상태, 결과 표, 작은 화면, 선택 상세와 필터 상태를 에이전트가 이미지로 확인했다. 배경과 버튼의 대비를 수정했다. 기본 크기 1360×880의 내용 영역에서 6개 행과 경로/근거가 표시되고, 1080×760 내용 영역에서는 표에 스크롤이 제공된다. 결합 오류 0건, 스캔 중 명령 상태 전환·취소 후 재시작 가능 상태도 검증했다.

렌더링은 `artifacts/p2/ui-empty.png`, `ui-results.png`, `ui-minimum.png`에 남는다. 로컬 fixture 경로가 포함되므로 Git에는 올리지 않는다. 이 결과는 자동 렌더링 및 에이전트 검토이며 실제 사람이 앱을 조작한 검증, 고대비/스크린리더/다중 DPI 수용 확인을 대신하지 않는다.

## 실행과 인계

```powershell
./scripts/Start-DevHarbor.ps1
```

Windows x64 및 .NET 10 SDK가 필요하다. 로컬 `.tools/dotnet`을 우선 사용한다. 설치 파일/서명/자동 업데이트는 P5 범위다. 앱을 열고 저장소 스캔을 눌러야 조회가 시작된다. 사용자 경로는 해당 실행 세션 동안만 유지한다.

P3는 P1의 D1–D5 차단 조건과 이 관찰 모델을 인수한다. 미지원 cloud/WSL/원격/파일시스템 확장, 회수 가능량 추정, blob/revision 의미 해석은 별도 검증이 필요하다. P2 행의 측정 완료를 정리 후보나 사용자 승인으로 사용할 수 없다.
