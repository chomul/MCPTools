# AI 에셋 생성 파이프라인 개발 계획서 (PLAN.md)

> 기획서 기반 AI 에셋 생성 파이프라인 — Unity Editor Tool + MCP 도구화 프로젝트
>
> 최종 수정: 2026-07-21

---

## 1. 개요 및 목표

### 1.1 프로젝트 개요

기획서(디자인 문서)를 입력으로 받아, ComfyUI 로컬 서버를 통해 게임 에셋(이미지/사운드)을 생성하고, Unity 프리팹/UI에 자동 적용하는 **4단계 파이프라인**을 구축한다. 각 단계는 독립된 Unity Editor Tool로 구현하며, unity-mcp를 경유해 MCP 도구로 노출하여 AI 에이전트가 파이프라인을 구동할 수 있게 한다.

### 1.2 파이프라인 4단계

| 단계 | 도구명 | 입력 | 출력 |
|------|--------|------|------|
| 1 | **AssetListup** | 기획서 + 현재 프로젝트 스캔 결과 | 생성할 이미지/UI 목록 문서 (적용 대상 프리팹·UI 여부 명시) |
| 2 | **PromptBuilder** | 1단계 목록 문서 | ComfyUI 모델에 맞는 항목별 프롬프트 |
| 3 | **ComfyUIGenerator** | 프롬프트 + Workflow JSON | 후보 4개 생성(시드 변경) → 사용자 선택 (불만족 시 재생성) |
| 4 | **AssetApplier** | 선택된 결과물 + 대상 경로 | 프리팹/UI에 적용 완료된 에셋 |

### 1.3 목표

- 각 단계를 **개별 테스트 가능한 Unity Editor Tool**로 먼저 완성하고, 마지막 Phase에서 통합한다.
- 모든 단계를 **MCP 도구**로 노출하여 AI 에이전트 주도 워크플로를 지원한다.
- **`Assets/MCPTools/` 폴더 단위 배포**(.unitypackage)가 가능한 자기완결적 구조를 유지한다.

### 1.4 비목표 (Out of Scope)

- ComfyUI 서버 자체의 설치/모델 관리 자동화 (README 안내로 대체)
- 런타임(빌드 게임 내) 에셋 생성 — 본 도구는 에디터 전용
- 3D 모델/애니메이션 생성 (이미지·사운드에 한정, 확장 여지만 남김)

---

## 2. 개발 환경 및 전제 조건

| 항목 | 값 |
|------|-----|
| Unity | 6000.5.2f1 (Unity 6) |
| 렌더 파이프라인 | URP 17.5 |
| UI | uGUI (Canvas 기반) |
| 입력 | Input System |
| 프로젝트 경로 | `C:\Project\CreateMCP\MCPToolTest\` |
| 언어 | C# (에디터 도구 전체) |
| ComfyUI | 로컬 서버, 기본 `http://127.0.0.1:8188` (MCPToolSettings로 변경 가능) |
| MCP 브리지 | unity-mcp (Unity ↔ MCP 클라이언트 연결) |
| 배포 단위 | `Assets/MCPTools/` → .unitypackage |

**전제 조건**

- ComfyUI가 로컬에 설치되어 있고, 사용할 체크포인트 모델이 로드 가능해야 한다.
- unity-mcp 패키지가 Unity 프로젝트에 설치되어 있어야 한다 (Phase 0에서 설치·검증).
- Assets는 현재 비어 있으므로, 폴더 구조를 Phase 0에서 새로 구축한다.

---

## 3. 아키텍처

### 3.1 폴더 구조

```
Assets/
├── MCPTools/                          # ★ 배포 단위 (자기완결)
│   ├── README.md                      # 설치·사용·문제해결 문서
│   ├── Editor/
│   │   ├── MCPTools.Editor.asmdef     # Editor 전용 asmdef
│   │   ├── Common/                    # 공용 유틸 (설정, HTTP, 로깅, MCP 등록)
│   │   ├── AssetListup/
│   │   ├── PromptBuilder/
│   │   ├── ComfyUIGenerator/
│   │   │   └── Workflows/             # Workflow JSON 템플릿 (API Format)
│   │   └── AssetApplier/
│   └── Runtime/
│       ├── MCPTools.Runtime.asmdef    # UnityEditor 참조 금지
│       └── Data/                      # 직렬화 데이터 모델 (필요 시)
├── Generated/                         # 생성물 (배포 제외)
│   ├── Images/                        # 확정(선택)된 이미지
│   ├── Audio/                         # 확정된 사운드
│   └── Candidates/                    # 후보 4개 임시 저장소
└── Docs/                              # 기획서·목록 문서 (배포 제외)
```

### 3.2 어셈블리 정의 (asmdef)

| asmdef | 플랫폼 | 참조 규칙 |
|--------|--------|-----------|
| `MCPTools.Editor` | Editor 전용 (`includePlatforms: ["Editor"]`) | `MCPTools.Runtime` 참조 가능 |
| `MCPTools.Runtime` | 전 플랫폼 | **UnityEditor 참조 금지, Editor asmdef 참조 금지** |

- 네임스페이스: `MCPTools.Editor.*` / `MCPTools.Runtime.*`
- Runtime에는 빌드에 포함되어도 무해한 순수 데이터 모델만 둔다(예: 생성 이력 메타데이터). 에디터 로직·네트워크·AssetDatabase 호출은 전부 Editor 측.
- Runtime→Editor 방향 참조는 asmdef 구조상 불가능하게 강제한다.

### 3.3 데이터 흐름

```
기획서(Assets/Docs/*.md)
        │
        ▼
[1] AssetListup ──── 프로젝트 스캔(프리팹/씬/UI) ────► AssetList.json (Assets/Docs/)
        │                                              · 항목별: id, 이름, 설명, 유형(Image/UI/Audio),
        ▼                                                적용 대상 프리팹 경로, UI 여부
[2] PromptBuilder ──────────────────────────────────► PromptSet.json (Assets/Docs/)
        │                                              · 항목별: positive/negative 프롬프트, 해상도, 모델 힌트
        ▼
[3] ComfyUIGenerator ── ComfyUI API 호출 ───────────► 후보 4장 (Assets/Generated/Candidates/{itemId}/)
        │                   │                          · 사용자 선택 → Assets/Generated/Images/ 확정 복사
        │                   └ 재생성(시드 변경) 루프
        ▼
[4] AssetApplier ── PrefabUtility + Undo ───────────► 프리팹/UI에 Sprite·Texture·AudioClip 적용
```

- 단계 간 인터페이스는 **JSON 문서 파일**(`Assets/Docs/`)로 고정한다. 각 도구는 앞 단계 산출물 파일만 있으면 독립 실행 가능 → 단계별 테스트와 MCP 도구화가 쉬워진다.

### 3.4 핵심 데이터 모델 (Runtime/Data 또는 Editor/Common)

```csharp
// 단계 1 산출물
[Serializable] public class AssetListDocument {
    public string sourceDesignDoc;        // 기획서 상대 경로 (Assets/ 기준)
    public string createdAt;
    public List<AssetListItem> items;
}
[Serializable] public class AssetListItem {
    public string id;                     // 예: "img_title_logo"
    public string displayName;
    public AssetKind kind;                // Image | UI | Audio
    public string description;            // 기획서에서 추출한 요구사항
    public string targetPrefabPath;       // 적용 대상 프리팹 (Assets/ 기준 상대 경로)
    public string targetObjectPath;       // 프리팹 내부 계층 경로 (예: "Canvas/TitlePanel/Logo")
    public bool isUI;                     // uGUI 요소 여부 (Sprite import 설정 분기)
    public int width, height;             // 권장 해상도
}

// 단계 2 산출물
[Serializable] public class PromptSetDocument {
    public string sourceAssetList;
    public string workflowTemplate;       // 사용할 Workflow JSON 파일명
    public List<PromptItem> items;
}
[Serializable] public class PromptItem {
    public string assetItemId;            // AssetListItem.id 참조
    public string positive;
    public string negative;
    public int width, height, steps;
    public float cfg;
}

// 단계 3 산출물 (선택 결과)
[Serializable] public class GenerationResult {
    public string assetItemId;
    public string[] candidatePaths;       // 후보 4개 경로
    public long[] seeds;                  // 각 후보의 시드
    public string selectedPath;           // 확정된 경로 (Assets/Generated/Images/...)
}
```

### 3.5 MCP 연동 구조

```
MCP 클라이언트 (Claude 등)
        │  JSON-RPC
        ▼
unity-mcp 브리지 (Unity 패키지)
        │  도구 등록/호출
        ▼
MCPTools.Editor.Common.McpToolRegistry
        │  단계별 핸들러 위임
        ├─► AssetListupTool.RunFromMcp(json) ─► JSON 결과 반환
        ├─► PromptBuilderTool.RunFromMcp(json)
        ├─► ComfyUIGeneratorTool.RunFromMcp(json)
        └─► AssetApplierTool.RunFromMcp(json)
```

**MCP 도구화 규칙**

- 도구 하나 = 파이프라인 단계 하나. 통합 실행도 별도 도구(`mcptools_run_pipeline`)로 분리.
- 파라미터/반환은 **JSON 직렬화 가능한 원시 타입·문자열 경로만** 사용 (UnityEngine.Object 직접 전달 금지).
- 반환 JSON 공통 포맷: `{ "success": bool, "message": string, "data": {...} }`
- 사용자 선택이 필요한 단계(3단계)는 "후보 생성까지"와 "선택 확정"을 별도 MCP 도구로 분리해 비대화형 호출을 지원한다.

### 3.6 공용 인프라 (Editor/Common)

| 클래스 | 책임 |
|--------|------|
| `MCPToolSettings : ScriptableObject` | ComfyUI 주소, 타임아웃, 기본 Workflow, 경로 설정. `Assets/MCPTools/Editor/Common/` 내 기본 에셋 제공, 프로젝트별 오버라이드 허용 |
| `ComfyUIClient` | ComfyUI REST API 래퍼 (async/await, 블로킹 금지) |
| `McpToolRegistry` | unity-mcp에 4개 도구 등록, JSON 파라미터 파싱/검증 |
| `AssetPathUtil` | Assets/ 기준 상대 경로 검증·정규화, 폴더 자동 생성 |
| `MCPToolsLogger` | 단계별 접두어 로그, 오류 시 해결 가이드 메시지 포함 |

```csharp
public class MCPToolSettings : ScriptableObject {
    public string comfyUIServerUrl = "http://127.0.0.1:8188";   // 기본값, 하드코딩 사용처 금지
    public int    requestTimeoutSeconds = 300;
    public string defaultImageWorkflow = "txt2img_basic.json";
    public string generatedRootPath = "Assets/Generated";
    public string docsRootPath = "Assets/Docs";
    public int    candidateCount = 4;

    public static MCPToolSettings GetOrCreate();   // 없으면 기본값으로 자동 생성
}
```

---

## 4. Phase별 개발 계획

> 원칙: **각 Phase는 단독으로 완결**되며, Phase 종료 시 사용자가 Unity 에디터에서 직접 테스트한다. 테스트 통과 후 다음 Phase 착수.

### Phase 0 — 기반 구축

**구현 항목**

1. 폴더 구조 생성 (`Assets/MCPTools/Editor|Runtime`, `Assets/Generated/*`, `Assets/Docs/`)
2. `MCPTools.Editor.asmdef` / `MCPTools.Runtime.asmdef` 작성 (3.2 규칙 적용)
3. `MCPToolSettings` ScriptableObject + 설정 인스펙터(기본 에셋 자동 생성 포함)
4. `ComfyUIClient` — ComfyUI API 클라이언트
   ```csharp
   public class ComfyUIClient {
       public ComfyUIClient(MCPToolSettings settings);
       public Task<bool>            CheckServerAsync();                    // GET /system_stats
       public Task<string>          QueuePromptAsync(string workflowJson); // POST /prompt → prompt_id
       public Task<HistoryEntry>    WaitForCompletionAsync(string promptId, IProgress<float> progress, CancellationToken ct); // GET /history/{id} 폴링
       public Task<byte[]>          DownloadOutputAsync(string filename, string subfolder, string type); // GET /view
   }
   ```
5. `McpToolRegistry` 뼈대 + unity-mcp 설치·연결 확인용 더미 도구 `mcptools_ping`
6. 서버 연결 테스트 창: `Tools/MCP/Settings` (설정 편집 + "서버 연결 테스트" 버튼)

**산출물**: asmdef 2종, Settings SO, ComfyUIClient, 설정 창, `mcptools_ping` MCP 도구

**사용자 에디터 테스트 체크리스트**

- [ ] 컴파일 오류 없음, asmdef 2개가 의도대로 분리됨 (Runtime asmdef에서 `UnityEditor` 참조 시 컴파일 에러 확인)
- [ ] `Tools/MCP/Settings` 메뉴가 열리고 MCPToolSettings 에셋이 자동 생성됨
- [ ] 서버 주소를 변경·저장할 수 있음
- [ ] ComfyUI 실행 상태에서 "서버 연결 테스트" 성공, 미실행 상태에서 "서버가 실행 중이 아닙니다. ComfyUI를 시작한 뒤 다시 시도하세요" 류의 명확한 안내 표시
- [ ] MCP 클라이언트에서 `mcptools_ping` 호출 시 응답 수신

---

### Phase 1 — AssetListup 도구

**구현 항목**

1. `AssetListupWindow : EditorWindow` — 메뉴 `Tools/MCP/Asset Listup`
   - 기획서 파일 선택(`Assets/Docs/` 내 .md/.txt), 프로젝트 스캔 실행, 목록 편집(추가/삭제/수정), 저장
2. `ProjectScanner` — 현재 프로젝트의 프리팹/씬을 스캔해 이미지·UI 슬롯 후보 추출
   ```csharp
   public static class ProjectScanner {
       public static List<ScanEntry> ScanPrefabs(string rootPath);  // Image/RawImage/SpriteRenderer/AudioSource 슬롯 수집
   }
   ```
3. `AssetListBuilder` — 기획서 텍스트 + 스캔 결과 → `AssetListDocument` 생성·병합
   - 각 항목에 **적용 대상 프리팹 경로와 UI 여부를 필수 기록** (누락 시 확인 후 저장 — 해당 항목 상태="대상 미정" 기록)
4. 산출물 직렬화: `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json`
5. MCP 도구 노출

**MCP 도구 스펙 (AI 중립 — scan/save 2도구 구조)**

목록 "작성" 지능은 AI(MCP 클라이언트)/사용자에 위임한다. 도구는 스캔 데이터+작성 지침 제공(scan)과 결과 검증 후 저장(save)만 담당한다.

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_asset_scan` | `designDocPath: string?` (Assets/ 상대 .md/.txt), `scanRootPath: string` (기본 "Assets") | `{ success, message, data: { designDocPath?, designDocText?, scanRootPath, scanEntries, itemSchema, instructions } }` — AI가 목록 작성에 쓸 재료 |
| `mcptools_asset_list_save` | `items: object[]` (필수, itemSchema 형식), `outputPath: string?`, `designDocPath: string?`, `scanRootPath: string?` (문서 메타 기록용) | `{ success, message, data: { outputPath, itemCount, warnings } }` — 경고(대상 미기록 등)가 있어도 저장은 수행되고 warnings로 반환 |

**사용자 에디터 테스트 체크리스트**

- [ ] `Tools/MCP/Asset Listup` 창이 열림
- [ ] 샘플 기획서(md)를 넣고 실행 시 항목 목록이 생성됨
- [ ] 테스트용 프리팹(Image 포함 Canvas 프리팹 1개)을 만들어 스캔하면 해당 슬롯이 목록에 잡힘
- [ ] 각 항목에 대상 프리팹 경로·UI 여부가 표시되고, 비워둔 채 저장하면 확인 다이얼로그 후 상태="대상 미정"으로 저장됨
- [ ] JSON이 `Assets/Docs/`에 저장되고 Project 뷰에서 즉시 보임 (AssetDatabase.Refresh 동작)
- [ ] MCP로 `mcptools_asset_scan` → `mcptools_asset_list_save` 순서 호출 시 동일한 JSON이 저장되고 outputPath·itemCount·warnings가 반환됨

---

### Phase 2 — PromptBuilder 도구

> **설계 노트 (Task 1 자산 재사용)**: Task 1에서 구축한 공용 `AiCliRunner`(`Editor/Common`) — PATH 기반 AI CLI 감지(claude/codex/gemini/cursor-agent/copilot + 직접 입력), stdin 프롬프트 전달 비대화형 실행, 타임아웃/취소, 프로젝트 탐색 모드(읽기 전용) — 와 "AI용 프롬프트 복사 → 외부 AI → 응답 JSON 붙여넣기" AI 위임 패턴을 Task 2에서도 동일하게 재사용한다. 프롬프트 작성 지능은 AI에 위임하고, 도구/창은 재료 제공과 결과 검증·저장을 담당한다 (AI 중립 파이프라인, Task2_PromptBuilder.md §5 참조).

**구현 항목**

1. `PromptBuilderWindow : EditorWindow` — 메뉴 `Tools/MCP/Prompt Builder`
   - AssetList JSON 선택 → 항목별 프롬프트 초안 생성 → 항목별 수동 편집 → 저장
2. `PromptTemplate` — 모델 스타일별 프롬프트 규칙 (ScriptableObject 또는 JSON)
   - 공통 스타일 접두어(게임 아트 스타일), 품질 태그, 공통 negative 프롬프트
   - UI 항목은 "clean edges, transparent background, game ui icon" 등 UI 특화 태그 자동 부여
3. `PromptBuilder` — `AssetListDocument` → `PromptSetDocument` 변환
   ```csharp
   public static class PromptBuilder {
       public static PromptSetDocument Build(AssetListDocument list, PromptTemplate template);
   }
   ```
4. 산출물: `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json`
5. MCP 도구 노출

**MCP 도구 스펙**

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_prompt_build` | `assetListPath: string`, `templateName: string?`, `outputPath: string?`, `overrides: { assetItemId, positive?, negative? }[]?` | `{ success, message, data: { outputPath, items: PromptItem[] } }` |

- `overrides` 파라미터로 AI 에이전트가 항목별 프롬프트를 직접 지정/수정 가능하게 한다 (에이전트가 기획서 맥락으로 더 좋은 프롬프트를 쓸 수 있으므로).

**사용자 에디터 테스트 체크리스트**

- [ ] `Tools/MCP/Prompt Builder` 창에서 Phase 1의 AssetList JSON을 불러올 수 있음
- [ ] 항목별 positive/negative 프롬프트 초안이 자동 생성됨
- [ ] UI 항목에 UI 특화 태그가 자동 포함됨
- [ ] 프롬프트를 수동 편집 후 저장하면 JSON에 반영됨
- [ ] 존재하지 않는 AssetList 경로 지정 시 명확한 오류 메시지
- [ ] MCP로 `mcptools_prompt_build` 호출(+overrides 포함) 시 결과 JSON 정상 생성

---

### Phase 3 — ComfyUIGenerator 도구

**구현 항목**

1. `ComfyUIGeneratorWindow : EditorWindow` — 메뉴 `Tools/MCP/ComfyUI Generator`
   - PromptSet JSON 로드 → 항목 선택 → "후보 4개 생성" → 썸네일 그리드에서 선택/재생성 → 확정
2. `WorkflowTemplateLoader` — `Editor/ComfyUIGenerator/Workflows/*.json` (API Format) 로드 및 플레이스홀더 치환
   ```csharp
   public static class WorkflowTemplateLoader {
       // 템플릿의 지정 노드 inputs에 프롬프트/시드/해상도 주입
       public static string Bind(string templateJson, PromptItem item, long seed);
   }
   ```
   - 치환 방식: 템플릿 JSON 내 규약 토큰(`{{POSITIVE}}`, `{{NEGATIVE}}`, `{{SEED}}`, `{{WIDTH}}`, `{{HEIGHT}}`, `{{STEPS}}`, `{{CFG}}`) 문자열 치환 + class_type 기반 노드 검증
   - 기본 제공 템플릿: `txt2img_basic.json` (SD 계열 표준 txt2img API 포맷)
3. `CandidateGenerator` — 후보 4개 생성 오케스트레이션
   - 시드 전략: 기준 시드 무작위 1개 생성 후 `seed, seed+1, seed+2, seed+3` 4회 큐잉 (동일 프롬프트·설정, 시드만 변경)
   - `ComfyUIClient` 사용, **async/await + EditorApplication.update 기반 진행률 표시**로 에디터 블로킹 금지, 취소 버튼 지원
   - 결과 저장: `Assets/Generated/Candidates/{assetItemId}/{seed}.png` + 시드·프롬프트 메타 JSON 동봉
4. 선택/재생성 UX
   - 4개 썸네일 클릭 선택 → "확정" 시 `Assets/Generated/Images/{assetItemId}.png`로 복사, `GenerationResult` 기록
   - "재생성" 시 새 기준 시드로 다시 4개 생성 (기존 후보는 덮어쓰기 전 삭제)
   - 확정 시 임포트 자동 설정 (2026-07-21 보완): UI 항목뿐 아니라 **`SpriteRenderer`/`Image` 대상 등 모든 이미지 항목을 TextureImporter로 Sprite(2D and UI) 설정**한다. `RawImage` 대상만 Texture(Default)로 남긴다. 공통으로 `alphaIsTransparency` 활성화, Pixels Per Unit은 `MCPToolSettings`의 설정값(`spritePixelsPerUnit`, 기본 100)을 적용한다 — 하드코딩 금지 규칙에 따라 설정 에셋으로 관리.
5. MCP 도구 노출 — 대화형 선택을 지원하기 위해 **생성/조회/확정 3분할**

**MCP 도구 스펙**

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_generate_candidates` | `promptSetPath: string`, `assetItemId: string`, `workflowName: string?`, `baseSeed: long?` | `{ success, message, data: { candidates: [{ path, seed }] } }` |
| `mcptools_list_candidates` | `assetItemId: string` | `{ success, data: { candidates: [{ path, seed }] } }` |
| `mcptools_select_candidate` | `assetItemId: string`, `candidatePath: string` | `{ success, data: { selectedPath } }` |

**사용자 에디터 테스트 체크리스트**

- [ ] `Tools/MCP/ComfyUI Generator` 창에서 PromptSet JSON 로드 가능
- [ ] "후보 4개 생성" 실행 중 에디터가 멈추지 않고 진행률이 표시됨, 취소 동작
- [ ] `Assets/Generated/Candidates/{id}/`에 서로 다른 시드의 PNG 4장이 저장됨
- [ ] 썸네일 4개 중 하나를 선택·확정하면 `Assets/Generated/Images/`로 복사됨
- [ ] 이미지 항목 확정 시 Sprite 임포트 설정이 자동 적용됨 (UI·SpriteRenderer 대상 포함, RawImage 대상은 Texture 유지, PPU=설정값)
- [ ] "재생성" 시 새로운 4장이 생성됨
- [ ] ComfyUI 미기동 상태에서 실행 시 서버 안내 오류가 뜨고 창이 멈추지 않음
- [ ] MCP로 generate → list → select 3단계 호출이 순서대로 동작함

---

### Phase 4 — AssetApplier 도구

**구현 항목**

1. `AssetApplierWindow : EditorWindow` — 메뉴 `Tools/MCP/Asset Applier`
   - AssetList + GenerationResult 로드 → 적용 대상 미리보기(프리팹 경로, 내부 오브젝트 경로, 현재/새 이미지 비교) → 개별/일괄 적용
2. `AssetApplier` — 실제 적용 로직
   ```csharp
   public static class AssetApplier {
       public static ApplyResult ApplyToPrefab(AssetListItem item, string assetPath);
       // 내부: PrefabUtility.LoadPrefabContents → 대상 컴포넌트 탐색
       //       (Image.sprite / RawImage.texture / SpriteRenderer.sprite / AudioSource.clip)
       //       → Undo.RecordObject → PrefabUtility.SaveAsPrefabAsset → UnloadPrefabContents
   }
   ```
3. 안전 장치
   - 적용 전 대상 프리팹·내부 경로·컴포넌트 존재 검증, 실패 항목은 이유와 함께 결과 목록에 표시
   - Undo 지원 (에디터에서 Ctrl+Z로 되돌리기 가능)
   - 씬이 아닌 **프리팹 에셋 자체를 수정** (PrefabUtility 경유), AssetDatabase.SaveAssets 마무리
4. MCP 도구 노출

**MCP 도구 스펙**

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_apply_asset` | `assetListPath: string`, `assetItemId: string`, `assetPath: string?` (생략 시 확정본 자동 탐색) | `{ success, message, data: { prefabPath, objectPath, appliedAssetPath } }` |
| `mcptools_apply_all` | `assetListPath: string` | `{ success, data: { applied: [...], failed: [{ id, reason }] } }` |

**사용자 에디터 테스트 체크리스트**

- [ ] `Tools/MCP/Asset Applier` 창에서 적용 대상 목록과 미리보기가 표시됨
- [ ] 개별 적용 시 대상 프리팹의 Image에 선택 이미지가 반영됨 (프리팹 에셋을 열어 확인)
- [ ] Ctrl+Z로 적용이 되돌려짐
- [ ] 존재하지 않는 프리팹 경로/내부 경로 항목은 실패 목록에 이유와 함께 표시됨
- [ ] 일괄 적용 후 성공/실패 요약이 정확함
- [ ] MCP로 `mcptools_apply_asset` 호출 시 동일 결과

---

### Phase 5 — 통합 도구 + MCP 노출 정리 + 배포 패키지 검증

**구현 항목**

1. `PipelineWindow : EditorWindow` — 메뉴 `Tools/MCP/Pipeline (All-in-One)`
   - 4단계를 탭/스텝퍼 UI로 통합, 단계별 산출물 자동 연결 (1 완료 → 2 입력 자동 지정 …)
   - 단계별 상태 표시 (미실행/완료/실패), 중간 단계부터 재시작 가능
2. 통합 MCP 도구
   | 도구명 | 파라미터 | 반환 |
   |--------|----------|------|
   | `mcptools_run_pipeline` | `designDocPath: string`, `autoSelect: "first" \| "none"` (후보 자동 선택 정책), `workflowName: string?` | `{ success, data: { assetListPath, promptSetPath, pendingSelections: [...], applied: [...] } }` |
   - `autoSelect:"none"`이면 3단계에서 멈추고 `pendingSelections` 반환 → 사용자가 선택 후 `mcptools_select_candidate` + `mcptools_apply_asset`으로 이어서 진행
3. `mcptools_status` — 설정·서버 상태·산출물 현황 조회 도구 (진단용)
4. README.md 작성 (`Assets/MCPTools/README.md`)
   - 설치 절차(unitypackage import, unity-mcp 연결), ComfyUI 준비(주소 설정, 모델·워크플로 준비), 4단계 사용법, MCP 도구 레퍼런스, 트러블슈팅(서버 미기동, 포트 변경, 방화벽)
5. 배포 패키지 검증
   - `Assets/MCPTools/`만 .unitypackage로 export
   - **깨끗한 신규 Unity 6 프로젝트에 import**하여 검증: 컴파일 오류 0, 외부 폴더(Generated/Docs/Design) 의존 없음, 첫 실행 시 필요한 폴더·설정 자동 생성 확인
   - 절대 경로 잔재 검색(코드 내 `C:\` 등 검사)

**사용자 에디터 테스트 체크리스트 (최종 인수 테스트)**

- [ ] `Tools/MCP/Pipeline` 창에서 기획서 하나로 1→4단계가 끊김 없이 진행됨
- [ ] 3단계에서 후보 선택 UI가 정상 동작하고, 재생성도 통합 창에서 가능함
- [ ] 중간 단계부터 재시작(예: PromptSet 수정 후 3단계부터) 가능
- [ ] MCP로 `mcptools_run_pipeline`(autoSelect:"none") → 선택 → 적용의 전체 시나리오가 동작함
- [ ] .unitypackage를 새 프로젝트에 import 시 컴파일 오류 없이 동작하고, 설정 기본값(127.0.0.1:8188)으로 즉시 사용 가능
- [ ] README만 보고 제3자가 설치·실행 가능한 수준인지 리뷰

---

## 5. ComfyUI 연동 상세

### 5.1 API 포맷 Workflow JSON

- **API Format**(노드 id → `{ class_type, inputs }` 맵) 사용. UI에서 export한 그래프 포맷이 아님에 유의.
- 템플릿은 `Assets/MCPTools/Editor/ComfyUIGenerator/Workflows/`에 저장하고, 규약 토큰으로 값을 주입한다.

```json
{
  "3": { "class_type": "KSampler",
         "inputs": { "seed": "{{SEED}}", "steps": "{{STEPS}}", "cfg": "{{CFG}}",
                     "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                     "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
  "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "{{CHECKPOINT}}" } },
  "5": { "class_type": "EmptyLatentImage", "inputs": { "width": "{{WIDTH}}", "height": "{{HEIGHT}}", "batch_size": 1 } },
  "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{POSITIVE}}", "clip": ["4", 1] } },
  "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{NEGATIVE}}", "clip": ["4", 1] } },
  "8": { "class_type": "VAEDecode", "inputs": { "samples": ["3", 0], "vae": ["4", 2] } },
  "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "mcptools", "images": ["8", 0] } }
}
```

- 사용자가 자신의 워크플로(모델·LoRA 포함)를 같은 폴더에 추가하면 도구가 자동 인식한다. 필수 토큰이 없는 템플릿은 로드 시 검증 오류로 안내.

### 5.2 엔드포인트 및 호출 흐름

| 순서 | 엔드포인트 | 용도 |
|------|-----------|------|
| 0 | `GET /system_stats` | 서버 생존 확인 (사전 점검) |
| 1 | `POST /prompt` | `{ "prompt": <workflow>, "client_id": <guid> }` 전송 → `prompt_id` 수신 |
| 2 | `GET /history/{prompt_id}` | 완료 폴링 (1초 간격, 타임아웃 = 설정값). `outputs`에서 파일명 획득 |
| 3 | `GET /view?filename=...&subfolder=...&type=output` | 결과 파일 다운로드 |

- 폴링은 `EditorApplication.update` 또는 `Task.Delay` 기반 async 루프로 구현하고, 진행 상황은 창 내 프로그레스 바로 표시 (EditorUtility.DisplayProgressBar의 모달 블로킹은 지양).
- 오류 분기: 연결 거부(서버 미기동 안내), HTTP 400(워크플로 검증 실패 — 노드 오류 본문 표시), 타임아웃(재시도 안내).

### 5.3 후보 4개 시드 전략

- 기준 시드: `baseSeed = 지정값 ?? Random(0, 2^31)`
- 후보 i의 시드 = `baseSeed + i` (i = 0..3), 프롬프트·해상도·스텝은 동일
- 4건을 순차 큐잉(POST /prompt ×4)하고 각 prompt_id를 병렬 폴링 — ComfyUI는 내부 큐로 순차 처리하므로 서버 부하 안전
- 재생성 시 새로운 baseSeed 발급. 메타 JSON에 시드를 기록해 "같은 결과 재현"이 가능하게 한다.
- 사운드 워크플로(차후): 동일 구조에서 SaveImage 대신 오디오 출력 노드를 갖는 템플릿으로 확장 (`AssetKind.Audio` 분기만 추가).

---

## 6. 배포 계획

### 6.1 배포 단위와 제외 대상

- **포함**: `Assets/MCPTools/` 전체 (Editor 코드, Runtime 코드, Workflow 템플릿, README.md, 기본 설정 SO)
- **제외**: `Assets/Generated/`, `Assets/Docs/`, `C:\Project\CreateMCP\Design\` 등 프로젝트 고유 산출물
- 도구 첫 실행 시 `Generated/`·`Docs/` 폴더가 없으면 자동 생성 → 새 프로젝트에서 추가 조치 불필요

### 6.2 자기완결성 규칙

- 코드·설정에 절대 경로 금지. 모든 경로는 `Assets/` 기준 상대 경로 (`AssetPathUtil`로 강제)
- 외부 asmdef·서드파티 DLL 의존 금지 (unity-mcp만 전제, README에 설치 안내)
- 기본값만으로 동작: MCPToolSettings 기본값(127.0.0.1:8188, 기본 워크플로)으로 즉시 사용 가능

### 6.3 검증 절차

1. `Tools/MCP` 하위 메뉴에서 export 대상 확인 후 `Assets/MCPTools/` 우클릭 → Export Package (의존성 포함 해제 확인)
2. 신규 Unity 6000.5.x URP 프로젝트 생성 → unity-mcp 설치 → .unitypackage import
3. 최종 인수 테스트 체크리스트(Phase 5) 전체 재수행
4. 버전 표기: README 상단 + `MCPToolsVersion` 상수 (예: 0.1.0), 변경 이력 섹션 유지

---

## 7. 리스크 및 대응

| # | 리스크 | 영향 | 대응 |
|---|--------|------|------|
| 1 | ComfyUI 서버 미기동/포트 상이 | 3단계 전체 불가 | 사전 `CheckServerAsync` 점검, 설정 창에서 주소 변경 + 연결 테스트 버튼, 오류 메시지에 해결 절차 명시 |
| 2 | 사용자 모델/워크플로 다양성 (템플릿 불일치) | 생성 실패(HTTP 400) | 규약 토큰 검증기로 로드 시 사전 검사, ComfyUI 오류 본문을 그대로 노출, 기본 템플릿 제공 |
| 3 | 생성 대기 중 에디터 프리징 | UX 저하, 강제 종료 | async/await + 비모달 진행률, CancellationToken 취소 지원, 타임아웃 설정화 |
| 4 | 프리팹 구조 변경으로 적용 대상 경로 불일치 | 4단계 적용 실패 | 적용 전 경로·컴포넌트 검증 후 실패 사유 리포트, AssetListup 재스캔으로 목록 갱신 유도 |
| 5 | unity-mcp API 변경/버전 비호환 | MCP 노출 실패 | MCP 등록 코드를 `McpToolRegistry` 한 곳에 격리(어댑터 패턴), 에디터 창 단독으로도 전 기능 사용 가능하게 유지 |
| 6 | 기획서 형식 다양성 (파싱 정확도) | 1단계 목록 품질 저하 | 목록은 항상 수동 편집 가능하게 설계, MCP `overrides`로 에이전트 보정 허용, 기획서 권장 양식을 README에 제공 |
| 7 | 대용량 이미지로 인한 임포트 지연 | 반복 작업 속도 저하 | 후보는 Candidates 폴더에 두고 확정본만 Images로 복사, 일괄 작업 시 AssetDatabase.StartAssetEditing/StopAssetEditing 사용 |
| 8 | 라이선스/생성물 권리 이슈 | 배포 리스크 | README에 "생성물 권리는 사용 모델 라이선스를 따름" 고지, 도구 자체는 모델 미포함 |

---

## 부록 A. 메뉴 구성 요약

| 메뉴 | 창 |
|------|-----|
| `Tools/MCP/Settings` | 설정 + 서버 연결 테스트 |
| `Tools/MCP/Asset Listup` | 1단계 |
| `Tools/MCP/Prompt Builder` | 2단계 |
| `Tools/MCP/ComfyUI Generator` | 3단계 |
| `Tools/MCP/Asset Applier` | 4단계 |
| `Tools/MCP/Pipeline (All-in-One)` | 통합 (Phase 5) |

## 부록 B. MCP 도구 목록 요약

| 도구 | 단계 | 비고 |
|------|------|------|
| `mcptools_ping` | 0 | 연결 진단 |
| `mcptools_status` | 5 | 설정·서버·산출물 상태 |
| `mcptools_asset_scan` | 1 | 분석 입력 수집 (AI 중립) |
| `mcptools_asset_list_save` | 1 | 목록 검증·저장 |
| `mcptools_prompt_build` | 2 | overrides 지원 |
| `mcptools_generate_candidates` | 3 | 후보 4개 생성 |
| `mcptools_list_candidates` | 3 | 후보 조회 |
| `mcptools_select_candidate` | 3 | 선택 확정 |
| `mcptools_apply_asset` | 4 | 단건 적용 |
| `mcptools_apply_all` | 4 | 일괄 적용 |
| `mcptools_run_pipeline` | 5 | 통합 실행 (autoSelect 정책) |
