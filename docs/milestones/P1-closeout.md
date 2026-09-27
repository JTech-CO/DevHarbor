# P1 종료와 P2 인계

2026-09-27 · **P1 기반·읽기 경계 완료 / P2 착수 GO / 제품 삭제 NO-GO**.

이름을 변경한 `DevHarbor` 작업 폴더에서 빌드와 통합 검증을 다시 수행했다. ADR-002의 제한된 P1 범위를 완료하고 읽기 전용 P2에 필요한 조회 계약을 추가했다. 최초 계획에 포함됐던 실제 Shell 휴지통 검증은 통과한 것으로 표시하지 않고 아래의 제품 변경 차단 조건으로 계속 관리한다.

## 완료 증거

- Release 빌드: 경고 0, 오류 0. Domain 정책 **12/12**, Windows 통합 **34/34** 통과.
- 기존 24개 경계·격리·별도 프로세스 복원·경합 검사 재통과.
- 추가 10개 검사: 데이터 읽기를 거부한 65 MiB 파일의 속성 조회, 5 GiB sparse 크기 구분, hardlink 동일 ID, 디렉터리 null/빈 파일 0, 경계·경로 거부, junction/offline, case-sensitive, 접근 거부/사라진 파일, 취소/핸들 해제, 한글·긴 경로/관찰 시각.
- 실제 사용자 캐시 대신 합성 fixture만 사용. 휴지통 설정과 사용자 데이터는 변경하지 않았다.
- 공개 조회와 internal 변경 primitive를 분리했고 모든 제품 삭제 capability는 false다.

실행: `./scripts/Validate.ps1 -WindowsIntegration`. 로컬 환경과 항목별 결과는 [2026-09-27 증거](../evidence/p1-windows-2026-09-27.json)에 기록했다. 기존 설계 판단과 첫 CI 증거는 [P1 결과](P1-results.md)에 보존한다.

## P2 진입 체크

- [x] 대형 파일의 내용을 읽지 않는 단일 항목 메타데이터 조회.
- [x] 지원 경계·실패·취소·미측정 값·하드링크 계약.
- [x] 기존 변경 primitive의 보호 검증 회귀 없음.
- [x] [읽기 계약](../architecture/READONLY_BOUNDARY.md)과 [capability matrix](../architecture/WINDOWS_CAPABILITIES.md) 인계.
- [x] P2에서 구현할 발견·순회·중복 집계·UI 범위 명시.

## 제품 변경 활성화 전 남은 차단 조건

| ID | 조건 | 담당 단계 / 현재 상태 |
|---|---|---|
| D1 | Shell handoff 중 경합 방지, 실제 휴지통 사용 가능/disabled/quota-full 처리와 원본 보존 | P3 시작 시 backend 지원 여부 결정, 구현·검증 전 CanRecycle=false |
| D2 | 단회·만료·대상 변경에 묶인 신뢰된 사람 승인, 위조/재사용 차단 | P3, 미구현 |
| D3 | write-ahead 원장, 단계별 프로세스 종료·재시작 대조, 복원 충돌 | P3, 미구현 |
| D4 | writable memory mapping 및 live 도구 저장소 사용 상태와 변경 경합 | P3, 미검증 |
| D5 | 64 MiB 초과/디렉터리 변경을 지원하려면 별도 identity·복구 전략 검증 | 현재 변경 미지원; P3에서 지원 확대 여부 결정 |

cross-volume·클라우드 placeholder·다른 파일시스템은 미지원으로 유지한다. 지원을 넓히려면 해당 환경의 검증이 필요하다. D1을 구현하지 않으면 Shell 기능은 계속 제공하지 않는다. D2/D3만 완료했다고 파일 삭제를 열 수 없다. 격리 이동은 공간 회수로 계산하지 않는다. 이 문서의 완료는 P2 착수에 필요한 P1 범위의 종료이며 클리너 출시 승인이 아니다.
