# Task 10 — 정확성 · 견고성 (MCP 신뢰성 · 적용 안전성 · 브리지 하드닝)

> 배경: Task 8은 **성능·메모리**만 감사했다. 그 축 밖에서 확인된 문제들 — MCP 경로에서 모달 다이얼로그가 뜨고, Play Mode 가드가 전혀 없고, 적용 이력이 저장되지 않아 일괄 적용이 이미 적용한 항목까지 다시 저장하며, 프리팹 변종·Prefab Stage를 고려하지 않는다 — 을 모은 Task다. 대부분 작은 변경이지만 **MCP 에이전트가 주도하는 사용 흐름의 신뢰성**을 좌우한다.
>
> 조사 근거: 2026-07-25 저장소 전수 조사.

> ## ⚠️ Task 8과 같은 파일을 고친다 — 동시 진행 금지
>
> | 파일 | Task 8 | Task 10 |
> |------|--------|---------|
> | `AssetApplier/AssetApplier.cs` | C17·C18·C19·C20·C21 (`ApplyBatch`·`ValidateItem`·`ApplyToPrefab` 재작성) | R3·R4 (같은 함수) |
> | `AssetApplier/AssetApplierWindow.cs` | C1·C2·C7 (리페인트 캐시화) | R3 (`ItemState`·`RebuildStates`) |
> | `ComfyUIGenerator/CandidateGenerator.cs` | S8·S9 | R1 |
> | `Pipeline/PipelineTool.cs` | S2·S3 | R1 (호출부) |
> | `Server~/bridge_server.py` | S4~S7·S10·S14 | R7 |
>
> **권장 순서: Task 10 → Task 8.** Task 10은 작고 의미가 바뀌는 변경이고 Task 8은 같은 함수의 큰 재작성이다. 반대로 하면 Task 8이 재작성한 코드를 다시 손대야 한다. (Task 9는 신규 파일 위주라 병렬 안전)

## 1. 목표

- MCP로 호출했을 때 **사람 개입 없이 끝나거나, 끝나지 않는 이유가 응답에 담긴다**.
- 이미 적용한 항목을 **다시 적용하지 않는다**.
- 되돌리기 어려운 적용(프리팹 저장) 전에 **예측 불가능한 상황을 미리 걸러 안내**한다.

## 2. 작업 항목

### R1. MCP 경로에서 모달 다이얼로그 제거 — 높음

**근거**: `ComfyUIGenerator/CandidateGenerator.cs:387` — `RunPreflightAsync`가 사전 검증 실패 시 `EditorUtility.DisplayDialog`를 띄우고 `OperationCanceledException`을 던진다. 이 함수는 `GenerateAsync`(`:164`) 안에 있어 **창과 MCP가 같은 경로를 공유**한다.

두 경로에서 서로 다른 방식으로 깨진다.

| 호출 경로 | 증상 |
|-----------|------|
| `mcptools_generate_candidates` (`ComfyUIGeneratorTool.cs:108` → `RunJobAsync` → `GenerateAsync`) | 호출은 `started`를 즉시 반환하지만 **에디터가 모달로 잠기고 Job은 사람이 클릭할 때까지 `running`** 으로 남는다. Task 8 M7의 "running 고착"과 겹치면 같은 항목 재생성이 계속 막힌다 |
| `mcptools_run_pipeline` (`Pipeline/PipelineTool.cs:106`) | `Task.Run`(스레드풀)에서 호출하므로 `DisplayDialog`가 **메인 스레드 위반 예외**를 던진다 → 유용한 preflight 안내가 `"후보 생성 실패: ..."` 로 변질된다. 같은 파일 `:99-101` 주석이 `AssetDatabase.Refresh()`에 대해 동일한 문제를 이미 인정하고 우회 중이다 |

**대응**: `GenerateAsync`에 상호작용 여부 인자를 추가한다 (`SpriteSheetImporter`가 이미 쓰는 `showProgress` 패턴과 동일).

- `interactive: true`(창) → 지금처럼 다이얼로그 + 취소
- `interactive: false`(MCP·파이프라인) → 다이얼로그 없이 **원인·조치가 담긴 예외 메시지만** 던진다. 메시지 본문은 `BuildPreflightFailureMessage`(`:392`)를 그대로 재사용
- 호출부 수정: `ComfyUIGeneratorTool.RunJobAsync`, `PipelineTool`, `ComfyUIGeneratorWindow`
- 같은 관점에서 `CandidateGenerator` 안의 나머지 `DisplayDialog` 호출도 전부 점검한다

**추가 점검**: `Editor/` 전체에서 MCP 경로에 도달할 수 있는 `DisplayDialog`/`DisplayProgressBar`가 더 없는지 확인한다 (현재 `SpriteSheetImporter`는 `showProgress` 인자로 이미 분리돼 있음).

### R2. Play Mode · 컴파일 중 가드 — 중간

**근거**: `isPlaying` / `isCompiling` 검사가 코드베이스에 **0건**이다.

- 플레이 중 `AssetDatabase.Refresh()`(`CandidateGenerator.cs:350`, `PipelineTool.cs:115` 등)는 도메인 리로드를 유발해 **진행 중인 async 생성과 플레이 세션을 함께 끊을 수 있다**.
- MCP 에이전트는 에디터가 Play Mode인지 컴파일 중인지 모른 채 호출한다.

**대응**

- `Common/McpToolRegistry`의 실행 진입점에 공통 가드를 넣어, Play Mode·컴파일 중이면 **실행하지 않고** 원인·조치를 담아 실패시킨다 (`"에디터가 Play Mode입니다. 재생을 멈춘 뒤 다시 호출해주세요."`).
- 예외적으로 허용할 도구가 있으면 화이트리스트로 둔다 (`mcptools_ping`, `mcptools_status`처럼 읽기 전용인 것).
- 창 쪽은 실행 버튼을 비활성화하고 사유를 표시한다 (생성·적용·스캔 버튼).

### R3. 적용 이력 저장 — 중간

**근거**

- `AssetApplier/AssetApplierWindow.cs:742`에서만 `ItemState.applied = true`를 쓰고, 로드 경로인 `RebuildStates()`(`:804`)는 복원하지 않는다 → **창을 다시 열면 이미 적용한 항목도 "적용 준비"** 로 돌아온다.
- `ReadyToApply`(`:46`)가 `!applied` 조건이라 **[일괄 적용]이 이미 적용된 항목까지 전부 다시 적용**한다 → 프리팹 재저장 → 불필요한 git diff. Task 8 C17(프리팹 그룹핑)이 들어가도 이 낭비는 남는다.
- 스키마에 있는 `AssetListItem.status`("pending/prompted/generated/applied", PLAN §3.4)는 파이프라인이 한 번도 전진시키지 않는 **죽은 필드**다. 실제 쓰기는 `"pending"`(`AssetListPromptBuilder.cs:288`), `"대상 미정"`(`AssetListupWindow.cs:1167`), 수동 TextField(`AssetListupWindow.cs:467`)뿐.

**대응** (둘 중 하나를 고르고 이유를 체크리스트에 남긴다)

- **(a) 결과 파일에 기록** — `GenerationResults.json`에 적용 기록(항목 id / 대상 / 적용 시각 / 적용한 에셋 경로)을 남기고, `RebuildStates`가 이를 읽어 `applied`를 복원한다. AssetList JSON을 건드리지 않아 1단계 산출물이 오염되지 않는다. **권장**
- **(b) `status` 전진** — 적용 성공 시 `AssetListItem.status = "applied"`로 쓰고 저장. 스키마 의도에 맞지만 AssetList JSON을 4단계가 쓰게 되고, 사용자가 수동 편집하는 열이라 충돌 여지가 있다.

어느 쪽이든:

- 이미 적용된 항목도 **의도적으로 다시 적용**할 수 있어야 한다 (개별 [적용]은 항상 가능, 일괄에서만 제외 + "N개 이미 적용됨(다시 적용하려면 …)" 표시).
- 적용 기록과 실제 프리팹 값이 어긋난 경우(사용자가 손으로 바꿈)를 어떻게 볼지 정한다 — 기록만 신뢰(단순) vs 현재 값 비교(정확하지만 Task 8 C1과 충돌). **기록만 신뢰 권장**.

### R4. 프리팹 변종 · Prefab Stage 검출 — 중간 (미검증 위험)

**근거**: `AssetApplier`는 `LoadAssetAtPath` + `PrefabUtility.SavePrefabAsset`만 쓴다(`AssetApplier.cs:500`, `:519`). `GetPrefabAssetType`·`IsPartOfVariantPrefab`·`PrefabStageUtility` 검사가 없다.

- **Prefab Variant**: 베이스에서 상속한 컴포넌트에 값을 쓰면 변종 오버라이드로 기록되는지, 의도대로 저장되는지 검증되지 않았다.
- **Prefab Stage(프리팹 모드로 열려 있는 상태)**: 열린 스테이지에 미저장 변경이 있는 채로 에셋을 직접 저장하면 **한쪽 변경이 유실될 수 있다.**
- 참고로 `ProjectScanner`는 `PrefabUtility.IsPartOfPrefabInstance`(`:258`, `:290`) 등으로 인스턴스를 이미 구분하고 있어, 스캔과 적용의 인지 수준이 어긋난다.

**대응**

1. 먼저 **실제 동작을 확인**한다 (변종 2단 + 중첩 프리팹 테스트 에셋을 만들어 적용해 보고 결과를 체크리스트에 기록). 추측으로 분기하지 않는다.
2. 확인 결과에 따라:
   - 대상 프리팹이 **Prefab Stage로 열려 있으면** 적용 전에 걸러 안내한다(닫거나 저장 후 재시도).
   - 변종·중첩에서 의도대로 동작하지 않으면 검증 사유로 걸러 안내한다. 의도대로 동작하면 **그 사실을 README에 명시**하고 코드는 두다.

### R5. id 충돌 · 덮어쓰기 무경고 — 낮음

**근거**

- `CandidateGenerator.SanitizeId`(`:639`)가 파일명 금지 문자를 `_`로 치환한다 → 서로 다른 항목 id가 **같은 파일명으로 충돌**하면 확정본(`3_Confirmed/Images/{id}.png`)과 후보 폴더가 조용히 덮어써진다.
- `SpriteSheetImporter`의 시트 저장(`:1439`)도 같은 이름이면 무경고 덮어쓰기다.

**대응**: 충돌을 검출해 안내한다. AssetList 저장·후보 생성 시 sanitize 결과가 같은 id가 둘 이상이면 경고(1단계 `warnings`에 추가), 시트 저장은 기존 파일이 있으면 창에서 확인을 받는다(MCP 경로는 응답에 덮어썼음을 명시).

### R6. 문서 스키마 버전 필드 — 낮음 (지금이 가장 쌈)

**근거**: AssetList / PromptSet / GenerationResults 어디에도 `schemaVersion`이 없다(`AssetListDocument.cs:112-131` 등). 지금까지는 키 추가만 해서 무해했지만(최근 `animatorControllerPath` 추가), **의미가 바뀌는 변경**이 오면 구 파일이 조용히 잘못 로드된다.

**대응**: 세 문서에 `schemaVersion`(정수, 현재 1)을 추가한다. 없으면 1로 간주(구 파일 그대로 로드). 로더는 **아는 버전보다 큰 값이면 경고**하고 계속 진행한다(막지 않는다).

### R7. 브리지 서버 하드닝 — 낮음~중간 / 비용 낮음

**근거**

- **Host/Origin 검증 없음**: 127.0.0.1 바인딩이고 `/shutdown`은 바인딩 주소로 가드(`bridge_server.py:812`)되어 있지만, `do_POST`(`:628`)가 Content-Type을 보지 않고 `json.loads`한다. 사용자가 방문한 웹페이지가 `text/plain` 단순 요청(프리플라이트 없음)으로 `/generate`·`/free`·`/shutdown`을 호출할 수 있다 — 응답은 못 읽어도 **부작용은 발생**한다.
- **입력 상한 없음**: `count = max(1, int(req.get("count") or 4))`(`:659`)에 상한이 없어 오타 하나로 수천 건이 큐잉된다. 본문 길이 제한도 없다.

**대응**

- `Host` 헤더가 `127.0.0.1:<port>` / `localhost:<port>` 계열이 아니면 403. (`--host`로 외부 바인딩한 경우의 처리 방침도 함께 정한다)
- `count` 상한(예: 32)과 본문 길이 상한을 두고, 초과 시 이유를 담아 400.
- 위 두 변경을 브리지 `BRIDGE_VERSION`과 README 문제 해결 절에 반영한다.

### R8. 통합 창에 스프라이트 시트 안내 — 낮음

**근거**: `Pipeline/PipelineWindow.cs:103-151`이 4단계만 그린다. Task 6/6-1로 시트 → 슬라이스 → 클립 → 적용까지 갖춰졌는데 통합 창에 진입점이 없어 발견되지 않는다.

**대응**: 4단계 아래에 "스프라이트 시트(선택)" 안내 블록 + [Sprite Sheet 창 열기] 버튼을 추가한다. 상태 판정까지는 하지 않아도 된다(산출물 위치가 항목별 확정본과 다르므로).

## 3. 이번 Task에서 하지 않는 것

- **파라미터 파서 통합** — `GetString`/`GetBool`/`GetInt`가 14곳에 중복돼 있고 의미도 엇갈린다(`AssetApplierTool.cs:194`는 없으면 `null`, `AssetListDocument.cs:166`은 `""`). 통합 자체는 옳지만 **모든 도구 파일을 건드려 Task 8과 대량 충돌**한다. Task 9의 테스트가 깔리고 Task 8이 끝난 뒤에 별도로 한다.
- **대형 파일 분리** (`ComfyUIGeneratorWindow.cs` 2,659줄 등) — Task 8의 GUI 캐시화와 정면 충돌한다.
- Task 8이 담당하는 성능·메모리 항목 전부.

## 4. 검증 방법

1. **R1** — ComfyUI에 없는 커스텀 노드를 쓰는 워크플로로 `mcptools_generate_candidates` 호출 → **다이얼로그 없이** Job이 `failed`가 되고 메시지에 누락 노드·조치가 담긴다. `mcptools_run_pipeline`도 같은 메시지가 `failed`에 담긴다(스레드 위반 예외 아님).
2. **R2** — Play Mode에서 `mcptools_apply_all` 호출 → 적용되지 않고 사유가 반환된다. 컴파일 중 호출도 동일.
3. **R3** — 항목을 적용하고 창을 닫았다 다시 열면 "적용됨"이 유지되고, [일괄 적용]의 대상 개수에서 빠진다. 개별 [적용]은 여전히 가능하다.
4. **R4** — 변종 프리팹 / 중첩 프리팹 / Prefab Stage로 열린 프리팹 각각에 적용해 결과를 기록한다.
5. **R5** — 같은 이름으로 sanitize되는 id 두 개를 만들어 경고가 뜨는지 확인.
6. **R6** — 구 JSON(`schemaVersion` 없음) 로드 정상, `schemaVersion: 99` 파일 로드 시 경고 후 진행.
7. **R7** — `curl -H "Host: evil.example" http://127.0.0.1:8189/health` 가 403. `count: 9999` 요청이 400.
8. **R8** — 통합 창에서 Sprite Sheet 창이 열린다.
9. **회귀** — Task 1~7·6-1의 에디터 테스트 중 **4단계 적용·3단계 생성 경로**를 다시 수행한다.

## 5. 산출물

- 개선된 `Assets/MCPTools/` (MCP 비대화형 경로, Play Mode 가드, 적용 이력, 프리팹 안전 검사, 스키마 버전)
- 개선된 `Server~/bridge_server.py` (Host 검증, 입력 상한)
- 갱신된 `Assets/MCPTools/README.md` (적용 이력 동작, 프리팹 변종·Prefab Stage 제약, 브리지 Host 정책), `CHANGELOG.md`

## 6. 완료 조건

- 체크리스트: [Task10_체크리스트.md](../checklist/Task10_체크리스트.md)
- R1~R8 구현 (R4는 "확인 후 결정"이므로 확인 결과와 결정 근거를 체크리스트에 기록하면 완료로 본다)
- §4 검증 1~9 통과
- 사용자 에디터 테스트 통과
