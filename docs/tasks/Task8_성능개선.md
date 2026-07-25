# Task 8 — 메모리 누수 · 성능 · 속도 개선

> 배경: Task 7까지 기능·배포 호환성을 확보했으나, 반복 사용 시의 **메모리 증가**, 대규모 프로젝트에서의 **스캔·적용 지연**, 생성 1회당 **불필요하게 소모되는 대기 시간**은 한 번도 감사한 적이 없다. 이 Task는 세 축(누수 / CPU / 속도)을 정적 감사로 전수 조사하고 개선한다.

## 1. 목표

도구를 **오래 켜두고 반복 사용해도 메모리가 늘지 않고**, **대규모 프로젝트에서도 에디터가 멈추지 않으며**, **생성 1회에 드는 대기 시간 중 실제 추론 외의 낭비를 제거**한다.

정량 목표:

| 지표 | 현재(추정) | 목표 |
|------|-----------|------|
| 후보 4장 반복 생성 회차당 부가 대기 (모델 재로드 + 폴링 + preflight) | 약 12~45초 | 3초 이하 |
| 3·4단계 창을 열어둔 상태의 리페인트당 힙 할당 | 수 KB~수백 KB (프리팹 크기 비례) | 0에 수렴 (상수) |
| 프리팹 2,000개 프로젝트의 1단계 스캔 | 수십 초, 진행률 없음 | 절반 이하 + 진행률·취소 |
| 동일 프리팹 10개 항목 일괄 적용 시 프리팹 저장 횟수 | 10회 | 1회 |
| `mcptools_run_pipeline` 실행 중 에디터 응답성 | 완전 프리즈 | 프리즈 없음 |

## 2. 감사 범위와 방법

- 정적 분석만 수행(Unity 실행·ComfyUI 호출 없음). 근거는 `파일:줄`로 표기한다.
- 대상: `MCPToolTest/Assets/MCPTools/` C# 37개 + `Runtime/Data/MCPToolsInfo.cs`(총 38개, 약 17,900줄), `Server~/bridge_server.py`(909줄), `workflows/*.json`·`variables.json`.
- 세 관점으로 나눠 독립 감사 후 중복을 통합했다 — ① 메모리 누수·리소스 미해제, ② 생성 파이프라인 속도, ③ CPU·알고리즘·GUI 핫패스.
- 표기: **심각도**(치명/높음/중간/낮음) × **분류**(M 메모리 / S 속도 / C CPU).

> **줄 번호 주의:** `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`는 감사 시점에 **커밋되지 않은 수정본**(2,358줄)이었다. 이 파일의 줄 번호는 **워킹트리 기준**이며 커밋 상태에 따라 어긋날 수 있다. 착수 시 `grep`으로 재확인할 것. 나머지 파일은 커밋 상태 기준으로 확정적이다.

## 3. 감사 결과 — 메모리 누수 · 리소스 미해제 (M)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| M1 | **`SerializedObject`를 생성하고 `Dispose()` 하지 않음** (4곳) | `SerializedObject`는 네이티브 메모리를 잡는다. 특히 4단계 창은 **매 리페인트마다** 생성하므로, 항목을 선택한 채 창을 몇 분 열어두면 네이티브 메모리가 계속 늘고 에디터가 눈에 띄게 무거워진다. 1단계 스캔도 컴포넌트마다 생성해 스캔 1회에 메모리가 수십~수백 MB 튄다 | `AssetApplier/AssetApplier.cs:811`(`GetObjectProperty`, 매 리페인트 경로), `:796`(`SetObjectProperty`), `:631`(`ValidateAudioField`), `AssetListup/ProjectScanner.cs:352`(`GetReferencedAssetName`) | **높음** | 네 곳 모두 `using (var serialized = new SerializedObject(component)) { ... }`로 감싼다. `SetObjectProperty`는 `ApplyModifiedProperties()` 이후 Dispose되도록 순서 유지. 리페인트마다 재조회하는 구조 자체는 C1에서 함께 해결 |
| M2 | **AI CLI 자식 프로세스가 도메인 리로드·에디터 종료 시 정리되지 않음** | AI 실행(수 분) 중 스크립트를 저장해 컴파일이 돌거나 Unity를 종료하면 `claude`/`codex`/`gemini` 프로세스가 백그라운드에 남아 CPU·API 토큰을 계속 소모한다. 작업 관리자로 직접 죽여야 함. 취소 신호는 창의 `OnDisable`에서만 오는데 도메인 리로드는 그 경로를 타지 않는다 | `Common/AiCliRunner.cs:392-440`(200ms 폴링 루프가 `cancellation`만 확인), 취소 등록부 `AssetListup/AssetListupWindow.cs:112-115`·`PromptBuilder/PromptBuilderWindow.cs:94-97`·`SpriteSheet/SpriteSheetPromptWizard.cs:88-95`, 코드베이스에 `AssemblyReloadEvents` 훅 없음 | 중간 | `AiCliRunner`에 실행 중 프로세스 정적 레지스트리(`HashSet<Process>`)를 두고, `[InitializeOnLoadMethod]`에서 `AssemblyReloadEvents.beforeAssemblyReload += KillAll`, `EditorApplication.quitting += KillAll`을 `-=` 후 `+=` 패턴으로 등록(`ComfyUIServerLauncher.cs:184-185`가 이미 쓰는 패턴) |
| M3 | 브리지 서버 시작 시 얻은 `Process` 객체를 `Dispose()` 하지 않음 | [서버 시작]/[서버 종료]를 반복할수록 에디터 프로세스의 Win32 핸들이 조금씩 증가 (즉각 체감은 낮음) | `ComfyUIGenerator/ComfyUIServerLauncher.cs:561`(`Process.Start` 결과를 `process.Id`만 쓰고 미해제) — 같은 파일 `:928`(`VerifyPython`)·`:657`(`Stop`)은 `using` 사용 | 중간 | `try/finally`로 `process.Dispose()`. PID는 이미 `SessionState`에 저장하므로 객체 보관 불필요 |
| M4 | **`async void` 종료 처리에서 `Repaint()`가 창 파괴 검사보다 먼저 실행됨** | AI 실행 중(수 분) 창을 닫으면 콘솔에 `MissingReferenceException`이 뜨고, `finally` 이후 정리 로직이 예외로 끊긴다. `async void`라 예외를 잡을 상위 프레임도 없음 | `AssetListup/AssetListupWindow.cs:938`(Repaint) → `:941`(`if (this == null)`), `PromptBuilder/PromptBuilderWindow.cs:702` → `:705`. 생성 경로도 동일: `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:1976`, `Common/MCPSettingsWindow.cs:353-357` | 중간 | `finally` 안에서 `if (this != null) Repaint();`로 가드. `SpriteSheet/SpriteSheetPromptWizard.cs:355-367`이 올바른 순서이므로 그 패턴에 맞춘다 |
| M5 | `CancellationTokenSource`를 `OnDisable`에서 `Cancel()`만 하고 `finally`의 `Dispose()`와 소유권이 경쟁 | 창을 닫는 순간 타이밍에 따라 `ObjectDisposedException`이 콘솔에 뜬다 (재현 빈도 낮음) | `AssetListup/AssetListupWindow.cs:832-838`(Cancel) vs `:933-937`(Dispose), `PromptBuilder/PromptBuilderWindow.cs:624-630` vs `:697-701`, `SpriteSheet/SpriteSheetPromptWizard.cs:88-95` vs `:355-360`, `ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(`CancelGeneration` vs 생성 `finally`) | 낮음 | 취소를 `try { cts?.Cancel(); } catch (ObjectDisposedException) { }`로 감싸거나, CTS를 지역 변수로 복사해 `finally`에서만 Dispose하고 필드는 즉시 null |
| M6 | **`BridgeClient`(내부 `HttpClient`)를 5초마다 새로 만들고 버림** | 3단계 창을 열어두기만 해도 5초 주기 health 폴링이 매번 새 소켓을 만든다 — 시간당 720개+. `TIME_WAIT` 소켓이 쌓이고 드물게 로컬 포트 고갈로 연결 오류. 요청마다 커넥션 수립 지연도 붙음 | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:130-137`(5초 폴링) → `:144`, 동일 패턴 `:185`, `:362`, `:1707`, `:1992`. 생성자 `ComfyUIGenerator/BridgeClient.cs:206-228`, 호출부 `CandidateGenerator.cs:109`, `ComfyUIServerLauncher.cs:221` | 중간 | `BridgeClient` 내부에 `static readonly HttpClient`(또는 공유 `SocketsHttpHandler`)를 두고 인스턴스는 URL만 보유. 또는 창 수명 동안 `BridgeClient` 1개를 필드로 유지하고 `OnDisable`에서 Dispose, 주소 변경 시에만 재생성 |
| M7 | **MCP 생성 Job 딕셔너리가 무한 증가하고, 도메인 리로드로 끊긴 Job이 `running`으로 고착** | MCP로 생성을 많이 한 세션에서 결과 리스트가 계속 메모리에 남는다. 더 큰 문제는 **고착** — continuation이 사라진 Job이 영원히 `running`이라, 같은 `assetItemId`를 다시 생성하면 "이미 실행 중" 오류로 막히고 **에디터를 재시작해야 풀린다** | `ComfyUIGenerator/ComfyUIGeneratorTool.cs:27`(`static readonly Dictionary<string, GenerationJob> Jobs`), `:69-74`(running이면 예외), `:105`(무제한 추가), `:118-135`(`async void RunJobAsync`, 취소 토큰 없음) | 중간 | 완료/실패 Job은 조회 후 제거하거나 최근 N건(예: 32) 상한으로 정리. Job에 시작 시각과 `CancellationTokenSource`를 두고 `jobTimeoutSeconds` 초과 시 `failed`로 전환해 고착을 푼다 |
| M8 | 브리지 `JOBS` 딕셔너리가 제거되지 않음 (Python) | 브리지를 장시간 켜두면 결과 파일 목록을 포함한 job이 무제한 누적. `/job` 응답마다 `dict(job)` 복사 비용도 함께 증가 | `Server~/bridge_server.py:63`(`JOBS = {}`), 삽입 `:697-707`, 삭제 코드 없음, 복사 `:598-600` | 낮음 | 완료 후 N분 경과한 job을 정리하는 TTL 스윕 스레드 추가 |
| M9 | 스프라이트 시트 임포트가 **이미지 크기의 임시 배열을 6개 이상** 동시 할당 | 4096×4096 시트에서 약 100MB 이상의 임시 할당 → GC 스파이크, 대형 시트에서 OOM 위험 | `SpriteSheet/SpriteSheetImporter.cs:654-656`(`nearWhite`/`neutral`), `:667`(`visited`), `:793`(`originalAlpha`), `:874`(`eroded`), `:906`(`reach`) | 중간 | `nearWhite`/`neutral`을 `byte[]` 비트 플래그 하나로 통합, `isBackground`는 `visited` 재사용으로 제거. 배열 수를 절반 이하로 축소 |

## 4. 감사 결과 — 생성 파이프라인 속도 (S)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| S1 | **단건 생성이 끝날 때마다 무조건 ComfyUI 모델을 언로드** → 다음 생성에서 전체 재로드 강제 | "후보 4개 생성"을 연속으로 누르는 **가장 흔한 사용 패턴**에서 매 회차 앞에 체크포인트 재로드가 붙는다. SDXL/Flux 계열 6~12GB 기준 **회차당 약 10~40초가 순수 대기로 추가**. 4장 생성이 20초인 워크플로에서는 체감 소요가 2배 이상 | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:1974`(단건 `finally`에서 조건 없이 `_ = TryFreeMemoryAsync();`), 본체 `:1698-1712`, 기본값 `Common/MCPToolSettings.cs:117`(`unloadModelsAfterBatch = true`), 배포 에셋 `Common/MCPToolSettings.asset:26`(`1`), 브리지 `Server~/bridge_server.py:758-772`(`unload_models:True, free_memory:True`) | **치명** | ① 기본값을 `false`로 변경. ② 단건 경로(`:1974`)의 호출을 제거하고 **일괄 경로(`:1871`)에만 유지** — 일괄은 이미 `done > 0` 조건부라 의도에 맞다. ③ 또는 "마지막 생성 후 N분 유휴 시 언로드"로 전환. 반복 생성 회차당 10~40초 절감 |
| S2 | **`mcptools_run_pipeline`이 항목 루프 안에서 메인 스레드를 완전히 블로킹** | 항목 수 × 생성 시간(항목당 수십 초~수 분) 동안 Unity 에디터가 통째로 얼어붙는다. 10항목이면 5~10분 무응답 — 진행률도 취소도 불가. 브리지/ComfyUI가 멈추면 무한 대기(타임아웃·취소 토큰 없음) | `Pipeline/PipelineTool.cs:106-107`(`Task.Run(() => CandidateGenerator.GenerateAsync(...)).GetAwaiter().GetResult()`, `CancellationToken` 미전달), 루프 `:91` | **치명** | `ComfyUIGenerator/ComfyUIGeneratorTool.cs:118-135`의 "즉시 started 반환 + 폴링" 잡 모델로 전환. 즉시 전환이 어렵다면 최소한 `CancellationTokenSource(jobTimeoutSeconds × count)`를 전달해 무한 블로킹을 막는다 |
| S3 | **파이프라인 루프 안에서 항목마다 `AssetDatabase.Refresh()` 호출** | Refresh는 프로젝트 전체 변경 스캔을 유발하는 고비용 연산. 10항목이면 전체 스캔 10회 → 프로젝트 규모에 비례해 수 초~수십 초 추가 | `Pipeline/PipelineTool.cs:115`(루프 `:91` 내부) | **높음** | 루프 전체를 `AssetDatabase.StartAssetEditing()`/`StopAssetEditing()`으로 감싸고 Refresh는 루프 종료 후 1회만. 또는 해당 후보 폴더만 `ImportAsset(..., ImportRecursive)` |
| S4 | **완료 감지 폴링이 2단(브리지 1초 + Unity 1초)으로 겹치고, 브리지는 첫 확인 전에 먼저 1초를 잠** | 실제 생성이 끝난 뒤에도 결과가 뜨기까지 **매 배치마다 최대 약 2초**(평균 1.5초)가 추가. `UI.json`처럼 steps=4인 고속 워크플로(4장 총 4~8초)에서는 **전체 소요의 25~50%가 순수 폴링 대기** | 브리지 `Server~/bridge_server.py:66`(`POLL_INTERVAL_SEC = 1.0`), `:471-476`(`while pending:` 진입 직후 확인보다 **먼저** `time.sleep`), Unity `ComfyUIGenerator/CandidateGenerator.cs:174-190`, `:189`(`Task.Delay(1초)`) | **높음** | ① `time.sleep`을 루프 **끝**으로 옮겨 첫 확인을 즉시 수행. ② `POLL_INTERVAL_SEC`을 0.25~0.4초로 하향(localhost `/history`는 저비용). ③ Unity 폴링은 0.3초 시작 + 지수 백오프. 배치당 1~2초 절감 |
| S5 | **후보 생성 1회마다 ComfyUI `/object_info` 전체를 2번 새로 받음** (캐시 없음) | 생성 버튼을 누른 뒤 실제 샘플링이 시작되기까지 앞단에서 수백 ms~수 초 소모. 커스텀 노드가 많아 응답이 수 MB급이면 악화되고, 일괄 생성에서는 항목 수만큼 곱해진다(10항목 = 20회) | `ComfyUIGenerator/CandidateGenerator.cs:127`(`GetWorkflowsAsync`)·`:164`(`RunPreflightAsync`), 브리지 `Server~/bridge_server.py:574`·`:741`(각각 `fetch_object_info()`), 조회 `:262-270` | **높음** | 브리지에 `fetch_object_info()` TTL 캐시(60초 + `/free`·수동 무효화). 추가로 `GenerateAsync`가 창이 이미 보유한 워크플로 목록을 인자로 받아 `/workflows` 왕복 1회 제거 |
| S6 | `/workflows` 응답 1건을 만들 때 워크플로 JSON을 **(워크플로 수 × 2)회** 디스크에서 다시 읽고 파싱 | 워크플로 5개 → 요청당 파일 열기·JSON 파싱 10회 + `os.listdir`. 창을 열 때마다, ComfyUI가 살아날 때마다, 생성할 때마다 반복 | `Server~/bridge_server.py:576-590`(루프 안에서 `attach_variable_options`의 `load_workflow`(`:315-317`)와 `compute_missing_nodes(load_workflow(name))`(`:583`)가 각각 별도 로드), 파일 목록 `:157-178` | **높음** | 루프 안에서 `load_workflow(name)`을 1회만 호출해 두 함수에 전달 + mtime 기반 메모리 캐시. 디스크 I/O·파싱 50% 이상 즉시 제거 |
| S7 | `/generate` 한 번에 워크플로 JSON을 `count`회(기본 4회) 다시 로드·파싱 | POST `/generate` 응답이 늦어져 생성 버튼을 눌러도 진행률 UI가 뜨기까지 지연. 후보 수를 12로 올리면 12회 반복 | `Server~/bridge_server.py:673-688`(`for i in range(count):` 안의 `build_workflow`(`:675`) → `load_workflow`(`:339`, `:181-201`)) | 중간 | 워크플로를 루프 밖에서 1회 로드·검증하고 `copy.deepcopy`로 시드만 교체. 파일 I/O 4→1회 |
| S8 | 후보 4개의 결과 파일을 **순차로 하나씩** 다운로드 | 배치당 다운로드 시간이 4배로 직렬화. 1024×1024 PNG는 localhost에서 수백 ms 수준이나, 오디오·대형 이미지·원격 ComfyUI에서는 초 단위 | `ComfyUIGenerator/CandidateGenerator.cs:207-229`(`foreach` 안 `await client.DownloadAsync(...)` `:215` → `File.WriteAllBytes` `:224`) | 중간 | 다운로드를 `Task.WhenAll`로 병렬화하고 파일 쓰기만 순차 처리. 다운로드 구간 최대 4배 단축 |
| S9 | 후보 1개 확정에 에셋 임포트가 **3중**으로 걸림 (`ImportAsset` → `SaveAndReimport` → `Refresh`) | 항목 하나 확정할 때마다 AssetDatabase 갱신이 3회. 파이프라인 자동 확정에서는 항목 수만큼 반복되어 항목당 수백 ms~수 초 누적 | `ComfyUIGenerator/CandidateGenerator.cs:339`(`ImportAsset(ForceUpdate)`) → `:343`(`ApplyImageImportSettings`) → `:473`(`importer.SaveAndReimport()`) → `:350`(`AssetDatabase.Refresh()`). 루프 호출부 `Pipeline/PipelineTool.cs:151` | 중간 | `ImportAsset(ForceUpdate)`와 말미 `Refresh()`를 제거하고 `TextureImporter` 설정 후 `SaveAndReimport()` 1회만. 파이프라인 경로는 `StartAssetEditing`/`StopAssetEditing`으로 배치화. 확정 1건당 임포트 3→1회 |
| S10 | 브리지 → ComfyUI 호출이 `urllib.request.urlopen` 기반이라 **커넥션을 재사용하지 않음** | `/history` 폴링만으로 초당 (후보 수)개의 새 TCP 연결 생성·폐기. 4후보 60초 생성 시 약 240개 연결 — localhost에서도 연결 수립·`TIME_WAIT` 오버헤드가 붙는다 | `Server~/bridge_server.py:401-411`(`comfy_request`가 요청마다 `urlopen`), 폴링 호출부 `:478-480` | 중간 | `http.client.HTTPConnection`을 스레드 로컬로 유지하는 keep-alive 방식으로 전환 (표준 라이브러리만 사용) |
| S11 | `[서버 시작]`이 조기 종료 감지를 위해 메인 스레드를 **1초 동기 블로킹** | 버튼을 누르면 정상 시작 시에도 에디터가 정확히 1초 멈춘다 | `ComfyUIGenerator/ComfyUIServerLauncher.cs:581`(`WaitedAndExited(process, 1000)`), 구현 `:1010-1022`(`process.WaitForExit(ms)`) | 중간 | `process.Exited` 이벤트 + `EditorApplication.update` 타이머로 비동기 감지 전환 |
| S12 | Python 탐지가 **실패했을 때는 캐시되지 않아** 매번 후보를 전부 다시 실행 검증 | Python 미설치·스토어 별칭 환경에서 [서버 시작]이나 [자동 탐지]를 누를 때마다 **후보 수 × 최대 5초** 동안 에디터가 멈춘다 (PATH 디렉터리까지 훑어 후보가 여러 개) | 캐시가 성공 경로에만 존재: `ComfyUIGenerator/ComfyUIServerLauncher.cs:438-441`(저장) vs `:449`·`:452-458`(실패, 캐시 없음). 후보 검증이 `process.WaitForExit(5000)`로 블로킹 `:937`, 후보 생성 `:421`·`:720` | 중간 | 실패 결과도 (설정값 해시와 함께) `SessionState`에 캐시하고, 검증 자체를 `Exited` 이벤트 기반 비블로킹으로 전환 |
| S13 | 진행률이 "이미지 1장 완료" 단위로만 갱신돼 **첫 장 동안 0%에 고정** | 4장 중 1장이 끝날 때까지(전체의 25% 구간, 수십 초) 진행률 바가 0%에 멈춰 있어 "멈춘 것처럼" 보인다 — 체감 지연의 큰 원인 | `Server~/bridge_server.py:502-513`(prompt 완료 시점에만 `job["progress"]` 갱신). ComfyUI의 `/progress`·WebSocket 미사용 (`CLIENT_ID`는 `:57`에서 만들지만 큐잉에만 사용 `:425`) | 중간 | 브리지가 ComfyUI WebSocket(`/ws?clientId=`)의 `progress`/`executing` 메시지를 구독해 스텝 단위 진행률 반영. 실제 시간은 같아도 체감 대기가 크게 개선 |
| S14 | `/view` 프록시가 파일 전체를 메모리에 버퍼링한 뒤 한 번에 씀 | 대형 결과물(고해상도 PNG·긴 오디오)에서 브리지 메모리가 파일 크기만큼 튀고 첫 바이트 전송이 지연 | `Server~/bridge_server.py:613-624`(`resp.read()` `:409` → `self.wfile.write(body)` `:624`) | 낮음 | 청크 단위 스트리밍 프록시(`shutil.copyfileobj` 상당)로 변경 |
| S15 | 후보 N개를 `batch_size=1` 워크플로 **N회 큐잉**으로 생성 | prompt 검증·CLIP 인코딩·모델 준비 오버헤드가 후보 수만큼 반복 (샘플링 자체는 동일) | `Server~/bridge_server.py:673-688`, 워크플로 `Server~/workflows/GenerateImage.json` 노드 6 `batch_size: 1`(동일하게 `UI.json` 노드 16, `Audio.json` 노드 9) | 낮음 | `EmptyLatentImage.batch_size`를 후보 수로 올려 1회 큐잉하는 모드를 **옵션으로** 추가. 현재 방식은 후보별 시드 다양성을 보장하므로 기본값은 유지 |
| S16 | `MiniJson` 파서가 `StringReader` 문자 단위 + `Convert.ToChar` + 이중 `Peek`로 동작 | 워크플로 목록 응답에 설치 모델 목록이 붙으면 수백 KB가 되고, 문자당 수 회의 가상 호출·박싱 검사로 파싱이 수십 ms 단위로 늘어난다 | `Common/MiniJson.cs:52`·`:56`(`new StringReader`), `:289-292`(`Convert.ToChar(_json.Peek())`), `:281-287`(`EatWhitespace`가 문자당 `Peek` 2회), `:299-312`. 대형 페이로드 진입점 `ComfyUIGenerator/BridgeClient.cs:672-692` | 낮음 | 문자열 인덱스 기반 파서로 교체(최소한 `PeekChar`를 `(char)` 캐스팅으로). 파싱 시간 2~3배 단축 가능 |

## 5. 감사 결과 — CPU · 알고리즘 · GUI 핫패스 (C)

### 5.1 OnGUI 핫패스 (초당 수십 회 실행 — 창을 열어두기만 해도 비용 발생)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| C1 | **4단계 상세 패널이 매 리페인트마다 프리팹을 로드하고 컴포넌트를 탐색해 `SerializedObject`까지 생성** | 항목을 선택한 채 마우스를 움직이면 CPU 점유가 치솟고 창이 버벅인다. 프리팹이 클수록 심하며, M1의 네이티브 누수와 겹쳐 증상이 배가 | `AssetApplier/AssetApplierWindow.cs:278`(`AssetApplier.GetCurrentValue(item)`) → `AssetApplier/AssetApplier.cs:710-712`(LoadAssetAtPath + 탐색) → `:811-813`(`new SerializedObject`) | **치명** | 선택 변경·편집 시점에만 계산해 `ItemState`에 캐시(`cachedCurrentValue`), `RevalidateState`(`AssetApplierWindow.cs:396`)에서 갱신. 리페인트당 비용이 사실상 0 |
| C2 | **매 리페인트마다 프리팹 전체 Transform 계층을 순회해 경로 문자열 목록을 새로 생성** | 자식 수백 개짜리 프리팹을 선택하면 리페인트마다 배열 1개 + 문자열 수백 개가 생성돼 창 조작 내내 GC 스파이크 | `AssetApplier/AssetApplierWindow.cs:334`(`ObjectPathOptions(prefab)`) → `:375-393`(`GetComponentsInChildren<Transform>(true)` + `string.Join` 루프), `:355`(`labels.ToArray()`) | **치명** | 프리팹 경로가 바뀔 때만 계산해 `_pathOptionsPrefabPath`/`_pathOptions`/`_pathLabels`에 캐시. 라벨 배열도 함께 캐시 |
| C3 | **매 리페인트마다 `EditorPrefs.SetString` 호출** (Windows에서는 레지스트리 쓰기) | 창을 열어두기만 해도 초당 수십 회 레지스트리 쓰기가 발생한다 — 값이 바뀌지 않아도 무조건 실행 | `AssetListup/AssetListupWindow.cs:729-732`, `PromptBuilder/PromptBuilderWindow.cs:161-164` | **높음** | 팝업 선택값이 실제로 바뀐 경우에만 저장하도록 `if (newIndex != _selectedAiIndex)` 가드 추가 — 같은 파일 `:739`·`:750`·`:762`가 이미 쓰는 패턴 |
| C4 | 리페인트마다 `new GUIStyle(...)` 생성 (3개 창, 총 6곳) | GUIStyle은 내부 상태가 무거워 초당 수십 개씩 누적 할당 → 지속적 GC 압박. 생성 중에는 `Repaint()`가 계속 호출돼 악화 | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:316`(`DrawStatusDot`, 리페인트당 2회 호출 `:252`·`:300`), `:519-520`, `:1050`, `AssetApplier/AssetApplierWindow.cs:159-160` | 중간 | 필드에 지연 초기화 캐시. 올바른 예가 이미 저장소에 있음 — `PromptBuilder/PromptBuilderWindow.cs:55`·`:332-335`. 색만 바뀌는 배지는 스타일 1개를 재사용하고 그리기 직전에 색만 대입 |
| C5 | 모델 선택 드롭다운이 매 리페인트 `GUIContent[]` 전체를 재생성 | 체크포인트/LoRA가 수백 개인 ComfyUI 환경에서 3단계 창이 프레임마다 수백 개 객체를 할당 | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:1423`(`new GUIContent[options.Count]`), `:1436`(`new GUIContent[options.Count + 1]`) | 중간 | `VariableState`에 `GUIContent[] cachedOptions`를 두고 `options` 참조가 바뀔 때(`RebuildVariableStates`)만 재생성 |
| C6 | 같은 프리팹을 한 리페인트 안에서 `LoadAssetAtPath`로 **두 번** 로드 | C1/C2와 겹쳐 리페인트 비용을 배가 | `AssetApplier/AssetApplierWindow.cs:302-304`와 `:321-323`(동일 `item.targetPrefabPath`) | 중간 | 첫 로드 결과(`currentPrefab`)를 재사용 |
| C7 | 리페인트마다 LINQ 집계·필터 3회 실행 | 항목 수가 많을수록 리페인트가 선형으로 느려짐(델리게이트 할당 + 리스트 생성 포함) | `AssetApplier/AssetApplierWindow.cs:186-187`(`_states.Count(...)` 2회), `:483`(`_states.Where(...).ToList()`) | 중간 | `ready`/`applied` 카운트를 상태 변경 시점(`RebuildStates`/`ApplyStates`)에만 갱신해 필드로 유지. 리스트 구성은 일괄 적용 시에만 |
| C8 | 리페인트마다 `File.Exists`로 디스크 접근 | 상태 라벨 하나를 위해 초당 수십 회 파일 시스템 스탯 호출 | `AssetListup/AssetListupWindow.cs:541`(`IsEditingLoadedFile()`) → `:587-593`(`File.Exists(_loadedListPath)`) | 중간 | 로드·저장 시점에만 확인해 bool 필드로 캐시하고 `OnFocus`에서 갱신 |
| C9 | 항목 표에 가상화가 없어 **화면 밖 행까지 전부 그림** | 항목 수백 개짜리 목록에서 스크롤이 눈에 띄게 끊긴다. 행마다 TextField 6개 + Popup 2개가 생성됨 | `AssetListup/AssetListupWindow.cs:383-389`(전체 items 루프), 행 구현 `:427-472`. 동일 패턴 `PromptBuilder/PromptBuilderWindow.cs:287-293` | 중간 | 고정 `RowHeight` 상수가 이미 있으므로 `firstVisible = scroll.y / RowHeight`로 가시 영역만 그리는 수동 가상화. 항목 수와 무관한 리페인트 비용 확보 |
| C10 | 리페인트마다 LINQ 배열 생성·선형 탐색·`GUIContent` 할당 | 소규모지만 C9와 곱해져 누적 | `AssetListup/AssetListupWindow.cs:522-524`(`Select(Path.GetFileName).ToArray()`), `:449-457`(`Array.IndexOf` + `new GUIContent`), `PromptBuilder/PromptBuilderWindow.cs:422-430`·`:454-461`, `ComfyUIGenerator/ComfyUIGeneratorWindow.cs`의 행별 `LoadAssetAtPath`, `Common/MCPSettingsWindow.cs:315`(인터페이스 열거자 박싱 → `Common/McpToolRegistry.cs:47-50`) | 낮음 | 표시용 이름 배열은 `RefreshAssetListPaths()`에서 함께 생성, 타입→인덱스는 정적 `Dictionary`, `GUIContent`는 정적 재사용 인스턴스, 도구 이름 목록은 `OnEnable`에서 스냅샷 |

### 5.2 프로젝트 스캔 (1단계)

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| C11 | **프리팹 1개당 계층을 4번 순회**하고, 그중 2번은 `GetComponentsInChildren<Component>(true)`로 전 컴포넌트 배열을 통째로 할당 | 프리팹 2,000개 프로젝트에서 1단계 스캔이 수십 초~분 단위로 걸리고 그동안 에디터가 완전히 멈춘다 | `AssetListup/ProjectScanner.cs:358-374`(`CollectSlots`가 `CollectUIPrefabSlots`를 Image/RawImage로 2회 호출 + SpriteRenderer/AudioSource 2회 순회), 실제 순회 `:312`. 씬 경로도 동일 `:238-249`·`:282` | **높음** | `GetComponentsInChildren<Component>(true)`를 1회만 호출해 배열을 재사용하고, 한 루프에서 Image/RawImage/SpriteRenderer/AudioSource를 모두 분기 판정. 순회·할당이 4→1로 줄어 스캔 시간 약 50~60% 감소 |
| C12 | `IsComponentOfType`이 컴포넌트마다 `Type.BaseType` 체인을 문자열 비교로 거슬러 오르며 **타입별 결과 캐시가 없음** | 컴포넌트 수 × 2회 × 상속 깊이(3~6)만큼의 `string.Equals` — 프리팹 2,000개면 수백만 회. 스캔 시간의 상당 부분을 차지 | `AssetListup/ProjectScanner.cs:329-345`(`for (Type type = component.GetType(); ...)`, 캐시 없음) | **높음** | `Dictionary<Type, bool>`(또는 매칭 종류) 정적 캐시로 타입당 1회만 체인 순회. 사실상 O(고유 타입 수)로 축소 |
| C13 | `ScanOpenScenesAndPrefabs`가 **모든 프리팹을 로드·순회한 뒤에야** 중복을 버림 | 열린 씬에 포함된 프리팹이 많을수록, 이미 스캔한 프리팹을 전부 다시 로드·순회하고 결과만 폐기 — 순수 낭비 | `AssetListup/ProjectScanner.cs:225-233`(`ScanPrefabs(rootPath)` 결과를 받은 뒤 `scannedPrefabPaths.Contains`로 폐기) | 중간 | `ScanPrefabs`에 "제외할 프리팹 경로 `HashSet`" 파라미터를 추가해 **로드 전에** 건너뛴다 |
| C14 | 스캔 전체에 진행률 표시가 없어 장시간 프리즈가 무응답으로 보임 | 대규모 프로젝트에서 "에디터가 죽었다"고 판단해 강제 종료 → 작업 손실 | `AssetListup/ProjectScanner.cs:69-79`(프리팹 루프에 진행률 호출 없음). 참고로 `SpriteSheet/SpriteSheetImporter.cs:164`에는 있음 | 중간 | 프리팹 루프에 `EditorUtility.DisplayProgressBar` + `finally { ClearProgressBar(); }` 추가 (CPU 절감은 아니나 체감·취소 확보) |
| C15 | 기획서 항목 ↔ 스캔 슬롯 매칭이 **O(D×S) 중첩 루프**이며 매 비교마다 `Normalize()`로 문자열을 새로 할당 | 기획서 50개 × 슬롯 5,000개 = 25만 회 비교 × 회당 3개 문자열 = 75만 개 임시 문자열 + GC | `AssetListup/AssetListBuilder.cs:52`(호출), `:365-379`(루프 안 `Normalize(LeafName(...))`·`Normalize(entry.currentAssetName)`), `:420-425` | 중간 | 스캔 엔트리 정규화 키를 루프 진입 전 1회 계산해 `List<(ScanEntry, string objKey, string assetKey)>`로 준비. 할당이 O(D×S)→O(S) |
| C16 | `ContainsAny`가 호출될 때마다 상수 키워드 배열에 `ToLowerInvariant()`를 다시 적용 | 기획서 라인 수 × 키워드 14개만큼의 불필요한 문자열 할당 | `AssetListup/AssetListBuilder.cs:406-418`(`lower.Contains(keyword.ToLowerInvariant())`) | 낮음 | `SectionKeywords`/`UiKeywords`/`AudioKeywords`를 처음부터 소문자 상수로 선언 |

### 5.3 적용(4단계) · 배치 처리

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| C17 | **같은 프리팹을 대상으로 하는 여러 항목이 그룹핑되지 않아 프리팹을 항목 수만큼 반복 저장** | 한 UI 프리팹에 이미지 10개를 적용하면 프리팹 직렬화·디스크 쓰기·재임포트가 10번 발생. 항목이 많을수록 `mcptools_apply_all`이 선형으로 느려진다 | `AssetApplier/AssetApplier.cs:452-472`(씬만 `sceneGroups`로 묶고 프리팹은 `:470`에서 개별 `ApplyToPrefab`), 저장 `:361`(`PrefabUtility.SavePrefabAsset` per item) | **치명** | 씬과 동일하게 `targetPrefabPath` 기준 `Dictionary<string, List<int>>` 그룹을 만들고, 그룹당 **1회 로드 → 전체 할당 → `SavePrefabAsset` 1회**. 저장 비용이 N→1 |
| C18 | 배치 적용 전체를 `StartAssetEditing`/`StopAssetEditing`으로 감싸지 않음 | 항목마다 프리팹 저장이 즉시 임포트 파이프라인을 트리거해 배치 중간중간 에디터가 반복적으로 멈춘다 | `AssetApplier/AssetApplier.cs:444-480`(`ApplyBatch`에 배치 편집 구간 없음). 호출부 `AssetApplier/AssetApplierTool.cs:110`, `Pipeline/PipelineTool.cs:230`, `AssetApplier/AssetApplierWindow.cs:507` | **높음** | `ApplyBatch` 본문을 `try { AssetDatabase.StartAssetEditing(); ... } finally { AssetDatabase.StopAssetEditing(); }`로 감싼다. `SaveAssets()`는 지금처럼 배치 후 1회 유지. C17과 합치면 대량 적용이 크게 단축 |
| C19 | 항목마다 `GenerationResults.json`을 **파일 읽기 + 전체 JSON 파싱**으로 다시 조회 | 항목 100개면 동일 JSON을 100번 읽고 100번 파싱. 결과 파일이 커질수록 제곱에 가깝게 악화 | `AssetApplier/AssetApplier.cs:66-88`(`FindConfirmedAssetPath`의 `File.ReadAllText` + `MiniJson.Deserialize` + 선형 탐색). 항목별 호출 `AssetApplier/AssetApplierTool.cs:98`, `AssetApplier/AssetApplierWindow.cs:588`, `AssetApplier.cs:516` | **높음** | 이미 있는 `CandidateGenerator.GetConfirmedOutputPaths(settings)`(id→경로 `Dictionary`)를 배치 시작 시 1회 호출해 맵을 만들고 항목별 조회는 `TryGetValue`로. I/O·파싱 N→1, 탐색 O(N)→O(1) |
| C20 | 검증과 실제 적용이 프리팹 로드·Transform 탐색·컴포넌트 탐색을 **완전히 동일하게 두 번** 수행 | 항목당 불필요한 중복 작업 2배 | `AssetApplier/AssetApplier.cs:222`·`:229`·`:236`(`ValidateItem`)와 `:354-356`(`ApplyToPrefab`의 동일 3단계) | 중간 | `ValidateItem`이 찾은 `Component`를 `out`으로 반환해 재사용. C17의 그룹핑과 결합하면 로드 횟수가 항목 수 → 프리팹 수로 축소 |
| C21 | `FindTargetComponent`가 기대 컴포넌트 이름마다 `GetComponents<Component>()` 배열을 새로 할당 | 이름이 2개(`Image`/`RawImage`)면 배열을 2번 받아 2번 순회 | `AssetApplier/AssetApplier.cs:778-788` | 낮음 | 배열을 1회만 받고 바깥 루프를 이름이 아닌 컴포넌트 배열로 뒤집는다 |
| C22 | id 매칭에 `FirstOrDefault` 선형 탐색 사용 (O(N×M)) | 확정 100개 × AssetList 500개 = 5만 회 비교 + 델리게이트 할당 | `Pipeline/PipelineTool.cs:213`(`listDoc.items.FirstOrDefault(i => i.id == pair.Key)`), 유사 `PromptBuilder/PromptBuilderWindow.cs:813-814`(병합 루프 내 `FirstOrDefault` + `IndexOf` → O(N²)) | 중간 | `Dictionary<string, AssetListItem>`(및 id→인덱스 맵)으로 1회 인덱싱 후 조회 |

### 5.4 스프라이트 시트 · 기타

| # | 문제 | 증상 | 근거 (파일:줄) | 심각도 | 대응 방안 |
|---|------|------|----------------|--------|-----------|
| C23 | pocket 침식이 픽셀마다 (2K+1)² 윈도를 재스캔하는 **O(N·K²)** 알고리즘 | 4096×4096 기준 최대 4억 회 이상의 내부 루프 — 시트 임포트가 수 초~수십 초 걸리며 그동안 에디터 프리즈 | `SpriteSheet/SpriteSheetImporter.cs:874-903`(`for y / for x` 안에 5×5 윈도 `for wy / for wx`) | **높음** | 체비쇼프 거리 침식은 **분리 가능(separable)** 하므로 가로 K-윈도 침식 → 세로 K-윈도 침식으로 교체(각 O(N·K), 슬라이딩 윈도 시 O(N)). 결과 동일하면서 수십 배 개선 |
| C24 | 격자 경계 검출이 **동일한 전체 픽셀 스캔을 2회** 수행 (세로·가로 각각) | 4096×4096 시트에서 1,670만 픽셀 스캔 2회 = 3,300만 회 연산 | `SpriteSheet/SpriteSheetImporter.cs:228-229`(vertical=true/false로 2회 호출) → `:422-435`(양쪽 모두 전체 `for y / for x` 순회, 판정식 동일) | **높음** | 한 번의 순회에서 `lineCountX[x]`·`lineCountY[y]`를 동시 누적하고 경계 추출만 축별로 분리. 검출 비용 정확히 절반 |
| C25 | 픽셀 인덱스 → 좌표 변환에 BFS 루프마다 `%`/`/` 정수 나눗셈 | 픽셀 수만큼 나눗셈 2회 (나눗셈은 곱셈 대비 수 배 느림) | `SpriteSheet/SpriteSheetImporter.cs:745-746`, `:933-934`, `:966-967` | 낮음 | 큐에 `(x, y)`를 함께 넣거나 행 시작 인덱스를 유지해 나눗셈 제거 |
| C26 | **`MCPToolSettings.GetOrCreate()`가 호출마다 `FindAssets`를 실행** (캐시 없음) | 일괄 적용 시 항목 수만큼 AssetDatabase 검색이 반복. 항목 100개면 `FindAssets` 100회 | `Common/MCPToolSettings.cs:126-163`(`:129`에서 매번 `FindAssets`, 정적 캐시 필드 없음). 반복 호출부 `AssetApplier/AssetApplier.cs:255`·`:341`·`:394`·`:516`, `SpriteSheet/SpriteSheetImporter.cs:995`·`:1040` | **높음** | `private static MCPToolSettings _cached;` 정적 캐시 + null 체크 즉시 반환(무효화는 도메인 리로드 또는 `AssetPostprocessor`). 추가로 `AssetApplier`의 정적 메서드가 `settings`를 파라미터로 받도록 정리해 호출 자체를 제거 |
| C27 | 문서 목록 정렬 비교자가 비교마다 `File.GetLastWriteTime`으로 디스크 조회 | O(n log n)회의 파일 시스템 스탯 호출 — 문서가 수백 개면 새로고침이 눈에 띄게 지연 | `Common/MCPToolFolders.cs:116`(`paths.Sort((a,b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)))`) | 중간 | 경로별 수정 시각을 1회 조회해 튜플 배열로 만든 뒤 정렬(Schwartzian transform). 호출 O(n log n)→O(n) |
| C28 | MCP 호출 1건마다 JSON을 **4번** 직렬화/역직렬화 | 대용량 `scanEntries`(수천 건)를 반환하는 `mcptools_asset_scan`에서 응답 조립만으로 수백 ms 소요 | `McpForUnityBridge/McpForUnityAdapter.cs:27-29`(`@params.ToString()` → `Execute` → `JObject.Parse`), 내부 `Common/McpToolRegistry.cs:95`·`:124` | 낮음 | 호출당 1회라 우선순위는 낮으나, 대용량 응답 경로는 `MiniJson.Serialize` 결과 문자열을 그대로 반환하는 경로를 둔다 |

## 6. 확인 결과 이상 없음 (가설 검증 결과 문제가 아닌 항목)

| 항목 | 확인 내용 | 근거 |
|------|-----------|------|
| 후보 프리뷰 텍스처 누수 | **정상.** 소유 여부를 `_ownsTexture`로 추적하고 로드 실패 시·재로드 시·`OnDisable`에서 `DestroyImmediate`. `HideFlags.HideAndDontSave`도 올바름 | `ComfyUIGenerator/CandidatePreviewWindow.cs:33`, `:43`, `:52`, `:64-78` |
| 스프라이트 시트 대형 텍스처 누수 | **정상.** 원본·저장용 텍스처 모두 `try/finally`로 해제하며 실패 경로도 해제 후 예외 | `SpriteSheet/SpriteSheetImporter.cs:167-204`(특히 `:203`), `:626-638`, `:1006-1016` |
| `EditorApplication.update` 구독 누적 | **정상.** `OnEnable` `+=` / `OnDisable` `-=` 짝이 맞으며, 코드베이스 전체에서 이 구독은 한 곳뿐 | `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:119`·`:125` |
| `EditorApplication.quitting` 중복 구독 | **정상.** `-=` 후 `+=` 패턴으로 도메인당 1회만 유지 | `ComfyUIGenerator/ComfyUIServerLauncher.cs:184-185` |
| 브리지 python 프로세스 잔존 | **정상(설계됨).** PID를 `SessionState`에 저장해 도메인 리로드 후에도 제어 가능하고, 종료 시 `taskkill /T /F`로 프로세스 트리 정리. 외부 기동 서버는 `POST /shutdown` 제공 | `ComfyUIServerLauncher.cs:587`, `:339-369`, `:599-629`, `:646-678` |
| `HttpClient`/`UnityWebRequest` 미해제 | **정상.** `UnityWebRequest` 사용처 없음. `BridgeClient`·`ComfyUIClient` 모두 `IDisposable` 구현 + 전 호출부 `using`, 요청별 타임아웃 CTS도 `using`. (소켓 재사용 문제는 M6으로 별도 기록) | `BridgeClient.cs:268-270`·`:547-548`·`:586-592`·`:642-643`·`:659-661`, `Common/ComfyUIClient.cs:61-62`·`:106-108`·`:239-240` |
| `LoadPrefabContents` 후 `UnloadPrefabContents` 누락 | **정상.** `LoadPrefabContents` 사용처가 전혀 없다. 프리팹 수정은 `LoadAssetAtPath` + `PrefabUtility.SavePrefabAsset` 방식(Undo 지원 목적으로 의도적 채택)이라 해당 누수가 성립하지 않음 | `AssetApplier/AssetApplier.cs:41-46`(설계 근거 주석), `:354-361` |
| Additive 씬 미종료 | **정상.** 모두 `openedHere` 플래그 + `try/finally CloseScene` | `AssetApplier/AssetApplier.cs:428-434`·`:540-546`, `AssetListup/ProjectScanner.cs:130-141` |
| AI CLI 경로의 `Process`/`StreamWriter` 미해제 | **정상.** 프로세스·stdin·locator 모두 `using`, 임시 파일은 `finally`에서 삭제. (도메인 리로드 시 잔존 문제는 M2로 별도 기록) | `Common/AiCliRunner.cs:392`, `:402-407`, `:172`, `:325-336` |
| Unity 오브젝트를 잡는 정적 캐시 | **정상.** 정적 필드는 문자열·값 타입만 보관 | `Common/MCPToolSettings.cs:29`(`_installRoot`), `Common/AiCliRunner.cs:57-66`(알려진 CLI 5종 상한), `ComfyUIGenerator/EditorAudioPreview.cs:18-22`, `Common/McpToolRegistry.cs:27` |
| 임시 `ScriptableObject` 프로브 누수 | **정상.** 설치 루트 역산용 인스턴스를 `finally`에서 `DestroyImmediate` | `Common/MCPToolSettings.cs:175-187` |
| 오디오 미리 듣기 핸들 누적 | **정상.** `AudioClip`을 새로 만들지 않고 임포트본을 재생하며, 재생 전·창 닫힘·항목 전환·생성 시작 시 항상 `Stop()` | `ComfyUIGenerator/EditorAudioPreview.cs:54`, `ComfyUIGeneratorWindow.cs:127`·`:1169` |
| 후보 4개를 순차 제출하며 N번째 완료 후 N+1을 제출 | **정상.** `count` 전부를 `/prompt`로 **먼저 모두 큐잉**한 뒤 job을 만들고 폴링을 시작하므로 ComfyUI 큐가 연속 처리되고 유휴 구간이 없다 | `Server~/bridge_server.py:673-688`, `:696-708`, `:464-521` |
| 완료 후에도 폴링이 계속 돎 | **정상.** 브리지는 `pending`이 비면 즉시 루프 종료 후 스레드 종료, Unity도 `completed` 확인 즉시 `break` | `Server~/bridge_server.py:471`·`:515-521`, `ComfyUIGenerator/CandidateGenerator.cs:184-187` |
| 후보 파일마다 `AssetDatabase.Refresh()` 호출 | **정상.** Refresh는 다운로드 루프 **밖에서 1회**만 | `ComfyUIGenerator/CandidateGenerator.cs:207-229` → `:232` |
| 생성 경로의 `.Result`/`.Wait()`/`Thread.Sleep`/모달 진행률 | **정상.** `BridgeClient`·`CandidateGenerator`·`ComfyUIGeneratorWindow` 전 경로가 `async/await` + `Task.Delay`. `DisplayProgressBar`는 스프라이트시트 임포터에만 존재. **유일한 예외가 S2의 `PipelineTool`** | `BridgeClient.cs` 전역, `CandidateGenerator.cs:189`, `SpriteSheetImporter.cs:164`·`:178`·`:186`·`:331`·`:384` |
| 브리지가 단일 스레드라 폴링 중 다른 요청이 막힘 | **정상.** `ThreadingHTTPServer` + `HTTP/1.1` + `Content-Length` 명시로 요청별 스레드 처리와 keep-alive가 성립 (`allow_reuse_address=False`는 포트 중복 방지 목적이며 성능과 무관) | `Server~/bridge_server.py:823-832`, `:535`, `:542-548` |
| `AssetDatabase.FindAssets`가 `Assets` + `Packages` 전체를 검색 | **정상.** 프리팹 스캔은 `new[] { rootPath }`로, 설정 에셋 검색은 `new[] { "Assets" }`로 범위 제한 | `AssetListup/ProjectScanner.cs:62-68`, `Common/MCPToolSettings.cs:129` |
| `InstallRoot` 해석이 호출마다 반복 | **정상.** 정적 필드에 캐시되어 도메인 리로드당 1회만 계산. 스크립트 위치는 리임포트(=도메인 리로드) 없이 변하지 않으므로 무효화 누락 위험도 없음 | `Common/MCPToolSettings.cs:29`, `:38-49` |
| `GetOrCreate()`가 OnGUI에서 직접 호출됨 | **정상(창 한정).** 모든 창이 `OnEnable`에서 필드에 캐시하고 OnGUI는 null 가드로만 접근. (정적 메서드 경로의 반복 호출은 C26으로 별도 기록) | `AssetApplierWindow.cs:73`·`:79-82`, `AssetListupWindow.cs:97`·`:119-122`, `PromptBuilderWindow.cs:80`·`:101-104`, `Pipeline/PipelineWindow.cs:36`·`:75-78`, `MCPSettingsWindow.cs:32`·`:37-40` |
| OnGUI에서 `Directory.GetFiles`·문서 목록 스캔·JSON 파싱 실행 | **정상.** `FindDocuments`/`ListTemplateNames`/`MiniJson.Deserialize` 호출은 모두 `OnEnable`·`OnFocus`·버튼 핸들러 경로에만 존재 | `AssetApplierWindow.cs:541-546`·`:553`, `AssetListupWindow.cs:600-606`·`:571`, `PromptBuilderWindow.cs:490-501`, `Pipeline/PipelineWindow.cs:34-55` |
| 상태 캐시 재계산이 리페인트마다 발생 (3단계) | **정상.** `RefreshItemStatuses`(디스크 스캔 + JSON 파싱)는 일괄 시작·종료·확정·문서 로드 이벤트에서만 호출되고 OnGUI 경로에 없음 | `ComfyUIGeneratorWindow.cs`의 `RefreshItemStatuses` 호출부 4곳, `OnGUI`는 `:207-238` |
| 문서 저장 시마다 전체 `Refresh()` 호출 | **정상.** 폴더를 새로 만든 경우에만 전체 Refresh, 평시에는 대상 파일만 `ImportAsset` | `AssetListup/AssetListBuilder.cs:243-250`, `PromptBuilder/PromptBuilder.cs:176-183`, `SpriteSheetImporter.cs:1000`·`:1018-1025` |
| 씬 항목 일괄 적용 시 같은 씬을 반복해서 엶 | **정상.** `sceneGroups`로 씬 경로별로 묶어 1회만 열고 그룹 처리 후 1회 저장·닫기. (**프리팹만 이 처리가 빠져 있음 → C17**) | `AssetApplier/AssetApplier.cs:452-476`, `:483-547` |
| `AssetDatabase.SaveAssets()`가 항목 루프 안에서 호출 | **정상.** 세 호출 경로 모두 배치 완료 후 1회만 | `AssetApplierTool.cs:131`, `AssetApplierWindow.cs:524`, `Pipeline/PipelineTool.cs:251` |
| 슬라이스 기록이 텍스처를 재임포트하며 픽셀을 반복 처리 | **정상.** rect 기록 후 `SaveAndReimport()` 1회만 호출하고 픽셀 재조립을 하지 않음 | `SpriteSheetImporter.cs:1065-1071`, `SpriteSlicing/SpriteSliceWriter.cs:29-67` |
| 배경 제거의 픽셀 판정이 중복 계산 | **정상.** `nearWhite`/`neutral`을 픽셀당 1회 사전 계산해 재사용. 셀 콘텐츠 판정도 조기 종료 있음 | `SpriteSheetImporter.cs:653-665`, `:263-272` |
| Python 탐지가 매번 프로세스를 띄움 | **성공 시 정상.** 성공 결과는 `SessionState`에 캐시. (실패 시 미캐시는 S12) | `ComfyUIServerLauncher.cs:404-418`, `:438-441` |
| 5초 주기 서버 상태 폴링 중첩 실행 | **정상.** `_serverChecking` 가드로 이전 요청 완료 전에는 새 요청을 보내지 않음 | `ComfyUIGeneratorWindow.cs:130-137`, `:141`, `:174-177` |

## 7. 작업 항목

### 7.1 1순위 — 효과 대비 비용이 가장 좋은 것 (치명 + 즉효)

1. **[S1] 모델 언로드 기본값·호출 위치 수정** — `unloadModelsAfterBatch` 기본값 `false` + 단건 경로 호출 제거. 사실상 몇 줄 변경으로 **반복 생성 회차당 10~40초 절감**. 가장 먼저 한다.
   - 대상: `Common/MCPToolSettings.cs:117`, `Common/MCPToolSettings.asset:26`, `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:1974`
2. **[S2] `PipelineTool` 메인 스레드 블로킹 제거** — 잡 모델 전환(또는 최소한 취소 토큰·타임아웃 전달). 에디터 프리즈 제거.
   - 대상: `Pipeline/PipelineTool.cs:104-118`
3. **[C1 + C2 + M1] 4단계 창 리페인트 캐시화** — 현재 값·경로 목록을 `ItemState`에 캐시하고 `SerializedObject`를 `using`으로 감싼다. 치명 3건이 한 번에 해결된다.
   - 대상: `AssetApplier/AssetApplierWindow.cs:278, 302-334, 375-393`, `AssetApplier/AssetApplier.cs:631, 796, 811`, `AssetListup/ProjectScanner.cs:352`
4. **[C17 + C18 + C19] 일괄 적용 배치화** — 프리팹 경로 그룹핑 + `StartAssetEditing`/`StopAssetEditing` + 확정 경로 맵 1회 조회.
   - 대상: `AssetApplier/AssetApplier.cs:66-88, 444-480`
5. **[S3] 파이프라인 루프 내 `AssetDatabase.Refresh()` 제거**.
   - 대상: `Pipeline/PipelineTool.cs:115`

### 7.2 2순위 — 높음

6. **[S4] 폴링 지연 축소** — 브리지 `time.sleep`을 루프 끝으로 이동 + 간격 하향, Unity는 지수 백오프. (`bridge_server.py:66, 471-476`, `CandidateGenerator.cs:189`)
7. **[S5 + S6] 브리지 캐시 도입** — `/object_info` TTL 캐시 + 워크플로 JSON mtime 캐시, `/workflows` 루프의 중복 로드 제거. (`bridge_server.py:262-270, 576-590`)
8. **[C11 + C12] 스캔 순회 통합 + 타입 판정 캐시** — 계층 순회 4→1회, `Dictionary<Type, bool>` 캐시. (`ProjectScanner.cs:312, 329-345, 358-374`)
9. **[C26] `MCPToolSettings.GetOrCreate()` 정적 캐시**. (`MCPToolSettings.cs:126-163`)
10. **[C3] 리페인트마다 `EditorPrefs.SetString` 호출 제거** — 변경 시에만 저장. (`AssetListupWindow.cs:729-732`, `PromptBuilderWindow.cs:161-164`)
11. **[C23 + C24] 시트 임포터 알고리즘 개선** — 침식을 분리 가능 필터로, 격자 검출을 단일 순회로. (`SpriteSheetImporter.cs:228-229, 422-435, 874-903`)

### 7.3 3순위 — 중간

12. **[M2]** AI CLI 프로세스 레지스트리 + 도메인 리로드·종료 훅.
13. **[M6]** `HttpClient` 공유 (5초 폴링 소켓 누적 해소).
14. **[M7]** MCP Job 딕셔너리 정리 + `running` 고착 해소(타임아웃).
15. **[M4 + M5]** `Repaint()` 파괴 가드, CTS Cancel/Dispose 경쟁 정리.
16. **[M3]** 브리지 `Process` 객체 Dispose.
17. **[M9]** 시트 임포터 임시 배열 통합 (메모리 스파이크 절반).
18. **[S7~S13]** 워크플로 로드 1회화, 다운로드 병렬화, 확정 임포트 3→1, 브리지 keep-alive, [서버 시작] 1초 블로킹 제거, Python 탐지 실패 캐시, WebSocket 스텝 진행률.
19. **[C4~C10, C13~C16, C20~C22, C27]** GUI 캐시화, 스캔 조기 제외·진행률, 매칭 자료구조 개선, 정렬 비교자 개선.

### 7.4 4순위 — 낮음 (여력이 있을 때)

20. **[M8]** 브리지 JOBS TTL 스윕. **[S14]** `/view` 스트리밍. **[S15]** `batch_size` 옵션. **[S16]** `MiniJson` 파서 교체. **[C25]** 나눗셈 제거. **[C28]** MCP JSON 4중 변환 축소.

## 8. 검증 방법 (재현 시나리오)

**A. 메모리**

1. 4단계 창에서 프리팹 대상 항목을 선택한 채 **5분간 마우스를 움직이며 방치** → Profiler(Memory)에서 네이티브 메모리·GC 할당이 증가하지 않아야 함 (M1/C1/C2).
2. "후보 4개 생성"을 **20회 연속** 실행 → 에디터 메모리가 회차마다 우상향하지 않아야 함.
3. AI CLI 실행 중 스크립트를 저장해 **도메인 리로드 유발** → 작업 관리자에 `claude`/`codex` 프로세스가 남지 않아야 함 (M2).
4. 3단계 창을 **1시간 열어둔 뒤** `netstat`로 `TIME_WAIT` 소켓 수 확인 → 수백 개 누적이 없어야 함 (M6).
5. MCP로 생성을 여러 번 실행한 뒤 도메인 리로드 → 같은 `assetItemId`로 재생성 시 "이미 실행 중" 오류가 나지 않아야 함 (M7).
6. 4096×4096 시트 임포트 중 Profiler로 피크 메모리 확인 → 100MB급 임시 할당이 절반 이하 (M9).

**B. 속도**

7. 동일 항목으로 "후보 4개 생성"을 **연속 3회** 실행하고 회차별 소요 시간 측정 → 2·3회차가 1회차와 비슷해야 함(모델 재로드 없음). 개선 전후를 함께 기록 (S1).
8. steps=4 고속 워크플로(`UI.json`)로 생성 → 마지막 이미지 완료 후 UI 반영까지 **0.5초 이내** (S4).
9. `mcptools_run_pipeline`을 10항목으로 실행 → 실행 중 **에디터 창을 드래그·스크롤할 수 있어야 함** (S2), 총 소요 시간이 개선 전 대비 단축 (S3).
10. 브리지 로그·`/workflows` 응답 시간 측정 → 두 번째 호출부터 캐시 적중으로 즉시 응답 (S5/S6).

**C. CPU**

11. **프리팹 2,000개 이상** 프로젝트(또는 테스트용 대량 프리팹 생성)에서 1단계 스캔 → 소요 시간 개선 전후 비교, 진행률 바 표시 및 취소 동작 (C11/C12/C14).
12. 한 프리팹에 **10개 항목**을 지정하고 일괄 적용 → 프리팹 저장이 1회만 발생하는지 확인(수정 시각·콘솔 로그), 소요 시간 단축 (C17/C18).
13. 항목 200개 AssetList를 1단계 창에 로드하고 스크롤 → 끊김 없이 스크롤되어야 함 (C9).
14. 4096×4096 시트 임포트 소요 시간 개선 전후 비교 (C23/C24).

**D. 회귀**

15. Task 1~7의 사용자 에디터 테스트 항목이 전부 그대로 통과해야 한다. 특히 **C17(프리팹 그룹핑)·S9(임포트 축소)·C12(타입 판정 캐시)** 는 동작 변경 위험이 있으므로 적용 결과·Sprite 임포트 설정·스캔 결과가 개선 전과 동일한지 대조한다.
16. `unloadModelsAfterBatch` 기본값 변경 후 **VRAM이 작은 환경**에서 연속 생성 시 OOM이 나지 않는지 확인. 문제가 있으면 설정으로 되돌릴 수 있어야 하며 README에 트레이드오프를 기록한다.

## 9. 산출물

- 개선된 `Assets/MCPTools/` (누수 해소, 배치화, 캐시화)
- 개선된 `Server~/bridge_server.py` (폴링·캐시·keep-alive·TTL)
- 갱신된 `Assets/MCPTools/README.md` — `unloadModelsAfterBatch` 기본값 변경과 VRAM 트레이드오프, 대규모 프로젝트 스캔 안내
- 갱신된 `CHANGELOG.md` + 버전 상향 (`package.json` / `MCPToolsInfo.Version` / git 태그 동기 — [릴리스절차.md](../릴리스절차.md))
- 개선 전후 측정치를 기록한 체크리스트

## 10. 완료 조건

- 체크리스트: [Task8_체크리스트.md](../checklist/Task8_체크리스트.md)
- §7.1(1순위) + §7.2(2순위) 전부 구현
- §8 시나리오 A·B·C 통과 및 **§1의 정량 목표 달성 여부를 측정치로 기록**
- §8 시나리오 D(회귀) 통과 — Task 1~7 기능에 변화 없음
- 사용자 에디터 테스트 통과
