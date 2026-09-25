# Windows P1 capability matrix

2026-09-25. “검증”은 합성 fixture의 현재 Windows build 결과이며 제품 출시 승인이 아니다.

| 대상/상황 | 구현 정책 | 실제 검증 |
|---|---|---|
| Windows 11 x64 / NTFS / 로컬 고정 디스크 | P1 지원 표면 | build 26200, C 볼륨 |
| 단일 파일 ≤64 MiB / 빈 파일 | Inspect, internal 격리·복원 | 통과 |
| 한글·공백·260자 초과 경로 | 핸들 기반 처리 | 통과 |
| 같은 NTFS 볼륨의 격리·복원 | exact file handle rename | 통과, 별도 프로세스 복원 |
| 원본 ACL·alternate data stream | 동일 객체 이동으로 보존 | 통과 |
| 루트 자체·형제 prefix·`..`·UNC·device path·ADS 요청 | 거부 | 통과 |
| Windows/Program Files/ProgramData/전체 프로필 root | 거부 | Windows·프로필 실제 거부, 나머지는 동일 정책 |
| junction/root alias/hardlink | 거부 | 통과 |
| case-sensitive 디렉터리 | 거부 | 실제 flag 설정 후 거부 확인 |
| offline flag | 거부, 데이터 읽기 전 검사 | 실제 속성 설정 후 거부 확인 |
| 실제 OneDrive provider placeholder | 거부 정책 | 실제 provider 미검증 |
| 64 MiB 초과 / 폴더 재귀 변경 | 미지원 | 거부 통과 |
| 접근 거부 / 독점 잠금 | AccessDenied / Busy 구분 | ACL deny 및 잠금으로 통과 |
| snapshot 이후 파일·root·내용 변경 | TargetChanged | 통과 |
| syscall 직전 취소 | Cancelled, 원본 유지 | 통과 |
| 복원 위치에 파일 등장 | DestinationExists, 양쪽 보존 | commit 직전 충돌 주입 통과 |
| 파일·부모·격리 폴더 동시 이름 변경 | 핸들로 차단 | 40회 × 4개 동시 시도 통과 |
| 격리 폴더 reparse retag | 상대 핸들 이동이 외부 경로를 사용하지 않음 | 직접 태그 주입, 외부 sentinel 보존 |
| ReFS/FAT/external/network/cross-volume | 미지원, copy fallback 없음 | 코드 guard; 별도 물리 디스크 미검증 |
| Shell 휴지통 사용 가능/비활성/한도 초과 | 모든 경우 CanRecycle=false | 요청 거부와 격리 payload 보존만 검증 |
| 실제 휴지통 quota-full/disabled 처리 | backend 미연결 | 미검증, 시스템 설정 변경하지 않음 |
| 영구 삭제 | API 없음, false | capability와 변경 없음 확인 |
| 재부팅/전원 손실/write-ahead 원장 | P3 선행 필요 | 미검증 |
| 실제 사람 승인/MCP/실제 캐시 | P3/P4 선행 필요 | 미구현 |

P2 읽기 전용 탐색을 진행할 수 있다. P1 실험의 64 MiB 내용 해시 제한을 대형 저장소 스캐너의 크기 조회 제한으로 그대로 복사하지 않는다. metadata-only enumeration 계약은 P2에서 별도로 구현한다. 제품 삭제·Shell 휴지통 capability를 열려면 승인·원장과 추가 Windows 검증 게이트를 통과해야 한다.
