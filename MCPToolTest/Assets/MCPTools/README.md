# MCP Tools — 기획서 기반 AI 에셋 생성 파이프라인

기획서(디자인 문서)를 입력으로 받아 AI로 게임 에셋(이미지/UI/사운드)을 생성하고, Unity 프리팹/UI에 자동 적용하는 **4단계 파이프라인**을 Unity Editor Tool + MCP 도구로 제공합니다.

| 단계 | 도구 | 상태 |
|------|------|------|
| 1. 에셋 리스트업 (AssetListup) | 기획서 + 프로젝트 스캔 → 생성할 에셋 목록 JSON | **구현됨** |
| 2. 프롬프트 제작 (PromptBuilder) | 목록 → 항목별 생성 프롬프트 | **구현됨** |
| 3. 생성 (ComfyUIGenerator) | 프롬프트 + Workflow JSON → 후보 4개 생성·선택 | **구현됨** |
| 4. 적용 (AssetApplier) | 선택 결과물 → 프리팹/UI 적용 | **구현됨** |

파이프라인은 **AI 중립** 설계입니다. 분석/작성 지능은 사용자가 쓰는 AI(MCP 클라이언트, 로컬 AI CLI, 웹 AI)에 위임하고, 도구는 "재료 수집(scan)"과 "결과 검증·저장(save)"만 담당합니다.

## 빠른 시작 (첫 실행 체크리스트)

처음 도입할 때 이 순서대로 따라가면 첫 생성까지 도달합니다. 각 항목의 상세는 링크된 절을 참고하세요.

1. **(해당하는 경우) 기존 설치본 삭제** — 예전에 `Assets/` 아래에 `MCPTools` 폴더를 직접 넣어 쓰던 프로젝트는 **패키지를 추가하기 전에** 그 폴더를 지웁니다. → [설치](#설치)
2. **패키지 설치** — `Window > Package Manager`의 좌측 상단 **[+] > Add package from git URL**에 아래 URL을 입력합니다. 사용자 PC에 `git`이 설치되어 있어야 합니다. → [설치](#설치)
   ```
   https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.1.0
   ```
3. **컴파일·메뉴 확인** — 콘솔에 컴파일 오류가 없고 `Tools/MCP/*` 메뉴가 보이는지 확인합니다.
4. **(3단계를 쓸 때) ComfyUI 준비** — ComfyUI를 실행하고, 워크플로가 요구하는 모델 파일과 커스텀 노드(ComfyUI-Inspyrenet-Rembg, ComfySwitchNode)를 설치합니다. → [요구 사항](#요구-사항)
5. **(3단계를 쓸 때) Python 3.7 이상 준비** — 브리지 서버 실행용입니다. pip 패키지 설치는 필요 없습니다. → [요구 사항](#요구-사항)
6. **설정 확인** — `Tools/MCP/Settings`를 열어 ComfyUI 주소를 확인합니다. 설정 에셋은 `Assets/MCPTools.User/MCPToolSettings.asset`에 자동 생성됩니다. → [MCPToolSettings 설정 항목](#mcptoolsettings-설정-항목)
7. **브리지 서버 시작** — `Tools/MCP/3. ComfyUI Generator` 상단의 **[서버 시작]** 을 누릅니다(3단계에서만 필요). ComfyUI 자체는 별도로 실행해야 합니다. → [3단계](#3단계--생성-comfyuigenerator-사용법)
8. **기획서 넣기** — `Assets/Docs` 폴더는 도구 창을 처음 열 때 자동 생성되지만, **기획서 `.md`/`.txt` 파일은 직접 넣어야** 합니다. → [요구 사항](#요구-사항)
9. **1단계 실행** — `Tools/MCP/1. Asset Listup`으로 생성할 에셋 목록(`AssetList_*.json`)을 만듭니다. → [1단계](#1단계--에셋-리스트업-assetlistup-사용법)
10. **2 → 3 → 4단계** — 프롬프트 제작 → 후보 4개 생성·확정 → 프리팹/UI 적용 순으로 진행합니다. 전체 진행 상황은 `Tools/MCP/Pipeline (All-in-One)`에서 한눈에 볼 수 있습니다. → [2단계](#2단계--프롬프트-제작-promptbuilder-사용법) · [3단계](#3단계--생성-comfyuigenerator-사용법) · [4단계](#4단계--적용-assetapplier-사용법)

막히면 [문제 해결](#문제-해결) 절을 먼저 확인하세요.

## 요구 사항

- **Unity 6000.5.2f1 (Unity 6)** — URP/uGUI 프로젝트에서 개발·검증됨
- **git 2.14 이상** — Package Manager의 git URL 설치가 사용자 PC의 `git` 실행 파일을 직접 호출합니다. → [설치](#설치)
- **기획서 파일** — 1단계 입력입니다. `Assets/Docs` 폴더는 도구 창을 처음 열 때 자동 생성되지만(경로는 설정의 `docsRootPath`), 그 안의 기획서 `.md`/`.txt` 파일은 사용자가 직접 넣어야 합니다. 파일이 없으면 1단계 창의 기획서 드롭다운이 비어 있습니다.
- **uGUI · 2D Sprite · unity-mcp 패키지는 모두 선택 사항**이며, `package.json`의 `dependencies`에 **선언하지 않았습니다** — 이 패키지를 강제로 설치시키지 않고, 없으면 해당 기능만 비활성화됩니다 (각 asmdef의 `versionDefines`/`defineConstraints`로 격리). 자세한 동작은 아래 각 항목을 참고하세요.
- **uGUI 패키지 (`com.unity.ugui`) — 선택 사항.** 도구는 uGUI 어셈블리를 **참조하지 않습니다.** 프리팹/씬 스캔(1단계)과 적용(4단계)은 컴포넌트 타입 이름과 SerializedProperty로 `Image`/`RawImage`를 다루므로, uGUI가 없는 프로젝트에서도 **컴파일 오류 없이 전 기능이 동작**합니다. 이 경우 프로젝트에 `Image`/`RawImage` 컴포넌트 자체가 존재하지 않으므로 해당 슬롯만 스캔 결과에 나타나지 않고, `SpriteRenderer`/`AudioSource` 슬롯은 그대로 수집됩니다.
- **2D Sprite 패키지 (`com.unity.2d.sprite`) — 선택 사항.** 스프라이트 시트의 Sprite Mode=Multiple 슬라이스 적용에만 필요합니다. Unity 6에서 구 `TextureImporter.spritesheet` API가 동작하지 않아 모던 `ISpriteEditorDataProvider`(어셈블리 `Unity.2D.Sprite.Editor`)를 사용합니다. 대부분의 Unity 프로젝트 템플릿에 기본 포함되어 있습니다.
  - 패키지 의존 코드는 별도 어셈블리 `Editor/SpriteSlicing/`(`MCPTools.Editor.SpriteSlicing.asmdef`)에 격리되어 있고 **defineConstraints**(`MCPTOOLS_HAS_2D_SPRITE`)가 걸려 있어, 패키지가 없으면 어셈블리 자체가 컴파일 대상에서 제외됩니다. 따라서 **미설치 시 스프라이트 시트 슬라이싱만 비활성화되고 나머지 기능(1~4단계 전 파이프라인)은 정상 동작**하며, 슬라이싱을 시도하면 설치 안내 메시지가 다이얼로그/도구 오류로 표시됩니다.
- **Python 3.7 이상** (3단계 생성용) — 브리지 서버(`Editor/ComfyUIGenerator/Server~/bridge_server.py`) 실행에 필요합니다. 표준 라이브러리만 사용하므로 pip 패키지 설치는 필요 없습니다.
  - Windows에서 [python.org](https://www.python.org/downloads/) 설치 시 첫 화면의 **"Add python.exe to PATH"** 체크를 권장합니다.
  - 실행 파일은 [서버 시작] 시 **자동 탐지**됩니다(설정값 → `py -3`/`python`/`python3` → 표준 설치 폴더 → PATH 순서로 찾아 실제 실행해 3.7 이상인지 검증). 자동 탐지에 실패하면 `Tools/MCP/Settings`의 **Python 실행 파일**에 `python.exe` 절대 경로를 지정하거나 같은 창의 **[Python 자동 탐지]** 버튼을 누르세요.
- **ComfyUI 로컬 서버** (3단계 생성용) — 기본 주소 `http://127.0.0.1:8188`, 설정 에셋에서 변경 가능. 1·2단계만 사용할 때는 필요 없습니다.
  - **버전**: 기본 워크플로가 `ReferenceLatent`(UI/StyleChange), `SaveAudioAdvanced`(Audio) 같은 비교적 최근에 추가된 **코어 노드**를 사용합니다. 특정 최소 버전·커밋을 검증한 근거가 없으므로, **이 코어 노드들을 포함하는 최신 버전(2025년 이후 릴리스) 사용을 권장**합니다. 버전이 낮으면 코어 노드가 없어 **커스텀 노드 미설치와 구분되지 않는 "노드 누락" 오류**로 나타납니다. 어떤 노드가 없는지는 생성 전 **사전 검증(preflight)** 이 목록으로 알려주므로(아래 참조), 목록에 나온 이름이 아래 커스텀 노드 목록에 없다면 코어 노드일 가능성이 높으므로 **ComfyUI를 최신 버전으로 업데이트**해보세요.
  - **사전 검증(preflight)**: 3단계 창에서 워크플로를 선택하면 누락된 커스텀 노드가 창 상단에 경고로 표시되고, [생성]을 누르면 제출 전에 누락 노드·ComfyUI에 없는 모델 파일명을 다이얼로그로 안내하고 생성을 중단합니다. ComfyUI에 연결되지 않은 상태에서는 검증이 생략됩니다.
  - **커스텀 노드 설치 방법**: [ComfyUI-Manager](https://github.com/ltdrdata/ComfyUI-Manager)를 설치한 뒤(저장소의 설치 안내 참고), ComfyUI 웹 UI의 **Manager** 버튼 → 커스텀 노드 관리 화면에서 노드 이름(`ComfyUI-Inspyrenet-Rembg`, `ComfySwitchNode`)으로 검색해 설치하고 **ComfyUI를 재시작**하면 됩니다. 모델 파일은 ComfyUI의 `models/` 하위 폴더(체크포인트는 `checkpoints/`, LoRA는 `loras/`, VAE는 `vae/` 등)에 직접 넣습니다.
  - 기본 워크플로(원본 JSON)는 다음 모델/커스텀 노드를 전제로 합니다:
    - 이미지/UI/스타일 변경: Flux.2 Klein 4B (`flux-2-klein-base-4b-fp8` / `flux-2-klein-4b-fp8`), CLIP `qwen_3_4b`, VAE `flux2-vae` — [설치 가이드](https://docs.comfy.org/ko/tutorials/flux/flux-2-klein)
    - 배경 제거: 커스텀 노드 **ComfyUI-Inspyrenet-Rembg** + **ComfySwitchNode**(Switch/PrimitiveBoolean)
    - 오디오: Stable Audio 3 (`stable_audio_3_small_music`/`_sfx`, 텍스트 인코더 `t5gemma_b_b_ul2`) — [모델](https://huggingface.co/Comfy-Org/stable-audio-3/tree/main)
    - GenerateImage 워크플로 기본값은 LoRA `klein4b_masterpieces_v3.1` / Checkpoint `miaomiaoHarem_anima13` 파일명을 참조합니다. 다른 모델을 쓰려면 창의 변수 UI에서 파일명을 바꾸면 됩니다 (JSON 수정 불필요).
    - GenerateImageFlux 워크플로는 GenerateImage와 달리 Checkpoint/LoRA 없이 **UNET+CLIP+VAE 조합만 사용하는 Flux 전용** 워크플로입니다. UNET `flux-2-klein-base-4b-fp8.safetensors`, 텍스트 인코더 `qwen_3_4b.safetensors`, VAE `flux2-vae.safetensors`와 커스텀 노드 **ComfyUI-Inspyrenet-Rembg**(배경 제거 스위치 노드 사용) + **ComfySwitchNode**를 전제로 합니다.
- **unity-mcp (MCP For Unity, `com.coplaydev.unity-mcp`) 패키지 — 선택 사항.** MCP 도구(`mcptools_*`)를 MCP 클라이언트에 노출할 때만 필요합니다.
  - 패키지 의존 코드는 별도 어셈블리 `Editor/McpForUnityBridge/`(`MCPTools.Editor.McpForUnity.asmdef`)에 격리되어 있습니다. 이 asmdef는 **versionDefines**(`com.coplaydev.unity-mcp` 패키지가 존재하면 `MCPTOOLS_HAS_MCPFORUNITY` 심볼 자동 정의)와 **defineConstraints**(`MCPTOOLS_HAS_MCPFORUNITY` 요구)를 함께 사용하므로, 패키지가 없으면 어셈블리가 참조 해석 전에 통째로 제외되어 "non-existent assemblies" 컴파일 오류가 발생하지 않습니다. 패키지가 없는 프로젝트에서도 에디터 창만으로 전 기능을 사용할 수 있습니다.

## 설치

이 도구는 **GitHub + Unity Package Manager(UPM, git URL)** 로 배포합니다. zip·`.unitypackage` 배포는 하지 않습니다.

- 저장소: <https://github.com/chomul/MCPTools>
- 패키지 이름: `com.sungchan.mcptools` (현재 버전 `0.1.0`)

> ⚠️ **기존 설치본이 있다면 먼저 삭제하세요.** 예전에 `Assets/` 아래에 `MCPTools` 폴더를 직접 넣어 쓰던 프로젝트는 **패키지를 추가하기 전에 그 폴더를 반드시 삭제**해야 합니다. 남겨두면 어셈블리 이름이 중복되어 `Assembly with name 'MCPTools.Editor' already exists` 컴파일 오류가 나고, `.meta` GUID·설정 에셋도 두 벌이 됩니다.
> **`Assets/MCPTools.User/`는 사용자 데이터(설정 에셋·프롬프트 템플릿·워크플로 사본)이므로 지우면 안 됩니다.** 지울 대상은 도구 본체가 들어 있던 `MCPTools` 폴더뿐입니다.

### 1) Package Manager로 설치 (git URL)

1. Unity 6에서 **`Window > Package Manager`** 를 엽니다.
2. 창 좌측 상단의 **[+] 버튼 → `Install package from git URL...`**(Unity 버전에 따라 `Add package from git URL...`)을 선택합니다.
3. 아래 URL을 붙여넣고 [Install]/[Add]를 누릅니다.

   ```
   https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.1.0
   ```

4. 설치가 끝나면 컴파일 오류 없이 `Tools/MCP/*` 메뉴가 나타납니다.

**git이 필요합니다.** UPM의 git URL 설치는 사용자 PC에 설치된 **`git`(2.14 이상)** 실행 파일을 직접 호출합니다. 설치되어 있지 않으면 `Unable to add package ... git executable not found` 같은 오류로 실패합니다. 이때는 [git](https://git-scm.com/downloads)을 설치한 뒤 **Unity 에디터와 Unity Hub를 모두 종료했다 다시 실행**하세요 (이미 실행 중인 프로세스는 설치 전의 옛 PATH를 계속 사용합니다).

### 2) 버전 고정과 업데이트

- URL 끝의 **`#v0.1.0`은 git 태그**입니다. 태그를 붙이면 해당 버전으로 **고정**되어 팀원 모두가 같은 버전을 받습니다.
- 태그를 **생략하면 기본 브랜치의 HEAD**를 받으므로, 설치 시점에 따라 서로 다른 버전이 될 수 있습니다. 팀 프로젝트에서는 태그를 붙이는 것을 권장합니다.
- 업데이트하려면 프로젝트의 `Packages/manifest.json`에서 이 패키지의 URL 태그를 새 버전으로 바꾸거나(예: `#v0.1.0` → `#v0.2.0`), Package Manager에서 **패키지를 제거한 뒤 새 URL로 다시 추가**합니다. 변경 이력은 [CHANGELOG.md](CHANGELOG.md)를 참고하세요.
- 패키지를 제거·재설치해도 `Assets/MCPTools.User/`의 설정·템플릿·워크플로 사본과 `Assets/Docs`·`Assets/Generated`의 산출물은 그대로 남습니다.

### 3) (선택) 로컬 개발용 설치

패키지를 직접 수정하며 개발하려면 폴더째 프로젝트의 **`Packages/` 아래에 넣어 embed**하거나(예: `Packages/com.sungchan.mcptools/`), `Assets/` 아래에 폴더째 복사해도 동작합니다. 설치 루트를 런타임에 계산하므로 세 형태(`Assets/`, `Packages/` embed, PackageCache) 모두에서 설정 로드·브리지 서버 시작·템플릿 탐색이 동작합니다. 단 **폴더 내부 구조는 그대로 유지**해야 합니다.

### 4) 첫 실행 시 생성되는 것

- `Tools/MCP/*` 메뉴를 처음 사용하면 설정 에셋이 **`Assets/MCPTools.User/MCPToolSettings.asset`** 에 자동 생성됩니다. 패키지 폴더(`Packages/...`)는 읽기 전용이라 그 안에는 에셋을 만들 수 없기 때문입니다.
  - 설정 조회는 **`Assets` 범위만** 검색하므로, 기존에 `Assets/MCPTools/Editor/Common/MCPToolSettings.asset`을 쓰던 프로젝트는 **그 에셋을 계속 사용**합니다(새로 만들어지지 않음). `Assets` 아래에 설정 에셋이 2개 이상이면 첫 번째를 사용하고 콘솔에 중복 경로를 경고로 남깁니다.
- 각 단계 창을 열면 `Assets/Docs`·`Assets/Generated` 폴더가 없을 때 자동 생성되고 콘솔에 안내가 1회 출력됩니다. **기획서 파일은 사용자가 `Assets/Docs`에 직접 넣어야 합니다.**
- (선택) MCP 도구를 쓰려면 unity-mcp 패키지를 설치하고 MCP 클라이언트를 연결합니다. `Tools/MCP/Ping (Local Test)` 메뉴 또는 `mcptools_ping` 호출로 연결을 확인할 수 있습니다.

### MCPToolSettings 설정 항목

설정 에셋 위치: **`Assets/MCPTools.User/MCPToolSettings.asset`** (자동 생성). 편집 창: **`Tools/MCP/Settings`**.

| 항목 | 기본값 | 설명 |
|------|--------|------|
| `comfyUIServerUrl` | `http://127.0.0.1:8188` | ComfyUI 서버 주소 (브리지 서버 실행 인자로 전달) |
| `bridgeServerUrl` | `http://127.0.0.1:8189` | 브리지 서버 주소 (Unity ↔ ComfyUI 중간 서버) |
| `pythonExecutable` | `python` | 브리지 서버 실행에 사용할 Python 3 실행 파일. **비워두거나 기본값(`python`)이면 자동 탐지**하며(설치된 Python 3.7 이상을 찾아 실행해 검증), 자동 탐지가 실패하는 환경에서만 `python.exe` 절대 경로를 직접 지정하면 됩니다 (설정 창의 [Python 자동 탐지] 버튼으로 채울 수 있음) |
| `requestTimeoutSeconds` | 300 | 요청(결과 다운로드 포함) 타임아웃(초) |
| `jobTimeoutSeconds` | 600 | 브리지 서버의 **생성 Job 타임아웃(초)** — 후보 1건 생성의 최대 대기 시간입니다. 저사양 GPU에서 생성이 오래 걸려 타임아웃이 나면 늘리세요. 브리지 서버 실행 인자(`--job-timeout`)로 전달되므로 **변경 후 브리지 서버를 재시작**해야 적용됩니다 |
| `defaultImageWorkflow` | `GenerateImage` | 이미지 항목 기본 워크플로 이름 |
| `generatedRootPath` | `Assets/Generated` | 생성 결과물 루트 경로 |
| `docsRootPath` | `Assets/Docs` | 기획서·목록 문서 루트 경로 |
| `candidateCount` | 4 | 항목당 후보 생성 개수 |
| `spritePixelsPerUnit` | 100 | 확정 시 Sprite 임포트에 적용할 Pixels Per Unit |
| `unloadModelsAfterBatch` | true | 생성(단건/일괄) 완료 후 브리지 `/free`로 ComfyUI 모델을 언로드해 VRAM/메모리를 확보. 다음 생성 시 모델을 다시 로드하므로 첫 생성이 느려질 수 있음 |

설정 창(`Tools/MCP/Settings`)의 버튼:

- **[Python 자동 탐지]** — 설치된 Python 3.7 이상을 찾아 `pythonExecutable`에 절대 경로를 채웁니다. 실패하면 원인·조치 안내 다이얼로그가 표시됩니다.
- **[워크플로를 프로젝트로 복사]** — 패키지에 동봉된 워크플로 JSON과 `variables.json`을 **`Assets/MCPTools.User/ComfyUI/`** 로 복사합니다. 복사본이 있으면 브리지 서버가 패키지 동봉본보다 **우선 사용**하므로, 읽기 전용인 UPM 설치에서도 워크플로/변수를 자유롭게 수정할 수 있습니다. 복사 후 브리지 서버를 재시작하세요. → [사용자 확장](#사용자-확장)
- **[서버 연결 테스트]** — 설정된 ComfyUI 주소로 연결을 확인합니다.

## 1단계 — 에셋 리스트업 (AssetListup) 사용법

메뉴: **`Tools/MCP/1. Asset Listup`**

창 구성(위에서 아래로): **기획서에서 항목 추측해 추가** 토글 → **AI 연동** 박스 → **로컬 AI 미사용 시 (수동 방식)** Foldout → 항목 표 → 하단 상태 메시지 + [항목 추가]/[저장].

### 0) 항목 소스 토글 — "기획서에서 항목 추측해 추가"

상단 토글 **[기획서에서 항목 추측해 추가 (끄면 스캔 항목만 · 기획서는 설명 참고용)]**(기본 **OFF**, EditorPrefs 유지)이 목록 생성 버튼의 동작을 결정합니다. 레이아웃은 바꾸지 않으며 모든 섹션이 항상 보입니다.

- **OFF (기본)** — 항목은 **열린 씬의 슬롯 + 스캔 루트(기본 `Assets`) 아래 모든 프리팹의 슬롯을 항상 병합한 스캔 결과에서만** 만들어집니다(Image/RawImage/SpriteRenderer/AudioSource 슬롯, `targetPrefabPath`/`targetScenePath`/`targetObjectPath`·UI 여부가 채워짐). 열린 씬에 포함된 프리팹과 스캔 루트 프리팹이 겹치면 프리팹 단위로 한 번만 담기므로 같은 슬롯이 중복 생성되지 않으며, **씬이 비어 있어도** 스캔 루트의 프리팹으로 목록이 만들어집니다(하단 상태 메시지에 "열린 씬 슬롯 N개 + 프리팹 슬롯 M개 = 총 K개" 구성이 표시됩니다). 열린 씬과 스캔 루트 양쪽 모두에서 슬롯을 찾지 못한 경우에만 안내 다이얼로그가 뜹니다. 기획서는 새 항목을 추가하지 않고 각 스캔 항목의 **설명/용도(description)를 채우는 참고 자료**로만 쓰입니다. [선택한 AI로 목록 생성]을 누르면 AI가 스캔 항목 목록 + 기획서를 받아 항목을 그대로 두고 설명만 채워 반환하며, 선택한 기획서 경로는 문서 `designDocPath`에 기록됩니다. AI 없이 수동 [스캔 + 휴리스틱 추출]을 쓰면 스캔 항목만 만들어지고 설명은 수동으로 보완합니다. 기획서를 선택하지 않았거나 AI 도구를 `클립보드 복사만`으로 둔 경우에는 스캔 항목만 생성합니다.
- **ON** — 기획서를 읽어 **스캔에 없는 항목까지 추측해 추가**하는 기존 추출 방식(AI/휴리스틱)으로 동작합니다. [선택한 AI로 목록 생성]/[스캔 + 휴리스틱 추출] 모두 종전과 동일합니다.

저장되지 않은 임시 씬은 스캔에서 제외됩니다.

### 1) AI 연동 (주 흐름)

1. **기획서 파일** 드롭다운에서 `docsRootPath`(기본 `Assets/Docs`)의 `.md`/`.txt` 파일을 선택합니다 ([새로고침]으로 갱신). **스캔 루트**(기본 `Assets`)를 지정합니다. **스캔 대상 씬** 목록([+ 추가]/[제거], SceneAsset 필드)에 씬을 지정하면 해당 씬에 **직접 배치된(프리팹 인스턴스가 아닌)** 오브젝트의 슬롯도 함께 스캔합니다 — 지정한 씬만 스캔하며(전체 씬 자동 스캔 아님), 프리팹 인스턴스는 원본 프리팹 스캔으로 커버되므로 제외됩니다. 열려 있지 않은 씬은 Additive로 열어 읽기 전용으로 스캔한 뒤 닫아 원래 상태를 복원합니다.
2. **AI 도구** 드롭다운 — PATH에서 자동 감지된 AI CLI(claude/codex/gemini/cursor-agent/copilot) + `직접 입력...`(임의 커맨드) + `클립보드 복사만` 중 선택. [다시 검색]으로 재탐지. 타임아웃(초, 기본 300)과 함께 EditorPrefs에 기억됩니다.
3. **프로젝트 코드 탐색 허용** 토글(기본 켬) — 켜면 AI CLI를 프로젝트 루트에서 읽기 전용 도구 허용으로 실행해 `Assets/` 아래 스크립트·씬·프리팹을 직접 읽으며 역할을 추론합니다(정확하지만 느리고 토큰 소모 큼). 끄면 기획서 원문 + 스캔 요약만 담아 일회성으로 묻습니다. 어느 쪽도 파일 쓰기/명령 실행은 허용하지 않습니다.
4. **[선택한 AI로 목록 생성]** — 프리팹 스캔 후 프롬프트를 만들어 선택한 CLI를 비대화형으로 실행합니다. 실행 중 에디터는 멈추지 않으며 [취소] 가능. 성공 시 응답 JSON을 파싱해 기존 목록에 **교체/병합/취소**를 물어 표에 반영합니다. 실패(미로그인/타임아웃 등) 시 오류 다이얼로그가 뜨고 응답 원문이 [AI 응답 JSON 불러오기] 창에 자동 주입되어 수동 보정할 수 있습니다.

### 2) 수동 방식 (AI CLI 미사용 시)

Foldout 안의 3버튼 (AI CLI 미감지 시 자동으로 펼쳐짐):

- **[스캔 + 휴리스틱 추출(보조)]** — 기획서 헤딩/표/불릿 휴리스틱 파싱 + 프리팹 스캔(+지정 씬 스캔) 매칭으로 목록 초안을 생성합니다.
- **[AI용 프롬프트 복사]** — 기획서 원문 + 스캔 슬롯 요약 + 항목 스키마 + "JSON 배열만 출력" 지침을 클립보드에 복사합니다. 웹 AI 등에 붙여넣어 사용합니다.
- **[AI 응답 JSON 불러오기]** — 보조 창에 AI 응답 JSON 배열을 붙여넣거나 파일로 불러와 목록에 **교체/병합**합니다. 마크다운 코드 펜스(```)는 자동 제거됩니다.

### 3) 편집과 저장

- 항목 표(ID/이름/종류/UI 여부/대상 프리팹/대상 씬/대상 오브젝트/설명/상태/삭제)에서 셀을 직접 편집합니다. 대상(프리팹/씬) 경로가 모두 비어 있거나 UI 여부가 비어 있는 행은 경고색으로 표시됩니다. `targetPrefabPath`와 `targetScenePath`는 상호 배타이며, 씬 항목의 대상 오브젝트 경로는 씬 루트 오브젝트 기준 계층 경로입니다.
- **[저장]** — 대상 미기록 항목이 있어도 저장이 차단되지 않습니다. "대상 미기록 항목 N건" 확인 다이얼로그에서 [저장]을 선택하면 해당 항목 상태가 **"대상 미정"** 으로 기록된 채 저장되고, [취소] 시 저장하지 않습니다. 저장 경로: `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json`.

## 2단계 — 프롬프트 제작 (PromptBuilder) 사용법

메뉴: **`Tools/MCP/2. Prompt Builder`**

1단계 산출물(`Assets/Docs/AssetList_*.json`)을 입력으로 받아, 항목별 ComfyUI용 positive/negative 프롬프트를 담은 `PromptSet_{yyyyMMdd_HHmm}.json`을 만듭니다. 창 구성은 1단계와 동일합니다: **AI 연동** 박스 → **로컬 AI 미사용 시 (수동 방식)** Foldout → 프롬프트 표 → 하단 상태 메시지 + [항목 추가]/[저장].

### 1) AI 연동 (주 흐름)

1. **에셋 목록 JSON** 드롭다운에서 `docsRootPath`의 `AssetList_*.json`을 선택합니다 (최신 파일이 기본 선택, [새로고침]으로 갱신). **템플릿** 드롭다운에서 프롬프트 템플릿을 선택합니다 (기본 `default`).
2. **AI 도구** 드롭다운/타임아웃/**프로젝트 코드 탐색 허용** 토글은 1단계와 동일합니다 (`AiCliRunner` 공용, EditorPrefs 기억). 탐색 모드에서는 AI가 각 항목의 대상 프리팹·스크립트를 읽어 프롬프트 묘사를 구체화합니다.
3. **[선택한 AI로 프롬프트 생성]** — 목록+템플릿+promptSchema로 프롬프트를 만들어 선택한 CLI를 비대화형으로 실행합니다. 성공 시 응답 JSON을 파싱해 **교체/병합/취소**를 물어 표에 반영합니다 (병합은 같은 `id` 항목을 덮어씀). 실패 시 오류 다이얼로그 + 응답 원문이 [AI 응답 JSON 불러오기] 창에 자동 주입됩니다.

### 2) 수동 방식 (AI CLI 미사용 시)

- **[템플릿 초안 생성(보조)]** — `PromptBuilder.Build`로 템플릿 규칙 기반 초안을 즉시 생성합니다. 이미지 항목: `stylePrefix + 이름/설명 + qualityTags`, UI 항목(isUI 또는 assetType=="ui"): `uiExtraTags`(clean edges, transparent background, game ui icon 등) 자동 추가, 오디오 항목: 이미지 태그 없이 `audioStylePrefix` 기반 사운드 프롬프트.
- **[AI용 프롬프트 복사]** — 목록 요약 + 템플릿 + promptSchema + "JSON 배열만 출력" 지침을 클립보드에 복사합니다 (웹 AI용).
- **[AI 응답 JSON 불러오기]** — 보조 창에 응답 JSON을 붙여넣거나 파일로 불러와 **교체/병합**합니다. 코드 펜스는 자동 제거됩니다.

### 3) 편집과 저장

- 표(ID/이름/종류/UI/대상 프리팹/Positive/Negative/삭제)에서 셀을 직접 편집합니다. positive가 비어 있는 행은 경고색으로 표시됩니다.
- **[저장]** — positive가 빈 항목이 있으면 확인 다이얼로그 후 저장합니다. 저장 경로: `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json`.

### 프롬프트 템플릿

기본 템플릿은 코드에 내장되어 있으며(`PromptTemplate.CreateDefault`), JSON 파일을 두면 추가 템플릿으로 선택할 수 있습니다. 템플릿은 두 폴더에서 찾아 **합쳐서** 목록에 보여줍니다.

- **`Assets/MCPTools.User/Templates/<이름>.json`** — 사용자 템플릿. 패키지가 읽기 전용이어도 자유롭게 추가·수정할 수 있고, 이름이 겹치면 이쪽이 **우선**합니다. → [사용자 확장](#사용자-확장)
- `<MCPTools 설치 루트>/Editor/PromptBuilder/Templates/<이름>.json` — 패키지 동봉 기본값(읽기 전용).

필드: `stylePrefix`, `qualityTags`, `commonNegative`, `uiExtraTags`, `audioStylePrefix`, `audioNegative` (생략된 필드는 기본값 유지).

## 3단계 — 생성 (ComfyUIGenerator) 사용법

메뉴: **`Tools/MCP/3. ComfyUI Generator`**

2단계 산출물(`Assets/Docs/PromptSet_*.json`)을 입력으로 **브리지 서버** 경유로 ComfyUI를 호출해 항목당 후보 4장(시드 변경)을 생성하고, 썸네일에서 선택·확정합니다.

### 브리지 서버 구조

Unity와 ComfyUI 사이에 로컬 중간 서버(`Editor/ComfyUIGenerator/Server~/bridge_server.py`, Python 3 표준 라이브러리만 사용)를 둡니다. 브리지 서버가 원본 워크플로 JSON 로드·변수 덮어쓰기·시드 변경 큐잉·완료 폴링을 담당하고, Unity는 REST로 요청/폴링만 합니다.

```
Unity(BridgeClient) ── HTTP :8189 ──> bridge_server.py ── HTTP :8188 ──> ComfyUI
```

| 엔드포인트 | 설명 |
|------|------|
| `GET /health` | `{ ok, comfyUrl, comfyAlive }` — ComfyUI 생존 여부 포함 |
| `GET /workflows` | 워크플로 목록 + 변수 매니페스트(기본값 = 원본 JSON 값) |
| `POST /generate` | `{ workflow, variables, count, baseSeed }` → 변수 덮어쓰기 + 시드 `baseSeed..+count-1`로 count회 큐잉, `jobId` 즉시 반환 |
| `GET /job/{jobId}` | `{ status: running\|completed\|failed, progress, message, results }` |
| `POST /upload` | 이미지를 ComfyUI `/upload/image`로 전달 (LoadImage용) |
| `GET /view` | ComfyUI `/view` 프록시 (결과 다운로드) |

워크플로 JSON은 `Server~/workflows/`에 **원본 구조 그대로** 있으며(토큰 치환 없음), 요청 시점에 노드 `inputs` 값만 덮어씁니다. 조정 가능한 변수는 `Server~/variables.json` 매니페스트로 정의됩니다. `Assets/MCPTools.User/ComfyUI/`에 사본이 있으면 브리지 서버가 **그쪽을 우선 사용**합니다 → [사용자 확장](#사용자-확장).

### 1) 서버 상태 / 시작·종료 (창 상단)

- 상단에 **브리지 서버 상태**와 **ComfyUI 연결 상태**(●, 5초 주기 자동 확인), **[서버 시작] / [서버 종료]** 버튼이 표시됩니다.
- **[서버 시작]** — Python 3 실행 파일을 **자동 탐지**해 브리지 서버를 실행합니다 (`--port`는 `bridgeServerUrl`의 포트, `--comfy-url`은 `comfyUIServerUrl`).
  - 탐지 순서: 설정의 `pythonExecutable`(절대 경로면 존재 확인) → `py -3` / `python` / `python3` → Windows 표준 설치 폴더(`%LOCALAPPDATA%\Programs\Python\Python3*`, `%ProgramFiles%\Python3*` 등) → PATH의 각 디렉터리. 각 후보를 실제로 실행해 **Python 3.7 이상**인지 검증하므로 Windows 스토어 앱 실행 별칭 스텁이나 Python 2는 자동으로 걸러집니다. 결과는 세션 단위로 캐시됩니다.
  - Python을 찾지 못하면 설치·PATH·Unity 재시작·스토어 별칭·절대 경로 지정 방법을 담은 안내 다이얼로그가 뜹니다. 시작은 됐지만 서버가 곧바로 죽는 경우(포트 충돌 등)도 종료 코드와 로그 마지막 내용을 포함한 안내가 표시됩니다.
  - **ComfyUI 자체는 별도로 실행해야 합니다.**
- **[서버 종료]** — 이 도구로 시작한 프로세스 트리를 종료합니다(taskkill /T /F). PID는 SessionState에 저장되어 스크립트 컴파일(도메인 리로드) 후에도 유지됩니다. 외부에서 직접 실행한 서버는 종료할 수 없습니다.

### 2) 워크플로 선택과 변수 편집

- **워크플로** 드롭다운 — 브리지 서버의 `GET /workflows` 목록(`GenerateImage` / `GenerateImageFlux` / `UI` / `StyleChange` / `Audio`)에서 선택합니다. 선택하면 해당 워크플로의 **변수 편집 UI가 동적으로 생성**됩니다 (기본값 = 원본 JSON의 현재 값, **[기본값 복원]** 버튼 제공).
- 선택한 워크플로가 요구하는 **커스텀 노드가 ComfyUI에 없으면 창 상단에 경고**가 표시됩니다 (누락 노드 이름 + ComfyUI-Manager 설치 안내). ComfyUI에 연결되지 않은 상태에서는 검증이 생략되어 경고가 나오지 않습니다.
- 변수 타입별 UI: 문자열=TextField, 정수=IntField, 실수=FloatField, bool=Toggle, **이미지=[파일 선택]**(생성 시 자동으로 `POST /upload`로 업로드되어 파일명이 치환됨).
  - **참조 이미지(`image` 타입) 변수는 기본값이 비어 있습니다.** 현재 화면에 표시된 image 변수 중 하나라도 비어 있으면 "참조 이미지를 선택해주세요" 안내와 함께 **[생성] 버튼이 비활성화**됩니다 (`UI`의 참조 이미지, `StyleChange`의 원본·스타일 참조 등). [파일 선택]으로 이미지를 지정하세요. 토글로 숨겨진(사용하지 않는) image 변수는 검사 대상이 아닙니다.
- 긍정/부정 프롬프트 변수는 PromptSet 항목 선택 시 자동으로 채워지며, 직접 수정할 수 있습니다.

워크플로별 조정 변수 (ComfyUI.md 기준, 기본값은 원본 JSON 값):

| 워크플로 | 변수 |
|------|------|
| `GenerateImage` | #1 `ckpt_name`, #2/#3 `text`(긍정/부정), #5 `steps`/`cfg`/`sampler_name`, #6 `width`/`height`, #9 `clip_name`/`type`, #10 `vae_name`, #21 `value`(배경 제거), #24 `lora_name`, #25 `unet_name`, #27 `value`(Checkpoint 사용), #29 `value`(LoRA 사용) |
| `GenerateImageFlux` | #2/#3 `text`(긍정/부정), #5 `steps`/`cfg`/`sampler_name`, #6 `width`/`height`, #9 `clip_name`/`type`, #10 `vae_name` |
| `UI` | #8/#7 `text`(긍정/부정), #22 `value`(참조 이미지 사용), #17 `image`(참조 이미지, 업로드), #16 `width`/`height`, #5 `steps`/`cfg` |
| `StyleChange` | #4/#38 `text`(긍정/부정), #16 `image`(원본, 업로드), #18 `image`(스타일 참조, 업로드), #8 `steps`/`cfg`/`denoise` |
| `Audio` | #3/#4 `text`(긍정/부정), #5 `steps`/`cfg`, #9 `seconds`, #11 `value`(SFX 모델 사용) |

### 3) 생성 → 선택 → 확정

1. **PromptSet JSON** 드롭다운(최신 파일 기본)에서 문서를 [로드]하고 **항목**을 선택합니다 (항목 선택 시 프롬프트 변수 자동 채움).
2. **[후보 4개 생성]** — 제출 전에 **사전 검증(preflight)** 을 수행해 누락된 커스텀 노드와 ComfyUI에 없는 파일/값(모델 파일명 등)이 있으면 원인·조치를 담은 다이얼로그를 띄우고 생성을 중단합니다. 통과하면 무작위 기준 시드부터 `seed..seed+3` 4건을 브리지 서버가 큐잉/폴링합니다(설정 `candidateCount`). 진행률 바가 표시되고 [취소]할 수 있으며 에디터는 멈추지 않습니다. 결과는 `Assets/Generated/Candidates/{항목id}/{시드}.png` + 메타 `{시드}.json`(시드/프롬프트/워크플로/대상 정보)로 저장됩니다.
3. 썸네일을 클릭해 선택 후 **[확정]** — `Assets/Generated/Images/{항목id}.png`(오디오는 `Audio/`)로 복사되고 `Assets/Generated/GenerationResults.json`에 기록됩니다. **[재생성]** 시 기존 후보를 삭제하고 새 시드로 다시 4장을 생성합니다.
4. **확정 결과 표시** — 확정된 항목은 목록 행에 소형 썸네일과 `확정됨` 배지가 표시되고, 항목 선택 시 왼쪽 하단에 확정 에셋 미리보기(오디오는 아이콘+파일명)와 경로가 표시됩니다. 썸네일/[에셋 위치 보기 (Ping)] 클릭 시 프로젝트 창에서 해당 에셋을 핑합니다. 확정 상태는 `Assets/Generated/GenerationResults.json` 기록으로 에디터 재시작 후에도 유지되며, 확정 에셋이 삭제된 경우 "확정 에셋 없음(삭제됨)" 안내가 표시됩니다.
5. **임포트 자동 설정** — 확정된 이미지는 TextureImporter로 **Sprite(2D and UI)** + `alphaIsTransparency` + PPU(`spritePixelsPerUnit`)가 적용됩니다. 단, 대상 프리팹의 대상 오브젝트가 **RawImage**인 항목만 Texture(Default)를 유지합니다.

항목 종류별 기본 워크플로: 오디오 항목 → `Audio`, UI 항목 → `UI`, 그 외 이미지 → 설정의 `defaultImageWorkflow`(기본 `GenerateImage`).

## 4단계 — 적용 (AssetApplier) 사용법

메뉴: **Tools/MCP/4. Asset Applier**. 3단계에서 확정한 결과물을 AssetList에 기록된 대상 프리팹 또는 씬(`targetScenePath` 항목)의 컴포넌트에 적용합니다. 프리팹 항목은 씬 인스턴스가 아닌 **프리팹 에셋 자체**를 수정하며, 적용 후 Ctrl+Z로 되돌릴 수 있습니다. 씬 항목은 씬에 직접 배치된 오브젝트에 적용합니다 — 씬이 이미 열려 있으면 그대로 값만 변경(dirty 표시, Ctrl+Z 가능, 저장은 사용자 몫), 열려 있지 않으면 Additive로 열어 적용·저장 후 닫습니다. 일괄 적용 시 같은 씬 항목은 묶어서 씬을 한 번만 엽니다.

1. **AssetList JSON** 드롭다운(최신 파일 기본)에서 1단계 산출물을 [로드]합니다.
2. 항목 목록에 상태 배지가 표시됩니다 — `확정본 없음`(3단계 미확정) / `검증 실패`(사유는 항목 선택 시 표시) / `적용 준비` / `적용됨`.
3. 항목을 선택하면 대상 정보(프리팹·내부 경로·기대 컴포넌트·확정본)와 **미리보기(현재 값 → 새 확정본)** 가 표시됩니다.
   - **적용 대상 수정**: 상세의 "적용 대상 (수정 가능)" 박스에서 대상을 직접 수정할 수 있습니다 — 프리팹 항목은 **프리팹 ObjectField**(변경 시 에셋 경로 자동 반영, 경로 표시)와 **내부 경로 드롭다운**(프리팹 계층 경로 목록, `(루트)` 포함 — 현재 값이 계층에 없으면 텍스트 필드 + 선택 드롭다운), 씬 항목은 내부 경로 텍스트 필드, 오디오 항목은 `targetComponent`/`targetField` 텍스트 필드를 제공합니다. 수정 즉시 검증이 다시 실행되어 배지/사유가 갱신되고, 박스의 **[저장]** 버튼으로 로드했던 AssetList JSON 파일에 수정 값이 기록됩니다 (저장 전에는 "수정됨" 표시).
4. **[선택 적용]** 또는 **[일괄 적용(검증 통과 항목 전체)]** — 적용 후 성공/실패 요약(실패 사유 포함)이 표시되고 `AssetDatabase.SaveAssets`로 마무리됩니다.

적용 대상 컴포넌트 판정: 오디오 항목 → `AudioSource.clip`, UI 항목 → `Image.sprite` 또는 `RawImage.texture`(대상 오브젝트의 실제 컴포넌트 기준), 그 외 이미지 → `SpriteRenderer.sprite`. 적용 전 프리팹 존재·내부 오브젝트 경로·컴포넌트 존재·에셋 임포트 타입(Sprite/Texture2D/AudioClip)을 검증하고, 실패 항목은 사유와 함께 표시됩니다.

**오디오 임의 필드 적용**: 오디오 항목에 `targetComponent`(컴포넌트 타입 이름, 사용자 MonoBehaviour 포함)와 `targetField`(직렬화 필드 경로)를 함께 지정하면 `AudioSource.clip` 대신 해당 컴포넌트의 직렬화된 AudioClip 필드에 적용합니다 — 예: `[SerializeField] AudioClip jumpSound`를 코드에서 `PlayOneShot`으로 재생하는 경우. 적용 전 컴포넌트 존재, 필드 존재, 필드가 AudioClip을 받는 ObjectReference인지 검증하며, 미리보기의 "현재 값"도 해당 필드 값을 표시합니다. 두 필드가 없으면 기존 `AudioSource.clip` 동작이 그대로 유지됩니다.

확정본 자동 탐색: `Assets/Generated/GenerationResults.json`의 `outputPath` 기록을 우선 사용하고, 없으면 `Assets/Generated/Images/{항목id}.png`(오디오는 `Audio/{항목id}.flac|wav|mp3|ogg`)를 찾습니다.

## 파이프라인 통합 창 (All-in-One)

메뉴: **`Tools/MCP/Pipeline (All-in-One)`**

4단계를 한눈에 보여주는 **스텝퍼** 창입니다. 개별 단계 창의 UI를 재구현하지 않고, 각 단계의 진행 상태를 배지로 표시하고 해당 단계 창으로 안내·연결합니다.

- **상태 배지** — 각 단계는 산출물 존재로 상태를 판정합니다: `미실행` / `준비`(앞 단계 산출물이 있어 시작 가능) / `완료`. 판정 근거는 1단계=최신 `AssetList_*.json`, 2단계=최신 `PromptSet_*.json`, 3단계=`GenerationResults.json`의 확정 항목(1건 이상), 4단계=확정본 유무(적용 여부는 안내 수준).
- **단계별 창 열기** — 각 단계의 `[N단계 창 열기]` 버튼은 항상 활성화되어 있어 **중간 단계부터 재시작**할 수 있습니다(기존 창을 그대로 엽니다).
- **산출물 자동 연결** — 1단계 완료 시 최신 AssetList 경로가 2단계 입력으로, 2단계 완료 시 최신 PromptSet 경로가 3단계 입력으로 표시됩니다. 창에 포커스가 돌아올 때마다 디스크를 다시 스캔해 상태를 갱신합니다.
- 상단 `[상태 새로고침]` / `[설정 열기]` 버튼을 제공합니다.

MCP 도구로 후반부를 자동화하려면 아래 `mcptools_run_pipeline`을, 현황 진단은 `mcptools_status`를 사용하세요.

## MCP 도구 사용법

모든 도구의 반환 공통 포맷: `{ "success": bool, "message": string, "data": {...} }`. 실패 시 `success:false` + `message`에 한국어 원인 안내. 경로는 전부 `Assets/` 기준 상대 경로 문자열입니다.

### `mcptools_ping` — 연결 진단

- 파라미터: 없음
- 반환 `data`: `{ version, unityVersion, serverUrl }`

### `mcptools_asset_scan` — 1단계 분석 입력 수집

AI(MCP 클라이언트)가 목록을 작성할 재료를 반환합니다. 항목 작성은 도구가 아니라 AI가 수행합니다.

- 파라미터:
  - `designDocPath: string` (선택) — 기획서 파일 경로 (`.md`/`.txt`). 파일이 없으면 오류.
  - `scanRootPath: string` (선택, 기본 `"Assets"`) — 프리팹 슬롯 스캔 루트.
  - `scenePaths: string[]` (선택) — 씬 직접 배치 오브젝트 슬롯을 스캔할 씬 경로 목록 (`Assets/` 상대). 지정한 씬만 스캔하며, 프리팹 인스턴스 소속 오브젝트는 제외됩니다.
  - `scanOnly: bool` (선택, 기본 `false`) — `true`면 기획서에서 항목을 추출하지 않고 **현재 열려 있는 씬의 슬롯 + `scanRootPath` 아래 모든 프리팹의 슬롯**을 병합해 스캔합니다 (`scenePaths`는 무시). 씬에 포함된 프리팹과 스캔 루트 프리팹이 겹치면 프리팹 단위로 한 번만 담기며, 열린 씬이 비어 있어도 프리팹 스캔 결과로 목록이 만들어집니다. 반환 `data`에 대상 경로가 채워진 완성 `items` 배열이 포함되어 그대로 `mcptools_asset_list_save`에 전달할 수 있습니다. `designDocPath`는 이때도 그대로 읽혀 `designDocPath`/`designDocText`로 반환되고 items 문서의 컨텍스트로 기록되니, 저장 시 `mcptools_asset_list_save`의 `designDocPath`에도 함께 넘겨 2단계에서 참고하게 하세요.
- 반환 `data`:
  - `designDocPath`, `designDocText` — 기획서를 지정한 경우에만 포함 (원문 전체)
  - `scanRootPath`
  - `scanEntries` — 슬롯 목록: `{ prefabPath, scenePath, objectPath, componentType, currentAssetName, isUI }[]` (Image/RawImage/SpriteRenderer/AudioSource 수집. `prefabPath`/`scenePath`는 상호 배타 — 씬 직접 배치 슬롯은 `scenePath`가 채워짐)
  - `itemSchema` — 항목 필드 스키마 (필드명 → 한국어 설명)
  - `instructions` — 목록 작성 지침 (MCP 클라이언트가 프로젝트 파일을 직접 읽을 수 있으면 스크립트·씬·프리팹을 참고해 역할을 추론하라는 안내 포함)

### `mcptools_asset_list_save` — 1단계 목록 검증·저장

- 파라미터:
  - `items: object[]` (필수) — itemSchema 형식의 항목 객체 배열. 유효 항목이 없으면 오류. `id` 생략 시 `item_001` 형식으로 자동 부여.
  - `outputPath: string` (선택) — 저장 경로. 생략 시 `Assets/Docs/AssetList_{yyyyMMdd_HHmm}.json`.
  - `designDocPath: string`, `scanRootPath: string` (선택) — 문서 메타 기록용.
- 반환 `data`: `{ outputPath, itemCount, warnings }` — `warnings`는 대상 프리팹/UI 여부 미기록 항목 안내 목록. **경고가 있어도 저장은 수행됩니다** (에디터 창에서 후속 보완).

### `mcptools_prompt_scan` — 2단계 프롬프트 재료 수집

AI(MCP 클라이언트)가 프롬프트를 작성할 재료를 반환합니다. 프롬프트 작성은 도구가 아니라 AI가 수행합니다.

- 파라미터:
  - `assetListPath: string` (필수) — 1단계 산출물 AssetList JSON 경로 (`Assets/` 상대). 파일이 없거나 항목이 없으면 오류.
  - `templateName: string` (선택, 기본 `"default"`) — 프롬프트 템플릿 이름.
- 반환 `data`:
  - `assetListPath`
  - `assetItems` — 1단계 목록 항목: `{ id, name, description, assetType, targetPrefabPath, targetObjectPath, isUI }[]`
  - `template` — 템플릿 값: `{ templateName, stylePrefix, qualityTags, commonNegative, uiExtraTags, audioStylePrefix, audioNegative }`
  - `promptSchema` — 프롬프트 항목 필드 스키마 (필드명 → 한국어 설명)
  - `instructions` — 작성 지침 (UI 특화 태그 부여, 오디오는 이미지 태그 금지, 프로젝트 파일 참고 안내 포함)

### `mcptools_prompt_save` — 2단계 프롬프트 검증·저장

- 파라미터:
  - `items: object[]` (필수) — promptSchema 형식의 항목 객체 배열. 유효 항목이 없으면 오류. `id` 생략 시 자동 부여.
  - `assetListPath: string`, `templateName: string` (선택) — 문서 메타 기록용.
  - `outputPath: string` (선택) — 저장 경로. 생략 시 `Assets/Docs/PromptSet_{yyyyMMdd_HHmm}.json`.
- 반환 `data`: `{ outputPath, itemCount, warnings }` — `warnings`는 빈 positive 프롬프트/대상 미기록 항목 안내 목록. **경고가 있어도 저장은 수행됩니다.**

### `mcptools_generate_candidates` — 3단계 후보 생성 Job 시작

생성은 수십 초~수 분이 걸리므로 **Job 방식**입니다: 이 도구는 Job을 시작하고 즉시 반환하며, 완료 여부는 `mcptools_list_candidates`로 폴링합니다.

- 파라미터:
  - `promptSetPath: string` (필수) — 2단계 PromptSet JSON 경로 (`Assets/` 상대).
  - `assetItemId: string` (필수) — 생성할 항목 id.
  - `workflowName: string` (선택) — 워크플로 이름 (`GenerateImage` | `GenerateImageFlux` | `UI` | `StyleChange` | `Audio`). 생략 시 항목 종류별 자동 선택.
  - `variables: object` (선택) — `{"nodeId.field": 값}` 형태의 워크플로 변수 덮어쓰기 (예: `{"5.steps": 20, "6.width": 512}`). 사용 가능한 변수는 위 워크플로별 조정 변수 표 참조.
  - `baseSeed: long` (선택) — 기준 시드. 생략 시 무작위.
- 반환 `data`: `{ status: "started", assetItemId, candidateFolder }`
- 같은 항목의 Job이 이미 실행 중이면 오류를 반환합니다. 기존 후보는 삭제 후 새로 생성됩니다.

### `mcptools_list_candidates` — 후보 목록/Job 상태 조회

- 파라미터: `assetItemId: string` (필수)
- 반환 `data`: `{ status: "running" | "completed" | "failed" | "idle", message, candidates: [{ path, seed }] }`
  - `running` — 생성 중 (candidates에는 지금까지 저장된 파일만 포함될 수 있음)
  - `completed` / `failed` — 완료/실패 (`message`에 실패 원인)
  - `idle` — 이 에디터 세션에 Job 기록 없음. 디스크의 기존 후보를 반환

### `mcptools_select_candidate` — 후보 확정

- 파라미터: `assetItemId: string` (필수), `candidatePath: string` (필수, list가 반환한 후보 경로)
- 반환 `data`: `{ selectedPath }` — `Assets/Generated/Images/`(오디오는 `Audio/`)로 복사된 확정본 경로
- 창의 [확정]과 동일한 공용 로직: `GenerationResults.json` 기록 + 이미지 항목 Sprite 임포트 자동 설정(RawImage 대상 제외).

### `mcptools_apply_asset` — 4단계 단건 적용

- 파라미터:
  - `assetListPath: string` (필수) — 1단계 AssetList JSON 경로 (`Assets/` 상대).
  - `assetItemId: string` (필수) — 적용할 항목 id.
  - `assetPath: string` (선택) — 적용할 에셋 경로. 생략 시 확정본 자동 탐색 (`GenerationResults.json` → 규칙 경로).
- 반환 `data`: `{ prefabPath, scenePath, objectPath, appliedAssetPath }`
- 검증 실패(프리팹·씬/내부 경로/컴포넌트 없음, 임포트 타입 불일치 등) 시 `success:false` + `message`에 사유.
- 항목의 `targetScenePath`가 채워져 있으면 자동으로 **씬 적용**으로 분기합니다: 씬이 이미 열려 있으면 그대로 값 변경 후 dirty 표시(저장은 사용자 몫, Ctrl+Z 가능), 열려 있지 않으면 Additive로 열어 적용·저장 후 닫습니다.

### `mcptools_apply_all` — 4단계 일괄 적용

- 파라미터: `assetListPath: string` (필수)
- 반환 `data`: `{ applied: [{ id, prefabPath, scenePath, objectPath, appliedAssetPath }], failed: [{ id, reason }] }`
- 씬 항목은 같은 씬끼리 묶어 씬을 한 번만 열어 처리합니다.
- 확정본이 없거나 검증에 실패한 항목은 적용하지 않고 `failed`에 사유와 함께 담깁니다. 완료 후 `AssetDatabase.SaveAssets` 수행.

### `mcptools_run_pipeline` — 후반부(3~4단계) 자동화

2단계 산출물(PromptSet)을 입력으로 3단계 생성 → 확정 → 4단계 적용을 순차 실행합니다. **1·2단계(AssetList/PromptSet 작성)는 AI 중립 설계상 사전에 AI로 작성되어 있어야 합니다** — 이 도구의 입력은 기획서가 아니라 이미 작성된 `promptSetPath`입니다.

- 파라미터:
  - `promptSetPath: string` (필수) — 2단계 PromptSet JSON 경로 (`Assets/` 상대). 없으면 오류.
  - `autoSelect: string` (선택, 기본 `"first"`) — `"first"`: 각 항목의 후보 중 **가장 낮은 시드**를 확정한 뒤 대상에 일괄 적용. `"none"`: 후보만 생성하고 확정/적용은 하지 않음.
  - `workflowName: string` (선택) — 워크플로 이름 (`GenerateImage` | `GenerateImageFlux` | `UI` | `StyleChange` | `Audio`). 생략 시 항목 종류별 자동 선택.
- 반환 `data`: `{ promptSetPath, assetListPath, pendingSelections, applied, failed }`
  - `pendingSelections` — `autoSelect="none"`에서 채워짐: `[{ assetItemId, candidates: [{ path, seed }] }]`. 이후 `mcptools_select_candidate` + `mcptools_apply_asset`으로 진행.
  - `applied` — `autoSelect="first"`에서 채워짐: `[{ id, prefabPath, scenePath, objectPath, appliedAssetPath }]`.
  - `failed` — 항목 단위 실패(생성/확정/적용): `[{ id, reason }]`. 부분 성공을 지원합니다.
  - 적용 대상 정보는 PromptSet의 `assetListPath` 메타로 AssetList를 로드해 얻습니다. `assetListPath`가 비어 있거나 파일이 없으면 확정본은 생성되지만 적용은 건너뛰고 `failed`에 안내됩니다.
- **주의(동기 처리)**: 각 항목의 생성을 스레드풀에서 시작해 순차 완료를 대기하므로, **생성이 끝날 때까지 에디터 메인 스레드가 블로킹됩니다**(수 초~수 분). 창(3단계)은 Job 방식으로 멈추지 않지만, run_pipeline은 결과를 한 번에 반환하기 위해 이 블로킹을 감수합니다. 에디터를 멈추지 않고 진행하려면 3단계 창 또는 `mcptools_generate_candidates`(Job) 경로를 사용하세요.

### `mcptools_status` — 진단

- 파라미터: 없음
- 반환 `data`:
  - `version`, `unityVersion`
  - `config` — `{ comfyUIServerUrl, bridgeServerUrl, generatedRootPath, docsRootPath, defaultImageWorkflow, candidateCount }`
  - `outputs` — `{ assetListCount, latestAssetList, promptSetCount, latestPromptSet, imageCount, audioCount, candidateFolderCount, confirmedCount }`
  - `serverHealthNote` — 서버 실시간 연결 확인은 하지 않는다는 안내(동기 블로킹 방지). 연결 확인은 3단계 창 또는 브리지 `/health` 참조.

### 산출물 형식 — PromptSet JSON 스키마

```json
{
  "assetListPath": "Assets/Docs/AssetList_20260721_1200.json",
  "templateName": "default",
  "createdAt": "2026-07-21 13:00",
  "items": [
    {
      "id": "item_001",
      "name": "타이틀 로고",
      "assetType": "ui",
      "isUI": true,
      "targetPrefabPath": "Assets/Prefabs/TitleCanvas.prefab",
      "targetObjectPath": "Canvas/TitlePanel/Logo",
      "description": "타이틀 화면 상단 로고, 밝은 판타지 분위기",
      "positive": "game asset illustration, ..., clean edges, transparent background, game ui icon, ...",
      "negative": "text, watermark, signature, blurry, ..."
    }
  ]
}
```

## 산출물 형식 — AssetList JSON 스키마

```json
{
  "designDocPath": "Assets/Docs/기획서.md",
  "scanRootPath": "Assets",
  "createdAt": "2026-07-21 12:00",
  "items": [
    {
      "id": "item_001",
      "name": "타이틀 로고",
      "description": "타이틀 화면 상단 로고, 밝은 판타지 분위기",
      "assetType": "image",
      "targetPrefabPath": "Assets/Prefabs/TitleCanvas.prefab",
      "targetObjectPath": "Canvas/TitlePanel/Logo",
      "isUI": true,
      "isUISpecified": true,
      "status": "pending"
    }
  ]
}
```

| 필드 | 설명 |
|------|------|
| `id` | 항목 고유 ID (`item_001` 형식, 생략 시 자동 부여) |
| `name` | 에셋 이름 (필수) |
| `description` | 설명/용도 — 2단계 프롬프트 제작에 쓰이므로 구체적으로 |
| `assetType` | `"image"` \| `"ui"` \| `"audio"` |
| `targetPrefabPath` | 적용 대상 프리팹 경로 (Assets/ 상대). 빈 문자열이면 미지정 |
| `targetObjectPath` | 프리팹 내부 GameObject 계층 경로 |
| `targetComponent` | (선택, 오디오 전용) 적용 대상 컴포넌트 타입 이름 (예: `"PlayerController"`, 사용자 MonoBehaviour 가능). `targetField`와 함께 지정 시 AudioSource.clip 대신 해당 컴포넌트의 AudioClip 필드에 적용 |
| `targetField` | (선택, 오디오 전용) 적용 대상 직렬화 필드 경로 (예: `"jumpSound"`). `targetComponent`와 반드시 함께 지정 |
| `isUI` | UI 여부 (uGUI 요소면 true) |
| `isUISpecified` | UI 여부가 실제로 지정되었는지 (false면 `isUI` 값 무시, 창에서는 "미지정") |
| `status` | 항목 상태 — `"pending"`(기본) / `"대상 미정"`(대상 미기록 상태로 저장됨) 등, 이후 단계에서 갱신 |

## 공통 구성요소

### AiCliRunner (`Editor/Common/AiCliRunner.cs`)

PC에 설치된 AI CLI를 감지·실행하는 공용 유틸리티로, 이후 단계(PromptBuilder 등)에서도 동일하게 재사용됩니다.

- **감지**: PATH 탐색(Windows `where.exe` / macOS·Linux `which`). Windows에서는 `.ps1` 스크립트 CLI(Copilot CLI 등)를 위해 `where.exe <name>.ps1` 재시도 + PATH 디렉터리 직접 순회(`.exe`/`.cmd`/`.bat`/`.ps1`) 폴백. 결과는 캐시되며 [다시 검색]으로 갱신. 지원 목록: `claude`, `codex`, `gemini`, `cursor-agent`, `copilot` + 직접 입력 커맨드.
- **실행**: 비대화형(헤드리스) 실행, 프롬프트는 원칙적으로 stdin 파이프로 전달 (claude `-p`, codex `exec -`, gemini stdin, cursor-agent `-p --output-format text`; copilot은 임시 파일 방식). `.cmd`/`.bat`은 `cmd.exe`, `.ps1`은 `powershell.exe -File` 경유. UTF-8 인코딩 명시(한글 대응).
- **타임아웃/취소**: 기본 300초(0 이하 지정 시 300초로 보정), CancellationToken 취소 시 프로세스 강제 종료.
- **프로젝트 탐색 모드**: `allowReadTools`/`workingDirectory` 파라미터로 프로젝트 루트에서 읽기 전용 도구만 허용해 실행 (claude는 `--allowedTools "Read Glob Grep"`, 나머지는 기본 샌드박스가 읽기 전용 원칙을 충족하거나 보수적으로 플래그 생략). 파일 쓰기/셸 실행 도구는 어떤 CLI에서도 허용하지 않습니다.

### 기타

- `McpToolRegistry` — MCP 도구 이름→핸들러 레지스트리. unity-mcp 브리지 의존은 별도 어셈블리 `Editor/McpForUnityBridge/`에 격리.
- `MiniJson` — 외부 패키지 없는 JSON 직렬화/역직렬화.
- `ComfyUIClient` — ComfyUI 직접 호출 REST 래퍼 (설정 창의 연결 테스트용).
- `BridgeClient` — 브리지 서버 REST 래퍼 (3단계 생성/업로드/다운로드용).

## 사용자 확장

UPM으로 설치한 패키지 폴더(`Packages/...`)는 **읽기 전용**입니다. 그래서 사용자가 추가·수정하는 것들은 모두 프로젝트 쪽 **`Assets/MCPTools.User/`** 아래에 둡니다.

| 용도 | 위치 | 만드는 방법 |
|------|------|-------------|
| 설정 에셋 | `Assets/MCPTools.User/MCPToolSettings.asset` | `Tools/MCP/*` 첫 사용 시 자동 생성 |
| 프롬프트 템플릿 | `Assets/MCPTools.User/Templates/<이름>.json` | 폴더를 만들고 JSON 파일을 직접 추가 |
| 워크플로 JSON·`variables.json` 사본 | `Assets/MCPTools.User/ComfyUI/workflows/*.json`, `Assets/MCPTools.User/ComfyUI/variables.json` | 설정 창의 **[워크플로를 프로젝트로 복사]** 버튼 |

> ⚠️ **패키지 폴더 안의 파일을 직접 고치지 마세요.** 패키지 재해결(버전 변경·제거 후 재설치·PackageCache 재생성) 시 **경고 없이 원본으로 되돌아가 수정 내용이 사라집니다.** 아래 방법으로 프로젝트 쪽에 두면 패키지를 업데이트해도 유지됩니다.

### 프롬프트 템플릿 추가

1. 프로젝트에 `Assets/MCPTools.User/Templates/` 폴더를 만듭니다.
2. `<이름>.json` 파일을 만들고 바꾸고 싶은 필드만 적습니다 (생략한 필드는 기본값 유지).

   ```json
   {
     "stylePrefix": "pixel art, 16-bit game asset",
     "qualityTags": "crisp pixels, limited palette",
     "uiExtraTags": "transparent background, centered composition"
   }
   ```

3. 2단계 창의 **템플릿** 드롭다운에 파일명(확장자 제외)이 나타납니다. 패키지 동봉 템플릿과 이름이 같으면 **사용자 폴더 쪽이 우선**합니다.

사용 가능한 필드: `stylePrefix`, `qualityTags`, `commonNegative`, `uiExtraTags`, `audioStylePrefix`, `audioNegative`.

### 워크플로·모델 교체

- **모델 파일만 바꾸는 경우 — JSON 수정 불필요.** 3단계 창의 **변수 UI**에서 `ckpt_name`/`unet_name`/`lora_name`/`clip_name`/`vae_name` 값을 설치된 파일명으로 바꾸면 됩니다. ComfyUI에 연결되어 있으면 설치된 파일 목록이 드롭다운으로 제공됩니다.
- **워크플로 구조를 바꾸거나 워크플로를 추가하는 경우** — 설정 창(`Tools/MCP/Settings`)의 **[워크플로를 프로젝트로 복사]** 를 눌러 패키지 동봉본을 `Assets/MCPTools.User/ComfyUI/`로 복사한 뒤, 그 사본의 JSON을 편집합니다.
  - 브리지 서버는 `Assets/MCPTools.User/ComfyUI/workflows/<이름>.json`이 있으면 **패키지 동봉본보다 우선** 로드하고, 사본에만 있는 이름은 워크플로 목록에 추가로 나타납니다.
  - `variables.json`은 **병합하지 않고** 사본이 있으면 사본을 통째로 사용합니다. 변수 매니페스트를 손볼 때는 사본 쪽에 모든 워크플로 정의가 들어 있어야 합니다.
  - 복사·편집 후에는 **브리지 서버를 재시작**해야 반영됩니다 ([서버 종료] → [서버 시작]).
- 워크플로 JSON은 **ComfyUI API Format**(노드 id → `class_type`/`inputs`)이어야 합니다. ComfyUI 웹 UI에서 워크플로를 내보낼 때 "Export (API)"로 저장하세요.

## 문제 해결

- **패키지 설치가 "git executable not found"로 실패** — UPM의 git URL 설치는 사용자 PC의 `git`(2.14 이상)을 호출합니다. [git](https://git-scm.com/downloads)을 설치한 뒤 **Unity 에디터와 Unity Hub를 모두 종료했다 다시 실행**하세요(실행 중인 프로세스는 옛 PATH를 계속 사용합니다). 설치 후 `git --version`이 터미널에서 동작하는지 확인하면 확실합니다.
- **`Assembly with name 'MCPTools.Editor' already exists` 컴파일 오류** — `Assets/` 아래에 예전 설치본(`MCPTools` 폴더)이 남은 채 패키지를 추가한 경우입니다. **`Assets/` 쪽 `MCPTools` 폴더를 삭제**하세요. `Assets/MCPTools.User/`는 사용자 데이터이므로 **지우면 안 됩니다.** → [설치](#설치)
- **설정이 저장되지 않거나 값이 되돌아감** — 설정 에셋이 여러 개인지 확인하세요. 콘솔에 "설정 에셋이 N개 발견되어 첫 번째를 사용합니다" 경고와 경로 목록이 출력됩니다. 사용하지 않는 에셋을 지우면 됩니다. 정상 위치는 `Assets/MCPTools.User/MCPToolSettings.asset`입니다(구버전 설치본의 `Assets/MCPTools/Editor/Common/MCPToolSettings.asset`도 그대로 사용됨).
- **패키지를 업데이트했더니 직접 고친 워크플로/템플릿이 사라짐** — 패키지 폴더는 재해결 시 원본으로 되돌아갑니다. 수정본은 `Assets/MCPTools.User/` 아래에 두세요. → [사용자 확장](#사용자-확장)
- **MCP 도구가 목록에 안 보임** — unity-mcp 패키지 설치 여부와 `MCPTOOLS_HAS_MCPFORUNITY` 심볼 정의(패키지 설치 시 자동)를 확인하고, 파라미터 스키마 변경 후에는 MCP 서버를 재시작하세요.
- **AI CLI가 감지되지 않음** — 해당 CLI가 PATH에 있는지 확인하거나 [직접 입력...]으로 커맨드를 지정하고, 그래도 안 되면 수동 방식(프롬프트 복사/JSON 붙여넣기)을 사용하세요.
- **AI 실행 실패** — CLI 로그인 상태와 네트워크를 확인하고, 타임아웃이면 타임아웃(초)을 늘려 다시 시도하세요.
- **브리지 서버가 시작되지 않을 때 ([서버 시작] 실패)** — 아래를 순서대로 확인하세요.
  1. **Python 미설치 / PATH 미등록** — 터미널에서 `python --version`(또는 `py -3 --version`)이 3.7 이상을 출력하는지 확인하세요. [python.org](https://www.python.org/downloads/) 설치 시 **"Add python.exe to PATH"** 체크를 켜야 합니다.
  2. **Unity 실행 중에 Python을 설치함** — 이미 실행 중인 프로세스는 설치 전의 옛 PATH를 계속 사용합니다. **Unity 에디터와 Unity Hub를 모두 종료했다가 다시 실행**하세요.
  3. **Windows 스토어 앱 실행 별칭** — `python`을 실행하면 Microsoft Store 창만 뜨고 서버가 안 뜨는 경우입니다. **설정 > 앱 > 고급 앱 설정 > 앱 실행 별칭**에서 `python.exe` / `python3.exe`를 끄세요. (도구의 자동 탐지는 이 스텁을 검증 단계에서 걸러내지만, 설정에 스텁 경로를 직접 지정한 경우 문제가 됩니다.)
  4. **포트 충돌** — 브리지 서버가 시작 직후 종료되면 대부분 `bridgeServerUrl`의 포트(기본 8189)를 이미 다른 프로세스가 쓰고 있는 경우입니다. 이전에 띄운 브리지 콘솔 창을 닫거나 설정에서 포트를 바꾸세요.
  5. **직접 지정 / 자동 탐지** — `Tools/MCP/Settings`의 **Python 실행 파일**에 `python.exe` 절대 경로를 입력하거나, 같은 창의 **[Python 자동 탐지]** 버튼을 눌러 자동으로 채우세요(찾은 경로와 버전이 다이얼로그로 표시됩니다).
  6. **로그 확인** — "브리지 콘솔 창 표시"가 꺼져 있으면 서버 로그는 `%TEMP%/mcptools_bridge_server.log`(시스템 임시 폴더)에 기록됩니다. 켜두면 콘솔 창에서 오류를 바로 볼 수 있습니다.
- **브리지 서버는 실행 중인데 ComfyUI 미연결** — ComfyUI 자체는 브리지가 실행해주지 않습니다. ComfyUI를 별도로 실행한 뒤 상태 표시가 갱신될 때까지 잠시 기다리세요.
- **"생성 사전 검증 실패" 다이얼로그 (커스텀 노드·모델 누락)** — 생성 전 preflight가 현재 ComfyUI 환경에서 실행할 수 없는 워크플로를 걸러낸 것입니다.
  - **설치되지 않은 커스텀 노드 N개** — 목록의 노드를 [ComfyUI-Manager](https://github.com/ltdrdata/ComfyUI-Manager)로 설치하고 ComfyUI를 재시작하세요. 목록에 나온 이름이 `ComfyUI-Inspyrenet-Rembg`/`ComfySwitchNode` 계열이 아니라면 **ComfyUI 코어 노드**일 수 있으니 ComfyUI를 최신 버전으로 업데이트해보세요 (위 [요구 사항](#요구-사항)의 버전 항목 참조).
  - **ComfyUI에 없는 파일/값 N건** — 워크플로가 참조하는 모델 파일이 없는 경우입니다. 안내에 함께 표시되는 "설치된 값 예시"를 보고 3단계 창의 변수 UI에서 드롭다운으로 실제 설치된 파일명을 선택하세요. 설치된 값이 하나도 없다면 해당 종류의 모델 파일을 ComfyUI의 `models/` 하위 폴더에 먼저 넣어야 합니다.
  - 워크플로를 선택했을 때 창 상단에 **누락 커스텀 노드 경고**가 이미 표시되므로, 생성 전에 확인할 수 있습니다. ComfyUI에 연결되지 않은 상태에서는 검증이 생략됩니다.
- **[생성] 버튼이 비활성화됨** — 두 가지 원인이 있습니다. ① 브리지 서버가 실행 중이 아니어서 워크플로 목록이 비어 있음 → 상단 [서버 시작]. ② **참조 이미지(image 변수)가 지정되지 않음** → 변수 UI의 [파일 선택]으로 이미지를 지정하세요 (`UI`·`StyleChange` 워크플로 등, 안내 메시지에 어떤 변수가 비었는지 표시됩니다).
- **생성 시 워크플로 거부 (HTTP 400)** — 사전 검증을 통과했는데도 거부되면, 워크플로가 요구하는 모델/커스텀 노드(위 "요구 사항" 참조)가 ComfyUI에 설치되어 있는지 확인하세요. 오류 메시지에 ComfyUI의 노드 검증 응답 본문이 포함됩니다. 모델 파일명이 다르면 변수 UI에서 파일명을 바꾸세요.
- **생성이 타임아웃됨** — 첫 생성은 모델 로드로 오래 걸릴 수 있습니다. 브리지 서버의 Job 제한은 설정의 `jobTimeoutSeconds`(기본 600초)이며 **변경 후 브리지 서버 재시작**이 필요합니다. 결과 다운로드 타임아웃은 `requestTimeoutSeconds`(기본 300초)입니다. 저사양 GPU라면 두 값을 함께 늘리세요.

## 버전과 라이선스

- 현재 버전: **0.1.0** (패키지 이름 `com.sungchan.mcptools`)
- 변경 이력: [CHANGELOG.md](CHANGELOG.md) — [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/) 형식, [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.
- 라이선스: **MIT** — 전문은 [LICENSE.md](LICENSE.md)를 참고하세요.
- 저장소·이슈: <https://github.com/chomul/MCPTools>
