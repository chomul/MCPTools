# Task 10 체크리스트 — 정확성 · 견고성

> Task 문서: [Task10_정확성견고성.md](../tasks/Task10_정확성견고성.md)
> 2026-07-25 작성: 조사 완료, 구현 미착수.
> **⚠️ Task 8과 같은 파일(`AssetApplier.cs`·`AssetApplierWindow.cs`·`CandidateGenerator.cs`·`PipelineTool.cs`·`bridge_server.py`)을 고친다. 동시 진행 금지 — Task 10 → Task 8 순서 권장.**

## 1. 구현 체크리스트

### R1. MCP 경로에서 모달 다이얼로그 제거 (높음)

- [x] `CandidateGenerator.GenerateAsync`에 상호작용 여부 인자 추가 (`SpriteSheetImporter`의 `showProgress` 패턴)
- [x] `RunPreflightAsync` — 비대화형이면 `DisplayDialog` 없이 `BuildPreflightFailureMessage` 내용을 예외 메시지로만 전달
- [x] 호출부 갱신 — `ComfyUIGeneratorTool.RunJobAsync`(비대화형), `PipelineTool`(비대화형), `ComfyUIGeneratorWindow`(대화형)
- [x] `Editor/` 전체에서 MCP 경로에 도달 가능한 `DisplayDialog`/`DisplayProgressBar`가 더 없는지 전수 확인 후 결과 기록

**구현 결과**

- 최종 시그니처: `GenerateAsync(settings, item, workflowName, variables, baseSeed, bool interactive = false, IProgress<float> progress = null, CancellationToken ct = default)`.
  기본값을 `false`(비대화형)로 둬서, 앞으로 호출부를 새로 추가할 때 인자를 빠뜨려도 모달이 뜨지 않는 쪽으로 실패한다.
- **비대화형에서 던지는 예외를 `InvalidOperationException`으로 정했다** (`OperationCanceledException` 아님). 호출부(`ComfyUIGeneratorWindow`의 일괄 생성 등)가 `catch (OperationCanceledException)`으로 "사용자 취소"를 구분하고 있어, 사전 검증 실패를 같은 타입으로 던지면 취소로 오인된다. MCP 경로에서는 Job이 `failed`가 되어야 하므로 별도 타입을 쓴다.
- 호출부 4곳 (전역 grep으로 누락 없음 확인):

  | 파일 | 인자 |
  |------|------|
  | `ComfyUIGenerator/ComfyUIGeneratorTool.cs` `RunJobAsync` | `interactive: false` |
  | `Pipeline/PipelineTool.cs` `ExecuteRunPipeline` | `interactive: false` |
  | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs` 일괄 생성 | `interactive: true` |
  | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs` 단건 생성 | `interactive: true` |

  창 2곳은 기존에 `progress`/`ct`를 위치 인자로 넘기고 있었다. 그대로 두면 `progress`가 새 `interactive` 자리에 바인딩되므로 named argument로 전환했다.

**전수 조사 결과 — MCP 경로에 도달 가능한 모달 0건**

`DisplayDialog`/`DisplayProgressBar`/`DisplayCancelableProgressBar`/`ClearProgressBar` 총 84건 / 8파일.

| 파일 | 건수 | 판정 |
|------|------|------|
| `ComfyUIGenerator/CandidateGenerator.cs` | 1 | **이번에 `interactive`로 분리** |
| `SpriteSheet/SpriteSheetImporter.cs` | 7 | 이미 `showProgress`로 분리됨 (7건 전부 `if (showProgress)` 안, 가드 밖 0건) |
| `Common/MCPSettingsWindow.cs` | 7 | 창 전용 |
| `AssetApplier/AssetApplierWindow.cs` | 4 | 창 전용 |
| `SpriteSheet/SpriteSheetPromptWizard.cs` | 8 | 창 전용 |
| `AssetListup/AssetListupWindow.cs` | 22 | 창 전용 |
| `PromptBuilder/PromptBuilderWindow.cs` | 15 | 창 전용 |
| `ComfyUIGenerator/ComfyUIGeneratorWindow.cs` | 20 | 창 전용 |

"창 전용"의 근거: 6개 창 타입의 `public static` 멤버는 전부 `Open()`뿐이고, `*Tool.cs`(MCP 등록 파일) 중 창 타입을 참조하는 파일이 하나도 없다. 창 타입 심볼이 등장하는 곳은 `PipelineWindow`가 다른 창의 `Open()`을 부르는 것과 XML 주석 참조가 전부다.
`SpriteSheetTool.cs`는 `Import(..., showProgress: false, ...)`·`Detect(..., false)`로 실제로 `false`를 넘기고 있고(창 경로인 `SpriteSheetPromptWizard`는 `true`), 분리가 실제로 동작 중임을 확인했다.

**관련 파일**: `Editor/ComfyUIGenerator/CandidateGenerator.cs`, `ComfyUIGeneratorTool.cs`, `ComfyUIGeneratorWindow.cs`, `Editor/Pipeline/PipelineTool.cs`

### R2. Play Mode · 컴파일 중 가드 (중간)

- [x] `McpToolRegistry` 실행 진입점에 공통 가드 — Play Mode·컴파일 중이면 실행하지 않고 원인·조치를 담아 실패
- [x] 읽기 전용 도구 화이트리스트 결정 및 기록
- [x] 창의 생성·적용·스캔 버튼을 같은 조건에서 비활성화 + 사유 표시

**구현 결과**

- `McpToolRegistry.GetBlockedReason(string toolName = null)` 공용 헬퍼를 신설하고, `Execute`가 도구 조회 직후·파라미터 파싱 전에 호출한다. 차단 시 예외를 던지지 않고 기존 `MakeResult(false, ...)` 경로로 사유·조치를 담아 반환한다.
- 차단 조건: `EditorApplication.isPlaying || isPlayingOrWillChangePlaymode` / `isCompiling || isUpdating`.
  재생 진입 **예약** 상태(`isPlayingOrWillChangePlaymode`)까지 막는 이유는, 그 시점에 시작한 작업이 곧 이어지는 도메인 리로드에 끊기기 때문이다.
- `toolName`이 null이면(창에서 호출) 화이트리스트를 적용하지 않고 상태만 판정한다 → 창과 MCP가 같은 판정 로직을 공유한다.

**화이트리스트 결정 — 허용 3개**

`mcptools_ping`, `mcptools_status`, `mcptools_list_candidates`.

기준은 "디스크·에셋에 아무것도 쓰지 않고 async 작업도 시작하지 않는 진단/폴링 도구만". `list_candidates`를 넣은 이유는 생성 Job의 완료를 확인하는 **폴링 경로**라서, 막으면 에이전트가 진행 상황을 알 수 없기 때문이다.

제외한 12개의 근거 (도구별로 구현을 열어 확인):

| 도구 | 부작용 | 근거 |
|------|--------|------|
| `mcptools_asset_scan` | **씬 추가 열기/닫기** | `ProjectScanner.cs:126`(`OpenScene`), `:139`(`CloseScene`) |
| `mcptools_asset_list_save` | 파일 쓰기 + Refresh/Import | `AssetListBuilder.cs:235,239,245,249` |
| `mcptools_prompt_save` | 파일 쓰기 + Refresh/Import | `PromptBuilder.cs:168,172,178,182` |
| `mcptools_generate_candidates` | async Job 시작 + 후보 쓰기 + Refresh | `ComfyUIGeneratorTool.cs:108`, `CandidateGenerator.cs:350` |
| `mcptools_select_candidate` | 파일 복사·ImportAsset·결과 기록·Refresh | `CandidateGenerator.cs:342-357` |
| `mcptools_run_pipeline` | 생성+Refresh+확정+일괄 적용+SaveAssets | `PipelineTool.cs:106,116,152,231,252` |
| `mcptools_apply_asset` / `mcptools_apply_all` | 프리팹/씬 적용 + SaveAssets | `AssetApplierTool.cs:84,90,135,157` |
| `mcptools_spritesheet_build_prompt` | 프롬프트 JSON 저장 | `SpriteSheetTool.cs:87` |
| `mcptools_spritesheet_import` | 시트 PNG 저장·슬라이스 | `SpriteSheetTool.cs:139` |
| `mcptools_spritesheet_build_clips` | `.anim`/`.controller` 생성·프리팹 수정 | `SpriteSheetTool.cs:231,248` |
| `mcptools_prompt_scan` | **쓰기 없음(파일 읽기만)** — 아래 참조 | `PromptBuilder.cs:85-104` |

- **`mcptools_prompt_scan`은 판단이 갈릴 수 있는 한 건이다.** 확인 결과 쓰기가 전혀 없어 기준상으로는 허용해도 된다. 그럼에도 제외한 이유는 ① PromptBuilder 창의 스캔 버튼을 비활성화하는데 MCP만 열어두면 판정이 엇갈리고, ② 진단·폴링이 아니라 곧바로 `prompt_save`로 이어지는 파이프라인 단계이기 때문이다. 정책을 바꾸려면 `McpToolRegistry.ReadOnlyTools`에 한 줄 추가하면 된다.
- 허용한 3개도 `MCPToolSettings.GetOrCreate()`를 거치므로 설정 에셋이 없는 프로젝트에서 **최초 1회만** 설정 에셋을 만든다(도메인 리로드는 유발하지 않는 부트스트랩). 엄밀히 "쓰기 0"은 아니어서 코드 주석에 명시했다.

**창 4개 변경** — 공통 패턴: `private string _blockedReason;` 필드 + `OnGUI` 상단에서 1회 산출 + `HelpBox(MessageType.Warning)` 1회 + 버튼 `DisabledScope` 조건에 `|| _blockedReason != null` 합성. 레이아웃·문구·기존 로직·`Repaint` 훅은 변경하지 않았다.

| 창 | 비활성화한 버튼 |
|----|------|
| `ComfyUIGeneratorWindow` | 후보 생성/재생성/일괄 생성(기존 `!canGenerate` 스코프에 합성), 확정 |
| `AssetApplierWindow` | 선택 적용, 일괄 적용, 대상 편집 [저장] |
| `AssetListupWindow` | 스캔+휴리스틱 추출, AI용 프롬프트 복사(둘 다 씬을 연다), 항목 추가·저장, 선택한 AI로 목록 생성 |
| `PromptBuilderWindow` | 선택한 AI로 프롬프트 생성, 템플릿 초안 생성(보조), 항목 추가·저장 |

의도적으로 **열어 둔 것**: 생성/AI 실행 중의 [취소], 불러오기·새로고침, 서버 시작/종료, PromptBuilder의 [AI용 프롬프트 복사](이 창에서는 읽기 전용), [AI 응답 JSON 불러오기](메모리 반영만). 되돌리기 쉽고 에셋을 건드리지 않는 조작은 막을 이유가 없다.

**관련 파일**: `Editor/Common/McpToolRegistry.cs`, `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`, `Editor/AssetApplier/AssetApplierWindow.cs`, `Editor/AssetListup/AssetListupWindow.cs`, `Editor/PromptBuilder/PromptBuilderWindow.cs`

### R3. 적용 이력 저장 (중간)

- [x] 방식 결정: **(a) GenerationResults.json 기록** 채택
- [x] 적용 성공 시 이력 기록 (항목 id / 대상 / 적용 시각 / 적용한 에셋 경로)
- [x] `AssetApplierWindow.RebuildStates()`가 이력을 읽어 `applied` 복원
- [x] 일괄 적용 대상에서 제외하되 **개별 [적용]은 항상 가능**, "N개 이미 적용됨" 표시
- [x] 기록과 실제 값이 어긋난 경우의 방침 결정 — **기록만 신뢰**
- [x] 죽은 필드였던 `status`를 어떻게 할지 결정 — **1단계 수동 열로 문서화** (코드로 전진시키지 않음)

**방식 (a)를 고른 이유**

(b) `AssetListItem.status` 전진은 스키마 의도에는 맞지만 채택하지 않았다.

- AssetList JSON은 **1단계 산출물이고 사용자가 손으로 편집하는 문서**다 (`AssetListupWindow.cs:467`의 수동 TextField). 4단계가 여기에 쓰면 사용자가 편집 중인 문서와 충돌하고, 1단계 산출물이 4단계 부작용으로 오염된다.
- AI 중립 설계상 AssetList는 외부 AI가 만든 결과를 담는 문서다. 도구가 그 문서를 되쓰는 흐름을 만들면 재생성·재붙여넣기 때마다 이력이 날아간다.
- `GenerationResults.json`은 이미 3단계 도구가 **소유해 누적 기록**하는 파일이고 항목 id 기준 대체 구조가 있어 확장이 자연스럽다. 1단계 산출물을 건드리지 않는다.

`status`는 그대로 두고 "1단계에서 사람이 관리하는 수동 열"로 README에 문서화한다. (a)를 택한 이상 `status`도 함께 쓰면 **진실의 원천이 둘**이 되어 어긋날 때 어느 쪽을 믿을지 정해야 한다.

**기록만 신뢰하는 이유**: 현재 값 비교는 항목 수만큼 프리팹을 로드해야 하고, 그 비용을 창 리페인트마다 치르게 된다 (Task 8 C1의 리페인트 캐시화와 정면 충돌). 어긋났을 때의 탈출구는 개별 [선택 적용]이 항상 가능하다는 것으로 충분하다.

**구현 결과**

- 신규 `Editor/AssetApplier/ApplyHistory.cs` (`internal static class`). 배포 패키지의 공개 표면을 늘리지 않기 위해 internal로 뒀고(`MCPToolFolders` 선례), `AssemblyInfo.cs`의 `InternalsVisibleTo`로 테스트에서는 접근 가능하다.
  - `Record(AssetListItem, ApplyResult)` — 성공 건만 기록, 같은 id는 대체. **모든 예외를 삼키고 `Debug.LogWarning`만** 남긴다 (이력 기록 실패가 적용 자체를 실패시키면 안 된다).
  - `LoadAppliedItemIds(settings)` → `HashSet<string>`. 창이 `applied` 불리언만 쓰고 적용 경로를 표시하는 UI가 없어서 Dictionary가 아닌 HashSet을 골랐다.
- JSON 형태 — `results`(3단계)와 `applications`(4단계)가 **형제 키**:

  ```json
  {"schemaVersion":1,
   "results":[{"assetItemId":"item_001", ..., "confirmedAt":"..."}],
   "applications":[{"assetItemId":"item_001","targetPrefabPath":"...","targetScenePath":"",
                    "targetObjectPath":"","appliedAssetPath":"...","spriteName":"",
                    "linkedControllerPath":"","appliedAt":"2026-07-25 18:31:40"}]}
  ```

- **양방향 보존** (가장 깨지기 쉬운 지점): `CandidateGenerator`에 internal 헬퍼 `LoadResultsDocument` / `SaveResultsDocument` / `ReadSchemaVersion`을 두고 **양쪽이 같은 코드를 쓰게** 했다. 두 경로 모두 "문서 전체를 읽어 **자기 배열 하나만** 갈아끼우고 다시 쓴다" — 키 화이트리스트가 아니라서 나중에 키가 늘어도 보존이 깨지지 않는다.
  - `RecordResult`는 `doc["results"]`만 교체 → `applications` 보존
  - `ApplyHistory.Record`는 `doc["applications"]`만 교체 → `results`·`schemaVersion` 보존
  - `GetConfirmedOutputPaths`도 `LoadResultsDocument`를 쓰도록 정리해, 이 문서를 읽는 경로가 갈라지지 않게 했다.
- **R6 후속 수정**: `SaveResultsDocument`가 `schemaVersion`을 `Math.Max(읽은 버전, CurrentSchemaVersion)`으로 기록한다. 기존 레코드는 원본 그대로 보존되므로, 미래 버전(예: 2) 문서에 기록을 추가하면서 라벨만 1로 되돌리면 사실과 어긋난다.
- **기록 호출 지점 — 정확히 1회 보장**: 성공 경로의 말단은 `ApplyToPrefab`과 `ApplyToOpenScene` 둘뿐이고 서로를 호출하지 않는다. 진입점 4개(`mcptools_apply_asset` → `Apply`, `mcptools_apply_all` → `ApplyBatch`, 창 `ApplyStates` → `ApplyBatch`, `PipelineTool` → `ApplyBatch`)가 전부 이 둘을 통과한다. 두 함수의 `try/catch` **바깥**에서 각각 1회 호출한다 (기록 중 예외가 catch에 걸려 성공 메시지가 오류로 변질되지 않게).
- **창**: `ItemState.CanApplyAgain`(확정본 있음 + 검증 통과, `applied` 무시) 신설, `ReadyToApply = !applied && CanApplyAgain`으로 한쪽이 다른 쪽을 참조하게 해 중복 제거. 개별 [선택 적용]은 `CanApplyAgain`을, 일괄은 `ReadyToApply`를 쓴다. R2의 `|| _blockedReason != null` 조건은 양쪽 모두 유지된다. 버튼 행 아래에 `이미 적용됨 N개 — 일괄 적용에서 제외됩니다. 다시 적용하려면 …` 라벨(N=0이면 미표시).

**알려진 경계 사례**: `ApplyToScene`이 씬을 직접 열어 적용한 뒤 `SaveScene`이 실패하면 이력만 남는다(이력은 `ApplyToOpenScene`에서 이미 씀). "기록만 신뢰" 방침에 부합하고 [선택 적용]으로 복구 가능해 그대로 뒀으며 코드 주석에 명시했다.

**관련 파일**: `Editor/AssetApplier/ApplyHistory.cs`(신규), `Editor/AssetApplier/AssetApplier.cs`, `Editor/AssetApplier/AssetApplierWindow.cs`, `Editor/ComfyUIGenerator/CandidateGenerator.cs`

### R4. 프리팹 변종 · Prefab Stage (중간, 확인 먼저)

- [x] **확인**: 변종 2단 프리팹의 상속 컴포넌트에 적용 → 결과 기록
- [x] **확인**: 중첩 프리팹 안의 오브젝트에 적용 → 결과 기록
- [x] **확인**: 대상 프리팹을 Prefab Mode로 열고 미저장 변경이 있는 상태에서 적용 → 결과 기록
- [x] 확인 결과에 따라 조치
- [x] `ProjectScanner`와 인지 수준을 맞출지 결정 — **맞추지 않는다**

## 확인 결과 (2026-07-26, Unity 6000.5.2f1에서 실측)

추측이 아니라 실제로 확인용 프리팹을 만들어 `mcptools_apply_asset`으로 적용하고 디스크 파일을 직접 읽어 검증했다.
확인용 에셋은 `Assets/MCPTools.User/Task10Verify/`에 만들었고 확인 후 전부 삭제했다 (`GenerationResults.json`에 남은 확인용 기록 3건도 제거, 실제 확정 기록 13건은 그대로).

**확인 1 — 변종 2단 (Base → Variant1 → Variant2)**

`VerifyVariant2`의 상속 SpriteRenderer에 스프라이트 적용:

| 대상 | 적용 후 sprite |
|------|------|
| VerifyBase (베이스) | null (변화 없음) |
| VerifyVariant1 (중간 변종) | null (변화 없음) |
| **VerifyVariant2 (적용 대상)** | **VerifySpriteA** |

- `VerifyVariant2.prefab` 파일에 `m_Sprite` 항목이 생겼고, `VerifyBase.prefab` 파일에는 스프라이트 GUID 참조가 없다 → **변종 오버라이드로 정확히 기록된다.**
- 변종 인스턴스를 만들어 `GetPropertyModifications`를 봤을 때 sprite 관련 modification이 0건 → 인스턴스 오버라이드가 아니라 **변종 에셋 자체에 저장**된 것이 맞다.
- **결론: 의도대로 동작한다. 코드 분기 불필요.**

**확인 2 — 중첩 프리팹 (Outer 안에 Base 인스턴스)**

`VerifyOuter/NestedBase`의 SpriteRenderer에 적용:

- `VerifyOuter.prefab`에 `m_Sprite` 오버라이드가 기록되고, 원본 `VerifyBase.prefab`은 변하지 않았다.
- **결론: 의도대로 동작한다. 코드 분기 불필요.**

**확인 3 — Prefab Stage (프리팹 모드로 열려 있고 미저장 변경 있음)**

Task 문서가 우려한 "한쪽 변경이 유실될 수 있다"는 **재현되지 않았다.**

- 스테이지에서 자식 이름을 바꾸고 자식 sprite를 B로 지정한 미저장 상태에서 루트에 A를 적용 → 디스크 파일에 **적용값 A와 스테이지의 미저장 변경이 모두** 기록됐다. 유실 없음.
- 같은 필드를 두고 충돌시킨 경우(스테이지=B 미저장, 적용=A) → **적용값 A가 이기고** 스테이지도 A로 갱신되며 `isDirty`가 false가 된다. 이후 스테이지를 저장 없이 닫아도 되돌아가지 않는다.
- **대신 다른 문제를 발견했다**: 유실이 아니라 **사용자가 저장하지 않고 작업 중이던 프리팹 모드 변경이 적용과 함께 디스크에 커밋되어 버린다.** 사용자는 더 이상 그 변경을 버릴 수 없다. MCP 에이전트가 `mcptools_apply_all`을 돌리면 사용자 동의 없이 이 일이 일어난다 — Task 10 목표 3번("되돌리기 어려운 적용 전에 예측 불가능한 상황을 미리 걸러 안내")에 정확히 해당한다.
- **중요한 부수 발견**: Unity 프리팹 모드의 **Auto Save가 기본 켜짐(`PrefabStage.autoSave == true`)** 이라, 평소에는 변경이 즉시 저장되어 미저장 창이 거의 존재하지 않는다. 실제로 Auto Save가 켜진 채로는 적용이 그대로 통과했고, **Auto Save를 끈 상태에서만** 미저장 변경이 유지됐다. 즉 이 가드가 실제로 의미를 갖는 대상은 **Auto Save를 꺼두고 프리팹 모드에서 실험하는 사용자**다.

**조치 (확인 결과 기반)**

- 변종·중첩: **코드 변경 없음.** `GetPrefabAssetType`·`IsPartOfVariantPrefab` 검사를 넣지 않았다. 정상 동작하므로 README에 그 사실을 명시한다.
- Prefab Stage: `AssetApplier.ValidateItem`에 검사를 추가해 **대상 프리팹이 프리팹 모드로 열려 있고 `scene.isDirty`인 경우에만** 검증 실패로 걸러 안내한다.
  - **열려 있어도 저장된(깨끗한) 상태면 막지 않는다.** 함께 커밋될 미저장 변경이 없어 사용자가 잃을 것이 없는데 막으면 마찰만 생긴다. Auto Save 기본값 때문에 대부분의 경우가 여기 해당하므로, 무조건 막았다면 정상 사용을 크게 방해했을 것이다.
  - 씬 항목(`IsSceneItem`)에는 적용하지 않는다.
  - 한계: `PrefabStageUtility.GetCurrentPrefabStage()`는 현재 스테이지 하나만 돌려주므로, 프리팹 안의 프리팹을 파고들어 스테이지 히스토리가 쌓인 경우 상위 스테이지는 잡지 못한다. 유실은 없으므로 허용 가능한 한계로 판단하고 코드 주석에 남겼다.
- **`ProjectScanner`와 인지 수준 맞추기: 하지 않는다.** `ProjectScanner`가 쓰는 `IsPartOfPrefabInstance`는 **씬에 배치된 인스턴스**를 구분하기 위한 것이고, `AssetApplier`는 **프리팹 에셋 자체**를 연다. 확인 1·2에서 변종·중첩 모두 정상 동작함이 드러났으므로 같은 검사를 넣을 이유가 없다. 넣으면 정상 케이스를 막는 분기만 늘어난다.

**검증 (실제 MCP 호출)**

| 상황 | `mcptools_apply_asset` 결과 |
|------|------|
| 스테이지 안 열림 | ✅ 적용됨 |
| 스테이지 열림 + Auto Save 켜짐(깨끗) | ✅ 적용됨 (막지 않음) |
| 스테이지 열림 + Auto Save 끔 + 미저장 변경 | ❌ 차단 — "대상 프리팹 …이 프리팹 모드로 열려 있고 저장하지 않은 변경이 있습니다. … 조치: 프리팹 모드에서 저장(Ctrl+S)하거나 변경을 버리고 프리팹 모드를 닫은 뒤 다시 시도해주세요." |

**관련 파일**: `Editor/AssetApplier/AssetApplier.cs`

### R5. id 충돌 · 덮어쓰기 경고 (낮음)

- [x] `SanitizeId` 결과가 같은 항목 id가 둘 이상이면 1단계 저장 `warnings`에 추가
- [x] 후보 생성·확정 시 같은 파일명 충돌 검출 후 안내
- [ ] 시트 저장(`SpriteSheetImporter.cs:1439`)이 기존 파일을 덮어쓸 때 창은 확인, MCP는 응답에 명시 — **보류(사용자 확인 대기)**

**구현 결과**

- **충돌 판정 기준**: `CandidateGenerator.PreviewFileNameForId`(internal)를 새로 두고 1단계 검사와 3단계 저장이 **같은 규칙**을 쓰게 했다. `Path.GetInvalidFileNameChars()`는 플랫폼마다 다르고(Unix는 `/`와 NUL뿐) 1단계 산출물 문서는 다른 OS에서도 열리므로, **현재 OS 기준 금지 문자 ∪ Windows 기준 금지 문자(`" < > | : * ? \ /` + 제어 문자)** 로 판정한다. Unix 전용 프로젝트에서는 실제로 충돌하지 않을 조합까지 경고할 수 있지만, 경고만 남기고 저장·생성을 막지 않으므로 놓치는 쪽보다 낫다.
- **(1) 1단계 저장**: `AssetListBuilder.Validate`가 `FindIdCollisionWarnings`를 호출해 경고를 `warnings`에 추가한다. 저장은 막지 않는다. 완전히 같은 id가 중복된 경우와 sanitize 후에만 충돌하는 경우를 다른 문구로 구분한다.
- **경고가 사용자에게 닿는 경로**: `mcptools_asset_list_save`는 응답 `warnings`로 반환한다(`AssetListupTool.cs:143`). 그런데 **창의 저장 경로(`AssetListupWindow.cs:1213`)는 `Validate`를 거치지 않아 경고가 보이지 않았다.** id를 손으로 고치는 곳이 바로 창이므로, `FindIdCollisionWarnings`를 `internal`로 올려 창의 저장 직후에도 안내하도록 보완했다. `Validate` 전체 경고가 아니라 **충돌 경고만** 띄운다 — "대상 프리팹 미기록" 같은 흔한 경고가 섞이면 소음이 되기 때문이다.
- **(2) 확정 시 충돌** (`ConfirmCandidate`): 확정본 저장 경로에 이미 파일이 있으면 `GenerationResults.json`의 `results` 기록으로 소유 항목을 판정한다. **같은 항목의 재확정은 정상 동작이라 경고하지 않고**, 다른 항목의 확정본을 덮어쓰거나 기록에 없는 파일을 덮어쓰는 경우만 `Debug.LogWarning`. 확정 자체는 **막지 않는다** — 사용자가 이미 선택한 동작이고 여기서 막으면 복구 수단이 없다.
- **(2) 후보 폴더 충돌** (`GenerateAsync`): `ClearFolder`가 다른 항목의 후보를 지우기 직전에 경고한다. 판정은 폴더의 후보 메타 JSON에 기록된 원본 `assetItemId`로만 하므로 오탐이 없고, 메타가 없는 구 후보 폴더에서는 조용히 넘어간다.

**관련 파일**: `Editor/ComfyUIGenerator/CandidateGenerator.cs`, `Editor/AssetListup/AssetListBuilder.cs`, `Editor/AssetListup/AssetListupWindow.cs`

### R6. 문서 스키마 버전 (낮음)

- [x] `AssetListDocument` / `PromptSetDocument` / GenerationResults에 `schemaVersion`(정수, 현재 1) 추가
- [x] 없으면 1로 간주해 구 파일 그대로 로드
- [x] 아는 버전보다 크면 **경고 후 계속 진행** (막지 않음)

**구현 결과**

- 세 문서 모두 `CurrentSchemaVersion = 1` 상수를 각 클래스(`AssetListDocument`, `PromptSetDocument`, `CandidateGenerator`)에 두고, 저장 시 **최상위 첫 키**로 기록한다. MiniJson의 `SerializeObject`가 `Dictionary` 삽입 순서를 따라가므로 JSON에서도 맨 앞에 나온다.
- 로드: 키 없음/`null` → 조용히 1로 간주하고 기존 경로 그대로 (구 파일 회귀 없음). 값 > 1 → `Debug.LogWarning` 1회 후 **그대로 계속 로드**. 정수로 해석 불가(불리언·객체·비숫자 문자열) → 조용히 1 취급.
- MiniJson은 JSON 정수를 **`long`**, 실수를 `double`로 돌려준다(`MiniJson.cs:261-279` 확인). 파싱 헬퍼는 `long`/`int`/`double`/문자열을 모두 견딘다.
- 공용 파서 클래스를 만들지 않고 각 파일에 private static 헬퍼 1개씩 뒀다. Task 문서 §3이 "파라미터 파서 통합"을 이번 Task에서 하지 않기로 못박았고, 공용화하려면 `Common/`에 파일을 추가해야 해 범위를 벗어난다.
- 경고 호출 지점은 OnGUI 매 프레임 경로가 아니다(`GetConfirmedOutputPaths`는 `RefreshItemStatuses`/`PipelineWindow.Refresh`/`PipelineTool`에서만 호출) → 로그 스팸 없음.

**확인한 사실**

- `AssetListDocument.FromDictionary` / `PromptSetDocument.FromDictionary` / 각 Item의 `FromDictionary`는 **아는 키만 `TryGetValue`로 꺼내고 나머지를 완전히 무시**한다. 따라서 구버전 MCPTools가 `schemaVersion`이 든 새 파일을 읽어도 안전하고, 이번 변경이 기존 로더를 깨뜨리지 않는다.
- AI 중립 설계에서 외부 AI가 만드는 것은 **항상 "항목 배열"이지 문서 전체가 아니다** (`AssetListPromptBuilder.GetItemSchema`, `PromptSetPromptBuilder.GetPromptSchema`가 item 스키마만 넘긴다). 최상위 문서는 언제나 `AssetListBuilder.Save`/`PromptBuilder.Save`가 만들어 저장하므로 **AI 프롬프트/템플릿에 `schemaVersion` 안내를 추가할 필요가 없다.** 사람이 문서 전체를 손으로 쓴 경우도 "키 없으면 1" 규칙으로 정상 동작한다.
- 기존에 저장된 산출물 JSON에는 `schemaVersion`이 없다(수정하지 않음). 다음 저장부터 자동으로 붙는다.

**관련 파일**: `Editor/AssetListup/AssetListDocument.cs`, `Editor/PromptBuilder/PromptSetDocument.cs`, `Editor/ComfyUIGenerator/CandidateGenerator.cs`

### R7. 브리지 서버 하드닝 (낮음~중간)

- [x] `Host` 헤더가 `127.0.0.1:<port>`/`localhost:<port>` 계열이 아니면 403
- [x] `--host`로 외부 바인딩한 경우의 정책 결정 및 기록
- [x] `count` 상한(32) + 본문 길이 상한, 초과 시 사유를 담아 400
- [x] `BRIDGE_VERSION` 상향, README 문제 해결 절에 반영

**구현 결과**

- `do_GET`/`do_POST` 선두에서 `check_host_header()`. 허용 판정: Host 헤더를 `(호스트명, 포트)`로 분리(대괄호 IPv6 `[::1]:8189`, 무괄호 `::1`, 포트 없음 모두 처리) → 호스트명이 `127.0.0.1`/`localhost`/`::1` 중 하나 **그리고** 포트가 생략됐거나 실제 `BIND_PORT`와 일치할 때만 통과. 그 외 403 + 원인·조치 메시지.
- Host 헤더가 아예 없는 요청(HTTP/1.0 등)은 브라우저발이 아니므로 통과시킨다.
- **`--host` 외부 바인딩 정책: Host 검증을 비활성화하고 기동 시 경고를 1회 출력한다.** 접속에 쓰일 호스트명(사설 IP·머신 이름·역방향 DNS)을 서버가 알 방법이 없어 허용 목록을 만들 수 없고, 운영자가 의도적으로 노출한 상황이기 때문이다. 근거를 `HOST_CHECK_ENABLED` 선언부와 `main()` 설정부 주석에 남겼다.
- 입력 상한: `do_POST`가 본문을 읽기 **전에** `Content-Length`를 검사. 기본 1 MiB, **`/upload`만 64 MiB**(참조 이미지 원본을 그대로 싣는 경로라 1 MiB로 막으면 스프라이트 시트 흐름이 깨진다 — Task 문서가 "예: 1 MiB"까지만 정해 재량 판단). 헤더 누락·비숫자·음수도 400.
- `count`: 누락/`null`/`0`이면 기본값 4(기존 동작 유지), **1 미만·32 초과·정수 아님은 조용히 클램프하지 않고 400으로 거절**한다. 오타 하나로 수천 건이 큐잉되는 것을 사용자가 알아야 하기 때문. `baseSeed`의 정수 변환 실패도 같은 방식으로 400.
- `BRIDGE_VERSION` 0.2.0 → **0.3.0**. Unity 쪽에 기대 최소 브리지 버전 상수·게이팅 로직은 **없다**(`ComfyUIServerLauncher.cs:247-273`이 `/health`의 `version`을 읽지만 경고 문구 표시용뿐). C# 변경 불필요.
- 회귀 확인: `Content-Length` 필수화가 `/shutdown`·`/free`를 깨뜨리지 않는다 — 둘 다 `{}` 본문을 실어 보낸다(`BridgeClient.cs:269`, `:626`).

**실제 검증** (포트 8189는 건드리지 않고 8199로 임시 기동 후 종료, `python -m py_compile` 통과)

| 요청 | 결과 |
|------|------|
| `Host: evil.example` → `/health` | **403** + 원인·조치 |
| 기본 / `Host: [::1]:8199` / `Host: LOCALHOST`(포트 생략) | 200 |
| `Host: 127.0.0.1:9999` (포트 불일치) | 403 |
| Host 헤더 없음 (HTTP/1.0) | 200 (정책대로 통과) |
| `count: 9999` / `33` / `-1` / `"abc"` | **400** + 사유 |
| 본문 1,100,031바이트 | 400 (상한 1048576) |
| `Content-Length` 없음(chunked) | 400 |

**관련 파일**: `Editor/ComfyUIGenerator/Server~/bridge_server.py`, `Assets/MCPTools/README.md`(문제 해결 절)

### R8. 통합 창에 스프라이트 시트 안내 (낮음)

- [x] `PipelineWindow`에 "스프라이트 시트(선택)" 블록 + [Sprite Sheet 창 열기] 버튼

**구현 결과**

- `PipelineWindow.OnGUI`의 4단계 블록 바로 아래에 `EditorGUILayout.Space(10f)`(단계 간 간격 4f보다 크게 둬서 시각적으로 분리) + `DrawSpriteSheetSection()`. 4단계 `DrawStep` 호출과 순번 표시는 그대로다.
- 기존 `DrawStep`을 직접 쓰지 않은 이유: 그 헬퍼가 `StepState` 배지를 필수로 그리는데, Task 문서가 상태 판정을 하지 말라고 했다. 배지만 빼고 같은 시각 요소(`helpBox` 스코프 + `boldLabel` 제목 + `wordWrappedMiniLabel` 설명 + 24f 버튼)로 인라인 구성했다. 새 스타일/헬퍼는 만들지 않았고 `StepState`/`DrawBadge`는 미변경.
- 버튼은 `SpriteSheetPromptWizard.Open()`을 호출한다(`Editor/SpriteSheet/SpriteSheetPromptWizard.cs:109`, 같은 `MCPTools.Editor` 네임스페이스·같은 어셈블리라 using 추가 불필요).
- 안내 문구는 패키지 README의 "스프라이트 시트 사용법" 절을 근거로 작성했다: 프롬프트 생성 → 외부 AI로 시트 이미지 생성 → 시트 임포트(배경 제거·격자 검출 후 슬라이스) → 클립·Animator 생성 → 4단계 적용. 산출물이 항목별 확정본과 다른 위치(`Docs/SpriteSheetPrompt/`, `3_Confirmed/SpriteSheets/`, `3_Confirmed/Animations/`)에 저장되어 위 단계 배지에 반영되지 않는다는 점을 명시했다.

**관련 파일**: `Editor/Pipeline/PipelineWindow.cs`

### 마무리

- [x] `README.md` 갱신 — 적용 이력 동작, 프리팹 변종·Prefab Stage 제약, 브리지 Host 정책
- [x] `CHANGELOG.md` `[Unreleased]`에 기록 (Task 9와 같은 파일 — 편집 직전에 다시 읽어 기존 항목 보존 확인)

패키지 `README.md`에 추가·갱신한 것:

- 4단계 절 — `적용됨` 배지가 재실행 후에도 유지되는 이유, [일괄 적용]이 이미 적용한 항목을 건너뛴다는 것과 개별 [선택 적용]은 항상 가능하다는 것, "기록만 신뢰" 방침
- 4단계 절 — **프리팹 변종·중첩은 그대로 지원**(확인 결과 명시), 프리팹 모드 미저장 변경 시 차단과 Auto Save 기본값 안내, 중첩 스테이지 한계
- MCP 도구 절 도입부 — Play Mode·컴파일 중 가드와 화이트리스트 3개, "모달 다이얼로그 없음"
- 스키마 절 — 두 JSON 예시에 `schemaVersion: 1` 추가, `schemaVersion` 규칙 설명, `id` 필드에 파일명 변환 주의, `status`를 "1단계 수동 열"로 정정, **id 충돌** 절과 **적용 이력(`applications`)** 절 신설(JSON 예시 포함)
- 문제 해결 절 — 403 Host 헤더, 400 입력 상한 (R7에서 추가)

## 2. 에디터 테스트 체크리스트

2026-07-26 Unity 6000.5.2f1 + unity-mcp로 실행한 결과다. **컴파일 통과 확인 완료** (force refresh + 컴파일 요청 후 `MCPTools.Editor` 어셈블리에 `ApplyHistory`·`GetBlockedReason`·`PreviewFileNameForId`·`AddPrefabStageConflictReason` 전부 로드됨, MCPTools 관련 에러·경고 0건).

- [x] **Play Mode에서 `mcptools_apply_all` → 차단** — `success:false` + "에디터가 Play Mode입니다. 재생을 멈춘 뒤 다시 호출해주세요. (도구: mcptools_apply_all)". 같은 상태에서 화이트리스트인 `mcptools_ping`·`mcptools_status`는 정상 응답.
- [x] **적용 이력이 실제로 남는다** — `mcptools_apply_asset` 3회 후 `GenerationResults.json`에 `applications` 배열이 생기고 `results`·`schemaVersion`이 보존됨. `ApplyHistory.LoadAppliedItemIds`가 3건을 그대로 복원.
- [x] **변종 / 중첩 / Prefab Stage 3종** — §R4 확인 결과 참조. Prefab Stage 가드가 실제 MCP 호출에서 차단되는 것까지 확인.
- [x] **같은 이름으로 sanitize되는 id 2개 → 경고** — `hero:idle`·`hero/idle` → 둘 다 `hero_idle`로 판정되고 `Validate`가 충돌 경고를 반환.
- [x] **구 JSON(`schemaVersion` 없음) 정상 로드** — 기존 `AssetList_20260721_0456.json`(키 없음)에서 항목 38개 정상 로드. `ReadSchemaVersion`이 `{없음}→1`, `{1}→1`, `{99}→99`, `{"3"}→3`, `{true}→1`로 정상 판정.
- [x] **`curl -H "Host: evil.example"` → 403, `count: 9999` → 400** — §R7 검증 표 참조 (포트 8189를 점유하지 않으려고 8199로 임시 기동 후 종료).
- [x] **MCP 경로에서 모달 없이 Job `failed`** — 브리지 미기동 상태로 `mcptools_generate_candidates` 호출 → 즉시 `started` 반환, 에디터가 잠기지 않고 `mcptools_list_candidates`가 `failed` + "브리지 서버(…)가 응답하지 않습니다. … [서버 시작] 버튼으로 …"를 반환.

**남은 사람 확인 항목** (자동 확인이 불가능하거나 공유 자원이 필요한 것)

- [ ] 없는 커스텀 노드 워크플로로 `mcptools_generate_candidates` → 다이얼로그 없이 Job `failed` + **누락 노드·조치**가 메시지에 담김
      — 위에서 확인한 것은 브리지 미기동 경로다. **preflight 분기 자체**는 브리지 + ComfyUI가 떠 있어야 탈 수 있다 (브리지는 터미널 간 공유 자원이라 기동하지 않았다).
- [ ] 같은 상황에서 `mcptools_run_pipeline` → `failed`에 같은 안내 (스레드 위반 예외 아님) — 위와 같은 이유
- [ ] 창에서 같은 상황 → 기존처럼 다이얼로그로 안내 (대화형 경로 회귀 없음) — 위와 같은 이유 + 사람의 창 조작 필요
- [ ] 창 버튼 비활성 + 사유 표시 (Play Mode 진입 시 노란 HelpBox, 생성/확정/적용/스캔/저장 버튼 회색) — GUI 표시라 사람 눈 확인 필요
- [ ] 창에서 항목 적용 → 창을 닫았다 다시 열면 "적용됨" 유지, [일괄 적용] 대상에서 제외, 개별 [적용]은 가능, "이미 적용됨 N개" 표시 — 코드 경로는 위에서 확인했고 GUI 표시만 남음
- [ ] 통합 창에서 [Sprite Sheet 창 열기] 동작 — GUI 확인
- [ ] **회귀**: 3단계 생성 → 확정 → 4단계 적용 전체 흐름 (브리지 + ComfyUI 필요)

## 3. 이번 Task에서 하지 않은 것

- **R5의 시트 저장 덮어쓰기 안내** (`SpriteSheetImporter.cs:1439`) — 사용자 결정으로 **Task 8(C23/C24)로 이월**. 같은 파일을 Task 8·Task 9가 함께 건드리는 중이라 지금 손대면 충돌 위험이 크다.
