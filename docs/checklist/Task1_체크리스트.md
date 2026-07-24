# Task 1 체크리스트 — AssetListup 도구

> Task 문서: [Task1_AssetListup.md](../tasks/Task1_AssetListup.md) · 원본 계획: ../PLAN.md §4 Phase 1

## 1. 구현 체크리스트

- [x] `AssetListupWindow : EditorWindow` — 메뉴 `Tools/MCP/Asset Listup` (기획서 선택, 스캔 실행, 목록 편집, 저장)
  - 구현 결과: 메뉴 `Tools/MCP/Asset Listup`. 설정(docsRootPath)의 .md/.txt 드롭다운 + 새로고침, 스캔 루트 입력(기본 Assets), [스캔 + 목록 생성], 항목별 편집(이름/설명/종류/대상 프리팹·오브젝트 경로/UI 여부 3택(미지정·UI 아님·UI)/상태), 항목 추가·삭제, 스크롤, 저장. 검증 실패 시 EditorUtility.DisplayDialog로 사유 목록 안내. 한국어 UI, MCPSettingsWindow 스타일 준수.
  - 구현 결과(추가): UI 재배치 — AI 연동 영역(기획서/스캔 루트 → AI 도구·다시 검색·타임아웃·탐색 토글 → 큰 [선택한 AI로 목록 생성] 버튼)을 주 흐름으로 상단 박스 그룹에 배치, 수동 방식 3버튼(스캔+휴리스틱/프롬프트 복사/JSON 불러오기)은 "로컬 AI 미사용 시 (수동 방식)" Foldout(EditorPrefs 기억, AI CLI 미감지 시 자동 펼침)으로 이동, 표가 남은 공간 전부 차지 + 상태 메시지·항목 추가/저장은 하단 고정 박스로 통일.
  - 검증 상태: Unity 6000.5.2f1 실제 DLL 참조 dotnet build 컴파일 검증 통과 (오류 0, UI 재배치 후 2구성 재검증 포함). 에디터 동작 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`
- [x] `ProjectScanner` — 프리팹/씬 스캔으로 Image/RawImage/SpriteRenderer/AudioSource 슬롯 수집
  - 구현 결과: `ScanPrefabs(rootPath)` — AssetDatabase.FindAssets("t:Prefab")로 루트 아래 프리팹을 찾아 Image/RawImage/SpriteRenderer/AudioSource 슬롯 수집(GetComponentsInChildren, 비활성 포함). ScanEntry에 프리팹 경로, 계층 경로, 컴포넌트 종류, 현재 할당 에셋 이름, UI 여부 기록. 잘못된 루트는 경고 후 빈 목록 반환.
  - 검증 상태: 컴파일 검증 통과. 에디터에서 테스트 프리팹 스캔 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/ProjectScanner.cs`
- [x] `AssetListBuilder` — 기획서 텍스트 + 스캔 결과 → `AssetListDocument` 생성·병합 (대상 프리팹 경로·UI 여부 필수, 누락 시 확인 후 저장(상태=대상 미정 기록))
  - 구현 결과: 휴리스틱 파싱 — 키워드(UI/화면/에셋/적/캐릭터/사운드 등) 포함 헤딩 섹션에서만 표 행(첫 셀=이름, 둘째 셀=설명)과 "- 이름 : 설명" 불릿을 추출, 섹션·이름 키워드로 assetType(image/ui/audio) 추정. 스캔 슬롯과 정규화 이름 포함 매칭으로 대상 프리팹/오브젝트 경로·UI 여부 자동 기입, 매칭 안 된 기획서 항목은 대상 비운 채 유지, 매칭 안 된 스캔 슬롯도 항목으로 추가. `Validate()`가 이름/대상 프리팹 경로/UI 여부 미기록 경고 목록 반환 — 창 저장은 미기록 항목이 있으면 확인 다이얼로그 후 상태="대상 미정"으로 기록하고 저장.
  - 검증 상태: 컴파일 검증 통과. 샘플 기획서(콘텐츠기획.md) 대상 실제 추출 결과는 에디터 테스트에서 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListDocument.cs`
- [x] 산출물 직렬화 — `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json`
  - 구현 결과: `AssetListBuilder.Save()` — 설정의 docsRootPath 사용, 폴더 없으면 생성, MiniJson 직렬화 저장 후 AssetDatabase.ImportAsset. AssetListDocument/AssetListItem은 ToDictionary/FromDictionary로 MiniJson 왕복 지원. 항목 필드: id, name, description, assetType, targetPrefabPath, targetObjectPath, isUI(+isUISpecified), status.
  - 검증 상태: 컴파일 검증 통과. 에디터에서 저장·Project 뷰 반영 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListDocument.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListBuilder.cs`
- [x] MCP 도구 노출 — `mcptools_asset_scan` / `mcptools_asset_list_save` (AI 위임형, 기존 `mcptools_asset_listup` 대체)
  - 구현 결과: 기획서 분석을 AI(MCP 클라이언트)에 위임하는 2도구 구조로 재구성. `mcptools_asset_scan` — 파라미터 designDocPath(선택)/scanRootPath(기본 "Assets"), 반환 data { designDocPath·designDocText(기획서 지정 시), scanRootPath, scanEntries(프리팹 슬롯 목록), itemSchema(항목 필드 스키마), instructions(한국어 작성 지침) }. `mcptools_asset_list_save` — 파라미터 items(필수, itemSchema 형식 객체 배열)/outputPath(선택)/designDocPath·scanRootPath(선택, 문서 메타 기록용), AssetListDocument 변환 후 Validate 경고를 warnings로 반환하되 저장은 수행, 반환 data { outputPath, itemCount, warnings }. 스키마/지침은 창의 AI 프롬프트와 공유하는 `AssetListPromptBuilder` 정적 클래스에 구현. McpForUnityAdapter.cs의 `[McpForUnityTool]` 노출도 McpToolsAssetScanTool/McpToolsAssetListSaveTool로 교체.
  - 검증 상태: 컴파일 검증 통과 (MCPTOOLS_HAS_MCPFORUNITY 심볼 포함/미포함 2회 빌드 모두 오류 0). MCP 클라이언트 호출 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupTool.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListPromptBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/Common/McpForUnityAdapter.cs`
- [x] 개선 — 목록 표(엑셀형) UI
  - 구현 결과: AssetListupWindow 항목 표시를 세로 나열에서 표 형태로 변경. 고정 열 너비 헤더 행(ID/이름/종류/UI 여부/대상 프리팹/대상 오브젝트/설명/상태/삭제) + 항목당 한 행, 셀별 편집(TextField/Popup). 헤더와 행이 같은 ScrollView(세로+가로) 안에 있어 가로 스크롤 시 열 정렬 유지. 줄무늬 배경(EditorGUI.DrawRect, 다크/라이트 스킨 대응), 미기록 항목(대상 프리팹/UI 여부 누락)은 옅은 경고색 배경 + ID 툴팁 안내. 항목 추가 시 기존 최대 번호 이후 ID 부여.
  - 검증 상태: 컴파일 검증 통과. 에디터 표시/편집 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`
- [x] 개선 — 기획서 분석 AI 위임 (창 연동)
  - 구현 결과: [AI용 프롬프트 복사] 버튼 — 선택 기획서 원문 + 스캔 슬롯 요약 + itemSchema + "JSON 배열만 출력" 지침을 `AssetListPromptBuilder.BuildPrompt`로 합쳐 systemCopyBuffer에 복사(MCP 없는 웹 AI용). [AI 응답 JSON 불러오기] 버튼 — 보조 창(AssetListJsonImportWindow: TextArea 붙여넣기 + 파일 불러오기)에서 AI 응답 JSON 배열을 파싱해 목록 교체/병합 선택 반영, 코드 펜스 자동 제거, 파싱 실패 시 한국어 오류 다이얼로그. 기존 휴리스틱 버튼은 [스캔 + 휴리스틱 추출(보조)]로 라벨 조정(AssetListBuilder.Build 유지).
  - 검증 상태: 컴파일 검증 통과. 에디터에서 프롬프트 복사/JSON 반영 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListPromptBuilder.cs`
- [x] 개선 — 저장 차단 완화 (대상 미기록 항목 확인 후 저장)
  - 구현 결과: [저장] 시 targetPrefabPath 또는 UI 여부 미기록 항목이 있어도 차단하지 않고, EditorUtility.DisplayDialog로 "대상 미기록 항목 N건이 있습니다. (예시 최대 5건)… 그래도 저장할까요?" 확인 후 [저장] 선택 시 해당 항목 status를 "대상 미정"으로 기록하고 저장, [취소] 시 중단. 항목 0건일 때만 저장 불가 안내. `AssetListBuilder.Validate()`는 MCP warnings 용도로 유지.
  - 검증 상태: 컴파일 검증 통과 (2구성 빌드 오류 0). 에디터 동작 확인 대기.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`
- [x] 개선 — 설치된 AI CLI 감지 + 선택 실행
  - 구현 결과: 공통 정적 클래스 `AiCliRunner`(Editor/Common) 신설 — PATH 탐지(Windows where.exe / macOS·Linux which, 캐시 + 다시 검색), 비대화형 실행(claude `-p`·codex `exec -`·gemini(stdin 파이프)·cursor-agent `-p --output-format text`는 stdin으로 프롬프트 전달, copilot은 임시 파일 방식·--allow-all-tools 미사용), Process 비동기 실행 + UTF-8 명시 + 타임아웃(기본 300초)·취소 지원, .cmd/.bat 셸 스크립트는 cmd.exe 경유. 창에 "AI 연동" 영역 추가 — AI 도구 드롭다운(감지 목록 + 직접 입력... + 클립보드 복사만) + [다시 검색] + 타임아웃 필드 + [선택한 AI로 목록 생성](실행 중 라벨/취소 버튼), 성공 시 응답 파싱 후 교체/병합/취소 다이얼로그로 표 반영, 실패 시 stderr/stdout 요약 다이얼로그 + 응답 원문을 [AI 응답 JSON 불러오기] 창에 자동 주입. 선택 도구명/직접 입력 커맨드/타임아웃은 EditorPrefs로 기억. 기존 수동 경로(프롬프트 복사/JSON 불러오기)와 MCP 도구는 유지·미변경.
  - 구현 결과(추가): .ps1 CLI 감지·실행 지원(Copilot CLI 등) — Windows에서 `where.exe <name>.ps1` 재시도 + PATH 디렉터리 직접 순회(.exe/.cmd/.bat/.ps1) 폴백, .ps1은 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` 경유 실행.
  - 검증 상태: 컴파일 검증 통과 (기본 / MCPTOOLS_HAS_MCPFORUNITY 2구성 빌드 오류 0). 실제 CLI 감지·실행은 에디터 테스트에서 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Common/AiCliRunner.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`
- [x] 개선 — AI 프로젝트 코드 탐색 모드 (헤드리스 CLI가 프로젝트 파일을 직접 읽으며 역할 추론)
  - 구현 결과: `AiCliRunner.RunAsync`에 `allowReadTools`/`workingDirectory` 선택 파라미터 추가 — 탐색 모드에서 `ProcessStartInfo.WorkingDirectory`를 Unity 프로젝트 루트(`Path.GetDirectoryName(Application.dataPath)`)로 지정하고, CLI별 읽기 전용 도구 허용 플래그 적용: claude는 `-p --allowedTools "Read Glob Grep"`(로컬 `claude -p --help`로 플래그 존재 확인, 쓰기/Bash 미허용), codex는 `exec` 기본 샌드박스가 workspace 읽기 허용이라 추가 플래그 불요, gemini는 비대화형에서 읽기 도구 기본 실행(--yolo 미사용), cursor-agent/copilot은 읽기 허용 플래그가 문서상 불확실해 플래그 없이 실행(프롬프트 인라인 재료만으로 동작하는 우아한 성능 저하) — 근거는 코드 주석에 기록. `AssetListPromptBuilder.BuildExplorationPrompt` 신설 — 기존 재료에 "작업 폴더가 프로젝트 루트, Assets/ 아래 스크립트·씬·프리팹을 읽어 역할 추론, 파일 수정 금지, JSON 배열만 출력" 지시 추가(클립보드 복사용 BuildPrompt는 기존 그대로 분리 유지). 창 AI 연동 영역에 "프로젝트 코드 탐색 허용" 토글 추가(기본 켬, EditorPrefs `MCPTools.AssetListup.AiAllowProjectExplore` 기억, 툴팁으로 정확도/속도·토큰 차이 안내) — 켜면 탐색 프롬프트+읽기 플래그+WorkingDirectory로 실행, 끄면 기존 일회성 방식. MCP `mcptools_asset_scan`의 instructions에도 `GetInstructions(true)`로 "MCP 클라이언트가 프로젝트 파일을 직접 읽을 수 있다면 스크립트·씬·프리팹을 참고해 역할을 추론하라" 단락 포함.
  - 검증 상태: 컴파일 검증 통과 (기본 / MCPTOOLS_HAS_MCPFORUNITY 2구성 빌드 오류 0). claude CLI 스모크 테스트 통과 — 프로젝트 루트에서 `claude -p "...클래스 이름만 출력" --allowedTools "Read Glob Grep"` 실행 시 파일을 실제로 읽어 AiCliTool/AiCliResult/AiCliRunner를 정확히 응답. 토글 온/오프 실제 동작은 에디터 테스트에서 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Common/AiCliRunner.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListPromptBuilder.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupWindow.cs`, `MCPToolTest/Assets/MCPTools/Editor/AssetListup/AssetListupTool.cs`

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

> 2026-07-21 전체 통과 — 창/표/AI 연동/저장 항목은 사용자가 에디터에서 직접 확인, MCP 항목 2건은 MCP for Unity HTTP 서버(127.0.0.1:8080) 경유 실호출로 검증(mcptools_asset_scan → designDocText·scanEntries·itemSchema·instructions 반환 확인, mcptools_asset_list_save → items 2건 저장·warnings 3건 반환 확인 후 검증 파일 삭제). 어댑터에 [ToolParameter] 파라미터 스키마 누락 문제를 발견·수정함(스키마 갱신에는 MCP 서버 재시작 필요).

**Task 1 완료.**

- [x] `Tools/MCP/Asset Listup` 창이 열리고, 항목 목록이 헤더 행 + 항목당 한 행의 표 형태(줄무늬 배경, 가로 스크롤 시 헤더·행 열 정렬 유지)로 표시됨
- [x] 표의 각 셀(이름/종류/UI 여부/대상 프리팹/대상 오브젝트/설명/상태)이 직접 편집되고, 항목 추가·삭제가 동작함
- [x] 샘플 기획서(md)를 넣고 [스캔 + 휴리스틱 추출(보조)] 실행 시 항목 목록이 생성됨
- [x] 테스트용 프리팹(Image 포함 Canvas 프리팹 1개)을 만들어 스캔하면 해당 슬롯이 목록에 잡힘
- [x] [AI용 프롬프트 복사] 클릭 시 기획서 원문·스캔 요약·itemSchema·출력 지침이 포함된 프롬프트가 클립보드에 복사됨
- [x] [AI 응답 JSON 불러오기] 보조 창에 AI가 출력한 JSON 배열을 붙여넣고 [목록 교체]/[목록에 병합]하면 표에 반영되고, 잘못된 JSON은 한국어 오류 안내가 뜸
- [x] 대상 프리팹 경로·UI 여부가 비어 있는 행은 경고색으로 표시되고, 저장 시 "대상 미기록 항목 N건" 확인 다이얼로그가 뜸 — [저장] 선택 시 해당 항목 상태가 "대상 미정"으로 기록되어 저장되고, [취소] 시 저장되지 않음
- [x] JSON이 `Assets/Docs/`에 저장되고 Project 뷰에서 즉시 보임 (AssetDatabase.Refresh 동작)
- [x] MCP로 `mcptools_asset_scan` 호출 시 designDocText·scanEntries·itemSchema·instructions가 반환됨
- [x] MCP로 `mcptools_asset_list_save`에 items 배열을 넘기면 JSON이 저장되고 outputPath·itemCount·warnings가 반환됨
- [x] AI 연동 영역의 AI 도구 드롭다운에 PC에 설치된 CLI(claude 등)가 표시되고 [다시 검색]으로 갱신됨
- [x] [선택한 AI로 목록 생성] 실행 시 "AI 실행 중..." 라벨과 [취소] 버튼이 표시되고, 에디터가 멈추지 않으며, 완료 시 교체/병합 다이얼로그 후 표에 항목이 반영됨
- [x] AI 실행 실패(미로그인/타임아웃 등) 시 한국어 오류 다이얼로그가 뜨고, 응답 원문이 [AI 응답 JSON 불러오기] 창에 자동으로 채워짐
- [x] [직접 입력...]으로 임의 커맨드를 지정해 실행 가능하고, 선택한 AI 도구·커맨드·타임아웃이 창을 다시 열어도 유지됨(EditorPrefs)
- [x] UI 재배치 레이아웃 확인 — 상단 "AI 연동" 박스(기획서/스캔 루트/AI 도구/타임아웃/탐색 토글/큰 생성 버튼) → "로컬 AI 미사용 시 (수동 방식)" Foldout(기본 접힘, AI CLI 미감지 시 자동 펼침, 상태가 창 재오픈 시 유지) → 표(남은 공간 채움) → 하단 고정 상태 메시지+[항목 추가]/[저장] 순서로 표시됨
- [x] "프로젝트 코드 탐색 허용" 토글 온/오프 동작 — 켜고 [선택한 AI로 목록 생성] 시 상태 라벨이 "AI 실행 중 (프로젝트 탐색 모드)"로 표시되고 프로젝트 파일을 참고한 더 구체적인 결과가 나오며, 끄면 기존 일회성 방식으로 실행됨. 토글 상태가 창을 다시 열어도 유지됨(EditorPrefs)

## 3. 보완 — 씬 직접 배치 오브젝트 스캔 지원 (2026-07-24)

- [x] `ScanEntry.scenePath` 필드 추가 (prefabPath와 상호 배타) + `ProjectScanner.ScanScenes(List<string>)` 신설
  - 구현 결과: 사용자가 지정한 씬만 스캔. 열려 있지 않은 씬은 `EditorSceneManager.OpenScene(path, OpenSceneMode.Additive)`로 열어 읽기 전용 스캔 후 저장 없이 닫아 현재 열린 씬 상태를 복원(Additive 열기는 비파괴적이므로 저장 확인 다이얼로그 없이 안전, MCP 경로에서 블로킹 다이얼로그도 방지). `PrefabUtility.IsPartOfPrefabInstance`가 true인 오브젝트(프리팹 인스턴스)는 원본 프리팹 스캔으로 커버되므로 제외. Image/RawImage/SpriteRenderer/AudioSource 슬롯을 씬 루트 기준 계층 경로로 수집.
  - 검증 상태: Unity MCP 브리지 미기동으로 컴파일/실호출 미검증 (아래 에디터 테스트 항목에서 확인 필요).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/ProjectScanner.cs`
- [x] `AssetListItem.targetScenePath` 스키마 추가 (targetPrefabPath와 상호 배타, 씬 항목은 targetObjectPath가 씬 루트 기준)
  - 구현 결과: ToDictionary/FromDictionary·ItemsFromObjects 직렬화 반영, itemSchema/instructions에 씬 항목 구분 기록 지침 추가, AssetListBuilder 매칭·Validate(양쪽 다 빈 경우·동시 지정 경고) 반영.
  - 관련 파일: `AssetListDocument.cs`, `AssetListPromptBuilder.cs`, `AssetListBuilder.cs`
- [x] AssetListupWindow — "스캔 대상 씬" 목록 UI(SceneAsset 오브젝트 필드 + [+ 추가]/[제거]) + 스캔 실행 시 프리팹/씬 스캔 병합, 표에 "대상 씬" 열 추가
  - 관련 파일: `AssetListupWindow.cs`
- [x] MCP `mcptools_asset_scan`에 `scenePaths`(문자열 배열, 선택) 파라미터 추가 — 지정 시 씬 스캔 결과를 scanEntries에 병합. 어댑터 Parameters에도 반영 (스키마 갱신에는 MCP 서버 재시작 필요).
  - 관련 파일: `AssetListupTool.cs`, `Common/McpForUnityAdapter.cs`, `README.md`

### 에디터 테스트 (씬 스캔 보완)

- [ ] "스캔 대상 씬"에 씬을 추가하고 스캔하면, 씬에 직접 배치된 Image/SpriteRenderer 등의 슬롯이 목록에 잡힘 (scenePath 채워짐)
- [ ] 씬 안의 프리팹 인스턴스 슬롯은 씬 스캔 결과에서 제외됨
- [ ] 닫혀 있던 씬을 스캔해도 스캔 후 씬 열림 상태가 원래대로 복원됨
- [ ] MCP `mcptools_asset_scan`에 scenePaths를 넘기면 scanEntries에 scenePath가 채워진 슬롯이 포함됨

## 4. 보완 — 항목 소스 토글 "기획서에서 항목 추측해 추가" (2026-07-24, 반전 확정본)

> 최종 확정 의미: 창 상단 토글 **[기획서에서 항목 추측해 추가 (끄면 스캔 항목만 · 기획서는 설명 참고용)]**
> (필드 `_extractFromDoc`, EditorPrefs `MCPTools.AssetListup.ExtractFromDoc`, 기본 **false=OFF**).
> - **OFF(기본)**: 항목은 열린 씬 + 포함 프리팹 스캔 결과에서만 생성. [선택한 AI로 목록 생성]은
>   `AssetListPromptBuilder.BuildDescribeScannedPrompt`로 스캔 항목의 description만 기획서로 채우고(응답을 id 매칭해
>   반영, `ApplyScanDescriptions`), 항목 자체는 스캔 결과 유지. 기획서 미선택/`클립보드 복사만`/수동 [스캔+휴리스틱]은
>   `RunScanOnlyBuild` 폴백(스캔 항목만 + designDocPath 컨텍스트 기록).
> - **ON**: 기존 doc 추출 방식 그대로(RunSelectedAi/RunBuild) — 스캔에 없는 항목까지 추측해 추가.
> - 레이아웃은 토글로 바뀌지 않으며 모든 섹션 항상 표시. MCP `mcptools_asset_scan`의 `scanOnly` 파라미터/의미는
>   변경하지 않음(프로그램 API 유지).
> - 관련 파일: `AssetListupWindow.cs`, `AssetListPromptBuilder.cs`(BuildDescribeScannedPrompt 신설), `README.md`

### (이전 반복 기록) 씬/프리팹 스캔으로만 항목 생성 (scanOnly, 2026-07-24)

- [x] `ProjectScanner.ScanOpenScenesWithPrefabs()` 신설 — 현재 열려 있는(로드된) 모든 씬의 직접 배치 슬롯 + 씬에 포함된 프리팹 인스턴스의 원본 프리팹(`PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot`, 중복 제거) 슬롯 수집
  - 구현 결과: 저장되지 않은 임시 씬(경로 없음)은 제외. 씬 슬롯은 기존 `CollectSceneSlots`(프리팹 인스턴스 소속 제외), 프리팹 슬롯은 기존 `CollectSlots` 재사용.
  - 검증 상태: 에디터 컴파일/실행 확인 필요 (아래 테스트 항목).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/ProjectScanner.cs`
- [x] AssetListupWindow — 상단 "씬/프리팹 스캔으로만 항목 생성 (기획서 항목 추출 안 함)" 토글(EditorPrefs 기억). 켜면 **기획서에서 항목을 추출하는 컨트롤(AI 연동 생성/수동 방식)만 숨기고**, 기획서 파일 선택(컨텍스트 기록용, `DrawDesignDocPicker` 공용화)과 [열린 씬 스캔으로 목록 생성] 버튼을 표시. 실행 시 `AssetListBuilder.BuildFromScan(entries, scanRoot, designDocPath)`으로 대상 경로·UI 여부가 채워진 목록 생성(기존 목록 있으면 교체 확인) — 항목은 스캔 결과로만 만들고, 선택한 기획서 경로는 문서 `designDocPath`에 컨텍스트로 기록되어 2단계(Prompt Builder)에서 참고. 표/저장 흐름은 기존과 동일.
  - 관련 파일: `AssetListupWindow.cs`, `AssetListBuilder.cs`(`BuildFromScan`에 designDocPath 컨텍스트 파라미터 추가)
- [x] MCP `mcptools_asset_scan`에 `scanOnly`(bool, 기본 false, 하위 호환) 파라미터 추가 — true면 기획서 항목 추출 없이 열린 씬+포함 프리팹 스캔 후 scanEntries와 함께 완성 `items` 배열 반환 (`mcptools_asset_list_save`에 그대로 전달 가능). `designDocPath`는 이때도 읽혀 `designDocPath`/`designDocText`로 반환되고 items 문서 컨텍스트로 기록됨 (`scenePaths`만 무시). 어댑터 Parameters/설명, README 갱신.
  - 관련 파일: `AssetListupTool.cs`, `Common/McpForUnityAdapter.cs`, `README.md`

### 에디터 테스트 (항목 소스 토글 — 반전 확정본)

- [ ] 토글 기본값은 OFF(체크 해제)이며, 창을 다시 열어도 상태가 유지됨. 토글로 어떤 섹션도 숨겨지지 않고 모든 컨트롤이 항상 보임
- [ ] **OFF + AI**: 기획서를 선택하고 [선택한 AI로 목록 생성] 실행 → 항목은 씬/프리팹 스캔 결과에서만 생성되고(새 항목 추가 없음), 각 항목의 설명이 기획서 내용으로 채워짐. 저장 시 `designDocPath`가 기록됨
- [ ] **OFF + AI 응답 파싱 실패/실행 실패**: 스캔 항목은 유지되고(설명만 비어 있음) 한국어 안내가 뜸
- [ ] **OFF + 기획서 미선택 또는 `클립보드 복사만` 또는 수동 [스캔 + 휴리스틱 추출]**: 스캔 항목만 생성되고 설명은 수동 보완(RunScanOnlyBuild 폴백)
- [ ] **ON**: [선택한 AI로 목록 생성]/[스캔 + 휴리스틱 추출]이 기존처럼 기획서에서 항목을 추측해 추가함(스캔에 없는 항목 포함 가능)
- [ ] 슬롯이 없는 씬에서 OFF 모드로 실행하면 한국어 안내 다이얼로그가 뜸
- [ ] MCP `mcptools_asset_scan`의 `scanOnly` 동작은 이전과 동일(창 토글과 독립) — scanOnly:true + designDocPath로 호출 시 items + designDocText 반환, `mcptools_asset_list_save`에 전달 가능
