# Task 4 — AssetApplier 도구

> 원본 계획: ../PLAN.md §4 Phase 4

## 1. 목표

확정된 생성물을 AssetList에 기록된 대상 프리팹/UI에 적용(Sprite·Texture·AudioClip)하는 4단계 도구를 완성한다. 적용 전 검증과 Undo(Ctrl+Z)를 지원하며, 씬이 아닌 프리팹 에셋 자체를 수정한다.

## 2. 선행 조건

- Task 1 산출물: `AssetList_*.json` (항목별 대상 프리팹 경로·내부 오브젝트 경로)
- Task 3 산출물: `GenerationResult` 및 확정본 (`Assets/Generated/Images/…`)

## 3. 구현 항목

1. **`AssetApplierWindow : EditorWindow`** — 메뉴 `Tools/MCP/Asset Applier`
   - AssetList + GenerationResult 로드 → 적용 대상 미리보기(프리팹 경로, 내부 오브젝트 경로, 현재/새 이미지 비교) → 개별/일괄 적용
2. **`AssetApplier`** — 실제 적용 로직
   ```csharp
   public static ApplyResult ApplyToPrefab(AssetListItem item, string assetPath);
   // 내부: PrefabUtility.LoadPrefabContents → 대상 컴포넌트 탐색
   //       (Image.sprite / RawImage.texture / SpriteRenderer.sprite / AudioSource.clip)
   //       → Undo.RecordObject → PrefabUtility.SaveAsPrefabAsset → UnloadPrefabContents
   ```
3. **안전 장치**
   - 적용 전 대상 프리팹·내부 경로·컴포넌트 존재 검증, 실패 항목은 이유와 함께 결과 목록에 표시
   - Undo 지원 (에디터에서 Ctrl+Z로 되돌리기 가능)
   - 씬이 아닌 **프리팹 에셋 자체를 수정** (PrefabUtility 경유), AssetDatabase.SaveAssets 마무리
4. **MCP 도구 노출** — 단건 적용 / 일괄 적용 2종

## 4. 산출물

- `AssetApplierWindow`, `AssetApplier` (Editor 코드)
- 에셋이 적용된 대상 프리팹 (실행 산출물)
- MCP 도구 2종 (`mcptools_apply_asset` / `mcptools_apply_all`)

## 5. MCP 도구

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_apply_asset` | `assetListPath: string`, `assetItemId: string`, `assetPath: string?` (생략 시 확정본 자동 탐색) | `{ success, message, data: { prefabPath, objectPath, appliedAssetPath } }` |
| `mcptools_apply_all` | `assetListPath: string` | `{ success, data: { applied: [...], failed: [{ id, reason }] } }` |

## 6. 완료 조건

- 체크리스트: [Task4_체크리스트.md](../checklist/Task4_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 후 다음 Task(Task 5)에 착수한다.**
