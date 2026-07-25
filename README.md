# MCP Tools — 기획서 기반 AI 에셋 생성 파이프라인

기획서(디자인 문서)를 입력으로 게임 에셋(이미지·UI·사운드)을 만들어 Unity 프리팹과 uGUI에 자동 적용하는 **4단계 파이프라인**(AssetListup → PromptBuilder → ComfyUIGenerator → AssetApplier)을 Unity Editor Tool + MCP 도구로 제공하는 UPM 패키지입니다. 이미지·사운드는 **로컬 ComfyUI**로 생성해 항목마다 후보 4개를 보여주고, 사용자가 고른 결과물만 대상 프리팹/UI 컴포넌트에 적용합니다. 분석·작성 지능은 사용자가 쓰는 AI(MCP 클라이언트, 로컬 AI CLI, 웹 AI)에 위임하는 **AI 중립** 설계라, 도구 자체는 재료 수집(scan)과 결과 검증·저장(save)만 담당합니다.

- 패키지 이름: `com.sungchan.mcptools` (현재 버전 **0.3.0**)
- 상세 문서: **[패키지 README](MCPToolTest/Assets/MCPTools/README.md)** (설치부터 단계별 사용법·MCP 도구 레퍼런스까지)

## 요구 사항

- **Unity 6000.5 이상** — Unity 6000.5.2f1(URP/uGUI 프로젝트)에서 개발·검증했습니다.
- **검증 플랫폼: Windows 10/11** — macOS·Linux는 미검증입니다. 알려진 Windows 종속 지점은 패키지 README의 [문제 해결](MCPToolTest/Assets/MCPTools/README.md#문제-해결)에 정리해 두었습니다.
- **git 2.14 이상** — Package Manager의 git URL 설치가 사용자 PC의 `git` 실행 파일을 직접 호출합니다.
- **ComfyUI 로컬 서버** (3단계 생성에만 필요) — 기본 주소 `http://127.0.0.1:8188`, 설정에서 변경 가능. 워크플로가 요구하는 모델과 커스텀 노드가 필요합니다.
- **Python 3.7 이상** (3단계 생성에만 필요) — Unity와 ComfyUI 사이의 브리지 서버 실행용이며 표준 라이브러리만 사용합니다(pip 설치 불필요).
- **선택 패키지** — uGUI(`com.unity.ugui`), 2D Sprite(`com.unity.2d.sprite`, 스프라이트 시트 슬라이스용), unity-mcp(`com.coplaydev.unity-mcp`, MCP 도구 노출용). 모두 `dependencies`에 선언하지 않았고, 없으면 해당 기능만 비활성화됩니다.

각 항목의 상세 조건은 패키지 README의 [요구 사항](MCPToolTest/Assets/MCPTools/README.md#요구-사항) 절을 참고하세요.

## 설치

Unity 6에서 **`Window > Package Manager` → 좌측 상단 [+] → `Install package from git URL...`** 에 아래 URL을 붙여넣습니다.

```
https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.3.0
```

- 끝의 `#v0.3.0`은 git 태그라 버전이 고정됩니다. 생략하면 기본 브랜치 HEAD를 받습니다.
- 예전에 `Assets/` 아래에 `MCPTools` 폴더를 직접 넣어 쓰던 프로젝트는 **패키지를 추가하기 전에 그 폴더를 삭제**해야 어셈블리 이름 충돌이 나지 않습니다.
- 설치 후 흐름은 패키지 README의 [빠른 시작 체크리스트](MCPToolTest/Assets/MCPTools/README.md#빠른-시작-첫-실행-체크리스트)를 순서대로 따라가면 첫 생성까지 도달합니다.

이 저장소에서 배포 단위(UPM 패키지 루트)는 `MCPToolTest/Assets/MCPTools/`이고, `MCPToolTest/`는 그 패키지를 개발·검증하는 Unity 6 테스트 프로젝트입니다.

## 파이프라인

| 단계 | 도구 | 입력 | 출력 |
|------|------|------|------|
| 1. 에셋 리스트업 | `Tools/MCP/1. Asset Listup` (AssetListup) | 기획서 + 프로젝트(프리팹·씬) 스캔 | 생성할 이미지/UI/오디오 목록 JSON (항목별 적용 대상 프리팹·씬 경로 포함) |
| 2. 프롬프트 제작 | `Tools/MCP/2. Prompt Builder` (PromptBuilder) | 1단계 목록 JSON + 프롬프트 템플릿 | 항목별 positive/negative 프롬프트 JSON |
| 3. 생성 | `Tools/MCP/3. ComfyUI Generator` (ComfyUIGenerator) | 프롬프트 JSON + Workflow JSON (브리지 서버 경유) | 항목별 후보 4개(시드 변경, 1~12로 조절 가능) → 사용자가 선택·확정한 이미지/오디오 |
| 4. 적용 | `Tools/MCP/4. Asset Applier` (AssetApplier) | 확정 결과물 + 1단계에 기록된 대상 경로 | 프리팹/씬 오브젝트의 `Image`·`RawImage`·`SpriteRenderer`·`AudioSource`에 적용 완료 (Undo 지원) |
| (별도) 스프라이트 시트 | `Tools/MCP/Sprite Sheet` (SpriteSheet) | 외부 AI로 만든 멀티 행 시트 png + 동작 행 목록 | 배경 제거·격자 검출 후 슬라이스한 Sprite + 동작별 AnimationClip·AnimatorController |

- 4단계 전체를 한눈에 보고 중간 단계부터 재시작하려면 `Tools/MCP/Pipeline (All-in-One)` 창을 사용합니다.
- unity-mcp 패키지를 설치하면 같은 단계를 `mcptools_*` MCP 도구로도 호출할 수 있습니다.

## 문서

- [패키지 README](MCPToolTest/Assets/MCPTools/README.md) — 요구 사항, 설치, 단계별 사용법, MCP 도구 레퍼런스, 문제 해결
- [CHANGELOG](MCPToolTest/Assets/MCPTools/CHANGELOG.md) — 변경 이력 (Keep a Changelog · Semantic Versioning)
- [LICENSE](LICENSE) — MIT 라이선스 전문
