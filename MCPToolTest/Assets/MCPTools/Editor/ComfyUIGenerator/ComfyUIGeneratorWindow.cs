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
    /// 후보 4개 생성(비동기·취소 가능) → 썸네일 선택 → 확정/재생성 흐름을 제공합니다.
    /// </summary>
    public class ComfyUIGeneratorWindow : EditorWindow
    {
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const float ThumbnailSize = 160f;
        private const double ServerCheckIntervalSeconds = 5.0;
        private const float LeftColumnWidth = 320f;

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

        // 서버 상태
        private bool _bridgeAlive;
        private bool _comfyAlive;
        private bool _serverChecking;
        private double _nextServerCheck;

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

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            DrawServerSection();
            EditorGUILayout.Space(6f);

            // 2열 레이아웃: 왼쪽 고정 열(입력/항목 목록), 오른쪽 열(워크플로/생성/후보)
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftColumnWidth)))
                {
                    DrawInputSection();
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    _scroll = EditorGUILayout.BeginScrollView(_scroll);
                    DrawWorkflowSection();
                    EditorGUILayout.Space(6f);
                    DrawGenerateSection();
                    EditorGUILayout.Space(6f);
                    DrawCandidateSection();
                    EditorGUILayout.EndScrollView();
                }
            }

            DrawBottomBar();
        }

        // ─────────────────────────── 서버 상태/시작/종료 ───────────────────────────

        private void DrawServerSection()
        {
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

                    using (new EditorGUI.DisabledScope(!ComfyUIServerLauncher.IsLaunchedProcessAlive()))
                    {
                        if (GUILayout.Button("서버 종료", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                        {
                            StopServer();
                        }
                    }
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
                            $"{_settings.docsRootPath} 폴더에 PromptSet_*.json이 없습니다. " +
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
                    EditorGUILayout.LabelField("PromptSet JSON을 로드하면 항목을 선택할 수 있습니다.", EditorStyles.miniLabel);
                    return;
                }

                DrawItemListPanel();

                PromptItem item = SelectedItem();
                if (item != null && string.IsNullOrEmpty(item.positive))
                {
                    EditorGUILayout.HelpBox("이 항목의 positive 프롬프트가 비어 있습니다. 2단계에서 채운 뒤 생성하세요.",
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
                EditorGUILayout.HelpBox("이 워크플로에 해당하는 항목이 없습니다.", MessageType.Info);
                return;
            }

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
                            new Rect(rowRect.x, rowRect.y, LeftColumnWidth - 24f, 22f),
                            new Color(0.24f, 0.49f, 0.90f, 0.25f));
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
            EditorGUILayout.LabelField($"확정 {confirmedCount}/{filtered.Count}", EditorStyles.miniLabel);

            DrawConfirmedAssetPanel(SelectedItem());
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
                        $"확정 에셋 없음(삭제됨): {path}", EditorStyles.wordWrappedMiniLabel);
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

                    using (new EditorGUILayout.VerticalScope())
                    {
                        if (asset is AudioClip)
                        {
                            EditorGUILayout.LabelField($"♪ {Path.GetFileName(path)}", EditorStyles.miniBoldLabel);
                        }

                        EditorGUILayout.LabelField(path, EditorStyles.wordWrappedMiniLabel);
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

            // assetType에 맞는 기본 워크플로 자동 선택 (audio→Audio, ui→UI, image→기본 이미지 워크플로)
            string wfName = DefaultWorkflowNameFor(item);
            int wfIndex = _workflows.FindIndex(w => w.name == wfName);
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
        private void ApplyItemPromptsToVariables()
        {
            PromptItem item = SelectedItem();
            if (item == null)
            {
                return;
            }

            foreach (VariableState state in _variableStates)
            {
                if (state.definition.role == "positive" && !string.IsNullOrEmpty(item.positive))
                {
                    state.stringValue = item.positive;
                }
                else if (state.definition.role == "negative" && !string.IsNullOrEmpty(item.negative))
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

            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int candidateCount = Mathf.Max(1, _settings.candidateCount);
                    string label = _candidates.Count > 0 && _candidatesItemId == CurrentItemId()
                        ? $"재생성 (새 시드로 {candidateCount}개)"
                        : $"후보 {candidateCount}개 생성";
                    if (GUILayout.Button(label, GUILayout.Height(PrimaryButtonHeight)))
                    {
                        StartGeneration();
                    }

                    using (new EditorGUI.DisabledScope(_document == null))
                    {
                        if (GUILayout.Button("전체 생성 (미생성만)", GUILayout.Height(PrimaryButtonHeight),
                                GUILayout.Width(160f)))
                        {
                            StartBatchGeneration();
                        }
                    }
                }
            }

            // 생성 완료 후 ComfyUI 모델 언로드 여부 (설정 에셋과 연동)
            EditorGUI.BeginChangeCheck();
            bool unload = EditorGUILayout.ToggleLeft(
                new GUIContent("생성 완료 후 모델 언로드 (메모리 확보)",
                    "생성 완료 후 ComfyUI에 로드된 모델을 언로드해 VRAM/메모리를 확보합니다. " +
                    "다음 생성 시 모델을 다시 로드하므로 첫 생성이 느려질 수 있습니다."),
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
        /// 프롬프트가 있고 아직 후보가 없는(확정되지 않은) 항목들을 순차 생성합니다.
        /// 이미 후보가 있거나 확정된 항목은 건너뜁니다.
        /// </summary>
        private void StartBatchGeneration()
        {
            if (_document == null || _generating)
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
            List<PromptItem> targets = _document.items
                .Where(it => ItemMatchesWorkflow(it) &&
                             !string.IsNullOrEmpty(it.positive) &&
                             !_confirmedPaths.ContainsKey(it.id) &&
                             (!_candidateCounts.TryGetValue(it.id, out int c) || c == 0))
                .ToList();

            if (targets.Count == 0)
            {
                _statusMessage = "일괄 생성할 항목이 없습니다 (모든 항목이 후보 보유/확정/프롬프트 없음 상태).";
                return;
            }

            _ = RunBatchGenerationAsync(targets);
        }

        /// <summary>
        /// 대상 항목들을 순차로 생성합니다 (항목당 후보 4개, 항목별 기본 워크플로 사용).
        /// 실패한 항목은 기록만 하고 다음 항목을 계속 진행하며, 완료 후 실패 목록을 요약합니다.
        /// 창을 닫으면 취소됩니다.
        /// </summary>
        private async Task RunBatchGenerationAsync(List<PromptItem> targets)
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
                for (int i = 0; i < targets.Count; i++)
                {
                    _cancelSource.Token.ThrowIfCancellationRequested();
                    PromptItem item = targets[i];
                    _batchCurrent = i + 1;
                    _batchCurrentLabel = $"{item.id} {item.name}";
                    _statusMessage = $"일괄 생성 중: {item.id} ({item.name})";

                    int captured = i;
                    var progress = new Progress<float>(p =>
                    {
                        _progress = (captured + Mathf.Clamp01(p)) / _batchTotal;
                        Repaint();
                    });

                    try
                    {
                        string wfName = DefaultWorkflowNameFor(item);

                        // 현재 선택된 워크플로와 같은 항목이면 사용자가 편집한 변수 값을 함께 사용한다
                        // (role 프롬프트 변수는 제외해 항목별 프롬프트가 주입되게 한다).
                        Dictionary<string, object> variables = null;
                        BridgeWorkflowInfo selected = SelectedWorkflow();
                        if (selected != null && selected.name == wfName)
                        {
                            variables = await BuildVariablesAsync(_cancelSource.Token, excludePromptRoles: true);
                        }

                        List<CandidateInfo> generated = await CandidateGenerator.GenerateAsync(
                            _settings, item, wfName, variables, null, progress, _cancelSource.Token);
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
                    ? $"일괄 생성 완료: {done}/{_batchTotal}개 항목 성공."
                    : $"일괄 생성 완료: 성공 {done}개, 실패 {failures.Count}개.";

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
                    progress, _cancelSource.Token);

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

                // 생성 시도 후에는 설정에 따라 모델을 언로드한다 (실패해도 생성 흐름에 영향 없음).
                _ = TryFreeMemoryAsync();

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
                            if (!string.IsNullOrEmpty(state.imageLocalPath))
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

            float rightWidth = Mathf.Max(ThumbnailSize + 16f, position.width - LeftColumnWidth - 40f);
            int columns = Mathf.Max(1, Mathf.FloorToInt(rightWidth / (ThumbnailSize + 16f)));
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

                        DrawCandidateCell(_candidates[index], index);
                    }

                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_selectedCandidateIndex < 0 || _generating))
                {
                    if (GUILayout.Button("확정", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        ConfirmSelected();
                    }
                }
            }
        }

        private void DrawCandidateCell(CandidateInfo candidate, int index)
        {
            bool selected = index == _selectedCandidateIndex;
            bool isAudio = IsAudioCandidate(candidate.path);

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ThumbnailSize + 8f)))
            {
                Rect rect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize,
                    GUILayout.Width(ThumbnailSize), GUILayout.Height(ThumbnailSize));

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
                    GUILayout.Width(ThumbnailSize));

                if (!isAudio)
                {
                    if (GUILayout.Button("크게 보기", GUILayout.Width(ThumbnailSize), GUILayout.Height(ButtonHeight)))
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
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                }
            }
        }

        // ─────────────────────────── 헬퍼 ───────────────────────────

        private void RefreshPromptSetPaths()
        {
            string docsRoot = _settings != null ? _settings.docsRootPath : "Assets/Docs";
            if (!Directory.Exists(docsRoot))
            {
                _promptSetPaths = new string[0];
                return;
            }

            _promptSetPaths = Directory.GetFiles(docsRoot, "PromptSet_*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .Select(p => p.Replace('\\', '/'))
                .ToArray();
        }

        private void LoadSelectedPromptSet()
        {
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
        /// 후보는 Assets/Generated/Candidates/manual_{워크플로}/에 저장됩니다.
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
}
