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

- [ ] **[S1]** 모델 언로드 기본값·호출 위치 수정 — `unloadModelsAfterBatch` 기본값 `false`, 단건 생성 경로의 `TryFreeMemoryAsync()` 호출 제거(일괄 경로만 유지)
  - 대상: `Common/MCPToolSettings.cs:117`, `Common/MCPToolSettings.asset:26`, `ComfyUIGenerator/ComfyUIGeneratorWindow.cs:1974`
  - 측정: 연속 3회 생성 회차별 소요 시간 (개선 전 → 개선 후)
- [ ] **[S2]** `PipelineTool` 메인 스레드 블로킹 제거 — 잡 모델 전환 또는 취소 토큰·타임아웃 전달
  - 대상: `Pipeline/PipelineTool.cs:104-118`
- [ ] **[C1 + C2 + M1]** 4단계 창 리페인트 캐시화 + `SerializedObject` `using` 처리
  - 대상: `AssetApplier/AssetApplierWindow.cs:278, 302-334, 375-393`, `AssetApplier/AssetApplier.cs:631, 796, 811`, `AssetListup/ProjectScanner.cs:352`
- [ ] **[C17 + C18 + C19]** 일괄 적용 배치화 — 프리팹 경로 그룹핑 + `StartAssetEditing`/`StopAssetEditing` + 확정 경로 맵 1회 조회
  - 대상: `AssetApplier/AssetApplier.cs:66-88, 444-480`
- [ ] **[S3]** 파이프라인 루프 내 `AssetDatabase.Refresh()` 제거
  - 대상: `Pipeline/PipelineTool.cs:115`

## 2. 2순위 구현 (높음)

- [ ] **[S4]** 폴링 지연 축소 — 브리지 `time.sleep`을 루프 끝으로 이동 + 간격 하향, Unity 지수 백오프 (`bridge_server.py:66, 471-476`, `CandidateGenerator.cs:189`)
- [ ] **[S5 + S6]** 브리지 캐시 — `/object_info` TTL 캐시, 워크플로 JSON mtime 캐시, `/workflows` 루프 중복 로드 제거 (`bridge_server.py:262-270, 576-590`)
- [ ] **[C11 + C12]** 스캔 계층 순회 4→1회 통합 + `Dictionary<Type, bool>` 타입 판정 캐시 (`ProjectScanner.cs:312, 329-345, 358-374`)
- [ ] **[C26]** `MCPToolSettings.GetOrCreate()` 정적 캐시 (`MCPToolSettings.cs:126-163`)
- [ ] **[C3]** 리페인트마다 `EditorPrefs.SetString` 호출 제거 (`AssetListupWindow.cs:729-732`, `PromptBuilderWindow.cs:161-164`)
- [ ] **[C23 + C24]** 시트 임포터 — 침식을 분리 가능 필터로, 격자 검출을 단일 순회로 (`SpriteSheetImporter.cs:228-229, 422-435, 874-903`)

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

- [ ] `Assets/MCPTools/README.md` — `unloadModelsAfterBatch` 기본값 변경과 VRAM 트레이드오프, 대규모 프로젝트 스캔 안내
- [ ] `CHANGELOG.md` + 버전 동기 (`package.json` / `MCPToolsInfo.Version` / git 태그 — [릴리스절차.md](../릴리스절차.md))

## 6. 사용자 에디터 테스트

> Task 문서 §8의 재현 시나리오. **개선 전 측정치를 먼저 기록**해야 효과를 판정할 수 있다.

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
