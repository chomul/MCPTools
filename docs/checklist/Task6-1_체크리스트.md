# Task 6-1 체크리스트 — 시트 임포트 행 이름 지정 + 애니메이션 클립 생성

> Task 문서: [Task6-1_애니메이션클립.md](../tasks/Task6-1_애니메이션클립.md)
> 착수: 2026-07-25
> **불변 조건: 격자 검출·배경 제거·셀 콘텐츠 판정·피벗 계산 로직은 수정하지 않는다.** (현재 분리 품질 유지)

## 1. 구현 체크리스트

### A. 임포트 행 이름 지정 + 검출/적용 분리

- [x] 임포트 섹션 전용 행 목록 — 행 추가/삭제/이름 직접 입력/순서 변경, [1번 행 목록 가져오기] 버튼 (§2.1)
- [x] [배경 제거 + 격자 검출] / [슬라이스 적용] 2단계 분리, 검출 결과 표(행별 프레임 수·이름 입력·프레임 썸네일·포함 체크박스) (§2.2)
- [x] 자동 `rowN` 이름 제거 — 빈 이름이 있으면 적용 차단 + 해당 행 안내
- [x] 제외한 프레임은 슬라이스 미생성, 프레임 번호는 포함분만 1부터 연속 부여
- [x] 알파 비율이 낮은 셀에 "비어 보임" 표시 (판정 기준 자체는 미변경)
- [x] **"비어 보임" 셀 자동 제외 (추가 요청, 2026-07-25)** — 검출 결과에서 비어 보이는 셀을 프레임이 없는 것처럼 처리
  - 구현 결과: `DetectGrid`가 셀을 만들 때 `include = !looksEmpty`로 설정해 전경 비율 2% 미만(`EmptyCellContentRatio`, 기존 `LowContentDisplayRatio` 개명) 셀을 자동 제외한다. 셀 자체는 표에 남으므로 오검출이면 사용자가 다시 체크해 되살릴 수 있다. 자동 제외로 프레임이 0이 된 행은 슬라이스에서 빠지므로, **행 동작명은 남은 행에만 순서대로 배정**하도록 `Import`(MCP)와 창의 `RunDetect`를 함께 고쳤다(여백 밴드 행 때문에 이름이 밀리던 문제 해소). `SpriteSheetDetection`에 `LooksEmptyFrameCount`·`IncludedRowCount`를 추가해 검출 완료 메시지("비어 보이는 프레임 N개를 자동 제외했습니다")와 MCP `dryRun` 응답(`autoExcludedEmptyFrameCount`, 행별 `autoExcludedEmptyCells`)에 노출한다. `dryRun` 응답의 `rowCount`/`totalFrameCount`/`framesPerRow`도 자동 제외 반영값으로 바뀌었다(기존 `emptyLookingFrames` 키 대체).
  - 불변 조건 유지: 셀 후보 판정(`GridCellContentRatio` 0.005)·격자 경계 검출·배경 제거·피벗 계산은 미변경. 바뀐 것은 검출된 셀의 **기본 포함 여부**뿐이다.
  - 검증 상태: 컴파일 확인 필요. **에디터 동작 확인 필요.**
  - 관련 파일: `.../Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`, `.../Editor/McpForUnityBridge/McpForUnityAdapter.cs`
- [x] `mcptools_spritesheet_import`에 `dryRun` 추가 — 검출 결과만 반환, 행 이름 부족 시 자동 이름 없이 실패 (§2.6)
  - 구현 결과: `ImportGrid`를 `Detect`(검출, 파일 저장·슬라이스 없음 → `SpriteSheetDetection`/`DetectedRow`/`DetectedCell` 반환) + `ApplySlices`(저장·슬라이스)로 분리. 창에 `_importRows`(1번 섹션 `_rows`와 완전 분리)와 검출 결과 표(썸네일은 `CreateCellThumbnail`, 재검출·창 닫기 시 `DestroyImmediate` 해제)를 추가하고, `ValidateForApply`가 빈 이름(행 번호 지목)·중복 이름·포함 프레임 0을 검사해 [슬라이스 적용]을 비활성화한다. 프레임을 전부 제외한 행은 이름 없이 통과(= 행 빼기 수단). `ApplySlices`는 `include` 셀만 순회하며 `frameNo`를 증가시켜 `{동작}_{프레임:00}` 부여. MCP 경로는 `dryRun`이면 `{applied:false, rowCount, framesPerRow[{row, detectedFrameCount, emptyLookingFrames[{frame, contentRatio}]}], cellWidth/Height}`만 반환하고, 행 이름이 부족하면 자동 이름 없이 검출 구성과 조치를 담아 실패한다. IMGUI 안정성을 위해 행/표 개수를 바꾸는 버튼은 `_pendingImportAction`으로 다음 Layout 이벤트에 실행.
  - **불변 조건 검증(주 에이전트)**: `git show HEAD:` 원본과 현재 파일의 공백 제거·정렬 비교로 순수 삭제 43줄이 전부 `ImportGrid` 분할·`rowN` 제거·기대치 기록 관련임을 확인. 슬라이스 rect(`new Rect(xMin, yLow, cellW, cellH)`), `TryComputeFeetPivot` 본문·인자, `GridCellContentRatio`(0.005), `DetectGridBoundaries`/`RefineGridBoundaries`/`ClearGridLineBands`/`hasContent` 판정 루프 모두 원문 그대로. "비어 보임" 표시는 판정 루프를 건드리지 않기 위해 별도 함수 `MeasureContentRatio` + 표시 전용 상수 `LowContentDisplayRatio`(0.02)로 계산.
  - 검증 상태: `validate_script`(standard) 오류 0건, 컴파일 오류 0건. `dryRun` 스모크 테스트 및 `ValidateForApply` 분기 5종 확인. **에디터 동작 확인 필요.**
  - 관련 파일: `.../Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`, `.../Editor/McpForUnityBridge/McpForUnityAdapter.cs`
  - **실사용 진단**: 사용자의 한 행짜리 시트는 기존 코드에서도 **위/아래 1px sliver 행 2개 + 실제 walk 행 1개 = 3행**으로 검출된다. 이것이 "이름이 이상함"(sliver 행에 walk/run/attack이 순서대로 배정)과 "빈칸 스프라이트"(sliver 행이 슬라이스됨)의 원인이었다. 검출 로직은 그대로 두고 표에서 sliver 행 프레임을 제외하는 방식으로 해결한다.

### B. AnimationClip 생성

- [x] `SpriteSheetClipBuilder` — 서브 스프라이트 수집(`LoadAllAssetRepresentationsAtPath`) + `^(?<action>.+)_(?<no>\d+)$` 파싱 → 동작별 클립 생성 (§2.3)
- [x] `MCPToolSettings.spriteAnimationFrameRate`(기본 12), `MCPToolFolders.AnimationsFolder`/`AnimationsDir` 추가
- [x] 루프 기본 규칙(idle/walk/run ON, attack/death OFF) + 행별 토글
- [x] 기존 클립은 새로 만들지 않고 커브·프레임레이트·루프만 덮어쓰기 (확인 다이얼로그)
- [x] AnimatorController 생성 옵션 — State 배치, 기존 컨트롤러는 없는 State만 추가, 프리팹 Animator 연결(`PrefabUtility`+`Undo`) (§2.4)
- [x] 창 "4. 애니메이션" 섹션 — 동작 표/대상 컴포넌트/FPS/대상 프리팹·오브젝트/[클립 생성]·[클립 + Animator 생성] (§2.5)
- [x] `mcptools_spritesheet_build_clips` 신규 도구 (§2.6)
  - 구현 결과: `SpriteSheetClipBuilder.Scan(sheetPath)`가 서브 스프라이트를 greedy 정규식(마지막 언더스코어 기준)으로 파싱해 동작별 그룹 + 번호 오름차순 정렬하고, 규칙 불일치는 `skipped`에 사유를 남긴다. 반환하는 `SpriteSheetClipPlan`은 덮어쓸 클립 목록(`ExistingClipPaths()`)을 **아무것도 기록하지 않고** 미리 제공해 확인 다이얼로그에 쓴다. `Build()`는 `EditorCurveBinding.PPtrCurve` + `SetObjectReferenceCurve`(키 시간 `i/frameRate`), `clip.frameRate`, `Get/SetAnimationClipSettings.loopTime`을 쓰고, 기존 클립은 에셋을 재생성하지 않고 대상이 바뀌어 무효가 된 `m_Sprite` 커브만 제거한 뒤 갱신한다. 대상 타입은 `typeof(SpriteRenderer)` / `Type.GetType("UnityEngine.UI.Image, UnityEngine.UI")`(실패 시 원인·조치 예외). 창 섹션은 [슬라이스 적용] 직후 그 시트를 자동 대상으로 잡고 스캔한다.
  - **설계 판단**: 문서 §2.3(커브 path = 대상 오브젝트 경로)과 §2.4(대상 오브젝트에 Animator)를 그대로 합치면 커브 경로가 Animator 기준 상대 경로라 재생되지 않는다. **Animator는 프리팹 루트, 커브 경로는 대상 오브젝트 경로**로 구현하고 창 안내문·XML 주석·README에 명시했다.
  - 검증 상태: `validate_script`(standard) 오류 0건, 컴파일 오류 0건. 스모크 테스트 — 언더스코어 포함 동작명 파싱, 루프 ON/OFF, 커브 키 8개·`t1=0.0833`(fps 12), 재실행 시 `created=false` 갱신 + 파라미터/트랜지션/기본 State 유지, 커브 경로 이동 시 옛 커브 제거, 프리팹 루트 Animator 연결까지 확인 후 테스트 산출물 삭제. **에디터 동작 확인 필요.**
  - 관련 파일: `.../Editor/SpriteSheet/SpriteSheetClipBuilder.cs`(신규), `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`, `.../Editor/Common/MCPToolFolders.cs`, `MCPToolSettings.cs`, `.../Editor/McpForUnityBridge/McpForUnityAdapter.cs`
  - 참고: `loopActions`를 지정하면 목록에 없는 동작은 강제 OFF가 된다(기본 규칙 무시). 스펙의 "미지정 시 기본 규칙"을 그렇게 해석했다.

### C. AssetApplier 서브 스프라이트 적용

- [x] `AssetListItem.spriteName` / `spriteSheetPath` 추가 + JSON 직렬화 (§2.7)
- [x] `AssignAsset` — `spriteName` 지정 시 `LoadAllAssetRepresentationsAtPath`에서 이름 일치 Sprite 할당, 미지정 시 기존 동작 유지
- [x] 이름 미발견 시 실패 사유에 사용 가능한 스프라이트 이름 목록 표시
- [x] `mcptools_apply_asset`에 `spriteName` 파라미터 추가
- [x] `FindConfirmedAssetPath`에 `SpriteSheets/`를 추가하지 않음 (기존 구분 유지)
  - 구현 결과: `ToDictionary`에 두 키 추가, `FromDictionary`는 기존 `GetString`(키 없으면 `""`) 사용 → **구 JSON 그대로 로드**. `AssignAsset`의 SpriteRenderer/Image 분기가 `ResolveSprite(assetPath, item)`를 호출 — `spriteName`이 비면 예전과 동일한 `LoadAssetAtPath<Sprite>`, 값이 있으면 `FindSubSprite`(`LoadAllAssetRepresentationsAtPath` 이름 일치) 사용. Image는 기존 `SetObjectProperty(component, "m_Sprite", …)` SerializedProperty 경로 유지(uGUI 어셈블리 미참조). `ValidateItem`과 `ResolveSprite` 양쪽에서 `DescribeAvailableSprites`로 사용 가능한 이름 최대 10개 + "외 N개" 안내. `FindConfirmedAssetPath`는 맨 앞에서 `spriteSheetPath`를 반환하고 규칙 경로 탐색 폴더는 그대로. 4단계 창 항목 상세에 시트 `ObjectField(Texture2D)` + 스프라이트 이름 TextField + 서브 스프라이트 이름 드롭다운 추가(기존 `_targetsDirty`/`RevalidateState` 흐름 재사용, 1단계 표는 미변경). MCP `spriteName`은 **호출 한정 오버라이드**(AssetList JSON에 저장하지 않음).
  - 보완(주 에이전트): RawImage 대상에 `spriteName`을 지정하면 무시된 채 시트 전체가 적용되던 것을 `ValidateItem`에서 걸러 "Image로 바꾸거나 이름을 비우라"고 안내하도록 추가.
  - 검증 상태: `validate_script`(standard) 오류 0건(보완분 포함), 컴파일 오류 0건. **에디터 동작 확인 필요.**
  - 관련 파일: `.../Editor/AssetListup/AssetListDocument.cs`, `.../Editor/AssetApplier/AssetApplier.cs`, `AssetApplierTool.cs`, `AssetApplierWindow.cs`
- [x] **시트 드롭다운 + 이름 필드 제거 + 컨트롤러 연결 (추가 요청, 2026-07-25)**
  - 구현 결과: 4단계 창의 시트 지정을 프로젝트 전체 `ObjectField`에서 **확정본 `SpriteSheets/` 폴더의 썸네일 그리드 선택기**로 바꿨다(`DrawSheetGrid`/`DrawSheetCell`, 폴드아웃으로 열고 닫으며 `OnFocus`마다 목록 갱신, 선택 셀은 파란 테두리). 폴드아웃·썸네일·[해제] 클릭이 `GUI.changed`를 세워 "수정됨"으로 오표시되지 않도록 클릭 전 값을 복원하고, 실제로 값이 바뀌는 `SelectSheet`에서만 다시 세운다. 스프라이트 이름 TextField·이름 선택 드롭다운과 `_spriteNameCache`를 제거하고, 대신 `AssetApplier.FindItemSprite`(이름 → 에셋 전체 → **첫 서브 스프라이트**)를 만들어 검증·미리보기·적용이 같은 규칙을 쓰게 했다. 이 폴백이 없으면 Sprite Mode=Multiple 시트는 `LoadAssetAtPath<Sprite>`가 null이라 이름 없이 지정 시 null이 적용됐다. `spriteName` 데이터와 MCP 파라미터는 유지(특정 프레임 지정 경로).
  - 컨트롤러 자동 연결: `AssetListItem.animatorControllerPath` 추가(직렬화 포함, 구 JSON은 빈 값). 다만 **창에서는 컨트롤러를 고르지 않는다** — 한 시트의 클립·컨트롤러는 한 벌뿐이므로 `AssetApplier.ResolveAnimatorControllerPath`가 "명시 값 → 없으면 `SpriteSheetClipBuilder.ControllerPathForSheet(시트)`가 실제로 존재할 때"를 자동으로 고르고, 창은 연결될 대상만 라벨로 보여준다(컨트롤러 경로 규칙은 `ControllerPathForSheet`로 일원화해 `Build`도 이 값을 쓴다). `ApplyToPrefab`이 스프라이트 할당과 같은 저장 안에서 `LinkAnimatorController`로 프리팹 루트에 Animator를 붙여(있으면 재사용) 연결하고 `ApplyResult.linkedControllerPath`에 남긴다(Undo 등록). `ValidateAnimatorController`가 컨트롤러 누락과 함께 **클립 커브 경로가 프리팹 계층에 있는지**(`AnimationUtility.GetObjectReferenceCurveBindings` + `FindTargetTransform`)를 검사해, 연결해도 재생되지 않는 조합을 적용 전에 걸러 안내한다(씬 항목은 자동 연결 대상에서 제외). MCP는 `mcptools_apply_asset`에 `animatorControllerPath` 오버라이드와 `linkedControllerPath` 반환을 추가하고 `_apply_all`도 반환에 포함했다(MCP for Unity 어댑터 스키마에 `spriteName`·`animatorControllerPath` 선언 누락도 함께 보완).
  - 검증 상태: 컴파일 확인 필요. **에디터 동작 확인 필요.**
  - 관련 파일: `.../Editor/AssetApplier/AssetApplierWindow.cs`, `AssetApplier.cs`, `AssetApplierTool.cs`, `.../Editor/AssetListup/AssetListDocument.cs`, `.../Editor/McpForUnityBridge/McpForUnityAdapter.cs`

### D. 문서

- [x] README 스프라이트 시트 절 + 4단계 절 갱신
- [x] CHANGELOG 갱신
  - 구현 결과: README에 **"스프라이트 시트 (SpriteSheet) 사용법"** 절을 신설했다(Task 6 때부터 누락 상태였음) — 프롬프트 생성 / 검출→확인→적용 임포트 흐름 / 클립·Animator 생성. 4단계 절에 "스프라이트 시트의 특정 프레임 적용" 문단, MCP 도구 절에 `mcptools_spritesheet_build_prompt`·`_import`(dryRun 포함)·`_build_clips` 3종과 `mcptools_apply_asset`의 `spriteName`을 추가했다. CHANGELOG `[Unreleased]`에 Added/Changed/Fixed로 기록하고 "검출 알고리즘 무변경"을 명시했다.

## 2. 에디터 테스트 체크리스트

- [ ] **한 행짜리 시트** — 행 목록을 1개로 줄이고 이름을 직접 입력하면 그 이름으로만 슬라이스됨 (`row2` 등 자동 이름 없음)
- [ ] 이름이 빈 행이 있으면 [슬라이스 적용]이 막히고 어느 행인지 안내됨
- [ ] 검출 결과 표에서 **빈칸으로 보이는 프레임의 체크를 해제**하면 그 스프라이트가 생성되지 않고 뒤 번호가 당겨져 연속됨
- [ ] **"비어 보임" 셀이 검출 직후 이미 "제외" 상태**이고, 검출 완료 메시지에 자동 제외 개수가 표시됨
- [ ] 위/아래 여백 sliver 행이 있는 시트 — 그 행이 통째로 빠지고 **동작명이 실제 동작 행에 배정**됨(이름이 밀리지 않음)
- [ ] 자동 제외된 셀을 **다시 체크하면 슬라이스에 포함**됨 (오검출 복구 경로)
- [ ] `mcptools_spritesheet_import` `dryRun=true` 응답의 `rowCount`/`totalFrameCount`가 자동 제외 반영값이고, `autoExcludedEmptyFrameCount`가 함께 옴
- [ ] **슬라이스 결과(프레임 위치·크기·피벗)가 변경 전과 동일** — 분리 품질 회귀 없음
- [ ] walk/run/attack/death 4행 시트 → 클립 4개가 각 행의 프레임 수·순서대로 생성됨
- [ ] walk/run은 루프 ON, attack/death는 OFF로 만들어짐
- [ ] [클립 + Animator 생성] → 컨트롤러의 각 State가 재생되고 지정한 프리팹에 Animator + 컨트롤러가 연결됨
- [ ] 같은 시트로 다시 실행 → 클립이 새로 생기지 않고 갱신되며 기존 컨트롤러의 트랜지션이 유지됨
- [ ] uGUI 미설치 환경에서 Image 대상 선택 시 원인·조치 안내가 표시됨
- [ ] `spriteName`을 지정한 항목이 4단계에서 시트의 해당 프레임으로 적용됨 (MCP 파라미터/JSON 값 경로)
- [ ] 없는 `spriteName`이면 사용 가능한 이름 목록이 안내됨
- [ ] 4단계 창 [시트 고르기 (썸네일)]에 **`SpriteSheets/` 폴더의 시트가 그림으로 모두 나오고**, 클릭하면 선택 강조 + 미리보기에 첫 프레임이 뜬 뒤 [적용]으로 그 프레임이 들어감
- [ ] 스프라이트 시트 창에서 방금 만든 시트가 4단계 창에 포커스를 주면 그리드에 바로 나타남
- [ ] 시트를 고르면 "애니메이터" 줄에 그 시트의 컨트롤러 경로가 뜨고, [적용] → 프리팹 루트에 Animator가 붙고 연결되며 재생 시 애니메이션이 동작함
- [ ] 컨트롤러를 아직 만들지 않은 시트를 고르면 "스프라이트만 적용" 안내가 뜨고 적용은 정상 동작함
- [ ] 클립 커브 경로가 없는 프리팹에 시트를 지정하면 적용 전에 검증 실패로 어느 경로가 없는지 안내됨
- [ ] 컨트롤러가 이미 붙어 있는 프리팹에 다시 적용해도 Animator가 중복 추가되지 않음
- [ ] 썸네일/폴드아웃을 눌러도 값이 바뀌지 않으면 "수정됨 (저장되지 않음)"이 뜨지 않음
- [ ] 씬 항목에는 "애니메이터" 줄이 보이지 않음
