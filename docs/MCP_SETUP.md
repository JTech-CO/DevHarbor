# DevHarbor MCP 연결

2026-09-27 · 개발 빌드용 설정이다. P5 설치 패키지용 설정은 아직 제공하지 않는다. 클라이언트 설정을 앱이 자동 수정하지 않는다.

## 빌드와 공유

1. 저장소에서 `./scripts/Start-DevHarbor.ps1`로 Release 빌드와 앱 실행을 진행한다. .NET 10 SDK가 필요하다.
2. 앱에서 스캔을 실행한다. ‘에이전트 연결’ 창에서 경로 메타데이터 공개 안내를 확인하고 ‘스캔 결과 공유 시작’을 누른다.
3. 같은 창에 표시되는 실제 command/args를 해당 클라이언트 설정에 복사한다. command는 저장소의 `.tools/dotnet/dotnet.exe`가 있으면 이를 사용하고 아니면 PATH의 `dotnet`이다. args는 `src/DevHarbor.Mcp/bin/Release/net10.0-windows/DevHarbor.Mcp.dll`의 절대 경로다. command에 따옴표 자체를 포함하지 않는다.
4. 클라이언트에서 연결 상태와 네 도구를 확인한다. 스캔이 10분 이상 지났으면 다시 스캔한다. 공유 중지 또는 주 앱 종료 시 더 이상 결과를 제공하지 않는다.

클라이언트 실행 명령에 `dotnet run`을 넣지 않는다. 빌드 로그가 stdout의 MCP 메시지와 섞일 수 있으므로 먼저 빌드한 DLL을 실행한다. Desktop 출력 디렉터리의 복사된 apphost 대신 MCP 프로젝트의 출력 DLL을 사용한다.

## 설정 형태

아래 `C:/path/to/DevHarbor`는 예시이므로 앱에 표시된 실제 경로로 교체한다. 기존 서버 항목을 유지하면서 devharbor 항목만 병합한다.

Claude Code 및 Cursor JSON:

```json
{
  "mcpServers": {
    "devharbor": {
      "type": "stdio",
      "command": "C:/path/to/DevHarbor/.tools/dotnet/dotnet.exe",
      "args": ["C:/path/to/DevHarbor/src/DevHarbor.Mcp/bin/Release/net10.0-windows/DevHarbor.Mcp.dll"]
    }
  }
}
```

Claude Code는 공식 문서의 stdio 서버 등록 방식으로 command/args를 사용한다. `claude mcp get devharbor`로 상태를 확인한다. Cursor는 프로젝트 `.cursor/mcp.json` 또는 사용자 `~/.cursor/mcp.json`의 mcpServers에 적용하는 형식이다. Cursor의 실제 연결은 이번 환경에서 검증하지 못했다.

Codex `config.toml`:

```toml
[mcp_servers.devharbor]
command = "C:/path/to/DevHarbor/.tools/dotnet/dotnet.exe"
args = ["C:/path/to/DevHarbor/src/DevHarbor.Mcp/bin/Release/net10.0-windows/DevHarbor.Mcp.dll"]
```

공백·한글 경로는 command와 args 각각의 문자열로 유지한다. shell 래퍼나 연결 문자열은 필요하지 않다. [Claude Code 공식 설정](https://code.claude.com/docs/en/mcp), [Codex 공식 설정](https://learn.chatgpt.com/docs/extend/mcp?surface=cli), [Cursor 공식 설정](https://cursor.com/docs/mcp)을 기준으로 했다.

## 사용 순서와 한계

`devharbor_overview` → `devharbor_items` → 선택한 item ID와 새 requestId로 `devharbor_plan` → `devharbor_plan_status` 순서로 호출한다. 모든 계획은 현재 Blocked다. 앱에서 차단 근거를 확인하면 reviewed가 true가 되지만 실제 정리 승인이 되지는 않는다. 모델 언로드는 앱의 별도 ‘모델 세션’ 화면에만 존재한다.

MCP 연결 성공은 저장소 공유 동의가 아니다. 공유를 켜지 않으면 SharingDisabled 또는 AppUnavailable 오류가 정상이다. SnapshotExpired는 재스캔, StaleCursor/StaleItem은 항목 재조회, RequestConflict는 새 requestId가 필요하다. stdout에는 진단 문자열을 추가하지 않는다. 앱 실행 또는 설정 등록만으로 모델 추론을 요청하지 않는다.

[검증한 클라이언트 버전 및 범위](milestones/P4-results.md) · [전체 요청·보안 계약](architecture/ADR-005-agent-model-sessions.md)
