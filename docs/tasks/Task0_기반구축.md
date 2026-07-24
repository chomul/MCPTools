# Task 0 — 기반 구축

> 원본 계획: ../PLAN.md §4 Phase 0

## 1. 목표

배포 단위인 `Assets/MCPTools/` 폴더 구조와 asmdef 분리 구조를 만들고, 설정(ScriptableObject)·ComfyUI API 클라이언트·MCP 등록 뼈대를 구축한다. 이 Task가 끝나면 설정 창에서 ComfyUI 서버 연결을 테스트할 수 있고, MCP 클라이언트에서 `mcptools_ping` 호출에 응답한다.

## 2. 선행 조건

- ComfyUI가 로컬에 설치되어 있고 체크포인트 모델이 로드 가능하다.
- unity-mcp 패키지가 Unity 프로젝트에 설치되어 있다 (본 Task에서 설치·검증한다).
- Assets는 비어 있는 상태이며, 폴더 구조를 본 Task에서 새로 구축한다.

## 3. 구현 항목

1. **폴더 구조 생성** — `Assets/MCPTools/Editor|Runtime`, `Assets/Generated/*`(Images/Audio/Candidates), `Assets/Docs/`
2. **asmdef 2종 작성** — `MCPTools.Editor.asmdef`(Editor 전용, Runtime 참조 가능) / `MCPTools.Runtime.asmdef`(UnityEditor 참조 금지, Editor asmdef 참조 금지). Runtime→Editor 방향 참조는 구조상 불가능하게 강제한다.
3. **`MCPToolSettings : ScriptableObject`** — ComfyUI 주소(기본 `http://127.0.0.1:8188`), 타임아웃, 기본 Workflow, 경로 설정. 설정 인스펙터 포함, 없으면 기본값으로 자동 생성(`GetOrCreate()`).
4. **`ComfyUIClient`** — ComfyUI REST API 래퍼 (async/await, 블로킹 금지)
   ```csharp
   public Task<bool>         CheckServerAsync();                    // GET /system_stats
   public Task<string>       QueuePromptAsync(string workflowJson); // POST /prompt → prompt_id
   public Task<HistoryEntry> WaitForCompletionAsync(string promptId, IProgress<float> progress, CancellationToken ct); // GET /history/{id} 폴링
   public Task<byte[]>       DownloadOutputAsync(string filename, string subfolder, string type); // GET /view
   ```
5. **`McpToolRegistry` 뼈대** — unity-mcp 도구 등록 구조 + 설치·연결 확인용 더미 도구 `mcptools_ping`
6. **서버 연결 테스트 창** — 메뉴 `Tools/MCP/Settings` (설정 편집 + "서버 연결 테스트" 버튼)

## 4. 산출물

- `MCPTools.Editor.asmdef`, `MCPTools.Runtime.asmdef` (asmdef 2종)
- `MCPToolSettings` ScriptableObject (기본 에셋 자동 생성 포함)
- `ComfyUIClient`
- `Tools/MCP/Settings` 설정 창
- `mcptools_ping` MCP 도구

## 5. MCP 도구

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_ping` | 없음 | `{ success, message, data }` (연결 진단용 응답) |

## 6. 완료 조건

- 체크리스트: [Task0_체크리스트.md](../checklist/Task0_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 후 다음 Task(Task 1)에 착수한다.**
