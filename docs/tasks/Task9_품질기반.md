# Task 9 — 품질 기반 (테스트 · CI · 배포 온보딩)

> 배경: Task 0~7로 기능과 배포 호환성을 갖췄고 Task 8(성능·메모리)은 감사까지 끝났다. 그런데 **자동 테스트가 0개**이고 **CI가 없으며**, GitHub 저장소 첫 화면에 **README가 없다**. Task 8은 `AssetApplier.ApplyBatch` 그룹핑·시트 알고리즘 재작성처럼 **동작이 바뀔 수 있는 리팩터**를 예고하는데, 회귀 검증 수단이 수동 에디터 테스트뿐이다. 이 Task는 Task 8 착수 **전에** 안전망과 배포 진입점을 만든다.
>
> 조사 근거: 2026-07-25 저장소 전수 조사(Task 8 문서에 없는 축만 선별).

## 1. 목표

- Task 8이 바꿀 코드의 **정답지를 테스트로 고정**해, 리팩터 회귀를 사람 눈이 아니라 실행으로 잡는다.
- 릴리스 절차의 **수동 점검을 CI로 자동화**한다 (Unity 라이선스 없이 가능한 범위).
- GitHub 저장소를 **처음 방문한 개발자가 설치까지 도달**할 수 있게 한다.

이 Task는 **새 파일 추가가 대부분**이며 기존 동작을 바꾸지 않는다 (예외: 없음). Task 8·10과 병렬로 진행해도 파일 충돌이 없도록 범위를 그렇게 잡았다.

## 2. 작업 항목

### Q1. 저장소 루트 README 작성 — 높음 / 비용 매우 낮음

**문제**: 루트 추적 파일이 `LICENSE`, `CLAUDE.md`, `.mcp.json`, `.gitignore`, `.gitattributes`뿐이다. `https://github.com/chomul/MCPTools` 첫 화면에 설명·설치 방법·스크린샷이 없다. 패키지 README는 674줄로 충실하지만 경로가 깊어(`MCPToolTest/Assets/MCPTools/README.md`) 방문자가 도달하지 못한다.

**대응**: 루트에 `README.md`를 만든다. 패키지 README를 복제하지 말고 **요약 + 링크**로 유지한다(이중 관리 금지).

- 한 문단 소개 (기획서 → 에셋 생성 4단계 파이프라인, Unity Editor Tool + MCP)
- 요구 사항 요약 (Unity 6000.5+, ComfyUI 로컬, Python 3, **현재 Windows 검증**)
- 설치 URL 1줄: `https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.2.0`
- 4단계 + 스프라이트 시트 흐름 요약 (표 1개)
- 상세 문서 링크: 패키지 README / CHANGELOG / LICENSE
- 가능하면 창 스크린샷 1~2장 (`docs/images/`에 두고 루트 README에서 참조)

### Q2. EditMode 테스트 어셈블리 + 순수 함수 테스트 — 높음

**문제**: `Assets/MCPTools/` 아래 테스트 어셈블리가 없다 (C# 39개 파일, 20,322줄). Task 8 §8-D의 회귀 검증이 전부 수동이다.

**대응**: `MCPToolTest/Assets/MCPTools/Tests/EditMode/`에 테스트 어셈블리(`MCPTools.Editor.Tests`)를 만들고, **Task 8이 건드릴 순수 함수부터** 채운다.

| 대상 | 테스트 내용 | Task 8 연관 |
|------|-------------|-------------|
| `Common/MiniJson` | 직렬화 왕복(중첩·유니코드·이스케이프·숫자 타입), 소수점 로케일 무관 | S16 파서 교체의 정답지 |
| `AssetListup/AssetListDocument` | `ToDictionary`↔`FromDictionary` 왕복, **구 JSON(키 누락) 로드 시 기본값** | 회귀 방지 |
| `PromptBuilder/PromptSetDocument` | 동일 | 회귀 방지 |
| `SpriteSheet/SpriteSheetImporter.Detect` | **합성 픽셀 버퍼**로 격자 검출·셀 수·"비어 보임" 자동 제외·`cellWidth/Height` 검증 | C23·C24 알고리즘 재작성의 정답지 |
| `SpriteSheet/SpriteSheetPromptBuilder` | `ParseRows`(정상/공백/잘못된 형식), `SanitizeActionName` | 입력 파싱 |
| `SpriteSheet/SpriteSheetClipBuilder.ControllerPathForSheet` | 경로 규칙 | 자동 연결 규칙 고정 |
| `Common/MCPToolFolders` | 신·구 위치 폴백(`ResolveForRead`), 경로 조합, `EnsureAssetFolder` 경로 검증 | C27 정렬 개선 |

**설계 제약**

- 테스트 대상은 **파일 I/O·AssetDatabase에 의존하지 않는 함수 우선**. 불가피하면 임시 폴더/임시 에셋을 만들고 `[TearDown]`에서 정리한다.
- `SpriteSheetImporter.Detect`는 현재 `imagePath`(파일)만 받는다. 테스트를 위해 **픽셀 버퍼를 직접 받는 internal 오버로드**를 추가하고 `[assembly: InternalsVisibleTo("MCPTools.Editor.Tests")]`로 노출한다. public API는 늘리지 않는다.
- 테스트 어셈블리는 `Tests/EditMode/MCPTools.Editor.Tests.asmdef`, `includePlatforms: ["Editor"]`, 참조 `MCPTools.Editor` / `MCPTools.Runtime` / `nunit.framework.dll`, `"defineConstraints": ["UNITY_INCLUDE_TESTS"]`.
- **패키지에 테스트가 포함돼도 사용자 프로젝트는 컴파일하지 않는다** (UPM은 프로젝트 `manifest.json`의 `testables`에 명시된 패키지의 테스트만 컴파일). 설치 검증 시 이 점을 확인한다.

**비목표**: 창(EditorWindow) UI 테스트, ComfyUI·브리지 통합 테스트, PlayMode 테스트. 이번 Task에서는 하지 않는다.

### Q3. CI 워크플로 — 중간 / 비용 낮음

**문제**: `.github/`가 없다. `docs/릴리스절차.md`의 점검 목록(`.meta` 누락, 버전 3중 동기, `Server~` 포함, 개인 값 미포함)이 전부 사람 손에 달려 있다.

**대응**: `.github/workflows/ci.yml` — push/PR에서 Unity 라이선스 **없이** 실행 가능한 검사만.

| 검사 | 방법 |
|------|------|
| `.meta` 누락 | `docs/릴리스절차.md`의 명령을 그대로 스크립트화 (결과가 비어야 통과) |
| **버전 3중 동기** | `package.json`의 `version` ↔ `MCPToolsInfo.Version` 일치. 태그 push 이벤트면 태그(`vX.Y.Z`)까지 3중 비교 |
| Python 문법 | `python -m py_compile Server~/bridge_server.py` |
| JSON 유효성 | `Server~/workflows/*.json`, `Server~/variables.json`, `package.json` 파싱 |
| 개인 값·절대 경로 | `MCPToolSettings.asset` 커밋 여부, 코드 내 `C:\`·사용자명 패턴 스캔 |
| `Server~` 포함 | `bridge_server.py` + `variables.json` + workflows 5종 존재 |

- 실패 시 어떤 점검이 왜 실패했는지 한 줄로 출력한다 (릴리스 담당자가 로그를 파고들지 않게).
- Unity 컴파일·테스트 실행은 라이선스가 필요하므로 **이번 범위에서 제외**한다. 필요해지면 `game-ci/unity-test-runner` + 개인 라이선스 시크릿으로 후속 확장.

### Q4. Task 8 S1 계획 정정 (문서만) — 높음

**문제**: Task 8 §7.1-1이 `unloadModelsAfterBatch` 수정 대상으로 `Common/MCPToolSettings.cs:117`과 **`Common/MCPToolSettings.asset:26`** 을 지목한다. 그런데 그 `.asset`은 `.gitignore:52`로 **배포 제외**되는 로컬 파일이라 고쳐도 사용자에게 전달되지 않는다. 더 중요한 건 **이미 설정 에셋을 가진 사용자는 C# 기본값을 바꿔도 직렬화된 `true`가 그대로 남는다**는 점이다. 즉 S1의 "회차당 10~40초 절감"이 **기존 사용자에게는 적용되지 않는다**.

**대응 (이 Task에서는 문서 수정만)**

- `docs/tasks/Task8_성능개선.md` §4 S1과 §7.1-1의 대상에서 `.asset`을 빼고, **1회성 마이그레이션 요구사항**을 추가한다.
- `docs/checklist/Task8_체크리스트.md`의 S1 항목에 마이그레이션 체크와 "기존 설정 에셋을 가진 상태에서 검증" 항목을 추가한다.
- 마이그레이션 구현 방식은 Task 8이 정한다(예: `MCPToolSettings`에 `settingsVersion` 필드를 두고 이전 버전이면 1회 보정 후 저장 + 콘솔 안내, 또는 설정 창에 권장값 배지).

> 구현은 Task 8에서 한다. 이 Task가 `MCPToolSettings.cs`를 건드리지 않는 이유는 Task 8 C26(정적 캐시)이 같은 파일을 크게 고치기 때문이다.

### Q5. 지원 플랫폼 명시 (문서만) — 중간

**문제**: Task 7 D10(macOS/Linux 경로 정리)이 장비 부재로 보류 상태인데, 문서 어디에도 "Windows 전용"이라고 적혀 있지 않다. 실제로 Windows 전제 코드가 있다 — `ComfyUIServerLauncher.cs:653`(`taskkill /PID /T /F`), `:881`(`SystemDrive` 보정), `.gitattributes`의 `*.ps1 eol=crlf`.

**대응**: 추측 분기를 넣지 않는다(Task 7 결정 유지). 대신 **검증 범위를 명시**한다.

- 패키지 `README.md` 요구 사항에 "검증 플랫폼: Windows 10/11. macOS·Linux는 미검증" 한 줄
- 루트 README(Q1)에도 동일 문구
- 알려진 Windows 종속 지점을 패키지 README 문제 해결 절에 각주로 기록 (이후 Task 7 D10 착수 시 출발점)

## 3. 검증 방법

1. **Q1** — GitHub 저장소 페이지에서 README가 렌더링되고, 설치 URL을 복사해 빈 Unity 6 프로젝트에 그대로 붙여 설치된다.
2. **Q2** — Unity Test Runner(EditMode)에서 전체 테스트 통과. 일부러 `SpriteSheetImporter`의 `EmptyCellContentRatio`를 바꿔 **테스트가 실패하는지** 확인한다(정답지가 실제로 동작하는지 검증).
3. **Q2 배포 영향** — 빈 Unity 6 프로젝트에 git URL로 설치했을 때 `manifest.json`에 `testables`를 넣지 않으면 테스트가 컴파일되지 않고 **컴파일 오류 0**이어야 한다.
4. **Q3** — 일부러 `MCPToolsInfo.Version`만 올린 브랜치를 push해 CI가 버전 불일치로 실패하는지 확인한다. `.meta`를 하나 지운 브랜치도 동일하게 검증.
5. **Q4/Q5** — 문서 diff 검토.

## 4. 산출물

- `README.md` (저장소 루트, 신규)
- `MCPToolTest/Assets/MCPTools/Tests/EditMode/**` (신규 — asmdef + 테스트)
- `.github/workflows/ci.yml` (신규)
- 정정된 `docs/tasks/Task8_성능개선.md`, `docs/checklist/Task8_체크리스트.md`
- 갱신된 `MCPToolTest/Assets/MCPTools/README.md` (플랫폼 명시), `CHANGELOG.md`

## 5. 완료 조건

- 체크리스트: [Task9_체크리스트.md](../checklist/Task9_체크리스트.md)
- Q1~Q5 전부 구현
- §3 검증 1~5 통과
- **기존 동작 변경 0** — Task 1~7·6-1의 에디터 테스트를 다시 돌릴 필요가 없어야 한다 (새 파일과 문서만 추가했으므로)
