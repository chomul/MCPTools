# CLAUDE.md

이 파일은 Claude Code가 이 프로젝트에서 작업할 때 따라야 할 가이드입니다.

## 프로젝트 개요

기획서 기반 AI 에셋 생성 파이프라인을 Unity Editor Tool + MCP 도구로 구축하는 프로젝트.
ComfyUI(로컬)로 이미지/사운드를 생성하고, 선택된 결과물을 프리팹과 UI에 자동 적용한다.

- Unity 프로젝트 경로: `MCPToolTest/`
- Unity 버전: **6000.5.2f1 (Unity 6)** — URP 17.5, uGUI, Input System 사용
- ComfyUI 로컬 서버: **http://127.0.0.1:8188** (기본 주소, 설정으로 변경 가능하게 구현)

## 작업 운영

- 사용자의 Task 실행 요청은 반드시 서브에이전트에 위임해 수행한다. 주 에이전트는 범위 지정, 결과 검토, 통합만 담당한다.
- Task 착수 전에 해당 Task의 체크리스트(`docs/checklist/TaskN_체크리스트.md`)를 확인하고, 없으면 먼저 생성한다. Task 정의는 `docs/tasks/`, 전체 계획은 `docs/PLAN.md`를 따른다.
- 모든 Task 완료 시 체크리스트의 해당 항목에 구현 결과, 검증 상태, 관련 파일을 기록하고 체크한다.
- 작업 완료 후 Unity 에디터에서 직접 확인해야 할 테스트 항목이 있으면, 해당 Task 체크리스트 항목 아래에 테스트 목록으로 함께 기록한다.
- 요청 범위 밖 파일, 생성물, 설정은 변경하지 않는다.
- 코드 작업 시 요청 범위에 필요하지 않은 클래스를 새로 만들지 않는다. 클래스 간 불필요한 의존성도 추가하지 않는다.

## 파이프라인 4단계

각 단계는 **독립된 Unity Editor Tool로 만들고 MCP 도구로 노출**한다. 단계별 산출물은 다음 단계의 입력이 된다.

| 단계             | 도구             | 입력                        | 출력                                                       |
| ---------------- | ---------------- | --------------------------- | ---------------------------------------------------------- |
| 1. 에셋 리스트업 | AssetListup      | 기획서 + 현재 프로젝트 스캔 | 생성할 이미지/UI 목록 문서 (적용 대상 프리팹/UI 경로 포함) |
| 2. 프롬프트 제작 | PromptBuilder    | 1단계 목록 문서             | ComfyUI 모델에 맞는 항목별 프롬프트                        |
| 3. 생성          | ComfyUIGenerator | 프롬프트 + Workflow JSON    | 이미지/사운드 후보 **4개** (사용자가 선택)                 |
| 4. 적용          | AssetApplier     | 선택된 결과물 + 대상 경로   | 프리팹/UI에 적용 완료된 에셋                               |

- 1단계 목록 문서에는 각 항목이 **어떤 프리팹에 적용될 이미지인지, UI인지**를 반드시 함께 기록한다.
- 3단계는 후보 4개를 생성해 보여주고 선택을 받는 구조로 만든다. 합당한 결과가 없으면 재생성한다.
- 각 단계별 Unity 툴을 생성하여 사용자가 테스트 할 수 있게 진행한 후 마지막에 툴을 통합하는 형식으로 진행한다.

## 폴더 구조 규칙

```
MCPToolTest/Assets/
├── MCPTools/            # ★ 배포 대상 — 이 폴더만으로 자기완결되어야 함
│   ├── Editor/          # Editor 전용 코드 (MCPTools.Editor.asmdef)
│   │   ├── AssetListup/
│   │   ├── PromptBuilder/
│   │   ├── ComfyUIGenerator/
│   │   └── AssetApplier/
│   └── Runtime/         # 런타임 코드 (MCPTools.Runtime.asmdef)
├── Generated/           # 생성된 에셋 저장 (프로젝트 작업 데이터, 배포 제외)
│   ├── Images/
│   ├── Audio/
│   └── Candidates/      # 3단계 후보 4개 임시 저장 (선택 후 정리)
└── Docs/                # 기획서, 에셋 리스트업 문서, 프롬프트 문서 (배포 제외)
```

## 코딩 규칙

1. **언어**: Unity Editor Tool은 C#으로 작성한다.
2. **Editor / Runtime 분리 (필수)**
   - Editor 코드는 `Assets/MCPTools/Editor/` 아래, `MCPTools.Editor.asmdef` (Include Platforms: Editor only) 소속.
   - Runtime 코드는 `Assets/MCPTools/Runtime/` 아래, `MCPTools.Runtime.asmdef` 소속. `UnityEditor` 네임스페이스 참조 금지.
   - Editor asmdef는 Runtime asmdef를 참조할 수 있지만 그 반대는 금지.
3. **메뉴 경로**: Editor 창/커맨드는 `Tools/MCP/<단계명>` 아래에 등록한다.
4. **네트워크 호출**: Editor에서 ComfyUI 호출은 `UnityWebRequest` + `EditorApplication.update` 폴링 또는 `async/await`를 사용한다. 에디터가 멈추지 않게 동기 블로킹 호출 금지.
5. **에셋 반영**: 파일 생성/수정 후 `AssetDatabase.Refresh()` 또는 `AssetDatabase.ImportAsset()`을 호출한다. 프리팹 수정은 `PrefabUtility` API를 사용하고 `Undo` 등록을 지원한다.
6. **설정값**: ComfyUI 주소, 모델명, 출력 경로 등은 하드코딩하지 말고 `ScriptableObject` 설정 에셋(예: `MCPToolSettings`)으로 관리한다.

## ComfyUI 연동 규칙

- 이미지 생성은 **ComfyUI API Format Workflow JSON**을 사용한다 (에디터 UI용 포맷이 아닌, `class_type`/`inputs` 키로 구성된 API 포맷).
- Workflow JSON 템플릿은 `Assets/MCPTools/Editor/ComfyUIGenerator/Workflows/`에 두고, 프롬프트/시드 등만 치환해서 사용한다.
- 주요 엔드포인트:
  - `POST /prompt` — workflow 제출, `prompt_id` 반환
  - `GET /history/{prompt_id}` — 완료 여부 및 출력 파일 정보 조회
  - `GET /view?filename=...&subfolder=...&type=output` — 결과 파일 다운로드
- 후보 4개는 시드를 바꿔 생성한다.

## MCP 도구화 규칙

- 각 파이프라인 단계는 unity-mcp를 통해 MCP 도구로 호출 가능해야 한다.
- 도구는 JSON 직렬화 가능한 파라미터/결과만 사용한다 (경로는 `Assets/` 기준 상대 경로 문자열).
- 하나의 도구는 하나의 단계만 책임진다. 단계를 건너뛰는 복합 도구를 만들지 않는다.

## 배포 고려사항

이 도구는 **GitHub + Unity Package Manager(git URL)** 로 다른 개발자에게 배포한다 (패키지 이름 `com.sungchan.mcptools`, 설치 URL은 `?path=MCPToolTest/Assets/MCPTools#vX.Y.Z` 형식). 모든 구현은 아래를 전제로 진행한다.

- **자기완결성**: 배포 단위는 `Assets/MCPTools/` 폴더 전체(= UPM 패키지 루트, `package.json` 포함)다. 이 폴더는 프로젝트 고유 에셋이나 외부 패키지에 의존하지 않아야 하며, git URL로 설치하는 것만으로 동작해야 한다. Workflow JSON 템플릿 등 필요한 리소스는 전부 이 폴더 안에 둔다. `.unitypackage`·zip 배포는 하지 않는다.
- **읽기 전용 패키지 전제**: UPM 설치 시 패키지 폴더(`Library/PackageCache/`)는 읽기 전용이다. 설치 폴더에 에셋을 생성·수정하는 코드를 넣지 않는다. 사용자가 편집·추가하는 파일(설정 에셋, 프롬프트 템플릿, 워크플로 사본)은 프로젝트 쪽 `Assets/MCPTools.User/`에 둔다. 설치 루트의 절대 경로가 필요하면 `Application.dataPath` 조합이 아니라 `PackageInfo.FindForAssetPath` 기반으로 해석한다.
- **경로/환경 독립**: 절대 경로나 특정 PC 환경(사용자명, 드라이브 문자 등)을 코드·에셋에 하드코딩하지 않는다. 경로는 항상 `Assets/` 기준 상대 경로를 사용하고, `Generated/` 같은 출력 경로는 폴더가 없으면 자동 생성한다.
- **설정 분리**: `MCPToolSettings`는 배포 시 그대로 쓸 수 있는 기본값(ComfyUI 주소 `http://127.0.0.1:8188` 등)을 가진다. 특정 사용자·프로젝트에만 유효한 값을 코드나 기본 설정 에셋에 남기지 않는다. `package.json`의 `dependencies`는 빈 객체로 유지하고 선택 의존은 asmdef `versionDefines`/`defineConstraints`로 처리한다.
- **버전 동기**: 릴리스 시 `package.json`의 `version`, `MCPToolsInfo.Version`, git 태그 `vX.Y.Z`를 항상 함께 올린다.
- **네임스페이스**: 모든 코드는 `MCPTools.Editor` / `MCPTools.Runtime` 네임스페이스(또는 그 하위)에 작성해 도입 프로젝트의 코드와 충돌하지 않게 한다.
- **문서화**: `Assets/MCPTools/README.md`에 요구 사항(Unity 버전, ComfyUI 서버 준비), 설치 방법, 각 도구 사용법을 기록하고 기능 변경 시 함께 갱신한다. 외부에서 호출되는 public API에는 XML 문서 주석을 단다.
- **오류 안내**: ComfyUI 서버 미기동, 모델/노드 누락 등 다른 환경에서 흔히 발생할 실패는 콘솔 로그만 남기지 말고, 사용자가 원인과 조치를 알 수 있는 메시지(다이얼로그 또는 툴 창 내 표시)로 안내한다.
- `Assets/Generated/`, `Assets/Docs/`, 저장소 루트의 `docs/`·`mds/`는 이 프로젝트의 작업 데이터이므로 배포에 포함하지 않는다.

## 주의사항

- `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/`는 절대 수정하지 않는다.
- 스크립트 추가/수정 후에는 Unity 컴파일 완료를 확인하고 다음 작업을 진행한다.
- `.meta` 파일은 Unity가 생성하도록 두고 직접 만들지 않는다. 에셋 이동/삭제 시 `.meta`를 함께 처리한다.
- 생성된 후보 이미지 중 선택되지 않은 파일은 적용 완료 후 `Candidates/`에서 정리한다.
