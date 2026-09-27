# P2 읽기 전용 Windows 경계 계약

2026-09-27 · P1 인계 계약. `DevHarbor.Windows.WindowsBoundary.ReadMetadata(root, path, cancellationToken)`을 사용한다. `Inspect`는 작은 파일의 변경 실험용 SHA-256 snapshot이므로 저장소 스캔에 사용하지 않는다.

## 입력과 지원 범위

Windows build 22000 이상 x64, 로컬 고정 NTFS 볼륨의 명시적인 도구 저장소 root가 대상이다. root 자신 또는 그 아래의 단일 경로만 조회한다. 전체 사용자 프로필·시스템 경로, UNC/device/ADS/상대/모호한 경로, reparse·offline/recall·case-sensitive 경로를 거부한다. 실제 클라우드 provider와 다른 파일시스템은 검증된 지원 범위가 아니다.

볼륨부터 조상을 열어 이름과 파일시스템 경계를 검사한다. 마지막 항목은 `FILE_READ_ATTRIBUTES`로 열며 파일 데이터 읽기, 해시, 자식 열거, 수정 API를 호출하지 않는다. 조상 디렉터리에는 기존 경계와 같은 열거 권한이 필요하다. 64 MiB 제한은 적용하지 않는다. 실제 크기 검증은 65 MiB 일반 파일과 5 GiB sparse fixture로 수행했다.

## 반환 의미

| 필드 | 의미와 사용 조건 |
|---|---|
| Root, Path | 검사된 스캔 범위와 항목 경로 |
| Identity | 볼륨 serial + NTFS 파일 ID. 동일 스캔의 같은 로컬 드라이브에서 hardlink 중복 제거에 사용 |
| IsDirectory | 디렉터리 여부. 디렉터리는 하위 용량을 측정한 것이 아님 |
| LogicalBytes | 이름 없는 기본 데이터 스트림의 EOF. 디렉터리는 null, 실제 빈 파일은 0 |
| AllocatedBytes | `FILE_STANDARD_INFO.AllocationSize`. 디렉터리는 null. 회수 가능 용량이 아님 |
| LinkCount | 외부 경로를 포함할 수 있는 hardlink 수. 이름별 용량을 중복 합산하지 않음 |
| LastWrite | Win32 FILETIME 형식의 마지막 쓰기 값. 내용 fingerprint가 아님 |
| ObservedAt | 메타데이터 조회가 끝난 UTC 시각 |

할당 값은 ADS 전체·NTFS 메타데이터·도구 공유 블롭·드라이브 여유 공간 변화까지 합산한 값이 아니다. 같은 파일 ID라도 스캔 사이에 재사용될 수 있고 볼륨 serial만으로 모든 드라이브의 고유성을 보장하지 않는다. P2 집계 키는 스캔 세션과 드라이브 범위를 함께 포함해야 한다. hardlink 조회 허용은 해당 파일의 정리 허용을 의미하지 않으며 기존 변경 경계는 여전히 다중 링크를 거부한다.

조회 중 관찰한 identity/속성/EOF/mtime/link 수 불일치는 `TargetChanged`다. 동시 쓰기 전체나 동일 길이의 내용 변화를 감지한다는 보장은 없다. 결과는 관찰값이며 lease·내용 snapshot·승인 토큰이 아니다. 반환/오류 시 모든 핸들을 해제한다.

## 오류와 취소

실패는 `BoundaryException.Reason`과 선택적인 `NativeError`로 전달한다. 접근 거부는 `AccessDenied`, 사라진/바뀐 항목은 `TargetChanged`, 경계 밖은 `OutsideBoundary`다. 나머지는 기존 `BoundaryError`를 따른다. OS 공유 위반이 반환될 때만 `Busy`이며 메타데이터 조회 성공으로 사용 중 여부를 판단할 수 없다.

취소는 호출자의 token을 담은 `OperationCanceledException`이다. 진입·조상 순회·최종 관찰 반환 전에 확인한다. 이미 진행 중인 동기 Windows syscall을 강제로 중단한다는 보장은 없다. P2는 UI 밖에서 실행하고 부분 결과를 보존하며, 실패/취소 항목을 0바이트로 처리하면 안 된다.

## P2에서 구현할 부분

이 API는 단일 항목 조회다. 도구 경로 발견, 경계 안의 자식 열거, 재귀 순회 중 경합/재검증, 제외된 항목 집계, hardlink/부모·자식 저장소 중복 제거, scan budget·취소 지연·UI는 P2 책임이다. raw 재귀 열거를 안전한 것으로 간주하거나 검사 후 경로를 다시 열어 내용을 읽는 데 이 결과를 사용하지 않는다. 첫 세로 구현은 pip/uv fixture → 경계 검사를 사용하는 제한된 순회 → 부분 결과 집계 → WPF 표로 진행한다.

## 근거

크기 및 link 정보는 Microsoft의 [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info) 정의와 [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandleex)를 따른다. 실제 검증과 삭제 기능의 미해결 조건은 [P1 종료·P2 인계](../milestones/P1-closeout.md)를 따른다.

## P2 구현 상태 (2026-09-27)

단일 항목 계약에 `ReadDirectory`와 `ReadScanMetadata`를 추가했다. 전자는 root/디렉터리 ID를 검증하고 pinned directory handle에서 열거하며, 후자는 시작 root 및 열거된 항목의 ID와 다시 비교한다. 발견·순회·집계·UI의 구현 계약은 [ADR-003](ADR-003-readonly-discovery.md), 검증은 [P2 결과](../milestones/P2-results.md)를 따른다. 위 P2 책임 항목을 이제 구현했지만 관찰 결과가 변경 권한으로 바뀌는 것은 아니다.
