# P1 — Windows 파일 경계와 변경 안전성

상태: 기반 구축·안전성 평가 완료. **제품 삭제 게이트는 NO-GO**이며 읽기 전용 P2 진행만 허용한다. 선행: P0. 담당 모듈: Windows, Execution.

결정: [ADR-002](../architecture/ADR-002-handle-quarantine.md)에 따라 P1의 지원 표면은 단일 파일의 핸들 기반 격리·복원으로 제한한다. Shell로 넘기는 방식은 안전성을 확보하지 못했으므로 disabled capability로 남긴다. 이는 휴지통 실패 시 자동 격리 fallback이 아니라, 명시적으로 구분된 internal 격리 primitive다.

## 작업

1. root/조상/leaf 식별과 handle 수명, reparse·hardlink·placeholder 정책을 제품 인터페이스로 정리한다.
2. 현재 검증 lease와 Shell 이동 사이의 TOCTOU를 해결할 전략을 실험한다. 무검증 핸들 해제 후 경로 기반 삭제는 허용하지 않는다.
3. 정확한 객체를 확보해 격리한 뒤 Shell로 넘기는 방법 등 대안을 비교하고 원자성·ACL·동일 볼륨 조건을 문서화한다. 이를 확정된 해법으로 미리 가정하지 않는다.
4. 반복 경로 교체, 루트 교체, 다중 경로 별칭, case-sensitive 폴더, 잠금, 휴지통 비활성화·한도 초과를 검증한다.
5. 지원 불가 경로·변경 오류·취소의 항목별 결과를 반환한다.

## 산출물 및 게이트

Windows 통합 테스트와 실행 증거, filesystem capability matrix, ADR-002를 작성한다. fixture 경계 밖 변경 0건, 영구 삭제 fallback 0건, 복구 가능하다고 표시한 항목의 원본 복원 성공이 필요하다. 현재 22개 실험은 시작 자료이며 통과를 대체하지 않는다.

검증 실패 시 변경 기능을 비활성화한 채 P2 읽기 전용 기능만 진행한다. 휴지통 전체 비우기·시스템 경로·네트워크 드라이브는 범위 밖이다.

## 완료 판정

- [x] 파일/디렉터리 핸들을 유지하는 경계 및 이동 primitive.
- [x] same-volume handle-relative rename과 kernel no-overwrite 복원.
- [x] 동시 경로 교체, junction retag, case-sensitive, 잠금, ACL, 취소 검증.
- [x] item별 오류 결과, capability matrix, ADR 및 결과 문서.
- [x] 지원하지 않는 Shell/영구 삭제 요청을 변경 없이 거부.
- [ ] 실제 Shell 휴지통 disabled/quota-full 및 경합 없는 handoff 검증 — backend 미연결, 제품 변경 게이트 유지.

상세 증거는 [P1 결과](P1-results.md), 범위는 [Windows capability matrix](../architecture/WINDOWS_CAPABILITIES.md)를 따른다. 마지막 항목이 미통과이므로 P1 전체 삭제 기능이 완성됐다고 해석하지 않는다.
