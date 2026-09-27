# ADR-004 — 세션 승인과 SQLite 복구 원장

채택 · 2026-09-27 · P3 기반 구현. 실제 캐시 정리 활성화 결정은 보류한다.

## 실행 범위

`Execution`은 불변 계획·세션 승인·실행 직전 검사·대조를, `Ledger`는 SQLite 트랜잭션을 담당한다. WPF가 두 모듈을 연결한다. `Windows`의 핸들 기반 이동은 internal로 유지하며 Desktop에 직접 공개하지 않는다. public 표면은 읽기 전용 `CleanupEligibility.Assess`뿐이다. `CanExecute`는 모든 실제 저장소에서 false다.

제품에서 변경할 수 있는 파일은 `%LOCALAPPDATA%/DevHarbor/Workflow/samples/<GUID>/input/sample.txt`에 앱이 직접 생성한 샘플 하나뿐이다. 인접한 `held` 폴더로 이동하고 원래 폴더로 복원한다. 사용자 지정 경로·CLI 명령·폴더 전체·실제 pip/uv 캐시는 변경할 수 없다. 같은 볼륨에서 위치만 바꾸므로 회수 공간은 0이다. Shell 휴지통과 영구 삭제 capability는 false다. P1의 NTFS·단일 일반 파일·64 MiB 이하 조건을 유지한다.

보관 파일명은 핸들 검사 후 생성한 무작위 식별자이며, 승인창에는 정확한 원본과 도착 **폴더**를 표시한다. 실제 도착 파일명은 intent 원장에 먼저 확정한다. 복원은 정확한 원본 보관 파일과 도착 파일명을 확인한다. 충돌 시 덮어쓰지 않으며, 다른 파일명을 사용하려면 새 계획과 새 승인이 필요하다.

## 계획과 승인

계획은 사용자 SID, Windows 세션, 현재 broker 세션, 작업 종류, 원본/도착 범위, 파일 ID·볼륨·크기·mtime·SHA-256, 부모 identity, 복원 receipt, 생성/만료 시각을 포함한다. 직렬화한 원문과 SHA-256을 DB에 함께 저장한다. 계획 수명은 2분, 승인 수명은 최대 30초다. 재시작한 프로세스에서 예전 승인은 사용할 수 없다.

확인창은 미체크·승인 버튼 비활성 상태로 시작하고 Enter 기본 버튼으로 승인하지 않는다. 체크만 해도 토큰이 나오지 않는다. 명시적 승인 클릭에서만 opaque 객체를 발급한다. broker는 객체 참조·무작위 nonce·계획 ID·digest·SID·세션·만료를 확인하고 단회 소비한다. 실행 직전과 intent 확정 직후에도 유효기간을 검사한다. 파일 스냅샷은 열린 핸들에서 재검사한다.

승인 발급·실행은 public API나 직렬화 메시지로 공개하지 않는다. friend assembly는 Desktop 및 검증 프로젝트로 제한한다. 테스트의 직접 발급은 엔진 계약 검증이며 사람 승인의 증거가 아니다. 이 경계는 앱 내부/API 오용 방지를 위한 것으로, 같은 사용자 권한의 코드 주입·reflection·UI 자동화·디버거·관리자를 격리하는 보안 샌드박스가 아니다. MCP는 이 broker를 직접 호출하거나 승인 객체를 발급할 수 있는 표면을 갖지 않는다. P4에서도 사람 확인 UI 없이 변경을 활성화할 수 없다.

## 저장과 경합

SQLite는 WAL, `synchronous=FULL`, 외래 키, `quick_check`를 사용한다. 연결은 작업 단위이며 pooling을 끈다. 현재는 `plans`, `operations`, `reservations`, `events` 네 테이블을 사용하는 초기 스키마다. 기존 DB 손상을 지우거나 재생성하지 않는다. 샘플이 남은 상태에서 DB가 없어졌다면 `LedgerMissing`으로 중단한다.

상태 디렉터리 최초 생성은 기존 조상부터 핸들을 고정한 뒤 진행하고 새 디렉터리는 현재 SID와 SYSTEM 권한으로 제한한다. 작업마다 상태 루트의 경계를 다시 검사한다. SQLite 본체·WAL·SHM·journal의 기존 reparse/다중 링크를 거부한다. SQLite leaf 파일을 전체 수명 동안 독점 고정하지는 않으므로 같은 사용자에 의한 악의적 DB 교체를 방어했다고 주장하지 않는다. 샘플 생성 역시 각 조상을 검사·고정하고 `CreateNew`로 파일을 생성한다.

사용자 SID와 상태 경로의 해시로 이름 지은 Global mutex가 실행·대조·계획 쓰기를 직렬화한다. 다른 프로세스/세션이 점유하면 `ConcurrentOperation`으로 거부하고, 프로세스 종료로 버려진 mutex만 회수한다. 열린 intent가 있으면 새 파일 이동을 막는다. 파일 ID 기반 reservation을 intent 트랜잭션에서 확보하며 보관 중에는 유지한다.

## 변경 순서와 복구

1. 계획 원문·승인·scope를 확인하고 단회 승인을 소비한다.
2. P1 핸들·내용 검사 후, 계획 Running + 대상 reservation + 작업 Intent + 이벤트를 한 트랜잭션으로 확정한다.
3. 승인의 만료와 취소를 재검사한 뒤 핸들 상대 rename을 수행한다.
4. Held/Restored/NotApplied 결과와 이벤트를 확정한다. 복원 시 부모 보관 작업/계획도 Restored로 갱신한다.

intent 전 기록 실패면 rename하지 않는다. rename 후 결과 기록 실패는 `RecoveryRequired`다. intent 이후 예외를 임의로 실패 확정하거나 자동 재시도하지 않는다. rename 완료 뒤의 취소도 완료를 되돌리지 않는다. UI 종료 요청은 진행 중 작업에 취소를 전달하고 상태 확정까지 창을 유지한다.

재시작 후 대조는 파일을 이동하지 않는다. 부모 identity·파일 ID·크기·mtime·해시와 양쪽 위치를 읽고 상태만 기록한다.

| 중단된 Intent의 실제 상태 | 대조 결과 |
|---|---|
| 원본 정확히 일치, 도착 없음 | NotApplied |
| 원본 없음, 도착 정확히 일치 | Held 또는 Restored |
| 양쪽 존재·변경·권한 오류·receipt 손상 등 | NeedsReview, 자동 복원 금지 |

이미 Held인 항목은 보관 payload가 정확히 일치하는지 재검사한다. 기존 원본 위치에 새 파일이 생겨도 보관 파일은 유지한다. 새 원본을 덮어쓰지 않는 별도 복원 계획으로 처리한다. NotApplied는 실패한 작업 reservation만 해제하고, Restored는 부모 보관 reservation을 해제한다. NeedsReview는 보수적으로 예약을 남긴다.

승인 전 취소한 계획과 생성 샘플은 보존한다. intent 없이 핸들 검사에서 반환한 NotApplied는 plans/events에 남는다. 승인 검증 거부는 화면 결과로 표시하며 별도 작업 이력으로 만들지 않는다. 화면의 이동 이력은 intent가 확정된 작업만 보여준다. 현재는 단일 파일·작업당 승인 방식이며 배치, 원장 압축/내보내기, 고아 샘플 자동 제거, 전원 손실 복구는 지원 범위 밖이다.

## 의존성과 근거

`Microsoft.Data.Sqlite` 10.0.12와 전이 의존성을 `packages.lock.json`에 고정하며 검증 스크립트에서 locked restore를 확인한다. 배포 전 네이티브 SQLite를 포함한 의존성 고지·취약점 검토는 P5에서 다시 수행한다.

- [Microsoft.Data.Sqlite 트랜잭션](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions): 여러 원장 변경을 한 트랜잭션으로 처리한다.
- [SQLite synchronous](https://sqlite.org/pragma.html#pragma_synchronous), [WAL](https://www.sqlite.org/wal.html): durability 설정은 프로세스 강제 종료 시험과 별도로 확인한다. 실제 전원 손실/스토리지 장치의 flush 보장은 이번 증거에 포함하지 않는다.
- [uv cache 안전성](https://docs.astral.sh/uv/concepts/cache/), [uv CLI](https://docs.astral.sh/uv/reference/cli/): 도구 전용 명령도 설치 연결 방식·사용 상태를 확인해야 한다. 스캔 완료만으로 `clean` 권한을 주지 않는다.
