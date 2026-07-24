# Task 5 체크리스트 — 통합 및 배포

> Task 문서: [Task5_통합및배포.md](../tasks/Task5_통합및배포.md) · 원본 계획: ../PLAN.md §4 Phase 5

## 1. 구현 체크리스트

- [x] `PipelineWindow : EditorWindow` — 메뉴 `Tools/MCP/Pipeline (All-in-One)` (4단계 탭/스텝퍼 통합, 산출물 자동 연결, 단계별 상태 표시, 중간 단계 재시작)
  - 구현 결과: 개별 창 UI를 재구현하지 않고 파이프라인 진행 상태를 한눈에 보여주는 **스텝퍼 창**으로 구현. 각 단계에 상태 배지(미실행/준비/완료 — 산출물 존재로 판정: 1=최신 AssetList_*.json, 2=최신 PromptSet_*.json, 3=GenerationResults.json 확정 항목 수, 4=확정본 유무)와 [N단계 창 열기] 버튼(기존 `*Window.Open()` 재사용), 다음 단계 입력 자동 표시(최신 AssetList→2단계, 최신 PromptSet→3단계, AssetList→4단계). OnFocus마다 디스크 재스캔해 개별 창에서 산출물 만든 뒤 돌아오면 상태 갱신. 버튼은 항상 활성이라 중간 단계부터 재시작 가능. 메뉴 priority 20(4단계 뒤·Ping 앞).
  - 검증 상태: **컴파일 오류 0건 확인**(read_console error 0). 의존 진입점 `AssetListupWindow/PromptBuilderWindow/ComfyUIGeneratorWindow/AssetApplierWindow/MCPSettingsWindow.Open()` 존재 확인. 창 UI 실동작은 에디터 테스트 항목으로 확인 필요.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Pipeline/PipelineWindow.cs`
- [x] 통합 MCP 도구 `mcptools_run_pipeline` — autoSelect 정책("first"/"none"), "none" 시 3단계에서 멈추고 `pendingSelections` 반환
  - 구현 결과: **AI 중립 설계 충돌로 사용자와 범위 확정 — 입력을 designDocPath가 아니라 `promptSetPath`(2단계 산출물)로 변경**해 후반부(3단계 생성 → autoSelect 확정 → 4단계 적용)만 자동화. 1·2단계 목록/프롬프트 작성은 AI가 사전 수행(scan/save 분리 유지). `"first"`=각 항목 최저 시드 후보 확정 후 `AssetApplier.ApplyBatch`로 일괄 적용, `"none"`=후보만 생성하고 `pendingSelections` 반환. 항목 단위 try/catch로 `failed:[{id,reason}]` 부분 성공 지원. 적용 대상은 PromptSet의 `assetListPath` 메타로 AssetList 로드해 획득. **핵심 리스크 해결(비동기)**: `CandidateGenerator.GenerateAsync`는 ConfigureAwait 없는 await를 포함해 메인 스레드 `.GetResult()` 시 데드락 → `Task.Run(() => GenerateAsync(...)).GetAwaiter().GetResult()`로 스레드풀에서 실행(BridgeClient가 `System.Net.Http.HttpClient` 사용이라 네트워크는 스레드 무관). GenerateAsync 말미의 `AssetDatabase.Refresh()`가 백그라운드 스레드에서 던질 수 있으나 후보 파일은 그 직전 디스크 기록되므로, 대기 후 **메인 스레드에서 Refresh + `ListCandidates`로 디스크 사실 재확인**해 복구(주 에이전트 보강). 반환 data: `{ promptSetPath, assetListPath, pendingSelections, applied, failed }`.
  - 검증 상태: **컴파일 오류 0건 + MCP 도구 등록·노출 확인**(mcptools_run_pipeline 도구 목록 노출). 실제 생성 실행(ComfyUI 필요)은 미수행 — Task.Run 경로의 백그라운드 Refresh 동작과 디스크 복구 로직은 후보 생성 시나리오 에디터 테스트로 확인 권장. 생성 완료까지 에디터 메인 스레드 블로킹(설계상 감수).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Pipeline/PipelineTool.cs`, `Editor/Common/McpForUnityAdapter.cs`(어댑터 클래스 `McpToolsRunPipelineTool`)
- [x] `mcptools_status` — 설정·서버 상태·산출물 현황 조회 (진단용)
  - 구현 결과: 파라미터 없음. 반환 data: `version`(MCPToolsInfo.Version)·`unityVersion`, `config`(comfyUIServerUrl/bridgeServerUrl/generatedRootPath/docsRootPath/defaultImageWorkflow/candidateCount), `outputs`(AssetList/PromptSet 개수·최신 파일, Generated/Images·Audio 파일 수, Candidates 하위 폴더 수, GenerationResults.json 확정 항목 수), `serverHealthNote`. **서버 실시간 연결 확인은 하지 않음**(동기 핸들러 블로킹 방지 — 3단계 창/브리지 `/health` 안내).
  - 검증 상태: **컴파일 0건 + 실호출 검증 완료** — `mcptools_status` 호출 시 version 0.1.0, unityVersion 6000.5.2f1, config·outputs(assetListCount 1, promptSetCount 1, imageCount 10, audioCount 2, confirmedCount 9 등) 정상 반환 확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Pipeline/PipelineTool.cs`, `Editor/Common/McpForUnityAdapter.cs`(어댑터 클래스 `McpToolsStatusTool`)
- [x] `Assets/MCPTools/README.md` — 설치 절차, ComfyUI 준비, 4단계 사용법, MCP 도구 레퍼런스, 트러블슈팅
  - 구현 결과: 상단 단계 표에서 4단계(AssetApplier) 상태 "예정"→"**구현됨**"으로 수정. **파이프라인 통합 창(All-in-One)** 사용법 섹션 추가(스텝퍼 동작·중간 재시작). MCP 도구 레퍼런스에 `mcptools_run_pipeline`(promptSetPath/autoSelect/workflowName, AI 중립 설계상 1·2단계 사전 작성 필요·동기 블로킹 주의 명시)과 `mcptools_status` 문서 추가. (설치/ComfyUI 준비/트러블슈팅은 Task 0~4에서 이미 작성됨.)
  - 검증 상태: 내용 일관성 확인 완료(입력이 designDocPath 아닌 promptSetPath임 명시).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/README.md`
- [x] 배포 패키지 검증 — `Assets/MCPTools/`만 export, 신규 Unity 6 프로젝트 import 검증(컴파일 오류 0, 외부 폴더 의존 없음, 폴더·설정 자동 생성), 절대 경로 잔재 검색
  - 구현 결과: **코드 레벨 자기완결성 점검**으로 대체(.unitypackage export/신규 프로젝트 import는 이 환경에서 실행 불가). 절대경로/드라이브 문자/사용자명 하드코딩 검색(.cs/.json/.md/.asmdef/.py) → 실제 잔재 **없음**(매칭은 전부 `http://127.0.0.1` 로컬 기본 주소·URL·`where.exe`/`status:"` 오탐). 외부 폴더(Assets/Generated·Docs) 경로는 코드 하드코딩 없이 전부 `settings.generatedRootPath`/`docsRootPath` 경유(신규 2파일 포함). 외부 asmdef/DLL 신규 의존 없음, 패키지 의존은 `McpForUnityAdapter.cs` 한 파일 + `MCPTOOLS_HAS_MCPFORUNITY` 조건 컴파일에만 격리(신규 어댑터 2개도 이 블록 내부).
  - 검증 상태: 코드 레벨 점검 완료. **실제 .unitypackage export → 신규 Unity 6 프로젝트 import → 컴파일 0 확인은 사용자 최종 인수 테스트로 남음**(에디터 테스트 항목).
  - ⚠️ **후속 검증에서 이 항목의 결론 2건이 틀린 것으로 확인됨** — 아래 "배포 형식 확정" / "선택적 패키지 asmdef 분리" 항목 참조. (1) `.unitypackage` 방식은 브리지 서버 누락으로 애초에 성립하지 않았고, (2) "패키지 의존이 `McpForUnityAdapter.cs` 한 파일에 격리됨"은 **C# 레벨에서만 참**이었다. asmdef의 `references` 배열이 선택적 어셈블리를 무조건 참조하고 있어 패키지 미설치 환경에서는 어셈블리 전체가 컴파일되지 않았다.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/` 전체
- [x] 배포 형식 확정 — zip 폴더 전달 방식 + 패키징 스크립트
  - 구현 결과: **`.unitypackage` 방식 폐기.** 브리지 서버가 든 `Editor/ComfyUIGenerator/Server~/`는 폴더명 끝의 `~` 때문에 Unity가 에셋으로 임포트하지 않고, Export Package는 AssetDatabase에 등록된 에셋만 내보내므로 이 폴더가 **경고 없이 누락**된다(받는 쪽 3단계 전체 불능). 폴더를 파일시스템 그대로 복사하는 **zip 방식**으로 확정. 패키징 스크립트 `tools/pack-mcptools.ps1` 신설 — `__pycache__`/`.pyc`/`.DS_Store`/`Thumbs.db` 제외, **압축 직전 `Server~/bridge_server.py` 존재를 검증해 누락 시 실패**, 기존 zip이 잠겨 있으면 `MCPTools.new.zip`으로 저장 후 안내. 산출물 `dist/MCPTools.zip`.
  - 검증 상태: **실행 검증 완료** — 104개 파일 / 206.9 KB 생성, zip 내부에 `Server~/bridge_server.py`·`variables.json`·`workflows/*.json` 4종 포함 확인, `__pycache__` 제외 확인. 부수 수정: 배포되는 `MCPToolSettings.asset`의 `defaultImageWorkflow`가 존재하지 않는 `txt2img_basic.json`이었던 것을 `GenerateImage`로 교정(그대로 배포 시 첫 생성에서 즉시 실패했을 값).
  - 관련 파일: `tools/pack-mcptools.ps1`, `MCPToolTest/Assets/MCPTools/README.md`(설치 절차 + .unitypackage 금지 경고), `MCPToolTest/Assets/MCPTools/Editor/Common/MCPToolSettings.asset`
  - 참고: 스크립트는 **UTF-8 BOM**으로 저장해야 한다(Windows PowerShell 5.1이 BOM 없는 UTF-8의 한글을 깨뜨려 파싱 실패).
- [x] 선택적 패키지 asmdef 분리 — 패키지 미설치 환경에서 어셈블리 전체가 죽는 문제 해결
  - 구현 결과: `MCPTools.Editor.asmdef`의 `references`가 `MCPForUnity.Editor`(선택)와 `Unity.2D.Sprite.Editor`를 **무조건 참조**하고 있었다. `versionDefines`는 심볼만 제어할 뿐 `references` 배열은 제어하지 못하므로, 해당 패키지가 없는 프로젝트에서 Unity가 `will not be compiled, because it has references to non-existent assemblies`를 내고 **MCPTools.Editor 전체(에디터 창 4개 포함)가 컴파일되지 않았다.** 선택적 패키지 사용 코드를 각각 별도 어셈블리로 분리하고 `defineConstraints`를 걸어 해결(불충족 asmdef는 참조 해석 **전에** 제외됨). 의존 방향은 **선택적 어셈블리 → 본체 단방향**이며 본체는 두 신규 asmdef를 참조하지 않는다(참조 시 동일 문제 재발).
    - `Editor/McpForUnityBridge/` (`MCPTools.Editor.McpForUnity.asmdef`, defineConstraints `MCPTOOLS_HAS_MCPFORUNITY`) — `McpForUnityAdapter.cs` 이동(.meta 동반, GUID 유지). 어댑터는 `McpToolRegistry.Execute`만 호출하는 단방향 구조라 분리 가능.
    - `Editor/SpriteSlicing/` (`MCPTools.Editor.SpriteSlicing.asmdef`, defineConstraints `MCPTOOLS_HAS_2D_SPRITE`) — `SpriteSliceWriter.cs` 신설. `SpriteDataProviderFactories`/`ISpriteEditorDataProvider`/`ISpriteNameFileIdDataProvider`/`SpriteRect`/`SpriteNameFileIdPair` 코드를 본체에서 이관하고, `[InitializeOnLoad]`로 `SpriteSheetImporter.SpriteRectWriter` 훅에 주입.
    - 훅 시그니처는 코어 타입만 사용: `public static Action<TextureImporter, List<SpriteMetaData>> SpriteRectWriter`. **별도 어셈블리에서 접근해야 하므로 `internal`이 아닌 `public`**이어야 한다. 훅이 null이면(패키지 미설치) `SaveAndReimport()` 후 `InvalidOperationException`을 던져 기존 `EditorUtility.DisplayDialog("스프라이트 시트 임포트 실패", ...)` 경로로 설치 안내가 표시된다(무성 실패 아님).
  - 검증 상태: **컴파일 오류 0건 확인**(read_console error 0, 주 에이전트 교차 확인). 런타임 확인으로 `MCPTools.Editor`/`MCPTools.Editor.McpForUnity`/`MCPTools.Editor.SpriteSlicing`/`MCPTools.Runtime` 4개 어셈블리 전부 로드 + `SpriteSheetImporter.SpriteRectWriter != null` 확인(defineConstraints 충족 시 제외되지 않음). `mcptools_ping`이 이전된 어댑터를 경유해 정상 왕복. **패키지를 실제로 제거한 환경에서의 확인은 미수행**(에디터 테스트 항목).
  - 관련 파일: `Editor/MCPTools.Editor.asmdef`, `Editor/McpForUnityBridge/*`, `Editor/SpriteSlicing/*`, `Editor/SpriteSheet/SpriteSheetImporter.cs`, `Assets/MCPTools/README.md`
  - 남은 정리(선택): `MCPTools.Editor.asmdef`에 `MCPTOOLS_HAS_MCPFORUNITY` versionDefines 항목이 남아 있으나 본체에서 더는 쓰이지 않음(무해).

## 2. 에디터 테스트 체크리스트 (최종 인수 테스트 — 사용자가 Unity 에디터에서 직접 확인)

> 아래 3개 항목은 **설계 변경으로 대체됨**(사용자 확인 완료). `PipelineWindow`는 계획의 탭 통합 창이 아니라 **상태 배지 + [N단계 창 열기] 버튼만 있는 스텝퍼**로 구현되어, 원래 문구가 검증할 동작 자체가 존재하지 않는다.
> - ~~`Tools/MCP/Pipeline` 창에서 기획서 하나로 1→4단계가 끊김 없이 진행됨~~ → 통합 창에 해당 동작 없음. 아래 스텝퍼 항목으로 대체.
> - ~~3단계에서 후보 선택 UI가 정상 동작하고, 재생성도 통합 창에서 가능함~~ → 통합 창에 후보 UI 없음. 후보 선택·재생성 검증은 **Task 3 체크리스트 테스트 11~14번**이 담당(미검증 상태).
> - ~~중간 단계부터 재시작(예: PromptSet 수정 후 3단계부터) 가능~~ → 버튼이 항상 활성이고 단계 게이팅이 없어 설계상 자명. 검증 불필요.

- [ ] `Tools/MCP/Pipeline (All-in-One)` 창의 단계별 상태 배지가 실제 산출물 유무와 일치하고, [N단계 창 열기] 버튼이 해당 창을 열며, 개별 창에서 산출물을 만든 뒤 돌아오면(OnFocus) 배지가 갱신됨
- [ ] MCP로 `mcptools_run_pipeline`(autoSelect:"none") → 선택 → 적용의 전체 시나리오가 동작함
  - **미검증 코드 중 위험도 최상.** 데드락 회피용 `Task.Run` 경로가 한 번도 실행된 적 없음. ComfyUI + 브리지 서버 기동 필요(Task 3 테스트와 함께 수행 권장). 실행 중 에디터 메인 스레드가 블로킹되는 것은 설계상 정상.
- [ ] `dist/MCPTools.zip`을 **새 Unity 6 프로젝트**에 풀어 넣었을 때 컴파일 오류 0건이고, 설정 기본값(127.0.0.1:8188)으로 즉시 사용 가능
- [ ] **unity-mcp 패키지가 없는** 프로젝트에서 컴파일 오류 0건 + `Tools/MCP/*` 에디터 창 4개가 전부 정상 동작 (asmdef 분리 실효성 확인 — 분리 전에는 여기서 전체 컴파일이 깨졌음)
- [ ] **`com.unity.2d.sprite` 패키지가 없는** 프로젝트에서 컴파일 오류 0건이고, 스프라이트 시트 슬라이싱 시도 시 설치 안내 다이얼로그가 뜨며 나머지 1~4단계는 정상 동작
- [ ] README만 보고 제3자가 설치·실행 가능한 수준인지 리뷰
