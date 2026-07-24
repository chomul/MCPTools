# Task 0 체크리스트 — 기반 구축

> Task 문서: [Task0_기반구축.md](../tasks/Task0_기반구축.md) · 원본 계획: ../PLAN.md §4 Phase 0

## 1. 구현 체크리스트

- [x] 폴더 구조 생성 (`Assets/MCPTools/Editor|Runtime`, `Assets/Generated/*`, `Assets/Docs/`)
  - 구현 결과: 계획된 11개 폴더 전부 생성 (Editor 하위 단계별 폴더 + Workflows, Runtime/Data, Generated/Images·Audio·Candidates, Docs). .meta는 Unity가 생성하도록 두었음.
  - 검증 상태: 파일 시스템 확인 완료. 에디터에서 .meta 생성 확인 대기.
  - 관련 파일: MCPToolTest/Assets/MCPTools/, Assets/Generated/, Assets/Docs/
- [x] `MCPTools.Editor.asmdef` / `MCPTools.Runtime.asmdef` 작성 (Editor 전용 분리, Runtime의 UnityEditor 참조 금지)
  - 구현 결과: Editor asmdef는 includePlatforms:["Editor"] + Runtime 참조. Runtime asmdef는 참조 없음(역참조 구조상 불가). Runtime 코드(MCPToolsInfo.cs)는 using 없음 — UnityEditor/UnityEngine 무참조.
  - 검증 상태: Unity 6000.5.2f1 실제 DLL 참조 dotnet build로 컴파일 검증 통과 (오류 0, 경고 0). 에디터 컴파일 확인 대기.
  - 관련 파일: Assets/MCPTools/Editor/MCPTools.Editor.asmdef, Assets/MCPTools/Runtime/MCPTools.Runtime.asmdef, Assets/MCPTools/Runtime/Data/MCPToolsInfo.cs
- [x] `MCPToolSettings` ScriptableObject + 설정 인스펙터 (기본 에셋 자동 생성 포함)
  - 구현 결과: 필드 6종(서버 주소 기본 http://127.0.0.1:8188, 타임아웃 300초, 기본 워크플로, Generated/Docs 경로, 후보 개수 4). GetOrCreate()가 Assets/MCPTools/Editor/Common/MCPToolSettings.asset 자동 생성. 편집 UI는 MCPSettingsWindow가 담당.
  - 검증 상태: 컴파일 검증 통과. 에디터에서 에셋 자동 생성 확인 대기.
  - 관련 파일: Assets/MCPTools/Editor/Common/MCPToolSettings.cs
- [x] `ComfyUIClient` — CheckServerAsync / QueuePromptAsync / WaitForCompletionAsync / DownloadOutputAsync (async/await, 블로킹 금지)
  - 구현 결과: HttpClient 기반 async 4메서드. 요청별 타임아웃(linked CTS), 1초 폴링, HTTP 400 시 노드 검증 오류 본문 포함, 연결 실패 시 한국어 안내 메시지. 출력 수집은 images/audio/gifs 지원. 외부 JSON 라이브러리 없이 자체 MiniJson 사용.
  - 검증 상태: 컴파일 검증 통과. MiniJson은 history 응답 파싱·한글 왕복 등 콘솔 테스트 통과. 실서버 연동 테스트는 에디터 테스트에서 진행.
  - 관련 파일: Assets/MCPTools/Editor/Common/ComfyUIClient.cs, Assets/MCPTools/Editor/Common/MiniJson.cs
- [x] `McpToolRegistry` 뼈대 + 더미 도구 `mcptools_ping` (unity-mcp 설치·연결 확인)
  - 구현 결과: 이름→핸들러 레지스트리 + 공통 응답 포맷({success,message,data}), 예외는 success:false로 흡수. mcptools_ping 등록(버전/Unity 버전/서버 주소 반환). 로컬 검증용 메뉴 Tools/MCP/Ping (Local Test) 포함. MCP for Unity(com.coplaydev.unity-mcp, v10.1.0) 어댑터 완료 — `McpForUnityAdapter.cs`에 `[McpForUnityTool("mcptools_ping")]` 정적 클래스로 노출, 패키지 의존은 이 파일 하나에 격리. asmdef versionDefines(`MCPTOOLS_HAS_MCPFORUNITY`) + `#if`로 감싸 패키지 미설치 프로젝트에서도 컴파일 오류 없음(배포 자기완결성 유지).
  - 검증 상태: 컴파일 검증 통과. 에디터에서 MCP 클라이언트 경유 mcptools_ping 호출 확인 대기 (아래 테스트 목록).
  - 관련 파일: Assets/MCPTools/Editor/Common/McpToolRegistry.cs, Assets/MCPTools/Editor/Common/McpForUnityAdapter.cs, Assets/MCPTools/Editor/MCPTools.Editor.asmdef
- [x] 서버 연결 테스트 창 `Tools/MCP/Settings` (설정 편집 + "서버 연결 테스트" 버튼)
  - 구현 결과: IMGUI EditorWindow. 설정 필드 편집(Undo 지원) + 저장, async 서버 연결 테스트(연타 방지, 성공/실패 한국어 안내), 등록된 MCP 도구 목록 표시, 버전 표시.
  - 검증 상태: 컴파일 검증 통과. 에디터 동작 확인 대기.
  - 관련 파일: Assets/MCPTools/Editor/Common/MCPSettingsWindow.cs

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

- [x] 컴파일 오류 없음, asmdef 2개가 의도대로 분리됨 — 2026-07-21 사용자 확인 (Console 오류 없음)
- [x] `Tools/MCP/Settings` 메뉴가 열리고 MCPToolSettings 에셋이 자동 생성됨 — 2026-07-21 사용자 확인
- [x] 서버 주소를 변경·저장할 수 있음 — 2026-07-21 사용자 확인 (변경·저장 후 Inspector 반영 확인, 기본값 복원 완료)
- [x] ComfyUI 실행 상태에서 "서버 연결 테스트" 성공, 미실행 상태에서 명확한 실패 안내 표시 — 2026-07-21 사용자 확인 (실패/성공 경로 모두 확인, 에디터 프리징 없음)
- [x] `Tools/MCP/Ping (Local Test)` 메뉴 실행 시 콘솔에 mcptools_ping 성공 JSON이 출력됨 — 2026-07-21 사용자 확인
- [x] MCP for Unity 패키지 설치 완료 (Window > MCP For Unity 브리지 연결됨) — 2026-07-21 사용자 확인
- [x] MCP 클라이언트에서 `mcptools_ping` 호출 시 응답 수신 — 2026-07-21 검증 완료. MCP for Unity 로컬 HTTP 서버(http://127.0.0.1:8080/mcp, mcp-for-unity-server v3.4.4)에 MCP 프로토콜(initialize → tools/call)로 호출, `{"success":true,"data":{"version":"0.1.0","unityVersion":"6000.5.2f1","serverUrl":"http://127.0.0.1:8188"}}` 정상 수신.

### MCP for Unity 연동 메모

- MCP For Unity 창에서 **로컬 HTTP 서버(포트 8080)** 모드를 켠 상태여야 함. 기존에 Claude 사용자 설정에 등록돼 있던 Unity 공식 릴레이(`~/.unity/relay/relay_win.exe`) 경로의 `unity-mcp` 서버는 이 패키지와 별개라 도구를 가져오지 못함.
- 이 프로젝트에는 `unity-mcp-http`(http://127.0.0.1:8080/mcp)를 프로젝트 범위(`.mcp.json`)로 등록함. Claude Code 세션 재시작 후 도구 목록에 나타남.
- [ ] (선택, 배포 검증) `Assets/MCPTools`만 내보낸 별도 프로젝트에서 unity-mcp 패키지 없이 컴파일 오류가 없는지 확인

## 사용자 세팅 필요 (에디터 테스트 전)

1. Unity Hub에서 `C:\Project\CreateMCP\MCPToolTest` 프로젝트 열기 (6000.5.2f1) → 컴파일 확인
2. ComfyUI 로컬 서버 실행 (기본 http://127.0.0.1:8188) → 연결 테스트용
3. (MCP 연동 테스트 시) Unity 공식 MCP 패키지(unity-mcp)를 프로젝트에 설치하고 에디터를 켠 상태에서 Claude 재연결 — 현재 세션의 unity-mcp 서버는 ~/.unity/relay/relay_win.exe (Unity 공식 릴레이)
