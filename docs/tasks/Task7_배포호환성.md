# Task 7 — 배포 호환성 (다른 PC / 다른 Unity 프로젝트)

> 배경: `dist/MCPTools.zip`을 다른 개발자에게 전달했을 때 개발 PC에서만 성립하던 전제(설치된 모델, 커스텀 노드, 폴더 위치, 설치된 Unity 패키지)가 드러났다. 이 Task는 **환경 의존 지점을 정적 감사로 전수 조사하고 제거**하는 것을 목표로 한다.

## 1. 목표

`Assets/MCPTools/`를 다른 PC·다른 Unity 6 프로젝트에 넣었을 때, **개발 PC의 환경에 의존하는 실패를 제거**한다. 제거할 수 없는 외부 의존(모델·커스텀 노드)은 **사전 점검과 원인·조치 안내**로 바꿔 사용자가 스스로 해결할 수 있게 한다.

> **추가 전제(2026-07-24):** 이 도구는 zip 배포에 그치지 않고 **GitHub 저장소에 올린 뒤 Unity Package Manager(UPM, git URL)로 관리**할 예정이다. 배포 단위가 `Assets/` 하위 폴더에서 **읽기 전용 패키지(`Packages/`·`Library/PackageCache/`)** 로 바뀌면 지금까지 잡은 D1~D17과는 **성격이 다른 실패**가 생긴다. 해당 감사는 [§9](#9-github--package-managerupm-배포-전환-시-추가-문제-u)에 U1~U14로 별도 정리한다.

## 2. 감사 범위와 방법

- 정적 분석만 수행(Unity 실행·ComfyUI 호출 없음). 근거는 파일:줄로 표기한다.
- 대상: `MCPToolTest/Assets/MCPTools/` 전체, `Server~/bridge_server.py`·`workflows/*.json`·`variables.json`, `tools/pack-mcptools.ps1`, `dist/MCPTools.zip`.
- 표기: **심각도**(치명/높음/중간/낮음) × **현재 상태**(미대응/부분대응/대응완료).

## 3. 완료된 선행 조치 (이번 Task 범위 밖 — 기록용)

| 조치 | 내용 | 관련 파일 |
|------|------|-----------|
| Python 자동 탐지·검증 | 설정값 → `py -3`/`python`/`python3` → Windows 표준 설치 폴더 → PATH 순으로 후보를 만들고, 각 후보를 **실제 실행해 3.7 이상인지 검증**(스토어 별칭 스텁·Python 2 탈락). 결과는 SessionState 캐시. | `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs:107-172, 368-636` |
| Python 미발견 안내 | 설치·PATH·Unity 재시작·스토어 별칭·절대 경로 지정 5단계 안내 다이얼로그 + 후보별 실패 사유 첨부 | `ComfyUIServerLauncher.cs:179-201` |
| 조기 종료 감지 | 시작 1초 내 프로세스가 죽으면 종료 코드 + 로그 꼬리를 붙여 원인 안내 | `ComfyUIServerLauncher.cs:281-286, 672-741` |
| [자동 탐지] 버튼 | 설정 창에서 탐지 결과를 `pythonExecutable`에 기록 | `Editor/Common/MCPSettingsWindow.cs:112-172` |
| Python 버전 가드 | 브리지 서버가 3.7 미만이면 한국어 안내 후 exit 1 | `Server~/bridge_server.py:16-26` |

## 4. 감사 결과 — 문제 목록 (심각도 순)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 현재 상태 | 대응 방안 |
|---|------|------|----------------|--------|-----------|-----------|
| D1 | 워크플로 기본 모델 파일명이 **개발 PC 전용** | 다른 PC ComfyUI에 `miaomiaoHarem_anima13` / `klein4b_masterpieces_v3.1` / `flux-2-klein-base-4b-fp8` / `qwen_3_4b` / `flux2-vae` / `stable_audio_3_small_*` / `t5gemma_b_b_ul2`가 없으면 **첫 생성부터 100% HTTP 400**. 사전 검증 없이 ComfyUI 응답 원문만 노출 | `Server~/variables.json:6-11,21-23,45-46`, `workflows/GenerateImage.json`·`GenerateImageFlux.json`·`UI.json`·`StyleChange.json`·`Audio.json`의 `ckpt_name`/`unet_name`/`lora_name`/`clip_name`/`vae_name`, `bridge_server.py:228-235` | **치명** | 부분대응 (`/object_info` 기반 드롭다운 `bridge_server.py:140-196`, README 요구 사항 표) | 생성 직전 `/object_info`로 **모델 파일명 존재 여부를 사전 검증**하고, 없으면 "설치된 파일 목록 + 변수 UI에서 교체" 안내를 다이얼로그로 표시. 워크플로 선택 시 기본값이 설치 목록에 없으면 변수 UI에 경고색 표시 |
| D2 | 커스텀 노드 미설치 **감지·안내 수단 없음** | `InspyrenetRembg`(4개 워크플로), `ComfySwitchNode`/`PrimitiveBoolean`(4개 워크플로) 미설치 시 HTTP 400. 사용자는 영문 노드 검증 응답을 직접 해석해야 함 | 워크플로 `class_type` 전수: GenerateImage/GenerateImageFlux/UI = `InspyrenetRembg`+`ComfySwitchNode`+`PrimitiveBoolean`, Audio = `ComfySwitchNode`+`PrimitiveBoolean`+`SaveAudioAdvanced`, StyleChange = `InspyrenetRembg`+`ReferenceLatent`. 조회는 하지만 노드 존재 검증엔 미사용 (`bridge_server.py:140-148, 172-196`) | **높음** | 부분대응 (README 요구 사항에 노드명 명시, 실패 시 원문 노출) | 브리지에 `GET /preflight`(또는 `/workflows` 응답 확장) 추가 — 워크플로의 `class_type` 집합을 `/object_info` 키와 대조해 **누락 노드 목록**을 반환. Unity 창은 워크플로 선택 시 이를 표시하고 [생성]을 막거나 경고. README에 ComfyUI-Manager 설치 경로 안내 추가 |
| D3 | **uGUI(`com.unity.ugui`) 하드 의존이 방어되지 않음** | 패키지가 없는 프로젝트에서 `MCPTools.Editor` 어셈블리 **전체가 컴파일 실패** → 에디터 창 4개 모두 사라짐 (Task 5에서 unity-mcp·2D Sprite에 대해 고친 것과 동일 유형의 잔여 버그) | (수정 전) `Editor/AssetListup/ProjectScanner.cs:7` (`using UnityEngine.UI`), 사용처 `:234-236, 270-279` | **높음** | **대응완료** | `ProjectScanner`에서 `using UnityEngine.UI` 제거. Image/RawImage는 `GetComponentsInChildren<Component>(true)` + **타입 이름 기반 판정**(`IsComponentOfType`, 기반 타입을 거슬러 올라가 파생 클래스도 포함)으로 수집하고, 참조 에셋 이름은 `SerializedObject`로 `m_Sprite`/`m_Texture`를 읽음(`GetReferencedAssetName`). 기존 `AssetApplier`/`CandidateGenerator`와 동일한 패턴이라 신규 asmdef 불필요. `MCPTools.Editor.asmdef`는 원래 uGUI를 참조하지 않아 변경 없음. README 요구 사항에 "uGUI 선택 사항" 명시 |
| D4 | 설치 폴더 경로 **하드코딩 3곳** | `Assets/Plugins/MCPTools/` 등 다른 위치에 두면 ① [서버 시작]이 "스크립트를 찾을 수 없습니다"로 실패(3단계 전체 불능), ② 동봉한 `MCPToolSettings.asset`이 무시되고 `Assets/MCPTools/Editor/Common/`에 **빈 설정 에셋과 폴더가 새로 생성**됨(무성 이중화), ③ 추가 프롬프트 템플릿 폴더 미탐색 | (수정 전) `ComfyUIServerLauncher.cs:26, 44`, `Common/MCPToolSettings.cs:13, 67-69`, `PromptBuilder/PromptTemplate.cs:18` | **높음** | **대응완료** | `MCPToolSettings.InstallRoot`(static, 캐시) 신설 — ① `FindAssets("t:MCPToolSettings")` 경로 → ② `MonoScript.FromScriptableObject` 스크립트 경로 → ③ `"Assets/MCPTools"` 폴백 순으로 계산하며 `<루트>/Editor/Common` 형태를 검증. `AssetPath`·`PromptTemplate.TemplatesFolder`를 `const`에서 `static` 속성으로 전환해 `InstallRoot` 기반 계산. `GetOrCreate`는 `FindAssets`로 위치 무관 조회(중복 시 경고) 후 없을 때만 생성하며 `EnsureFolder`는 재귀 생성. `ComfyUIServerLauncher.GetScriptPath`는 `InstallRoot`의 선행 `Assets`를 `Application.dataPath`로 치환해 `Server~` 경로를 조합하고, 미발견 시 탐색 경로·설치 루트·`Server~` 동반 복사 확인 안내를 예외 메시지에 포함 |
| D5 | `StyleChange`/`UI` 워크플로 `image` 기본값이 **개발 PC ComfyUI input 폴더의 실제 파일명** | 파일 미지정 상태로 생성하면 ComfyUI가 해당 input 파일을 찾지 못해 실패. 기본값 자체가 배포에 무의미한 잔재 | `variables.json:36` (`ComfyUI_00050_.png`), `:45` (`3fcca473f7034277ea1a365775872584dbc98168c9ea1fdf6334ae8d9a2158cd.jpg`), `:46` (`astronaut-pixel-art-...jpg`), `workflows/UI.json`·`StyleChange.json`의 `LoadImage.image` | 중간 | 부분대응 (`variables.json:45` description에 "다른 PC에서는 지정이 사실상 필수" 안내) | `image` 타입 변수는 **미지정 상태를 기본값으로** 두고(빈 문자열), 생성 시 비어 있으면 "참조 이미지를 선택해주세요" 검증 오류로 차단. 워크플로 JSON의 잔재 파일명도 중립적인 이름으로 정리 |
| D6 | Windows에서 **브리지 포트 중복 바인딩이 감지되지 않음** | `http.server.HTTPServer.allow_reuse_address = 1`(확인: 로컬 Python 3.12에서 값 1) → Windows의 `SO_REUSEADDR`는 이미 리슨 중인 포트에도 바인딩을 허용. 두 번째 브리지가 **죽지 않고 뜨므로 조기 종료 감지가 발동하지 않고**, 요청이 두 서버로 비결정적으로 분배됨 (macOS/Linux는 `EADDRINUSE`로 죽어 감지가 정상 동작) | `bridge_server.py:38, 543` (`ThreadingHTTPServer(("127.0.0.1", args.port), ...)`), `ComfyUIServerLauncher.cs:281-286` | 중간 | 미대응 | `bridge_server.py`에서 `ThreadingHTTPServer.allow_reuse_address = False`로 명시하거나, 바인딩 전에 해당 포트로 `GET /health`를 시도해 **이미 브리지가 떠 있으면 즉시 안내 후 종료**. Unity 쪽은 [서버 시작] 직전에도 `/health`를 1회 확인 |
| D7 | 다중 Unity 프로젝트 동시 사용 시 **브리지 공유** | A 프로젝트가 띄운 브리지가 8189를 점유하면 B 프로젝트 창은 `/health` 성공으로 [서버 시작]이 비활성화되고 **A의 `Server~/workflows`·`variables.json`을 그대로 사용**. 버전이 다르면 워크플로/변수 불일치. `unloadModelsAfterBatch`의 `/free`도 서로 간섭 | `ComfyUIGeneratorWindow.cs:225-232` (`DisabledScope(_bridgeAlive)`), `bridge_server.py:40-42` (워크플로는 서버 자신의 폴더에서 로드), `:486-500` (`/free`) | 중간 | 부분대응 (`/health` 기반 중복 시작 방지) | `/health` 응답에 브리지 스크립트 경로·버전을 포함시키고, Unity가 **자기 설치 경로와 다르면 경고**를 표시. 또는 프로젝트별 포트 자동 할당(사용 중이면 +1 탐색) 옵션 제공 |
| D8 | 브리지 Job 타임아웃 **600초 하드코딩** | 저사양 GPU PC에서 후보 4장 생성이 600초를 넘으면 실패로 처리됨. 설정 `requestTimeoutSeconds`는 다운로드에만 적용되어 조정 불가 | `bridge_server.py:52` (`JOB_TIMEOUT_SEC = 600.0`), `:272-275` | 중간 | 미대응 | `--job-timeout` 인자를 추가하고 `MCPToolSettings`에 `jobTimeoutSeconds`(기본 600) 신설 → `ComfyUIServerLauncher.Start`에서 전달 |
| D9 | ComfyUI **최소 버전 미명시** | 워크플로가 `ReferenceLatent`(UI/StyleChange), `SaveAudioAdvanced`(Audio) 등 비교적 최신 코어 노드를 요구. 구버전 ComfyUI에서는 D2와 구분되지 않는 노드 누락 오류로 나타남 | 워크플로 `class_type` 전수, README "요구 사항" 절 (ComfyUI 버전 언급 없음) | 중간 | 미대응 | README에 검증된 ComfyUI 버전/커밋을 명시. D2의 preflight가 누락 노드를 코어/커스텀으로 구분해 안내 |
| D10 | macOS/Linux 에디터 **미검증** | 종료 경로는 `UNITY_EDITOR_WIN` 분기가 있으나(`taskkill` ↔ `Process.Kill`), ① `showBridgeConsole=true`일 때 `UseShellExecute=true`가 유닉스에서 콘솔 창을 여는지, ② `python3` 후보 탐지 결과의 실행 권한, ③ `Server~` 경로의 `~`가 셸을 거치지 않으므로 홈 디렉터리로 확장되지는 않음(문제 없음)에 대한 실기기 확인이 없음 | `ComfyUIServerLauncher.cs:312-330, 356-359, 388-421`, `Common/AiCliRunner.cs:102-159, 354-378`(Windows 분기 있음) | 중간 | 부분대응 | macOS 또는 Linux 에디터에서 1회 실기 검증. 유닉스에서는 `showBridgeConsole` 옵션을 숨기거나 항상 로그 파일 모드로 강제 |
| D11 | `bridgeServerUrl`의 **호스트가 무시됨** | 런처는 URL에서 포트만 추출하고 서버는 항상 `127.0.0.1`에 바인딩. 사용자가 LAN IP를 넣으면 Unity는 그 주소로 접속을 시도해 실패 | `ComfyUIServerLauncher.cs:240, 344-351`, `bridge_server.py:543` | 낮음 | 미대응 | 설정 툴팁/README에 "브리지는 로컬 전용(127.0.0.1)"임을 명시하거나, `--host` 인자를 추가해 URL의 호스트를 그대로 전달 |
| D12 | `Assets/Docs`가 **자동 생성되지 않음** | 새 프로젝트 첫 실행 시 1단계 기획서 드롭다운이 비어 있고 "Assets/Docs 폴더에 .md/.txt를 넣어주세요" 안내만 뜸. 사용자가 폴더를 직접 만들어야 함 (저장 시점에는 `Directory.CreateDirectory`로 생성됨) | `AssetListupWindow.cs:488, 536, 588`, `AssetListBuilder.cs:202-209`, `MCPToolSettings.cs:34` | 낮음 | 미대응 | 각 창 `OnEnable`에서 `docsRootPath`/`generatedRootPath`를 `AssetDatabase.CreateFolder`로 보장하거나, 안내 다이얼로그에 [폴더 생성] 버튼 추가 |
| D13 | 신규 폴더 생성 직후 `ImportAsset`만 호출 | `Directory.CreateDirectory` 후 `AssetDatabase.ImportAsset(path)`만 호출하므로, 폴더가 AssetDatabase에 없던 첫 저장에서는 산출물이 즉시 프로젝트 창에 보이지 않을 수 있음(다음 Refresh까지 지연). **확인 필요** — 정적 분석으로는 Unity의 실제 동작 단정 불가 | `AssetListBuilder.cs:202-209`, `PromptBuilder/PromptBuilder.cs:165-171` | 낮음 | 확인 필요 | 폴더를 새로 만든 경우 `AssetDatabase.Refresh()`를 함께 호출 |
| D14 | 배포본 설정 에셋에 **PC 고유 경로가 유입될 위험** | 현재 `MCPToolSettings.asset`의 값은 깨끗하지만(`pythonExecutable: python`), 개발자가 [Python 자동 탐지]를 누르면 `C:\Users\<이름>\...\python.exe` 절대 경로가 에셋에 기록되고, 패키징 스크립트에 초기화 단계가 없어 **그대로 배포될 수 있음** | `MCPToolSettings.asset:15-25`(현재 값 확인 — 이상 없음), `MCPSettingsWindow.cs:153-156`(절대 경로 기록), `tools/pack-mcptools.ps1:38-45, 69-73`(제외 패턴에 설정 초기화 없음) | 낮음 | 미대응 | 패키징 스크립트에 **설정 에셋 기본값 검증** 단계 추가 — `pythonExecutable`이 `python`이 아니거나 URL이 `127.0.0.1`이 아니면 경고/차단, 또는 스테이징 사본에서 기본값으로 강제 치환 |
| D15 | 배포 형식 서술 불일치 | 프로젝트 규칙 문서(`CLAUDE.md` "배포 고려사항")는 여전히 "`.unitypackage`로 내보내 넣는 것만으로 동작"이라고 기술. 실제로는 `Server~/`가 Export Package에 포함되지 않아 성립하지 않으며 README는 이미 zip 전용으로 경고 중 | `CLAUDE.md` 배포 고려사항 §자기완결성, `Assets/MCPTools/README.md:34-36`, `tools/pack-mcptools.ps1:1-14` | 낮음 | 부분대응 (README·패키징 스크립트는 정정 완료) | `CLAUDE.md`의 배포 규칙 문장을 zip 방식으로 정정 |
| D16 | `.meta` GUID 중복 가능성 | 같은 배포본을 **다른 경로에 중복 설치**하면 동일 GUID를 가진 에셋이 두 벌 생겨 Unity가 GUID 충돌을 보고하고 한쪽을 재생성 | `dist/MCPTools.zip`에 원본 `.meta` 포함(105개 파일) | 낮음 | 미대응 | README 설치 절차에 "**기존 `MCPTools` 폴더를 먼저 삭제한 뒤** 새 버전을 넣을 것"을 명시 |
| D18 | **브리지 제어 버튼 동시 잠김** — Unity 재시작 후 창에서 브리지를 내릴 방법이 없음 | 브리지가 살아 있는데 이 세션이 띄운 게 아니면 **[서버 시작]·[서버 종료]가 동시에 비활성**된다. 시작은 `_bridgeAlive`(HTTP `/health`)로 판정하고 종료는 `IsLaunchedProcessAlive()`(SessionState PID)로 판정하는데, **SessionState는 Unity 재시작 시 사라지므로** 브리지만 남고 PID는 없는 상태가 흔하게 생긴다. `Stop()`도 이 경우 "외부에서 직접 실행한 서버는 해당 콘솔 창에서 종료해주세요" 예외를 던지는데, `showBridgeConsole=false`(기본)면 콘솔 창 자체가 없어 안내대로 할 수도 없다. → 사용자는 작업 관리자로 프로세스를 직접 죽여야 한다 | `ComfyUIGeneratorWindow.cs:232`(`DisabledScope(_bridgeAlive)`), `:240`(`DisabledScope(!IsLaunchedProcessAlive())`), `ComfyUIServerLauncher.cs`의 `Stop()` PID 가드, `MCPToolSettings.showBridgeConsole` 기본 false | **높음** | 미대응 (2026-07-25 실사용 중 발견) | 브리지에 `POST /shutdown`을 추가해 **누가 띄웠든 HTTP로 종료**할 수 있게 한다. [서버 종료] 활성 조건을 `IsLaunchedProcessAlive() \|\| _bridgeAlive`로 넓히고, 이 세션이 띄운 게 아니면 종료 전에 `/health`의 `scriptPath`를 보여주고 확인받아 **다른 프로젝트의 브리지를 실수로 내리는 것을 막는다**(D7과 함께 해결). 로컬 바인딩 전제이므로 인증은 불필요하나, `--host`로 로컬 외 바인딩한 경우(D11)는 `/shutdown`을 거부하는 편이 안전 |
| D17 | 온보딩 문서 공백 | 다른 개발자가 zip만 받아 첫 생성까지 도달하기에 부족한 항목: ① uGUI 필수(D3) 미기재, ② ComfyUI 최소 버전(D9) 미기재, ③ 커스텀 노드 **설치 방법**(ComfyUI-Manager 등) 링크 없음(노드 이름만 있음), ④ `Assets/Docs` 준비 안내 없음(D12), ⑤ 설치→설정→ComfyUI 실행→[서버 시작]→1단계 순서를 한 곳에 모은 "빠른 시작" 절 없음 | `Assets/MCPTools/README.md:14-39` | 중간 | 부분대응 (Python·모델·패키지 안내는 상세함) | README 상단에 **"빠른 시작 (첫 실행 체크리스트)"** 절 신설 + 위 5개 항목 보강 |

### 확인 결과 이상 없음 (가설 검증 결과 문제가 아닌 항목)

| 항목 | 확인 내용 | 근거 |
|------|-----------|------|
| `Server~` + `.unitypackage` | Unity는 `~`로 끝나는 폴더를 임포트하지 않으므로 Export Package에 `bridge_server.py`·`workflows/`·`variables.json`이 **포함되지 않는다**. → `.unitypackage` 배포는 성립하지 않고 **zip 배포만 유효**. README·패키징 스크립트는 이미 이 결론을 반영해 zip 전용으로 경고 중이며, `dist/MCPTools.zip`에 3종이 모두 포함된 것을 확인 | `README.md:34-36`, `tools/pack-mcptools.ps1:1-14, 68-73`, `dist/MCPTools.zip` 목록에 `Server~/bridge_server.py`·`variables.json`·`workflows/*.json` 5종 존재 (총 105개 파일, `__pycache__` 제외됨). 남은 것은 `CLAUDE.md` 문구뿐 → D15 |
| unity-mcp 미설치 방어 | **방어되어 있음.** `MCPTools.Editor.asmdef`는 `MCPTools.Runtime`만 참조하고 unity-mcp를 참조하지 않으며, 패키지 의존 코드는 `MCPTools.Editor.McpForUnity.asmdef`(`defineConstraints: MCPTOOLS_HAS_MCPFORUNITY` + `versionDefines`)로 분리되어 **참조 해석 전에 어셈블리째 제외**됨. 소스에도 `#if MCPTOOLS_HAS_MCPFORUNITY` 이중 가드 | `Editor/MCPTools.Editor.asmdef:4-6, 16-22`, `Editor/McpForUnityBridge/MCPTools.Editor.McpForUnity.asmdef:6-7, 17-26`, `McpForUnityAdapter.cs:6, 544` |
| 2D Sprite 미설치 방어 | 동일 패턴으로 방어됨 (`MCPTools.Editor.SpriteSlicing.asmdef`, `defineConstraints: MCPTOOLS_HAS_2D_SPRITE`) | `Editor/SpriteSlicing/MCPTools.Editor.SpriteSlicing.asmdef:15-24` |
| 설정 에셋의 PC 고유 값 | 배포본 `MCPToolSettings.asset`의 모든 값이 기본값(`http://127.0.0.1:8188`/`8189`, `pythonExecutable: python`, `Assets/Generated`, `Assets/Docs`)으로 **PC·프로젝트 고유 값 없음**. 없을 때 `GetOrCreate`가 자동 생성 | `Editor/Common/MCPToolSettings.asset:15-25`, `MCPToolSettings.cs:59-89` |
| 절대 경로·드라이브 문자·사용자명 하드코딩 | 코드 전수 검색 결과 없음. Python 탐지도 `Environment.SpecialFolder`/환경변수 조합만 사용 | `ComfyUIServerLauncher.cs:402-421, 519-540` |
| 출력 폴더 자동 생성 (Generated) | `Candidates/{id}`, `Images`, `Audio`, `generatedRootPath` 모두 사용 직전 생성되고 `AssetDatabase.Refresh`/`ImportAsset` 호출됨 | `CandidateGenerator.cs:136, 201, 304, 308, 319, 473` |
| 경로 공백·비ASCII | 프로세스 인자에서 스크립트 경로·로그 경로가 모두 큰따옴표로 감싸짐. 로그 파일은 `encoding="utf-8"` 명시. AI CLI 실행은 stdin/stdout UTF-8 명시. 항목 id는 `Path.GetInvalidFileNameChars()`로 치환하므로 한글 id도 파일명으로 안전 | `ComfyUIServerLauncher.cs:243, 249`, `bridge_server.py:539`, `AiCliRunner.cs:288, 377-378, 403`, `CandidateGenerator.cs:524-537` |
| 네임스페이스·메뉴·어셈블리 이름 | 모든 코드가 `MCPTools.Editor`/`MCPTools.Runtime` 이하, 어셈블리 4종 모두 `MCPTools.*` 접두, 메뉴는 `Tools/MCP/*`로 통일 — 도입 프로젝트와 충돌 여지 낮음 | 각 asmdef, `MenuItem` 8개 (`Tools/MCP/1~4`, `Settings`, `Ping`, `Pipeline`, `Sprite Sheet`) |
| ComfyUI 원격/다른 포트 | 브리지는 `--comfy-url`을 그대로 사용하며 `/view` 프록시·`/upload`·`/free`가 모두 같은 URL 기준이므로 **원격 ComfyUI도 동작**. `/object_info` 응답 형식이 다르면 옵션 첨부만 생략되고 예외는 나지 않음(안전한 폴백) | `bridge_server.py:201-211, 391-402, 502-524, 140-196` |
| Unity 6 미만에서 없는 API | 정적 검색 범위에서 Unity 6 전용 API 사용은 발견되지 않음(`SessionState`/`PrefabUtility`/`EditorSceneManager`/`versionDefines`는 모두 2019 LTS 이상). 다만 `ISpriteEditorDataProvider` 경로가 2D Sprite 패키지에 의존하고 README가 6000.5.2f1을 명시하므로 **하위 버전 지원은 목표 아님**. 실제 하위 버전 컴파일은 **확인 필요** | `Editor/SpriteSlicing/SpriteSliceWriter.cs`, `README.md:16` |

## 5. 작업 항목

### 5.1 즉시 조치 (치명 / 높음)

1. **[D1] 모델 파일 사전 검증** — 브리지에 워크플로별 모델 필드 검증 추가(`/object_info`의 `CheckpointLoaderSimple`/`UNETLoader`/`LoraLoaderModelOnly`/`CLIPLoader`/`VAELoader` 선택지와 대조), Unity 3단계 창에서 생성 전 누락 목록을 다이얼로그로 표시하고 변수 UI에 경고색 적용.
   - 대상: `Server~/bridge_server.py`(검증 함수 + 응답 필드), `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`, `CandidateGenerator.cs`(생성 진입 시 검사)
2. **[D2] 커스텀 노드 preflight** — 워크플로 JSON의 `class_type` 집합 ∖ `/object_info` 키 = 누락 노드. `GET /workflows` 응답에 `missingNodes`를 추가하고 창 상단에 "필요한 커스텀 노드 N개 누락: InspyrenetRembg, ComfySwitchNode" + 설치 안내(ComfyUI-Manager) 표시.
   - 대상: `Server~/bridge_server.py`, `Editor/ComfyUIGenerator/BridgeClient.cs`(응답 모델), `ComfyUIGeneratorWindow.cs`, `README.md`
3. ~~**[D3] uGUI 의존 분리 또는 명시**~~ — **완료.** 별도 asmdef 분리 대신, 이미 코드베이스에서 쓰던 **타입 이름 판정 + SerializedProperty 조회** 패턴(`AssetApplier`/`CandidateGenerator`)을 `ProjectScanner`에도 적용해 uGUI 참조 자체를 제거했다. uGUI는 **선택 사항**이 되었고 미설치 시 Image/RawImage 슬롯만 수집되지 않는다.
   - 대상: `Assets/MCPTools/README.md`, `Editor/AssetListup/ProjectScanner.cs` (신규 asmdef 불필요)
4. ~~**[D4] 설치 경로 독립화**~~ — **완료.** `MCPToolSettings.InstallRoot`로 설치 루트를 런타임 계산하고 `AssetPath`·`TemplatesFolder`·`GetScriptPath`가 모두 이를 기준으로 동작한다. `GetOrCreate`는 `FindAssets` 우선 조회 후 없을 때만 생성.
   - 대상: `Common/MCPToolSettings.cs`, `ComfyUIGenerator/ComfyUIServerLauncher.cs`, `PromptBuilder/PromptTemplate.cs`, `Assets/MCPTools/README.md`

### 5.2 후속 (중간)

5. **[D5]** `image` 타입 변수 기본값을 빈 값으로 바꾸고, 미지정 시 생성 차단 + 안내. 워크플로 JSON의 잔재 파일명 정리. (`variables.json:36,45,46`, `workflows/UI.json`, `workflows/StyleChange.json`)
6. **[D6]** `bridge_server.py`에 `allow_reuse_address = False` 명시 + 바인딩 실패 시 한국어 안내, Unity는 [서버 시작] 직전 `/health` 재확인.
7. **[D7]** `/health` 응답에 `scriptPath`·`version` 추가, Unity가 자기 설치 경로와 다르면 경고 표시.
8. **[D8]** `--job-timeout` 인자 + `MCPToolSettings.jobTimeoutSeconds`(기본 600) 신설.
9. **[D9]** README에 검증된 ComfyUI 버전 명시, preflight가 코어/커스텀 노드를 구분해 안내.
10. **[D10]** macOS 또는 Linux 에디터에서 [서버 시작]/[서버 종료] 1회 실기 검증, 유닉스에서 `showBridgeConsole` 처리 정리.
11. **[D17]** README에 "빠른 시작 (첫 실행 체크리스트)" 절 신설 + uGUI/ComfyUI 버전/커스텀 노드 설치 링크/`Assets/Docs` 준비 보강.

### 5.3 선택 (낮음)

12. **[D11]** 브리지 `--host` 인자 추가 또는 "로컬 전용" 문구 명시.
13. **[D12]** 창 진입 시 `docsRootPath`/`generatedRootPath` 폴더 자동 생성.
14. **[D13]** 신규 폴더 생성 경로에 `AssetDatabase.Refresh()` 추가 (실동작 확인 후).
15. **[D14]** (§9.0으로 형태 변경) 설정 에셋에 개인 값([Python 자동 탐지]의 절대 경로 등)이 들어간 채 git에 커밋되지 않게 커밋 전 기본값 확인. U2로 설정 에셋이 `Assets/` 쪽으로 분리되면 패키지에서 아예 제외.
16. **[D15]** `CLAUDE.md` 배포 규칙 문구를 **UPM(git URL) 방식**으로 정정 (zip 문구로의 정정은 §9.0으로 무효).
17. ~~**[D16]**~~ 폐기 — U5로 흡수 (§9.0).

## 6. 검증 방법 (재현 시나리오)

**A. 새 빈 Unity 6 프로젝트 (같은 PC에서도 가능)**

1. 빈 Unity 6 프로젝트에 `MCPTools/` 폴더를 복사해 `Assets/MCPTools/`로 넣기 → 컴파일 오류 0, `Tools/MCP/*` 메뉴 8개 노출. (zip 배포는 중단됐지만 개발 프로젝트 자체가 `Assets/` 설치 형태이므로 이 시나리오는 유지 — 배포 본선은 §9.5 시나리오 E)
2. unity-mcp 패키지 **없이** 위 1을 수행 → 컴파일 오류 0, 에디터 창 4개 정상 (D3 수정 전에도 통과해야 함).
3. `com.unity.2d.sprite` **제거** 후 → 컴파일 오류 0, 스프라이트 슬라이싱만 설치 안내.
4. **`com.unity.ugui` 제거** 후 → **현재는 컴파일 실패 예상(D3)**. 수정 후 컴파일 오류 0 + 1단계 스캔이 안내 메시지와 함께 비활성.
5. `Assets/Plugins/MCPTools/`로 위치를 바꿔 설치 → [서버 시작] 동작, `Assets/MCPTools/`가 새로 생기지 않음 (D4).
6. `Assets/Docs`가 없는 상태로 1단계 창 열기 → 폴더 자동 생성 또는 [폴더 생성] 안내 (D12).

**B. ComfyUI 환경 차이**

7. 모델을 하나도 설치하지 않은 ComfyUI에 연결 → 생성 전에 **누락 모델 목록 다이얼로그**가 뜨고 ComfyUI 원문 400이 그대로 노출되지 않음 (D1).
8. `ComfyUI-Inspyrenet-Rembg` / `ComfySwitchNode`를 제거한 ComfyUI에 연결 → 워크플로 선택 시 **누락 노드 경고** 표시 (D2).
9. `StyleChange`/`UI`에서 참조 이미지를 지정하지 않고 [생성] → "참조 이미지를 선택해주세요"로 차단 (D5).
10. ComfyUI를 다른 PC(원격 IP)에 두고 `comfyUIServerUrl` 변경 → 생성·업로드·다운로드 정상.

**C. 프로세스/포트**

11. 브리지가 이미 8189에서 실행 중인 상태로 다른 Unity 프로젝트에서 [서버 시작] → **중복 바인딩 없이** 명확한 안내 (D6/D7).
12. 8189를 다른 프로그램이 점유한 상태로 [서버 시작] → 조기 종료 감지 메시지 표시 (선행 조치 회귀 확인).
13. 후보 4장 생성이 600초를 넘는 저사양 조건에서 타임아웃 설정을 늘려 성공 (D8).

**D. 경로/환경**

14. 사용자명에 **한글·공백**이 포함된 계정에서 Unity 프로젝트를 열고 [서버 시작] → 정상 기동, 로그 파일 기록 정상.
15. macOS 또는 Linux 에디터에서 [서버 시작]/[서버 종료] (D10).
16. README만 보고 제3자가 git URL 설치 → ComfyUI 준비 → 첫 생성까지 도달 가능한지 리뷰 (D17/U9).

## 7. 산출물

- 수정된 `Assets/MCPTools/` (경로 독립화, preflight, uGUI 분리, 기본값 정리)
- 갱신된 `Assets/MCPTools/README.md` (빠른 시작 + 요구 사항 보강 + git URL 설치 절)
- **UPM 배포 산출물** — `package.json`, `CHANGELOG.md`, `LICENSE.md`, `Documentation~/`, 저장소 루트 `.gitignore`·`.gitattributes`, 릴리스 태그 절차 문서 (§9.4)
- ~~`tools/pack-mcptools.ps1` 갱신·`dist/MCPTools.zip` 재패키징~~ — zip 배포 중단으로 폐기 (§9.0)

## 8. 완료 조건

- 체크리스트: [Task7_체크리스트.md](../checklist/Task7_체크리스트.md)
- 5.1(치명/높음) 항목 전부 구현 + §6 시나리오 A·B 통과
- 9.3(치명/높음) 항목 전부 구현 + §9.5 시나리오 E 통과
- 사용자 에디터 테스트 통과

---

## 9. GitHub + Package Manager(UPM) 배포 전환 시 추가 문제 (U)

### 9.-1 진행 상태 (2026-07-24 구현 착수분)

**구현 28/30 완료** — D1~D9·D11~D15·D17, U1~U12·U14 (정적/스모크 검증까지). 상세는 [체크리스트](../checklist/Task7_체크리스트.md).

남은 것 2개:
- **U13** (`.meta` 커밋 검증) — Unity를 한 번 연 뒤 첫 커밋 시 수행.
- **D10** (macOS/Linux) — 장비 확보 후로 보류. 추측 분기를 넣지 않고 실기 검증 후 결정한다. Windows 배포에는 영향 없음.

전체가 **Unity 에디터 컴파일·실동작 확인 대기** 상태다 (에디터를 실행할 수 없어 정적 검증과 브리지 스모크 테스트까지만 수행).

> **첫 커밋 전 필수:** 이번에 새로 만든 `package.json`·`CHANGELOG.md`·`LICENSE.md`·`Editor/Common/MCPToolFolders.cs`에는 아직 `.meta`가 없다(Unity가 생성하는 것이 원칙). **Unity 에디터를 한 번 연 뒤** 커밋해야 사용자 프로젝트에서 GUID가 깨지지 않는다. 절차는 [릴리스절차.md](../릴리스절차.md).

확정 사항:
- 패키지 이름 **`com.sungchan.mcptools`** (변경 시 사용자 재설치 필요), 버전 `0.1.0`, 라이선스 **MIT**.
- 저장소 **`github.com/chomul/MCPTools`** (공개), 레이아웃은 **§9.4 A안(모노 저장소)**. 저장소 루트 `git init -b main` 완료.
  - `MCPToolTest/` 안에 있던 중첩 저장소(원격 `chomul/MCPToolTest`, 커밋 1개)는 `.mcptooltest-git-backup/`으로 이동 — 방치하면 MCPToolTest 전체가 gitlink로 취급되어 커밋에서 누락된다.
- 설치 URL **`https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.1.0`**
- 사용자 데이터 폴더 규약 **`Assets/MCPTools.User/`** — 설정 에셋, 프롬프트 템플릿, 워크플로/변수 사본이 모두 여기로 간다.

설계 결정 변경:
- **U5** — 에디터 중복 설치 감지 코드는 만들지 않는다. 구 설치본과 패키지가 공존하면 asmdef 이름 중복으로 **컴파일 자체가 실패**해 감지 코드가 실행될 수 없다. README 경고가 유일한 실효 조치.
- **U14** — `Samples~/`와 `Documentation~/`는 만들지 않는다. U6의 [워크플로를 프로젝트로 복사] 버튼이 같은 목적을 달성하고, `documentationUrl`이 README를 직접 가리키므로 문서를 이중 관리할 이유가 없다.
- **D9** — preflight의 코어/커스텀 노드 자동 구분은 구현하지 않는다. ComfyUI가 둘을 구분해 주지 않아 브리지가 코어 목록을 알 방법이 없다. README의 판단 방법 안내로 대체.

### 9.0 zip 배포 중단 결정 (2026-07-24)

배포 채널은 **GitHub + UPM(git URL) 단일화**로 확정. zip 전제였던 항목은 다음과 같이 조정한다.

- **D14 형태 변경** — "패키징 시 설정 에셋 검증" → "**설정 에셋을 개인 값이 들어간 채 git에 커밋하지 않기**". [Python 자동 탐지]가 절대 경로를 에셋에 기록하는 위험은 그대로 유효하며, 커밋 전 기본값 확인(또는 U2로 설정 에셋이 `Assets/` 쪽으로 나가면 패키지에서 아예 제외)으로 대응.
- **D16 폐기** — zip 재설치 시 GUID 중복 문제는 U5(zip 설치본 + 패키지 공존)로 흡수.
- **`tools/pack-mcptools.ps1`·`dist/` 폐기** — 유지보수 중단. U12의 `.gitignore`에서 `dist/` 제외 유지.
- **U8 축소** — "태그 커밋에서 zip을 만들어 Release 자산 첨부" 삭제. semver + 태그 + CHANGELOG만 유지. `Runtime/Data/MCPToolsInfo.cs:9`의 `Version` 상수와 `package.json`의 `version`을 릴리스 시 **함께** 올린다.
- **U9 축소** — "zip 설치 경로 병기" 삭제. git 미설치·사설 저장소 인증 안내만 유지.
- **README 설치 절 전면 교체 필요** — 현재 설치 절은 zip 전용 서술("zip을 풀어 `Assets/`에", ".unitypackage 금지" 경고, "설치 위치 자유", 설정 에셋이 설치 루트에 생성됨)이라 git URL 설치 + U2의 새 설정 에셋 위치 기준으로 다시 쓴다 (U9에 포함).

### 9.1 무엇이 달라지는가

| 구분 | 현재 (zip → `Assets/MCPTools/`) | 전환 후 (git URL → UPM 패키지) |
|------|--------------------------------|-------------------------------|
| 설치 위치 | `Assets/` 아래 임의 폴더 | `Library/PackageCache/<name>@<해시>/` (git 설치) 또는 `Packages/<name>/` (embed) |
| 에셋 경로 접두 | `Assets/...` | **`Packages/<name>/...`** — `Application.dataPath` 기반 절대 경로 조합이 성립하지 않음 |
| 쓰기 가능 여부 | 자유 (사용자가 파일 추가·수정 가능) | **읽기 전용(immutable).** 패키지 폴더에 에셋 생성·수정 불가, 강제로 수정해도 재해결(resolve) 시 소실 |
| `Server~` | zip에 그대로 포함, Unity가 임포트만 안 함 | git clone에 포함되어 **디스크에는 존재**하나 AssetDatabase로는 조회 불가 → 절대 경로 해석 방식이 바뀜 |
| 갱신 | 사용자가 폴더 교체 | Package Manager의 버전/태그 선택 |

핵심은 **① 경로 접두가 `Assets`가 아니다, ② 설치 폴더에 쓸 수 없다** 두 가지이며, D4에서 만든 `InstallRoot` 구조가 여기서 다시 깨진다.

### 9.2 감사 결과 — 문제 목록 (심각도 순)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 현재 상태 | 대응 방안 |
|---|------|------|----------------|--------|-----------|-----------|
| U1 | **`GetScriptPath()`가 `Packages/` 경로를 처리하지 못함** | `InstallRoot`가 `Packages/com.x.mcptools`가 되면 `Assets/` 접두 분기에 걸리지 않아 `underAssets = root` 폴백이 타고, 결과가 `<프로젝트>/Assets/Packages/com.x.mcptools/Editor/ComfyUIGenerator/Server~/bridge_server.py`라는 **존재하지 않는 경로**가 된다 → [서버 시작] 100% 실패, **3단계(생성) 전체 불능**. D4에서 넣은 "Server~ 동반 복사 확인" 안내가 오히려 오진을 유도 | `ComfyUIServerLauncher.cs:45-67` (`root.StartsWith("Assets/")` 아니면 `underAssets = root`), `:26` (`ScriptRelativePath`), `:251-254` (오진 안내) | **치명** | 미대응 | `Application.dataPath` 문자열 조합을 버리고 **`Path.GetFullPath(MCPToolSettings.InstallRoot)`** 로 해석(에디터에서 `Packages/...` 가상 경로를 실제 경로로 변환). 더 확실히 하려면 `UnityEditor.PackageManager.PackageInfo.FindForAssetPath(InstallRoot + "/package.json")?.resolvedPath` 우선 사용 후 `GetFullPath` 폴백. `Assets`/`Packages` 양쪽 + embed(`Packages/<name>/`)·PackageCache 모두에서 동작해야 함 |
| U2 | **읽기 전용 패키지에 설정 에셋을 생성하려고 함** | `GetOrCreate()`가 `AssetPath`(= `InstallRoot + "/Editor/Common/MCPToolSettings.asset"`)에 `CreateFolder`/`CreateAsset`을 호출. 대상이 `Packages/...`면 immutable 패키지라 **생성 실패 또는 재해결 시 소실** → 창을 열 때마다 오류가 반복되고 설정이 저장되지 않음. 설정 창에서 값을 바꿔도 유지 불가 | `MCPToolSettings.cs:98-134` (`EnsureFolder` → `AssetDatabase.CreateAsset(settings, assetPath)`), `:48-51` (`AssetPath`가 `InstallRoot` 기준) | **치명** | 미대응 | **설정 에셋 저장 위치를 설치 루트에서 분리**한다. 저장은 항상 프로젝트 쪽(`Assets/MCPTools.Settings/MCPToolSettings.asset` 등 상수 경로, 없으면 생성)으로 하고, 패키지에 동봉한 기본 설정은 **읽기 전용 기본값 원본**으로만 쓴다(최초 1회 `Assets/`로 복사). 사용자에게 위치를 알리는 로그 1회 출력 |
| U3 | `FindAssets`가 **`Packages/`까지 검색**해 동봉 설정이 사용자 설정을 가릴 수 있음 | `AssetDatabase.FindAssets("t:MCPToolSettings")`는 `searchInFolders` 미지정 시 `Assets`와 `Packages`를 모두 뒤진다. 패키지에 설정 에셋을 동봉하면 사용자가 `Assets/`에 만든 설정과 **2개가 잡히고 반환 순서에 따라 읽기 전용 쪽이 선택**될 수 있음(현재 코드는 `guids[0]`을 사용). 이 경우 사용자가 바꾼 ComfyUI 주소·Python 경로가 무시되고, 경고만 반복 출력 | `MCPToolSettings.cs:100-123` (`guids[0]` 채택 + 중복 경고), `:143-150` (`ResolveInstallRoot`도 동일 검색) | **높음** | 미대응 | 설정 조회는 **`FindAssets(..., new[]{"Assets"})`로 범위를 한정**하고, 없을 때만 패키지 기본값을 복사 생성. `ResolveInstallRoot`는 설정 에셋이 아니라 **스크립트 파일 위치(`MonoScript`)를 1순위**로 쓰도록 순서를 뒤집는다(U2로 설정 에셋이 설치 루트 밖으로 나가므로 역산 근거로 부적합해짐) |
| U4 | **패키지 매니페스트·저장소 레이아웃 부재** — 현재 상태로는 설치 자체가 불가 | `Assets/MCPTools/`에 `package.json`이 없고(현재 파일: `README.md`, `Editor/`, `Runtime/`뿐), 저장소 루트에는 Unity 프로젝트(`MCPToolTest/`)·작업 문서(`docs/`, `mds/`)·`ComfyUI/`·`dist/`가 섞여 있다. UPM git 설치는 **① 저장소 루트가 패키지**이거나 **② `?path=<서브폴더>`** 둘 중 하나여야 하며, 매니페스트가 없으면 "Cannot find package.json"으로 실패 | `MCPToolTest/Assets/MCPTools/` 파일 목록(=`README.md`/`Editor`/`Runtime`, `package.json` 없음), 저장소 루트 목록(`CLAUDE.md`, `ComfyUI/`, `dist/`, `docs/`, `mds/`, `tools/`, `MCPToolTest/`) | **높음** | 미대응 | `Assets/MCPTools/package.json` 신설 (`name: com.<조직>.mcptools`, `version`, `displayName`, `description`, `unity: "6000.5"`, `author`, `documentationUrl`, `dependencies: {}`). 설치 URL은 **`https://github.com/<user>/<repo>.git?path=MCPToolTest/Assets/MCPTools#v1.0.0`** 형식. 장기적으로는 패키지 전용 저장소 분리를 검토(§9.4) |
| U5 | **zip 설치본과 패키지가 동시에 존재하면 컴파일 실패** | 기존 사용자가 `Assets/MCPTools/`를 지우지 않고 패키지를 추가하면 ① **asmdef 이름 중복**(`MCPTools.Editor` 등 4종이 두 벌) → "Assembly with name 'MCPTools.Editor' already exists" 컴파일 오류, ② 동일 `.meta` GUID 두 벌 → GUID 충돌, ③ 설정 에셋 2개(U3). D16(같은 zip을 두 경로에 설치)의 **훨씬 발생 확률 높은 변형** | `Editor/MCPTools.Editor.asmdef:2`, `Editor/McpForUnityBridge/MCPTools.Editor.McpForUnity.asmdef:2`, `Editor/SpriteSlicing/MCPTools.Editor.SpriteSlicing.asmdef`, `Runtime/MCPTools.Runtime.asmdef` | **높음** | 미대응 | README 설치 절차 최상단에 **"패키지로 전환할 때는 `Assets/` 아래 기존 `MCPTools` 폴더를 먼저 삭제"**를 굵게 명시. 추가로 에디터 진입 시 `Assets/` 하위에 중복 설치본이 있으면 감지해 경고 다이얼로그 표시(설정 에셋 중복 경고와 동일 지점에서 처리 가능) |
| U6 | **사용자 확장 지점이 전부 읽기 전용 폴더 안에 있음** | ① 프롬프트 템플릿(`<루트>/Editor/PromptBuilder/Templates/*.json`) — 사용자가 파일을 **추가**하는 것이 사용법인데(체크리스트에 테스트 항목으로 존재) 패키지에는 추가 불가. 현재 이 폴더는 아직 존재하지도 않음, ② 워크플로 JSON·`variables.json` — 모델 파일명을 바꾸려면 편집이 필요한데(D1 안내가 "변수 UI에서 바꾸면 됨"이라 일부 완화됨) 워크플로 추가·수정은 불가, ③ 수정해도 패키지 재해결/버전 변경 시 **경고 없이 소실** | `PromptTemplate.cs:21-23` (`InstallRoot + "/Editor/PromptBuilder/Templates"`), `:68, 95-97` (`Directory.Exists`/`GetFiles`로 직접 조회), `Editor/PromptBuilder/` 실제 목록에 `Templates/` 없음, `bridge_server.py:40-42` (`BASE_DIR = dirname(__file__)` → `WORKFLOWS_DIR`·`VARIABLES_PATH`가 패키지 내부 고정) | **높음** | 미대응 | **패키지 기본값 + 프로젝트 오버라이드 2단 탐색**으로 전환: 템플릿은 `<설치 루트>/…/Templates`(기본)와 `Assets/MCPTools.User/Templates`(사용자, 없으면 안내 후 생성) 두 곳을 병합해 목록화하고 이름 충돌 시 사용자 쪽 우선. 워크플로/`variables.json`은 브리지에 `--workflows-dir` 인자를 추가해 **프로젝트 쪽 폴더를 우선 탐색**하게 하고, 설정 창에 [워크플로를 프로젝트로 복사] 버튼 제공. Package Manager의 `Samples~` 규칙을 써서 UI에서 `Assets/`로 Import하게 하는 방법도 대안 |
| U7 | 선택 의존 패키지를 `dependencies`에 **넣을 수도, 안 넣을 수도 없음** | UPM `dependencies`는 **레지스트리 패키지 버전만** 허용하고 git URL은 지원하지 않으므로 `com.coplaydev.unity-mcp`(git 설치)는 선언 불가. 반대로 `com.unity.ugui`·`com.unity.2d.sprite`는 선언하면 **선택 사항이 아니라 강제 설치**가 되어 D3에서 확보한 "uGUI 없이도 동작" 성질이 무의미해짐 | `package.json` 미존재(신설 대상), `MCPTools.Editor.asmdef:16-22`·`MCPTools.Editor.McpForUnity.asmdef:17-26`(`versionDefines`/`defineConstraints`로 이미 선택 의존 처리), README "요구 사항" 절 | 중간 | 부분대응 (asmdef 격리는 완료) | `dependencies`는 **비워 둔다.** 선택 의존은 지금처럼 `versionDefines` + `defineConstraints`로 처리하고, README와 `package.json`의 `description`에 "uGUI/2D Sprite/unity-mcp는 선택 사항"을 명시. unity-mcp는 git URL 설치 안내만 문서로 제공 |
| U8 | **버전 태그·CHANGELOG·릴리스 절차 부재** | git URL에 `#태그`를 붙이지 않으면 사용자는 항상 기본 브랜치 HEAD를 받게 되어 **재현 가능한 설치가 불가**하고, 팀원마다 다른 버전을 쓰게 된다. `package.json`의 `version`과 태그가 어긋나면 Package Manager UI가 잘못된 버전을 표시하며, 사용자가 "업데이트 가능" 여부를 판단할 근거가 없음 | 저장소가 아직 git 저장소가 아님(초기화 전), `CHANGELOG.md`·`LICENSE.md` 없음 | 중간 | 미대응 | semver 채택 + `package.json` `version`과 `vX.Y.Z` 태그를 **항상 함께 올리는 릴리스 절차**를 `docs/`에 문서화. `CHANGELOG.md`(Keep a Changelog 형식)·`LICENSE.md` 추가. 태그를 붙인 커밋에서만 zip을 만들어 GitHub Release 자산으로 첨부(zip 배포와 UPM 배포의 버전 일치) |
| U9 | 사용자 PC에 **git 실행 파일이 필요**하고 사설 저장소는 인증이 필요 | git URL 설치는 UPM이 사용자 머신의 `git`(2.14+)을 직접 호출하므로 미설치 시 "Unable to add package ... git executable not found"로 실패한다. 비개발자 아티스트 PC에서 흔한 실패. 사설/사내 저장소면 HTTPS PAT 또는 SSH 키 설정이 추가로 필요하고, `?path=` 서브폴더 지정은 Unity 2019.3.4+ 에서만 동작(현재 대상 Unity 6이므로 문제 없음) | Unity UPM git 지원 요건, README "설치" 절(zip 절차만 존재) | 중간 | 미대응 | README 설치 절에 **git URL 설치 / zip 설치 두 경로**를 나란히 쓰고, git 미설치 시의 오류 메시지와 조치(설치 링크, Unity 재시작)를 함께 안내. 사설 저장소일 경우 인증 설정 안내 추가 |
| U10 | Python이 **읽기 전용 패키지 폴더에 `__pycache__`를 만들려 시도** | `bridge_server.py`를 실행하면 CPython이 같은 폴더에 `__pycache__`를 쓰려 한다. PackageCache가 읽기 전용이면 조용히 건너뛰지만(치명적이지 않음), 쓰기가 되는 embed 설치(`Packages/<name>/`)에서는 **패키지 폴더가 오염**되어 git status가 더러워지고 재해결 시 경고가 날 수 있다. 현재 패키징 스크립트가 `__pycache__`를 제외 대상으로 둔 것과 같은 문제 | `ComfyUIServerLauncher.cs:243-249`(스크립트 절대 경로로 직접 실행), `tools/pack-mcptools.ps1:38-45`(`__pycache__` 제외 패턴) | 중간 | 미대응 | 브리지 실행 인자에 **`-B`** 를 추가하거나 `PYTHONDONTWRITEBYTECODE=1` 환경 변수를 세팅해 바이트코드 생성을 끈다. `.gitignore`에도 `__pycache__/` 추가 |
| U11 | **Windows 경로 길이(MAX_PATH) 초과 위험** | PackageCache 경로는 `Library/PackageCache/com.x.mcptools@<40자 커밋 해시>/`처럼 길어지고, 여기에 `Editor/ComfyUIGenerator/Server~/workflows/GenerateImageFlux.json`(59자)이 더해진다. 프로젝트가 깊은 경로(`C:\Users\<한글이름>\Documents\Unity Projects\...`)에 있으면 260자를 넘겨 **파일 열기 실패**가 날 수 있고, 이는 zip 설치(`Assets/MCPTools/...`)보다 훨씬 쉽게 발생 | `bridge_server.py:40-42, 59, 74-78`(경로 조합 후 `open`), `ComfyUIServerLauncher.cs:243`(절대 경로를 프로세스 인자로 전달) | 중간 | 미대응 | 실기 확인 필요. 파일 열기 실패 시 "경로가 너무 깁니다 — 프로젝트를 더 짧은 경로로 옮기거나 Windows 긴 경로 지원을 켜세요" 안내를 브리지·런처 양쪽 오류 메시지에 추가. 내부 폴더 깊이를 줄이는 것도 검토 |
| U12 | **저장소 위생 파일 부재** (`.gitignore`/`.gitattributes`) | 현재 저장소는 git 초기화 전이며 무시 규칙이 없다. 그대로 커밋하면 ① `MCPToolTest/`가 **2.2GB**(대부분 `Library/`·`Temp/`)라 clone이 사실상 불가능해지고(UPM git 설치는 `?path=`를 써도 **저장소 전체를 clone**한다), ② `dist/*.zip`(228KB)·`ComfyUI/`가 함께 받아지며, ③ `.meta`·`.asset`·`.asmdef` 같은 Unity YAML의 개행이 CRLF/LF로 뒤섞여 diff가 오염된다 | 저장소 루트에 `.gitignore`·`.gitattributes` 없음, `du -sh` 측정: `MCPToolTest` 2.2G / `dist` 228K / `docs` 268K / `ComfyUI` 32K (5MB 초과 단일 파일은 없음) | 중간 | 미대응 | Unity 표준 `.gitignore` + `dist/`·`__pycache__/` 추가. `.gitattributes`에 `* text=auto eol=lf`와 Unity YAML(`*.meta`, `*.asset`, `*.prefab`, `*.unity`, `*.asmdef`) 텍스트 지정, 바이너리 에셋은 LFS 검토. `dist/`의 zip은 커밋 대신 **GitHub Release 자산**으로 올림 |
| U13 | `.meta` 파일이 **반드시 커밋되어야 함** (패키지 에셋도 GUID 참조) | 패키지 내 스크립트·에셋도 GUID로 참조되므로 `.meta`가 빠지면 사용자 프로젝트마다 GUID가 새로 생성되어 asmdef 참조·설정 에셋 참조가 깨진다. 일반적인 라이브러리 저장소 감각으로 `.meta`를 무시 목록에 넣으면 바로 사고 | `Assets/MCPTools/` 전 파일에 `.meta` 존재(현재 정상), `.gitignore` 미존재 | 낮음 | 확인 필요 | `.gitignore`에 `*.meta`를 **절대 넣지 않는다**는 주석을 명시. 첫 커밋 후 `git ls-files | grep -c meta`로 개수 검증(현재 배포본 기준 `.cs`/폴더 수와 일치해야 함) |
| U14 | Package Manager UI에 **문서·샘플이 노출되지 않음** | 패키지 루트의 `README.md`는 Package Manager 창에 표시되지 않는다. UPM은 `Documentation~/index.md` 또는 `package.json`의 `documentationUrl`을 사용하며, 예제 워크플로/템플릿은 `Samples~/` 규칙을 따라야 UI의 [Import] 버튼이 생긴다. 지금 구조로는 사용자가 설치 직후 **아무 안내도 못 본다** | `Assets/MCPTools/README.md`(패키지 루트), `Documentation~/`·`Samples~/` 없음 | 낮음 | 미대응 | `Documentation~/index.md`를 두거나 `documentationUrl`을 GitHub README로 지정. U6의 사용자 오버라이드 자산(기본 프롬프트 템플릿, 워크플로 사본)을 `Samples~/`로 제공하면 U6 대응과 한 번에 해결 |

### 9.3 작업 항목 (UPM)

**즉시 조치 (치명 / 높음)**

18. **[U1] 설치 루트 절대 경로 해석 교체** — `GetScriptPath()`의 `Application.dataPath` 문자열 조합을 `PackageInfo.FindForAssetPath(...).resolvedPath` → `Path.GetFullPath(InstallRoot)` 순서로 대체하고, 실패 시 오류 메시지에 **해석된 절대 경로와 설치 형태(Assets / Packages embed / PackageCache)** 를 포함시킨다.
    - 대상: `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs:45-67, 245-256`
19. **[U2] 설정 에셋 저장 위치를 설치 루트에서 분리** — 항상 `Assets/` 하위 고정 경로에 생성·저장하고, 패키지 동봉 설정은 최초 1회 복사 원본으로만 사용. 생성 위치를 콘솔에 1회 안내.
    - 대상: `Editor/Common/MCPToolSettings.cs:48-51, 98-134`, `Editor/Common/MCPSettingsWindow.cs`
20. **[U3] 설정 조회 범위 한정 + 루트 역산 순서 변경** — `FindAssets`에 `new[]{"Assets"}` 지정, `ResolveInstallRoot`는 `MonoScript` 경로를 1순위로.
    - 대상: `Editor/Common/MCPToolSettings.cs:100-123, 140-176`
21. **[U4] `package.json` 신설 + 저장소 레이아웃 확정** — 매니페스트 작성, 설치 URL(`?path=…#태그`) 확정, README에 반영.
    - 대상: `Assets/MCPTools/package.json`(신규), `Assets/MCPTools/README.md`
22. **[U5] 중복 설치 감지·안내** — `Assets/` 하위에 구 설치본이 있으면 경고. README 설치 절차 최상단에 삭제 안내 명시.
    - 대상: `Editor/Common/MCPToolSettings.cs`(중복 경고 지점), `Assets/MCPTools/README.md`
23. **[U6] 사용자 오버라이드 경로 신설** — 템플릿 2단 탐색(패키지 기본 + `Assets/` 사용자), 브리지 `--workflows-dir` 인자, [워크플로를 프로젝트로 복사] 버튼.
    - 대상: `Editor/PromptBuilder/PromptTemplate.cs:21-23, 60-100`, `Server~/bridge_server.py:40-42, 526-545`, `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`, `Editor/Common/MCPSettingsWindow.cs`

**후속 (중간)**

24. **[U7]** `package.json`의 `dependencies`를 빈 객체로 두고 선택 의존을 문서·`versionDefines`로만 처리.
25. **[U8]** semver + `vX.Y.Z` 태그 릴리스 절차 문서화, `CHANGELOG.md`·`LICENSE.md` 추가, Release 자산으로 zip 첨부.
26. **[U9]** README에 git URL 설치 경로 추가 + git 미설치·인증 실패 안내.
27. **[U10]** 브리지 실행에 `-B`(또는 `PYTHONDONTWRITEBYTECODE=1`) 적용.
28. **[U11]** 긴 경로 실패 시 원인·조치 메시지 추가 (실기 확인 후).
29. **[U12]** 저장소 루트에 Unity 표준 `.gitignore` + `.gitattributes` 추가, `dist/` 커밋 제외.

**선택 (낮음)**

30. **[U13]** `.meta` 커밋 여부 검증 절차를 릴리스 체크리스트에 포함.
31. **[U14]** `Documentation~/index.md` 또는 `documentationUrl` 지정, 사용자 자산을 `Samples~/`로 제공.

### 9.4 저장소 레이아웃 — 두 가지 선택지

| 안 | 구조 | 설치 URL | 장점 | 단점 |
|----|------|----------|------|------|
| **A. 모노 저장소 (권장 — 초기)** | 지금 구조 유지. 패키지는 `MCPToolTest/Assets/MCPTools/` | `…/<repo>.git?path=MCPToolTest/Assets/MCPTools#v1.0.0` | 개발용 Unity 프로젝트와 패키지가 한 저장소에 있어 **개발·검증 흐름이 그대로** 유지됨. 이동 작업 불필요 | clone 시 저장소 전체(프로젝트·문서·zip)를 받으므로 **설치가 무거움** → U12의 `.gitignore` 정리가 필수. 경로가 길어 U11 위험 증가 |
| **B. 패키지 전용 저장소 분리** | 패키지만 담은 저장소를 만들고, 개발 프로젝트는 `Packages/manifest.json`에서 `file:` 로컬 참조 또는 embed | `…/<package-repo>.git#v1.0.0` | 설치가 가볍고 URL이 짧다. 태그·CHANGELOG가 패키지 이력과 1:1 | 저장소가 2개로 늘어 **동기화 절차**가 필요. 개발 중 수정→검증 루프가 번거로워짐 |

**권장:** A로 시작해 U1~U6를 해결하고 첫 태그(`v1.0.0`)를 낸 뒤, 외부 사용자가 늘면 B로 이관한다. 어느 쪽이든 `package.json`의 `name`은 처음부터 확정해야 한다(변경 시 사용자 재설치 필요).

### 9.5 검증 방법 — 시나리오 E (UPM 설치)

**E. GitHub + Package Manager 설치**

17. 빈 Unity 6 프로젝트에서 `Window > Package Manager > Add package from git URL`에 `?path=…#v0.0.1` 형식으로 입력 → **컴파일 오류 0**, `Tools/MCP/*` 메뉴 8개 노출 (U4).
18. 같은 상태에서 `Tools/MCP/Settings` 열기 → 설정 에셋이 **`Assets/` 아래에 생성**되고 값 변경이 저장·유지됨. 패키지 폴더에는 아무 것도 생기지 않음 (U2/U3).
19. 3단계 창에서 [서버 시작] → 브리지가 **PackageCache 안의 `Server~/bridge_server.py`** 로 정상 기동 (U1). 실패 시 오류 메시지에 해석된 절대 경로가 표시되는지 확인.
20. 패키지를 `Packages/<name>/`으로 **embed**한 뒤 18·19 재확인 (설치 형태 2종 모두 동작).
21. Package Manager에서 패키지를 **제거 후 재설치**(재해결) → 사용자 설정·사용자 템플릿·프로젝트 쪽 워크플로 사본이 **모두 살아 있음** (U2/U6).
22. 기존 `Assets/MCPTools/` zip 설치본이 있는 프로젝트에 패키지를 추가 → **중복 설치 경고**가 뜨고 조치 안내가 나옴 (U5). 안내대로 구 폴더 삭제 후 컴파일 오류 0.
23. 2단계 창에서 사용자 템플릿 폴더에 `.json`을 추가 → 드롭다운에 나타남. 패키지 기본 템플릿과 이름이 같으면 사용자 것이 우선 (U6).
24. git이 설치되지 않은 PC에서 git URL 설치 시도 → 실패하지만 README의 안내로 원인 파악 가능. zip 설치 경로로 우회 성공 (U9).
25. `C:\Users\<한글이름>\Documents\Unity Projects\<긴 이름>\` 등 **깊은 경로**의 프로젝트에서 17~19 재확인 (U11).
26. 태그 `v0.0.1`과 `v0.0.2`를 각각 설치해 **버전이 Package Manager UI에 정확히 표시**되고 전환이 동작함 (U8).
