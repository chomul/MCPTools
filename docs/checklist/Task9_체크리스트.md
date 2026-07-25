# Task 9 체크리스트 — 품질 기반 (테스트 · CI · 배포 온보딩)

> Task 문서: [Task9_품질기반.md](../tasks/Task9_품질기반.md)
> 2026-07-25 작성: 조사 완료, 구현 미착수.
> **범위 원칙: 새 파일 추가와 문서 수정만 한다.** 기존 코드의 동작을 바꾸지 않는다 — Task 8·10과 병렬로 진행하기 위한 제약이다.
> **예외 1건**: Q2에서 `SpriteSheetImporter`에 테스트용 internal 오버로드 + `InternalsVisibleTo`를 추가한다 (public API·기존 경로 동작 무변경).

## 1. 구현 체크리스트

### Q1. 저장소 루트 README

- [x] 루트 `README.md` 생성 — 소개 / 요구 사항 / 설치 URL / 4단계+시트 흐름 표 / 상세 문서 링크
- [x] 패키지 README를 복제하지 않고 **링크로 연결** (이중 관리 금지)
- [x] 설치 URL이 현재 릴리스 태그와 일치 (`#v0.2.0`)
- [~] (선택) 창 스크린샷 — 이미지 파일이 없어 넣지 않았다. 빈 링크를 남기지 않는 편을 택함
  - 구현 결과: 5개 절(소개 / 요구 사항 / 설치 / 파이프라인 / 문서)로 구성. 파이프라인 표는 단계·도구(메뉴 경로 포함)·입력·출력 4열이며 4단계 + `(별도) 스프라이트 시트` 행을 담았다. 상세는 전부 패키지 README 앵커 링크로 넘겨 이중 관리를 피했다.
  - 검증 상태: 인용 값을 원본에서 대조 완료 — 메뉴 경로 5종(`1. Asset Listup`/`2. Prompt Builder`/`3. ComfyUI Generator`/`4. Asset Applier`/`Sprite Sheet`/`Pipeline (All-in-One)`)이 실제 `[MenuItem]` 문자열과 일치, Python 3.7 이상(`ComfyUIServerLauncher.cs:44,47`의 `MinimumPythonMajor/Minor`), 버전 `0.2.0`(package.json ↔ `MCPToolsInfo.Version` ↔ 태그 `v0.2.0` 3중 일치), 설치 URL 형식(`docs/릴리스절차.md:13`). **✅ 푸시 후 확인 완료(`b1a5bc3`)** — GitHub API로 루트 README가 렌더링됨(5,056B, 제목·설치 URL·검증 플랫폼 문구 포함), 링크 대상 3종(패키지 README/CHANGELOG/LICENSE) 전부 HTTP 200, 앵커 대상 헤딩(`## 요구 사항`·`## 문제 해결`·`## 빠른 시작`)이 패키지 README에 실재함을 확인.
  - 관련 파일: `README.md`(신규)

### Q2. EditMode 테스트

- [x] `Tests/EditMode/MCPTools.Editor.Tests.asmdef` — Editor 전용, `MCPTools.Editor`·`MCPTools.Runtime`·nunit 참조, `defineConstraints: ["UNITY_INCLUDE_TESTS"]`
- [x] `MiniJson` 왕복 테스트 (중첩·유니코드·이스케이프·정수/실수 타입, 로케일 무관)
- [x] `AssetListDocument` 왕복 + **구 JSON(키 누락) 로드 시 기본값** 테스트
- [x] `PromptSetDocument` 왕복 테스트
- [x] `SpriteSheetImporter.Detect` — 합성 시트로 격자 검출 / 셀 수 / "비어 보임" 자동 제외 / 셀 크기 / rect / 배경 제거 결과
- [~] ~~`SpriteSheetImporter`에 픽셀 버퍼를 받는 internal 오버로드~~ — **불필요해 하지 않음**(아래 설계 변경). `InternalsVisibleTo`는 새 파일 `Editor/AssemblyInfo.cs`에 추가
- [x] `SpriteSheetPromptBuilder.ParseRows` / `SanitizeActionName`
- [x] `SpriteSheetClipBuilder.ControllerPathForSheet`
- [x] `MCPToolFolders` — 신·구 위치 폴백, 경로 조합, `FindDocuments`, `EnsureAssetFolder` 경로 검증
- [x] 파일/AssetDatabase를 쓰는 테스트는 `[TearDown]`에서 생성물 정리
  - **설계 변경 (원안 대비)**: Task 9 문서 §Q2는 `Detect`에 픽셀 버퍼 오버로드를 추가하자고 했으나, `Detect(string imagePath, bool, bool)`가 이미 public이고 **절대 경로를 그대로 처리**(`ResolveFullPath`)하므로 합성 시트 PNG를 시스템 임시 폴더에 쓰고 그 경로로 호출하는 방식을 택했다. 결과: **`SpriteSheetImporter.cs` 원본 무수정** → Task 8 C23/C24와의 충돌 위험 제거. `Detect`는 파일을 쓰지 않으므로 프로젝트에도 아무것도 남지 않는다(`ApplySlices`는 호출하지 않음).
  - 구현 결과: 테스트 7파일 / 메서드 55개(NUnit 케이스 약 74). 합성 시트는 200×200, 50px 셀 4×4, 배경 순백, **격자선 242 무채색 3px**(내부 경계만 — 가장자리에 선을 두면 sliver 셀이 생겨 경계 정리 로직이 개입), 콘텐츠는 채도 190 빨강. 242를 고른 근거는 `GridLineMinChannel(150) < 242 < GridLineMaxChannel(248)`을 만족하면서 동시에 `NearWhiteThreshold(235)` 이상이라 외곽 BFS가 격자선을 넘어 셀 안쪽 배경까지 지운다는 점(중간 회색이면 BFS가 막혀 "비어 보임" 검증이 불가능). 배치는 행0 정상 4칸 / 행1 정상 3칸+작은 점 1칸 / 행2 정상 2칸 / 행3 공백.
  - 기대값: `rows=3`(행3 탈락), 행별 셀 4/4/2, `TotalFrameCount=10`, `LooksEmptyFrameCount=1`, `IncludedRowCount=3`, 정상 셀 `contentRatio=0.16` / 작은 점 `0.01`(→ `looksEmpty`, `include=false`), `cellWidth=cellHeight=50`, rect 하단-좌 원점. `whiteBackground=false`면 rows=4·total=16·looksEmpty=0.
  - 검증 상태: **✅ Unity 6000.5.2f1에서 EditMode 74/74 통과, 실패 0, 0.27초** (`run_tests` MCP, assembly `MCPTools.Editor.Tests`). 컴파일 오류·경고 0건(`Logs/Editor.log`에 CS#### 0건), `MCPTools.Editor.Tests.dll` 정상 빌드. 부작용 없음 — 프로젝트에 생성된 파일 0개, 시스템 임시 폴더의 합성 시트도 `[TearDown]`이 전부 삭제(잔여 0). 작성 단계에서는 서브에이전트가 `Detect`의 전 파이프라인(외곽 BFS → fringe → RestorePockets → FeatherEdges → DetectGridBoundaries → RefineGridBoundaries → ClearGridLineBands → 셀 판정)을 소스 상수 그대로 Python으로 시뮬레이션해 기대값을 맞췄고, 실행 결과가 그 값과 일치했다.
  - **미확인 가정 3건 → 전부 해소**: PNG `EncodeToPNG`↔`LoadImage` 무손실 왕복 OK(격자 검출 기대값이 정확히 일치), NUnit `Assert.AreEqual` 혼합 숫자 타입 오버로드 OK, `File.SetLastWriteTime` 기반 정렬 결정성 OK.
  - **주의**: `SpriteSheetImporter`의 판정 상수는 전부 `private const`라 테스트에서 참조할 수 없어 값을 복사하고 주석에 근거를 남겼다. Task 8이 상수를 바꾸면 테스트가 실패한다(의도된 감지) — 그때 **주석의 숫자도 함께 갱신**해야 한다.
  - 관련 파일: `Tests/EditMode/*.cs`(7), `Tests/EditMode/MCPTools.Editor.Tests.asmdef`, `Editor/AssemblyInfo.cs`

### Q3. CI 워크플로

- [x] `.github/workflows/ci.yml` 생성 (push / PR / tag / workflow_dispatch)
- [x] `.meta` 누락 검사 (릴리스절차의 명령 그대로)
- [x] 버전 3중 동기 검사 (`package.json` ↔ `MCPToolsInfo.Version` ↔ 태그)
- [x] `python -m py_compile bridge_server.py`
- [x] JSON 유효성 (`workflows/*.json`, `variables.json`, `package.json`, `Packages/manifest.json` 총 8개)
- [x] 개인 값·절대 경로 스캔 (`MCPToolSettings.asset` 커밋 여부, 절대 경로 패턴)
- [x] `Server~` 필수 파일 존재 검사 (7종)
- [x] 실패 시 원인을 한 줄로 출력 (`FAIL [점검명] 사유` + `::error::` + 실행 요약 표)
  - 구현 결과: 점검 6개를 각각 `.github/scripts/check-*.sh`로 분리하고 `_lib.sh`(저장소 루트 이동 / `fail`·`pass` / Python 실행 파일 탐지)를 공유한다. 워크플로는 `ubuntu-latest` + `actions/checkout@v4` + `setup-python@v5`, 모든 step에 `if: always()`를 걸어 하나가 실패해도 나머지 결과를 함께 볼 수 있다. 각 step 주석에 릴리스절차의 대응 항목을 명시했다.
  - **절대 경로 스캔 오탐 회피**: 순진한 `C:\` grep은 정상 코드를 잡는다(`ComfyUIServerLauncher.cs:167`의 사용자 안내 예시 `C:\Unity\<프로젝트명>`, `:881`의 `"C:"` 설명 주석). 주석을 제외하는 대신(주석에 박힌 개인 경로도 유출이므로) **드라이브 문자 + 홈/설치 성격 세그먼트**(`Users`/`Program Files`/`ProgramData`/`Projects` 등)가 이어질 때만 매치하도록 패턴을 좁혔다. POSIX는 `/home/<이름>/`·`/Users/<이름>/`만 본다.
  - **`python3` 폴백**: Windows Git Bash의 `python3`는 Microsoft Store 스텁이라 실행되지 않는다. `_lib.sh`가 `python3 → python → py` 순으로 실제 Python 3를 탐지해 CI와 로컬 양쪽에서 동작한다.
  - 통합 시 정리: 서브에이전트가 만든 `.github/.gitattributes`를 삭제하고 **루트 `.gitattributes`에 `*.sh text eol=lf`를 추가**했다(Task 7이 세운 단일 `.gitattributes` 규약 유지, `*.yml`은 이미 루트에 있음).
  - 검증 상태: **현재 HEAD에서 6개 전부 PASS(주 에이전트가 독립 재실행해 확인)**. 실패 경로도 확인 — 태그 불일치(`v9.9.9`) 시 `FAIL [버전 3중 동기] 태그(v9.9.9) 와 package.json(0.2.0)…` 한 줄로 종료(exit 1). 서브에이전트가 격리 픽스처 저장소에서 `.meta` 삭제 / JSON 파손 / `Server~` 미추적 / 문법 오류 / `MCPToolSettings.asset` 커밋 / 절대 경로 유출 시뮬레이션을 수행해 전부 검출을 확인했고, 오탐 대조군 4종은 통과했다. **✅ 푸시 후 실제 실행 확인 완료** — 실행 [30166612571](https://github.com/chomul/MCPTools/actions/runs/30166612571)(`b1a5bc3`)에서 job `릴리스 사전 점검`이 `success`, **6개 점검 step이 전부 실행되어 success**(조용히 건너뛴 step 없음). ubuntu 러너에서 `.sh` 줄바꿈·`setup-python`·`GITHUB_STEP_SUMMARY`가 정상 동작함을 확인.
  - 관련 파일: `.github/workflows/ci.yml`, `.github/scripts/*.sh`(7개), `.gitattributes`

### Q4. Task 8 S1 계획 정정 (문서만)

- [x] `docs/tasks/Task8_성능개선.md` §4 S1 — 대상에서 `MCPToolSettings.asset` 제거(`.gitignore` 대상이라 배포 무관)
- [x] 같은 문서 §7.1-1 — **기존 설정 에셋 사용자용 1회성 마이그레이션** 요구사항 추가
- [x] `docs/checklist/Task8_체크리스트.md` S1 — 마이그레이션 체크 + "기존 설정 에셋이 있는 상태에서 검증" 항목 추가
- [x] Task 8 §1 정량 목표의 "회차당 부가 대기" 항목에 *신규 설치 기준 / 기존 사용자 기준* 구분 명시
  - 구현 결과: Task 8 문서 4곳(§1 목표 표 + 표 아래 주석 신설, §4 S1 근거·대응, §7.1-1, §8 B-7)과 Task 8 체크리스트 §1 S1 항목을 정정했다. 핵심은 두 가지 — ① `MCPToolSettings.asset`은 `.gitignore:52-53`으로 git 추적 제외라 고쳐도 배포물에 반영되지 않으므로 수정 대상에서 제외, ② `MCPToolSettings.cs:134`의 `GetOrCreate()`가 `Assets` 범위의 기존 설정 에셋을 그대로 쓰기 때문에 **C# 기본값만 바꾸면 기존 사용자에게는 직렬화된 `true`가 남아 절감 효과가 0**이라는 사실을 명시하고 1회성 마이그레이션을 S1의 **필수 요구사항**으로 승격. 구현 방식은 Task 8이 정하도록 열어 뒀다(예: `settingsVersion` 필드 + 1회 보정, 설정 창 권장값 배지). 함께 잘못된 줄 번호도 교정(`MCPToolSettings.cs:117` → `:125`).
  - 검증 상태: 문서 diff 검토 완료. 인용한 줄 번호(`MCPToolSettings.cs:125` = `unloadModelsAfterBatch = true`, `:134` = `GetOrCreate()`, `.gitignore:52-53`)를 실제 파일에서 재확인함. 코드·`.asset`은 변경하지 않음.
  - 관련 파일: `docs/tasks/Task8_성능개선.md`, `docs/checklist/Task8_체크리스트.md`
  - **Task 8 담당자 영향**: S1이 "기본값 2줄 변경"에서 "기본값 변경 + 마이그레이션 구현 + 기존 에셋 상태 검증"으로 확대됐다. Task 8 착수 시 두 문서를 다시 읽을 것.

### Q5. 지원 플랫폼 명시 (문서만)

- [x] 패키지 `README.md` 요구 사항에 "검증 플랫폼: Windows 10/11, macOS·Linux 미검증"
- [x] 루트 README(Q1)에도 동일 문구
- [x] 알려진 Windows 종속 지점을 패키지 README 문제 해결 절에 기록 (`ComfyUIServerLauncher.cs`의 `taskkill`·`SystemDrive`)
  - **조사 정정**: 최초 조사에서 "Windows 전제 코드"라고 판단했으나 실제로는 **플랫폼 분기가 이미 있고 비Windows 경로가 미검증**이다. `ComfyUIServerLauncher.cs:648`이 `#if UNITY_EDITOR_WIN`(`taskkill /PID /T /F` 프로세스 트리 종료) / `#else`(`Process.Kill()` 단일 프로세스)로 갈라지고, `:710`의 `IsWindows`로 Python 탐지 후보를 분기해 Windows에서만 `SystemDrive` 루트(`:761`)를 훑는다. 따라서 문서를 "Windows 전용"이 아니라 "Windows 검증 / 그 외 미검증 + 알려진 동작 차이"로 기술했다. Task 9 문서 §Q5에도 이 정정을 기록함.
  - 구현 결과: 패키지 README 2곳 — ① 요구 사항 절 Unity 버전 다음 줄에 검증 플랫폼 한 줄(+ 문제 해결 앵커 링크), ② 문제 해결 절 마지막에 "macOS·Linux에서 쓸 때 (미검증)" 항목과 하위 2개(브리지 종료 시 자식 python 잔존 가능 → 콘솔 종료 또는 OS 무관한 [원격 종료], Python 자동 탐지 경로 차이 → 설정에 절대 경로 지정). 추측성 해결책은 넣지 않고 "동작을 보장하지 않습니다"를 명시.
  - 검증 상태: 근거 줄 번호를 실제 파일에서 재확인(`:648`, `:710`, `:761`). 문서 diff 검토 완료.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/README.md`, `README.md`, `docs/tasks/Task9_품질기반.md`

### 마무리

- [ ] `CHANGELOG.md` `[Unreleased]`에 기록 (Task 10과 같은 파일 — 충돌 시 두 항목 모두 유지)

### Unity 검증 (완료)

- [x] **Unity 에디터를 한 번 열어 `Tests/EditMode/**`·`Editor/AssemblyInfo.cs`의 `.meta` 생성** — 9개 전부 생성 확인 후 커밋. `.meta` 없이 커밋하면 사용자 프로젝트에서 GUID가 새로 생성돼 asmdef 참조가 깨지고(릴리스절차 §커밋 전 점검), Q3에서 만든 CI의 `.meta` 검사도 실패한다 → **Q3의 검사가 실제로 이 실수를 막았다**
- [x] 컴파일 오류 0 확인 후 커밋

## 2. 에디터 테스트 체크리스트

- [x] Unity Test Runner(EditMode)에서 전체 테스트 통과 — **74/74 통과, 0.27초** (Unity 6000.5.2f1)
- [ ] `EmptyCellContentRatio` 값을 일부러 바꾸면 시트 검출 테스트가 **실패**한다 (정답지가 실제로 동작하는지 확인)
  - 미수행 — `SpriteSheetImporter.cs`는 터미널 B(Task 8) 영역이라 이번 병행 작업 중에는 건드리지 않았다. Task 8이 C23/C24로 알고리즘을 재작성할 때 이 테스트가 실제로 회귀를 잡는지가 자연스러운 검증이 된다
- [ ] 빈 Unity 6 프로젝트에 git URL로 설치 → `testables` 미지정 상태에서 **컴파일 오류 0**, 테스트 어셈블리 미컴파일
- [ ] 같은 프로젝트의 `manifest.json`에 `testables`로 `com.sungchan.mcptools`를 추가하면 Test Runner에 테스트가 나타나고 통과
- [x] GitHub 저장소 첫 화면에서 README가 렌더링되고 링크 3종이 살아 있음 — API로 확인. **빈 프로젝트에 설치 URL로 실제 설치하는 검증은 미수행**(별도 Unity 프로젝트 필요, 릴리스 시 `docs/릴리스절차.md` §설치 검증에서 수행)
- [~] `MCPToolsInfo.Version`만 올린 브랜치를 push하면 CI가 버전 불일치로 실패 — 원격 push로는 미수행. 대신 **로컬에서 같은 스크립트에 불일치 값을 주입해 exit 1과 한 줄 사유를 확인**했고(주 에이전트), 서브에이전트가 격리 픽스처 저장소에서 실제 파일을 고쳐 재현했다. 원격 실패 경로를 남기려고 일부러 깨진 커밋을 push하지는 않았다
- [ ] `.meta` 하나를 지운 브랜치를 push하면 CI가 실패
