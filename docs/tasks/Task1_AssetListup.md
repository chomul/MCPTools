# Task 1 — AssetListup 도구

> 원본 계획: ../PLAN.md §4 Phase 1

## 1. 목표

기획서(md/txt)와 현재 프로젝트 스캔 결과를 입력으로 받아, 생성할 이미지/UI/사운드 목록 문서(`AssetList.json`)를 만드는 1단계 도구를 완성한다. 각 항목에는 적용 대상 프리팹 경로와 UI 여부가 필수로 기록된다.

## 2. 선행 조건

- Task 0 산출물: 폴더 구조, asmdef, `MCPToolSettings`, `McpToolRegistry`(MCP 등록 구조)
- 기획서 파일이 `Assets/Docs/`에 존재한다 (.md/.txt)

## 3. 구현 항목

1. **`AssetListupWindow : EditorWindow`** — 메뉴 `Tools/MCP/Asset Listup`
   - 기획서 파일 선택(`Assets/Docs/` 내 .md/.txt), 프로젝트 스캔 실행, 목록 편집(추가/삭제/수정), 저장
2. **`ProjectScanner`** — 현재 프로젝트의 프리팹/씬을 스캔해 이미지·UI 슬롯 후보 추출
   ```csharp
   public static List<ScanEntry> ScanPrefabs(string rootPath); // Image/RawImage/SpriteRenderer/AudioSource 슬롯 수집
   ```
3. **`AssetListBuilder`** — 기획서 텍스트 + 스캔 결과 → `AssetListDocument` 생성·병합
   - 각 항목에 **적용 대상 프리팹 경로와 UI 여부를 필수 기록** (누락 시 확인 후 저장(상태=대상 미정 기록))
4. **산출물 직렬화** — `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json`
5. **MCP 도구 노출** — `mcptools_asset_scan` / `mcptools_asset_list_save` (AI 위임형 2도구)

## 4. 산출물

- `AssetListupWindow`, `ProjectScanner`, `AssetListBuilder` (Editor 코드)
- `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json` (실행 산출물)
- `mcptools_asset_scan` / `mcptools_asset_list_save` MCP 도구

## 5. MCP 도구 — AI 중립 설계 (scan/save 분리, 실제 구현 기준)

목록 "작성" 지능은 AI(MCP 클라이언트 또는 웹 AI)/사용자에 위임한다. 도구는 재료 제공(scan)과 검증·저장(save)만 담당한다.

| 도구명                     | 파라미터                                                                                                                                                    | 반환                                                                                                                                                                                                     |
| -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `mcptools_asset_scan`      | `designDocPath: string?` (Assets/ 상대 .md/.txt, 파일 없으면 오류), `scanRootPath: string` (기본 "Assets")                                                  | `{ success, message, data: { designDocPath?, designDocText?(기획서 지정 시), scanRootPath, scanEntries: {prefabPath, objectPath, componentType, currentAssetName, isUI}[], itemSchema, instructions } }` |
| `mcptools_asset_list_save` | `items: object[]` (필수, itemSchema 형식. id 생략 시 자동 부여), `outputPath: string?`, `designDocPath: string?`·`scanRootPath: string?` (문서 메타 기록용) | `{ success, message, data: { outputPath, itemCount, warnings } }` — 대상 프리팹/UI 여부 미기록 경고가 있어도 저장은 수행되고 warnings로 반환                                                             |

## 6. 완료 조건

- 체크리스트: [Task1\_체크리스트.md](../checklist/Task1_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 후 다음 Task(Task 2)에 착수한다.**
