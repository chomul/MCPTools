# Task 6-1 — 시트 임포트 행 이름 지정 + 애니메이션 클립 생성

> 배경 1: 임포트 섹션이 1번(프롬프트) 섹션의 행 목록을 그대로 재사용해, 한 행짜리 시트를 넣어도 목록에 남아 있던
> 기본값 이름이 붙고 검출 행이 더 많으면 `row2`·`row3` 자동 이름이 된다(`SpriteSheetImporter.cs:326`). 또 셀 콘텐츠
> 판정(셀 면적의 0.5% 이상 알파 픽셀)을 격자선 잔여·옅은 그림자가 넘기면 **빈칸에도 스프라이트가 생긴다**.
>
> 배경 2: Task 6은 Sprite Multiple 슬라이스(`{동작}_{NN}`)까지만 하고 끝난다. AnimationClip은 사용자가 프레임을
> 직접 드래그해 만들어야 하고, 4단계 AssetApplier는 `LoadAssetAtPath<Sprite>`만 써서 시트 안의 개별 스프라이트를
> 지정할 수 없다.

## 1. 목표

1. 임포트할 때 **동작명을 사람이 직접 지정**하게 한다(행 추가/삭제/이름 편집). 자동 이름과 빈칸 프레임이 그대로
   슬라이스되는 일을 없앤다.
2. 확정된 행 이름을 기준으로 **행별 AnimationClip을 자동 생성**하고, 선택적으로 AnimatorController 구성 +
   대상 프리팹 연결까지 수행한다.
3. 4단계 AssetApplier가 **시트 안의 특정 스프라이트(`walk_03` 등)** 를 대상 컴포넌트에 적용할 수 있게 한다.

> **격자 검출·슬라이스 알고리즘은 변경하지 않는다.** 현재 분리 품질은 문제가 없다는 사용자 확인이 있었다.
> 배경 제거, 격자 경계 검출/정리/복원, 셀 콘텐츠 판정 기준, 피벗 계산은 **그대로 둔다.**

## 2. 구현 항목

### 2.1 임포트 행 정의 — 사람이 지정 (`SpriteSheetPromptWizard` 임포트 섹션)

- **임포트 전용 행 목록**을 임포트 섹션에 둔다. 지금처럼 1번 섹션의 프롬프트용 행 목록을 그대로 쓰지 않는다
  (프롬프트를 만든 시트와 실제로 임포트하는 시트가 다를 수 있다). 편의를 위해 **[1번 행 목록 가져오기]** 버튼만 제공한다.
- 행별로 **동작명 직접 입력**(프리셋 드롭다운 + 자유 입력) + **[행 추가] / [행 삭제]**. 순서 변경(▲▼)도 지원한다.
- 행 순서는 시트의 위→아래에 그대로 대응한다(현재 규칙 유지).

### 2.2 검출 결과 확인·편집 후 적용 (2단계 실행)

- **[배경 제거 + 격자 검출]** 과 **[슬라이스 적용]** 을 분리한다. 검출까지 마친 뒤 결과를 보여주고, 사용자가 확인해야 슬라이스가 기록된다.
- 검출 결과 표: 행별로 **검출된 프레임 수**, **동작명 입력란**, 프레임별 **썸네일 + 포함/제외 체크박스**.
  - 검출된 행 수가 정의한 행 수와 다르면 표에 그대로 드러나므로, 사용자가 이름을 채우거나 행을 제외하면 된다.
  - **자동 `rowN` 이름을 붙이지 않는다.** 이름이 빈 행이 있으면 [슬라이스 적용]을 막고 어느 행인지 표시한다.
  - 빈칸으로 보이는 프레임은 사용자가 체크를 해제해 제외한다(콘텐츠 판정 기준 자체는 건드리지 않는다).
    보조로, 콘텐츠 알파 픽셀 비율이 낮은 셀은 표에 "비어 보임" 표시를 달아 눈에 띄게 한다.
- 프레임 번호는 **포함된 프레임만 1부터 순서대로** 부여한다(현재 규칙과 동일: `{동작}_{프레임:00}`).
- 검출·슬라이스 계산 자체는 기존 `SpriteSheetImporter` 로직을 그대로 호출한다. 이번 변경은 **적용 시점을 뒤로 미루고
  이름/포함 여부를 사용자 입력으로 받는 것**에 한정한다.

### 2.3 `SpriteSheetClipBuilder` — 행별 AnimationClip 생성 (`Editor/SpriteSheet/`)

- **입력**: 슬라이스 완료된 시트 텍스처 경로, 프레임 레이트(기본 12 — `MCPToolSettings`에 `spriteAnimationFrameRate` 추가), 대상 컴포넌트 종류(`SpriteRenderer` / `Image`), 동작별 루프 여부, 생성 대상 동작 목록.
- **스프라이트 수집**: `AssetDatabase.LoadAllAssetRepresentationsAtPath`로 서브 스프라이트를 읽어 이름을 파싱한다.
  파싱 규칙은 **마지막 `_` 뒤의 숫자를 프레임 번호, 앞부분 전체를 동작명**으로 본다(`^(?<action>.+)_(?<no>\d+)$`).
  동작명에 `_`가 들어가도(`attack_combo_01`) 안전하다. 규칙에 맞지 않는 이름은 건너뛰고 사유를 결과에 남긴다.
- **클립 생성**: 동작 1개 = 클립 1개.
  - `EditorCurveBinding.PPtrCurve(path, type, "m_Sprite")` + `AnimationUtility.SetObjectReferenceCurve`, 키 시간 `i / frameRate`, `clip.frameRate = frameRate`.
  - `path`는 대상 오브젝트가 지정된 경우 그 계층 경로, 아니면 빈 문자열(루트 자기 자신).
  - `SpriteRenderer` 대상은 `typeof(SpriteRenderer)`. `Image` 대상은 uGUI 어셈블리를 직접 참조하지 않는 기존 규칙에 맞춰 `Type.GetType("UnityEngine.UI.Image, UnityEngine.UI")`로 해석하고, 실패 시 **원인·조치 안내**(uGUI 패키지 미설치)를 표시한다.
  - **루프 설정**: `AnimationUtility.GetAnimationClipSettings` / `SetAnimationClipSettings`의 `loopTime`. 기본값은 `idle`·`walk`·`run` 계열 ON, `attack`·`death` 계열 OFF이며 표에서 행별로 바꿀 수 있다.
- **출력 경로**: `Assets/Generated/3_Confirmed/Animations/{시트이름}/{동작}.anim`.
  `MCPToolFolders`에 `AnimationsFolder = "Animations"` + `AnimationsDir(settings)`를 추가하고 폴더가 없으면 자동 생성한다.
- **기존 파일 처리**: 같은 경로에 클립이 있으면 새로 만들지 않고 **커브·프레임 레이트·루프 설정만 덮어쓴다**(Animator에 물려 둔 참조와 사용자가 붙인 이벤트 보존). 덮어쓸 대상 목록을 확인 다이얼로그로 먼저 보여준다.

### 2.4 AnimatorController 생성 + 프리팹 연결 (옵션)

- 토글이 켜진 경우에만 수행한다. 끄면 `.anim` 에셋만 만들고 끝낸다.
- `UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath`로 `{시트이름}.controller`를 만들고 동작별 State를 배치한다. 기본 State는 `idle` → `walk` 우선순위, 둘 다 없으면 첫 동작.
- 이미 컨트롤러가 있으면 **없는 State만 추가**하고 기존 트랜지션·파라미터·레이어는 건드리지 않는다.
- **대상 프리팹이 지정된 경우에만** 프리팹의 대상 오브젝트에 `Animator`를 붙이고(있으면 재사용) 컨트롤러를 할당한다. 프리팹 에셋 자체를 수정하며 `PrefabUtility` + `Undo` 규칙을 따른다.

### 2.5 창 UI — `Tools/MCP/Sprite Sheet` 창에 "4. 애니메이션" 섹션 추가

- **새 창을 만들지 않는다.** 슬라이스 적용 직후 이어지는 흐름이므로 임포트 섹션 아래에 붙인다.
- 확정된 행 목록을 표로 표시: 동작명 / 프레임 수 / 루프 토글 / 클립 생성 체크박스.
- 공통 입력: 대상 컴포넌트 선택(SpriteRenderer / Image), 프레임 레이트, 대상 프리팹(ObjectField, 선택), 대상 오브젝트(프리팹 계층 드롭다운, 선택).
- 버튼: **[클립 생성]** / **[클립 + Animator 생성]**. 실행 후 생성·갱신된 경로 목록과 건너뛴 항목의 사유를 표시한다.

### 2.6 MCP 도구

- **`mcptools_spritesheet_import` 변경** — `dryRun`(기본 false) 파라미터 추가. true면 배경 제거·격자 검출까지만 하고
  **행/프레임 검출 결과만 반환**하며 슬라이스를 적용하지 않는다. 호출자(AI)가 검출 결과를 보고 행 이름을 채워 다시
  호출하는 흐름을 만든다. 행 이름이 검출 행 수보다 부족하면 자동 이름을 붙이지 않고 **어느 행이 비었는지 알려주며 실패**한다.
- **`mcptools_spritesheet_build_clips` 신규** — 파라미터: `sheetPath`(필수), `frameRate`(기본 12), `targetComponent`(`"SpriteRenderer"`|`"Image"`, 기본 SpriteRenderer), `loopActions`(쉼표 구분, 미지정 시 기본 규칙), `createController`(bool, 기본 false), `targetPrefabPath`(선택), `targetObjectPath`(선택).
  반환: 생성·갱신된 클립 경로 목록, 동작별 프레임 수, 컨트롤러 경로, 프리팹 연결 여부, 건너뛴 항목과 사유.
- **한 도구는 한 단계만 담당한다** — 임포트와 클립 생성을 합치지 않는다.

### 2.7 AssetApplier — `spriteName`으로 서브 스프라이트 적용

- `AssetListItem`에 `spriteName`(기본 빈 문자열)과 `spriteSheetPath`(선택, 기본 빈 문자열) 필드를 추가하고 JSON 직렬화에 포함한다.
- `AssignAsset`: `spriteName`이 비어 있으면 **기존 동작 그대로**(`LoadAssetAtPath<Sprite>`). 값이 있으면 `LoadAllAssetRepresentationsAtPath`에서 이름이 일치하는 `Sprite`를 찾아 `SpriteRenderer.sprite` 또는 `Image.m_Sprite`에 할당한다.
- 이름을 찾지 못하면 실패 사유에 **그 시트에서 사용 가능한 스프라이트 이름 목록(앞부분 일부)** 을 함께 표시한다.
- 대상 에셋 경로 결정: `spriteSheetPath`가 지정된 항목은 확정본 자동 탐색 대신 그 경로를 쓴다. `FindConfirmedAssetPath`의 탐색 폴더에 `SpriteSheets/`를 **추가하지 않는다**(시트는 항목별 확정본이 아니라는 기존 구분 유지).
- `mcptools_apply_asset`에 `spriteName` 파라미터를 추가한다.
- 1단계 표에는 열을 추가하지 않는다(폭 문제). 편집은 4단계 창의 항목 상세와 MCP 파라미터로 한다.

## 3. 규칙

- **격자 검출·배경 제거·셀 콘텐츠 판정·피벗 계산 로직은 수정하지 않는다.** 이번 작업은 이름 지정 UI, 적용 시점 분리,
  클립 생성, 서브 스프라이트 적용에 한정한다.
- 전부 Editor 전용. 클립 빌더는 `Editor/SpriteSheet/`, AssetApplier 변경은 기존 파일 안에서 처리하고 불필요한 새 클래스를 만들지 않는다.
- 2D Sprite·uGUI는 **선택 의존**이다. 타입 이름/리플렉션 판정을 유지하고, 없으면 콘솔 로그가 아니라 원인·조치를 담은 메시지로 안내한다.
- 에셋 생성·수정 후 `AssetDatabase.SaveAssets` / `ImportAsset`를 호출하고, 프리팹 수정은 `PrefabUtility` + `Undo` 등록을 지원한다.
- 출력 경로는 설정 기반 `Assets/` 상대 경로만 쓰고 폴더가 없으면 자동 생성한다. 배포 패키지 폴더(`Assets/MCPTools/`)에는 어떤 생성물도 만들지 않는다.
- 기존 클립·컨트롤러를 덮어쓰기 전에 사용자 확인을 받는다.

## 4. 완료 조건

- 체크리스트: `docs/checklist/Task6-1_체크리스트.md` (착수 시 생성)
- 에디터 테스트 통과:
  - **한 행짜리 시트**를 임포트할 때 행 목록에서 행을 1개로 줄이고 이름을 직접 넣으면 그 이름으로만 슬라이스됨 (`row2` 같은 자동 이름이 생기지 않음)
  - 이름이 빈 행이 있으면 [슬라이스 적용]이 막히고 어느 행인지 안내됨
  - 검출 결과 표에서 **빈칸으로 보이는 프레임의 체크를 해제**하면 그 스프라이트가 생성되지 않고, 뒤 프레임 번호가 당겨져 연속됨
  - 슬라이스 결과(프레임 위치·크기·피벗)가 이번 변경 전과 동일함 — 분리 품질 회귀 없음
  - walk/run/attack/death 4행 시트 → 클립 4개가 각 행의 프레임 수·순서대로 생성되고, walk/run은 루프 ON·attack/death는 OFF
  - [클립 + Animator 생성] → 각 State가 재생되고 지정한 프리팹에 Animator + 컨트롤러가 연결됨
  - 같은 시트로 다시 실행 → 클립이 새로 생기지 않고 갱신되며 기존 컨트롤러의 트랜지션이 유지됨
  - `spriteName`을 지정한 항목이 4단계에서 시트의 해당 프레임으로 적용됨 / 없는 이름이면 사용 가능한 이름 목록이 안내됨
- README(스프라이트 시트 절 + 4단계 절)·CHANGELOG 갱신

## 5. 범위 밖

- **격자 검출·슬라이스 알고리즘 개선** — 현재 분리 결과에 문제가 없으므로 건드리지 않는다
- 3D 모델·애니메이션 생성 (`PLAN.md` §1.4 비목표)
- 애니메이션 이벤트, 블렌드 트리, 트랜지션 조건 자동 구성 — 컨트롤러는 State 배치까지만
- 시트 재생성·재슬라이스 (Task 6 소관)
- 1단계/2단계 UI에 스프라이트 이름 열 추가
