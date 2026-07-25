# Task 9 체크리스트 — 품질 기반 (테스트 · CI · 배포 온보딩)

> Task 문서: [Task9_품질기반.md](../tasks/Task9_품질기반.md)
> 2026-07-25 작성: 조사 완료, 구현 미착수.
> **범위 원칙: 새 파일 추가와 문서 수정만 한다.** 기존 코드의 동작을 바꾸지 않는다 — Task 8·10과 병렬로 진행하기 위한 제약이다.
> **예외 1건**: Q2에서 `SpriteSheetImporter`에 테스트용 internal 오버로드 + `InternalsVisibleTo`를 추가한다 (public API·기존 경로 동작 무변경).

## 1. 구현 체크리스트

### Q1. 저장소 루트 README

- [ ] 루트 `README.md` 생성 — 소개 / 요구 사항 / 설치 URL / 4단계+시트 흐름 표 / 상세 문서 링크
- [ ] 패키지 README를 복제하지 않고 **링크로 연결** (이중 관리 금지)
- [ ] 설치 URL이 현재 릴리스 태그와 일치 (`#v0.2.0`)
- [ ] (선택) 창 스크린샷 1~2장 — `docs/images/`에 두고 참조

### Q2. EditMode 테스트

- [ ] `Tests/EditMode/MCPTools.Editor.Tests.asmdef` — Editor 전용, `MCPTools.Editor`·`MCPTools.Runtime`·nunit 참조, `defineConstraints: ["UNITY_INCLUDE_TESTS"]`
- [ ] `MiniJson` 왕복 테스트 (중첩·유니코드·이스케이프·정수/실수 타입, 로케일 무관)
- [ ] `AssetListDocument` 왕복 + **구 JSON(키 누락) 로드 시 기본값** 테스트
- [ ] `PromptSetDocument` 왕복 테스트
- [ ] `SpriteSheetImporter.Detect` — 합성 픽셀 버퍼로 격자 검출 / 셀 수 / "비어 보임" 자동 제외 / 셀 크기
- [ ] `SpriteSheetImporter`에 픽셀 버퍼를 받는 internal 오버로드 + `InternalsVisibleTo` 추가
- [ ] `SpriteSheetPromptBuilder.ParseRows` / `SanitizeActionName`
- [ ] `SpriteSheetClipBuilder.ControllerPathForSheet`
- [ ] `MCPToolFolders` — 신·구 위치 폴백, 경로 조합, `EnsureAssetFolder` 경로 검증
- [ ] 파일/AssetDatabase를 쓰는 테스트는 `[TearDown]`에서 생성물 정리

### Q3. CI 워크플로

- [ ] `.github/workflows/ci.yml` 생성 (push / PR / tag)
- [ ] `.meta` 누락 검사 (릴리스절차의 명령 그대로)
- [ ] 버전 3중 동기 검사 (`package.json` ↔ `MCPToolsInfo.Version` ↔ 태그)
- [ ] `python -m py_compile bridge_server.py`
- [ ] JSON 유효성 (`workflows/*.json`, `variables.json`, `package.json`)
- [ ] 개인 값·절대 경로 스캔 (`MCPToolSettings.asset` 커밋 여부, `C:\` 패턴)
- [ ] `Server~` 필수 파일 존재 검사
- [ ] 실패 시 원인을 한 줄로 출력

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

- [ ] 패키지 `README.md` 요구 사항에 "검증 플랫폼: Windows 10/11, macOS·Linux 미검증"
- [ ] 루트 README(Q1)에도 동일 문구
- [ ] 알려진 Windows 종속 지점을 패키지 README 문제 해결 절에 기록 (`ComfyUIServerLauncher.cs`의 `taskkill`·`SystemDrive`)

### 마무리

- [ ] `CHANGELOG.md` `[Unreleased]`에 기록 (Task 10과 같은 파일 — 충돌 시 두 항목 모두 유지)

## 2. 에디터 테스트 체크리스트

- [ ] Unity Test Runner(EditMode)에서 전체 테스트 통과
- [ ] `EmptyCellContentRatio` 값을 일부러 바꾸면 시트 검출 테스트가 **실패**한다 (정답지가 실제로 동작하는지 확인)
- [ ] 빈 Unity 6 프로젝트에 git URL로 설치 → `testables` 미지정 상태에서 **컴파일 오류 0**, 테스트 어셈블리 미컴파일
- [ ] 같은 프로젝트의 `manifest.json`에 `testables`로 `com.sungchan.mcptools`를 추가하면 Test Runner에 테스트가 나타나고 통과
- [ ] GitHub 저장소 첫 화면에서 README가 렌더링되고 설치 URL 복사 → 설치 성공
- [ ] `MCPToolsInfo.Version`만 올린 브랜치를 push하면 CI가 버전 불일치로 실패
- [ ] `.meta` 하나를 지운 브랜치를 push하면 CI가 실패
