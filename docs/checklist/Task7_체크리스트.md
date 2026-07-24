# Task 7 체크리스트 — 배포 호환성 (다른 PC / 다른 Unity 프로젝트)

> Task 문서: [Task7_배포호환성.md](../tasks/Task7_배포호환성.md)
> 2026-07-24 감사 기준. 감사 결과 문제 목록(D1~D17)과 항목 번호가 대응한다.
> 2026-07-24 추가: **GitHub + Package Manager(UPM) 배포 전환** 감사(U1~U14)를 §4에 반영했다. Task 문서 §9 참조.
> 선행 조치(Python 자동 탐지·검증·조기 종료 감지)는 이미 완료되어 이 체크리스트의 회귀 확인 항목으로만 남는다.

## 1. 구현 체크리스트 — 즉시 조치 (치명 / 높음)

- [x] **[D1] 워크플로 모델 파일 사전 검증** — 생성 전에 `/object_info` 선택지와 최종 워크플로 값을 대조해 누락 목록을 반환하고, Unity가 다이얼로그 + 변수 UI 경고색으로 안내
  - 구현 결과:
    - 브리지에 `POST /preflight` 신설 — `{workflow, variables}`를 받아 `/generate`와 **동일한 공유 함수**(`build_workflow`)로 변수 치환한 뒤, `/object_info`의 각 노드 입력 중 **선택지(choice) 목록이 있는 입력**의 최종 값이 선택지에 없으면 `invalidInputs`로 반환 (`{node, classType, field, value, availableSample(≤10), availableCount}`). 로더 필드명을 하드코딩하지 않는 일반 판정이라 ckpt/unet/lora/clip/vae는 물론 `LoadImage.image` 등 모든 choice 필드를 커버. 선택지 0개(모델 미설치 ComfyUI)도 `availableCount: 0`으로 검출.
    - choice 스펙은 구형 `[[...]]`과 신형 `["COMBO", {options}]` 두 포맷 지원. 노드 연결값·숫자·자유 입력은 검증 제외(오탐 없음).
    - `CandidateGenerator.GenerateAsync`가 후보 폴더 정리(`ClearFolder`) **이전에** preflight를 호출하고, 실패 시 `EditorUtility.DisplayDialog`(누락 값 + 설치된 값 예시 + 조치)를 띄운 뒤 취소 예외로 중단(이중 다이얼로그 방지, 일괄 생성도 즉시 중단). 변수 드롭다운은 현재 값이 옵션에 없으면 **빨간 배경 + 경고 툴팁**.
    - ComfyUI 미기동 시 `comfyReachable=false`로 경고 없이 통과시켜 기존 "ComfyUI 미연결" 안내가 단독 표시됨(이중 안내 없음). 구버전 브리지(`/preflight` 없음)면 경고 로그만 남기고 생성 계속.
  - 검증 상태: Python `py_compile` 통과 + **실서버 스모크 테스트 통과**(미기동 폴백, 노드 누락 검출, choice 3포맷, 값 일치 통과, 404). C# 정적 검토 완료 — **Unity 에디터 컴파일·실동작 확인 필요** (아래 §5.2 테스트).
  - 관련 파일: `Server~/bridge_server.py`(151-178, 207-282, 441-467, 506, 541, 581-626), `Editor/ComfyUIGenerator/BridgeClient.cs`(86-146, 255-409), `Editor/ComfyUIGenerator/CandidateGenerator.cs`(136-139, 205-289), `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(865-889)
- [x] **[D2] 커스텀 노드 preflight** — 워크플로 `class_type` 집합과 `/object_info` 키를 대조해 누락 노드 안내
  - 구현 결과:
    - `GET /workflows` 응답의 각 워크플로에 `missingNodes`(class_type ∖ object_info 키), 최상위에 `comfyReachable` 추가. ComfyUI 미연결·워크플로별 검증 예외 시 개별 폴백으로 목록 응답 자체는 항상 유지.
    - 창의 워크플로 선택 영역 아래 HelpBox — "필요한 커스텀 노드 N개가 ComfyUI에 없습니다: … ComfyUI-Manager에서 설치 후 ComfyUI를 재시작". 미연결 상태에서는 표시 안 함. `/preflight` 응답에도 `missingNodes` 포함되어 생성 차단 다이얼로그에 함께 안내.
    - README의 ComfyUI-Manager 설치 링크 보강은 U9(README 전면 교체)에서 일괄 처리.
  - 검증 상태: D1과 동일 (브리지 스모크 통과, Unity 에디터 확인 필요).
  - 관련 파일: `Server~/bridge_server.py`, `Editor/ComfyUIGenerator/BridgeClient.cs`, `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(65-67, 166-169, 712-731)
- [x] **[D3] uGUI(`com.unity.ugui`) 의존 방어** — `ProjectScanner`에서 uGUI 참조를 완전히 제거 (별도 asmdef 분리 대신, 코드베이스에 이미 있던 타입 이름 판정 + SerializedProperty 조회 패턴을 채택)
  - 구현 결과:
    - `using UnityEngine.UI;` 제거. `Image`/`RawImage` 제네릭 사용부를 삭제하고 `CollectUISceneSlots`(씬) / `CollectUIPrefabSlots`(프리팹) 두 private 메서드로 교체. 두 메서드는 `GetComponentsInChildren<Component>(true)`로 순회하며 `IsComponentOfType`으로 대상만 고른다.
    - `IsComponentOfType(Component, string)` — `for (Type t = component.GetType(); t != null; t = t.BaseType)`로 기반 타입을 거슬러 올라가며 이름을 비교하므로, 기존 `GetComponentsInChildren<Image>`와 동일하게 **사용자 정의 파생 클래스(`MyImage : Image`)도 수집**된다. Missing 스크립트로 인한 `null` 컴포넌트는 걸러낸다.
    - `GetReferencedAssetName(Component, string)` — `SerializedObject` → `FindProperty("m_Sprite"/"m_Texture")` → `objectReferenceValue?.name`. `AssetApplier.GetObjectProperty`와 동일한 방식이며, `Image.sprite`/`RawImage.texture` 프로퍼티가 각각 `m_Sprite`/`m_Texture`를 그대로 반환하므로 값이 일치한다.
    - `SpriteRenderer`/`AudioSource`는 uGUI가 아니므로 기존 제네릭 `CollectComponentSlots<T>` 경로를 **그대로 유지**했다.
    - `Editor/MCPTools.Editor.asmdef`는 원래 uGUI를 참조하지 않아 **변경 없음**(확인만 함). 신규 asmdef·신규 클래스·신규 파일 없음.
    - README 요구 사항에 "uGUI는 **선택 사항**, 없으면 Image/RawImage 슬롯만 스캔되지 않음"을 명시.
  - 검증 상태: 정적 검증 완료 (Unity 미실행). `Assets/MCPTools` 전체에서 `UnityEngine.UI` 문자열 0건 재확인. 동작 보존 근거 — 수집 순서(루트별 Image → RawImage → SpriteRenderer → AudioSource), `componentType` 문자열(`"Image"`/`"RawImage"` 리터럴 그대로), `isUI=true`, 프리팹 인스턴스 제외(`PrefabUtility.IsPartOfPrefabInstance`) 규칙이 모두 동일. `GetComponentsInChildren<Component>(true)`는 `GetComponentsInChildren<Image>(true)`와 같은 깊이 우선 순회 순서를 가지므로 필터링 결과의 상대 순서도 동일하다. **에디터 컴파일 확인 필요.**
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/AssetListup/ProjectScanner.cs`, `MCPToolTest/Assets/MCPTools/README.md`
- [x] **[D4] 설치 경로 독립화** — 브리지 스크립트 경로·설정 에셋 경로·템플릿 폴더 경로를 `Assets/MCPTools` 하드코딩에서 **앵커 에셋 기준 역산**으로 교체
  - 구현 결과:
    - `MCPToolSettings.InstallRoot`(`public static string`, static 필드 캐시) 신설 — ① `AssetDatabase.FindAssets("t:MCPToolSettings")` 결과 경로 → ② `MonoScript.FromScriptableObject(CreateInstance<MCPToolSettings>())`의 스크립트 경로(임시 인스턴스는 `finally`에서 `DestroyImmediate`) → ③ `"Assets/MCPTools"` 폴백 순으로 계산. `RootFromCommonFolderPath`가 경로가 `<루트>/Editor/Common/<파일>` 형태인지 **검증**하고 아니면 다음 후보로 넘어간다.
    - `MCPToolSettings.AssetPath`를 `public const` → `public static string` 속성으로 전환 (`InstallRoot + "/Editor/Common/MCPToolSettings.asset"`). 전체 grep 결과 이 클래스 밖 사용처·상수 문맥(attribute 인자, switch case) 사용 **0건**이라 추가 정리 불필요.
    - `GetOrCreate()` — `FindAssets("t:MCPToolSettings")`로 **위치와 무관하게** 기존 에셋을 먼저 찾아 사용하고, 2개 이상이면 첫 번째를 쓰면서 전체 경로를 `Debug.LogWarning`으로 안내. 없을 때만 `AssetPath`에 생성하며, `EnsureFolder`는 상위 폴더부터 **재귀 생성**하도록 바꿔 `Assets/Plugins/MCPTools/Editor/Common` 같은 다단계 경로도 안전하다.
    - `ComfyUIServerLauncher.ScriptRelativePath`를 설치 루트 기준(`"Editor/ComfyUIGenerator/Server~/bridge_server.py"`)으로 바꾸고, `GetScriptPath()`가 `InstallRoot`의 선행 `"Assets"`을 `Application.dataPath`로 치환해 절대 경로를 조합한다(`Server~`는 에셋이 아니라 AssetDatabase로 찾을 수 없음).
    - 스크립트 미발견 예외 메시지에 **탐색한 실제 경로 + 인식된 설치 루트 + `Server~` 폴더 동반 복사 확인 안내**를 포함.
    - `PromptTemplate.TemplatesFolder`도 `const` → `static` 속성(`InstallRoot + "/Editor/PromptBuilder/Templates"`)으로 전환. 사용처는 모두 같은 파일 내부라 코드 변경 없이 동작.
    - 코드 전체 재grep 결과 남은 `"Assets/MCPTools"` 리터럴은 `MCPToolSettings.DefaultInstallRoot`(의도된 폴백) 1곳뿐이며, README·주석의 설명용 경로는 그대로 두었다.
  - 검증 상태: 정적 검증 완료 (Unity 미실행). 표준 설치(`Assets/MCPTools/`) 기준 회귀 없음 근거 — `InstallRoot`가 `"Assets/MCPTools"`로 계산되므로 `AssetPath`=`Assets/MCPTools/Editor/Common/MCPToolSettings.asset`, `TemplatesFolder`=`Assets/MCPTools/Editor/PromptBuilder/Templates`, `GetScriptPath()`=`<dataPath>/MCPTools/Editor/ComfyUIGenerator/Server~/bridge_server.py`로 **수정 전과 문자열이 동일**하다. **에디터 컴파일·실동작 확인 필요.**
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/Common/MCPToolSettings.cs`, `MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`, `MCPToolTest/Assets/MCPTools/Editor/PromptBuilder/PromptTemplate.cs`, `MCPToolTest/Assets/MCPTools/README.md`

## 2. 구현 체크리스트 — 후속 (중간)

- [x] **[D5] 참조 이미지 기본값 정리**
  - 구현 결과:
    - `variables.json`의 image 타입 변수 3개(36/45/46행) 기본값을 `""`로, description을 "[파일 선택]으로 지정해야 함" 취지로 정리. `UI.json:130`·`StyleChange.json:141,166`의 `LoadImage.image` 잔재 파일명도 `""`로 교체(노드 구조·키 순서 불변). 나머지 워크플로 3종에는 잔재 없음을 전수 확인.
    - **차단은 UI 단에서** — `MissingImageVariableLabels()` 신설(매니페스트 `def.type == "image"`로만 판정, 하드코딩 없음, `visibleWhen`으로 숨겨진 변수 제외, 로컬 파일·서버 파일명 둘 다 비었을 때만 미지정). 미지정이면 [후보 N개 생성]/[전체 생성]을 함께 비활성화하고 "참조 이미지를 선택해주세요: {라벨}" 경고 표시. 변수 행 표시도 `(미지정 — [파일 선택]으로…)`로 보강.
    - **preflight와 중복 안내 방지**: 빈 값을 그대로 제출하면 preflight가 `노드 #17 (LoadImage) image = ""`라는 원인 파악 어려운 메시지를 띄우므로, 제출 자체를 앞단에서 막아 preflight 다이얼로그가 뜨지 않게 했다(코드 주석에도 명시). MCP 도구 경로는 창 UI를 거치지 않으므로 preflight가 최종 방어선으로 남는다.
  - 검증 상태: JSON 5종 파싱 검증 통과, image 변수 3개 기본값 `''` 확인. **Unity 에디터 확인 필요.**
  - 관련 파일: `Server~/variables.json`(36,45,46), `Server~/workflows/UI.json`(130), `Server~/workflows/StyleChange.json`(141,166), `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(795-813, 962-986, 1032-1050)
- [x] **[D6] 포트 중복 바인딩 차단**
  - 구현 결과: `BridgeHTTPServer(ThreadingHTTPServer)` 서브클래스에 `allow_reuse_address = False` 명시 — Windows에서 SO_REUSEADDR가 "이미 리슨 중인 포트에도 바인딩 허용"으로 동작하던 문제 차단. `create_server()`가 바인딩 `OSError` 시 한국어 3단계 안내(다른 프로젝트 브리지 종료 / 설정에서 포트 변경 / `netstat -ano | findstr :<포트>`) 후 `sys.exit(1)`. **로그 리다이렉션 이후에 바인딩**하므로 안내문이 런처의 기존 조기 종료 감지 메시지에 로그 꼬리로 그대로 실린다.
  - 검증 상태: **실측 확인** — 같은 포트 두 번째 인스턴스가 exit 1로 **0.19초 만에 종료**, stderr에 `[WinError 10048]` + 한국어 안내, 첫 서버는 정상 유지. `--log-file` 사용 시 안내문이 로그에 기록됨도 확인. **Unity 확인 필요**(§5.3).
  - 관련 파일: `Server~/bridge_server.py`(723-755)
- [x] **[D7] 브리지 신원 노출**
  - 구현 결과: 브리지에 `BRIDGE_VERSION = "0.1.0"`·`SCRIPT_PATH` 상수 추가, `/health`에 `scriptPath`·`version` 노출(기존 키 전부 유지 → `BridgeClient` 파싱 무영향), 시작 로그에도 출력. 런처에 `WarnIfForeignBridgeAsync()` 신설 — `[InitializeOnLoadMethod]` + `delayCall`로 `/health`를 1회 조회해 `scriptPath`가 자기 `GetScriptPath()`와 다르면 `Debug.LogWarning`("실행 중: X, 버전 v / 이 프로젝트: Y"). 경로 비교는 `Path.GetFullPath` + 구분자 정규화 + Windows 대소문자 무시.
    - 응답이 없으면 확인 완료로 표시하지 않아 나중에 브리지가 떠도 재확인되고, 경고 자체는 `SessionState`로 세션당 1회. 설정 에셋이 없으면 조회를 건너뛰어 도메인 로드 중 에셋을 새로 만들지 않는다.
  - 검증 상태: `/health` 신원 노출 실측 확인. **Unity 확인 필요** — 다른 경로의 브리지가 떠 있을 때 경고 1회, 같은 프로젝트면 무경고, 브리지 미기동 시 조용히 통과.
  - 관련 파일: `Server~/bridge_server.py`(40-46, 489-495, 787-789), `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(142-293)
- [x] **[D8] Job 타임아웃 설정화**
  - 구현 결과: `MCPToolSettings.jobTimeoutSeconds`(기본 600, 툴팁에 "저사양 GPU에서 타임아웃 시 증가 / 변경 후 브리지 재시작 필요") 신설, 설정 창에 IntField 노출(최소 1 클램프), 런처가 `--job-timeout`으로 전달, 브리지 argparse에서 `JOB_TIMEOUT_SEC`에 반영. README 반영은 U9에서.
  - 검증 상태: Python 문법 검증 통과, argparse 배선 마무리 진행 중. **Unity 확인 필요** — 설정 창에서 값 변경 → 브리지 재시작 후 적용.
  - 관련 파일: `Editor/Common/MCPToolSettings.cs`(72-76), `Editor/Common/MCPSettingsWindow.cs`(77-81, 105), `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(341), `Server~/bridge_server.py`
- [x] **[D9] ComfyUI 최소 버전 명시** — 범위 서술로 대체
  - 구현 결과: README 요구 사항의 ComfyUI 항목에 **버전** 하위 항목 신설 — 특정 커밋을 특정할 근거가 없으므로 "`ReferenceLatent`·`SaveAudioAdvanced` 코어 노드를 포함하는 최신 버전(2025년 이후 릴리스) 권장"으로 **근거와 함께 범위**로 기재하고, 버전이 낮으면 커스텀 노드 누락과 구분되지 않는 오류가 난다는 점 + preflight가 누락 노드를 알려준다는 점을 함께 서술. **없는 버전 번호를 지어내지 않았다.**
    - **preflight의 코어/커스텀 자동 구분은 구현하지 않음** — `compute_missing_nodes`는 이름 목록만 반환한다. 대신 README에서 "목록의 이름이 커스텀 노드 목록에 없으면 코어 노드일 가능성이 높다"는 사용자 판단 방법으로 대체. 코어 노드 목록을 브리지가 알 방법이 없어(ComfyUI가 구분해 주지 않음) 자동 구분은 과잉 설계로 판단.
  - 검증 상태: 문서 — 별도 검증 불필요.
  - 관련 파일: `Assets/MCPTools/README.md`
- [x] **[D18] 브리지 제어 버튼 동시 잠김** — **2026-07-25 실사용 중 발견 → 같은 날 수정**
  - 증상: Unity 재시작 후 또는 **다른 Unity 프로젝트**에서 브리지만 살아 있으면 [서버 시작](브리지 살아있음)·[서버 종료](SessionState PID 없음)가 **동시에 비활성**되어 창에서 손쓸 수 없다. 작업 관리자로 프로세스를 직접 죽여야 함. `showBridgeConsole=false`(기본)면 `Stop()`의 "콘솔 창에서 종료해주세요" 안내도 따를 수 없다. UPM 설치 검증(§5.5) 중 새 빈 프로젝트에서 재현되어 U1 항목 진행이 막혔다.
  - 구현 결과:
    - 브리지에 `POST /shutdown` 신설 — 응답을 먼저 보내고 별도 스레드에서 `server.shutdown()`(serve_forever 스레드에서 호출하면 교착). `--host`로 **로컬 외 바인딩한 경우 403으로 거부**(D11 연계). `BRIDGE_VERSION` 0.1.0 → 0.2.0.
    - [서버 종료]의 활성 조건은 **그대로 두고**(이 세션이 띄운 프로세스 트리 종료라는 의미를 유지) 별도 **[원격 종료]** 버튼을 신설 — 브리지가 살아 있는데 이 세션 것이 아닐 때만 노출. `/health`의 `scriptPath`를 확인 다이얼로그에 표시해 어느 설치본을 내리는지 알린다(D7 연계). 구버전 브리지(404)는 "콘솔 창을 닫아주세요" 안내로 분기.
    - 두 버튼이 동시에 비활성인 상태에 **원인·조치 HelpBox**를 추가 — 원격 종료 / 포트 변경 안내와 함께 "그 상태로도 생성은 정상 동작한다"를 명시.
  - 검증 상태: **브리지 실측 통과** — 8190/8191에서 `/shutdown` 200 후 서버 실제 종료 확인, `--host 0.0.0.0` 기동 시 403 거부 + 서버 생존 확인, 구버전(8189 기동본) `/shutdown` 404 확인(C# 폴백 분기 조건과 일치). `py_compile` 통과. **Unity 컴파일·UI 동작 확인 필요.**
  - 관련 파일: `Server~/bridge_server.py`(`handle_shutdown`, `BIND_HOST`), `Editor/ComfyUIGenerator/BridgeClient.cs`(`ShutdownAsync`, `BridgeHealth.scriptPath`), `Editor/ComfyUIGenerator/ComfyUIGeneratorWindow.cs`(`DrawServerSection`, `ShutdownForeignServerAsync`)
- [ ] **[D10] macOS/Linux 경로 정리** — **보류 (장비 필요)**
  - 판단: 유닉스에서 `showBridgeConsole`(`UseShellExecute=true`)이 실제로 어떻게 동작하는지, `python3` 후보의 실행 권한이 문제되는지는 **실기 없이 정적으로 단정할 수 없다.** 추측으로 분기를 넣으면 검증되지 않은 코드가 늘어날 뿐이므로, macOS/Linux 에디터 확보 후 §5.4 테스트를 거쳐 결정한다.
  - 현재 상태: 종료 경로는 이미 `UNITY_EDITOR_WIN` 분기가 있어(`taskkill` ↔ `Process.Kill`) 최소한 프로세스 정리는 양쪽 모두 동작할 것으로 보인다. **Windows 배포에는 영향 없음.**
  - 관련 파일: `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`, `Editor/Common/MCPSettingsWindow.cs`
- [x] **[D17] README 온보딩 보강**
  - 구현 결과: 최상단에 **"빠른 시작 (첫 실행 체크리스트)"** 10단계 번호 목록 신설(기존 설치본 삭제 → git URL 설치 → 컴파일 확인 → ComfyUI/Python 준비 → 설정 확인 → [서버 시작] → 기획서 넣기 → 1~4단계), 각 항목에 상세 절 앵커 링크. 공백 5개 전부 보강 — ① uGUI는 **선택 사항**으로 정정(D3 수정 결과 반영), ② ComfyUI 버전(D9), ③ 커스텀 노드 설치 방법(ComfyUI-Manager 링크 + 절차), ④ 기획서 파일 준비(`Assets/Docs`는 자동 생성되지만 파일은 사용자 몫), ⑤ git 2.14+ 요구.
  - 검증 상태: 문서 — 별도 검증 불필요.
  - 관련 파일: `Assets/MCPTools/README.md`

## 3. 구현 체크리스트 — 선택 (낮음)

- [x] **[D11] 브리지 호스트 처리** — `--host` 인자 추가로 해결
  - 구현 결과: 브리지에 `--host`(기본 `127.0.0.1`) 추가해 바인딩 주소로 사용. 런처가 `ExtractHost()`로 `bridgeServerUrl`의 호스트를 뽑아 전달(IPv6 리터럴 대괄호 제거). 호스트가 `127.0.0.1`/`localhost`/`::1`이 아니면 시작 로그에 "로컬 외 주소에 바인딩합니다 — 같은 네트워크의 다른 기기가 접속할 수 있습니다" 경고. `bridgeServerUrl` Tooltip에도 이 동작 명시(필드 변경 없음).
  - 검증 상태: **실측 확인** — 기본값 `127.0.0.1` 바인딩·무경고, `--host 0.0.0.0` 정상 기동 + 경고 출력. **Unity 확인 필요**(LAN IP 설정 시 연결).
  - 관련 파일: `Server~/bridge_server.py`(761-763, 784-794), `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(495, 502, 614-625), `Editor/Common/MCPToolSettings.cs`(69-71)
- [x] **[D12] 출력/문서 폴더 자동 생성**
  - 구현 결과: 신규 `Editor/Common/MCPToolFolders.cs`(internal static) — `EnsureWorkFolders(settings)`가 `docsRootPath`/`generatedRootPath`를 재귀 생성하고 **새로 만든 경우에만** 콘솔 로그 1회. `Assets` 밖 경로는 조용히 무시(읽기 전용 패키지 경로 방어). 두 창의 `OnEnable`에서 `GetOrCreate()` 직후·목록 갱신 앞에 호출.
    - `MCPToolSettings.EnsureFolder`가 private이고 두 창이 공유해야 해서, 중복 구현 대신 공용 헬퍼 1곳으로 뒀다.
  - 검증 상태: 정적 검증. **Unity 확인 필요** — `Assets/Docs`·`Assets/Generated` 삭제 후 창 열기.
  - 관련 파일: `Editor/Common/MCPToolFolders.cs`(신규), `Editor/AssetListup/AssetListupWindow.cs`(90), `Editor/PromptBuilder/PromptBuilderWindow.cs`(83)
- [x] **[D13] 신규 폴더 생성 시 Refresh 보강**
  - 구현 결과: `createdFolder` 플래그를 두어 `Directory.CreateDirectory`가 실제로 폴더를 만든 경우에만 `AssetDatabase.Refresh()`를 호출하고, 기존 폴더면 종전대로 `ImportAsset(outputPath)`만 호출(불필요한 전체 Refresh 회피).
  - 검증 상태: 정적 검증. **Unity 확인 필요** — 폴더 없는 상태 첫 저장 시 산출물이 프로젝트 창에 즉시 보이는지(원래 "확인 필요"였던 항목).
  - 관련 파일: `Editor/AssetListup/AssetListBuilder.cs`(201-224), `Editor/PromptBuilder/PromptBuilder.cs`(163-186)
- [x] **[D14] 설정 에셋 개인 값 커밋 방지**
  - 구현 결과: `.gitignore`에 `MCPToolSettings.asset`(+`.meta`)와 `Assets/MCPTools.User/`를 사유 주석과 함께 추가 — [Python 자동 탐지]가 기록하는 개인 PC 절대 경로가 저장소에 올라가지 않는다. U2로 설정 에셋 기본 생성 위치가 `Assets/MCPTools.User/`로 옮겨져 **패키지에 애초에 포함되지 않으므로** 패키징 시점 검증 단계가 불필요해졌다.
  - 검증 상태: `git check-ignore`로 설정 에셋 무시 확인 완료.
  - 관련 파일: `.gitignore`(47-57행)
- [x] **[D15] CLAUDE.md 배포 규칙 정정** — UPM(git URL) 방식으로 수정 완료
  - 구현 결과: "배포 고려사항" 절 전면 갱신 — 배포 채널을 UPM(git URL, `com.sungchan.mcptools`)으로 명시, `.unitypackage`·zip 배포 금지 문구, **읽기 전용 패키지 전제** 항목 신설(설치 폴더 쓰기 금지, `Assets/MCPTools.User/` 규약, `PackageInfo.FindForAssetPath` 경로 해석), `dependencies` 빈 객체 방침, 버전 3중 동기 규칙 추가.
  - 검증 상태: 문서 수정 — 별도 검증 불필요.
  - 관련 파일: `CLAUDE.md`(배포 고려사항 절)
- ~~**[D16] 재설치 안내**~~ — **폐기** (zip 배포 중단, Task 문서 §9.0). GUID/asmdef 중복 문제는 U5로 흡수

## 4. 구현 체크리스트 — GitHub + Package Manager(UPM) 배포 전환

> 배포 단위가 `Assets/` 하위 폴더 → **읽기 전용 패키지(`Packages/`·`Library/PackageCache/`)** 로 바뀌면서 새로 생기는 문제. Task 문서 §9 참조.
> **주의:** U1·U2를 고치기 전에는 UPM 설치본에서 **3단계(생성)와 설정 저장이 전혀 동작하지 않는다.**

### 4.1 즉시 조치 (치명 / 높음)

- [x] **[U1] 브리지 스크립트 절대 경로 해석 교체** — 설치 형태 3종(Assets / PackageCache / embed) 모두 지원
  - 구현 결과:
    - `GetScriptPath()` 교체 — ① `PackageInfo.FindForAssetPath(InstallRoot)`가 non-null이면 `resolvedPath` + `ScriptRelativePath` 조합(UPM/embed), ② null이면 기존 `Application.dataPath` 치환(Assets 설치). `Server~`는 항상 파일시스템 경로로 접근한다는 기존 취지 유지.
    - `BuildScriptNotFoundMessage()` 신설 — 실패 시 탐색한 절대 경로, 인식된 InstallRoot, 설치 형태 판정, 형태별 조치(Assets → `Server~` 동반 복사 확인 / UPM → Package Manager에서 Remove 후 재추가)를 예외 메시지에 포함.
    - 회귀 논증: Assets 설치에서는 `FindForAssetPath`가 null → 기존 분기 그대로 실행되어 결과 경로가 수정 전과 동일.
  - 검증 상태: 정적 검증 — 설치 형태 A/B/C별 경로 해석을 표로 논증 완료. **Unity 에디터 컴파일·회귀 확인 필요.** UPM 형태 실검증은 U4 태그 배포 후 §5.5에서.
  - 관련 파일: `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(47-93, 99-129, 270)
- [x] **[U2] 설정 에셋 저장 위치를 설치 루트에서 분리**
  - 구현 결과:
    - `AssetPath`를 InstallRoot 기반 계산에서 고정 경로 **`Assets/MCPTools.User/MCPToolSettings.asset`** 으로 교체 (읽기 전용 패키지에 생성 불가하므로 항상 프로젝트 쪽에 생성). 생성 시 위치를 `Debug.Log`로 1회 안내.
    - 기존 프로젝트의 `Assets/MCPTools/Editor/Common/MCPToolSettings.asset`도 Assets 범위 조회로 계속 발견되므로 **마이그레이션 불필요** (생성 분기 미진입).
    - 패키지 동봉 설정 복사는 불필요 판정 — 동봉 에셋 값이 코드 기본값과 동일하고, D14 조치로 설정 에셋 자체를 `.gitignore`로 커밋 제외해 **패키지에 동봉되지 않음**.
  - 검증 상태: 정적 검증 완료. Unity 확인 필요 — 설정 에셋 임시 삭제 후 `Tools/MCP/Settings` → `Assets/MCPTools.User/`에 생성 + 콘솔 로그 1회.
  - 관련 파일: `Editor/Common/MCPToolSettings.cs`(26, 57-60, 109-146)
- [x] **[U3] 설정 조회 범위 한정 + 루트 역산 순서 변경**
  - 구현 결과: `GetOrCreate`의 `FindAssets("t:MCPToolSettings", new[]{"Assets"})` 범위 한정(패키지 동봉 에셋이 사용자 설정을 가리지 않음). `ResolveInstallRoot` 순서 반전 — ① `MonoScript` 스크립트 경로 역산(1순위, Packages/ 경로도 그대로 반환됨) → ② 설정 에셋 역산(구버전 배치용 최후 폴백으로 강등) → ③ `"Assets/MCPTools"`. `RootFromCommonFolderPath`의 `/Editor/Common` suffix 검증 재사용.
  - 검증 상태: 정적 검증 완료 (개발 프로젝트 InstallRoot 결과 동일 논증). `MCPSettingsWindow.cs`는 `AssetPath` 직접 사용처가 없어 변경 불필요 확인.
  - 관련 파일: `Editor/Common/MCPToolSettings.cs`(109-190)
- [x] **[U4] `package.json` 신설 + 저장소 레이아웃 확정**
  - 구현 결과:
    - `package.json` 생성 — **`name: "com.sungchan.mcptools"`(사용자 확정)**, `version: "0.1.0"`(`MCPToolsInfo.Version`과 일치 확인), `displayName: "MCP Tools"`, `unity: "6000.5"`, `dependencies: {}`(U7), keywords. `documentationUrl`은 저장소 URL 확정 후 U14에서.
    - `CHANGELOG.md` 생성 (Keep a Changelog, `[0.1.0] - 미배포`, 버전 3중 동기 규칙 주석). `LICENSE.md`는 MIT로 사용자 확정 — 생성 진행 중.
    - **저장소 레이아웃 확정(사용자 결정): 루트 일원화(§9.4 A안)** — `C:\Project\CreateMCP` 루트를 저장소로 `git init -b main` 완료. 기존 중첩 저장소(`MCPToolTest/.git`, 원격 chomul/MCPToolTest)는 `.mcptooltest-git-backup/`으로 이동(원격에 초기 커밋 보존, 중첩 gitlink 문제 해소). 중첩 `.gitattributes`가 LFS 필터 템플릿이라 함께 걷어냄(UPM git 설치는 LFS 미지원 — 포인터 파일 사고 방지).
    - 설치 URL 형식: `https://github.com/<user>/<repo>.git?path=MCPToolTest/Assets/MCPTools#vX.Y.Z` — README 반영은 U9에서.
  - 검증 상태: JSON 파싱 검증 통과. `git status`로 무시 규칙 동작 확인(Library/dist/설정 에셋 무시, 추적 대상 9개 최상위 항목만). 첫 커밋은 나머지 항목 완료 후.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/package.json`, `MCPToolTest/Assets/MCPTools/CHANGELOG.md`, `.gitignore`, `.gitattributes`
- [x] **[U5] 중복 설치(zip 설치본 + 패키지) 감지·안내** — **README 안내로만 대응하는 것으로 결정 변경**
  - 구현 결과: 에디터 감지 코드는 **구현하지 않음** — 구 설치본과 패키지가 공존하면 asmdef 이름 중복으로 **컴파일 자체가 실패**해 감지 코드가 실행될 수 없음(정적 분석으로 확정). 실효 조치는 README 설치 절 최상단의 "기존 `Assets/` 하위 `MCPTools` 폴더 먼저 삭제" 경고뿐이며 U9(README 교체)에 포함.
  - 검증 상태: 설계 결정만 — U9 완료 시 README 문구 확인.
  - 관련 파일: `Assets/MCPTools/README.md`(U9에서)
- [x] **[U6] 사용자 확장 지점을 프로젝트 쪽으로 이전** — 사용자 폴더 규약 `Assets/MCPTools.User/`로 통일
  - 구현 결과:
    - **프롬프트 템플릿 2단 탐색** — `UserTemplatesFolder`(`Assets/MCPTools.User/Templates`) 신설. `ListTemplateNames()`는 패키지 폴더 + 사용자 폴더를 `SortedSet`으로 병합(이름 중복 시 1개, 정렬 유지), `LoadByName()`은 사용자 폴더 → 패키지 폴더 → 기본 템플릿 순 폴백.
    - **브리지 사용자 오버라이드** — `--user-dir` 인자 신설. `<user-dir>/workflows/<이름>.json`이 있으면 우선 로드, 목록은 두 폴더 합집합(사용자 우선). `<user-dir>/variables.json`이 있으면 **통째로 대체**(병합하지 않음 — 예측 가능성 우선). 폴더 미존재 시 조용히 기본값 사용. `ComfyUIServerLauncher`가 `UserComfyDirAssetPath`(`Assets/MCPTools.User/ComfyUI`)의 절대 경로를 항상 전달.
    - **[워크플로를 프로젝트로 복사] 버튼** — 설정 창에 추가. 패키지 `Server~/workflows/*.json` + `variables.json`을 사용자 폴더로 복사(기존 복사본 있으면 덮어쓰기 확인 다이얼로그), 완료 후 `AssetDatabase.Refresh()` + 브리지 재시작 안내. 원본 경로는 U1의 설치 형태 해석을 재사용해 UPM 설치에서도 동작.
  - 검증 상태: Python 문법 검증 통과. `--user-dir` argparse 배선 마무리 진행 중. **Unity 에디터 확인 필요** — 복사 버튼 동작, 복사본 우선 적용, 사용자 템플릿 드롭다운 노출.
  - 관련 파일: `Editor/PromptBuilder/PromptTemplate.cs`(13-34, 68-81, 108-130), `Server~/bridge_server.py`(44-48, 64-120), `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(30-34, 297-341), `Editor/Common/MCPSettingsWindow.cs`(128-140, 195-260)

### 4.2 후속 (중간)

- [x] **[U7] 선택 의존 패키지 선언 방침** — `dependencies` 빈 객체로 확정
  - 구현 결과: `package.json`의 `dependencies: {}` 확정. 선택 의존(uGUI/2D Sprite/unity-mcp)은 기존 `versionDefines`/`defineConstraints` asmdef 격리 유지, `description`에 "optional" 명시. README 반영은 U9에서.
  - 검증 상태: `package.json` 내용 확인 완료.
  - 관련 파일: `MCPToolTest/Assets/MCPTools/package.json`
- [x] **[U8] CHANGELOG·LICENSE 추가** (릴리스 절차 문서화는 아래 잔여)
  - 구현 결과: `CHANGELOG.md` 생성(Keep a Changelog 한국어, `[0.1.0] - 미배포`, 상단에 "package.json·MCPToolsInfo.Version·git 태그 vX.Y.Z를 항상 함께 올린다" 주석). **MIT LICENSE**(사용자 확정) — `Assets/MCPTools/LICENSE.md`와 저장소 루트 `LICENSE` 두 곳에 생성(Copyright (c) 2026 sungchan). 버전 3중 동기 규칙은 `CLAUDE.md` 배포 고려사항에도 반영(D15).
  - 릴리스 절차 문서 `docs/릴리스절차.md` 작성 완료 — 배포 정보 표, **버전 3중 동기**(package.json / MCPToolsInfo.Version / git 태그), 릴리스 순서, **커밋 전 점검 목록**(`.meta` 누락 검사 명령, 개인 값 미포함, `Server~` 포함 여부, LFS 미사용, 저장소 크기, package.json 유효성), 사용자 업데이트 방법.
  - 검증 상태: 파일 생성 확인. 점검 명령은 첫 커밋 시 실제 실행(U13).
  - 관련 파일: `Assets/MCPTools/CHANGELOG.md`, `Assets/MCPTools/LICENSE.md`, `LICENSE`, `CLAUDE.md`, `docs/릴리스절차.md`
- [x] **[U9] README 설치 절 교체**
  - 구현 결과: zip 설치 절차와 ".unitypackage 금지" 경고 블록을 전부 삭제하고 git URL 단일 경로로 교체 — ① Package Manager `Add package from git URL` 절차(Unity 6 메뉴 경로), git 2.14+ 필요·미설치 시 오류 메시지·조치, ② 버전 고정(`#v0.1.0`)과 업데이트 방법(`manifest.json` 태그 교체 또는 제거 후 재추가), ③ 로컬 embed/폴더 복사 언급, ④ 첫 실행 시 생성되는 것(설정 에셋 새 위치·작업 폴더 자동 생성). **U5 경고**(기존 `Assets/MCPTools` 삭제, 단 `Assets/MCPTools.User/`는 보존)를 설치 절 최상단 인용 블록으로 배치.
    - 사설 저장소 인증(PAT/SSH) 안내는 **공개 저장소로 확정**되어 생략.
    - "사용자 확장" 절 신설(U6·U2), 설정 항목 표에 `jobTimeoutSeconds`·설정 창 버튼 3종 추가, 문제 해결에 git 미설치·어셈블리 중복·패키지 업데이트 시 수정 소실·preflight 실패·[생성] 비활성 항목 추가, 하단에 버전·라이선스·CHANGELOG 링크.
  - 검증 상태: 서술 내용을 전부 소스에서 대조 확인(설정 경로·버튼 레이블·실행 인자·변수 판정 로직). **확인 필요** — 설치 URL은 저장소 푸시 후 실제 동작 확인.
  - 관련 파일: `Assets/MCPTools/README.md`
- [x] **[U10] 바이트코드 생성 차단**
  - 구현 결과: 브리지 기동 인자를 `python -B "<script>" …` 형태로 변경(서버 기동 인자에만 적용, Python 자동 탐지 검증 실행부는 미변경). `.gitignore`에 `__pycache__/`·`*.py[cod]` 추가 완료(U12).
  - 검증 상태: 정적 검증 — Unity 확인 시 [서버 시작] 후 `Server~` 폴더에 `__pycache__`가 생기지 않는지 함께 확인.
  - 관련 파일: `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(338-339), `.gitignore`
- [x] **[U11] 긴 경로(MAX_PATH) 대응**
  - 구현 결과:
    - 판정 규칙: `abspath` 길이 **≥ 250자** 또는 `errno == ENAMETOOLONG`. 둘 다 아니면 빈 문자열을 반환해 **기존 메시지가 그대로 유지**된다.
    - 브리지 — `path_hint()`/`hinted_error()`/`read_json_file()` 공통 헬퍼 신설. `hinted_error`는 **원본 예외 종류를 보존**하며 메시지만 덧붙여 `FileNotFoundError` → HTTP 404 매핑이 깨지지 않는다. `load_variables_manifest`·`list_workflow_names`·`load_workflow`에 적용.
      - `load_workflow`의 "찾을 수 없습니다"는 `os.path.isfile` 기반이라 OSError가 아닌데, **Windows는 MAX_PATH 초과 경로를 오류 대신 "없음"으로 돌려주므로** 여기에도 길이 기반 힌트를 붙여 일반적인 파일 없음과 구분되게 했다.
    - 런처 — `IsLongPath()`/`BuildLongPathNote()` 헬퍼. `BuildScriptNotFoundMessage`의 조치 3번 항목과 `Process.Start` 실패 catch에 적용(짧은 경로면 기존 문구와 바이트 단위로 동일). 안내에 짧은 경로 이동 + `LongPathsEnabled=1` 레지스트리 + PackageCache 해시 경로 설명 포함.
  - 검증 상태: **스모크 통과** — 단위 13/13(긴 경로 379자 힌트 붙음 / 짧은 경로 130자 안 붙음, `ENAMETOOLONG` vs `ENOENT` 구분, 기존 메시지 문자열 완전 일치, 예외 타입 보존), HTTP 스모크(`/preflight` 404 + 본문 키 불변, `/workflows` 5개, `/health` 키 집합 불변). C# 구문 오류 0. **Unity 컴파일 확인 필요.**
  - 관련 파일: `Server~/bridge_server.py`(29, 66-125, 140-198), `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`(49-55, 147-171, 560-567)
- [x] **[U12] 저장소 위생 파일 추가 + git 저장소 초기화**
  - 구현 결과:
    - `.gitignore` 신설 — Unity 표준 목록(`MCPToolTest/` 접두 스코프, `*.slnx`·`.vscode/` 등 실존 파일 반영), `dist/`, `__pycache__/`·`*.py[cod]`, `MCPToolSettings.asset`(+.meta, D14 사유 주석), `MCPTools.User/`(+.meta), `Assets/Generated/`(Generated.meta는 GUID 고정 위해 유지), `.claude/settings.local.json`, `.mcptooltest-git-backup/`. 상단에 "*.meta 절대 무시 금지"(U13) 주석.
    - `.gitattributes` 신설 — `* text=auto`, Unity YAML·코드 `text eol=lf`, `*.ps1` CRLF, 이미지/오디오/아카이브 binary. **패키지 폴더에 LFS 금지 경고 주석**(UPM git 설치는 LFS 미지원).
    - 루트 `git init -b main` 완료. 중첩 저장소(`MCPToolTest/.git` 235K + 자체 gitignore/gitattributes)는 `.mcptooltest-git-backup/`으로 이동 — 방치 시 MCPToolTest 전체가 gitlink로 취급되어 커밋에서 빠지는 문제 해소 (사용자 결정: 루트 일원화).
  - 검증 상태: `git check-ignore`로 Library/dist/설정 에셋 무시 확인, `git status` 최상위 9개 항목만 추적 대상.
  - 관련 파일: `.gitignore`, `.gitattributes`, `.mcptooltest-git-backup/`

### 4.3 선택 (낮음)

- [ ] **[U13] `.meta` 커밋 검증** — 첫 커밋 직전에 수행
  - 구현 결과: `.gitignore`에 "*.meta 절대 무시 금지" 주석 명시 완료, `docs/릴리스절차.md`에 누락 검사 명령 포함.
  - 검증 상태: **선행 조건 발견 — 아직 미충족.** 이번 작업으로 새로 만든 4개 파일에 `.meta`가 없다(Unity가 생성하는 것이 원칙이라 에디터를 열기 전에는 생성되지 않음): `package.json`, `CHANGELOG.md`, `LICENSE.md`, `Editor/Common/MCPToolFolders.cs`.
    → **사용자가 Unity 에디터를 한 번 연 뒤에 첫 커밋을 해야 한다.** 그 전에 커밋하면 사용자 프로젝트에서 GUID가 새로 생성되어 참조가 깨진다. 릴리스 절차 문서 상단에 경고로 명시함.
    - `Server~/` 5종(`bridge_server.py`·`variables.json`·`workflows/*.json` 4개)은 `.meta`가 없는 것이 정상이며 git에는 커밋되어야 함을 확인.
  - 관련 파일: `.gitignore`, `docs/릴리스절차.md`
- [x] **[U14] Package Manager UI 문서 노출**
  - 구현 결과: `package.json`에 `documentationUrl`(README) / `changelogUrl` / `licensesUrl`을 GitHub 링크로 추가 — Package Manager 상세 화면의 문서·변경 로그·라이선스 링크가 활성화된다.
    - **`Samples~/`는 만들지 않기로 결정** — U6에서 설정 창의 [워크플로를 프로젝트로 복사] 버튼으로 같은 목적(패키지 기본값을 프로젝트로 꺼내 편집)을 이미 달성했고, 도구 안에서 경로·재시작 안내까지 함께 해주므로 Package Manager의 [Import] 경로를 추가하면 진입점만 둘로 갈린다.
    - `Documentation~/index.md`도 생략 — `documentationUrl`이 README를 직접 가리키므로 같은 내용을 두 곳에서 관리할 이유가 없다.
  - 검증 상태: `package.json` JSON 파싱 검증 통과. **확인 필요** — URL은 저장소 푸시 후에야 실제로 열린다(브랜치명 `main` 기준).
  - 관련 파일: `MCPToolTest/Assets/MCPTools/package.json`

## 5. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

### 5.1 새 빈 Unity 6 프로젝트

- [ ] `MCPTools/` 폴더를 새 Unity 6 프로젝트의 `Assets/`에 복사해 넣었을 때 컴파일 오류 0건이고 `Tools/MCP/*` 메뉴 8개가 모두 보임 (배포 본선은 §5.5 UPM 설치 — 이 항목은 `Assets/` 설치 형태 회귀 확인용)
- [ ] **unity-mcp 패키지가 없는** 프로젝트에서 컴파일 오류 0건 + 에디터 창 4개 정상 동작 (Task 5에서 미검증으로 남은 항목 — 회귀 확인)
- [ ] **`com.unity.2d.sprite`가 없는** 프로젝트에서 컴파일 오류 0건이고 스프라이트 슬라이싱만 설치 안내 다이얼로그 (Task 5에서 미검증으로 남은 항목)
- [ ] **`com.unity.ugui`를 제거한** 프로젝트에서 컴파일 오류 0건이고 `Tools/MCP/*` 창 4개가 모두 열림. 1단계 [프로젝트 스캔]도 오류 없이 끝나며 `SpriteRenderer`/`AudioSource` 슬롯은 정상 수집됨 (D3 수정 검증)

#### D3 — uGUI 제거 후에도 기존 동작이 유지되는지 (표준 환경, uGUI 있는 프로젝트)

- [ ] 1단계 [프로젝트 스캔] 결과의 **항목 수·순서·종류(`Image`/`RawImage`/`SpriteRenderer`/`AudioSource`)가 수정 전과 동일**한지 확인 (수정 전 저장한 `AssetList_*.json`과 비교하면 가장 확실함)
- [ ] `Image`에 Sprite가, `RawImage`에 Texture가 할당된 프리팹을 스캔했을 때 **"현재 에셋" 이름이 수정 전과 동일하게 표시**되는지 확인 (미할당 슬롯은 빈 값)
- [ ] 씬에 직접 배치한 `Image`와, 씬에 놓인 **프리팹 인스턴스** 안의 `Image`를 함께 두고 스캔 → 프리팹 인스턴스 쪽은 씬 슬롯으로 중복 수집되지 않고 원본 프리팹 슬롯으로만 나오는지 확인
- [ ] (선택) `class MyImage : Image` 같은 **사용자 정의 파생 컴포넌트**를 프리팹에 붙이고 스캔 → `componentType`이 `Image`로 수집되는지 확인 (파생 클래스 누락 방지 로직 검증)
- [ ] 한 GameObject에 `Image`와 `AudioSource`를 함께 붙인 뒤 스캔 → 두 슬롯이 각각 1개씩 나오는지 확인

#### D4 — 설치 위치 독립 검증

- [ ] `Assets/Plugins/MCPTools/`처럼 **다른 위치에 설치**해도 [서버 시작]이 동작하고, `Assets/MCPTools/` 폴더와 두 번째 설정 에셋이 새로 생기지 않음 (D4)
- [ ] 위치를 옮긴 뒤 `Tools/MCP/Settings`를 열었을 때 **동봉된 설정 에셋의 값**(ComfyUI 주소 등)이 그대로 보이는지 확인 (빈 기본값으로 재생성되면 실패)
- [ ] 위치를 옮긴 뒤 2단계 창의 **프롬프트 템플릿 드롭다운**에 `Templates/*.json`으로 추가한 템플릿이 나타나는지 확인
- [ ] `Server~` 폴더만 일부러 지운 뒤 [서버 시작] → 오류 메시지에 **탐색한 실제 경로와 인식된 설치 루트**가 표시되고 `Server~` 동반 복사 확인 안내가 뜨는지 확인
- [ ] 표준 위치(`Assets/MCPTools/`) 프로젝트에서 [서버 시작]/[서버 종료]·설정 로드·템플릿 목록이 **수정 전과 동일하게 동작**하는지 회귀 확인
- [ ] (선택) 설정 에셋을 복제해 2개로 만든 뒤 창을 열면 콘솔에 **중복 경로 경고**가 1회 뜨고 도구는 정상 동작하는지 확인
- [x] `Assets/Docs`가 없는 상태로 1단계 창을 열었을 때 폴더가 자동 생성되거나 [폴더 생성] 안내가 뜸 (D12)
  - 2026-07-25 에디터 실측 통과 — `Assets/Docs`를 다른 이름으로 바꾼 뒤 `Tools/MCP/1. Asset Listup`을 열어 폴더가 재생성되고 콘솔 안내가 1회만 출력됨을 확인. 확인 후 폴더명 복구.
- [ ] 1단계 저장 직후 `Assets/Docs/AssetList_*.json`이 프로젝트 창에 **즉시** 보임 (D13 실동작 확인)

### 5.2 ComfyUI 환경 차이

- [ ] 워크플로 기본 모델이 하나도 설치되지 않은 ComfyUI에 연결했을 때, 생성 전에 **누락 모델 목록 안내**가 뜨고 ComfyUI 400 원문만 보이는 상황이 발생하지 않음 (D1)
- [ ] `ComfyUI-Inspyrenet-Rembg` / `ComfySwitchNode`를 제거한 ComfyUI에서 워크플로를 선택하면 **누락 커스텀 노드 경고 + 설치 안내**가 표시됨 (D2)
- [x] `StyleChange`/`UI`에서 참조 이미지를 지정하지 않고 [생성] 시 차단 메시지가 뜸 (D5)
  - 2026-07-25 에디터 실측 통과 — 워크플로를 `UI`/`StyleChange`로 바꾸면 "참조 이미지를 선택해주세요: …" 경고와 함께 [생성] 버튼이 비활성화되고, `GenerateImage`로 되돌리면 다시 활성화됨.
- [x] **정상 환경 생성 회귀** — 모델·커스텀 노드가 모두 갖춰진 ComfyUI에서 `GenerateImage`로 후보 4개 생성 시 preflight가 **오탐으로 막지 않음** (D1/D2 회귀)
  - 2026-07-25 에디터 실측 통과 — "생성 사전 검증 실패" 다이얼로그 없이 평소대로 후보 4개 생성 완료.
- [ ] ComfyUI를 **원격 PC/다른 포트**에 두고 `comfyUIServerUrl`만 바꿨을 때 생성·업로드·결과 다운로드가 모두 정상

### 5.3 프로세스 / 포트

- [ ] 브리지가 이미 8189에서 실행 중인 상태로 **다른 Unity 프로젝트**에서 [서버 시작]을 눌렀을 때, 중복 바인딩 없이 원인이 명확한 안내가 표시됨 (D6/D7)
- [ ] 8189를 다른 프로그램이 점유한 상태에서 [서버 시작] → 조기 종료 감지 메시지(종료 코드 + 로그 꼬리) 표시 (**선행 조치 회귀 확인**)
- [ ] Python이 설치되지 않은 PC에서 [서버 시작] → 5단계 설치 안내 다이얼로그 표시 (**선행 조치 회귀 확인**)
- [ ] 설정 창의 [Python 자동 탐지]가 경로와 버전을 찾아 채움 (**선행 조치 회귀 확인**)
- [ ] 후보 생성이 600초를 넘는 저사양 조건에서 타임아웃 값을 늘려 성공 (D8)
- [ ] 브리지가 실행 중인 상태로 **다른 Unity 프로젝트**의 3단계 창을 열면 [서버 시작]·[서버 종료]가 비활성이면서 **원인 안내 HelpBox + [원격 종료] 버튼**이 보임 (D18)
- [ ] [원격 종료] → 확인 다이얼로그에 **대상 서버의 `bridge_server.py` 절대 경로**가 표시되고, 종료 후 상태 점이 "미기동"으로 바뀌며 [서버 시작]이 다시 활성화됨 (D18)
- [ ] Unity를 재시작해 SessionState PID를 잃은 뒤에도 [원격 종료]로 자기 프로젝트가 띄운 브리지를 내릴 수 있음 (D18)

### 5.4 경로 / 플랫폼

- [ ] 사용자명에 **한글 또는 공백**이 포함된 Windows 계정에서 [서버 시작]이 정상 동작하고 로그 파일이 정상 기록됨
- [ ] macOS 또는 Linux 에디터에서 [서버 시작]/[서버 종료]가 동작함 (D10)
- [ ] 제3자가 **README만 보고** git URL 설치 → ComfyUI 준비 → 첫 생성까지 도달 가능한지 리뷰 (D17/U9)

### 5.5 GitHub + Package Manager(UPM) 설치

> §4의 U1~U6는 구현 완료. 태그를 붙인 커밋(`v0.1.0`)을 `github.com/chomul/MCPTools`에 푸시한 뒤 진행한다.
> 설치 URL: `https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.1.0`

- [x] 빈 Unity 6 프로젝트에서 `Window > Package Manager > Add package from git URL`에 `https://github.com/chomul/MCPTools.git?path=MCPToolTest/Assets/MCPTools#v0.1.0` 입력 → **컴파일 오류 0**, `Tools/MCP/*` 메뉴 8개 노출 (U4)
  - 2026-07-25 통과. uGUI·2D Sprite·unity-mcp가 없는 빈 프로젝트에서도 컴파일 오류 0 — §5.1의 선택 의존 항목도 함께 검증됨.
- [x] `Tools/MCP/Settings`를 열면 설정 에셋이 **`Assets/` 아래에** 생성되고, 값을 바꿔 저장한 뒤 에디터를 재시작해도 유지됨. **패키지 폴더에는 아무 파일도 생기지 않음** (U2/U3)
  - 2026-07-25 통과.
- [ ] 3단계 창에서 [서버 시작] → 브리지가 **PackageCache 안의 `Server~/bridge_server.py`** 로 정상 기동 (U1). 실패 시 오류 메시지에 **해석된 절대 경로**가 표시되는지 확인
  - 2026-07-25 **미검증** — 아래 D18로 막혀 진행하지 못함. D18 수정본으로 재시도 필요.
- [ ] 패키지를 `Packages/<name>/`으로 **embed**한 상태에서 위 두 항목을 재확인 (설치 형태 2종 모두 동작) (U1)
- [ ] Package Manager에서 패키지를 **제거 후 재설치**(재해결) → 사용자 설정·사용자 템플릿·프로젝트 쪽 워크플로 사본이 **전부 살아 있음** (U2/U6)
- [ ] 기존 `Assets/MCPTools/` zip 설치본이 있는 프로젝트에 패키지를 추가 → **중복 설치 경고**가 뜨고, 안내대로 구 폴더를 지우면 컴파일 오류 0 (U5) — 경고가 없으면 `Assembly with name 'MCPTools.Editor' already exists` 오류가 나는지 확인
- [ ] 2단계 창에서 **사용자 템플릿 폴더**에 `.json`을 추가 → 드롭다운에 나타나고, 패키지 기본 템플릿과 이름이 같으면 사용자 것이 우선 적용 (U6)
- [ ] 3단계에서 워크플로를 프로젝트로 복사한 뒤 모델 파일명을 수정 → 브리지가 **수정본을 사용**하고, 패키지 업데이트 후에도 수정이 유지됨 (U6)
- [ ] git이 설치되지 않은 PC에서 git URL 설치 시도 → 실패 원인과 조치(git 설치 → Unity 재시작)를 README 안내로 파악 가능 (U9)
- [ ] `C:\Users\<한글이름>\Documents\Unity Projects\<긴 폴더명>\` 같은 **깊은 경로** 프로젝트에서 설치 + [서버 시작] 정상 동작 (U11)
- [ ] `__pycache__`가 패키지 폴더(embed 설치 기준)에 **생성되지 않음** (U10)
- [ ] 태그 `v0.1.0`·`v0.1.1`을 각각 설치 → Package Manager UI에 **버전이 정확히 표시**되고 전환이 동작 (U8)
- [ ] 패키지 상세 화면에 **문서 링크가 노출**되고 클릭 시 사용법에 도달 (U14)

## 6. 추가 요청 (2026-07-25, UPM 검증 중 발생)

- [x] **에디터 종료 시 브리지 자동 정리** — D18의 재발 방지책. 브리지를 띄운 채 Unity를 끄면 다음 실행/다른 프로젝트에서 포트만 잡힌 통제 불능 상태가 되므로, 종료 시점에 정리한다.
  - 구현 결과: `ComfyUIServerLauncher`의 기존 `[InitializeOnLoadMethod]`에서 `EditorApplication.quitting`을 구독(중복 구독 방지를 위해 `-=` 후 `+=`)하고, `OnEditorQuitting`이 **이 세션이 시작한 PID가 살아 있을 때만** `Stop()`을 호출한다. 외부 실행 서버·다른 프로젝트 서버는 PID를 모르므로 건드리지 않는다. 설정 `shutdownBridgeOnEditorQuit`(기본 true)로 끌 수 있고 `Tools/MCP/Settings`에 [종료 시 브리지 정리] 토글로 노출. 설정 로드 실패 시에도 종료 흐름을 막지 않고 기본 동작(정리)을 따른다.
  - 클래스 단위 `[InitializeOnLoad]` + 정적 생성자를 새로 두지 않고 **기존 `[InitializeOnLoadMethod]`에 합쳤다** (진입점 중복 방지).
  - 관련 파일: `Editor/ComfyUIGenerator/ComfyUIServerLauncher.cs`, `Editor/Common/MCPToolSettings.cs`, `Editor/Common/MCPSettingsWindow.cs`
- [x] **단계별 산출물 하위 폴더 분리** — `Assets/Docs`에 기획서와 3종 생성 JSON이 평평하게 쌓여(SpriteSheetPrompt만 17개) 단계 구분이 되지 않던 문제.
  - 확정 구조: `Docs/1_AssetList` · `Docs/2_PromptSet` · `Docs/SpriteSheetPrompt`, `Generated/3_Candidates/{항목id}` · `Generated/3_Confirmed/{Images,Audio,SpriteSheets,GenerationResults.json}`. 기획서 `.md`/`.txt`는 Docs 루트 유지.
  - 스프라이트 시트: `Docs/SpriteSheetPrompt/`에는 **프롬프트 JSON**(`answers`+`prompt`, 이미지 아님)이, 슬라이스한 **시트 PNG**는 `3_Confirmed/SpriteSheets/`에 저장된다. 시트는 4단계 적용 대상이 아니라 슬라이스 입력이므로 항목별 확정본(`Images/`)과 분리했다.
  - 경로 결정을 `MCPToolFolders`로 일원화(폴더명 상수 + `AssetListDir`/`PromptSetDir`/`SpriteSheetDir`/`CandidatesRoot`/`ConfirmedRoot` + `FindDocuments`/`ResolveForRead`). 각 창·도구가 직접 문자열을 조립하던 곳을 전부 이 헬퍼로 교체.
  - **하위 호환(파일 이동 없음)**: 읽기는 `FindDocuments`가 새 하위 폴더와 구 위치(루트)를 함께 훑고 파일명이 겹치면 새 위치를 채택. 확정본 규칙 경로도 `3_Confirmed` → 구 위치 순으로 탐색. 후보 폴더는 새 위치에 없고 구 위치에만 있으면 구 위치를 반환. `GenerationResults.json`은 새 위치에 없으면 구 위치 내용을 이어받아 새 위치에 저장. 쓰기는 항상 새 위치.
  - `EnsureWorkFolders`가 단계별 하위 폴더까지 생성. `SpriteSheetPromptBuilder`도 새 폴더 첫 생성 시 `ImportAsset` 대신 `Refresh`하도록 보강(D13과 같은 이유).
  - 관련 파일: `Editor/Common/MCPToolFolders.cs`, `AssetListup/AssetListBuilder.cs`, `PromptBuilder/PromptBuilder.cs`, `SpriteSheet/SpriteSheetPromptBuilder.cs`, `SpriteSheet/SpriteSheetImporter.cs`, `ComfyUIGenerator/CandidateGenerator.cs`, `AssetApplier/AssetApplier.cs`, 각 창 4종, `Pipeline/PipelineWindow.cs`, `Pipeline/PipelineTool.cs`, MCP 도구 설명(`McpForUnityAdapter.cs` 외)
- 검증 상태: 정적 검증(괄호/중괄호 델타가 HEAD와 동일, 헬퍼 심볼 존재, 잔여 하드코딩 경로 0건). **Unity 컴파일·동작 확인 필요.**

### 에디터 테스트 (추가 요청분)

- [ ] 브리지 [서버 시작] 후 Unity를 정상 종료 → 작업 관리자/`netstat`에서 python 브리지 프로세스가 사라짐. 다시 Unity를 켜면 [서버 시작]이 활성 상태
- [ ] `Tools/MCP/Settings`에서 [종료 시 브리지 정리]를 끄고 Unity 종료 → 브리지가 계속 살아 있음 (D18 안내 + [원격 종료]로 정리 가능)
- [ ] 다른 프로젝트가 띄운 브리지가 있는 상태로 Unity를 종료 → 그 브리지는 종료되지 않음 (내 세션 PID만 정리)
- [ ] 1단계 [저장] → `Assets/Docs/1_AssetList/`에 생성됨. 2단계 → `2_PromptSet/`, 스프라이트 시트 프롬프트 → `SpriteSheetPrompt/`
- [ ] 3단계 생성 → `Assets/Generated/3_Candidates/{항목id}/`, [확정] → `3_Confirmed/Images`(오디오는 `Audio`) + `3_Confirmed/GenerationResults.json`
- [ ] 스프라이트 시트 임포트 → 시트 PNG가 `3_Confirmed/SpriteSheets/{name}_sheet.png`에 저장되고 슬라이스(`walk_01`~)가 정상 적용됨. `Images/`에는 생기지 않음
- [ ] **구 위치 호환**: `Assets/Docs` 루트에 남아 있는 기존 `AssetList_*.json`/`PromptSet_*.json`이 1·2·4단계 드롭다운에 **그대로 보임**. 구 위치 파일을 불러와 [저장]하면 덮어쓰기가 동작
- [ ] **구 위치 확정본 호환**: 기존 `Assets/Generated/Images/{id}.png`만 있는 항목이 4단계에서 "확정본 없음"으로 뜨지 않고 정상 적용됨
- [ ] 기존 `Assets/Generated/GenerationResults.json`이 있는 상태에서 새로 [확정] → `3_Confirmed/GenerationResults.json`에 **기존 기록이 함께** 들어가고 이전 확정 항목의 배지가 유지됨
- [ ] `Assets/Generated/Sprite/`(사용자가 만든 .anim/.controller)는 도구가 건드리지 않고 그대로 남음
- [ ] `mcptools_status` 호출 → `assetListCount`/`promptSetCount`/`imageCount`/`candidateFolderCount`가 구·신 위치를 합산한 값으로 나옴
