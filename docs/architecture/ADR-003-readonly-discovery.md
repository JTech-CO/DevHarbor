# ADR-003 — 읽기 전용 저장소 지도와 측정 계약

채택 · 2026-09-27 · P2

## 구조

`DevHarbor.Desktop`은 WPF 화면과 view model을, `DevHarbor.Discovery`는 경로 발견·CLI 제한·스캔 예산·집계를 맡는다. `DevHarbor.Windows`가 실제 NTFS 핸들을 연다. 새 외부 NuGet 의존성은 없다. UI/발견 모듈은 internal 격리 함수를 사용할 수 없으며 삭제·복원·승인 버튼을 제공하지 않는다.

## 발견

앱 시작 시 자동 스캔하지 않는다. 사용자가 스캔을 누르면 현재 프로세스 환경과 로컬 PATH의 실행 파일을 사용한다. 현재 PATH가 선택한 Python만 조회하며 모든 가상환경을 자동 탐색하지 않는다. 작업 디렉터리는 사용자 프로필이므로 프로젝트별 설정을 모두 대표하지 않는다. 사용자 지정 저장소는 별도 행으로 추가할 수 있으며 소유 도구를 추정하지 않는다.

| 도구 | 구현한 읽기 표면 | 제한 |
|---|---|---|
| pip | 선택한 Python의 `pip --version`, `pip cache dir` | interpreter 자동 전수 탐색 없음 |
| uv | `uv --version`, `uv cache dir` | 현재 사용자 설정 범위 |
| npm | 로컬 node + npm-cli.js, version/config get cache | shell/.cmd 실행 없음, 로그·업데이트 알림 억제 |
| Ollama | OLLAMA_MODELS 또는 Windows 기본 모델 경로, 실행 파일 버전 메타데이터 | 서버 API/CLI 연결 없음, 실행 서버와 설정 일치·버전 미확인 가능 |
| Hugging Face | Python 설치 메타데이터, HF_HUB_CACHE → HUGGINGFACE_HUB_CACHE → HF_HOME/XDG → 기본 hub | hub만 조사, 토큰·datasets·xet·assets는 범위 밖 |
| Docker | CLI 버전, context show/inspect, 명시적 로컬 named pipe의 system df | TCP/SSH/원격 endpoint는 연결하지 않음, 별도 builder 조사 없음 |

명령은 절대 실행 파일과 ArgumentList로 실행한다. 공통 제한은 8초와 stdout/stderr 각각 32,768자다. 시간/출력 초과 및 취소 시 자신이 시작한 프로세스 트리를 종료한다. raw stderr나 환경 변수 전체를 UI/증거에 저장하지 않는다. 설정 조회 도구 자체의 구현까지 무부작용이라고 보증하는 것은 아니며 DevHarbor는 설치·prune·clean·purge 명령을 호출하지 않는다.

Docker는 DOCKER_CONTEXT가 DOCKER_HOST보다 우선한다. 로컬 endpoint를 검증한 다음 context 이름 대신 해당 named pipe를 `--host`에 고정하며 자식 프로세스의 context/host/TLS override를 제거한다. 조회 결과의 네 분류와 크기 형식을 검증하고, 누락/잘못된 응답을 0으로 바꾸지 않는다.

## 순회와 경합

스캔 시작의 root ID를 유지한다. `ReadDirectory`는 조상과 대상 디렉터리 핸들을 유지하고 `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`로 이름과 ID를 열거한다. 각 항목을 `ReadScanMetadata`로 다시 열어 시작 root ID 및 열거된 파일 ID와 비교한다. 교체된 root/파일, reparse/offline/recall, 경계 밖, case-sensitive 경로는 제외한다. 폴더 변경 후 다른 객체를 이어 읽지 않으며 핸들은 iterator 종료·오류·취소 시 해제한다.

동시 쓰기 중인 전체 저장소의 원자적 snapshot은 아니다. 파일 ID 재사용, 동일 길이 메모리 매핑 변경 등 P1의 제한은 그대로다. 결과는 조사 시점의 메타데이터 관찰값이며 정리 허가나 재생성 증거가 아니다.

## 크기와 중복

집계 키는 해당 스캔 세션 안에서 drive + volume serial + file ID다. 한 저장소 안의 hardlink, 다른 저장소와 겹치는 root, 부모·자식 경로는 전체 합계에서 한 번만 계산한다. 도구별 행에는 각 도구가 참조한 고유 파일 크기를 표시하므로 행들을 단순 합산하면 안 된다. 다른 행에서도 관찰했거나 link 수가 2 이상인 파일을 ‘공유 포함분’으로 표시한다. 내용이 같은 별개 파일은 별개의 점유량이다.

논리 크기는 기본 스트림 EOF, 할당 크기는 FILE_STANDARD_INFO 값이다. ADS·NTFS 메타데이터·압축/VHD의 실제 회수량 전체를 대표하지 않는다. Docker가 보고한 엔진 사용량은 별도 상세로 표시하며 호스트 파일 합계에 섞지 않는다. 디렉터리는 열거에 성공해야 빈 저장소 0으로 표시할 수 있다. 읽지 못한 root는 null, 일부 읽은 저장소는 부분합과 제외 개수를 남긴다. 부분합의 ‘≥’는 조사된 부분을 나타내며 계속 바뀌는 저장소의 현재 크기를 보장하지 않는다.

## 예산과 UI

한 번의 스캔은 최대 100개 저장소, 100,000개 항목, 깊이 128, 파일 조사 60초다. UI 사용자 경로는 최대 20개다. 저장소별 상세 오류는 100개까지 저장하고 전체 제외 개수는 유지한다. 예산/취소 후 부분 결과를 반환한다. 동기 Windows syscall 자체의 취소 지연을 보장하지는 않는다.

발견과 조사는 UI 밖에서 실행한다. 진행 알림은 100ms 간격으로 제한하며, 완료/취소 후 도구별 결과를 갱신한다. 스캔 중 중복 실행과 사용자 경로 추가를 막는다. 드라이브/환경 필터는 표에만 적용하고 상단 합계는 전체 관찰값임을 표시한다. 취소 후 재스캔할 수 있다.

## 공식 근거

[pip cache](https://pip.pypa.io/en/stable/cli/pip_cache/), [uv cache](https://docs.astral.sh/uv/concepts/cache/), [npm config](https://docs.npmjs.com/cli/v11/using-npm/config/), [Ollama 모델 경로](https://docs.ollama.com/faq#where-are-models-stored), [Hugging Face 환경 변수](https://huggingface.co/docs/huggingface_hub/en/package_reference/environment_variables), [Docker contexts](https://docs.docker.com/engine/manage-resources/contexts/), [docker system df](https://docs.docker.com/reference/cli/docker/system/df/), [FILE_ID_BOTH_DIR_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info).
