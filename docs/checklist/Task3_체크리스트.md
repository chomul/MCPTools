# Task 3 체크리스트 — ComfyUIGenerator 도구 (재작업: 브리지 서버 구조)

> Task 문서: [Task3_ComfyUIGenerator.md](../tasks/Task3_ComfyUIGenerator.md) · 원본 계획: ../PLAN.md §4 Phase 3
> 2026-07-23 재작업: 토큰 템플릿 방식 폐기 → 원본 워크플로 JSON + 브리지 서버(변수 덮어쓰기) 구조로 전환.

## 1. 구현 체크리스트

- [x] 브리지 서버 (`Server~/bridge_server.py`, Python 3 표준 라이브러리만)
  - 구현 결과: `http.server.ThreadingHTTPServer` 기반. 엔드포인트: `GET /health`(ok/comfyUrl/comfyAlive), `GET /workflows`(워크플로 목록+변수 매니페스트), `POST /generate`(원본 JSON 로드 → 변수 `{"nodeId.field": 값}` 타입 변환 후 inputs 덮어쓰기 → 모든 노드 inputs의 seed/noise_seed 자동 탐지 → baseSeed..+count-1로 count회 `/prompt` 큐잉 → jobId 즉시 반환, 백그라운드 스레드 `/history` 폴링), `GET /job/{jobId}`(status/progress/message/results), `POST /upload`(multipart를 ComfyUI `/upload/image`로 그대로 전달, name 반환), `GET /view`(ComfyUI `/view` 프록시). 인자: `--port`(기본 8189), `--comfy-url`(기본 http://127.0.0.1:8188). ComfyUI 미기동 시 /generate가 502+원인 메시지, 실행 중 오류(모델 누락 등)는 job status=failed+history 오류 메시지 전달. Job 타임아웃 600초.
  - 검증 상태: 로컬 스모크 테스트 완료 — /health·/workflows·/job(404) 응답 확인 + **실제 ComfyUI 연동으로 GenerateImage 4장 생성(연속 시드) → completed → /view 다운로드(200) → /upload 전달(ok) 전 과정 확인.**
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/Server~/bridge_server.py`
- [x] 원본 워크플로 4종 + 변수 매니페스트 (`Server~/workflows/`, `Server~/variables.json`)
  - 구현 결과: `GenerateImage.json`/`UI.json`/`StyleChange.json`/`Audio.json`을 `C:\Project\CreateMCP\ComfyUI\`에서 **원본 구조 그대로 복사** (노드 제거/재배선/토큰화 없음). `variables.json`은 ComfyUI.md 명세 기반으로 각 변수의 nodeId/field/label/type(string|int|float|bool|image)/default(원본 JSON 값)/min·max(숫자)/role(positive|negative) 정의. ComfyUI.md와의 차이: `value` 필드(#21/#27/#29, UI #22, Audio #11)는 md에 타입 표기가 없으나 실제 JSON이 boolean이라 bool 타입으로 정의. GenerateImage #9 `type`·#5 `sampler_name`은 문자열. 그 외 명세와 실제 JSON 일치. `Server~` 폴더는 Unity 임포트 제외(.meta 불필요)이며 배포 폴더(Assets/MCPTools) 안에 있어 자기완결.
  - 관련 파일: `.../ComfyUIGenerator/Server~/workflows/*.json`, `.../ComfyUIGenerator/Server~/variables.json`
- [x] `ComfyUIServerLauncher` 재작업 — [서버 시작]/[서버 종료]가 브리지 서버 제어
  - 구현 결과: `pythonExecutable`(기본 "python")로 `Server~/bridge_server.py` 실행. 스크립트 경로는 `Application.dataPath` 기준 상대 계산(하드코딩 없음). 인자 `--port`(bridgeServerUrl에서 추출)·`--comfy-url`(comfyUIServerUrl) 전달. PID SessionState 저장/재연결, taskkill /T /F 트리 종료 유지. Python 미설치/스크립트 누락 시 원인·조치 예외 → 창에서 다이얼로그 안내.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIServerLauncher.cs`
  - 2026-07-23 보완: 브리지 서버가 기본으로 콘솔 창 없이(hidden) 실행되도록 변경 — 로그는 시스템 임시 폴더 `mcptools_bridge_server.log`에 기록(`--log-file` 인자 신설). 설정의 "브리지 콘솔 창 표시" 토글로 기존 콘솔 표시 방식 선택 가능.
- [x] `BridgeClient` — 브리지 REST async 래퍼 (신규)
  - 구현 결과: `GetHealthAsync`/`GetWorkflowsAsync`/`GenerateAsync`/`GetJobAsync`/`DownloadAsync`(/view 프록시)/`UploadImageAsync`(multipart). HttpClient + async/await, 동기 블로킹 없음. 오류 응답의 error 필드를 한국어 예외 메시지로 전달.
  - 관련 파일: `.../ComfyUIGenerator/BridgeClient.cs`
- [x] `ComfyUIGeneratorWindow` 재작업 — 브리지 상태 + 워크플로 변수 편집 UI
  - 구현 결과: 상단에 브리지 서버 상태(●)+[서버 시작]/[서버 종료]+ComfyUI 연결 상태(health.comfyAlive) 표시, 5초 주기 자동 확인. 워크플로 드롭다운(GET /workflows, 브리지 기동 시 자동 로드) → 선택 시 매니페스트 기반 변수 편집 UI 동적 생성: string=TextField(프롬프트 role은 TextArea), int=IntField(min/max 클램프), float=FloatField, bool=Toggle, image=[파일 선택]/[해제](생성 시 자동 업로드 → 파일명 치환, 미지정 시 원본 파일명 유지). [기본값 복원] 버튼. PromptSet 항목 선택 시 role(positive/negative) 변수 자동 채움(수정 가능). 생성→폴링→썸네일→확정/재생성 흐름 유지.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`
  - 2026-07-23 보완: 변수 라벨에서 "(#노드번호)" 제거, `variables.json`에 `description` 추가(마우스 오버 툴팁), bool(토글) 변수 항상 최상단 배치. 생성 버튼이 PromptSet 미로드 시 잠기던 문제 수정 — 워크플로 목록만 있으면 활성화(항목 미선택 시 `manual_{워크플로}` 수동 항목으로 생성), 비활성 사유 HelpBox 안내.
  - 2026-07-23 보완: 후보 미리 보기/미리 듣기 추가 — 이미지 후보 [크게 보기] 버튼으로 `CandidatePreviewWindow`(유틸리티 창, 720x720, 파일 바이트 원본 해상도 로드) 표시, 오디오 후보(.flac/.wav 등)는 셀에 파일명/시드 + [▶ 재생]/[■ 정지] 토글(`EditorAudioPreview` — UnityEditor.AudioUtil 리플렉션, 창 닫힘/재생성 시작 시 정지, API 미탐지 시 상태 메시지 안내). 관련 파일: `.../ComfyUIGenerator/EditorAudioPreview.cs`, `.../ComfyUIGenerator/CandidatePreviewWindow.cs`
  - 2026-07-23 보완: 변수 UI 개선 3종 — ① `variables.json`에 범용 `visibleWhen`(AND 조건 배열) 신설, GenerateImage의 ckpt_name(#27=true)/unet_name(#27=false)/lora_name(#27=false·#29=true)에 적용해 bool 토글 값 기준 조건부 표시(값 상태 유지, CLIP/VAE는 양쪽 경로 공용이라 미적용). ② 라벨 "UNET 파일명"→"디퓨전 모델명", "CLIP 파일명"→"텍스트 인코더" 및 설명 갱신. ③ 브리지 `/workflows`가 ComfyUI `/object_info`(1회 조회)에서 각 string 변수의 노드 class_type+field 선택지가 리스트면 `options` 첨부(미기동 시 키 생략) → 창에서 options 있는 string 변수는 Popup(현재 값이 목록에 없으면 "(현재: 값)" 항목), ComfyUI 미연결→연결 전환 시 워크플로 목록 1회 자동 재로드(편집 값 보존). 관련 파일: `Server~/variables.json`, `Server~/bridge_server.py`, `BridgeClient.cs`, `ComfyUIGeneratorWindow.cs`
- [x] `CandidateGenerator` 재작업 — 브리지 기반 생성
  - 구현 결과: `GenerateAsync(settings, item, workflowName, variables, baseSeed?, ...)` — 브리지 /health 확인(브리지·ComfyUI 각각 원인 안내) → /workflows 매니페스트에서 role 필드에 항목 positive/negative 자동 주입(사용자 variables 우선) → /generate → /job 1초 폴링(진행률 0~0.9) → 결과를 /view 프록시로 다운로드해 `Candidates/{id}/{seed}.png|flac` + 메타 JSON 저장. 기본 워크플로: audio→Audio, UI→UI, 그 외 defaultImageWorkflow(기본 GenerateImage). ConfirmCandidate/ListCandidates(Sprite 임포트·RawImage 판정·GenerationResults.json 기록)는 기존 로직 유지.
  - 관련 파일: `.../ComfyUIGenerator/CandidateGenerator.cs`
- [x] MCP 도구 3종 유지 + variables 파라미터 추가
  - 구현 결과: `mcptools_generate_candidates`/`mcptools_list_candidates`/`mcptools_select_candidate` 이름·반환 스키마 유지, 내부만 브리지 경유. generate에 `workflowName`(GenerateImage|UI|StyleChange|Audio)·`variables`(객체, 선택) 파라미터 추가. 어댑터(`McpForUnityAdapter.cs`)의 Parameters에 `JObject variables` 필드 추가 + 설명 갱신.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorTool.cs`, `.../Common/McpForUnityAdapter.cs`
- [x] 설정/문서 갱신 및 구파일 폐기
  - 구현 결과: `MCPToolSettings`에 `bridgeServerUrl`(기본 http://127.0.0.1:8189)·`pythonExecutable`(기본 "python") 추가, `comfyUIInstallPath`/`comfyUILaunchArguments` 제거(설정 창도 정리), `defaultImageWorkflow` 기본값 `GenerateImage`로 변경. `WorkflowTemplateLoader.cs`와 토큰화 `Workflows/*.json` 3종 삭제(.meta 포함). `Assets/MCPTools/README.md`에 브리지 구조/엔드포인트 표/Python 3 요구사항/워크플로별 조정 변수 표/필요 모델(Flux.2 Klein 4B, ComfyUI-Inspyrenet-Rembg, Stable Audio 3)/문제 해결 갱신.
  - 관련 파일: `.../Common/MCPToolSettings.cs`, `.../Common/MCPSettingsWindow.cs`, `MCPToolTest/Assets/MCPTools/README.md`

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

- [ ] `Tools/MCP/ComfyUI Generator` 창 상단 [서버 시작]으로 브리지 서버 콘솔이 뜨고 상태가 "브리지 서버 실행 중"으로 바뀜 (Python 3 필요)
- [ ] ComfyUI를 별도 실행하면 "ComfyUI 연결됨"으로 바뀜 (미실행 시 경고 HelpBox 표시)
- [ ] [서버 종료]로 브리지 프로세스 트리가 종료됨 (도메인 리로드 후에도 종료 버튼 동작)
- [ ] Python 미설치(또는 잘못된 pythonExecutable) 상태에서 [서버 시작] 시 안내 다이얼로그가 뜸
- [ ] 워크플로 드롭다운에 GenerateImage/UI/StyleChange/Audio 4종이 표시되고, 선택 시 변수 UI가 원본 JSON 기본값으로 생성됨
- [ ] [기본값 복원] 버튼으로 수정한 변수가 원본 값으로 되돌아감
- [ ] PromptSet 항목 선택 시 긍정/부정 프롬프트 변수가 자동으로 채워지고 수정 가능함
- [ ] StyleChange/UI 워크플로에서 이미지 변수 [파일 선택] 후 생성하면 업로드가 수행되고 해당 이미지 기반으로 생성됨
- [ ] "후보 4개 생성" 실행 중 에디터가 멈추지 않고 진행률이 표시됨, 취소 동작
- [ ] `Assets/Generated/Candidates/{id}/`에 서로 다른 시드의 PNG 4장 + 메타 `{seed}.json` 저장됨
- [ ] 썸네일 선택·확정 시 `Assets/Generated/Images/`(오디오는 `Audio/`) 복사 + `GenerationResults.json` 기록, Sprite 임포트 자동 설정(RawImage 대상은 Texture 유지)
- [ ] "재생성" 시 기존 후보 삭제 후 새 시드 4장 생성
- [ ] ComfyUI 미기동 상태에서 생성 시 원인 안내 다이얼로그가 뜨고 창이 멈추지 않음
- [ ] MCP로 generate(variables 포함) → list(running → completed) → select 3단계 호출이 순서대로 동작함
- [ ] Audio 워크플로로 오디오 항목 생성 시 `Audio/`로 확정 복사됨

## 3. 2026-07-23 보완 — 항목 파이프라인 중심 창 개선 (PromptSet 본격 연동)

- [x] 항목 목록 패널 (드롭다운 → 행 목록 + 상태 배지)
  - 구현 결과: PromptSet 로드 시 항목을 행 목록으로 표시. 각 행에 id/이름/종류(image|ui|audio, /UI)와 상태 배지 표시 — "프롬프트 없음"(positive 비어 있음, 적색) / "미생성"(회색) / "후보 N개"(청색, Candidates 폴더 조회) / "확정됨"(녹색, GenerationResults.json 기록). 목록 하단에 "확정 n/N" 요약. 행 클릭 시 항목 선택(선택 행 하이라이트 + ▶ 표시): role 변수에 프롬프트 자동 채움, assetType별 기본 워크플로 자동 선택(audio→Audio, ui→UI, image→defaultImageWorkflow — 워크플로 전환 시 `RebuildVariableStates(preserveValues:true)`로 role 외 변수 값 유지), 기존 후보가 있으면 `ListCandidates`로 썸네일 자동 로드.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs` (DrawItemListPanel/GetItemBadge/SelectItem/DefaultWorkflowNameFor/RefreshItemStatuses)
- [x] `CandidateGenerator.GetConfirmedItemIds` 조회 헬퍼 추가
  - 구현 결과: GenerationResults.json의 results 배열에서 assetItemId 집합을 반환 (배지 "확정됨" 판정용, 파일 없으면 빈 집합).
  - 관련 파일: `.../ComfyUIGenerator/CandidateGenerator.cs`
- [x] 일괄 생성 — [전체 생성 (미생성만)] 버튼
  - 구현 결과: 프롬프트가 있고 후보가 없으며 미확정인 항목만 순차 생성(항목당 4개, `GenerateAsync` 재사용, 항목별 기본 워크플로 사용 — 현재 선택 워크플로와 같은 항목은 편집한 변수 값도 함께 사용하되 role 프롬프트 변수는 제외해 항목 프롬프트 자동 주입 유지). 진행률 바에 "일괄 생성 중 (i/N) 항목명... %" 표시, [취소] 지원, 창 닫으면 취소(OnDisable). 실패 항목은 기록 후 다음 항목 계속 진행, 완료 시 성공/실패 개수 상태 메시지 + 실패 목록 요약 다이얼로그. 대상 0개면 안내 메시지. async/await 기반으로 에디터 블로킹 없음.
- [x] 확정 연동 — 배지 즉시 갱신 + 다음 미확정 항목 자동 이동
  - 구현 결과: 확정 성공 시 `RefreshItemStatuses()`로 배지 즉시 갱신 후 `MoveToNextUnconfirmedItem()`이 현재 위치에서 순환 탐색해 다음 미확정 항목을 자동 선택(프롬프트/워크플로/기존 후보 자동 반영). 모든 항목 확정 시 "모든 항목 확정 완료 — 4단계(AssetApplier)로 진행하세요" 상태 메시지 + 다이얼로그. PromptSet 미로드(수동 생성 확정)면 기존 동작 유지.
- [x] 기존 기능 유지 확인
  - 서버 시작/종료, 변수 편집 UI(visibleWhen/options 포함), 수동 생성(manual_), 미리 보기/듣기, 재생성 로직 변경 없음. 워크플로/변수 UI는 항목 선택과 독립 편집 가능(항목 전환 시 role 변수만 갱신).
  - 검증 상태: refresh_unity(compile request) + read_console 확인 — 컴파일 오류/경고 0건 (MCP WebSocket 무관 경고 1건 제외).

### 에디터 테스트 체크리스트 (보완분)

- [ ] PromptSet 로드 시 항목이 행 목록으로 표시되고 상태 배지(프롬프트 없음/미생성/후보 N개/확정됨)가 실제 폴더·기록과 일치함
- [ ] 후보가 이미 있는 항목을 클릭하면 기존 후보 썸네일이 자동 로드되고, 항목 종류에 맞는 워크플로가 자동 선택됨(다른 변수 편집 값은 유지)
- [ ] [전체 생성 (미생성만)] 실행 시 후보 보유/확정/프롬프트 없음 항목은 건너뛰고 나머지가 순차 생성됨 — 진행률 (i/N) 표시, 취소 동작, 일부 실패 시 완료 후 실패 목록 다이얼로그 표시
- [ ] 항목 확정 시 배지가 즉시 "확정됨"으로 바뀌고 다음 미확정 항목으로 자동 이동, 마지막 항목 확정 시 "모든 항목 확정 완료 — 4단계 진행" 안내가 뜸
- [x] 선택 항목 생성 (2026-07-23 추가 보완)
  - 구현 결과: 항목 행 왼쪽에 체크박스 추가(행 클릭 선택과 별개, 프롬프트 없는 항목은 체크 불가), 목록 상단 [모두 선택]/[모두 해제] + "선택 M개" 표시. [선택 항목 생성 (M개)] 버튼(체크 0개면 비활성) — 체크 항목만 순차 생성, 기존 일괄 생성 로직(`RunBatchGenerationAsync`) 재사용(진행률 (i/N)/취소/실패 기록 후 계속/완료 요약/배지 갱신). 후보가 이미 있는 체크 항목은 재생성(기존 후보 삭제 후 새 시드). PromptSet 재로드 시 체크 초기화. 검증: refresh_unity+read_console 오류 0건.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`

### 에디터 테스트 체크리스트 (선택 생성)

- [ ] 항목 체크박스/[모두 선택]/[모두 해제]가 동작하고 프롬프트 없는 항목은 체크 불가, [선택 항목 생성 (M개)] 버튼이 체크 0개일 때 비활성됨
- [ ] 후보가 이미 있는 항목을 체크해 [선택 항목 생성] 실행 시 해당 항목이 재생성(기존 후보 삭제 후 새 시드 4장)되고 완료 후 배지가 갱신됨
- [x] 워크플로별 항목 필터 + 2열 레이아웃 (2026-07-23 추가 보완)
  - 구현 결과: ① 항목 목록이 현재 선택된 워크플로에 해당하는 항목만 표시(Audio→audio, UI→ui/isUI, GenerateImage·StyleChange 등 이미지 계열→image; 워크플로 드롭다운 변경 시 즉시 갱신). "확정 n/N"·[모두 선택/해제]·일괄/선택 생성 대상 모두 필터 기준으로 동작, 숨겨진 항목의 체크는 생성 대상에서 제외, 필터 결과 0개면 "이 워크플로에 해당하는 항목이 없습니다" 안내(전체 확정 완료 판정은 문서 전체 기준 유지). ② 서버 상태 바 아래를 2열로 재배치 — 왼쪽 고정 열(320px, PromptSet 선택/로드 + 항목 목록 자체 스크롤), 오른쪽 열(워크플로/변수 + 생성 버튼 + 후보 썸네일/확정, 기존 스크롤 유지). minSize 960x600, 썸네일 열 수는 오른쪽 열 폭 기준 계산. 검증: refresh_unity+read_console 오류 0건.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`

## 4. 2026-07-24 보완 — GenerateImageFlux 워크플로 추가 (Flux 전용)

- [x] 워크플로 JSON 배치 (`Server~/workflows/GenerateImageFlux.json`)
  - 구현 결과: `C:\Project\CreateMCP\ComfyUI\GenrateImage Only Flux.json`을 **구조 변경·토큰 치환 없이 원본 그대로** 복사하고, 설정값·MCP 파라미터로 쓰기 좋게 파일명만 `GenerateImageFlux.json`으로 정규화(원본 파일명의 오타·공백 제거). GenerateImage와 달리 Checkpoint/LoRA 노드 없이 **UNET(#25 `flux-2-klein-base-4b-fp8.safetensors`) + CLIP(#9 `qwen_3_4b.safetensors`, type `flux2`) + VAE(#10 `flux2-vae.safetensors`)** 조합만 사용하는 Flux 전용 워크플로이며, 배경 제거는 #21 PrimitiveBoolean(true) → #20 ComfySwitchNode → #23 InspyrenetRembg 경로를 사용. 브리지 서버 `list_workflow_names()`가 `workflows/*.json`을 자동 스캔하므로 **C# 로직 수정 없이 드롭다운/`GET /workflows`에 자동 노출**되고, `set_seed()`가 #5 KSampler의 `seed` 필드를 찾아 4후보 연속 시드 생성이 그대로 동작함. `Server~`는 Unity 임포트 제외 폴더라 `.meta` 미생성.
  - 검증 상태: JSON 파싱 성공 + 원본 JSON과 내용 완전 동일(`dict` 비교 True) 확인, #5 `seed` 필드 존재 확인, `list_workflow_names()`/`load_workflow()` 코드 경로 재확인.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/Server~/workflows/GenerateImageFlux.json`
- [x] 변수 매니페스트 항목 추가 (`Server~/variables.json` → `"GenerateImageFlux"`)
  - 구현 결과: ComfyUI.md `# GenrateImage Only Flux.json` 명세와 **정확히 일치하는 10개 변수**를 기존 GenerateImage 스타일(한국어 label/description, type, min/max, role)로 정의 — #9 `clip_name`(텍스트 인코더, 기본 `qwen_3_4b.safetensors`) / #9 `type`(CLIP 타입, `flux2`) / #10 `vae_name`(`flux2-vae.safetensors`) / #2 `text`(긍정 프롬프트, role positive, 원본 JSON의 긴 프롬프트) / #3 `text`(부정 프롬프트, role negative, 빈 문자열) / #5 `steps`(30, 1~150) / #5 `cfg`(5.5, 0~30) / #5 `sampler_name`(`euler`) / #6 `width`(1024, 64~4096) / #6 `height`(2560, 64~4096). 명세에 없는 #21 배경 제거 토글·#25 `unet_name`은 의도적으로 매니페스트에 넣지 않음(워크플로 JSON 기본값 그대로 사용). 배치는 `GenerateImage` 다음.
  - 검증 상태: `json.load` 파싱 성공 + 10개 항목의 `default` 값이 워크플로 JSON 원본 값과 전부 일치함을 스크립트로 대조 확인(10/10 MATCH). 매니페스트 키 순서 `GenerateImage → GenerateImageFlux → UI → StyleChange → Audio`.
  - 관련 파일: `.../ComfyUIGenerator/Server~/variables.json`
- [x] 워크플로 이름 나열 문자열/문서 갱신 (로직 변경 없음)
  - 구현 결과: MCP 도구 설명·파라미터 설명·설정 툴팁의 워크플로 나열에 `GenerateImageFlux` 추가 — `McpForUnityAdapter.cs`(도구 Description 1곳, `workflowName` ToolParameter 2곳), `ComfyUIGeneratorTool.cs`(도구 설명 1곳), `MCPSettingsWindow.cs`(기본 이미지 워크플로 툴팁 1곳). 창의 워크플로별 항목 필터는 `Audio`/`UI` 외 전부 이미지 계열로 처리하므로 `GenerateImageFlux`는 자동으로 image 항목에 매칭됨(코드 수정 불필요).
  - 검증 상태: 문자열 리터럴만 변경(따옴표/이스케이프 변경 없음), Unity 실행은 하지 않음 — 컴파일 확인은 아래 에디터 테스트 항목에서 수행.
  - 관련 파일: `.../McpForUnityBridge/McpForUnityAdapter.cs`, `.../ComfyUIGenerator/ComfyUIGeneratorTool.cs`, `.../Common/MCPSettingsWindow.cs`
- [x] README 갱신
  - 구현 결과: 요구 사항 섹션에 GenerateImageFlux 전제 모델/커스텀 노드 한 줄 추가(UNET `flux-2-klein-base-4b-fp8.safetensors`, 텍스트 인코더 `qwen_3_4b.safetensors`, VAE `flux2-vae.safetensors`, ComfyUI-Inspyrenet-Rembg + ComfySwitchNode, "Checkpoint/LoRA 없이 UNET+CLIP+VAE만 쓰는 Flux 전용" 성격 명시), 워크플로 드롭다운 목록 설명·"워크플로별 조정 변수" 표에 `GenerateImageFlux` 행 추가, MCP 도구(`mcptools_generate_candidates`·`mcptools_run_generate_and_apply`) `workflowName` 파라미터 설명의 워크플로 나열 갱신.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/README.md`

### 에디터 테스트 체크리스트 (GenerateImageFlux 추가분)

- [ ] 스크립트 컴파일 오류/경고 0건 확인 (문자열만 수정했으나 재확인)
- [ ] [서버 시작] 후 워크플로 드롭다운에 `GenerateImageFlux`가 표시됨 (총 5종: GenerateImage/GenerateImageFlux/UI/StyleChange/Audio)
- [ ] `GenerateImageFlux` 선택 시 변수 UI에 10개 항목(텍스트 인코더·CLIP 타입·VAE 파일명·긍정/부정 프롬프트·Steps·CFG·Sampler·Width·Height)이 표시되고 기본값이 각각 `qwen_3_4b.safetensors` / `flux2` / `flux2-vae.safetensors` / (원본 긴 프롬프트) / (빈 문자열) / 30 / 5.5 / `euler` / 1024 / 2560 으로 채워짐
- [ ] ComfyUI 연결 상태에서 `sampler_name`·`type` 등 string 변수가 `/object_info` 선택지 기반 Popup으로 표시됨
- [ ] [기본값 복원] 동작 확인
- [ ] PromptSet의 이미지 항목이 `GenerateImageFlux` 선택 시 항목 목록에 필터링되어 표시되고, 항목 선택 시 긍정/부정 프롬프트가 자동 채워짐
- [ ] [후보 4개 생성] 실행 시 서로 다른 연속 시드로 PNG 4장 + 메타 `{seed}.json`이 `Assets/Generated/Candidates/{항목id}/`에 저장됨 (배경 제거가 적용된 투명 PNG)
- [ ] 썸네일 선택 → [확정] 시 `Assets/Generated/Images/{항목id}.png` 복사 + Sprite 임포트 설정 적용
- [ ] 모델(`flux-2-klein-base-4b-fp8.safetensors` 등) 미설치 상태에서 생성 시 원인 안내 메시지가 표시되고 에디터가 멈추지 않음
- [ ] MCP 도구 `mcptools_generate_candidates`를 `workflowName: "GenerateImageFlux"`로 호출해 후보가 생성됨

## 5. 2026-07-24 보완 — 배포 안정성: Python 자동 탐지 + 시작 실패 안내

> 배경: zip 배포를 받은 다른 PC에서 `pythonExecutable` 기본값 `"python"`을 그대로 실행하다 보니
> ① PATH 미등록 ② Unity 실행 뒤 Python 설치(옛 PATH 사용) ③ Windows 스토어 앱 실행 별칭 스텁
> ④ Python 2 ⑤ 시작 직후 파이썬이 죽어도 안내가 없음 — 의 상황에서 서버가 뜨지 않거나 조용히 실패함.

- [x] `ComfyUIServerLauncher.ResolvePython` — Python 후보 탐지 + 실제 실행 검증 (새 클래스 없이 기존 static 클래스에 메서드 추가)
  - 구현 결과: `ResolvePython(MCPToolSettings, out argumentPrefix, out version, out diagnostics)` 추가. 후보 생성 순서 = ① 설정 `pythonExecutable`(경로 구분자 포함 시 `File.Exists` 확인, 아니면 명령어 이름) → ② 명령어 이름(Windows: `py`(접두사 `-3`)/`python`/`python3`, 그 외: `python3`/`python`) → ③ Windows 표준 설치 폴더 글로빙(`%LOCALAPPDATA%\Programs\Python\Python3*`, `%ProgramFiles%\Python3*`, `%ProgramFiles(x86)%\Python3*`, `%SystemDrive%\Python3*` — `Directory.GetDirectories` 패턴 매칭, 상위 폴더 없으면 조용히 건너뜀, 최신 버전 폴더 우선) → ④ PATH 환경변수 직접 파싱해 각 디렉터리의 `python.exe`/`python3.exe`(그 외 OS는 `python3`/`python`)를 **절대 경로** 후보로 추가 → ⑤ 비Windows 표준 경로(`/usr/bin/python3`, `/usr/local/bin/python3`, `/opt/homebrew/bin/python3`). 후보는 중복 제거.
    검증은 각 후보를 `-c "import sys; print(sys.version_info[0]); print(sys.version_info[1]); print(sys.executable)"`로 **실제 실행**(`UseShellExecute=false`, stdout/stderr 리다이렉트, `CreateNoWindow=true`, `WaitForExit(5000)` — 타임아웃 시 Kill 후 실패 처리)하고 **종료 코드 0 + major==3 + minor>=7**일 때만 성공 처리. 스토어 별칭 스텁·Python 2·비Python 실행 파일은 여기서 자동 탈락. 성공 시 `sys.executable`(절대 경로)이 존재하면 그 경로를 반환하고 인자 접두사는 비움(=`py -3`로 찾아도 실제 python.exe 절대 경로가 남음).
    결과는 SessionState(`MCPTools.Bridge.PythonPath` / `.PythonArgs` / `.PythonVersion` / `.PythonSetting`)에 캐시하며, 캐시된 경로가 사라졌거나 설정의 `pythonExecutable` 값이 바뀌면(캐시에 사용된 설정값을 함께 저장해 비교) 캐시를 무시하고 재탐색. `ClearPythonCache()` 공개 메서드로 수동 무효화.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`
- [x] `Start()`가 탐지 결과 사용 + 탐지 실패 시 행동 가능한 안내
  - 구현 결과: `Start()`는 `ResolvePython` 결과(실행 파일 + 인자 접두사)를 사용하고, 접두사가 있으면 스크립트 경로 앞에 붙임. 탐지 실패 시 `BuildPythonNotFoundMessage(diagnostics)`(공개 메서드)로 만든 `InvalidOperationException`을 던져 창에서 다이얼로그로 표시 — ① python.org 설치 + "Add python.exe to PATH" 체크 ② Unity 실행 중 설치했으면 Unity/Unity Hub 재시작 ③ 설정 > 앱 > 고급 앱 설정 > 앱 실행 별칭에서 python.exe 끄기 ④ Tools/MCP/Settings의 Python 실행 파일에 절대 경로 지정([자동 탐지] 버튼) + 마지막에 시도한 후보와 실패 사유 목록. 시작 로그에 실행 파일과 검증된 Python 버전을 함께 기록.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIServerLauncher.cs`
- [x] 시작 직후 조기 종료 감지
  - 구현 결과: `Process.Start` 후 `WaitForExit(1000)`으로 1초 대기해 이미 종료됐으면 PID를 저장하지 않고 예외를 던짐. 메시지에 **종료 코드**, 실행 대상, "포트 {port}가 이미 사용 중일 수 있습니다" 힌트, [자동 탐지] 안내, 그리고 `--log-file` 로그의 **마지막 2000자**(FileShare.ReadWrite로 읽어 실행 중 잠금 회피, 실패 시 조용히 생략)를 포함. `showBridgeConsole=true`(UseShellExecute)일 때도 `HasExited`/`ExitCode`는 확인되므로 동일하게 동작.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIServerLauncher.cs`
- [x] `MCPSettingsWindow` — [Python 자동 탐지] 버튼
  - 구현 결과: "Python 실행 파일" 필드 아래에 [Python 자동 탐지] 버튼 추가(버튼이 `GUI.changed`를 켜서 필드 값이 되돌려지는 것을 막기 위해 `EndChangeCheck` 블록 **밖**에 배치). 클릭 시 캐시를 지우고 `ResolvePython` 호출 → 성공하면 `Undo.RecordObject` + 절대 경로 대입 + `EditorUtility.SetDirty` + `AssetDatabase.SaveAssets`(창의 기존 저장 흐름) 후 경로·버전 다이얼로그, 실패하면 `BuildPythonNotFoundMessage` 안내 다이얼로그. 편집 중 텍스트 필드가 옛 값을 유지하지 않도록 `GUI.FocusControl(null)` + `Repaint()`. 필드 툴팁을 "비워두면 자동 탐지합니다. 자동 탐지에 실패하면 python.exe 절대 경로를 직접 지정하세요."로 갱신. 다른 설정 항목·레이아웃은 변경 없음.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Common/MCPSettingsWindow.cs`
- [x] `bridge_server.py` 버전 가드
  - 구현 결과: 모듈 상단(`import sys` 직후, `urllib.error`/`http.server` 등 Python 3 전용 임포트보다 **앞**)에서 `sys.version_info < (3, 7)`이면 현재 버전과 조치(python.org 설치 + PATH 체크, Unity 설정에서 [Python 자동 탐지]/절대 경로 지정)를 stderr에 한국어로 출력하고 `sys.exit(1)`. 그 외 로직 변경 없음(f-string 미사용 파일이라 Python 2에서도 파싱되어 가드가 실제로 동작함).
  - 검증 상태: `python -m py_compile bridge_server.py` 성공(Python 3.12.7).
  - 관련 파일: `.../ComfyUIGenerator/Server~/bridge_server.py`
- [x] README 갱신
  - 구현 결과: 요구 사항의 Python 항목을 **Python 3.7 이상**으로 명시하고 "Add python.exe to PATH" 체크 권장 + 자동 탐지 동작 요약 추가, 설정 표 `pythonExecutable` 행을 "비워두거나 기본값이면 자동 탐지" 취지로 갱신, [서버 시작] 설명에 탐지 순서·검증·조기 종료 안내 반영, "문제 해결"에 **브리지 서버가 시작되지 않을 때** 항목(미설치/PATH, Unity 재시작, 스토어 별칭, 포트 충돌, 절대 경로 지정·[자동 탐지], 로그 위치 `%TEMP%/mcptools_bridge_server.log`) 추가.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/README.md`
- [x] 제약 준수
  - 새 파일·새 클래스 없음(기존 `ComfyUIServerLauncher`/`MCPSettingsWindow` 내부에만 추가), `Microsoft.Win32.Registry` 미사용(`System.Diagnostics.Process`/`System.IO`/`System.Environment`/`UnityEditor.SessionState`만 사용), 절대 경로·드라이브 문자 하드코딩 없음(환경변수 조합), Editor 전용 유지(`MCPTools.Editor` 네임스페이스).
  - 검증 상태: Unity 실행 없이 정적 검증만 수행 — 중괄호/문자열 정합성 스캐너로 두 C# 파일 균형 확인, python 파일 `py_compile` 성공. **컴파일 확인은 아래 에디터 테스트 항목에서 수행 필요.**

### 에디터 테스트 체크리스트 (Python 자동 탐지 보완분)

- [ ] 스크립트 컴파일 오류/경고 0건 확인 (`ComfyUIServerLauncher.cs`, `MCPSettingsWindow.cs`)
- [ ] `Tools/MCP/Settings`에서 [Python 자동 탐지] 클릭 → python.exe **절대 경로**가 필드에 채워지고 "경로 / 버전 Python 3.x" 다이얼로그가 뜸, 창을 닫았다 열어도 값이 유지됨(에셋 저장 확인)
- [ ] 설정의 Python 실행 파일을 `python`(기본값) 또는 빈 값으로 두고 [서버 시작] → 정상 시작되고 콘솔 로그에 실행 파일·Python 버전·PID·포트가 표시됨
- [ ] 설정에 존재하지 않는 경로(예: `D:/none/python.exe`)를 넣고 [서버 시작] → 자동 탐지가 다른 후보로 넘어가 정상 시작됨
- [ ] PATH에서 Python을 제거한 환경(또는 존재하지 않는 명령어만 남긴 상태)에서 [서버 시작] → "Python 3.7 이상을 찾지 못했습니다" 안내 다이얼로그에 4가지 조치와 시도한 후보/실패 사유가 표시됨
- [ ] 브리지 서버를 이미 띄운 상태에서 [서버 시작]을 한 번 더 실행(또는 8189 포트를 다른 프로그램이 점유) → "시작 직후 종료(종료 코드 N)" + "포트 8189가 이미 사용 중일 수 있습니다" + 로그 마지막 내용이 다이얼로그에 표시됨
- [ ] "브리지 콘솔 창 표시"를 켠 상태에서도 시작/조기 종료 감지가 동일하게 동작함
- [ ] 두 번째 [서버 시작]부터는 탐지가 즉시 끝남(SessionState 캐시), 설정의 Python 실행 파일을 바꾸면 다시 탐지함
- [ ] Windows 스토어 별칭만 있는 환경(설정 > 앱 실행 별칭 켬, 실제 Python 미설치)에서 [서버 시작] → 스토어 창이 뜨지 않고 안내 다이얼로그가 표시됨

## 후보 개수 창에서 직접 조절 (2026-07-25 요청)

- [x] 3단계 생성 창의 [생성] 버튼 바로 위에 **후보 개수 슬라이더**(`IntSlider`, 1~12, 기본 4) 추가.
  - 값은 새 필드를 만들지 않고 기존 `MCPToolSettings.candidateCount`에 저장한다 — `Tools/MCP/Settings`의 "후보 개수", `CandidateGenerator`, 전체 생성, MCP 도구(`PipelineTool`의 `config.candidateCount`)가 이미 같은 값을 쓰므로 경로 전체가 자동으로 따라간다.
  - `Undo.RecordObject` + `EditorUtility.SetDirty`로 기존 "생성 완료 후 모델 언로드" 토글과 동일하게 처리.
  - 버튼 라벨은 이미 `후보 {N}개 생성` / `재생성 (새 시드로 {N}개)`로 동적이었으므로 라벨 로직 변경 없음.
  - 상한 12는 ComfyUI 큐가 한 번에 처리하기 현실적인 수준으로 잡은 값(`MaxCandidateCount` 상수).
  - 관련 파일: `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`, `Assets/MCPTools/README.md`(3단계 §3)
- 검증 상태: 정적 검증. **Unity 컴파일·동작 확인 필요.**

### 에디터 테스트 (후보 개수)

- [ ] 슬라이더를 6으로 바꾸면 버튼 라벨이 [후보 6개 생성]으로 즉시 바뀌고, 생성 결과가 실제로 6개 나옴(시드 `seed..seed+5`)
- [ ] 바꾼 값이 `Tools/MCP/Settings`의 "후보 개수"에도 반영되고, 창을 닫았다 열거나 에디터를 재시작해도 유지됨
- [ ] [미생성 전체 생성]·[선택 항목 생성]도 같은 개수로 생성됨
- [ ] 1로 내렸을 때 정상 동작하고, 슬라이더가 0 이하로 내려가지 않음

## 6. 2026-07-25 요청 — 현재 설정으로 다중 항목 생성 + 생성 창 항목 편집

> 배경: 3단계에서 항목을 하나씩만 생성할 수 있어(전체 생성은 "미생성만" 한 종류뿐) 같은 설정으로 여러 항목을
> 돌리기 번거로웠고, 항목을 고치려면 2단계 창으로 돌아가야 했다.

- [x] 항목 체크박스 + 선택 도구 모음 (재구현 — 2열 레이아웃 개편 때 유실됨)
  - 구현 결과: 목록 각 행 왼쪽에 체크박스(행 클릭 선택과 독립, `_checkedItemIds` HashSet 보관). 목록 상단에 [전체]/[해제]/[미생성] 버튼(`DrawSelectionToolbar`) — 대상은 현재 워크플로 필터를 통과한 항목으로 한정. 목록 하단 요약을 "확정 n/N · 체크 M개"로 확장. PromptSet 재로드·[새 목록] 시 체크 초기화, 항목 삭제 시 해당 체크 제거.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs` (`DrawSelectionToolbar`/`SetItemChecked`/`CheckedTargets`/`UnrenderedTargets`/`IsUnrenderedTarget`)
- [x] [선택 항목 생성] / [미생성 전체 생성] — 현재 설정 그대로 일괄 생성
  - 구현 결과: 생성 버튼을 3개로 분리 — [후보 N개 생성](단건) / [선택 항목 생성 (M개 × N)](체크 0개면 비활성) / [미생성 전체 생성 (K개 × N)]. 두 일괄 버튼 모두 `StartBatchGeneration(targets, scopeLabel)` → `RunBatchGenerationAsync(targets, scopeLabel)`를 공유한다. **일괄 생성이 항목별 기본 워크플로가 아니라 현재 선택된 워크플로와 편집 중인 변수 값을 모든 대상에 적용**하도록 변경(role=positive/negative 변수만 제외해 항목별 프롬프트 자동 주입 유지). 변수 맵은 루프 **밖에서 1회** 만들어 참조 이미지 업로드가 항목 수만큼 반복되지 않는다. 실행 전 확인 다이얼로그에 대상 수·워크플로·항목당 후보 수, 프롬프트가 비어 제외되는 수, **기존 후보가 삭제되고 재생성되는 수**를 표시. 진행률/취소/실패 기록 후 계속/완료 요약/배지 갱신은 기존 로직 유지.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs` (`DrawGenerateSection`/`StartBatchGeneration`/`RunBatchGenerationAsync`)
- [x] 항목 클릭 시 워크플로 자동 전환 조건 완화
  - 구현 결과: `SelectItem`이 항목을 고를 때마다 기본 워크플로로 되돌리던 동작을, **현재 워크플로가 이미 그 항목 종류에 해당하면 유지**하도록 변경(`ItemMatchesWorkflow(item)`이면 전환하지 않음). 일괄 생성이 "현재 워크플로"를 쓰기 때문에 `StyleChange` 등으로 작업 중 항목을 클릭하면 설정이 되돌아가는 문제를 막는다. 종류가 다를 때(오디오 항목을 이미지 워크플로에서 클릭 등)는 기존대로 전환.
- [x] 항목 추가/수정/삭제 — **별도 편집 창** 방식 (사용자 피드백 반영, 2026-07-25 재작업)
  - 1차 구현(왼쪽 열 하단의 접이식 [항목 편집] 인라인 패널)은 **PromptSet 로드 시 왼쪽 열 레이아웃이 눌려 기존 2열 화면이 달라 보이는 문제**로 사용자 피드백을 받아 폐기했다. 인라인 패널·[새 목록] 버튼·문서 경로 표시 행을 모두 제거하고 왼쪽 열 폭도 320px로 되돌려 **기존 2열 레이아웃을 그대로 유지**한다.
  - 구현 결과: 항목 목록 아래에 **한 줄짜리 조작 행**(`DrawItemActionRow`)만 추가 — [추가]/[편집]/[삭제]/[저장 (*)]. [추가]·[편집]은 보조 창 `PromptItemEditWindow`(같은 파일, `PromptSetJsonImportWindow` 패턴)를 띄우고, 그 창에서 ID(자동 부여·읽기 전용)·이름·종류·UI 여부·대상 프리팹/오브젝트 경로·설명·positive/negative를 작성한 뒤 [저장]하면 `ApplyEditedItem`이 **같은 id면 교체, 없으면 목록 끝에 추가**하고 그 항목을 선택 + 변수 편집란 동기화(`ApplyItemPromptsToVariables(overwriteWithEmpty: true)`)까지 수행한다. 편집 창은 **복사본**을 다루므로 [취소] 시 원본이 그대로 남는다. 새 항목 초안(`CreateItemDraft`)은 현재 워크플로 종류에 맞는 assetType + 변수란의 프롬프트를 초기값으로 갖고, 창의 [생성 창의 현재 프롬프트 가져오기] 버튼으로 다시 가져올 수 있다. 편집 창 필드는 `[SerializeField]`로 보관해 도메인 리로드 후에도 입력 내용과 소유 창 연결이 유지된다.
  - 삭제는 목록에서만 제거(후보/확정 파일 보존)하며 확인 다이얼로그를 거친다. [저장]은 `PromptBuilder.Save(doc, path)` 재사용 — 로드한 경로가 있으면 덮어쓰고 없으면 `Docs/2_PromptSet/PromptSet_{yyyyMMdd_HHmm}.json` 신규 저장 후 드롭다운을 갱신·선택. 미저장 변경은 버튼 라벨 `저장 *`로 표시하고, 다른 PromptSet 로드 전에 확인 다이얼로그(`ConfirmDiscardChanges`). PromptSet 미로드 상태에서도 입력 영역의 [항목 추가]로 빈 목록을 시작할 수 있다(문서 없을 때만 보이던 안내 라벨과 같은 줄이라 행 수 변화 없음).
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(`DrawItemActionRow`/`OpenItemEditor`/`CreateItemDraft`/`ApplyEditedItem`/`DeleteSelectedItem`/`SaveDocument`/`PromptItemEditWindow`), `MCPToolTest/Assets/MCPTools/README.md`(3단계 §3·§4), `MCPToolTest/Assets/MCPTools/CHANGELOG.md`
- 검증 상태: `validate_script`(standard) 오류 0건, `refresh_unity`(compile request) + `read_console` 컴파일 오류 0건 (MCP WebSocket 무관 경고 1건 제외). **동작 확인은 아래 에디터 테스트 필요.**

### 에디터 테스트 체크리스트 (다중 생성 / 항목 편집)

- [ ] 행 체크박스와 [전체]/[해제]/[미생성] 버튼이 현재 워크플로 필터 기준으로 동작하고, 하단 요약이 "확정 n/N · 체크 M개"로 갱신됨
- [ ] [선택 항목 생성 (M개 × N)] — 체크 0개면 비활성, 실행 시 확인 다이얼로그에 대상 수/워크플로/재생성 대상 수가 맞게 표시되고 체크한 항목만 순차 생성됨
- [ ] 워크플로를 `StyleChange`(또는 `GenerateImageFlux`)로 바꾸고 변수를 조정한 뒤 일괄 생성 → **모든 항목이 그 워크플로와 조정한 변수 값으로** 생성되고, 프롬프트만 항목별 값이 들어감(후보 메타 `{seed}.json`으로 확인)
- [ ] 참조 이미지가 있는 워크플로(UI/StyleChange)로 일괄 생성 시 업로드가 1회만 일어나고 모든 항목에 같은 참조가 적용됨
- [ ] 이미지 항목을 여러 개 클릭해도 워크플로 선택이 `StyleChange`에서 기본값으로 되돌아가지 않음 / 오디오 항목을 클릭하면 `Audio`로 전환됨
- [ ] **PromptSet을 로드해도 창 레이아웃이 기존 2열 그대로**이고(왼쪽 320px 목록 + 오른쪽 워크플로/생성/후보), 목록 아래에 [추가]/[편집]/[삭제]/[저장] 한 줄만 추가되어 있음
- [ ] [추가] → 편집 창에서 이름/프롬프트 작성 → [저장 (목록에 추가)] 시 목록 끝에 새 항목이 생기고 자동 선택되며, 그 상태로 [후보 N개 생성]이 작성한 프롬프트로 동작함
- [ ] [편집] → 값 수정 후 [저장 (항목에 반영)] 시 같은 ID 항목이 갱신되고 오른쪽 변수 편집란의 프롬프트도 함께 바뀜 / [취소] 시 원본이 그대로 유지됨
- [ ] 편집 창의 [생성 창의 현재 프롬프트 가져오기]가 워크플로 변수의 positive/negative를 그대로 채움
- [ ] [삭제] 시 확인 다이얼로그가 뜨고, 삭제해도 `Generated/3_Candidates/{항목id}/`의 후보 파일은 남아 있음
- [ ] 편집 후 저장하지 않으면 버튼이 [저장 *]로 표시되고, 그 상태로 다른 PromptSet을 [로드]하면 확인 다이얼로그가 뜸
- [ ] [저장] — 로드한 PromptSet JSON이 갱신되고 `*`가 사라짐 / PromptSet 없이 시작한 목록은 `Docs/2_PromptSet/`에 새 파일로 저장되고 드롭다운에서 그 파일이 선택됨
- [ ] PromptSet 미로드 상태에서 [항목 추가] → 항목 구성 → 생성 → 확정 → [저장] 흐름이 동작함

## 7. 2026-07-25 요청 — 항목 편집 창 대상 선택 방식 + 빈 프롬프트 + 오른쪽 열 잘림

> 배경: 항목 편집 창에서 대상 프리팹/오브젝트를 경로 문자열로 직접 타자쳐야 했고, 새 항목의 프롬프트가
> 변수란 값으로 미리 채워져 있었다. 또 생성 창 오른쪽 열이 창 폭을 넘어가 내용이 잘려 보였다.

- [x] 대상 프리팹/대상 오브젝트를 **선택 방식**으로 변경
  - 구현 결과: `PromptItemEditWindow`의 두 TextField를 `DrawTargetFields`로 교체. **대상 프리팹**은 `EditorGUILayout.ObjectField(GameObject, allowSceneObjects: false)` — 드래그&드롭/◎ 선택, 선택 즉시 `AssetDatabase.GetAssetPath`로 경로 기록하고 대상 오브젝트 선택은 초기화. **대상 오브젝트**는 프리팹 계층을 훑어 만든 경로 드롭다운(`CollectObjectPaths`/`DrawObjectPathPopup`) — 경로 형식은 `AssetApplier.FindTargetTransform` 규칙 그대로(루트=루트 이름, 자식=루트 기준 상대 경로), 표시 라벨은 팝업이 하위 메뉴로 해석하지 않게 `/`를 ` › `로 치환하고 Image/RawImage/SpriteRenderer/AudioSource가 있으면 `[컴포넌트]` 표시를 덧붙인다. 목록은 `_cachedPrefabPath` 기준으로 프리팹이 바뀔 때만 재생성. 프리팹 미지정 시 비활성 팝업으로 안내하고, 프리팹 경로를 찾을 수 없으면 경고 + [경로 지우기], 저장된 오브젝트 경로가 계층에 없으면 값을 버리지 않고 `(현재: 경로)`를 경고색으로 유지한다.
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(`PromptItemEditWindow.DrawTargetFields`/`RefreshObjectPathsIfNeeded`/`CollectObjectPaths`/`CollectChildPaths`/`SlotSuffix`/`DrawObjectPathPopup`)
- [x] 새 항목 프롬프트는 기본 빈칸
  - 구현 결과: `CreateItemDraft`에서 `CopyVariablePromptsTo(item)` 호출 제거 — ID·종류만 채우고 positive/negative는 빈칸으로 시작한다. 변수란 값이 필요하면 편집 창의 [생성 창의 현재 프롬프트 가져오기] 버튼으로 가져온다(기존 버튼 유지).
- [x] 오른쪽 열이 창 밖으로 잘리던 문제 — 가로 스크롤 없이 표시 폭에 맞춤
  - 1차 시도(가로 스크롤바를 `GUIStyle.none`으로 지정)는 **역효과**라 폐기했다 — Unity는 가로 스크롤바가 none이면 `allowHorizontalScroll = false`로 보고 **스크롤뷰의 최소 폭을 내용 폭으로 잡기 때문에**, 워크플로/변수가 채워져 세로 스크롤바가 생기는 순간 오른쪽 열이 창 밖으로 밀려 더 심하게 잘렸다(사용자 피드백).
  - 구현 결과: 스크롤뷰는 기본값(`BeginScrollView(_scroll)`)으로 되돌리고, **내용을 표시 폭에 고정**해 애초에 가로로 넘치지 않게 한다. 열 폭은 스크롤뷰 **바깥**에서 높이 0 사각형으로 측정(`MeasureRightColumnWidth`, Repaint 시에만 갱신)하고 — 안에서 재면 내용이 넓어질수록 측정값도 커지는 되먹임이 생긴다 — 세로 스크롤바 몫 20px를 뺀 값(`RightContentWidth`)을 `VerticalScope(GUILayout.Width(...))`에 적용한다. 같은 폭 기준으로 `EditorGUIUtility.labelWidth`를 `Clamp(폭*0.4, 90, 160)`으로 낮췄다가 스크롤뷰 종료 전에 복원한다. 후보 격자는 이 실측 폭으로 열 수를 정하고 셀 크기도 남는 폭에 맞춰 축소(`DrawCandidateCell(candidate, index, cellSize)`), 왼쪽 열 폭은 고정 320px에서 `Mathf.Clamp(창폭 - 420, 240, 320)`으로 변경(`CurrentLeftColumnWidth`), 라벨이 긴 일괄 생성 버튼 2개는 오른쪽 열이 520px 미만이면 세로로 쌓는다.
  - 함께 수정: 확정 에셋 패널의 경로 라벨과 하단 상태 메시지는 **띄어쓰기 없는 긴 경로** 때문에 word-wrap이 줄을 못 나눠 최소 폭이 커지고, 그만큼 왼쪽 열/창을 넓혀 오른쪽을 밀어냈다. 두 라벨 모두 `GUILayout.Width`로 폭을 고정했다(`DrawConfirmedAssetPanel`/`DrawBottomBar`).
  - 관련 파일: `.../ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(`OnGUI`/`CurrentLeftColumnWidth`/`MeasureRightContentWidth`/`RightContentWidth`/`DrawCandidateSection`/`DrawCandidateCell`/`DrawGenerateSection`/`DrawItemListPanel`), `MCPToolTest/Assets/MCPTools/README.md`(3단계 §4), `MCPToolTest/Assets/MCPTools/CHANGELOG.md`
- 검증 상태: `validate_script`(standard) 오류 0건, `refresh_unity`(compile request) + `read_console` 컴파일 오류 0건(MCP WebSocket 무관 경고 1건 제외). **동작 확인은 아래 에디터 테스트 필요.**

### 에디터 테스트 체크리스트 (대상 선택 / 빈 프롬프트 / 오른쪽 열)

- [ ] [추가]/[편집] 창에서 **대상 프리팹**에 프로젝트 창의 프리팹을 끌어다 놓거나 ◎로 선택하면 경로가 기록되고, 씬 오브젝트는 선택되지 않음
- [ ] **대상 오브젝트** 드롭다운에 프리팹 루트와 모든 자식이 나오고, Image/RawImage/SpriteRenderer/AudioSource가 있는 오브젝트에 `[컴포넌트]` 표시가 붙음. 선택 후 [저장] → 4단계에서 그 오브젝트에 정상 적용됨
- [ ] 대상 프리팹을 다른 프리팹으로 바꾸면 대상 오브젝트 선택이 초기화되고, 새 프리팹의 계층이 드롭다운에 반영됨
- [ ] 대상 프리팹을 삭제/이동한 항목을 [편집]하면 경고와 [경로 지우기]가 뜨고, 계층이 바뀌어 없어진 오브젝트 경로는 `(현재: ...)`로 유지됨
- [ ] [추가]로 만든 새 항목의 positive/negative가 **빈칸**이고, [생성 창의 현재 프롬프트 가져오기]를 누르면 변수란 값이 채워짐
- [ ] 창을 좁게(가로 1000px 이하) 줄여도 오른쪽 열에 **가로 스크롤바가 생기지 않고** 워크플로/변수/버튼이 잘리지 않음. 후보 썸네일이 오른쪽 끝에서 잘리지 않고 열 수가 폭에 맞춰 줄어듦
- [ ] **PromptSet 로드 후 워크플로/변수가 채워져 세로 스크롤바가 생겨도** 오른쪽 열이 창 밖으로 밀리지 않음 (1차 시도에서 발생한 회귀)
- [ ] 확정된 항목을 선택해 왼쪽 하단에 긴 확정 에셋 경로가 표시돼도 오른쪽 열이 밀리지 않음 / 하단 상태 메시지에 긴 경로가 떠도 마찬가지
- [ ] 오른쪽 열 내용이 길어지면 **세로 스크롤**로 [확정] 버튼까지 도달할 수 있음
