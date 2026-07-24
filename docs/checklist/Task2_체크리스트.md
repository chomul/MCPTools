# Task 2 체크리스트 — PromptBuilder 도구

> Task 문서: [Task2_PromptBuilder.md](../tasks/Task2_PromptBuilder.md) · 원본 계획: ../PLAN.md §4 Phase 2

## 1. 구현 체크리스트

- [x] `PromptBuilderWindow : EditorWindow` — 메뉴 `Tools/MCP/Prompt Builder` (AssetList 선택 → 초안 생성 → 수동 편집 → 저장)
  - 구현 결과: 메뉴 `Tools/MCP/Prompt Builder`. Task 1 창과 동일한 레이아웃 — 상단 "AI 연동" 박스(에셋 목록 JSON 드롭다운(docsRootPath의 AssetList_*.json, 최신 파일 우선 정렬로 기본 선택 + 새로고침) / 템플릿 드롭다운 / AI 도구·다시 검색·타임아웃·프로젝트 코드 탐색 토글 / 큰 [선택한 AI로 프롬프트 생성] 버튼) → "로컬 AI 미사용 시 (수동 방식)" Foldout(템플릿 초안 생성(보조)/AI용 프롬프트 복사/AI 응답 JSON 불러오기, EditorPrefs 기억, CLI 미감지 시 자동 펼침) → 프롬프트 표(남은 공간 채움) → 하단 고정 상태 메시지+[항목 추가]/[저장]. 표 열: ID/이름/종류/UI/대상 프리팹/Positive/Negative/삭제, 줄무늬 배경, positive 빈 행은 경고색+툴팁. 오류는 EditorUtility.DisplayDialog 한국어 안내. 보조 창 `PromptSetJsonImportWindow`(붙여넣기/파일 불러오기, 코드 펜스 자동 제거, 교체/병합 — 병합은 같은 id 덮어쓰기).
  - 구현 결과(추가): 긴 프롬프트 확인·편집 UI 개선 — 표의 Positive/Negative 셀을 편집 TextField에서 한 줄 요약 라벨(전체 텍스트 툴팁 포함, 빈 값은 안내 플레이스홀더)로 변경하고, 행 클릭 시 선택(선택 행은 에디터 선택색으로 강조, 삭제 시 선택 인덱스 보정, 목록 교체 시 선택 초기화). 표 하단(저장 바 위)에 "선택 항목 편집" 상세 영역 추가 — 선택 항목의 positive(높이 64)/negative(높이 48)를 word-wrap 멀티라인 TextArea + 개별 스크롤로 전체 확인·편집, [선택 해제] 버튼 제공. 미선택 시 "행을 클릭하면 편집 가능" 안내만 표시. 다른 파일·기능 변경 없음.
  - 검증 상태: Unity 6000.5.2f1 실제 DLL 참조 dotnet build 컴파일 검증 통과 (기본 / MCPTOOLS_HAS_MCPFORUNITY 2구성 오류 0, UI 개선 후 재검증 포함). 에디터 동작 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptBuilderWindow.cs`
- [x] `PromptTemplate` — 모델 스타일별 프롬프트 규칙 (공통 스타일 접두어, 품질 태그, 공통 negative, UI 특화 태그 자동 부여)
  - 구현 결과: `PromptTemplate` 직렬화 클래스 — 필드 stylePrefix/qualityTags/commonNegative/uiExtraTags("clean edges, transparent background, game ui icon, centered composition, flat design")/audioStylePrefix/audioNegative. 기본 템플릿은 코드 내장(`CreateDefault`, 배포 자기완결), 추가 템플릿은 `Assets/MCPTools/Editor/PromptBuilder/Templates/<이름>.json` JSON 리소스를 `LoadByName`으로 로드(누락 필드는 기본값 유지, 파일 없으면 기본 템플릿 폴백). `ListTemplateNames`로 창 드롭다운 목록 제공, `ToDictionary`로 MCP scan 반환/AI 프롬프트 재료 공유.
  - 검증 상태: 컴파일 검증 통과 (2구성 오류 0). 에디터 동작 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptTemplate.cs`
- [x] `PromptBuilder.Build` — `AssetListDocument` → `PromptSetDocument` 변환
  - 구현 결과: `PromptBuilder.Build(AssetListDocument, PromptTemplate)` — 항목별 초안 생성: 이미지 항목 positive = stylePrefix + 이름/설명 + qualityTags, UI 항목(isUI 또는 assetType=="ui")은 uiExtraTags 자동 삽입, 오디오 항목(assetType=="audio")은 이미지 태그 없이 audioStylePrefix + 이름/설명(negative는 audioNegative). id/name/assetType/isUI/targetPrefabPath/targetObjectPath/description은 1단계 항목에서 그대로 승계. 부속: `LoadAssetList`(파일 없음/형식 오류 시 한국어 예외), `Validate`(빈 이름/positive/대상 경로 경고), `Save`(폴더 자동 생성 + ImportAsset). 데이터 모델 `PromptItem`/`PromptSetDocument`는 Task 1 패턴의 MiniJson ToDictionary/FromDictionary 왕복 지원.
  - 검증 상태: 컴파일 검증 통과 (2구성 오류 0). 실제 초안 품질은 에디터 테스트에서 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptSetDocument.cs`
- [x] 산출물 직렬화 — `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json`
  - 구현 결과: `PromptBuilder.Save` — outputPath 생략 시 `settings.docsRootPath`(기본 Assets/Docs)에 `PromptSet_{yyyyMMdd_HHmm}.json` 자동 이름, 폴더 없으면 생성, MiniJson 직렬화 후 AssetDatabase.ImportAsset. 문서 메타: assetListPath/templateName/createdAt.
  - 검증 상태: 컴파일 검증 통과. 에디터에서 저장·Project 뷰 반영 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptSetDocument.cs`
- [x] MCP 도구 노출 — `mcptools_prompt_scan` / `mcptools_prompt_save` (AI 중립 2도구, Task 1 패턴)
  - 구현 결과: `PromptBuilderTool`([InitializeOnLoad])이 McpToolRegistry에 등록. `mcptools_prompt_scan` — 파라미터 assetListPath(필수, 없으면 오류)/templateName(선택), 반환 data { assetListPath, assetItems, template, promptSchema, instructions(프로젝트 파일 직접 읽기 안내 포함 GetInstructions(true)) }. `mcptools_prompt_save` — 파라미터 items(필수)/assetListPath·templateName·outputPath(선택), Validate 경고(빈 positive 등)를 warnings로 반환하되 저장은 수행, 반환 data { outputPath, itemCount, warnings }. McpForUnityAdapter.cs에 `[McpForUnityTool]` 어댑터 `McpToolsPromptScanTool`/`McpToolsPromptSaveTool` 추가 — Task 1에서 발견된 이슈 재발 방지로 `Parameters` 중첩 클래스 + `[ToolParameter]` 스키마를 처음부터 포함(스키마 반영에는 MCP 서버 재시작 필요).
  - 검증 상태: 컴파일 검증 통과 (기본 / MCPTOOLS_HAS_MCPFORUNITY 2구성 오류 0). MCP 실호출 검증 완료(2026-07-21): `mcptools_prompt_scan`(AssetList_20260721_0456.json → assetItems 38건+template+promptSchema+instructions 반환), `mcptools_prompt_save`(빈 positive 포함 2항목 → 저장 수행+warnings 3건 반환) 정상. 검증용 임시 파일은 삭제함.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptBuilderTool.cs`, `MCPToolTest/Assets/MCPTools/Editor/Common/McpForUnityAdapter.cs`
- [x] AI 위임 UI — Task 1 공용 `AiCliRunner` 재사용 + "AI용 프롬프트 복사 / AI 응답 JSON 불러오기" 지원 (새 CLI 실행 코드 금지)
  - 구현 결과: 새 CLI 실행 코드 없이 `AiCliRunner.GetInstalledTools/RunAsync`만 호출 (감지 드롭다운+직접 입력+클립보드 복사만, 다시 검색, 타임아웃, 취소, 탐색 모드 allowReadTools+projectRoot 작업 디렉터리 — Task 1 창과 동일 패턴, EditorPrefs 키만 MCPTools.PromptBuilder.*로 분리). 프롬프트/스키마/파싱은 공유 정적 클래스 `PromptSetPromptBuilder`에 구현 — GetPromptSchema, GetInstructions(탐색 힌트 선택), BuildPrompt/BuildExplorationPrompt(목록 요약+템플릿+스키마+"JSON 배열만 출력" 지침), ParseItemsJson(코드 펜스 제거, {"items":[...]} 허용, 한국어 FormatException), ItemsFromObjects(MCP items 처리 공용). AI 실행 실패/파싱 실패 시 다이얼로그 안내 + 응답 원문을 불러오기 창에 자동 주입.
  - 검증 상태: 컴파일 검증 통과 (2구성 오류 0). 실제 CLI 실행은 에디터 테스트에서 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptSetPromptBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptBuilderWindow.cs`, `MCPToolTest/Assets/MCPTools/Editor/Common/AiCliRunner.cs`(미변경, 재사용)

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

- [ ] `Tools/MCP/Prompt Builder` 창에서 Phase 1의 AssetList JSON을 불러올 수 있음 (드롭다운 기본 선택이 최신 파일)
- [ ] 항목별 positive/negative 프롬프트 초안이 자동 생성됨 ([템플릿 초안 생성(보조)] 또는 AI 실행)
- [ ] UI 항목에 UI 특화 태그(clean edges, transparent background, game ui icon 등)가 자동 포함됨
- [ ] 오디오 항목(assetType=audio)의 프롬프트에 이미지 태그가 붙지 않고 사운드 성격으로 생성됨
- [ ] 프롬프트를 수동 편집 후 저장하면 `Assets/Docs/PromptSet_*.json`에 반영됨
- [ ] 존재하지 않는 AssetList 경로 지정 시 명확한 오류 메시지 (MCP: success:false + 한국어 message / 창: 다이얼로그)
- [ ] MCP로 `mcptools_prompt_scan` → `mcptools_prompt_save` 순서 호출 시 PromptSet JSON이 저장되고 outputPath·itemCount·warnings가 반환됨
- [ ] "AI용 프롬프트 복사" → 외부 AI 응답 JSON 붙여넣기([AI 응답 JSON 불러오기])로 프롬프트 목록이 채워짐
- [ ] positive가 빈 행이 경고색으로 표시되고, 저장 시 "빈 프롬프트 N건" 확인 다이얼로그가 뜸
- [ ] [선택한 AI로 프롬프트 생성] 실행 시 에디터가 멈추지 않고, 완료 시 교체/병합 다이얼로그 후 표에 반영됨 (병합은 같은 id 덮어쓰기)
- [ ] 긴 프롬프트 전체 확인·편집 가능 — 표 행 클릭 시 하단 상세 영역에 positive/negative 전체가 word-wrap TextArea로 표시·편집되고, 표 셀 요약에 마우스를 올리면 전체 텍스트 툴팁이 보임
