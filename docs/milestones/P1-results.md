# P1 결과 — Windows 경계·격리 기반

2026-09-25 · 기반 구현과 평가 완료, **제품 삭제 게이트 NO-GO**

## 구현 결과

`DevHarbor.Windows`와 `DevHarbor.WindowsChecks`를 추가했다. source/root/조상/격리 디렉터리의 핸들을 유지하고 파일 ID·조상 ID·크기·mtime·SHA-256을 대조한다. 파일 핸들과 디렉터리 핸들에 상대적인 `NtSetInformationFile` rename으로 정확한 객체를 이동한다. restore는 덮어쓰기를 커널 단계에서 거부하고 변경된 원본 조상이나 격리 payload를 감지한다.

공개 표면은 읽기 전용 검사다. 실제 이동 함수는 internal test surface이며 승인/원장이 없는 상태로 제품에 연결하지 않는다. Shell 휴지통이나 영구 삭제 API로의 fallback은 없다. 사용자 파일·캐시를 스캔하거나 정리하지 않았다.

## 검증

Windows NT 10.0.26200.0 / x64 / 로컬 NTFS / .NET SDK 10.0.401.

```powershell
./scripts/Validate.ps1 -WindowsIntegration
```

- Release 빌드: 경고 0, 오류 0.
- Domain 정책: **12/12 통과**.
- Windows 통합: **24/24 통과**.
- 40회 반복, 매 회 파일 이동·source root 이동·quarantine root 이동·파일 쓰기의 4개 동시 시도를 차단.
- 별도 프로세스에서 receipt를 읽어 원본 복원, SHA-256/ACL/ADS 보존 확인.
- 복원 commit 직전 동일 이름의 새 파일을 생성해 kernel no-overwrite 확인.
- 열린 격리 폴더를 직접 junction으로 retag하는 시험에서 외부 sentinel 보존과 경계 밖 파일 생성 없음 확인.
- 실제 case-sensitive flag, offline 속성, ACL deny, hardlink, junction, 파일 잠금 fixture 사용.
- 취소·snapshot 변경·내용 변경·root 변경·복원 재사용·미지원 recycle 요청의 변경 차단 확인.

실행 증거: [p1-windows-2026-09-25.json](../evidence/p1-windows-2026-09-25.json). 원본 fixture와 각 실행 로그는 Git에서 제외한 `artifacts/p1/`에 남긴다. 공유 JSON에는 로컬 절대 경로를 포함하지 않는다.

원격 검증: 구현 커밋 `8ad28eb`의 [Windows CI 실행](https://github.com/JTech-CO/DevHarbor/actions/runs/36094085722)이 성공했다. `windows-2025` runner에서 Release 빌드·정책·Windows fixture suite를 실행했고 job은 41초에 완료했다. 로컬 Windows 11 결과와 호스팅된 Windows Server runner 결과를 구분한다.

## 검증 중 수정한 문제

속성 전용 directory handle은 빈 디렉터리 이름 변경을 막지 못했다. FILE_LIST_DIRECTORY를 요청하도록 수정했다. 가변 길이 native rename buffer의 문자열 종료 처리를 수정했다. 디렉터리 write sharing 전체 차단은 정상 rename 자체도 막았으므로, 절대 경로를 재해석하는 방식 대신 destination directory 객체에 상대적인 NT rename으로 변경하고 retag 테스트를 추가했다.

## 미통과/미검증 항목

Shell backend는 disabled다. 실제 휴지통 비활성화·한도 초과 상태, 경합 없는 Shell handoff는 **검증하지 않았으며 P1 전체 삭제 게이트가 남아 있다**. 테스트의 recycle 거부는 이를 대신하는 성공 증거가 아니다.

물리적 cross-volume 환경, 실제 OneDrive provider, writable memory mapping, 전원 손실/재부팅, write-ahead 원장, 신뢰된 사용자 승인도 미검증/후속 범위다. P1 대상은 단일 regular file ≤64 MiB이며 이 제한을 제품의 대형 모델 지원으로 오인하지 않는다.

## 다음 단계

ADR-002와 capability matrix에 따라 읽기 전용 P2는 진행할 수 있다. 실제 사용자 데이터 변경은 승인·원장뿐 아니라 미통과 Windows/Shell 게이트를 해소한 뒤에만 허용한다. 격리 이동은 디스크 용량 회수가 아니다.
