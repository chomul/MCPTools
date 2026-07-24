# Task 3 — ComfyUIGenerator 도구

> 원본 계획: ../PLAN.md §4 Phase 3

## 1. 목표

PromptSet JSON을 입력으로 ComfyUI API를 호출해 항목당 후보 이미지 4장(시드 변경)을 생성하고, 썸네일에서 선택·확정하거나 재생성하는 3단계 도구를 완성한다. 확정본은 `Assets/Generated/Images/`에 복사되고 이미지 항목은 Sprite 임포트 설정이 자동 적용된다(RawImage 대상 제외).

## 2. 선행 조건

- Task 2 산출물: `Assets/Docs/PromptSet_*.json` (`PromptSetDocument`)
- Task 0 산출물: `ComfyUIClient`, `MCPToolSettings`
- ComfyUI 로컬 서버가 실행 중이고 체크포인트 모델이 로드 가능하다.

## 3. 구현 항목

1. **`ComfyUIGeneratorWindow : EditorWindow`** — 메뉴 `Tools/MCP/ComfyUI Generator`
   - PromptSet JSON 로드 → 항목 선택 → "후보 4개 생성" → 썸네일 그리드에서 선택/재생성 → 확정
2. **`WorkflowTemplateLoader`** — `Editor/ComfyUIGenerator/Workflows/*.json` (API Format) 로드 및 플레이스홀더 치환
   ```csharp
   public static string Bind(string templateJson, PromptItem item, long seed); // 지정 노드 inputs에 프롬프트/시드/해상도 주입
   ```
   - 치환 방식: 규약 토큰(`{{POSITIVE}}`, `{{NEGATIVE}}`, `{{SEED}}`, `{{WIDTH}}`, `{{HEIGHT}}`, `{{STEPS}}`, `{{CFG}}`) 문자열 치환 + class_type 기반 노드 검증
   - 기본 제공 템플릿: `txt2img_basic.json` (SD 계열 표준 txt2img API 포맷)
3. **`CandidateGenerator`** — 후보 4개 생성 오케스트레이션
   - 시드 전략: 기준 시드 무작위 1개 생성 후 `seed, seed+1, seed+2, seed+3` 4회 큐잉 (동일 프롬프트·설정, 시드만 변경)
   - `ComfyUIClient` 사용, **async/await + EditorApplication.update 기반 진행률 표시**로 에디터 블로킹 금지, 취소 버튼 지원
   - 결과 저장: `Assets/Generated/Candidates/{assetItemId}/{seed}.png` + 시드·프롬프트 메타 JSON 동봉
4. **선택/재생성 UX**
   - 4개 썸네일 클릭 선택 → "확정" 시 `Assets/Generated/Images/{assetItemId}.png`로 복사, `GenerationResult` 기록
   - "재생성" 시 새 기준 시드로 다시 4개 생성 (기존 후보는 덮어쓰기 전 삭제)
   - 확정 시 임포트 자동 설정 (2026-07-21 보완): UI 항목뿐 아니라 `SpriteRenderer`/`Image` 대상 등 **모든 이미지 항목을 TextureImporter로 Sprite(2D and UI) 설정** — 4단계에서 `SpriteRenderer.sprite`/`Image.sprite` 할당이 가능하려면 Sprite 임포트가 전제이기 때문. `RawImage` 대상만 Texture(Default) 유지. 공통으로 `alphaIsTransparency` 활성화, Pixels Per Unit은 `MCPToolSettings`에 신설하는 `spritePixelsPerUnit`(기본 100) 설정값 적용.
5. **MCP 도구 노출** — 대화형 선택을 지원하기 위해 **생성/조회/확정 3분할**

## 4. 산출물

- `ComfyUIGeneratorWindow`, `WorkflowTemplateLoader`, `CandidateGenerator` (Editor 코드)
- Workflow 템플릿 `txt2img_basic.json` (`Editor/ComfyUIGenerator/Workflows/`)
- `Assets/Generated/Candidates/{assetItemId}/{seed}.png` + 메타 JSON (실행 산출물)
- `Assets/Generated/Images/{assetItemId}.png` + `GenerationResult` (확정 산출물)
- MCP 도구 3종 (`mcptools_generate_candidates` / `mcptools_list_candidates` / `mcptools_select_candidate`)

## 5. MCP 도구

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_generate_candidates` | `promptSetPath: string`, `assetItemId: string`, `workflowName: string?`, `baseSeed: long?` | `{ success, message, data: { candidates: [{ path, seed }] } }` |
| `mcptools_list_candidates` | `assetItemId: string` | `{ success, data: { candidates: [{ path, seed }] } }` |
| `mcptools_select_candidate` | `assetItemId: string`, `candidatePath: string` | `{ success, data: { selectedPath } }` |

## 6. 완료 조건

- 체크리스트: [Task3_체크리스트.md](../checklist/Task3_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 후 다음 Task(Task 4)에 착수한다.**
