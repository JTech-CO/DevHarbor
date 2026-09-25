# DevHarbor Windows 안전성 실험 보고서

2026-09-25 · 실험 단계, 제품용 삭제 기능 비활성화

## 결과

**자동 검사 22개 통과.** 파일 경계 13개, 승인 상태 전이 8개, 실제 Shell 휴지통 왕복 시나리오 1개다. 마지막 시나리오에는 사전 삭제 거부, 별도 프로세스 복원, 충돌 방지, SHA-256 검사가 포함된다.

환경: Windows build 26200, x64 호스트, PowerShell 7.6.5, 현재 사용자의 로컬 C 드라이브. .NET SDK는 설치되어 있지 않아 PowerShell `Add-Type`으로 C#을 컴파일했다. 다른 PC·파일시스템·Windows 버전의 결과로 일반화하지 않는다.

실행 명령:

```powershell
./prototypes/windows-safety/Run-Probes.ps1 -IncludeRecycle
```

증거:

- [개인 경로를 제외한 실행 증거](evidence/windows-probe-2026-09-25.json)
- 성공 실행 ID: `df290895e57e4de49c0ad91d4212ba01`
- 해당 실행 디렉터리: `prototypes/windows-safety/runs/df290895e57e4de49c0ad91d4212ba01/`
- 그 안의 `results.json`, `recycle-manifest.json`, 복원된 fixture를 보존했다.
- 매니페스트 최종 상태: `Restored`, `vetoVerified=true`, `collisionVerified=true`.

원본 SHA-256과 복원 SHA-256은 다음 값으로 일치했다.

```text
3FB953E3DE29BCB89E32ECC965E698AF6CF4B093D17347D53146D6BC02639461
```

첫 샌드박스 실행은 사용자 홈 조상 디렉터리의 Win32 핸들 열기가 거부되어 정상 경로부터 실패했다. 이를 통과로 해석하지 않고, 테스트 파일만 대상으로 하는 권한 확장 실행을 거쳐 재검증했다. 제품에서 UAC 관리자 권한이 필요하다는 결론은 아니다. 최초 실패 기록도 별도 run에 남아 있다. 원시 run과 최신 JSON에는 로컬 경로가 있어 Git에서 제외하고, 공유 증거에는 테스트 이름·상태·환경만 보존한다.

## 파일 경계 검사

| 시나리오 | 결과 |
|---|---|
| 한글·공백이 포함된 정상 파일 | 허용 |
| 허용 root 자체, 비슷한 prefix의 형제 폴더 | 거부 |
| `..` 경로, ADS, UNC | 거부 |
| 존재하지 않는 파일, 디렉터리 | 거부 |
| 바깥 폴더로 이어지는 junction | 거부 |
| hardlink가 여러 개인 파일 | 거부 |
| 독점 잠금된 파일 | 거부 |
| 검증 lease를 유지하는 동안 파일 쓰기 | OS가 거부 |
| 검증 lease를 유지하는 동안 부모 폴더 이름 변경 | OS가 거부 |

실제 Win32 `CreateFileW`, `GetFileInformationByHandle`, `GetFinalPathNameByHandleW`를 사용한다. 조상 디렉터리를 볼륨 root부터 열고 reparse/offline/recall 속성을 거부하며, 볼륨·파일 ID·크기·수정 시각을 계획 fingerprint에 포함한다. 파일 내용 digest는 대용량 모델에 대한 일반 정책으로 넣지 않았다.

검증 중 핸들의 공유 모드가 쓰기/이름 변경을 제한함은 실제 Windows 동작으로 확인했다. [CreateFileW 공유 모드](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)

**남은 핵심 문제:** 현재 lease가 열린 동안에는 Shell도 해당 파일을 이동할 수 없다. lease를 닫고 Shell로 넘기는 사이의 경합을 이 실험은 해결하지 못했다. 따라서 경계 검사와 실제 휴지통 작업은 각각의 검증이며, 안전한 통합 삭제 엔진 완성으로 간주할 수 없다. 타임스탬프를 보존하는 내용 변경 탐지, case-sensitive 디렉터리, 조상 경합 스트레스, 다중 볼륨, OneDrive 실제 환경도 남아 있다.

## 휴지통 복원

실험 파일 이름은 `devharbor-fixture-<GUID>.txt`이고 내용은 고정된 실험 표식과 무작위 값이다. 실제 캐시·모델·문서를 사용하지 않았다.

1. fixture 경계 확인 및 의도 매니페스트 저장.
2. `PreDeleteItem`이 거부하도록 실행해 파일이 그대로 남는지 확인.
3. `IFileOperation`의 recycle 플래그로 실제 휴지통 이동.
4. callback에서 휴지통 항목 경로를 받아 매니페스트에 저장, 원래 경로 부재 확인.
5. 첫 프로세스 종료 후 새 STA PowerShell 프로세스 시작.
6. 기록된 휴지통 파일의 SHA-256 확인.
7. 원래 위치에 새 fixture를 생성한 상태에서 복원을 시도하고 덮어쓰기 거부 확인.
8. 별도 복원 이름으로 Shell 이동 후 원본 해시 일치 및 휴지통 payload 부재 확인.

기존 휴지통 전체를 비우거나 다른 항목을 변경하지 않았다. [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation), [PreDeleteItem 취소 계약](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem)

**아직 검증하지 않은 것:** 휴지통 비활성화/한도 초과, 디스크 부족, 대형 폴더, 다중 항목 부분 실패, Explorer 재시작·재부팅, 이동 후 매니페스트 기록 전 크래시, 휴지통 외부 비우기, 목적지 충돌 경합, ACL/ADS 보존. JSON 매니페스트는 원자적 원장이나 보안 저장소가 아니므로 제품에 그대로 사용할 수 없다. `TSF_DELETE_RECYCLE_IF_POSSIBLE` 검사만으로 모든 환경에서 영구 삭제 방지를 보증하지 않는다.

## 승인 흐름

| 시나리오 | 결과 |
|---|---|
| 미승인 실행 | 거부 |
| 거절 후 실행 | 거부 |
| 다른 계획 digest로 승인 | 거부 |
| 승인 후 유효기간 만료 | 거부 |
| 승인 후 대상 파일 수정 | 실행 전 거부 |
| 승인 후 같은 경로의 파일 교체 | 실행 전 거부 |
| 승인한 무해한 callback 실행 후 재사용 | 첫 실행 완료, 두 번째 거부 |
| executor 실패 후 재실행 | Failed 상태, 재승인 없이 실행 불가 |

테스트는 `DecideFromTestUI`를 직접 호출하는 **가짜 UI 승인 주체**를 사용한다. 실제 사람의 승인을 얻었다는 뜻이 아니다. 실행 callback도 삭제 기능이 없는 동작이다. WPF 수동 확인창 샘플은 `Show-ApprovalDemo.ps1`로 제공하며 실제 삭제와 연결하지 않았다. 이번 작업에서 사람이 창을 조작하는 검증은 수행하지 않았다.

제품용 승인 broker, 현재 사용자 ACL, named pipe 클라이언트 신원, MCP 공격/동시 요청, 계획 영속성·변조, 재시작 후 승인 무효화는 후속 통합 검증 대상이다. 현재 공개 테스트 메서드를 production 승인 API로 사용하면 안 된다.

## 결론과 다음 작업

확인한 것은 **현재 환경의 단일 fixture에서 경계 제한, 휴지통 이동·복원, 계획에 대한 승인 상태 전이가 동작한다**는 것이다. 사용자 캐시를 안전하게 지우는 제품이 완성되었다는 의미는 아니다.

다음 구현 게이트는 경계 검사와 Shell 실행 사이의 경합 해결 → 크래시 복구 원장 → 실제 승인 UI/IPC → 읽기 전용 도구 adapter 순서다. 이를 통과하기 전에는 실험 코드를 사용자 캐시 정리에 연결하지 않는다.
