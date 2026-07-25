# Task 8 체크리스트 — 메모리 누수 · 성능 · 속도 개선

> Task 문서: [Task8_성능개선.md](../tasks/Task8_성능개선.md)
> 2026-07-25 작성: 정적 감사 완료(총 53건 — M 9건 / S 16건 / C 28건), 구현 미착수.

## 0. 감사 단계

- [x] 메모리 누수·리소스 미해제 정적 감사 (M1~M9)
  - 구현 결과: `Editor/**/*.cs` 38개 전수 정독. `SerializedObject` 미해제 4곳, AI CLI 자식 프로세스의 도메인 리로드 잔존, `Process` 미해제, `async void` 종료 시 `Repaint()` 순서 오류, CTS 소유권 경쟁, `HttpClient` 5초 주기 재생성, MCP Job 딕셔너리 무한 증가 + `running` 고착, 브리지 `JOBS` 미정리, 시트 임포터 임시 배열 6개 동시 할당을 확인. 반대로 텍스처 해제·`EditorApplication` 구독 해제·`LoadPrefabContents` 미사용·Additive 씬 종료·정적 캐시 내용·오디오 핸들은 모두 정상으로 검증.
  - 검증 상태: 정적 분석 완료 (Unity 실행·Profiler 측정 미수행)
  - 관련 파일: `docs/tasks/Task8_성능개선.md` §3, §6
- [x] 생성 파이프라인 속도 정적 감사 (S1~S16)
  - 구현 결과: `ComfyUIGenerator/**` + `Common/{ComfyUIClient,AiCliRunner,MiniJson}.cs` + `Server~/bridge_server.py`(909줄) 정독. 단건 생성마다 모델 언로드(회차당 10~40초 낭비), `PipelineTool`의 메인 스레드 완전 블로킹, 2단 폴링 + 확인 전 sleep, `/object_info` 생성 1회당 2회 무캐시 조회, 워크플로 JSON 반복 로드, 순차 다운로드, 확정 시 임포트 3중 호출 등을 확인. 반대로 "후보 4개를 먼저 모두 큐잉"·완료 시 폴링 종료·다운로드 루프 밖 1회 Refresh·생성 경로 전체 async/await·브리지 스레딩 모델은 정상으로 검증.
  - 검증 상태: 정적 분석 완료 (실측 타이밍 미수행)
  - 관련 파일: `docs/tasks/Task8_성능개선.md` §4, §6
- [x] CPU·알고리즘·GUI 핫패스 정적 감사 (C1~C28)
  - 구현 결과: `AssetListup`/`AssetApplier`/`PromptBuilder`/`SpriteSheet`/`SpriteSlicing`/`Pipeline`/`Common`/`McpForUnityBridge` 정독. 4단계 창의 리페인트당 프리팹 로드·계층 순회·`SerializedObject` 생성(치명 2건), 리페인트마다 `EditorPrefs.SetString`(레지스트리 쓰기), 프리팹당 계층 4회 순회, 타입 판정 무캐시, `GetOrCreate()` 무캐시, 프리팹 그룹핑 부재로 인한 항목당 반복 저장, `GenerationResults.json` 항목마다 재파싱, 침식 O(N·K²)·격자 검출 2회 스캔 등을 확인. 반대로 `FindAssets` 범위 제한·`InstallRoot` 캐시·창의 `GetOrCreate` 캐시·OnGUI의 파일/JSON 접근 부재·씬 그룹핑·`SaveAssets` 1회 호출은 정상으로 검증.
  - 검증 상태: 정적 분석 완료 (대규모 프로젝트 실측 미수행)
  - 관련 파일: `docs/tasks/Task8_성능개선.md` §5, §6

> **줄 번호 주의:** 감사 시점에 `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`가 커밋되지 않은 수정 상태(2,358줄)였다. 이 파일의 줄 번호는 워킹트리 기준이므로 착수 시 `grep`으로 재확인할 것.

## 1. 1순위 구현 (치명 + 즉효)

- [x] **[S1]** 모델 언로드 기본값·호출 위치 수정
  - [x] 기존 설정 에셋 사용자용 **1회성 마이그레이션** 구현
  - [x] **기존 설정 에셋(`unloadModelsAfterBatch=true`)이 있는 상태에서 검증** — 아래 실측 결과 참조
  - [x] `Assets/MCPTools/README.md`에 VRAM 트레이드오프 기록
- [x] **[S2]** `PipelineTool` 메인 스레드 블로킹 제거 — 잡 모델 전환
- [x] **[C1 + C2 + M1]** 4단계 창 리페인트 캐시화 + `SerializedObject` `using` 처리
- [x] **[C17 + C18 + C19]** 일괄 적용 배치화
- [x] **[S3]** 파이프라인 루프 내 `AssetDatabase.Refresh()` 제거

### S1 — 모델 언로드 (치명)

- `unloadModelsAfterBatch` 기본값 `true` → **`false`**, 단건 생성 `finally`의 `TryFreeMemoryAsync()` 호출 제거(일괄 경로는 `done > 0` 조건부라 그대로 유지).
- **1회성 마이그레이션**: `MCPToolSettings`에 `[SerializeField] private int settingsVersion = 0;`과 `CurrentSettingsVersion = 1`을 두고, `GetOrCreate()`가 구버전 에셋을 만나면 `unloadModelsAfterBatch = false`로 보정 후 버전을 올려 저장한다.
  - **초기값을 `0`으로 둔 것이 핵심이다.** Unity는 역직렬화 시 없는 키에 대해 필드 초기화 식 값을 남긴다. `1`로 뒀다면 모든 구버전 에셋이 "이미 마이그레이션됨"으로 로드되어 보정이 영원히 돌지 않는다.
  - 새로 만드는 에셋은 `CreateAsset` **전에** 최신 버전을 찍어 마이그레이션 대상에서 빠진다.
  - 값이 실제로 바뀐 경우에만 콘솔 안내를 출력하고, 다시 켜는 방법(`Tools/MCP/Settings`)을 문구에 넣었다.
- **실측 (2026-07-26, Unity 6000.5.2f1)** — 이 저장소의 `MCPToolSettings.asset`이 마침 `unloadModelsAfterBatch: 1` / `settingsVersion` 없음인 **v0 = 기존 사용자 상태**였으므로 그대로 검증했다.

  | 확인 | 결과 |
  |------|------|
  | 컴파일 직후 `GetOrCreate()` 1회 호출 | `unloadModelsAfterBatch: 1` → **`0`**, `settingsVersion: 1` 추가 |
  | 콘솔 안내 | 1회 출력, 되돌리는 방법 포함 |
  | 같은 도메인에서 재호출 | 안내 **여전히 1건** (2회 실행되지 않음) |

- **미측정**: 연속 3회 생성의 회차별 소요 시간(§8-B7). 브리지 + ComfyUI가 필요한데 브리지는 터미널 간 공유 자원이라 기동하지 않았다. 사람 확인 항목으로 남긴다.

### S2 — 파이프라인 프리즈 제거 (치명) · S3 — 루프 내 Refresh 제거

- **Job 모델로 전환**했다. `mcptools_run_pipeline`이 즉시 `{status:"started", promptSetPath, assetListPath, itemCount, timeoutSeconds, statusNote}`를 반환하고 백그라운드로 진행한다.
- **상태 조회는 새 도구가 아니라 `mcptools_status`의 `pipeline` 블록**으로 했다. 근거 2가지:
  - MCP 노출은 `McpForUnityBridge/McpForUnityAdapter.cs`의 `[McpForUnityTool]` 클래스가 있어야 한다. `McpToolRegistry.Register`만으로는 **에이전트에게 보이지 않는다.**
  - Task 10의 `ReadOnlyTools` 화이트리스트에 없는 새 도구는 컴파일·임포트 중 거부된다 — **하필 Job이 `Refresh()`로 `isUpdating`을 만드는 순간** 폴링이 막힌다. `mcptools_status`는 이미 화이트리스트에 있고 파라미터가 없어 부작용도 없다.
- 기존 반환 필드(`promptSetPath`/`assetListPath`/`pendingSelections`/`applied`/`failed`)는 **이름·형태 그대로** `pipeline` 블록 안에 있다.
- 취소·타임아웃: `CancellationTokenSource(항목 수 × jobTimeoutSeconds)`를 `GenerateAsync`에 전달. 수동 취소 수단은 없다(위 이유로 새 도구를 만들 수 없고, 화이트리스트인 `mcptools_status`에 쓰기 파라미터를 붙이면 "부작용 없음" 전제가 깨진다). 타임아웃 또는 도메인 리로드로 해소된다.
- 에셋 수정이 메인 스레드에서 도는 것은 `await`에 `ConfigureAwait(false)`를 쓰지 않아 Unity의 SynchronizationContext로 복귀하기 때문이며, 안전장치로 Job 시작 시 메인 스레드 ID를 기록해 확정·적용 직전에 검증한다.
- **S3에서 방향을 한 번 바로잡았다.** 최초 구현은 생성 루프 전체를 `StartAssetEditing`/`StopAssetEditing`으로 감쌌는데, 이 구간은 항목마다 네트워크 I/O를 `await`하므로 **수 분~수십 분** 동안 AssetDatabase가 정지한다. 그러면 ① 창은 움직여도 에셋 임포트와 스크립트 컴파일이 멈춰 S2의 목표가 반쪽이 되고, ② 그 사이 도메인 리로드나 강제 종료가 나면 `StopAssetEditing`이 실행되지 않아 **AssetDatabase가 정지된 채 남는다**(에디터 재시작 필요).
  - 임포트를 억누르는 대신 **애초에 Refresh가 일어나지 않게** 바꿨다 — `CandidateGenerator.GenerateAsync`에 `refreshAssets`(기본 `true`) 인자를 추가하고 파이프라인만 `false`로 넘긴 뒤 루프 종료 후 1회 Refresh한다. "항목 N개에 Refresh 1회"라는 S3의 목표는 그대로 달성되면서 장시간 정지가 사라진다.
  - `ConfirmCandidate`는 `TextureImporter`를 읽어야 하므로 Refresh **이후**에 실행한다(생성 전부 → Refresh → 확정·적용 2단계).
- `McpForUnityAdapter.cs`의 `[McpForUnityTool]` Description도 갱신했다. **이게 에이전트가 실제로 읽는 설명**이라, "생성 완료까지 에디터가 블로킹됩니다"가 남아 있으면 계속 블로킹 호출로 취급한다.

### C1+C2+M1 — 4단계 창 리페인트 · C17+C18+C19 — 일괄 적용 배치화

- 리페인트마다 하던 프리팹 로드·계층 순회·`SerializedObject` 생성·LINQ 집계를 **선택 변경·대상 편집·적용 시점에만** 계산해 캐시한다. 중복 `LoadAssetAtPath` 1건도 제거(C6).
- `ApplyBatch`가 프리팹도 씬처럼 `targetPrefabPath` 기준으로 그룹핑해 **그룹당 1회 로드 → 전체 할당 → `SavePrefabAsset` 1회**. 단건 적용도 "항목 1개짜리 그룹"으로 같은 경로를 타게 해 두 경로가 갈라지지 않는다.
- Task 10 추가분은 전부 보존된다 — `_blockedReason` 가드, `CanApplyAgain`/`ReadyToApply`, `ApplyHistory.Record`(**성공 항목 인덱스를 도는 구조라 그룹당 1회가 아니라 항목당 1회**), `AddPrefabStageConflictReason`.
- **실측 (2026-07-26)** — `Assets/MCPTools.User/Task8Verify/`에 프리팹(자식 3) + 닫힌 씬을 만들어 `ApplyBatch`를 직접 호출. 확인 후 전부 삭제(이력 4건도 제거, 실제 확정 기록 13건은 유지).

  | 확인 | 결과 |
  |------|------|
  | 같은 프리팹 3항목 | Slot1=`T8_A`, Slot2=`T8_B`, Slot3=`T8_A` — 항목별 값 정확 |
  | 같은 그룹 안 실패 1건(`NoSuchChild`) | 사유 문구 정상, 나머지 3건에 영향 없음, 이력에도 없음 |
  | **닫힌 씬 항목** | 씬 파일에 GUID 기록 확인, 적용 후 다시 닫힘 |
  | 적용 이력 | **4건** (항목별). 실패 항목 제외 |
  | 결과 개수 | 입력과 동일(5건), 순서 보존 |
  | `isUpdating` | `False` — AssetDatabase가 정지 상태로 남지 않음 |

  → **C18의 가장 큰 미검증 위험이었던 "`StartAssetEditing` 구간 안에서 씬 열기/저장"이 정상 동작함을 실측으로 확인했다.** 추측으로 씬 그룹을 브래킷 밖으로 빼지 않았다.

## 2. 2순위 구현 (높음)

- [x] **[S4]** 폴링 지연 축소
- [x] **[S5 + S6]** 브리지 캐시
- [x] **[C11 + C12]** 스캔 계층 순회 4→1회 통합 + 타입 판정 캐시
- [x] **[C26]** `MCPToolSettings.GetOrCreate()` 정적 캐시
- [x] **[C3]** 리페인트마다 `EditorPrefs.SetString` 호출 제거
- [x] **[C23 + C24]** 시트 임포터 — 침식을 분리 가능 필터로, 격자 검출을 단일 순회로

### S4 — 폴링 지연

- 브리지: `time.sleep`을 `while pending:` 루프 **끝**으로 이동(첫 확인 즉시 수행) + `POLL_INTERVAL_SEC` 1.0 → **0.3**.
- Unity: `Task.Delay(1초)` → **0.3초 시작 + ×1.5 백오프(상한 1.5초)**, 진행률이 오르면 초기값으로 리셋. 리셋이 없으면 마지막 장의 완료를 최대 1.5초 늦게 잡아 §8-8("마지막 이미지 후 0.5초 이내")을 못 맞춘다.

### S5+S6 — 브리지 캐시

- `/object_info` **60초 TTL 캐시**. 락은 캐시 dict 구간에만 걸고 HTTP 조회는 락 밖 — 락을 잡은 채 조회하면 ComfyUI 미응답 시 최대 15초 동안 `/health`·`/job` 폴링까지 전부 막힌다(`ThreadingHTTPServer`). 중복 조회보다 응답성을 택했다.
- 무효화: `POST /free` 진입 시, `GET /workflows?refresh=1`(신규), ComfyUI 사망 확인 시.
- **캐시 도입으로 새로 생긴 문제를 발견해 함께 고쳤다** — 실패는 캐시하지 않아도, 성공 캐시가 살아 있는 동안 ComfyUI가 죽으면 `comfyReachable=true`가 최대 60초 유지된다(테스트에서 실제 재현). 캐시를 내주기 전 `/system_stats`(수 KB) 1회로 생존만 확인하도록 보완했다.
- 워크플로 JSON은 **mtime 기반 캐시**. `/workflows` 루프에서 `load_workflow`를 1회만 호출해 두 함수에 전달(이전에는 워크플로 수 × 2회 디스크 읽기).
  - **가장 깨지기 쉬운 지점**: `build_workflow`/`apply_variables`/`set_seed`가 캐시 dict의 **중첩 노드를 제자리 수정**한다. 캐시를 그대로 돌려주면 첫 생성의 시드·프롬프트가 캐시에 눌러붙는다. 얕은 복사도 노드 dict가 공유되어 불충분하므로, `load_workflow`가 항상 `copy.deepcopy` 사본을 반환하게 했다.
- **실측** (포트 8199 + ComfyUI 스텁 8197, **8189 미사용**, 패키지 `workflows/*.json` 미변경):

  | 확인 | 결과 |
  |------|------|
  | `/workflows` 1회차 → 2회차 | 41.5ms → **19.1ms**, 응답 **바이트 단위 동일** |
  | 스텁의 `/object_info` 실제 요청 수 | 1회차 후 1 → 3회차 후에도 **1** (TTL 적중) |
  | 워크플로 JSON mtime 변경 후 | `missingNodes` 반영 확인 |
  | `?refresh=1` / `POST /free` 후 | object_info 재조회 확인 |
  | ComfyUI 미기동 | `comfyReachable=false` 유지(캐시가 가리지 않음), 재기동 시 TTL 대기 없이 즉시 true |
  | `/generate` → job completed | **77ms** (첫 확인 전 sleep이 남아 있었다면 300ms+) |

- `BRIDGE_VERSION` 0.3.0 → **0.4.0** (`?refresh=1` 신규 + 60초 캐시라는 관측 가능한 동작 변화).

### C11+C12 — 스캔

- `GetComponentsInChildren<Component>(true)`를 **1회만** 호출해 한 루프에서 Image/RawImage/SpriteRenderer/AudioSource를 모두 분기 판정. 프리팹·씬 경로를 `CollectSlotsCore`로 통합.
- **순서 보존이 이 작업의 핵심 위험이었다.** `AssetListBuilder.AssignIds`가 인덱스로 `item_001…`을 부여하고 `FindMatch`가 첫 매칭을 반환하므로, 순회를 합쳐 순서가 섞이면 **항목 id 자체가 달라져** 3·4단계 산출물과 어긋난다. 종류별 임시 버퍼에 모았다가 기존 순서(Image → RawImage → SpriteRenderer → AudioSource)로 이어붙이고, flush 단위도 기존과 같이 **프리팹은 프리팹 단위, 씬은 루트 오브젝트 단위**로 맞췄다.
- C12는 `(Type, string)` 조합 키가 아니라 **`Type` → 비트 플래그(`SlotKind`)** 로 캐시했다. 판정 종류가 4개로 고정이라 단일 루프에서 컴포넌트당 딕셔너리 조회 1회로 끝난다. Image/RawImage는 기존 `IsComponentOfType`과 글자 그대로 같은 규칙(`BaseType` 체인 이름 비교)을 보존했다.
- **실측** — 4종이 섞인 프리팹(계층 순서를 일부러 섞음)을 스캔:

  ```
  T8Scan/c_image      | Image
  T8Scan/f_rawimage   | RawImage
  T8Scan/a_sprite     | SpriteRenderer
  T8Scan/e_sprite     | SpriteRenderer
  T8Scan/b_audio      | AudioSource
  T8Scan/d_audio      | AudioSource
  ```

  → 종류별 그룹 순서와 그룹 내부의 계층 깊이우선 순서가 모두 기존과 동일.
- **미측정**: 프리팹 2,000개 규모의 스캔 시간(§8-11). 이 저장소에 그 규모의 프리팹이 없다.

### C26 · C3

- `GetOrCreate()`에 `private static MCPToolSettings _cached`. `UnityEngine.Object`의 `==` 오버로드 덕분에 에셋을 삭제하면 자동으로 재조회된다.
  - **Task 8 문서 §6의 "Unity 오브젝트를 잡는 정적 캐시 — 정상(정적 필드는 문자열·값 타입만 보관)" 판정이 이 변경으로 깨진다.** 다만 캐시가 가리키는 것은 `AssetDatabase`가 이미 살려두는 영속 에셋이라 수명을 연장하지 않고, 도메인 리로드마다 초기화된다. 누수가 아니라고 판단한다.
- C3: 팝업 선택값이 실제로 바뀐 경우에만 `EditorPrefs.SetString`. **부수효과 하나를 함께 보존했다** — 기존의 무조건 쓰기는 "저장된 AI가 목록에 없을 때 Prefs를 실제 표시값으로 정규화"하는 역할도 겸하고 있었다. 가드만 넣으면 그게 사라지므로, 리페인트 경로가 아닌 `RefreshAiToolList`에서 1회 보정하도록 옮겼다.

### C23+C24 — 시트 임포터

- **침식이 실제로 분리 가능한지 먼저 확인했다.** 원 구현의 판정은 정사각(체비쇼프) 윈도이고 경계는 `Mathf.Max/Min`으로 **윈도를 잘라내는** 방식이며, 잘라내기가 x·y축 독립이라 행 AND → 열 AND로 분해된다. 유클리드 원형이었다면 분리하면 안 됐다.
  - 슬라이딩 카운터로 O(N·K²) → **O(N)**. 추가 할당은 `bool[N]` 1개 + `int[width]` 1개(2패스에 필수).
- C24는 한 순회에서 `lineCountX`/`lineCountY`를 동시 누적하고 경계 추출만 축별로 분리. **판정식·임계값·경계 추출 코드는 한 글자도 바꾸지 않았다.** 카운트가 `int[]`라 부동소수점 누적 오차 문제도 없다.
- **결과 동일성 검증**: 수정된 실제 파일을 스텁 환경(.NET 콘솔 + Unity 타입 최소 스텁)에서 컴파일해 원본 구현과 대조 — 침식 **760 케이스**, 격자 검출 **182 케이스** 전부 일치. 기존 테스트가 200×200 정사각만 쓰기 때문에 **비정사각 이미지·비정사각 셀**을 일부러 포함해 width/height 전치 버그를 덮었다.
- **EditMode 테스트 74건 전부 통과** (`SpriteSheetImporterDetectTests` 6건 포함) — C24의 회귀 안전망이 실제로 지켜졌다.
- 참고 성능(4096×4096, 배경 비율별 old→new): 0.70 → 107→98ms, 0.95 → **172→60ms**, 1.00 → **214→44ms**. 문서의 "수십 배"는 원 구현의 조기 탈출을 무시한 이론값이다.

### 함께 처리한 이월 항목

- **Task 10 R5의 시트 저장 덮어쓰기 안내** — 같은 파일을 고치는 김에 함께 구현했다. `ApplySlices`/`Import`에 `interactive`(기본 `false` = 비대화형) 인자를 추가해, 창은 확인 다이얼로그로 묻고(취소 시 저장·슬라이스하지 않고 `canceled=true` 반환) MCP는 다이얼로그 없이 덮어쓰되 응답 `overwroteExisting`으로 알린다.
  - `showProgress`를 재사용하지 않은 이유: 그건 "진행률 바 표시"라는 UI 장식이고 이건 "에디터를 잠그는 모달을 띄워도 되는가"라는 동작 결정이다. 묶으면 진행률만 원하는 배치 호출자가 모달을 얻게 된다. `CandidateGenerator.GenerateAsync`의 `interactive`와 같은 이름·같은 기본값으로 맞췄다.
  - 창 호출부는 취소를 먼저 처리하도록 고쳤다 — 그대로 뒀으면 취소 시에도 "임포트 완료"를 출력하고 없는 에셋을 참조했다.

## 3. 3순위 구현 (중간)

- [ ] **[M2]** AI CLI 프로세스 정적 레지스트리 + `AssemblyReloadEvents.beforeAssemblyReload`·`EditorApplication.quitting` 훅
- [ ] **[M6]** `BridgeClient` 내부 `HttpClient` 공유 (5초 폴링 소켓 누적 해소)
- [ ] **[M7]** MCP Job 딕셔너리 상한·정리 + 타임아웃으로 `running` 고착 해소
- [ ] **[M4 + M5]** `finally`의 `Repaint()` 파괴 가드, CTS Cancel/Dispose 경쟁 정리
- [ ] **[M3]** 브리지 시작 `Process` 객체 Dispose
- [ ] **[M9]** 시트 임포터 임시 배열 통합 (`nearWhite`/`neutral` 비트 플래그화, `isBackground` 제거)
- [ ] **[S7]** `/generate`의 워크플로 로드 1회화 + `deepcopy`
- [ ] **[S8]** 후보 결과 파일 다운로드 병렬화(`Task.WhenAll`)
- [ ] **[S9]** 확정 시 임포트 3→1회 (`ImportAsset(ForceUpdate)`·말미 `Refresh()` 제거)
- [ ] **[S10]** 브리지 → ComfyUI keep-alive 커넥션 재사용
- [ ] **[S11]** `[서버 시작]` 1초 동기 블로킹 제거 (`Exited` 이벤트 기반)
- [ ] **[S12]** Python 탐지 실패 결과도 `SessionState` 캐시 + 비블로킹 검증
- [ ] **[S13]** ComfyUI WebSocket 구독으로 스텝 단위 진행률
- [ ] **[C4~C10]** GUI 캐시화 — `GUIStyle`·`GUIContent[]`·중복 `LoadAssetAtPath`·LINQ 집계·`File.Exists`·목록 가상화
- [ ] **[C13 + C14]** 스캔 조기 제외(로드 전 스킵) + 진행률·취소
- [ ] **[C15 + C16 + C22]** 매칭 자료구조 개선 — 정규화 키 사전 계산, 소문자 상수, `Dictionary` 인덱싱
- [ ] **[C20 + C21]** `ValidateItem`의 컴포넌트 `out` 반환 재사용, `GetComponents` 1회 호출
- [ ] **[C27]** 문서 목록 정렬 비교자 개선 (Schwartzian transform)

## 4. 4순위 구현 (낮음)

- [ ] **[M8]** 브리지 `JOBS` TTL 스윕
- [ ] **[S14]** `/view` 청크 스트리밍
- [ ] **[S15]** `batch_size` 일괄 큐잉 옵션 (기본값은 현행 유지)
- [ ] **[S16]** `MiniJson` 문자열 인덱스 기반 파서
- [ ] **[C25]** BFS 좌표 변환 나눗셈 제거
- [ ] **[C28]** MCP 응답 JSON 4중 변환 축소

## 5. 문서 갱신

- [x] `Assets/MCPTools/README.md` — `unloadModelsAfterBatch` 기본값 변경과 VRAM 트레이드오프(마이그레이션 동작 포함), 대규모 프로젝트 스캔 안내, `run_pipeline` Job 모델과 `mcptools_status`의 `pipeline` 블록, `/object_info` 60초 캐시 안내
- [x] `CHANGELOG.md` `[Unreleased]` 기록 (Task 10 항목과 함께 유지)
- [ ] **버전 동기 (`package.json` / `MCPToolsInfo.Version` / git 태그) — 보류.** 릴리스 시점의 판단이 필요해 임의로 올리지 않았다. Task 10 + Task 8 + Task 9의 `[Unreleased]` 내용이 모두 쌓여 있고, `run_pipeline`의 반환 형태가 바뀌는 **동작 변경**이 포함되므로 minor 이상이 적절하다. 태그·푸시는 사용자 지시가 있을 때 수행한다.

## 6. 사용자 에디터 테스트

> Task 문서 §8의 재현 시나리오. **개선 전 측정치를 먼저 기록**해야 효과를 판정할 수 있다.
>
> **2026-07-26 자동 검증분** (Unity 6000.5.2f1 + unity-mcp): 컴파일 통과(MCPTools 관련 에러·경고 0건), **EditMode 테스트 74건 전부 통과**, S1 마이그레이션 실측, 일괄 적용 배치화 실측(프리팹 그룹·부분 실패·닫힌 씬·이력), 스캔 순서 보존 실측, 브리지 캐시·폴링 실측. 상세는 §1·§2의 각 실측 표 참조.
>
> **아래 항목은 브리지 + ComfyUI(공유 자원) 또는 대규모 프로젝트가 필요해 자동 검증하지 못했다.** 개선 전 측정치가 없으므로, 지금 상태를 "개선 후"로 기록하고 필요하면 이전 커밋(`bb385c1`)에서 "개선 전"을 측정해 대조한다.

**메모리**

- [ ] 4단계 창에서 항목 선택 후 5분 방치 → Profiler에서 네이티브 메모리·GC 할당 증가 없음 (M1/C1/C2)
- [ ] "후보 4개 생성" 20회 연속 → 회차마다 메모리 우상향 없음
- [ ] AI CLI 실행 중 스크립트 저장(도메인 리로드) → `claude`/`codex` 프로세스 잔존 없음 (M2)
- [ ] 3단계 창 1시간 방치 후 `netstat` → `TIME_WAIT` 소켓 수백 개 누적 없음 (M6)
- [ ] MCP 생성 여러 번 후 도메인 리로드 → 같은 `assetItemId` 재생성 시 "이미 실행 중" 오류 없음 (M7)
- [ ] 4096×4096 시트 임포트 피크 메모리 절반 이하 (M9)

**속도**

- [ ] 동일 항목 "후보 4개 생성" 연속 3회 → 2·3회차가 1회차와 비슷한 소요 시간 (S1)
  - 개선 전: ___초 / ___초 / ___초 → 개선 후: ___초 / ___초 / ___초
- [ ] `UI.json`(steps=4) 생성 → 마지막 이미지 완료 후 UI 반영까지 0.5초 이내 (S4)
- [ ] `mcptools_run_pipeline` 10항목 실행 중 에디터 창 드래그·스크롤 가능 (S2), 총 소요 시간 단축 (S3)
- [ ] `/workflows` 두 번째 호출부터 캐시 적중으로 즉시 응답 (S5/S6)

**CPU**

- [ ] 프리팹 2,000개 이상 프로젝트에서 1단계 스캔 시간 단축 + 진행률·취소 동작 (C11/C12/C14)
  - 개선 전: ___초 → 개선 후: ___초
- [ ] 한 프리팹에 10개 항목 일괄 적용 → 프리팹 저장 1회만 발생, 소요 시간 단축 (C17/C18)
- [ ] 항목 200개 AssetList 스크롤 시 끊김 없음 (C9)
- [ ] 4096×4096 시트 임포트 시간 단축 (C23/C24)

**회귀**

- [ ] Task 1~7의 사용자 에디터 테스트 전체 통과
- [ ] C17(프리팹 그룹핑) 적용 결과가 개선 전과 동일 — 대상 컴포넌트·값·Undo 동작 대조
- [ ] S9(임포트 축소) 후 Sprite 임포트 설정(Sprite Mode, `alphaIsTransparency`, PPU)이 개선 전과 동일
- [ ] C12(타입 판정 캐시) 후 스캔 결과 항목이 개선 전과 완전히 동일
- [ ] VRAM이 작은 환경에서 `unloadModelsAfterBatch=false`로 연속 생성 시 OOM 없음 — 문제 시 설정으로 복구 가능하고 README에 트레이드오프 기록 (S1)
