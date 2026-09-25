# Windows fixture integration checks

```powershell
./scripts/Validate.ps1 -WindowsIntegration
```

Windows build 22000 이상 x64 및 로컬 NTFS가 필요합니다. 실제 검증 환경은 결과 JSON에 기록됩니다. 현재 계정이 소유한 `artifacts/p1/<run-id>` 안의 synthetic fixture만 생성·이동합니다. 테스트가 만든 파일의 ACL deny, offline flag, 디렉터리 case-sensitive flag, junction을 사용하고 정상 finally 경로에서 변경한 속성/ACL을 복원합니다. 강제 종료 시 fixture를 보존하며 사용자 저장소는 건드리지 않습니다.

마지막 검사에서는 검증 핸들이 열린 상태에서 파일/부모/격리 폴더 이름 변경과 파일 쓰기를 별도 스레드로 40회 반복 시도합니다. 초기 실패를 숨기는 재시도는 하지 않습니다. 별도 프로세스 복원에는 fixture receipt JSON을 사용하며 제품 승인·원장으로 취급하지 않습니다.

결과는 `artifacts/p1/latest.json`과 각 run의 `results.json`에 저장됩니다. 현재 결과에 나타난 실패는 테스트 실패이며 지원 불가 상황으로 묵살하지 않습니다. 공유용 JSON은 경로 없는 결과만 docs/evidence로 복사합니다. fixture를 자동 재귀 삭제하거나 휴지통에 보내지 않습니다.

SDK/계정의 제한으로 상위 디렉터리 핸들 접근이나 case-sensitive flag 설정이 거부되면 실제 검증을 완료한 것으로 기록하지 마세요. 샌드박스 밖의 승인된 테스트 실행이 필요할 수 있습니다.

현재 Shell backend는 비활성화되어 있으므로 이 suite의 recycle 거부 테스트는 실제 휴지통 quota-full/disabled 설정 검증을 대신하지 않습니다. 이 gate와 writable memory mapping·크래시 원장·실제 승인 UI는 제품 삭제 허용 전 별도 검증 대상입니다.
