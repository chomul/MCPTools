# Task 4 체크리스트 — AssetApplier 도구

> Task 문서: [Task4_AssetApplier.md](../tasks/Task4_AssetApplier.md) · 원본 계획: ../PLAN.md §4 Phase 4

## 1. 구현 체크리스트

- [x] `AssetApplierWindow : EditorWindow` — 메뉴 `Tools/MCP/4. Asset Applier` (AssetList + GenerationResult 로드, 적용 대상 미리보기, 개별/일괄 적용)
  - 구현 결과: AssetList JSON 드롭다운 로드 → 항목별 상태 배지(확정본 없음/검증 실패/적용 준비/적용됨) 목록 + 현재 값 → 새 확정본 비교 미리보기(썸네일/이름). [선택 적용]/[일괄 적용(검증 통과 N개)] 버튼과 성공/실패 요약 다이얼로그, AssetDatabase.SaveAssets 마무리. 메뉴 priority 4.
  - 검증 상태: refresh_unity(compile request) + read_console 오류 0건. 창 UI는 에디터 테스트 항목으로 확인 필요.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplierWindow.cs`
- [x] `AssetApplier.ApplyToPrefab` — 컴포넌트 탐색(Image.sprite / RawImage.texture / SpriteRenderer.sprite / AudioSource.clip) → Undo 등록 → 프리팹 에셋 저장
  - 구현 결과: `AssetDatabase.LoadAssetAtPath<GameObject>`로 프리팹 에셋을 직접 얻어 대상 Transform 탐색(루트 이름 포함 경로 허용) → 컴포넌트 판정(audio→AudioSource, UI→Image/RawImage, 그 외→SpriteRenderer) → `Undo.RecordObject` 후 값 할당(uGUI는 어셈블리 참조 없이 SerializedProperty m_Sprite/m_Texture) → `PrefabUtility.SavePrefabAsset`. **Undo 방식 선택 근거**: `LoadPrefabContents`는 분리된 임시 복사본이라 Undo가 불가능하므로, 임포트된 에셋 직접 수정 + RecordObject + SavePrefabAsset 방식을 채택 (Ctrl+Z로 실제 되돌아가며, 되돌린 상태는 다음 저장 시 파일 반영). ApplyResult(success/prefabPath/objectPath/appliedAssetPath/message) 반환. 확정본 자동 탐색(FindConfirmedAssetPath): GenerationResults.json outputPath 우선 → Generated/Images/{id}.png·Audio/{id}.* 폴백.
  - 검증 상태: 컴파일 오류 0건. 실제 적용/Undo 동작은 에디터 테스트 항목으로 확인 필요.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplier.cs`
- [x] 안전 장치 — 적용 전 프리팹·내부 경로·컴포넌트 존재 검증(실패 사유 표시), Undo 지원, 프리팹 에셋 자체 수정 + AssetDatabase.SaveAssets 마무리
  - 구현 결과: `ValidateItem(item, assetPath)`이 실패 사유 문자열 목록 반환 — 프리팹 미존재, targetObjectPath 미존재, 기대 컴포넌트 없음, 확정본 없음/파일 없음, 임포트 타입 불일치(Image·SpriteRenderer→Sprite, RawImage→Texture2D, AudioSource→AudioClip). ApplyToPrefab은 검증 통과 시에만 적용하며 씬 인스턴스/프리팹 스테이지는 건드리지 않고 에셋만 수정. 창·MCP 양쪽 모두 적용 후 SaveAssets.
  - 검증 상태: 컴파일 오류 0건. 존재하지 않는 경로 오류 응답은 MCP 호출로 확인 완료(아래 항목).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplier.cs`, `AssetApplierWindow.cs`, `AssetApplierTool.cs`
- [x] MCP 도구 노출 — `mcptools_apply_asset` / `mcptools_apply_all`
  - 구현 결과: McpToolRegistry 등록(AssetApplierTool.cs) + McpForUnityAdapter.cs에 `[McpForUnityTool]` 어댑터 클래스 2종(McpToolsApplyAssetTool/McpToolsApplyAllTool, Parameters 스키마 포함) 양쪽 모두 추가. apply_asset → data:{prefabPath,objectPath,appliedAssetPath}, apply_all → data:{applied:[...],failed:[{id,reason}]}.
  - 검증 상태: MCP로 존재하지 않는 경로 호출 → 두 도구 모두 `{"success":false,"message":"AssetList JSON을 찾을 수 없습니다: ..."}` 정상 응답 확인 (등록 확인 완료).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplierTool.cs`, `MCPToolTest/Assets/MCPTools/Editor/Common/McpForUnityAdapter.cs`, `MCPToolTest/Assets/MCPTools/README.md`(4단계 사용법·도구 문서 추가)

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

- [ ] `Tools/MCP/4. Asset Applier` 창에서 적용 대상 목록과 미리보기(현재 값 → 새 확정본)가 표시됨
- [ ] 개별 적용 시 대상 프리팹의 Image에 선택 이미지가 반영됨 (프리팹 에셋을 열어 확인)
- [ ] Ctrl+Z로 적용이 되돌려짐
- [ ] 존재하지 않는 프리팹 경로/내부 경로 항목은 "검증 실패" 배지 + 선택 시 사유가 표시됨
- [ ] 일괄 적용 후 성공/실패 요약이 정확함
- [ ] MCP로 `mcptools_apply_asset` 호출 시 창의 [선택 적용]과 동일 결과

## 3. 보완 — 씬 직접 배치 오브젝트 적용 지원 (2026-07-24)

- [x] `AssetApplier.Apply`(프리팹/씬 자동 분기 진입점) + `ApplyToScene` 신설
  - 구현 결과: `targetScenePath` 항목은 씬 적용으로 분기. 씬이 이미 열려 있으면 그대로 사용해 값 변경 + `MarkSceneDirty`(저장은 사용자 몫, 열린 씬에서 Ctrl+Z 동작), 열려 있지 않으면 Additive로 열어 적용 후 `EditorSceneManager.SaveScene` 저장·닫기. 계층 경로 탐색(`FindSceneTransform`, 씬 루트 기준)과 컴포넌트 판정·SerializedProperty 할당·Undo.RecordObject는 프리팹 적용과 동일 로직(`AssignAsset` 공용화). `ValidateItem`에 씬 분기(씬 파일 존재·상호 배타·objectPath 필수, 열려 있는 씬에 한해 오브젝트/컴포넌트 검증 — 닫힌 씬은 적용 시점에 확인). `GetCurrentValue`도 열린 씬에 한해 씬 항목 미리보기 지원.
  - 검증 상태: Unity MCP 브리지 미기동으로 컴파일/실호출 미검증 (아래 에디터 테스트 항목에서 확인 필요).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplier.cs`
- [x] `AssetApplier.ApplyBatch` — 일괄 적용 시 같은 씬 항목은 묶어서 씬을 한 번만 열고 처리 (프리팹 항목은 개별 처리)
  - 관련 파일: `AssetApplier.cs`, `AssetApplierWindow.cs`(일괄/개별 적용을 ApplyBatch 경유로 변경), `AssetApplierTool.cs`(apply_all이 ApplyBatch 사용)
- [x] AssetApplierWindow — 씬 항목 목록 표시([.../씬] 태그), 상세에 "씬" 경로 표시, 개별/일괄 적용 지원
- [x] MCP `mcptools_apply_asset`/`mcptools_apply_all` — 파라미터 변경 없이 씬 항목 자동 분기, 반환 data에 scenePath 추가
  - 관련 파일: `AssetApplierTool.cs`, `Common/McpForUnityAdapter.cs`(설명 갱신), `README.md`

### 에디터 테스트 (씬 적용 보완)

- [ ] 씬 항목(targetScenePath) 적용 시, 열려 있는 씬이면 값이 즉시 반영되고 씬이 dirty 표시되며 Ctrl+Z로 되돌려짐
- [ ] 닫혀 있는 씬 항목 적용 시 Additive로 열려 적용·저장 후 닫히고, 씬 파일에 값이 반영됨
- [ ] 같은 씬의 여러 항목을 일괄 적용하면 씬이 한 번만 열려 처리되고 성공/실패 요약이 정확함
- [ ] MCP `mcptools_apply_asset`으로 씬 항목 적용 시 반환 data의 scenePath가 채워짐

## 4. 보완 — 오디오 임의 컴포넌트 필드 적용 지원 (2026-07-24)

- [x] AssetList 항목에 선택 필드 `targetComponent`/`targetField` 추가 (오디오 전용, 하위 호환)
  - 구현 결과: `AssetListItem`에 `targetComponent`(컴포넌트 타입 이름)·`targetField`(직렬화 필드 경로) 문자열 필드와 `HasCustomAudioTarget` 프로퍼티 추가. `ToDictionary`/`FromDictionary`에 직렬화 반영 — 필드가 없는 기존 JSON은 빈 문자열로 복원되어 기존 AudioSource.clip 동작이 그대로 유지됨.
  - 검증 상태: 컴파일은 Unity 에디터에서 확인 필요.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListDocument.cs`
- [x] `AssetApplier` — 임의 컴포넌트의 직렬화 AudioClip 필드 적용 (검증 + SerializedObject 할당)
  - 구현 결과: `ExpectedComponentNames`가 오디오+targetComponent 지정 시 해당 타입 이름을 반환(기존 컴포넌트 탐색 로직이 `GetType().Name` 비교라 사용자 MonoBehaviour도 매칭됨). `ValidateItem`에 (1) targetComponent/targetField 한쪽만 지정 시 실패, (2) `ValidateAudioField` — `new SerializedObject(component).FindProperty(targetField)` 존재 및 ObjectReference(`PPtr<$AudioClip>`/`PPtr<$Object>`) 검증(한국어 사유 메시지), (3) 오디오 항목은 컴포넌트 종류와 무관하게 AudioClip 임포트 검증. `AssignAsset`이 `Undo.RecordObject` 후 `SetObjectProperty(component, targetField, clip)`으로 할당 — 프리팹(`SavePrefabAsset`)/씬 적용 공용, Ctrl+Z 지원. `GetCurrentValue`도 해당 필드 값을 반환(창 미리보기).
  - 검증 상태: 컴파일/실동작은 에디터 테스트 항목으로 확인 필요.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplier.cs`
- [x] AssetApplierWindow — 상세에 "대상 필드" (`컴포넌트.필드 (AudioClip)`) 표시, 현재 값 미리보기가 필드 값을 표시
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplierWindow.cs`
- [x] MCP `mcptools_apply_asset` 설명 갱신 — 파라미터 변경 없음 (targetComponent/targetField는 AssetList JSON으로 전달됨)
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplierTool.cs`, `MCPToolTest/Assets/MCPTools/README.md`(4단계 사용법·AssetList 필드 표 갱신)

### 에디터 테스트 (오디오 임의 필드 적용)

- [ ] `[SerializeField] AudioClip jumpSound`를 가진 MonoBehaviour를 프리팹에 붙이고, AssetList 항목(assetType:"audio")에 `targetComponent`(스크립트 클래스명)·`targetField`("jumpSound")를 지정해 적용 → 프리팹 에셋의 해당 필드에 AudioClip이 할당됨
- [ ] 적용 후 Ctrl+Z로 필드 값이 되돌려짐
- [ ] 창에서 해당 항목 선택 시 "대상 필드" 표시 및 미리보기 "현재 값"에 필드의 기존 클립이 표시됨
- [ ] 잘못된 컴포넌트명/필드명/AudioClip이 아닌 필드 지정 시 "검증 실패" 배지와 한국어 사유가 표시됨
- [ ] targetComponent/targetField가 없는 기존 오디오 항목은 종전대로 AudioSource.clip에 적용됨

## 5. 보완 — 적용 대상 수동 설정/수정 (2026-07-24)

- [x] AssetApplierWindow 상세 "적용 대상 (수정 가능)" 박스 — 프리팹 항목: GameObject ObjectField(allowSceneObjects=false, 변경 시 `AssetDatabase.GetAssetPath`로 targetPrefabPath 반영 + 경로 라벨 표시) + 내부 경로 드롭다운(프리팹 전체 Transform 계층 경로, "(루트)"=빈 경로 포함. 현재 값이 계층에 없으면 텍스트 필드 + "(선택...)" 드롭다운 병행). 씬 항목: 내부 경로 텍스트 필드. 오디오 항목: targetComponent/targetField 텍스트 필드.
  - 구현 결과: `EditorGUI.BeginChangeCheck`로 수정 감지 → 즉시 `RevalidateState`(확정본 재탐색 + `AssetApplier.ValidateItem`)로 배지/사유 갱신, `_targetsDirty` 표시.
  - 검증 상태: 에디터 컴파일/실행 확인 필요 (아래 테스트 항목).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetApplier/AssetApplierWindow.cs`
- [x] [저장] 버튼 — 로드했던 AssetList JSON 경로(`_loadedListPath`)에 `AssetListBuilder.Save(_document, path)`로 수정 값을 그대로 다시 기록 (기존 저장 메서드 재사용, 스키마 동일). 저장 성공 시 상태 메시지 + 알림, dirty 해제.
  - 관련 파일: `AssetApplierWindow.cs`, `AssetListup/AssetListBuilder.cs`(재사용), `README.md`(4단계 사용법 갱신)

### 에디터 테스트 (적용 대상 수동 수정)

- [ ] 프리팹 항목 선택 후 ObjectField에 다른 프리팹을 드래그하면 경로 라벨이 즉시 갱신되고, 내부 경로 드롭다운에 새 프리팹의 계층 경로가 나열됨
- [ ] 내부 경로 드롭다운에서 다른 경로를 고르면 검증이 즉시 재실행되어 배지("검증 실패"/"적용 준비")와 사유·미리보기가 갱신됨
- [ ] 현재 targetObjectPath가 프리팹 계층에 없는 항목은 텍스트 필드 + "(선택...)" 드롭다운으로 표시되고, 드롭다운 선택으로 교정 가능함
- [ ] 오디오 항목에서 targetComponent/targetField를 수정하면 검증(필드 존재/AudioClip 타입)이 재실행됨
- [ ] [저장] 후 AssetList JSON 파일을 열어 수정 값이 기록되어 있고, [로드]를 다시 해도 수정 값이 유지됨. 저장 전에는 "수정됨 (저장되지 않음)" 라벨이 표시됨
