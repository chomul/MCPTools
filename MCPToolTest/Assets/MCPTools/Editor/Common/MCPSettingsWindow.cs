using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using MCPTools.Runtime;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// MCP Tools 설정 편집 + ComfyUI 서버 연결 테스트 창입니다. (메뉴: Tools/MCP/Settings)
    /// </summary>
    public class MCPSettingsWindow : EditorWindow
    {
        private MCPToolSettings _settings;
        private bool _isTesting;
        private bool _isDetecting;
        private string _testStatusMessage = string.Empty;
        private string _detectStatusMessage = string.Empty;

        /// <summary>ComfyUI가 여러 개 감지됐을 때 다음 OnGUI 패스에서 선택 메뉴를 띄우기 위한 대기 목록입니다.</summary>
        private int[] _pendingDetectedPorts;

        private Vector2 _scroll;

        /// <summary>설정 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/Settings", false, 100)]
        public static void Open()
        {
            var window = GetWindow<MCPSettingsWindow>();
            window.titleContent = new GUIContent("MCP Tools 설정");
            window.minSize = new Vector2(380f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();
        }

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField($"MCP Tools v{MCPToolsInfo.Version}", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawSettingsFields();
            EditorGUILayout.Space();
            DrawConnectionTest();
            EditorGUILayout.Space();
            DrawRegisteredTools();

            EditorGUILayout.EndScrollView();
        }

        private void DrawSettingsFields()
        {
            EditorGUILayout.LabelField("설정", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            string serverUrl = EditorGUILayout.TextField("ComfyUI 서버 주소", _settings.comfyUIServerUrl);
            string bridgeUrl = EditorGUILayout.TextField(
                new GUIContent("브리지 서버 주소", "Unity와 ComfyUI 사이의 로컬 중간 서버 주소입니다. (기본 http://127.0.0.1:8189)"),
                _settings.bridgeServerUrl);
            string python = EditorGUILayout.TextField(
                new GUIContent("Python 실행 파일",
                    "브리지 서버 실행에 사용할 Python 3 실행 파일입니다. " +
                    "비워두면 자동 탐지합니다. 자동 탐지에 실패하면 python.exe 절대 경로를 직접 지정하세요."),
                _settings.pythonExecutable);
            bool showConsole = EditorGUILayout.Toggle(
                new GUIContent("브리지 콘솔 창 표시",
                    "켜면 브리지 서버 시작 시 콘솔 창이 떠서 로그를 직접 볼 수 있습니다. " +
                    "끄면(기본) 창 없이 실행되고 로그는 임시 폴더의 mcptools_bridge_server.log에 기록됩니다."),
                _settings.showBridgeConsole);
            int timeout = EditorGUILayout.IntField("요청 타임아웃(초)", _settings.requestTimeoutSeconds);
            int jobTimeout = EditorGUILayout.IntField(
                new GUIContent("생성 Job 타임아웃(초)",
                    "후보 1건 생성의 최대 대기 시간(초)입니다. 저사양 GPU에서 타임아웃이 나면 늘려주세요. " +
                    "변경 후 브리지 서버 재시작이 필요합니다."),
                _settings.jobTimeoutSeconds);
            string workflow = EditorGUILayout.TextField(
                new GUIContent("기본 이미지 워크플로", "이미지 항목 기본 워크플로 이름입니다 (GenerateImage | GenerateImageFlux | StyleChange 등)."),
                _settings.defaultImageWorkflow);
            string generatedRoot = EditorGUILayout.TextField("Generated 경로", _settings.generatedRootPath);
            string docsRoot = EditorGUILayout.TextField("Docs 경로", _settings.docsRootPath);
            int candidateCount = EditorGUILayout.IntField("후보 개수", _settings.candidateCount);
            bool unloadAfterBatch = EditorGUILayout.Toggle(
                new GUIContent("일괄 생성 후 모델 언로드",
                    "일괄 생성이 끝나면 ComfyUI에 로드된 모델을 언로드해 VRAM/메모리를 확보합니다. " +
                    "기본값은 끔(모델 유지)이며, 켜면 다음 생성 때 모델을 다시 로드하느라 " +
                    "10~40초가 추가될 수 있습니다. VRAM이 부족할 때만 켜세요. (단건 생성에는 적용되지 않습니다.)"),
                _settings.unloadModelsAfterBatch);
            int ppu = EditorGUILayout.IntField(
                new GUIContent("Sprite PPU", "확정 시 Sprite 임포트에 적용할 Pixels Per Unit 값입니다."),
                _settings.spritePixelsPerUnit);
            bool shutdownOnQuit = EditorGUILayout.Toggle(
                new GUIContent("종료 시 브리지 정리",
                    "Unity를 종료할 때 이 도구로 시작한 브리지 서버도 함께 종료합니다. " +
                    "끄면 브리지가 계속 실행된 채 남아 다음 실행에서 재사용됩니다."),
                _settings.shutdownBridgeOnEditorQuit);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_settings, "MCP Tools 설정 변경");
                _settings.comfyUIServerUrl = serverUrl;
                _settings.bridgeServerUrl = bridgeUrl;
                _settings.pythonExecutable = python;
                _settings.showBridgeConsole = showConsole;
                _settings.requestTimeoutSeconds = Mathf.Max(1, timeout);
                _settings.jobTimeoutSeconds = Mathf.Max(1, jobTimeout);
                _settings.defaultImageWorkflow = workflow;
                _settings.generatedRootPath = generatedRoot;
                _settings.docsRootPath = docsRoot;
                _settings.candidateCount = Mathf.Max(1, candidateCount);
                _settings.unloadModelsAfterBatch = unloadAfterBatch;
                _settings.spritePixelsPerUnit = Mathf.Max(1, ppu);
                _settings.shutdownBridgeOnEditorQuit = shutdownOnQuit;
                EditorUtility.SetDirty(_settings);
            }

            // 버튼은 변경 감지 블록 밖에 둔다 (버튼 클릭도 GUI.changed를 켜서 위 필드 값이 덮어쓰이는 것을 방지).
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                using (new EditorGUI.DisabledScope(_isDetecting))
                {
                    if (GUILayout.Button(
                            new GUIContent(_isDetecting ? "감지 중..." : "ComfyUI 자동 감지",
                                "현재 PC에서 리스닝 중인 TCP 포트를 검사해 실행 중인 ComfyUI를 찾아 " +
                                "위 \"ComfyUI 서버 주소\"에 채웁니다."),
                            GUILayout.Width(160f)))
                    {
                        StartComfyUIDetection();
                    }
                }

                GUILayout.FlexibleSpace();
            }

            if (!string.IsNullOrEmpty(_detectStatusMessage))
            {
                EditorGUILayout.LabelField(_detectStatusMessage, EditorStyles.wordWrappedLabel);
            }

            // 감지 결과가 여러 개면 다음 OnGUI 패스에서 선택 메뉴를 띄운다
            // (async 완료 콜백에는 GUI 컨텍스트가 없어 GenericMenu를 바로 표시할 수 없음).
            if (_pendingDetectedPorts != null && Event.current.type == EventType.Layout)
            {
                int[] detectedPorts = _pendingDetectedPorts;
                _pendingDetectedPorts = null;
                ShowPortSelectionMenu(detectedPorts);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button(
                        new GUIContent("Python 자동 탐지",
                            "설치된 Python 3.7 이상을 찾아 위 \"Python 실행 파일\"에 절대 경로를 채웁니다."),
                        GUILayout.Width(160f)))
                {
                    DetectPythonExecutable();
                }

                GUILayout.FlexibleSpace();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button(
                        new GUIContent("워크플로를 프로젝트로 복사",
                            "패키지에 동봉된 워크플로 JSON과 variables.json을 " +
                            $"\"{ComfyUIServerLauncher.UserComfyDirAssetPath}\"로 복사합니다. " +
                            "복사본이 있으면 브리지 서버가 패키지 동봉본보다 우선 사용하므로, " +
                            "UPM(읽기 전용) 설치에서도 워크플로/변수를 자유롭게 수정할 수 있습니다."),
                        GUILayout.Width(180f)))
                {
                    CopyWorkflowsToProject();
                }

                GUILayout.FlexibleSpace();
            }

            if (GUILayout.Button("저장"))
            {
                AssetDatabase.SaveAssets();
                ShowNotification(new GUIContent("설정을 저장했습니다."));
            }
        }

        /// <summary>
        /// 설치된 Python 3.7 이상을 탐지해 설정의 Python 실행 파일에 절대 경로를 채웁니다.
        /// 실패하면 원인·조치 안내 다이얼로그를 표시합니다.
        /// </summary>
        private void DetectPythonExecutable()
        {
            // 이전 탐지 결과를 무시하고 현재 환경을 다시 검사한다.
            ComfyUIServerLauncher.ClearPythonCache();

            string argumentPrefix;
            string version;
            string diagnostics;
            string python = ComfyUIServerLauncher.ResolvePython(_settings, out argumentPrefix, out version, out diagnostics);

            if (string.IsNullOrEmpty(python))
            {
                EditorUtility.DisplayDialog(
                    "Python 자동 탐지 실패",
                    ComfyUIServerLauncher.BuildPythonNotFoundMessage(diagnostics),
                    "확인");
                return;
            }

            Undo.RecordObject(_settings, "MCP Tools Python 실행 파일 자동 탐지");
            _settings.pythonExecutable = python;
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssets();

            // 설정값이 바뀌었으므로 다음 서버 시작 시 새 값 기준으로 다시 검증하게 한다.
            ComfyUIServerLauncher.ClearPythonCache();

            // 편집 중이던 텍스트 필드가 옛 값을 유지하지 않도록 포커스를 해제한다.
            GUI.FocusControl(null);
            Repaint();

            string extra = string.IsNullOrEmpty(argumentPrefix)
                ? string.Empty
                : $"\n실행 인자 접두사: {argumentPrefix}";
            EditorUtility.DisplayDialog(
                "Python 자동 탐지",
                $"Python을 찾아 설정에 저장했습니다.\n\n경로: {python}\n버전: Python {version}{extra}",
                "확인");
        }

        /// <summary>
        /// 패키지 동봉 워크플로(Server~/workflows/*.json)와 variables.json을
        /// 사용자 오버라이드 폴더("Assets/MCPTools.User/ComfyUI/")로 복사합니다.
        /// 원본 위치는 <see cref="ComfyUIServerLauncher.GetScriptPath"/>의 부모 폴더(Server~)에서 유도하므로
        /// Assets 설치·UPM(PackageCache/embed) 설치 모두에서 동작합니다.
        /// 대상에 파일이 이미 있으면 덮어쓸지 확인하고, 완료 후 AssetDatabase.Refresh()와 결과 안내를 표시합니다.
        /// </summary>
        private void CopyWorkflowsToProject()
        {
            string serverDir;
            try
            {
                serverDir = Path.GetDirectoryName(ComfyUIServerLauncher.GetScriptPath());
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("워크플로 복사 실패",
                    $"브리지 서버 폴더(Server~) 위치를 확인하지 못했습니다: {e.Message}", "확인");
                return;
            }

            string sourceWorkflowsDir = string.IsNullOrEmpty(serverDir)
                ? string.Empty
                : Path.Combine(serverDir, "workflows");
            string sourceVariablesPath = string.IsNullOrEmpty(serverDir)
                ? string.Empty
                : Path.Combine(serverDir, "variables.json");

            string[] sourceWorkflows = Directory.Exists(sourceWorkflowsDir)
                ? Directory.GetFiles(sourceWorkflowsDir, "*.json", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            bool hasVariables = !string.IsNullOrEmpty(sourceVariablesPath) && File.Exists(sourceVariablesPath);

            if (sourceWorkflows.Length == 0 && !hasVariables)
            {
                EditorUtility.DisplayDialog("워크플로 복사 실패",
                    "패키지의 Server~ 폴더에서 워크플로 JSON과 variables.json을 찾지 못했습니다.\n" +
                    $"확인한 위치: \"{serverDir}\"\n" +
                    "패키지가 불완전하게 설치됐을 수 있습니다. Package Manager에서 재설치해주세요.", "확인");
                return;
            }

            string destRoot = Path.GetFullPath(ComfyUIServerLauncher.UserComfyDirAssetPath);
            string destWorkflowsDir = Path.Combine(destRoot, "workflows");
            string destVariablesPath = Path.Combine(destRoot, "variables.json");

            // 기존 복사본이 있으면 덮어쓸지 확인한다.
            bool destHasAny = File.Exists(destVariablesPath) ||
                              (Directory.Exists(destWorkflowsDir) &&
                               Directory.GetFiles(destWorkflowsDir, "*.json", SearchOption.TopDirectoryOnly).Length > 0);
            if (destHasAny && !EditorUtility.DisplayDialog(
                    "덮어쓰기 확인",
                    $"\"{ComfyUIServerLauncher.UserComfyDirAssetPath}\"에 이미 워크플로/변수 복사본이 있습니다.\n" +
                    "패키지 동봉본으로 덮어쓸까요? (직접 수정한 내용은 사라집니다)",
                    "덮어쓰기", "취소"))
            {
                return;
            }

            int copied;
            try
            {
                Directory.CreateDirectory(destWorkflowsDir);
                copied = 0;
                foreach (string source in sourceWorkflows)
                {
                    File.Copy(source, Path.Combine(destWorkflowsDir, Path.GetFileName(source)), true);
                    copied++;
                }

                if (hasVariables)
                {
                    File.Copy(sourceVariablesPath, destVariablesPath, true);
                    copied++;
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("워크플로 복사 실패",
                    $"파일 복사 중 오류가 발생했습니다: {e.Message}", "확인");
                return;
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("워크플로 복사 완료",
                $"파일 {copied}개를 \"{ComfyUIServerLauncher.UserComfyDirAssetPath}\"로 복사했습니다.\n" +
                "이후 생성은 복사본을 우선 사용합니다. 브리지 서버를 재시작해주세요.", "확인");
        }

        private void DrawConnectionTest()
        {
            EditorGUILayout.LabelField("서버 연결", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_isTesting))
            {
                if (GUILayout.Button("서버 연결 테스트"))
                {
                    StartConnectionTest();
                }
            }

            if (!string.IsNullOrEmpty(_testStatusMessage))
            {
                EditorGUILayout.LabelField(_testStatusMessage, EditorStyles.wordWrappedLabel);
            }
        }

        private void DrawRegisteredTools()
        {
            EditorGUILayout.LabelField("등록된 MCP 도구", EditorStyles.boldLabel);

            foreach (string toolName in McpToolRegistry.ToolNames)
            {
                EditorGUILayout.LabelField("• " + toolName);
            }
        }

        private void StartConnectionTest()
        {
            if (_isTesting)
            {
                return;
            }

            // async void 대신 async Task + fire-and-forget (예외는 내부에서 전부 처리)
            _ = RunConnectionTestAsync();
        }

        private async Task RunConnectionTestAsync()
        {
            _isTesting = true;
            string url = _settings.comfyUIServerUrl;
            _testStatusMessage = "연결 확인 중...";
            Repaint();

            try
            {
                using (var client = new ComfyUIClient(_settings))
                {
                    bool ok = await client.CheckServerAsync();
                    _testStatusMessage = ok
                        ? $"✓ ComfyUI 서버 연결 성공 ({url})"
                        : $"✗ 서버가 실행 중이 아닙니다. ComfyUI를 시작한 뒤 다시 시도하세요. (주소: {url})";
                }
            }
            catch (Exception e)
            {
                _testStatusMessage = $"✗ 연결 테스트 중 오류가 발생했습니다: {e.Message}";
            }
            finally
            {
                _isTesting = false;
                Repaint();
            }
        }

        private void StartComfyUIDetection()
        {
            if (_isDetecting)
            {
                return;
            }

            // async void 대신 async Task + fire-and-forget (예외는 내부에서 전부 처리)
            _ = RunComfyUIDetectionAsync();
        }

        /// <summary>
        /// 리스닝 중인 로컬 TCP 포트를 수집해 각 포트의 /system_stats를 병렬 조회하고,
        /// ComfyUI 시그니처("comfyui_version")가 확인된 포트를 결과로 반영합니다.
        /// 1개면 즉시 설정에 적용, 여러 개면 선택 메뉴 예약, 0개면 상태 라벨에 안내를 표시합니다.
        /// </summary>
        private async Task RunComfyUIDetectionAsync()
        {
            _isDetecting = true;
            _detectStatusMessage = "실행 중인 ComfyUI를 찾는 중...";
            Repaint();

            try
            {
                int[] candidates = CollectLocalListeningPorts();
                int[] found = await ProbeComfyUIPortsAsync(candidates);

                // 감지 중 창이 닫혔으면 파괴된 창의 상태를 건드리지 않는다.
                if (this == null)
                {
                    return;
                }

                if (found.Length == 0)
                {
                    _detectStatusMessage =
                        "✗ 실행 중인 ComfyUI를 찾지 못했습니다. ComfyUI가 켜져 있는지 확인한 뒤 다시 시도하세요.";
                }
                else if (found.Length == 1)
                {
                    ApplyDetectedPort(found[0]);
                }
                else
                {
                    _detectStatusMessage = $"ComfyUI가 {found.Length}개 발견되었습니다. 사용할 포트를 선택하세요.";
                    _pendingDetectedPorts = found;
                }
            }
            catch (Exception e)
            {
                if (this != null)
                {
                    _detectStatusMessage = $"✗ 자동 감지 중 오류가 발생했습니다: {e.Message}";
                }
            }
            finally
            {
                _isDetecting = false;
                if (this != null)
                {
                    Repaint();
                }
            }
        }

        /// <summary>
        /// 현재 PC에서 리스닝 중인 TCP 포트를 수집합니다.
        /// 루프백(127.0.0.1/::1)과 Any(0.0.0.0/::) 바인딩만 대상으로 하고, 포트는 중복 없이 반환합니다.
        /// </summary>
        private static int[] CollectLocalListeningPorts()
        {
            var ports = new HashSet<int>();
            foreach (IPEndPoint listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (IPAddress.IsLoopback(listener.Address) ||
                    listener.Address.Equals(IPAddress.Any) ||
                    listener.Address.Equals(IPAddress.IPv6Any))
                {
                    ports.Add(listener.Port);
                }
            }

            var sorted = new List<int>(ports);
            sorted.Sort();
            return sorted.ToArray();
        }

        /// <summary>
        /// 각 포트의 /system_stats를 짧은 타임아웃(1초)으로 병렬 조회해
        /// ComfyUI 시그니처("comfyui_version")가 있는 포트만 골라 오름차순으로 반환합니다.
        /// 브리지 서버(기본 8189)는 /system_stats가 없으므로 이 검사에서 자동 배제됩니다.
        /// </summary>
        private static async Task<int[]> ProbeComfyUIPortsAsync(int[] ports)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(1);

                var probes = new List<Task<int>>(ports.Length);
                foreach (int port in ports)
                {
                    probes.Add(ProbePortAsync(client, port));
                }

                int[] results = await Task.WhenAll(probes);
                var found = new List<int>();
                foreach (int port in results)
                {
                    if (port > 0)
                    {
                        found.Add(port);
                    }
                }

                found.Sort();
                return found.ToArray();
            }
        }

        /// <summary>단일 포트가 ComfyUI인지 검사합니다. 맞으면 포트 번호, 아니면 0을 반환합니다.</summary>
        private static async Task<int> ProbePortAsync(HttpClient client, int port)
        {
            try
            {
                string json = await client.GetStringAsync($"http://127.0.0.1:{port}/system_stats");
                return json.Contains("\"comfyui_version\"") ? port : 0;
            }
            catch
            {
                // 연결 거부·타임아웃·HTTP 오류는 해당 포트가 ComfyUI가 아니라는 뜻이므로 조용히 무시한다.
                return 0;
            }
        }

        /// <summary>감지된 ComfyUI 포트 목록을 GenericMenu로 표시하고, 선택된 포트를 설정에 적용합니다.</summary>
        private void ShowPortSelectionMenu(int[] ports)
        {
            var menu = new GenericMenu();
            foreach (int port in ports)
            {
                int selected = port; // 클로저 캡처용 지역 변수
                menu.AddItem(new GUIContent($"http://127.0.0.1:{selected}"), false, () => ApplyDetectedPort(selected));
            }

            menu.ShowAsContext();
        }

        /// <summary>감지된 포트를 ComfyUI 서버 주소로 적용·저장하고 결과를 상태 라벨에 표시합니다.</summary>
        private void ApplyDetectedPort(int port)
        {
            // 메뉴 선택 콜백이 창이 닫힌 뒤 돌아올 수 있으므로 방어한다.
            if (this == null || _settings == null)
            {
                return;
            }

            string url = $"http://127.0.0.1:{port}";
            Undo.RecordObject(_settings, "MCP Tools ComfyUI 주소 자동 감지");
            _settings.comfyUIServerUrl = url;
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssets();

            // 편집 중이던 텍스트 필드가 옛 값을 유지하지 않도록 포커스를 해제한다.
            GUI.FocusControl(null);
            _detectStatusMessage =
                $"✓ ComfyUI를 찾아 서버 주소를 적용했습니다: {url}\n" +
                "브리지 서버가 실행 중이면 재시작해야 새 주소가 반영됩니다.";
            Repaint();
        }
    }
}
