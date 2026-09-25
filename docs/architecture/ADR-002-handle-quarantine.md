# ADR-002 — 핸들을 유지하는 격리와 휴지통 capability 차단

상태: 채택 · 2026-09-25 · P1 기반 실험의 범위 결정

## 문제

검사한 파일 핸들을 닫고 Shell에 경로를 넘기면 그 사이 대상이 교체될 수 있다. 이름 재확인만 추가해도 마지막 검사와 실제 변경 사이에 경합이 남는다. Shell이 파일을 옮길 수 있게 delete 공유를 허용하면 제3자의 이름 변경도 허용될 수 있다.

## 결정

P1 Windows 모듈은 **검사한 원본 파일 핸들을 그대로 유지한 NTFS 동일 볼륨 격리와 원본 복원**을 구현한다. Shell 휴지통·영구 삭제·cross-volume copy는 구현하지 않고 capability를 false로 반환한다. 이는 디스크 공간 회수 기능이 아니며, 격리된 바이트를 확보 용량으로 계산하면 안 된다.

1. source/root/모든 조상을 볼륨부터 순서대로 연다. reparse/offline/recall/case-sensitive 경로와 별칭을 거부한다.
2. 조상 디렉터리는 `FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES`, share read/write로 열고 delete 공유는 허용하지 않는다. 속성 전용 핸들만으로는 빈 폴더 이름 변경을 막지 못하는 것이 실험에서 확인되었다.
3. 원본 파일은 read+DELETE 권한, share read만 허용한다. 소유 파일·볼륨 ID, 모든 조상 ID, 크기·mtime·SHA-256을 이전 snapshot과 대조한다.
4. 격리 위치도 같은 검사를 거쳐 핸들을 유지한다. 소스 범위와 격리 범위는 서로 포함할 수 없다.
5. `NtSetInformationFile(FileRenameInformation)`에 파일 핸들과 destination directory 핸들, **단일 상대 파일명**을 전달한다. 경로 기반으로 destination을 다시 열지 않는다. ReplaceIfExists=false다.
6. syscall 성공이 commit point다. 그 전에 취소하면 이동하지 않는다. 성공 후 취소를 “미실행”이라고 보고하지 않는다.
7. 복원도 같은 방법을 쓰며 원본 조상과 격리 파일 ID를 확인한다. 이름 충돌은 커널이 원자적으로 거부한다. 별도 파일명은 사용자가 선택할 후속 UX에 연결할 수 있지만 자동 덮어쓰기는 없다.

디렉터리의 write 공유를 완전히 차단하면 rename이 내부적으로 필요로 하는 target directory 접근도 실패한다. 대신 destination **객체 핸들에 상대적인** 이동을 사용한다. 열린 격리 폴더에 junction 태그를 주입하는 실험에서도 외부 경로 변경이 없음을 검증했다. 이 실험은 현재 Windows build에 대한 증거이며 모든 파일시스템 필터나 악성 관리자까지 방어한다는 주장이 아니다.

## 범위와 신뢰 경계

- Windows x64, 검사된 로컬 NTFS, 단일 regular file 64 MiB 이하. 대형 모델의 전체 해시 비용을 무제한 지불하지 않도록 P1의 실험 표면을 제한했다.
- 격리 파일은 원본 ACL·ADS를 유지한다. “격리”는 되돌릴 수 있는 위치 변경이라는 의미이며 동일 사용자 권한의 악성 코드로부터 비밀을 지키는 저장소가 아니다.
- 공개 API는 읽기 전용 `WindowsBoundary.Inspect`다. 변경 primitive는 internal이며 테스트 프로젝트에만 노출한다. 실제 앱/에이전트에서는 호출할 수 없다.
- P3의 승인·write-ahead 원장·크래시 복구가 없으므로 제품 변경 기능은 계속 false다.
- 파일 내용이 이미 존재하는 writable memory mapping을 통해 변경되는 경우까지 불변임을 보증하지 않는다. P3 도구 사용 상태 검사와 별도 검증 없이는 live 저장소에 연결하지 않는다.
- receipt JSON은 테스트 프로세스 사이의 전달용이다. 신뢰된 승인 토큰이나 영속 원장이 아니다.

## 검토한 대안

| 방법 | 결정 |
|---|---|
| 검사 후 핸들을 닫고 Shell로 이동 | 대상 교체 경합 때문에 거부 |
| share-delete를 허용한 채 Shell로 이동 | 다른 호출자의 이름 변경도 허용될 수 있어 거부 |
| 절대 destination 경로로 Win32 rename | 작동하지만 namespace 재해석을 줄이기 위해 상대 NT 핸들 방식 선택 |
| 디렉터리 write sharing 전체 차단 | rename 자체가 sharing violation으로 실패하여 거부 |
| 정확한 파일 핸들 + 디렉터리 핸들 상대 rename | 현재 지원 표면에서 선택, NT API 호환성 검증 지속 필요 |

## 공식 근거

NT rename은 원본 DELETE 접근과 destination 디렉터리 권한을 요구하며, RootDirectory로 상대 이름을 지정할 수 있다. ReplaceIfExists=false는 충돌 시 실패한다. [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information), [NtSetInformationFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile)

Win32 rename 구조와 API도 비교 실험에 사용했다. [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info), [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
