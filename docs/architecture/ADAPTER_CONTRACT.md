# Adapter 및 안전 정책 계약 v0.1

## 지원 표

아래는 목표 범위다. 실제 지원 버전은 P2/P3 검증 후 adapter manifest에 기록한다. P0에서 실환경 지원 완료로 표시하지 않는다.

| 도구 | 발견 근거 | 첫 기능 | 변경 전 추가 조건 |
|---|---|---|---|
| pip | interpreter별 `pip cache dir` | P2 읽기 | 버전·범위·출처·동시 실행 계약 |
| uv | 도구의 cache dir와 설정 | P2 읽기 | 공식 clean 범위·복구 표현·실행 중 계약 |
| npm | config 및 인덱스 | P2 읽기 | provenance/로컬·비공개 산출물 검증 |
| Ollama | 모델 설정과 로컬 API/CLI | P2 읽기, P4 언로드 | 모델 제거는 후속, 공유 blob 직접 삭제 금지 |
| Hugging Face | 환경 설정·캐시 레이아웃 | P2 읽기 | 정확한 revision/shared blob 참조·버전 검증 |
| Docker | 로컬 context/endpoint/builder | P2 읽기 | 변경은 후속, volume/container 보호 |

## 공통 필드

Artifact: stable item ID, owner, environment, root/object ID, 논리/할당/회수 추정 크기, 분류, 근거, 사용 상태, 사용자 자산 여부, 관측 시각. `null` 크기는 unknown이며 0과 다르다. 공유 크기는 owner 합계와 physical 합계를 따로 계산한다.

Evidence: kind, source, artifact identity, observedAt, verification result, requiresAuthentication. 토큰이나 파일 내용을 저장하지 않는다.

Capability: read/plan/recycle/vendorClean/restore 각각의 지원 여부와 검증한 도구 버전. 기본값은 read 외 false. 모르는 버전은 변경 false로 내려간다.

## 정리 후보 정책

하나라도 충족하지 못하면 후보에서 제외한다: 재생성 증명, 사용자 자산 아님, native/local 범위, 사용 중 아님, 사용 상태 확인 가능, 근거 미만료, 적절한 변경 capability. 증명됐어도 P0 release gate에서는 변경 계획을 생성할 수 없다.

후보 가능은 실행 승인이 아니다. executor는 P1 경계·P3 승인/원장·직전 재검증을 별도로 통과해야 한다. `IsCandidate=true`를 삭제 API 호출 조건으로 직접 사용하지 않는다.

## 오류 계약

NotInstalled / AccessDenied / UnsupportedLayout / UnsupportedVersion / Busy / UnknownUsage / EvidenceUnavailable / TargetChanged / ApprovalRequired / Expired / PartialFailure를 구분한다. 도구 오류·미설치·네트워크 실패를 안전한 빈 저장소로 반환하지 않는다. 모든 CLI는 검증된 executable+인자 배열로 실행하고 timeout/cancellation/output limit을 공통 적용한다.
