# Task 2 — PromptBuilder 도구

> 원본 계획: ../PLAN.md §4 Phase 2

## 1. 목표

Task 1의 AssetList JSON을 입력으로 받아, 항목별 ComfyUI용 positive/negative 프롬프트를 담은 `PromptSet.json`을 만드는 2단계 도구를 완성한다. UI 항목에는 UI 특화 태그가 자동 부여되고, 항목별 수동 편집이 가능하다.

## 2. 선행 조건

- Task 1 산출물: `Assets/Docs/AssetList_*.json` (`AssetListDocument`)
- Task 0 산출물: `MCPToolSettings`, `McpToolRegistry`

## 3. 구현 항목

1. **`PromptBuilderWindow : EditorWindow`** — 메뉴 `Tools/MCP/Prompt Builder`
   - AssetList JSON 선택 → 항목별 프롬프트 초안 생성 → 항목별 수동 편집 → 저장
2. **`PromptTemplate`** — 모델 스타일별 프롬프트 규칙 (ScriptableObject 또는 JSON)
   - 공통 스타일 접두어(게임 아트 스타일), 품질 태그, 공통 negative 프롬프트
   - UI 항목은 "clean edges, transparent background, game ui icon" 등 UI 특화 태그 자동 부여
3. **`PromptBuilder`** — `AssetListDocument` → `PromptSetDocument` 변환
   ```csharp
   public static PromptSetDocument Build(AssetListDocument list, PromptTemplate template);
   ```
4. **산출물 직렬화** — `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json`
5. **MCP 도구 노출** — `mcptools_prompt_build` (`overrides` 파라미터로 AI 에이전트가 항목별 프롬프트를 직접 지정/수정 가능)

## 4. 산출물

- `PromptBuilderWindow`, `PromptTemplate`, `PromptBuilder` (Editor 코드)
- `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json` (실행 산출물)
- `mcptools_prompt_build` MCP 도구

## 5. MCP 도구 — AI 중립 설계 원칙 (2026-07-21 사용자 결정, Task 1과 동일 패턴)

프롬프트 "작성" 지능은 사용자가 쓰는 AI(클로드 코드·Codex·Cursor 등 MCP 클라이언트, 또는 MCP 없는 웹 AI)에 위임한다. 도구는 재료 제공/결과 제출로 분리한다.

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_prompt_scan` | `assetListPath: string`, `templateName: string?` | `{ success, message, data: { assetItems, template, promptSchema, instructions } }` — AI가 프롬프트 작성에 쓸 재료 |
| `mcptools_prompt_save` | `items: PromptItem[]`, `assetListPath: string?`, `outputPath: string?` | `{ success, message, data: { outputPath, itemCount, warnings } }` |

> **설계 노트 (Task 1 자산 재사용)**: Task 1에서 구축한 공용 `AiCliRunner`(`Editor/Common/AiCliRunner.cs`) — PATH 기반 AI CLI 감지(claude/codex/gemini/cursor-agent/copilot + 직접 입력), stdin 프롬프트 전달 비대화형 실행, 타임아웃(기본 300초)/취소, 프로젝트 탐색 모드(읽기 전용) — 와 "AI용 프롬프트 복사 → 외부 AI → 응답 JSON 붙여넣기" 위임 패턴을 Task 2 창에서도 동일하게 재사용한다. 새 CLI 실행 코드를 만들지 않는다.

- 템플릿 기반 자동 초안 생성(PromptBuilder.Build)은 창의 "보조" 기능으로 유지한다.
- 창에는 Task 1과 동일하게 "AI용 프롬프트 복사" / "AI 응답 JSON 불러오기" 버튼을 두어 MCP 없는 AI도 지원한다.
- 프롬프트/스키마 문자열 생성 코드는 MCP 도구와 창이 공유한다.

## 6. 완료 조건

- 체크리스트: [Task2_체크리스트.md](../checklist/Task2_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 후 다음 Task(Task 3)에 착수한다.**
