using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 3단계 생성 창입니다. (메뉴: Tools/MCP/3. ComfyUI Generator)
    /// 브리지 서버 시작/종료, 워크플로 선택과 변수 편집(원본 JSON 기본값), PromptSet 항목 선택,
    /// 후보 N개 생성(기본 4개, 비동기·취소 가능) → 썸네일 선택 → 확정/재생성 흐름을 제공합니다.
    /// </summary>
    public class ComfyUIGeneratorWindow : EditorWindow
    {
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const float ThumbnailSize = 160f;
        private const double ServerCheckIntervalSeconds = 5.0;

        // 왼쪽 열은 기본 320px이지만, 창이 좁아지면 오른쪽 열이 잘리지 않도록 240px까지 줄어든다.
        private const float LeftColumnMaxWidth = 320f;
        private const float LeftColumnMinWidth = 240f;

        // 오른쪽 열이 가로 스크롤 없이 쓸 수 있는 최소 폭 (이보다 좁아지면 왼쪽 열을 줄인다).
        private const float RightColumnMinWidth = 420f;

        // 세로 스크롤바 + 여백 예상치. 내용 폭을 이만큼 줄여 가로 스크롤이 생기지 않게 한다.
        private const float ScrollbarWidth = 20f;

        /// <summary>항목 편집 패널의 종류 선택지입니다 (2단계 창과 동일).</summary>
        private static readonly string[] AssetTypeOptions = { "image", "ui", "audio" };

        // 후보 개수 슬라이더 범위. 상한은 ComfyUI 큐가 한 번에 처리하기 현실적인 수준으로 잡았다.
        private const int MinCandidateCount = 1;
        private const int MaxCandidateCount = 12;

        /// <summary>변수 1개의 편집 상태입니다 (매니페스트 정의 + 현재 값).</summary>
        private class VariableState
        {
            public BridgeVariable definition;
            public string stringValue = string.Empty;
            public int intValue;
            public float floatValue;
            public bool boolValue;

            /// <summary>image 타입: 업로드할 로컬 파일 경로 (비어 있으면 기본 파일명 사용).</summary>
            public string imageLocalPath = string.Empty;
        }

        private MCPToolSettings _settings;

        // PromptSet 선택/로드
        private string[] _promptSetPaths = new string[0];
        private int _selectedSetIndex;
        private PromptSetDocument _document;
        private int _selectedItemIndex = -1;

        // 로드한 PromptSet JSON 경로와 편집 상태 (항목 편집 창으로 추가/수정한 결과는 목록에만 반영되고,
        // [저장]을 눌러야 JSON 파일에 기록된다).
        private string _documentPath = string.Empty;
        private bool _documentDirty;

        // 일괄 생성 대상으로 체크된 항목 ID들 (목록 행의 체크박스).
        private readonly HashSet<string> _checkedItemIds = new HashSet<string>();

        // 항목별 상태 캐시 (목록 배지/확정 미리보기 표시용):
        // 항목 ID → 후보 개수, 항목 ID → 확정본 경로 (GenerationResults.json 기록 기반)
        private readonly Dictionary<string, int> _candidateCounts = new Dictionary<string, int>();
        private Dictionary<string, string> _confirmedPaths = new Dictionary<string, string>();

        // 일괄 생성 상태
        private bool _batchRunning;
        private int _batchCurrent;
        private int _batchTotal;
        private string _batchCurrentLabel = string.Empty;

        // 워크플로/변수
        private List<BridgeWorkflowInfo> _workflows = new List<BridgeWorkflowInfo>();
        private int _selectedWorkflowIndex;
        private List<VariableState> _variableStates = new List<VariableState>();
        private bool _workflowsLoading;

        // 워크플로 목록 로드 시점에 브리지가 ComfyUI /object_info 조회에 성공했는지.
        // false면 missingNodes/options 검증이 생략된 상태이므로 누락 노드 경고를 띄우지 않는다.
        private bool _workflowsComfyReachable;

        // 생성 상태
        private bool _generating;
        private float _progress;
        private CancellationTokenSource _cancelSource;
        private List<CandidateInfo> _candidates = new List<CandidateInfo>();
        private string _candidatesItemId = string.Empty;
        private int _selectedCandidateIndex = -1;
        private string _statusMessage = string.Empty;
        private Vector2 _scroll;
        private Vector2 _itemListScroll;

        // 오른쪽 열의 실제 폭 (Repaint 때 측정). 창 폭에서 빼는 추정은 스크롤바/여백만큼 어긋나
        // 후보 썸네일이 잘렸기 때문에, 레이아웃이 실제로 준 폭을 재서 쓴다.
        private float _rightColumnWidth;

        // 서버 상태
        private bool _bridgeAlive;
        private bool _comfyAlive;
        private bool _serverChecking;
        private double _nextServerCheck;
        private bool _remoteShutdownRunning;

        // 지금 포트에 떠 있는 브리지의 스크립트 경로 (/health). 원격 종료 확인 다이얼로그에 표시.
        private string _bridgeScriptPath = string.Empty;

        /// <summary>생성 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/3. ComfyUI Generator", false, 3)]
        public static void Open()
        {
            var window = GetWindow<ComfyUIGeneratorWindow>();
            window.titleContent = new GUIContent("ComfyUI 생성");
            window.minSize = new Vector2(960f, 600f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();
            RefreshPromptSetPaths();
            EditorApplication.update += OnEditorUpdate;
            _nextServerCheck = 0.0; // 즉시 1회 확인
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CancelGeneration();
            EditorAudioPreview.Stop();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup >= _nextServerCheck && !_serverChecking)
            {
                _nextServerCheck = EditorApplication.timeSinceStartup + ServerCheckIntervalSeconds;
                _ = CheckServerAsync();
            }
        }

        private async Task CheckServerAsync()
        {
            _serverChecking = true;
            try
            {
                using (var client = new BridgeClient(_settings))
                {
                    BridgeHealth health = await client.GetHealthAsync();
                    bool changed = health.ok != _bridgeAlive || health.comfyAlive != _comfyAlive;
                    bool comfyCameAlive = health.comfyAlive && !_comfyAlive;
                    _bridgeAlive = health.ok;
                    _comfyAlive = health.comfyAlive;
                    _bridgeScriptPath = health.scriptPath;

                    // 브리지가 살아났고 워크플로 목록이 아직 없으면 자동 로드.
                    // ComfyUI가 미연결→연결로 바뀐 경우에도 1회 재로드해
                    // 설치된 모델 선택지(options)를 받아온다.
                    if (_bridgeAlive && !_workflowsLoading &&
                        (_workflows.Count == 0 || comfyCameAlive))
                    {
                        await LoadWorkflowsAsync();
                        changed = true;
                    }

                    if (changed)
                    {
                        Repaint();
                    }
                }
            }
            catch (Exception)
            {
                _bridgeAlive = false;
                _comfyAlive = false;
            }
            finally
            {
                _serverChecking = false;
            }
        }

        private async Task LoadWorkflowsAsync()
        {
            _workflowsLoading = true;
            try
            {
                using (var client = new BridgeClient(_settings))
                {
                    BridgeWorkflowsResult result = await client.GetWorkflowsAsync();
                    _workflows = result.workflows;
                    _workflowsComfyReachable = result.comfyReachable;
                }

                _selectedWorkflowIndex = Mathf.Clamp(_selectedWorkflowIndex, 0, Mathf.Max(0, _workflows.Count - 1));
                // 재로드로 options만 갱신하고 사용자가 편집 중인 값은 유지한다.
                RebuildVariableStates(preserveValues: true);
            }
            catch (Exception e)
            {
                _statusMessage = $"워크플로 목록 로드 실패: {e.Message}";
            }
            finally
            {
                _workflowsLoading = false;
                Repaint();
            }
        }

        /// <summary>
        /// Play Mode·컴파일/임포트로 실행 버튼을 막아야 하는 사유입니다 (막지 않아도 되면 null).
        /// MCP 도구와 같은 판정(<see cref="McpToolRegistry.GetBlockedReason"/>)을 쓰며 OnGUI마다 한 번 갱신합니다.
        /// </summary>
        private string _blockedReason;

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            _blockedReason = McpToolRegistry.GetBlockedReason();
            if (_blockedReason != null)
            {
                EditorGUILayout.HelpBox(_blockedReason, MessageType.Warning);
            }

            DrawServerSection();
            EditorGUILayout.Space(6f);

            // 2열 레이아웃: 왼쪽 열(입력/항목 목록), 오른쪽 열(워크플로/생성/후보)
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(CurrentLeftColumnWidth())))
                {
                    DrawInputSection();
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    // 열 폭은 스크롤뷰 "바깥"에서 잰다. 안에서 재면 내용이 넓어질 때 그 값이 다시 커지는
                    // 되먹임이 생겨 열이 창 밖으로 밀린다.
                    MeasureRightColumnWidth();
                    float contentWidth = RightContentWidth();

                    _scroll = EditorGUILayout.BeginScrollView(_scroll);

                    // 내용을 표시 폭에 고정해 가로 스크롤이 필요 없게 만든다. 라벨 폭도 열 폭에 맞춰 줄여
                    // 좁은 창에서 입력 필드가 밀려나지 않게 한다.
                    float previousLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = Mathf.Clamp(contentWidth * 0.4f, 90f, 160f);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(contentWidth)))
                    {
                        DrawWorkflowSection();
                        EditorGUILayout.Space(6f);
                        DrawGenerateSection();
                        EditorGUILayout.Space(6f);
                        DrawCandidateSection();
                    }

                    EditorGUIUtility.labelWidth = previousLabelWidth;
                    EditorGUILayout.EndScrollView();
                }
            }

            DrawBottomBar();
        }

        /// <summary>
        /// 현재 창 폭에 맞는 왼쪽 열 폭입니다. 오른쪽 열이 최소 폭을 유지하도록 창이 좁으면 함께 줄어듭니다.
        /// </summary>
        private float CurrentLeftColumnWidth()
        {
            return Mathf.Clamp(position.width - RightColumnMinWidth, LeftColumnMinWidth, LeftColumnMaxWidth);
        }

        /// <summary>
        /// 오른쪽 열의 실제 폭을 측정합니다 (Repaint 시에만 유효).
        /// 높이 0짜리 가로 확장 사각형을 잡아 창 폭에서 추정하지 않고 레이아웃이 준 값을 그대로 얻습니다.
        /// </summary>
        private void MeasureRightColumnWidth()
        {
            Rect probe = GUILayoutUtility.GetRect(1f, 0f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && probe.width > 1f)
            {
                _rightColumnWidth = probe.width;
            }
        }

        /// <summary>
        /// 오른쪽 열에서 내용이 쓸 수 있는 폭입니다 (세로 스크롤바와 여백 제외).
        /// 아직 측정 전이면 창 폭에서 추정합니다.
        /// </summary>
        private float RightContentWidth()
        {
            float column = _rightColumnWidth > 1f
                ? _rightColumnWidth
                : Mathf.Max(RightColumnMinWidth, position.width - CurrentLeftColumnWidth() - 24f);
            return Mathf.Max(180f, column - ScrollbarWidth);
        }

        // ─────────────────────────── 서버 상태/시작/종료 ───────────────────────────

        private void DrawServerSection()
        {
            // 이 에디터 세션이 시작한 서버만 PID를 알기 때문에 프로세스 종료가 가능하다.
            // 그 외(다른 프로젝트 / 에디터 재시작 후)는 [원격 종료]로 처리한다.
            bool ownProcess = ComfyUIServerLauncher.IsLaunchedProcessAlive();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawStatusDot(_bridgeAlive, _bridgeAlive ? "브리지 서버 실행 중" : "브리지 서버 미기동", 170f);
                    EditorGUILayout.LabelField(_settings.bridgeServerUrl, EditorStyles.miniLabel);

                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(_bridgeAlive))
                    {
                        if (GUILayout.Button("서버 시작", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                        {
                            StartServer();
                        }
                    }

                    using (new EditorGUI.DisabledScope(!ownProcess))
                    {
                        if (GUILayout.Button("서버 종료", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                        {
                            StopServer();
                        }
                    }

                    if (_bridgeAlive && !ownProcess)
                    {
                        using (new EditorGUI.DisabledScope(_remoteShutdownRunning))
                        {
                            if (GUILayout.Button("원격 종료", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                            {
                                _ = ShutdownForeignServerAsync();
                            }
                        }
                    }
                }

                // 두 버튼이 동시에 비활성인 상태(외부에서 띄운 서버가 이미 포트를 쓰는 중)는
                // 안내가 없으면 "도구가 고장난 것"으로 보인다. 원인과 조치를 반드시 표시한다.
                if (_bridgeAlive && !ownProcess)
                {
                    EditorGUILayout.HelpBox(
                        $"이미 실행 중인 브리지 서버가 {_settings.bridgeServerUrl}에 응답하고 있습니다. " +
                        "다른 Unity 프로젝트에서 시작했거나, 이 에디터를 재시작해 프로세스 정보를 잃은 경우입니다. " +
                        "그대로 사용해도 생성은 정상 동작합니다.\n" +
                        "이 프로젝트의 서버로 바꾸려면 [원격 종료]로 내린 뒤 [서버 시작]을 누르세요. " +
                        "두 서버를 함께 쓰려면 Tools/MCP/Settings에서 브리지 서버 주소의 포트를 다른 번호로 바꾸세요.",
                        MessageType.Info);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawStatusDot(_comfyAlive, _comfyAlive ? "ComfyUI 연결됨" : "ComfyUI 미연결", 170f);
                    EditorGUILayout.LabelField(_settings.comfyUIServerUrl, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                }

                if (!_comfyAlive && _bridgeAlive)
                {
                    EditorGUILayout.HelpBox(
                        "브리지 서버는 실행 중이지만 ComfyUI가 응답하지 않습니다. " +
                        $"ComfyUI({_settings.comfyUIServerUrl})를 별도로 실행해주세요.", MessageType.Warning);
                }
            }
        }

        private static void DrawStatusDot(bool alive, string label, float width)
        {
            var statusStyle = new GUIStyle(EditorStyles.boldLabel);
            statusStyle.normal.textColor = alive
                ? new Color(0.2f, 0.75f, 0.3f)
                : new Color(0.85f, 0.3f, 0.25f);
            EditorGUILayout.LabelField($"● {label}", statusStyle, GUILayout.Width(width));
        }

        private void StartServer()
        {
            try
            {
                ComfyUIServerLauncher.Start(_settings);
                _statusMessage = "브리지 서버를 시작했습니다. 잠시 후 상태가 갱신됩니다.";
                _nextServerCheck = EditorApplication.timeSinceStartup + 2.0;
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("브리지 서버 시작 실패", e.Message, "확인");
            }
        }

        /// <summary>
        /// 이 세션이 시작하지 않아 PID를 모르는 브리지 서버를 <c>POST /shutdown</c>으로 내립니다.
        /// 구버전 브리지(엔드포인트 없음)면 콘솔 창을 닫도록 안내합니다.
        /// </summary>
        private async Task ShutdownForeignServerAsync()
        {
            // 어느 서버를 내리는지 /health의 scriptPath로 알려준다 — 다른 프로젝트의
            // 설치본을 실수로 종료하는 것을 막기 위한 확인 정보다.
            string target = string.IsNullOrEmpty(_bridgeScriptPath)
                ? _settings.bridgeServerUrl
                : $"{_settings.bridgeServerUrl}\n({_bridgeScriptPath})";

            if (!EditorUtility.DisplayDialog("브리지 서버 원격 종료",
                    $"실행 중인 브리지 서버를 종료합니다.\n{target}\n\n" +
                    "다른 Unity 프로젝트가 이 서버를 쓰고 있다면 그 프로젝트의 생성 작업도 중단됩니다. " +
                    "계속할까요?",
                    "종료", "취소"))
            {
                return;
            }

            _remoteShutdownRunning = true;
            try
            {
                bool supported;
                using (var client = new BridgeClient(_settings))
                {
                    supported = await client.ShutdownAsync();
                }

                if (supported)
                {
                    _statusMessage = "브리지 서버에 종료를 요청했습니다. 잠시 후 상태가 갱신됩니다.";
                }
                else
                {
                    EditorUtility.DisplayDialog("브리지 서버 원격 종료",
                        "실행 중인 브리지 서버가 원격 종료를 지원하지 않는 구버전입니다.\n\n" +
                        "서버를 띄운 콘솔 창을 직접 닫거나, 시작한 Unity 프로젝트의 3단계 창에서 " +
                        "[서버 종료]를 눌러주세요.", "확인");
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("브리지 서버 원격 종료 실패", e.Message, "확인");
            }
            finally
            {
                _remoteShutdownRunning = false;
                _nextServerCheck = EditorApplication.timeSinceStartup + 1.0;
                Repaint();
            }
        }

        private void StopServer()
        {
            try
            {
                ComfyUIServerLauncher.Stop();
                _statusMessage = "브리지 서버를 종료했습니다.";
                _bridgeAlive = false;
                _comfyAlive = false;
                _nextServerCheck = EditorApplication.timeSinceStartup + 2.0;
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("브리지 서버 종료 실패", e.Message, "확인");
            }
        }

        // ─────────────────────────── 입력(문서/항목) ───────────────────────────

        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("입력", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_promptSetPaths.Length == 0)
                    {
                        EditorGUILayout.HelpBox(
                            $"{MCPToolFolders.PromptSetDir(_settings)} 폴더에 PromptSet_*.json이 없습니다. " +
                            "2단계(Tools/MCP/2. Prompt Builder)에서 먼저 저장해주세요.", MessageType.Warning);
                    }
                    else
                    {
                        _selectedSetIndex = EditorGUILayout.Popup("PromptSet JSON",
                            Mathf.Clamp(_selectedSetIndex, 0, _promptSetPaths.Length - 1), _promptSetPaths);
                    }

                    if (GUILayout.Button("새로고침", GUILayout.Width(SmallButtonWidth)))
                    {
                        RefreshPromptSetPaths();
                    }

                    using (new EditorGUI.DisabledScope(_promptSetPaths.Length == 0))
                    {
                        if (GUILayout.Button("로드", GUILayout.Width(SmallButtonWidth)))
                        {
                            LoadSelectedPromptSet();
                        }
                    }
                }

                if (_document == null)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("PromptSet JSON을 로드하면 항목을 선택할 수 있습니다.", EditorStyles.miniLabel);
                        if (GUILayout.Button(
                                new GUIContent("항목 추가", "PromptSet 없이 항목을 직접 작성해 새 목록을 시작합니다."),
                                EditorStyles.miniButton, GUILayout.Width(SmallButtonWidth)))
                        {
                            OpenItemEditor(null);
                        }
                    }

                    return;
                }

                DrawItemListPanel();
                DrawItemActionRow();

                PromptItem item = SelectedItem();
                if (item != null && string.IsNullOrEmpty(item.positive))
                {
                    EditorGUILayout.HelpBox("이 항목의 positive 프롬프트가 비어 있습니다. [편집]이나 2단계에서 채운 뒤 생성하세요.",
                        MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// 현재 선택된 워크플로에 해당하는 항목인지 판정합니다
        /// (Audio→audio, UI→ui/isUI, 이미지 계열→image). 워크플로 목록이 없으면 전체 표시.
        /// </summary>
        private bool ItemMatchesWorkflow(PromptItem item)
        {
            BridgeWorkflowInfo workflow = SelectedWorkflow();
            if (workflow == null)
            {
                return true;
            }

            if (workflow.name == "Audio")
            {
                return item.assetType == "audio";
            }

            if (workflow.name == "UI")
            {
                return item.assetType == "ui" || item.isUI;
            }

            // GenerateImage/StyleChange 등 이미지 계열
            return item.assetType == "image" && !item.isUI;
        }

        /// <summary>
        /// PromptSet 항목 중 현재 워크플로에 해당하는 항목만 행 목록으로 그립니다.
        /// 각 행에 id/이름/종류, 상태 배지(프롬프트 없음/미생성/후보 N개/확정됨)를
        /// 표시하고, 클릭하면 해당 항목을 선택합니다. 목록은 자체 스크롤을 가집니다.
        /// 확정된 항목은 행의 소형 썸네일 클릭으로 확정 에셋을 핑(Ping)할 수 있습니다.
        /// </summary>
        private void DrawItemListPanel()
        {
            var rowStyle = new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };
            var badgeStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

            var filtered = new List<int>();
            for (int i = 0; i < _document.items.Count; i++)
            {
                if (ItemMatchesWorkflow(_document.items[i]))
                {
                    filtered.Add(i);
                }
            }

            if (filtered.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "이 워크플로에 해당하는 항목이 없습니다. 아래 [항목 편집]의 [추가]로 항목을 만들 수 있습니다.",
                    MessageType.Info);
                return;
            }

            DrawSelectionToolbar(filtered);

            _itemListScroll = EditorGUILayout.BeginScrollView(_itemListScroll, GUILayout.ExpandHeight(true));
            foreach (int i in filtered)
            {
                PromptItem item = _document.items[i];
                bool selected = i == _selectedItemIndex;

                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect rowRect = GUILayoutUtility.GetRect(0f, 0f, GUILayout.Width(0f), GUILayout.Height(0f));
                    if (selected && Event.current.type == EventType.Repaint)
                    {
                        EditorGUI.DrawRect(
                            new Rect(rowRect.x, rowRect.y, CurrentLeftColumnWidth() - 24f, 22f),
                            new Color(0.24f, 0.49f, 0.90f, 0.25f));
                    }

                    // 일괄 생성 대상 체크박스 (행 선택과 독립적으로 동작한다).
                    bool wasChecked = _checkedItemIds.Contains(item.id);
                    bool nowChecked = EditorGUILayout.Toggle(wasChecked, GUILayout.Width(16f), GUILayout.Height(20f));
                    if (nowChecked != wasChecked)
                    {
                        SetItemChecked(item.id, nowChecked);
                    }

                    string rowLabel =
                        $"{(selected ? "▶ " : "   ")}{item.id}  {item.name}  [{item.assetType}{(item.isUI ? "/UI" : string.Empty)}]";
                    if (GUILayout.Button(rowLabel, rowStyle, GUILayout.Height(20f), GUILayout.ExpandWidth(true)))
                    {
                        SelectItem(i);
                    }

                    DrawRowConfirmedThumbnail(item);

                    GetItemBadge(item, out string badge, out Color badgeColor);
                    badgeStyle.normal.textColor = badgeColor;
                    EditorGUILayout.LabelField(badge, badgeStyle, GUILayout.Width(90f), GUILayout.Height(20f));
                }
            }

            EditorGUILayout.EndScrollView();

            int confirmedCount = filtered.Count(i => _confirmedPaths.ContainsKey(_document.items[i].id));
            EditorGUILayout.LabelField(
                $"확정 {confirmedCount}/{filtered.Count} · 체크 {CheckedTargets().Count}개", EditorStyles.miniLabel);

            DrawConfirmedAssetPanel(SelectedItem());
        }

        /// <summary>
        /// 목록 위의 일괄 생성 대상 선택 도구 모음입니다 (전체 / 해제 / 미생성만).
        /// 선택 대상은 현재 워크플로에 해당하는 항목으로 한정합니다.
        /// </summary>
        /// <param name="filtered">현재 워크플로에 해당하는 항목의 문서 인덱스 목록.</param>
        private void DrawSelectionToolbar(List<int> filtered)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent("일괄 생성 대상", "체크한 항목만 [선택 항목 생성]으로 한 번에 생성합니다."),
                    EditorStyles.miniBoldLabel, GUILayout.Width(80f));

                if (GUILayout.Button("전체", EditorStyles.miniButtonLeft, GUILayout.Width(44f)))
                {
                    foreach (int i in filtered)
                    {
                        SetItemChecked(_document.items[i].id, true);
                    }
                }

                if (GUILayout.Button("해제", EditorStyles.miniButtonMid, GUILayout.Width(44f)))
                {
                    _checkedItemIds.Clear();
                }

                if (GUILayout.Button(
                        new GUIContent("미생성", "후보가 없고 확정되지 않은 항목만 체크합니다."),
                        EditorStyles.miniButtonRight, GUILayout.Width(52f)))
                {
                    _checkedItemIds.Clear();
                    foreach (int i in filtered)
                    {
                        PromptItem item = _document.items[i];
                        if (IsUnrenderedTarget(item))
                        {
                            SetItemChecked(item.id, true);
                        }
                    }
                }

                GUILayout.FlexibleSpace();
            }
        }

        /// <summary>항목의 일괄 생성 체크 상태를 설정합니다.</summary>
        private void SetItemChecked(string itemId, bool value)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return;
            }

            if (value)
            {
                _checkedItemIds.Add(itemId);
            }
            else
            {
                _checkedItemIds.Remove(itemId);
            }
        }

        /// <summary>후보도 확정본도 없고 프롬프트가 채워진(= 아직 생성하지 않은) 항목인지 판정합니다.</summary>
        private bool IsUnrenderedTarget(PromptItem item)
        {
            return !string.IsNullOrEmpty(item.positive) &&
                   !_confirmedPaths.ContainsKey(item.id) &&
                   (!_candidateCounts.TryGetValue(item.id, out int count) || count == 0);
        }

        /// <summary>현재 워크플로에 해당하면서 체크된 항목 목록을 반환합니다.</summary>
        private List<PromptItem> CheckedTargets()
        {
            if (_document == null)
            {
                return new List<PromptItem>();
            }

            return _document.items
                .Where(it => _checkedItemIds.Contains(it.id) && ItemMatchesWorkflow(it))
                .ToList();
        }

        /// <summary>현재 워크플로에 해당하는 항목 중 아직 생성하지 않은 항목 목록을 반환합니다.</summary>
        private List<PromptItem> UnrenderedTargets()
        {
            if (_document == null)
            {
                return new List<PromptItem>();
            }

            return _document.items
                .Where(it => ItemMatchesWorkflow(it) && IsUnrenderedTarget(it))
                .ToList();
        }

        // ─────────────────────────── 항목 편집(별도 창) ───────────────────────────

        /// <summary>
        /// 목록 아래 한 줄짜리 항목 조작 행입니다. 추가/편집은 별도 편집 창(<see cref="PromptItemEditWindow"/>)을 띄우고,
        /// 그 창에서 [저장]하면 이 목록에 반영됩니다. [저장]은 목록 전체를 PromptSet JSON에 기록합니다.
        /// </summary>
        private void DrawItemActionRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                        new GUIContent("추가", "새 항목을 편집 창에서 작성해 이 목록에 추가합니다."),
                        EditorStyles.miniButtonLeft))
                {
                    OpenItemEditor(null);
                }

                using (new EditorGUI.DisabledScope(SelectedItem() == null))
                {
                    if (GUILayout.Button(
                            new GUIContent("편집", "선택한 항목을 편집 창에서 수정합니다."),
                            EditorStyles.miniButtonMid))
                    {
                        OpenItemEditor(SelectedItem());
                    }

                    if (GUILayout.Button(
                            new GUIContent("삭제", "선택한 항목을 목록에서 제거합니다 (생성된 후보/확정 파일은 유지)."),
                            EditorStyles.miniButtonMid))
                    {
                        DeleteSelectedItem();
                    }
                }

                using (new EditorGUI.DisabledScope(_document.items.Count == 0))
                {
                    if (GUILayout.Button(
                            new GUIContent(_documentDirty ? "저장 *" : "저장",
                                string.IsNullOrEmpty(_documentPath)
                                    ? "Docs의 PromptSet 폴더에 새 PromptSet_*.json으로 저장합니다."
                                    : $"{_documentPath}에 덮어씁니다."),
                            EditorStyles.miniButtonRight))
                    {
                        SaveDocument(string.IsNullOrEmpty(_documentPath) ? null : _documentPath);
                    }
                }
            }
        }

        /// <summary>
        /// 항목 편집 창을 엽니다. 문서가 없으면 빈 목록을 먼저 만듭니다.
        /// </summary>
        /// <param name="existing">수정할 기존 항목. null이면 새 항목을 작성합니다.</param>
        private void OpenItemEditor(PromptItem existing)
        {
            if (_document == null)
            {
                _document = new PromptSetDocument { createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                _documentPath = string.Empty;
                _documentDirty = false;
                _checkedItemIds.Clear();
                _selectedItemIndex = -1;
                RefreshItemStatuses();
            }

            PromptItemEditWindow.Open(this, existing);
        }

        /// <summary>
        /// 편집 창에서 쓸 새 항목 초안을 만듭니다: 새 ID와 현재 워크플로 종류(audio/ui/image)만 채우고,
        /// positive/negative 프롬프트는 빈칸으로 둡니다 (변수 편집란의 값이 필요하면 편집 창의
        /// [생성 창의 현재 프롬프트 가져오기]로 가져옵니다).
        /// </summary>
        /// <returns>새 항목 초안.</returns>
        internal PromptItem CreateItemDraft()
        {
            var item = new PromptItem { id = NextItemId(), name = "새 항목" };

            BridgeWorkflowInfo workflow = SelectedWorkflow();
            if (workflow != null && workflow.name == "Audio")
            {
                item.assetType = "audio";
            }
            else if (workflow != null && workflow.name == "UI")
            {
                item.assetType = "ui";
                item.isUI = true;
            }

            return item;
        }

        /// <summary>
        /// 편집 창의 [저장] 결과를 목록에 반영합니다. 같은 ID의 항목이 있으면 교체하고, 없으면 끝에 추가한 뒤
        /// 그 항목을 선택합니다. 목록(메모리)에만 반영되므로 파일에 남기려면 생성 창의 [저장]을 눌러야 합니다.
        /// </summary>
        /// <param name="edited">편집 창에서 작성한 항목.</param>
        internal void ApplyEditedItem(PromptItem edited)
        {
            if (_document == null || edited == null)
            {
                return;
            }

            int index = _document.items.FindIndex(it => it.id == edited.id);
            if (index >= 0)
            {
                _document.items[index] = edited;
                _statusMessage = $"항목을 수정했습니다: {edited.id} ({edited.name}) — 파일에 남기려면 [저장]을 누르세요.";
            }
            else
            {
                _document.items.Add(edited);
                index = _document.items.Count - 1;
                _statusMessage = $"항목을 추가했습니다: {edited.id} ({edited.name}) — 파일에 남기려면 [저장]을 누르세요.";
            }

            _documentDirty = true;
            RefreshItemStatuses();
            SelectItem(index);

            // 편집 결과를 변수 편집란에 그대로 맞춘다 (프롬프트를 비운 경우까지 반영).
            ApplyItemPromptsToVariables(overwriteWithEmpty: true);
            Repaint();
        }

        /// <summary>변수 편집란(role=positive/negative)의 현재 값을 항목에 복사합니다.</summary>
        internal void CopyVariablePromptsTo(PromptItem item)
        {
            foreach (VariableState state in _variableStates)
            {
                if (state.definition.role == "positive")
                {
                    item.positive = state.stringValue ?? string.Empty;
                }
                else if (state.definition.role == "negative")
                {
                    item.negative = state.stringValue ?? string.Empty;
                }
            }
        }

        /// <summary>선택 항목을 목록에서 삭제합니다 (이미 생성된 후보/확정 파일은 지우지 않습니다).</summary>
        private void DeleteSelectedItem()
        {
            PromptItem item = SelectedItem();
            if (item == null)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog("항목 삭제",
                    $"{item.id} ({item.name}) 항목을 목록에서 삭제합니다.\n" +
                    "이미 생성된 후보/확정 파일은 삭제되지 않습니다. 계속할까요?", "삭제", "취소"))
            {
                return;
            }

            int index = Mathf.Clamp(_selectedItemIndex, 0, _document.items.Count - 1);
            _document.items.RemoveAt(index);
            _checkedItemIds.Remove(item.id);
            _documentDirty = true;

            if (_candidatesItemId == item.id)
            {
                _candidates.Clear();
                _candidatesItemId = string.Empty;
                _selectedCandidateIndex = -1;
            }

            if (_document.items.Count == 0)
            {
                _selectedItemIndex = -1;
                Repaint();
                return;
            }

            SelectItem(Mathf.Clamp(index, 0, _document.items.Count - 1));
        }

        /// <summary>다음 항목 ID(item_00N)를 만듭니다 (기존 최대 번호 + 1).</summary>
        private string NextItemId()
        {
            int max = 0;
            foreach (PromptItem item in _document.items)
            {
                if (!string.IsNullOrEmpty(item.id) && item.id.StartsWith("item_") &&
                    int.TryParse(item.id.Substring(5), out int n) && n > max)
                {
                    max = n;
                }
            }

            return $"item_{max + 1:000}";
        }

        /// <summary>
        /// 편집한 항목 목록을 PromptSet JSON으로 저장합니다.
        /// </summary>
        /// <param name="outputPath">덮어쓸 경로. null이면 Docs의 PromptSet 폴더에 새 파일로 저장합니다.</param>
        private void SaveDocument(string outputPath)
        {
            if (_document == null || _document.items.Count == 0)
            {
                EditorUtility.DisplayDialog("ComfyUI 생성", "저장할 항목이 없습니다.", "확인");
                return;
            }

            try
            {
                string saved = PromptBuilder.Save(_document, outputPath);
                _documentPath = saved;
                _documentDirty = false;

                RefreshPromptSetPaths();
                int index = Array.IndexOf(_promptSetPaths, saved);
                if (index >= 0)
                {
                    _selectedSetIndex = index;
                }

                _statusMessage = $"저장 완료: {saved}";
                ShowNotification(new GUIContent("항목 목록을 저장했습니다."));
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("저장 실패", $"PromptSet JSON 저장 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        /// <summary>저장하지 않은 편집이 있으면 확인 다이얼로그를 띄웁니다.</summary>
        /// <param name="actionDescription">"~하면" 형태의 동작 설명.</param>
        /// <returns>진행해도 되면 true.</returns>
        private bool ConfirmDiscardChanges(string actionDescription)
        {
            if (_document == null || !_documentDirty)
            {
                return true;
            }

            return EditorUtility.DisplayDialog("저장하지 않은 변경",
                $"항목 편집 내용이 저장되지 않았습니다. {actionDescription} 변경 내용이 사라집니다.\n계속할까요?",
                "계속", "취소");
        }

        /// <summary>
        /// 확정된 항목이면 행 오른쪽에 확정 에셋의 소형 미리보기(이미지 썸네일, 오디오는 아이콘)를
        /// 그립니다. 클릭하면 해당 에셋을 프로젝트 창에서 핑(Ping)합니다. 에셋이 삭제된 경우 그리지 않습니다.
        /// </summary>
        private void DrawRowConfirmedThumbnail(PromptItem item)
        {
            if (!_confirmedPaths.TryGetValue(item.id, out string path) || string.IsNullOrEmpty(path))
            {
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                return;
            }

            Rect thumbRect = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f), GUILayout.Height(20f));
            Texture preview = asset is Texture2D tex
                ? tex
                : AssetPreview.GetMiniThumbnail(asset);
            if (preview != null)
            {
                GUI.DrawTexture(thumbRect, preview, ScaleMode.ScaleToFit);
            }

            if (Event.current.type == EventType.MouseDown && thumbRect.Contains(Event.current.mousePosition))
            {
                EditorGUIUtility.PingObject(asset);
                Event.current.Use();
            }
        }

        /// <summary>
        /// 선택된 항목이 확정된 경우 확정 에셋 미리보기 패널을 그립니다:
        /// "확정됨" 배지 + 썸네일(오디오는 아이콘+파일명) + 에셋 경로.
        /// 썸네일/경로 클릭 시 에셋을 핑하며, 에셋이 삭제된 경우 안내 문구를 표시합니다.
        /// </summary>
        private void DrawConfirmedAssetPanel(PromptItem item)
        {
            if (item == null || !_confirmedPaths.TryGetValue(item.id, out string path))
            {
                return;
            }

            // 에셋 경로는 띄어쓰기가 없어 word-wrap이 줄을 못 나눈다. 폭을 고정하지 않으면 이 라벨의
            // 최소 폭이 왼쪽 열을 넓혀 오른쪽 열을 창 밖으로 밀어낸다.
            float panelWidth = CurrentLeftColumnWidth() - 26f;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var badgeStyle = new GUIStyle(EditorStyles.boldLabel);
                badgeStyle.normal.textColor = new Color(0.2f, 0.75f, 0.3f);
                EditorGUILayout.LabelField($"✔ 확정됨 — {item.id}", badgeStyle);

                var asset = string.IsNullOrEmpty(path)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset == null)
                {
                    EditorGUILayout.LabelField(
                        $"확정 에셋 없음(삭제됨): {path}", EditorStyles.wordWrappedMiniLabel,
                        GUILayout.Width(panelWidth));
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    const float previewSize = 64f;
                    Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize,
                        GUILayout.Width(previewSize), GUILayout.Height(previewSize));

                    if (asset is Texture2D texture)
                    {
                        GUI.DrawTexture(previewRect, texture, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        Texture icon = AssetPreview.GetMiniThumbnail(asset);
                        if (icon != null)
                        {
                            GUI.DrawTexture(previewRect, icon, ScaleMode.ScaleToFit);
                        }
                    }

                    float textWidth = Mathf.Max(80f, panelWidth - previewSize - 8f);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(textWidth)))
                    {
                        if (asset is AudioClip)
                        {
                            EditorGUILayout.LabelField($"♪ {Path.GetFileName(path)}", EditorStyles.miniBoldLabel,
                                GUILayout.Width(textWidth));
                        }

                        EditorGUILayout.LabelField(path, EditorStyles.wordWrappedMiniLabel,
                            GUILayout.Width(textWidth));
                        if (GUILayout.Button("에셋 위치 보기 (Ping)", GUILayout.Width(140f), GUILayout.Height(ButtonHeight)))
                        {
                            EditorGUIUtility.PingObject(asset);
                        }
                    }

                    if (Event.current.type == EventType.MouseDown &&
                        previewRect.Contains(Event.current.mousePosition))
                    {
                        EditorGUIUtility.PingObject(asset);
                        Event.current.Use();
                    }
                }
            }
        }

        /// <summary>항목의 상태 배지 문자열과 색상을 계산합니다.</summary>
        private void GetItemBadge(PromptItem item, out string badge, out Color color)
        {
            if (_confirmedPaths.ContainsKey(item.id))
            {
                badge = "확정됨";
                color = new Color(0.2f, 0.75f, 0.3f);
                return;
            }

            if (string.IsNullOrEmpty(item.positive))
            {
                badge = "프롬프트 없음";
                color = new Color(0.85f, 0.3f, 0.25f);
                return;
            }

            int count = _candidateCounts.TryGetValue(item.id, out int c) ? c : 0;
            if (count > 0)
            {
                badge = $"후보 {count}개";
                color = new Color(0.35f, 0.6f, 0.95f);
                return;
            }

            badge = "미생성";
            color = Color.gray;
        }

        /// <summary>
        /// 항목을 선택합니다: 종류에 맞는 기본 워크플로 자동 선택(다른 변수 값은 유지),
        /// role 변수에 프롬프트 채움, 기존 후보가 있으면 썸네일 자동 로드.
        /// </summary>
        private void SelectItem(int index)
        {
            if (_document == null || index < 0 || index >= _document.items.Count)
            {
                return;
            }

            _selectedItemIndex = index;
            PromptItem item = _document.items[index];

            // assetType에 맞는 기본 워크플로 자동 선택 (audio→Audio, ui→UI, image→기본 이미지 워크플로).
            // 현재 워크플로가 이미 이 항목 종류에 해당하면(예: 이미지 항목 + StyleChange) 사용자의 선택을 유지한다 —
            // 일괄 생성이 "현재 설정된 워크플로"를 쓰기 때문에 항목을 고를 때마다 기본값으로 되돌아가면 안 된다.
            string wfName = DefaultWorkflowNameFor(item);
            int wfIndex = ItemMatchesWorkflow(item) ? -1 : _workflows.FindIndex(w => w.name == wfName);
            if (wfIndex >= 0 && wfIndex != _selectedWorkflowIndex)
            {
                _selectedWorkflowIndex = wfIndex;
                // 항목 전환 시 role 변수만 갱신하고 나머지 편집 값은 유지한다.
                RebuildVariableStates(preserveValues: true);
            }
            else
            {
                ApplyItemPromptsToVariables();
            }

            // 기존 후보가 있으면 자동 로드해 썸네일로 보여준다.
            List<CandidateInfo> existing = CandidateGenerator.ListCandidates(_settings, item.id);
            _candidates = existing;
            _candidatesItemId = existing.Count > 0 ? item.id : string.Empty;
            _selectedCandidateIndex = -1;
            EditorAudioPreview.Stop();
            Repaint();
        }

        /// <summary>항목 종류별 기본 워크플로 이름을 반환합니다 (audio→Audio, ui→UI, 그 외 설정 기본값).</summary>
        private string DefaultWorkflowNameFor(PromptItem item)
        {
            if (item.assetType == "audio")
            {
                return "Audio";
            }

            if (item.isUI || item.assetType == "ui")
            {
                return "UI";
            }

            return string.IsNullOrEmpty(_settings.defaultImageWorkflow) ? "GenerateImage" : _settings.defaultImageWorkflow;
        }

        /// <summary>목록 배지/확정 미리보기용 항목 상태 캐시(후보 개수·확정본 경로)를 다시 계산합니다.</summary>
        private void RefreshItemStatuses()
        {
            _candidateCounts.Clear();
            _confirmedPaths = CandidateGenerator.GetConfirmedOutputPaths(_settings);
            if (_document == null)
            {
                return;
            }

            foreach (PromptItem item in _document.items)
            {
                _candidateCounts[item.id] = CandidateGenerator.ListCandidates(_settings, item.id).Count;
            }
        }

        // ─────────────────────────── 워크플로/변수 편집 ───────────────────────────

        private void DrawWorkflowSection()
        {
            EditorGUILayout.LabelField("워크플로 / 변수", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_workflows.Count == 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.HelpBox(
                            _bridgeAlive
                                ? "워크플로 목록을 불러오는 중입니다."
                                : "브리지 서버가 실행 중이어야 워크플로 목록을 불러올 수 있습니다. 상단의 [서버 시작]을 눌러주세요.",
                            MessageType.Info);
                        using (new EditorGUI.DisabledScope(!_bridgeAlive || _workflowsLoading))
                        {
                            if (GUILayout.Button("목록 로드", GUILayout.Width(SmallButtonWidth)))
                            {
                                _ = LoadWorkflowsAsync();
                            }
                        }
                    }

                    return;
                }

                string[] workflowNames = _workflows.Select(w => w.name).ToArray();

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    int newIndex = EditorGUILayout.Popup("워크플로",
                        Mathf.Clamp(_selectedWorkflowIndex, 0, workflowNames.Length - 1), workflowNames);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _selectedWorkflowIndex = newIndex;
                        RebuildVariableStates();
                    }

                    if (GUILayout.Button("기본값 복원", GUILayout.Width(90f)))
                    {
                        RebuildVariableStates();
                        _statusMessage = "변수를 원본 JSON 기본값으로 복원했습니다.";
                    }
                }

                DrawMissingNodesWarning();

                foreach (VariableState state in _variableStates)
                {
                    // 표시 조건(visibleWhen)을 만족하지 않는 변수는 그리지 않는다 (값 상태는 유지).
                    if (IsVariableVisible(state.definition))
                    {
                        DrawVariableField(state);
                    }
                }
            }
        }

        /// <summary>
        /// 선택된 워크플로가 요구하는 커스텀 노드가 ComfyUI에 없으면 경고 박스를 그립니다 (D2).
        /// ComfyUI 미연결(_workflowsComfyReachable=false)이면 검증이 생략된 상태이므로 그리지 않습니다.
        /// </summary>
        private void DrawMissingNodesWarning()
        {
            BridgeWorkflowInfo workflow = SelectedWorkflow();
            if (!_workflowsComfyReachable || workflow == null ||
                workflow.missingNodes == null || workflow.missingNodes.Count == 0)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                $"필요한 커스텀 노드 {workflow.missingNodes.Count}개가 ComfyUI에 없습니다: " +
                string.Join(", ", workflow.missingNodes) + "\n" +
                "ComfyUI-Manager에서 해당 노드를 설치한 뒤 ComfyUI를 재시작해주세요. " +
                "설치 전에는 이 워크플로의 생성이 실패합니다.",
                MessageType.Warning);
        }

        /// <summary>
        /// 변수의 visibleWhen 조건(AND)을 현재 bool 변수 값 기준으로 평가합니다.
        /// 조건이 없으면 항상 표시하고, 참조 변수를 찾지 못한 조건은 무시합니다.
        /// </summary>
        private bool IsVariableVisible(BridgeVariable def)
        {
            if (def.visibleWhen == null || def.visibleWhen.Count == 0)
            {
                return true;
            }

            foreach (BridgeVisibleCondition cond in def.visibleWhen)
            {
                VariableState target = _variableStates.FirstOrDefault(s =>
                    s.definition.type == "bool" &&
                    s.definition.nodeId == cond.nodeId &&
                    s.definition.field == cond.field);
                if (target != null && target.boolValue != cond.equals)
                {
                    return false;
                }
            }

            return true;
        }

        private void DrawVariableField(VariableState state)
        {
            BridgeVariable def = state.definition;
            string tooltip = string.IsNullOrEmpty(def.description)
                ? $"노드 #{def.nodeId} · {def.field} ({def.type})"
                : def.description;
            var label = new GUIContent(def.label, tooltip);

            switch (def.type)
            {
                case "int":
                    state.intValue = EditorGUILayout.IntField(label, state.intValue);
                    if (def.min.HasValue || def.max.HasValue)
                    {
                        state.intValue = Mathf.Clamp(state.intValue,
                            def.min.HasValue ? (int)def.min.Value : int.MinValue,
                            def.max.HasValue ? (int)def.max.Value : int.MaxValue);
                    }

                    break;

                case "float":
                    state.floatValue = EditorGUILayout.FloatField(label, state.floatValue);
                    if (def.min.HasValue || def.max.HasValue)
                    {
                        state.floatValue = Mathf.Clamp(state.floatValue,
                            def.min.HasValue ? (float)def.min.Value : float.MinValue,
                            def.max.HasValue ? (float)def.max.Value : float.MaxValue);
                    }

                    break;

                case "bool":
                    state.boolValue = EditorGUILayout.Toggle(label, state.boolValue);
                    break;

                case "image":
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // 로컬 파일 미지정 + 워크플로 기본값도 비어 있으면 "미지정"으로 표시한다 (D5).
                        string display;
                        if (!string.IsNullOrEmpty(state.imageLocalPath))
                        {
                            display = state.imageLocalPath;
                        }
                        else if (!string.IsNullOrEmpty(state.stringValue))
                        {
                            display = $"(서버 기존 파일: {state.stringValue})";
                        }
                        else
                        {
                            display = "(미지정 — [파일 선택]으로 참조 이미지를 지정해주세요)";
                        }

                        EditorGUILayout.LabelField(label, new GUIContent(display, display));
                        if (GUILayout.Button("파일 선택", GUILayout.Width(70f)))
                        {
                            string picked = EditorUtility.OpenFilePanel(
                                "업로드할 이미지 선택", Application.dataPath, "png,jpg,jpeg,webp");
                            if (!string.IsNullOrEmpty(picked))
                            {
                                state.imageLocalPath = picked;
                                GUI.FocusControl(null);
                            }
                        }

                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(state.imageLocalPath)))
                        {
                            if (GUILayout.Button("해제", GUILayout.Width(44f)))
                            {
                                state.imageLocalPath = string.Empty;
                            }
                        }
                    }

                    break;

                default: // string
                    if (def.role == "positive" || def.role == "negative")
                    {
                        EditorGUILayout.LabelField(label);
                        state.stringValue = EditorGUILayout.TextArea(state.stringValue, GUILayout.MinHeight(40f));
                    }
                    else if (def.options != null && def.options.Count > 0)
                    {
                        DrawOptionsPopup(state, label);
                    }
                    else
                    {
                        state.stringValue = EditorGUILayout.TextField(label, state.stringValue);
                    }

                    break;
            }
        }

        /// <summary>
        /// options가 있는 string 변수를 드롭다운으로 그립니다.
        /// 현재 값이 목록에 없으면 맨 앞에 "(현재: 값)" 항목으로 표시합니다.
        /// </summary>
        private static void DrawOptionsPopup(VariableState state, GUIContent label)
        {
            List<string> options = state.definition.options;
            int currentIndex = options.IndexOf(state.stringValue);

            if (currentIndex >= 0)
            {
                var contents = new GUIContent[options.Count];
                for (int i = 0; i < options.Count; i++)
                {
                    contents[i] = new GUIContent(options[i]);
                }

                int newIndex = EditorGUILayout.Popup(label, currentIndex, contents);
                state.stringValue = options[Mathf.Clamp(newIndex, 0, options.Count - 1)];
            }
            else
            {
                // 현재 값이 설치 목록에 없음(D1: 다른 PC에 해당 모델/파일 미설치 등)
                // → 경고색(빨간 배경)으로 표시하고, 맨 앞에 "(현재: 값)"으로 유지하며 선택 가능하게 한다.
                var contents = new GUIContent[options.Count + 1];
                contents[0] = new GUIContent(
                    $"(현재: {state.stringValue})",
                    "현재 값이 ComfyUI 설치 목록에 없습니다. 이대로 생성하면 실패합니다. " +
                    "설치된 파일 목록에서 선택해주세요.");
                for (int i = 0; i < options.Count; i++)
                {
                    contents[i + 1] = new GUIContent(options[i]);
                }

                Color previousColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                int newIndex = EditorGUILayout.Popup(label, 0, contents);
                GUI.backgroundColor = previousColor;

                if (newIndex > 0)
                {
                    state.stringValue = options[newIndex - 1];
                }
            }
        }

        /// <summary>
        /// 선택된 워크플로의 매니페스트로 변수 상태를 다시 만듭니다.
        /// </summary>
        /// <param name="preserveValues">true면 같은 키(nodeId.field)의 기존 편집 값을 유지합니다
        /// (워크플로 목록 재로드 시 사용). false면 원본 JSON 기본값으로 초기화합니다.</param>
        private void RebuildVariableStates(bool preserveValues = false)
        {
            var previous = preserveValues
                ? _variableStates.ToDictionary(s => s.definition.Key, s => s)
                : null;

            _variableStates.Clear();
            BridgeWorkflowInfo workflow = SelectedWorkflow();
            if (workflow == null)
            {
                return;
            }

            // bool(토글) 변수는 항상 최상단에 배치하고, 나머지는 매니페스트 순서를 유지한다.
            IEnumerable<BridgeVariable> ordered = workflow.variables
                .Where(v => v.type == "bool")
                .Concat(workflow.variables.Where(v => v.type != "bool"));

            foreach (BridgeVariable def in ordered)
            {
                var state = new VariableState { definition = def };
                object value = def.defaultValue;

                switch (def.type)
                {
                    case "int":
                        state.intValue = value is long l ? (int)l : value is double d ? (int)d : 0;
                        break;
                    case "float":
                        state.floatValue = value is double fd ? (float)fd : value is long fl ? fl : 0f;
                        break;
                    case "bool":
                        state.boolValue = value is bool b && b;
                        break;
                    default:
                        state.stringValue = value as string ?? string.Empty;
                        break;
                }

                // 재로드 시 기존 편집 값을 유지한다 (타입이 같은 경우만).
                if (previous != null && previous.TryGetValue(def.Key, out VariableState old) &&
                    old.definition.type == def.type)
                {
                    state.stringValue = old.stringValue;
                    state.intValue = old.intValue;
                    state.floatValue = old.floatValue;
                    state.boolValue = old.boolValue;
                    state.imageLocalPath = old.imageLocalPath;
                }

                _variableStates.Add(state);
            }

            ApplyItemPromptsToVariables();
        }

        /// <summary>
        /// 값이 비어 있는 image 타입 변수의 라벨 목록을 반환합니다 (D5).
        /// 변수 타입은 매니페스트(variables.json)의 type 필드로 판정하며,
        /// 표시 조건(visibleWhen)을 만족하지 않아 화면에 없는 변수는 제외합니다.
        /// 로컬 파일도, 서버 파일명(워크플로 기본값)도 없는 변수만 "미지정"으로 봅니다.
        /// </summary>
        private List<string> MissingImageVariableLabels()
        {
            var labels = new List<string>();
            foreach (VariableState state in _variableStates)
            {
                BridgeVariable def = state.definition;
                if (def.type != "image" || !IsVariableVisible(def))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(state.imageLocalPath) && string.IsNullOrEmpty(state.stringValue))
                {
                    labels.Add(string.IsNullOrEmpty(def.label) ? $"#{def.nodeId}.{def.field}" : def.label);
                }
            }

            return labels;
        }

        /// <summary>선택된 항목의 positive/negative 프롬프트를 role 변수에 채웁니다 (사용자 수정 가능).</summary>
        /// <param name="overwriteWithEmpty">true면 항목의 프롬프트가 비어 있어도 그대로 덮어씁니다
        /// (항목 편집 중 동기화용). false면 비어 있는 값은 건드리지 않습니다.</param>
        private void ApplyItemPromptsToVariables(bool overwriteWithEmpty = false)
        {
            PromptItem item = SelectedItem();
            if (item == null)
            {
                return;
            }

            foreach (VariableState state in _variableStates)
            {
                if (state.definition.role == "positive" && (overwriteWithEmpty || !string.IsNullOrEmpty(item.positive)))
                {
                    state.stringValue = item.positive;
                }
                else if (state.definition.role == "negative" && (overwriteWithEmpty || !string.IsNullOrEmpty(item.negative)))
                {
                    state.stringValue = item.negative;
                }
            }
        }

        // ─────────────────────────── 생성 ───────────────────────────

        private void DrawGenerateSection()
        {
            if (_generating)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect barRect = EditorGUILayout.GetControlRect(GUILayout.Height(18f));
                    string barLabel = _batchRunning
                        ? $"일괄 생성 중 ({_batchCurrent}/{_batchTotal}) {_batchCurrentLabel}... {(int)(_progress * 100f)}%"
                        : $"생성 중... {(int)(_progress * 100f)}%";
                    EditorGUI.ProgressBar(barRect, _progress, barLabel);
                    if (GUILayout.Button("취소", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                    {
                        CancelGeneration();
                    }
                }

                return;
            }

            // PromptSet 항목이 없어도 변수 UI의 프롬프트만으로 생성할 수 있다.
            // 워크플로 목록(브리지 서버)이 없거나 참조 이미지가 미지정일 때만 비활성화하고, 사유를 안내한다.
            List<string> missingImages = MissingImageVariableLabels();
            bool canGenerate = _workflows.Count > 0 && missingImages.Count == 0;
            if (_workflows.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "생성 버튼을 사용하려면 브리지 서버가 실행 중이고 워크플로 목록이 로드되어야 합니다. " +
                    "상단의 [서버 시작]을 눌러주세요.", MessageType.Info);
            }
            else if (missingImages.Count > 0)
            {
                // D5: image 타입 변수가 비어 있으면 제출 전에 막는다.
                // (여기서 막으므로 브리지 /preflight의 "ComfyUI에 없는 파일/값" 안내와 중복되지 않는다.)
                EditorGUILayout.HelpBox(
                    $"참조 이미지를 선택해주세요: {string.Join(", ", missingImages)}\n" +
                    "위 변수의 [파일 선택] 버튼으로 이미지를 지정하면 생성 시 자동으로 ComfyUI에 업로드됩니다.",
                    MessageType.Warning);
            }

            // 후보 개수는 설정 에셋(candidateCount)에 저장된다. 생성할 때마다 조정하는 값이라
            // Tools/MCP/Settings까지 가지 않고 이 창에서 바로 바꿀 수 있게 노출한다.
            EditorGUI.BeginChangeCheck();
            int newCount = EditorGUILayout.IntSlider(
                new GUIContent("후보 개수",
                    "한 번에 생성할 후보 이미지/사운드 개수입니다. 시드를 1씩 올려가며 생성하므로 " +
                    "개수가 많을수록 생성 시간과 VRAM 사용량이 늘어납니다."),
                Mathf.Clamp(_settings.candidateCount, MinCandidateCount, MaxCandidateCount),
                MinCandidateCount, MaxCandidateCount);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_settings, "MCP Tools 후보 개수 변경");
                _settings.candidateCount = newCount;
                EditorUtility.SetDirty(_settings);
            }

            // Play Mode·컴파일 중에는 생성(단건/일괄) 전체를 막는다 (R2).
            using (new EditorGUI.DisabledScope(!canGenerate || _blockedReason != null))
            {
                int candidateCount = Mathf.Max(1, _settings.candidateCount);

                string label = _candidates.Count > 0 && _candidatesItemId == CurrentItemId()
                    ? $"재생성 (새 시드로 {candidateCount}개)"
                    : $"후보 {candidateCount}개 생성";
                if (GUILayout.Button(new GUIContent(label, "선택된 항목 1개만 생성합니다."),
                        GUILayout.Height(PrimaryButtonHeight)))
                {
                    StartGeneration();
                }

                List<PromptItem> checkedTargets = CheckedTargets();
                List<PromptItem> unrendered = UnrenderedTargets();

                using (new EditorGUI.DisabledScope(_document == null))
                {
                    // 두 일괄 버튼은 라벨이 길어 좁은 창에서 한 줄에 두면 글자가 잘린다. 폭이 부족하면 세로로 쌓는다.
                    bool stackBatchButtons = RightContentWidth() < 520f;
                    using (stackBatchButtons
                               ? (IDisposable)new EditorGUILayout.VerticalScope()
                               : new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(checkedTargets.Count == 0))
                        {
                            if (GUILayout.Button(
                                    new GUIContent($"선택 항목 생성 ({checkedTargets.Count}개 × {candidateCount})",
                                        "목록에서 체크한 항목을 현재 워크플로/변수 설정으로 순서대로 생성합니다."),
                                    GUILayout.Height(PrimaryButtonHeight), GUILayout.MinWidth(60f)))
                            {
                                StartBatchGeneration(checkedTargets, "선택 항목");
                            }
                        }

                        using (new EditorGUI.DisabledScope(unrendered.Count == 0))
                        {
                            if (GUILayout.Button(
                                    new GUIContent($"미생성 전체 생성 ({unrendered.Count}개 × {candidateCount})",
                                        "후보가 없고 확정되지 않은 항목을 모두 생성합니다."),
                                    GUILayout.Height(PrimaryButtonHeight), GUILayout.MinWidth(60f)))
                            {
                                StartBatchGeneration(unrendered, "미생성 항목");
                            }
                        }
                    }
                }

                if (_document != null)
                {
                    EditorGUILayout.LabelField(
                        "일괄 생성은 위 워크플로와 변수 설정을 그대로 사용합니다 (프롬프트만 항목별 값으로 대체).",
                        EditorStyles.miniLabel);
                }
            }

            // 일괄 생성 완료 후 ComfyUI 모델 언로드 여부 (설정 에셋과 연동)
            EditorGUI.BeginChangeCheck();
            bool unload = EditorGUILayout.ToggleLeft(
                new GUIContent("일괄 생성 완료 후 모델 언로드 (VRAM 확보)",
                    "일괄 생성이 끝나면 ComfyUI에 로드된 모델을 언로드해 VRAM/메모리를 확보합니다. " +
                    "기본값은 끔(모델 유지)이며, 켜면 다음 생성 때 모델을 다시 로드하느라 " +
                    "10~40초가 추가될 수 있습니다. VRAM이 부족할 때만 켜세요. (단건 생성에는 적용되지 않습니다.)"),
                _settings.unloadModelsAfterBatch);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_settings, "MCP Tools 설정 변경");
                _settings.unloadModelsAfterBatch = unload;
                EditorUtility.SetDirty(_settings);
            }
        }

        /// <summary>
        /// 설정이 켜져 있으면 브리지 /free 로 ComfyUI 모델을 언로드합니다.
        /// 일괄 생성 종료 시에만 호출합니다 — 단건 생성마다 호출하면 다음 회차에 모델 재로드 대기가 붙습니다.
        /// 실패해도 경고 로그만 남기고 생성 흐름에는 영향을 주지 않습니다.
        /// </summary>
        private async Task TryFreeMemoryAsync()
        {
            if (_settings == null || !_settings.unloadModelsAfterBatch)
            {
                return;
            }

            try
            {
                using (var client = new BridgeClient(_settings))
                {
                    await client.FreeMemoryAsync();
                }

                Debug.Log("[MCPTools] 생성 완료 후 ComfyUI 모델을 언로드했습니다.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCPTools] 생성 후 모델 언로드에 실패했습니다 (생성 결과에는 영향 없음): {e.Message}");
            }
        }

        // ─────────────────────────── 일괄 생성 ───────────────────────────

        /// <summary>
        /// 대상 항목들을 현재 워크플로/변수 설정으로 일괄 생성합니다.
        /// positive 프롬프트가 비어 있는 항목은 제외하고, 이미 후보가 있거나 확정된 항목이 섞여 있으면
        /// 기존 후보가 삭제된다는 점을 확인 다이얼로그로 안내합니다.
        /// </summary>
        /// <param name="targets">생성 대상 항목 목록.</param>
        /// <param name="scopeLabel">확인 다이얼로그/상태 메시지에 쓸 대상 설명 (예: "선택 항목").</param>
        private void StartBatchGeneration(List<PromptItem> targets, string scopeLabel)
        {
            if (_document == null || _generating || targets == null)
            {
                return;
            }

            if (!_bridgeAlive || !_comfyAlive)
            {
                EditorUtility.DisplayDialog("서버 미연결",
                    "일괄 생성에는 브리지 서버와 ComfyUI가 모두 실행 중이어야 합니다.\n" +
                    "상단의 [서버 시작] 버튼과 ComfyUI 실행 상태를 확인해주세요.", "확인");
                return;
            }

            RefreshItemStatuses();

            List<PromptItem> withPrompt = targets.Where(it => !string.IsNullOrEmpty(it.positive)).ToList();
            int skipped = targets.Count - withPrompt.Count;
            if (withPrompt.Count == 0)
            {
                _statusMessage = $"일괄 생성할 항목이 없습니다 ({scopeLabel}: positive 프롬프트가 비어 있는 항목 {skipped}개 제외).";
                return;
            }

            BridgeWorkflowInfo workflow = SelectedWorkflow();
            int regenerate = withPrompt.Count(it => !IsUnrenderedTarget(it));

            string summary =
                $"{scopeLabel} {withPrompt.Count}개를 워크플로 \"{(workflow != null ? workflow.name : "항목별 기본")}\"와 " +
                $"현재 변수 설정으로 생성합니다.\n항목당 후보 {Mathf.Max(1, _settings.candidateCount)}개.";
            if (skipped > 0)
            {
                summary += $"\n\npositive 프롬프트가 비어 제외되는 항목: {skipped}개";
            }

            if (regenerate > 0)
            {
                summary += $"\n\n이미 후보가 있거나 확정된 항목 {regenerate}개는 기존 후보가 삭제되고 새로 생성됩니다.";
            }

            if (!EditorUtility.DisplayDialog("일괄 생성", $"{summary}\n\n계속할까요?", "생성", "취소"))
            {
                return;
            }

            _ = RunBatchGenerationAsync(withPrompt, scopeLabel);
        }

        /// <summary>
        /// 대상 항목들을 순차로 생성합니다 (항목당 후보 N개, 현재 선택된 워크플로와 변수 값 사용).
        /// 실패한 항목은 기록만 하고 다음 항목을 계속 진행하며, 완료 후 실패 목록을 요약합니다.
        /// 창을 닫으면 취소됩니다.
        /// </summary>
        /// <param name="targets">생성 대상 항목 목록.</param>
        /// <param name="scopeLabel">상태 메시지에 쓸 대상 설명.</param>
        private async Task RunBatchGenerationAsync(List<PromptItem> targets, string scopeLabel)
        {
            EditorAudioPreview.Stop();
            _generating = true;
            _batchRunning = true;
            _batchTotal = targets.Count;
            _progress = 0f;
            _selectedCandidateIndex = -1;
            _cancelSource = new CancellationTokenSource();
            var failures = new List<string>();
            int done = 0;

            try
            {
                // 화면에 설정된 워크플로와 변수 값을 그대로 모든 항목에 적용한다.
                // role(positive/negative) 변수는 제외해야 항목별 프롬프트가 주입된다.
                // 참조 이미지 업로드도 여기서 1회만 수행하고 결과 파일명을 재사용한다.
                BridgeWorkflowInfo batchWorkflow = SelectedWorkflow();
                Dictionary<string, object> batchVariables =
                    await BuildVariablesAsync(_cancelSource.Token, excludePromptRoles: true);

                for (int i = 0; i < targets.Count; i++)
                {
                    _cancelSource.Token.ThrowIfCancellationRequested();
                    PromptItem item = targets[i];
                    _batchCurrent = i + 1;
                    _batchCurrentLabel = $"{item.id} {item.name}";
                    _statusMessage = $"일괄 생성 중({scopeLabel}): {item.id} ({item.name})";

                    int captured = i;
                    var progress = new Progress<float>(p =>
                    {
                        _progress = (captured + Mathf.Clamp01(p)) / _batchTotal;
                        Repaint();
                    });

                    try
                    {
                        string wfName = batchWorkflow != null ? batchWorkflow.name : DefaultWorkflowNameFor(item);

                        List<CandidateInfo> generated = await CandidateGenerator.GenerateAsync(
                            _settings, item, wfName, batchVariables, null, interactive: true,
                            progress: progress, ct: _cancelSource.Token);
                        _candidateCounts[item.id] = generated.Count;
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{item.id} ({item.name}): {e.Message}");
                    }

                    Repaint();
                }

                _statusMessage = failures.Count == 0
                    ? $"일괄 생성 완료({scopeLabel}): {done}/{_batchTotal}개 항목 성공."
                    : $"일괄 생성 완료({scopeLabel}): 성공 {done}개, 실패 {failures.Count}개.";

                if (failures.Count > 0)
                {
                    EditorUtility.DisplayDialog("일괄 생성 — 실패 항목 요약",
                        $"성공 {done}개 / 실패 {failures.Count}개\n\n실패 항목:\n" +
                        string.Join("\n", failures), "확인");
                }
            }
            catch (OperationCanceledException)
            {
                _statusMessage = $"일괄 생성을 취소했습니다 (완료 {done}/{_batchTotal}).";
            }
            catch (Exception e)
            {
                _statusMessage = "일괄 생성 실패.";
                EditorUtility.DisplayDialog("일괄 생성 실패", e.Message, "확인");
            }
            finally
            {
                _generating = false;
                _batchRunning = false;
                _batchCurrentLabel = string.Empty;

                // 실제로 생성이 1건이라도 시도되었으면 설정에 따라 모델을 언로드한다 (실패해도 무시).
                if (done > 0 || failures.Count > 0)
                {
                    _ = TryFreeMemoryAsync();
                }

                if (_cancelSource != null)
                {
                    _cancelSource.Dispose();
                    _cancelSource = null;
                }

                RefreshItemStatuses();

                // 현재 선택 항목의 후보가 새로 생겼으면 썸네일을 갱신한다.
                PromptItem current = SelectedItem();
                if (current != null)
                {
                    List<CandidateInfo> existing = CandidateGenerator.ListCandidates(_settings, current.id);
                    if (existing.Count > 0)
                    {
                        _candidates = existing;
                        _candidatesItemId = current.id;
                    }
                }

                Repaint();
            }
        }

        private void StartGeneration()
        {
            // PromptSet 항목이 없으면 변수 UI의 프롬프트로 생성하는 수동 항목을 만든다.
            PromptItem item = SelectedItem() ?? CreateManualItem();
            if (item == null)
            {
                return;
            }

            if (!_bridgeAlive)
            {
                EditorUtility.DisplayDialog("브리지 서버 미기동",
                    $"브리지 서버({_settings.bridgeServerUrl})가 응답하지 않습니다.\n\n" +
                    "상단의 [서버 시작] 버튼으로 브리지 서버를 시작해주세요. (Python 3 필요)", "확인");
                return;
            }

            if (!_comfyAlive)
            {
                EditorUtility.DisplayDialog("ComfyUI 미연결",
                    $"ComfyUI 서버({_settings.comfyUIServerUrl})가 응답하지 않습니다.\n\n" +
                    "ComfyUI를 실행한 뒤 다시 시도해주세요.", "확인");
                return;
            }

            _ = RunGenerationAsync(item);
        }

        private async Task RunGenerationAsync(PromptItem item)
        {
            EditorAudioPreview.Stop();
            _generating = true;
            _progress = 0f;
            _selectedCandidateIndex = -1;
            _statusMessage = $"후보 생성 중: {item.id} ({item.name})";
            _cancelSource = new CancellationTokenSource();

            var progress = new Progress<float>(p =>
            {
                _progress = p;
                Repaint();
            });

            try
            {
                Dictionary<string, object> variables = await BuildVariablesAsync(_cancelSource.Token);

                BridgeWorkflowInfo workflow = SelectedWorkflow();
                List<CandidateInfo> candidates = await CandidateGenerator.GenerateAsync(
                    _settings, item, workflow != null ? workflow.name : null, variables, null,
                    interactive: true, progress: progress, ct: _cancelSource.Token);

                _candidates = candidates;
                _candidatesItemId = item.id;
                _candidateCounts[item.id] = candidates.Count;
                _statusMessage = $"후보 {candidates.Count}개 생성 완료 — 썸네일을 클릭해 선택 후 [확정]하세요.";
            }
            catch (OperationCanceledException)
            {
                _statusMessage = "후보 생성을 취소했습니다.";
            }
            catch (Exception e)
            {
                _statusMessage = "후보 생성 실패.";
                EditorUtility.DisplayDialog("후보 생성 실패", e.Message, "확인");
            }
            finally
            {
                _generating = false;
                if (_cancelSource != null)
                {
                    _cancelSource.Dispose();
                    _cancelSource = null;
                }

                // 단건 생성 후에는 모델을 언로드하지 않는다 — "후보 4개 생성"을 연속으로 누르는 흐름에서
                // 회차마다 체크포인트 재로드(10~40초)가 붙기 때문. 언로드는 일괄 생성 종료 시에만 수행한다.
                Repaint();
            }
        }

        /// <summary>
        /// 변수 편집 상태를 브리지 /generate용 변수 맵으로 변환합니다.
        /// image 타입은 로컬 파일이 지정된 경우 먼저 업로드해 서버 파일명으로 치환합니다.
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <param name="excludePromptRoles">true면 role이 positive/negative인 변수를 제외합니다
        /// (일괄 생성 시 항목별 프롬프트 자동 주입을 방해하지 않기 위함).</param>
        private async Task<Dictionary<string, object>> BuildVariablesAsync(
            CancellationToken ct, bool excludePromptRoles = false)
        {
            var variables = new Dictionary<string, object>();

            using (var client = new BridgeClient(_settings))
            {
                foreach (VariableState state in _variableStates)
                {
                    BridgeVariable def = state.definition;
                    if (excludePromptRoles && (def.role == "positive" || def.role == "negative"))
                    {
                        continue;
                    }

                    switch (def.type)
                    {
                        case "int":
                            variables[def.Key] = state.intValue;
                            break;
                        case "float":
                            variables[def.Key] = (double)state.floatValue;
                            break;
                        case "bool":
                            variables[def.Key] = state.boolValue;
                            break;
                        case "image":
                            // 표시 조건을 만족하지 않는 참조 이미지(예: '참조 이미지 사용' 꺼짐)는
                            // 브리지가 해당 분기를 제거하므로 업로드하지 않는다.
                            if (!string.IsNullOrEmpty(state.imageLocalPath) && IsVariableVisible(def))
                            {
                                _statusMessage = $"이미지 업로드 중: {Path.GetFileName(state.imageLocalPath)}";
                                Repaint();
                                string uploadedName = await client.UploadImageAsync(state.imageLocalPath, ct);
                                variables[def.Key] = uploadedName;
                            }
                            // 로컬 파일 미지정 시 원본 JSON의 기존 파일명을 유지한다 (덮어쓰지 않음).
                            break;
                        default:
                            variables[def.Key] = state.stringValue ?? string.Empty;
                            break;
                    }
                }
            }

            return variables;
        }

        private void CancelGeneration()
        {
            if (_cancelSource != null)
            {
                _cancelSource.Cancel();
            }
        }

        // ─────────────────────────── 후보 표시/확정 ───────────────────────────

        private void DrawCandidateSection()
        {
            if (_candidates.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField($"후보 ({_candidatesItemId})", EditorStyles.boldLabel);

            // 측정한 표시 폭 안에서만 격자를 만든다. 폭이 한 칸도 못 담을 만큼 좁으면 썸네일 자체를 줄여
            // 가로로 넘치지 않게 한다 (가로 스크롤 없이 창 폭에 맞춤).
            float rightWidth = RightContentWidth();
            float cellSize = Mathf.Min(ThumbnailSize, Mathf.Max(48f, rightWidth - 16f));
            int columns = Mathf.Max(1, Mathf.FloorToInt(rightWidth / (cellSize + 16f)));
            for (int row = 0; row * columns < _candidates.Count; row++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;
                        if (index >= _candidates.Count)
                        {
                            break;
                        }

                        DrawCandidateCell(_candidates[index], index, cellSize);
                    }

                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           _selectedCandidateIndex < 0 || _generating || _blockedReason != null))
                {
                    if (GUILayout.Button("확정", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        ConfirmSelected();
                    }
                }
            }
        }

        /// <param name="cellSize">썸네일 한 변의 크기 (창 폭에 맞춰 축소될 수 있습니다).</param>
        private void DrawCandidateCell(CandidateInfo candidate, int index, float cellSize)
        {
            bool selected = index == _selectedCandidateIndex;
            bool isAudio = IsAudioCandidate(candidate.path);

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(cellSize + 8f)))
            {
                Rect rect = GUILayoutUtility.GetRect(cellSize, cellSize,
                    GUILayout.Width(cellSize), GUILayout.Height(cellSize));

                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f),
                        selected ? new Color(0.24f, 0.49f, 0.90f) : new Color(0f, 0f, 0f, 0.25f));
                }

                if (isAudio)
                {
                    DrawAudioCell(rect, candidate);
                }
                else
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(candidate.path);
                    if (texture != null)
                    {
                        GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        GUI.Box(rect, Path.GetFileName(candidate.path));
                    }
                }

                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    _selectedCandidateIndex = index;
                    Event.current.Use();
                    Repaint();
                }

                EditorGUILayout.LabelField($"시드 {candidate.seed}", EditorStyles.miniLabel,
                    GUILayout.Width(cellSize));

                if (!isAudio)
                {
                    if (GUILayout.Button("크게 보기", GUILayout.Width(cellSize), GUILayout.Height(ButtonHeight)))
                    {
                        CandidatePreviewWindow.Open(candidate.path, candidate.seed);
                    }
                }
            }
        }

        /// <summary>오디오 후보 셀을 그립니다 (파일명/시드 + 재생/정지 토글 버튼).</summary>
        private void DrawAudioCell(Rect rect, CandidateInfo candidate)
        {
            GUI.Box(rect, string.Empty);

            var labelRect = new Rect(rect.x + 4f, rect.y + 8f, rect.width - 8f, rect.height * 0.5f);
            GUI.Label(labelRect,
                $"♪ 오디오\n{Path.GetFileName(candidate.path)}\n시드 {candidate.seed}",
                EditorStyles.wordWrappedMiniLabel);

            bool playing = EditorAudioPreview.IsPlaying(candidate.path);
            var buttonRect = new Rect(rect.x + rect.width * 0.2f, rect.y + rect.height - 34f,
                rect.width * 0.6f, 26f);

            if (GUI.Button(buttonRect, playing ? "■ 정지" : "▶ 재생"))
            {
                if (playing)
                {
                    EditorAudioPreview.Stop();
                }
                else
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(candidate.path);
                    if (!EditorAudioPreview.Play(clip, candidate.path, out string error))
                    {
                        _statusMessage = error;
                    }
                }

                Repaint();
            }
        }

        /// <summary>확장자로 오디오 후보 여부를 판정합니다.</summary>
        private static bool IsAudioCandidate(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".flac" || ext == ".wav" || ext == ".mp3" || ext == ".ogg";
        }

        private void ConfirmSelected()
        {
            if (_selectedCandidateIndex < 0 || _selectedCandidateIndex >= _candidates.Count)
            {
                return;
            }

            try
            {
                string outputPath = CandidateGenerator.ConfirmCandidate(
                    _settings, _candidatesItemId, _candidates[_selectedCandidateIndex].path);
                _statusMessage = $"확정 완료: {outputPath}";
                ShowNotification(new GUIContent("후보를 확정했습니다."));

                // 목록 배지를 즉시 갱신하고 다음 미확정 항목으로 이동한다.
                RefreshItemStatuses();
                MoveToNextUnconfirmedItem();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("확정 실패", e.Message, "확인");
            }
        }

        /// <summary>
        /// 확정 후 다음 "미확정" 항목으로 선택을 이동합니다 (현재 위치에서 순환 탐색).
        /// 모든 항목이 확정되었으면 4단계 진행 안내를 표시합니다.
        /// </summary>
        private void MoveToNextUnconfirmedItem()
        {
            if (_document == null || _document.items.Count == 0)
            {
                return; // 수동 생성(manual_) 확정 등 PromptSet 없이 쓰는 경우
            }

            int n = _document.items.Count;
            int start = Mathf.Clamp(_selectedItemIndex, 0, n - 1);
            for (int offset = 1; offset <= n; offset++)
            {
                int index = (start + offset) % n;
                if (!_confirmedPaths.ContainsKey(_document.items[index].id))
                {
                    SelectItem(index);
                    _statusMessage += $"  → 다음 항목으로 이동: {_document.items[index].id}";
                    return;
                }
            }

            _statusMessage = "모든 항목 확정 완료 — 4단계(AssetApplier)로 진행하세요.";
            EditorUtility.DisplayDialog("ComfyUI 생성",
                "모든 항목이 확정되었습니다.\n4단계(Tools/MCP/4. Asset Applier)로 진행하세요.", "확인");
        }

        private void DrawBottomBar()
        {
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // 상태 메시지에는 긴 에셋 경로가 들어간다. 폭을 창에 맞춰 고정해 창을 넓히지 않게 한다.
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel,
                        GUILayout.Width(Mathf.Max(200f, position.width - 24f)));
                }
            }
        }

        // ─────────────────────────── 헬퍼 ───────────────────────────

        private void RefreshPromptSetPaths()
        {
            // 새 하위 폴더(2_PromptSet)와 구 위치(Docs 루트)를 함께 훑는다. 최신 파일이 앞에 온다.
            _promptSetPaths = MCPToolFolders.FindDocuments(
                MCPToolFolders.DocsRoot(_settings), MCPToolFolders.PromptSetFolder, "PromptSet_*.json");
        }

        private void LoadSelectedPromptSet()
        {
            if (!ConfirmDiscardChanges("다른 PromptSet을 로드하면"))
            {
                return;
            }

            string path = _promptSetPaths[Mathf.Clamp(_selectedSetIndex, 0, _promptSetPaths.Length - 1)];
            try
            {
                var dict = MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
                PromptSetDocument doc = PromptSetDocument.FromDictionary(dict);
                if (doc == null || doc.items.Count == 0)
                {
                    EditorUtility.DisplayDialog("ComfyUI 생성",
                        $"PromptSet JSON에서 항목을 읽지 못했습니다: {path}", "확인");
                    return;
                }

                _document = doc;
                _documentPath = path;
                _documentDirty = false;
                _checkedItemIds.Clear();
                _candidates.Clear();
                _candidatesItemId = string.Empty;
                _selectedCandidateIndex = -1;
                _statusMessage = $"로드 완료: {path} (항목 {doc.items.Count}개)";
                RefreshItemStatuses();
                SelectItem(0);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("ComfyUI 생성", $"PromptSet JSON 로드 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        private PromptItem SelectedItem()
        {
            if (_document == null || _document.items.Count == 0)
            {
                return null;
            }

            return _document.items[Mathf.Clamp(_selectedItemIndex, 0, _document.items.Count - 1)];
        }

        /// <summary>선택된 항목 id, 항목이 없으면 수동 생성 id를 반환합니다 (재생성 라벨 판정용).</summary>
        private string CurrentItemId()
        {
            PromptItem item = SelectedItem();
            return item != null ? item.id : ManualItemId();
        }

        private string ManualItemId()
        {
            BridgeWorkflowInfo workflow = SelectedWorkflow();
            return workflow != null ? $"manual_{workflow.name}" : "manual";
        }

        /// <summary>
        /// PromptSet 없이 변수 UI의 프롬프트만으로 생성할 때 사용할 수동 항목을 만듭니다.
        /// 후보는 Assets/Generated/3_Candidates/manual_{워크플로}/에 저장됩니다.
        /// </summary>
        private PromptItem CreateManualItem()
        {
            BridgeWorkflowInfo workflow = SelectedWorkflow();
            if (workflow == null)
            {
                return null;
            }

            string positive = string.Empty;
            string negative = string.Empty;
            foreach (VariableState state in _variableStates)
            {
                if (state.definition.role == "positive")
                {
                    positive = state.stringValue;
                }
                else if (state.definition.role == "negative")
                {
                    negative = state.stringValue;
                }
            }

            return new PromptItem
            {
                id = ManualItemId(),
                name = $"수동 생성 ({workflow.name})",
                assetType = workflow.name == "Audio" ? "audio" : workflow.name == "UI" ? "ui" : "image",
                positive = positive,
                negative = negative
            };
        }

        private BridgeWorkflowInfo SelectedWorkflow()
        {
            if (_workflows.Count == 0)
            {
                return null;
            }

            return _workflows[Mathf.Clamp(_selectedWorkflowIndex, 0, _workflows.Count - 1)];
        }
    }

    /// <summary>
    /// 3단계 생성 창의 항목을 별도 창에서 작성/수정하는 보조 창입니다.
    /// [저장]을 누르면 생성 창의 목록에 새 항목으로 추가되거나 같은 ID의 기존 항목이 갱신됩니다.
    /// PromptSet JSON 파일에 남기려면 생성 창의 [저장]을 눌러야 합니다.
    /// </summary>
    public class PromptItemEditWindow : EditorWindow
    {
        private static readonly string[] AssetTypeOptions = { "image", "ui", "audio" };

        [SerializeField] private ComfyUIGeneratorWindow _owner;
        [SerializeField] private PromptItem _item;
        [SerializeField] private bool _isNew;

        private Vector2 _scroll;

        // 대상 오브젝트 드롭다운 캐시: 어떤 프리팹의 계층을 읽어 만든 목록인지와 그 경로/표시 라벨.
        // 프리팹이 바뀌면 다시 만든다 (도메인 리로드 후에도 _cachedPrefabPath가 비어 자동 재생성).
        private string _cachedPrefabPath;
        private List<string> _objectPaths = new List<string>();
        private GUIContent[] _objectLabels = new GUIContent[0];

        /// <summary>항목 편집 창을 엽니다.</summary>
        /// <param name="owner">편집 결과를 반영할 생성 창.</param>
        /// <param name="existing">수정할 기존 항목. null이면 새 항목을 작성합니다.</param>
        public static void Open(ComfyUIGeneratorWindow owner, PromptItem existing)
        {
            var window = GetWindow<PromptItemEditWindow>(true);
            window.titleContent = new GUIContent(existing == null ? "항목 추가" : "항목 편집");
            window.minSize = new Vector2(420f, 440f);
            window._owner = owner;
            window._isNew = existing == null;

            // 목록의 항목을 직접 건드리지 않도록 복사본을 편집한다 ([취소] 시 원본 유지).
            window._item = existing == null
                ? (owner != null ? owner.CreateItemDraft() : new PromptItem())
                : Clone(existing);
            window.Show();
        }

        private static PromptItem Clone(PromptItem source)
        {
            return new PromptItem
            {
                id = source.id,
                name = source.name,
                assetType = source.assetType,
                isUI = source.isUI,
                targetPrefabPath = source.targetPrefabPath,
                targetObjectPath = source.targetObjectPath,
                description = source.description,
                positive = source.positive,
                negative = source.negative
            };
        }

        private void OnGUI()
        {
            if (_item == null)
            {
                EditorGUILayout.LabelField("편집할 항목이 없습니다. 창을 닫고 다시 열어주세요.", EditorStyles.miniLabel);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("ID", _item.id, EditorStyles.boldLabel);
            _item.name = EditorGUILayout.TextField("이름", _item.name);

            int typeIndex = Mathf.Max(0, Array.IndexOf(AssetTypeOptions, _item.assetType));
            _item.assetType = AssetTypeOptions[EditorGUILayout.Popup("종류", typeIndex, AssetTypeOptions)];
            _item.isUI = EditorGUILayout.Toggle("UI 여부", _item.isUI);

            DrawTargetFields();

            _item.description = EditorGUILayout.TextField(
                new GUIContent("설명", "용도 메모 (생성에는 쓰이지 않습니다)"), _item.description);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Positive 프롬프트");
            _item.positive = EditorGUILayout.TextArea(_item.positive, GUILayout.MinHeight(90f));
            EditorGUILayout.LabelField("Negative 프롬프트");
            _item.negative = EditorGUILayout.TextArea(_item.negative, GUILayout.MinHeight(60f));

            using (new EditorGUI.DisabledScope(_owner == null))
            {
                if (GUILayout.Button(
                        new GUIContent("생성 창의 현재 프롬프트 가져오기",
                            "생성 창의 워크플로 변수(positive/negative)에 입력해 둔 값을 이 항목에 채웁니다."),
                        EditorStyles.miniButton))
                {
                    _owner.CopyVariablePromptsTo(_item);
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.EndScrollView();

            if (string.IsNullOrEmpty(_item.positive))
            {
                EditorGUILayout.HelpBox(
                    "positive 프롬프트가 비어 있으면 이 항목은 생성 대상에서 제외됩니다.", MessageType.Warning);
            }

            if (_owner == null)
            {
                EditorGUILayout.HelpBox(
                    "생성 창과의 연결이 끊어졌습니다. 이 창을 닫고 3단계 창에서 [추가]/[편집]을 다시 눌러주세요.",
                    MessageType.Error);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("취소", GUILayout.Height(26f)))
                {
                    Close();
                }

                using (new EditorGUI.DisabledScope(_owner == null))
                {
                    if (GUILayout.Button(_isNew ? "저장 (목록에 추가)" : "저장 (항목에 반영)", GUILayout.Height(26f)))
                    {
                        Save();
                    }
                }
            }
        }

        /// <summary>
        /// 대상 프리팹/대상 오브젝트를 선택 방식으로 그립니다.
        /// 프리팹은 프로젝트 에셋 ObjectField(끌어다 놓기·◎ 선택), 오브젝트는 그 프리팹의 계층 경로 드롭다운입니다.
        /// </summary>
        private void DrawTargetFields()
        {
            var prefab = string.IsNullOrEmpty(_item.targetPrefabPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(_item.targetPrefabPath);

            var picked = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("대상 프리팹",
                    "4단계 적용 대상 프리팹입니다. 프로젝트 창에서 끌어다 놓거나 ◎ 버튼으로 선택하세요."),
                prefab, typeof(GameObject), false);
            if (picked != prefab)
            {
                _item.targetPrefabPath = picked == null ? string.Empty : AssetDatabase.GetAssetPath(picked);
                _item.targetObjectPath = string.Empty; // 다른 프리팹의 계층 경로는 더 이상 유효하지 않다
                prefab = picked;
                GUI.FocusControl(null);
            }

            // 저장된 경로의 프리팹이 삭제·이동된 경우: 값을 말없이 버리지 않고 원인과 조치를 안내한다.
            if (prefab == null && !string.IsNullOrEmpty(_item.targetPrefabPath))
            {
                EditorGUILayout.HelpBox(
                    $"저장된 프리팹 경로를 찾을 수 없습니다: {_item.targetPrefabPath}\n" +
                    "프리팹이 삭제·이동되었을 수 있습니다. 위에서 다시 지정하거나 [경로 지우기]를 누르세요.",
                    MessageType.Warning);
                if (GUILayout.Button("경로 지우기", EditorStyles.miniButton, GUILayout.Width(90f)))
                {
                    _item.targetPrefabPath = string.Empty;
                    _item.targetObjectPath = string.Empty;
                }
            }

            RefreshObjectPathsIfNeeded(prefab);
            DrawObjectPathPopup(prefab);
        }

        /// <summary>대상 프리팹이 바뀌었으면 계층 경로 드롭다운 목록을 다시 만듭니다.</summary>
        private void RefreshObjectPathsIfNeeded(GameObject prefab)
        {
            string prefabPath = _item.targetPrefabPath ?? string.Empty;
            if (_cachedPrefabPath == prefabPath)
            {
                return;
            }

            _cachedPrefabPath = prefabPath;
            _objectPaths = new List<string>();
            var labels = new List<GUIContent>();
            if (prefab != null)
            {
                CollectObjectPaths(prefab.transform, _objectPaths, labels);
            }

            _objectLabels = labels.ToArray();
        }

        /// <summary>
        /// 프리팹 루트와 모든 자식의 계층 경로를 수집합니다.
        /// 경로는 <see cref="AssetApplier.FindTargetTransform"/>이 해석하는 형식(루트는 루트 이름,
        /// 자식은 루트 기준 상대 경로)이며, 표시 라벨은 팝업이 "/"를 하위 메뉴로 해석하지 않도록 " › "로 바꿉니다.
        /// </summary>
        private static void CollectObjectPaths(Transform root, List<string> paths, List<GUIContent> labels)
        {
            paths.Add(root.name);
            labels.Add(new GUIContent($"(루트) {Display(root.name)}{SlotSuffix(root)}", root.name));

            for (int i = 0; i < root.childCount; i++)
            {
                CollectChildPaths(root.GetChild(i), string.Empty, paths, labels);
            }
        }

        private static void CollectChildPaths(
            Transform target, string parentPath, List<string> paths, List<GUIContent> labels)
        {
            string path = string.IsNullOrEmpty(parentPath) ? target.name : $"{parentPath}/{target.name}";
            paths.Add(path);
            labels.Add(new GUIContent($"{Display(path)}{SlotSuffix(target)}", path));

            for (int i = 0; i < target.childCount; i++)
            {
                CollectChildPaths(target.GetChild(i), path, paths, labels);
            }
        }

        private static string Display(string path)
        {
            return path.Replace("/", " › ");
        }

        /// <summary>에셋 적용 대상이 되는 컴포넌트가 있으면 라벨 뒤에 붙일 표시를 반환합니다.</summary>
        private static string SlotSuffix(Transform target)
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (typeName == "Image" || typeName == "RawImage" ||
                    typeName == "SpriteRenderer" || typeName == "AudioSource")
                {
                    return $"  [{typeName}]";
                }
            }

            return string.Empty;
        }

        /// <summary>대상 오브젝트 계층 경로 드롭다운을 그립니다 (프리팹 미지정 시 비활성 안내).</summary>
        private void DrawObjectPathPopup(GameObject prefab)
        {
            var label = new GUIContent("대상 오브젝트",
                "프리팹 루트 기준 계층 경로입니다. 적용 가능한 컴포넌트가 있는 오브젝트에는 [Image] 같은 표시가 붙습니다.");

            if (prefab == null || _objectPaths.Count == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    string shown = string.IsNullOrEmpty(_item.targetObjectPath)
                        ? "(대상 프리팹을 먼저 선택하세요)"
                        : _item.targetObjectPath;
                    EditorGUILayout.Popup(label, 0, new[] { new GUIContent(shown) });
                }

                return;
            }

            int index = _objectPaths.IndexOf(_item.targetObjectPath ?? string.Empty);

            // 저장된 경로가 이 프리팹에 없는 경우(계층 변경 등): 값을 지우지 않고 경고색으로 유지해 보여준다.
            if (index < 0 && !string.IsNullOrEmpty(_item.targetObjectPath))
            {
                var contents = new GUIContent[_objectLabels.Length + 1];
                contents[0] = new GUIContent($"(현재: {Display(_item.targetObjectPath)})",
                    "이 프리팹에서 찾을 수 없는 경로입니다. 목록에서 다시 선택해주세요.");
                Array.Copy(_objectLabels, 0, contents, 1, _objectLabels.Length);

                Color previousColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                int selected = EditorGUILayout.Popup(label, 0, contents);
                GUI.backgroundColor = previousColor;

                if (selected > 0)
                {
                    _item.targetObjectPath = _objectPaths[selected - 1];
                }

                return;
            }

            // 비어 있으면 루트(첫 항목)를 가리킨다 — 적용 시에도 빈 경로는 루트로 해석된다.
            int newIndex = EditorGUILayout.Popup(label, Mathf.Max(0, index), _objectLabels);
            if (newIndex != index)
            {
                _item.targetObjectPath = _objectPaths[Mathf.Clamp(newIndex, 0, _objectPaths.Count - 1)];
            }
        }

        private void Save()
        {
            if (_owner == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(_item.name))
            {
                _item.name = _item.id;
            }

            _owner.ApplyEditedItem(_item);
            Close();
        }
    }
}
