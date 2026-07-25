using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 2단계 프롬프트 제작 창입니다. (메뉴: Tools/MCP/2. Prompt Builder)
    /// 1단계 AssetList JSON 선택 → 템플릿/AI로 프롬프트 초안 생성 → 항목별 수동 편집 → JSON 저장 흐름을 제공합니다.
    /// </summary>
    public class PromptBuilderWindow : EditorWindow
    {
        private static readonly string[] AssetTypeOptions = { "image", "ui", "audio" };

        // 표 열 너비 (헤더와 행이 공유)
        private const float ColId = 64f;
        private const float ColName = 120f;
        private const float ColType = 70f;
        private const float ColUi = 40f;
        private const float ColPrefab = 200f;
        private const float ColPositive = 340f;
        private const float ColNegative = 260f;
        private const float ColDelete = 44f;
        private const float RowHeight = 22f;

        private const string PrefKeySelectedAi = "MCPTools.PromptBuilder.SelectedAiTool";
        private const string PrefKeyCustomCommand = "MCPTools.PromptBuilder.CustomAiCommand";
        private const string PrefKeyTimeout = "MCPTools.PromptBuilder.AiTimeoutSeconds";
        private const string PrefKeyAllowExplore = "MCPTools.PromptBuilder.AiAllowProjectExplore";
        private const string PrefKeyManualFoldout = "MCPTools.PromptBuilder.ManualFoldout";

        // 버튼 크기 일관화 (AssetListupWindow와 동일)
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const string CopyOnlyOption = "클립보드 복사만";
        private const string CustomOption = "직접 입력...";

        private MCPToolSettings _settings;
        private string[] _assetListPaths = new string[0];
        private int _selectedListIndex;
        private string[] _templateNames = { PromptTemplate.DefaultName };
        private int _selectedTemplateIndex;
        private PromptSetDocument _document;
        private Vector2 _scroll;
        private string _statusMessage = string.Empty;

        // 선택 항목 상세 편집 (긴 프롬프트 전체 확인·편집용)
        private int _selectedRowIndex = -1;
        private Vector2 _detailPositiveScroll;
        private Vector2 _detailNegativeScroll;
        private GUIStyle _wrapTextAreaStyle;

        // AI CLI 연동 상태
        private List<AiCliTool> _aiTools;
        private string[] _aiOptions = new string[0];
        private int _selectedAiIndex;
        private string _customAiCommand = string.Empty;
        private int _aiTimeoutSeconds = 300;
        private bool _aiAllowExplore = true;
        private bool _aiRunning;
        private CancellationTokenSource _aiCancelSource;
        private bool _manualFoldout;

        /// <summary>프롬프트 빌더 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/2. Prompt Builder", false, 2)]
        public static void Open()
        {
            var window = GetWindow<PromptBuilderWindow>();
            window.titleContent = new GUIContent("프롬프트 빌더");
            window.minSize = new Vector2(640f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();

            // 처음 여는 프로젝트에서 문서/출력 폴더가 없어 사용자가 직접 만들어야 하는 것을 막는다 (D12).
            MCPToolFolders.EnsureWorkFolders(_settings);
            RefreshAssetListPaths();
            RefreshTemplateList();

            _customAiCommand = EditorPrefs.GetString(PrefKeyCustomCommand, string.Empty);
            _aiTimeoutSeconds = EditorPrefs.GetInt(PrefKeyTimeout, 300);
            _aiAllowExplore = EditorPrefs.GetBool(PrefKeyAllowExplore, true);
            _manualFoldout = EditorPrefs.GetBool(PrefKeyManualFoldout, false);
            RefreshAiToolList(false);
        }

        private void OnDisable()
        {
            CancelAiRun();
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

            DrawAiSection();
            EditorGUILayout.Space(6f);
            DrawManualSection();
            EditorGUILayout.Space(6f);
            DrawItemTable();
            EditorGUILayout.Space(4f);
            DrawDetailSection();
            DrawBottomBar();
        }

        private void DrawAiSection()
        {
            EditorGUILayout.LabelField("AI 연동", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // 입력: AssetList JSON + 템플릿
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_assetListPaths.Length == 0)
                    {
                        EditorGUILayout.HelpBox(
                            $"{MCPToolFolders.AssetListDir(_settings)} 폴더에 AssetList_*.json이 없습니다. " +
                            "1단계(Tools/MCP/1. Asset Listup)에서 목록을 먼저 저장해주세요.", MessageType.Warning);
                    }
                    else
                    {
                        _selectedListIndex = EditorGUILayout.Popup("에셋 목록 JSON",
                            Mathf.Clamp(_selectedListIndex, 0, _assetListPaths.Length - 1), _assetListPaths);
                    }

                    if (GUILayout.Button("새로고침", GUILayout.Width(SmallButtonWidth)))
                    {
                        RefreshAssetListPaths();
                        RefreshTemplateList();
                    }
                }

                _selectedTemplateIndex = EditorGUILayout.Popup("템플릿",
                    Mathf.Clamp(_selectedTemplateIndex, 0, _templateNames.Length - 1), _templateNames);

                EditorGUILayout.Space(4f);

                // AI 도구 선택 + 옵션
                using (new EditorGUILayout.HorizontalScope())
                {
                    _selectedAiIndex = EditorGUILayout.Popup("AI 도구",
                        Mathf.Clamp(_selectedAiIndex, 0, Mathf.Max(0, _aiOptions.Length - 1)), _aiOptions);

                    if (GUILayout.Button("다시 검색", GUILayout.Width(SmallButtonWidth)))
                    {
                        RefreshAiToolList(true);
                    }
                }

                if (_selectedAiIndex >= 0 && _selectedAiIndex < _aiOptions.Length)
                {
                    EditorPrefs.SetString(PrefKeySelectedAi, _aiOptions[_selectedAiIndex]);
                }

                bool isCustom = SelectedAiOption() == CustomOption;
                if (isCustom)
                {
                    string newCommand = EditorGUILayout.TextField(
                        new GUIContent("실행 커맨드", "예: mytool --flag (프롬프트는 stdin으로 전달됩니다)"), _customAiCommand);
                    if (newCommand != _customAiCommand)
                    {
                        _customAiCommand = newCommand;
                        EditorPrefs.SetString(PrefKeyCustomCommand, _customAiCommand);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    int newTimeout = EditorGUILayout.IntField(
                        new GUIContent("타임아웃(초)", "AI CLI 실행 제한 시간입니다. 기본 300초."), _aiTimeoutSeconds);
                    if (newTimeout != _aiTimeoutSeconds)
                    {
                        _aiTimeoutSeconds = Mathf.Max(10, newTimeout);
                        EditorPrefs.SetInt(PrefKeyTimeout, _aiTimeoutSeconds);
                    }

                    bool newAllowExplore = GUILayout.Toggle(_aiAllowExplore,
                        new GUIContent("프로젝트 코드 탐색 허용",
                            "켜면 AI CLI를 프로젝트 루트에서 읽기 전용 도구 허용으로 실행해, 각 항목의 대상 프리팹·스크립트를 " +
                            "직접 읽으며 프롬프트 묘사를 구체화합니다 (더 정확하지만 느리고 토큰 소모가 큼). " +
                            "끄면 목록 요약+템플릿만 프롬프트에 담아 일회성으로 묻습니다 (빠름). 파일 쓰기/명령 실행은 어느 쪽도 허용하지 않습니다."),
                        GUILayout.Width(180f));
                    if (newAllowExplore != _aiAllowExplore)
                    {
                        _aiAllowExplore = newAllowExplore;
                        EditorPrefs.SetBool(PrefKeyAllowExplore, _aiAllowExplore);
                    }
                }

                if (_aiTools != null && _aiTools.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "PATH에서 AI CLI(claude/codex/gemini/cursor-agent/copilot)를 찾지 못했습니다. " +
                        "[직접 입력...]으로 임의 커맨드를 지정하거나 아래 [로컬 AI 미사용 시 (수동 방식)]으로 진행하세요.",
                        MessageType.Info);
                }

                EditorGUILayout.Space(4f);

                if (_aiRunning)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("AI 실행 중... (완료까지 수 분이 걸릴 수 있습니다)");
                        if (GUILayout.Button("취소", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                        {
                            CancelAiRun();
                        }
                    }
                }
                else
                {
                    // AI 실행은 PromptSet 저장까지 이어지므로 Play Mode·컴파일 중에는 막는다 (R2).
                    using (new EditorGUI.DisabledScope(_blockedReason != null))
                    {
                        if (GUILayout.Button("선택한 AI로 프롬프트 생성", GUILayout.Height(PrimaryButtonHeight)))
                        {
                            RunSelectedAi();
                        }
                    }
                }
            }
        }

        private void DrawManualSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool newFoldout = EditorGUILayout.Foldout(_manualFoldout, "로컬 AI 미사용 시 (수동 방식)", true);
                if (newFoldout != _manualFoldout)
                {
                    _manualFoldout = newFoldout;
                    EditorPrefs.SetBool(PrefKeyManualFoldout, _manualFoldout);
                }

                if (!_manualFoldout)
                {
                    return;
                }

                EditorGUILayout.HelpBox("AI CLI가 설치되지 않았거나 웹 AI를 쓰는 경우 이 방식을 사용하세요.", MessageType.Info);

                using (new EditorGUILayout.HorizontalScope())
                {
                    // 템플릿 초안 생성은 목록을 읽어 문서를 만드는 실행 버튼이므로 Play Mode·컴파일 중에는 막는다 (R2).
                    using (new EditorGUI.DisabledScope(_blockedReason != null))
                    {
                        if (GUILayout.Button("템플릿 초안 생성(보조)", GUILayout.Height(ButtonHeight)))
                        {
                            RunTemplateBuild();
                        }
                    }

                    if (GUILayout.Button("AI용 프롬프트 복사", GUILayout.Height(ButtonHeight)))
                    {
                        CopyAiPrompt();
                    }

                    if (GUILayout.Button("AI 응답 JSON 불러오기", GUILayout.Height(ButtonHeight)))
                    {
                        PromptSetJsonImportWindow.Open(this);
                    }
                }
            }
        }

        private void DrawItemTable()
        {
            if (_document == null)
            {
                EditorGUILayout.HelpBox(
                    "에셋 목록 JSON을 선택한 뒤 [선택한 AI로 프롬프트 생성]을 실행하세요. " +
                    "AI CLI가 없으면 [로컬 AI 미사용 시 (수동 방식)]의 버튼들을 사용합니다.",
                    MessageType.Info);
                GUILayout.FlexibleSpace();
                return;
            }

            EditorGUILayout.LabelField($"프롬프트 목록 ({_document.items.Count}개)", EditorStyles.boldLabel);

            // 가로+세로 스크롤 하나에 헤더와 행을 함께 넣어 가로 스크롤 시 열이 어긋나지 않게 한다.
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            DrawHeaderRow();

            int removeIndex = -1;
            for (int i = 0; i < _document.items.Count; i++)
            {
                if (DrawItemRow(_document.items[i], i))
                {
                    removeIndex = i;
                }
            }

            if (removeIndex >= 0)
            {
                _document.items.RemoveAt(removeIndex);
                if (_selectedRowIndex == removeIndex)
                {
                    _selectedRowIndex = -1;
                }
                else if (_selectedRowIndex > removeIndex)
                {
                    _selectedRowIndex--;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 선택한 행의 positive/negative 전체 텍스트를 word-wrap 멀티라인 TextArea로 확인·편집하는 영역입니다.
        /// 표 셀은 한 줄 요약(+툴팁)만 보여주고, 실제 프롬프트 편집은 이 영역에서 수행합니다.
        /// </summary>
        private void DrawDetailSection()
        {
            if (_document == null || _document.items.Count == 0)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_selectedRowIndex < 0 || _selectedRowIndex >= _document.items.Count)
                {
                    EditorGUILayout.LabelField(
                        "표에서 행을 클릭하면 여기에서 positive/negative 프롬프트 전체를 확인·편집할 수 있습니다.",
                        EditorStyles.miniLabel);
                    return;
                }

                if (_wrapTextAreaStyle == null)
                {
                    _wrapTextAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
                }

                PromptItem item = _document.items[_selectedRowIndex];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        $"선택 항목 편집 — {item.id} {(string.IsNullOrEmpty(item.name) ? string.Empty : $"({item.name})")}",
                        EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("선택 해제", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                    {
                        _selectedRowIndex = -1;
                        GUI.FocusControl(null);
                        return;
                    }
                }

                EditorGUILayout.LabelField("Positive 프롬프트", EditorStyles.miniBoldLabel);
                _detailPositiveScroll = EditorGUILayout.BeginScrollView(_detailPositiveScroll, GUILayout.Height(64f));
                item.positive = EditorGUILayout.TextArea(item.positive, _wrapTextAreaStyle, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();

                EditorGUILayout.LabelField("Negative 프롬프트", EditorStyles.miniBoldLabel);
                _detailNegativeScroll = EditorGUILayout.BeginScrollView(_detailNegativeScroll, GUILayout.Height(48f));
                item.negative = EditorGUILayout.TextArea(item.negative, _wrapTextAreaStyle, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawHeaderRow()
        {
            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin
                    ? new Color(0.17f, 0.17f, 0.17f)
                    : new Color(0.75f, 0.75f, 0.75f));
            }

            HeaderCell("ID", ColId);
            HeaderCell("이름", ColName);
            HeaderCell("종류", ColType);
            HeaderCell("UI", ColUi);
            HeaderCell("대상 프리팹", ColPrefab);
            HeaderCell("Positive 프롬프트", ColPositive);
            HeaderCell("Negative 프롬프트", ColNegative);
            HeaderCell("삭제", ColDelete);
            EditorGUILayout.EndHorizontal();
        }

        private static void HeaderCell(string label, float width)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(width));
        }

        private bool DrawItemRow(PromptItem item, int index)
        {
            bool incomplete = string.IsNullOrEmpty(item.positive);

            bool selected = index == _selectedRowIndex;

            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            if (Event.current.type == EventType.Repaint)
            {
                if (selected)
                {
                    // 선택된 행은 에디터 선택색 계열로 강조
                    EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin
                        ? new Color(0.24f, 0.37f, 0.58f, 0.55f)
                        : new Color(0.24f, 0.49f, 0.90f, 0.30f));
                }
                else if (incomplete)
                {
                    // positive 프롬프트가 비어 있는 항목은 옅은 경고색
                    EditorGUI.DrawRect(rowRect, new Color(0.8f, 0.6f, 0.1f, 0.15f));
                }
                else if (index % 2 == 1)
                {
                    // 줄무늬 배경
                    EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin
                        ? new Color(1f, 1f, 1f, 0.04f)
                        : new Color(0f, 0f, 0f, 0.05f));
                }
            }

            EditorGUILayout.LabelField(
                new GUIContent(item.id, incomplete ? "positive 프롬프트가 비어 있습니다." : item.id),
                EditorStyles.miniLabel, GUILayout.Width(ColId));

            item.name = EditorGUILayout.TextField(item.name, GUILayout.Width(ColName));

            int typeIndex = System.Array.IndexOf(AssetTypeOptions, item.assetType);
            typeIndex = EditorGUILayout.Popup(Mathf.Max(0, typeIndex), AssetTypeOptions, GUILayout.Width(ColType));
            item.assetType = AssetTypeOptions[typeIndex];

            item.isUI = EditorGUILayout.Toggle(item.isUI, GUILayout.Width(ColUi));
            item.targetPrefabPath = EditorGUILayout.TextField(item.targetPrefabPath, GUILayout.Width(ColPrefab));

            // 프롬프트 셀은 한 줄 요약 라벨(+전체 텍스트 툴팁)로 표시하고, 편집은 하단 상세 영역에서 한다.
            PromptSummaryCell(item.positive, ColPositive, "(비어 있음 — 행을 클릭해 아래에서 입력)");
            PromptSummaryCell(item.negative, ColNegative, "(비어 있음)");

            bool remove = GUILayout.Button("삭제", EditorStyles.miniButton, GUILayout.Width(ColDelete));
            EditorGUILayout.EndHorizontal();

            // 행 클릭으로 선택 (버튼/입력 필드가 소비하지 않은 클릭만 도달)
            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
            {
                _selectedRowIndex = index;
                GUI.FocusControl(null);
                Event.current.Use();
                Repaint();
            }

            return remove;
        }

        private static void PromptSummaryCell(string text, float width, string emptyPlaceholder)
        {
            bool empty = string.IsNullOrEmpty(text);
            EditorGUILayout.LabelField(
                new GUIContent(empty ? emptyPlaceholder : text.Replace('\n', ' '),
                    empty ? "행을 클릭하면 하단 상세 영역에서 편집할 수 있습니다." : text),
                EditorStyles.miniLabel, GUILayout.Width(width));
        }

        private void DrawBottomBar()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    // [저장]은 PromptSet JSON을 쓰므로 Play Mode·컴파일 중에는 막는다 (R2).
                    using (new EditorGUI.DisabledScope(_document == null || _blockedReason != null))
                    {
                        if (GUILayout.Button("항목 추가", GUILayout.Height(ButtonHeight)))
                        {
                            _document.items.Add(new PromptItem { id = NextItemId() });
                        }

                        if (GUILayout.Button("저장", GUILayout.Height(ButtonHeight)))
                        {
                            SaveDocument();
                        }
                    }
                }
            }
        }

        private void RefreshAssetListPaths()
        {
            // 새 하위 폴더(1_AssetList)와 구 위치(Docs 루트)를 함께 훑는다. 최신 파일이 앞에 온다.
            _assetListPaths = MCPToolFolders.FindDocuments(
                MCPToolFolders.DocsRoot(_settings), MCPToolFolders.AssetListFolder, "AssetList_*.json");
        }

        private void RefreshTemplateList()
        {
            _templateNames = PromptTemplate.ListTemplateNames().ToArray();
            _selectedTemplateIndex = Mathf.Clamp(_selectedTemplateIndex, 0, _templateNames.Length - 1);
        }

        private string SelectedAssetListPath()
        {
            return _assetListPaths.Length == 0
                ? null
                : _assetListPaths[Mathf.Clamp(_selectedListIndex, 0, _assetListPaths.Length - 1)];
        }

        private PromptTemplate SelectedTemplate()
        {
            string name = _templateNames[Mathf.Clamp(_selectedTemplateIndex, 0, _templateNames.Length - 1)];
            return PromptTemplate.LoadByName(name);
        }

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

        private AssetListDocument LoadSelectedAssetList(out string listPath)
        {
            listPath = SelectedAssetListPath();
            if (string.IsNullOrEmpty(listPath))
            {
                EditorUtility.DisplayDialog("프롬프트 빌더",
                    $"에셋 목록 JSON이 없습니다.\n{MCPToolFolders.AssetListDir(_settings)} 폴더에 1단계 산출물(AssetList_*.json)을 먼저 저장해주세요.",
                    "확인");
                return null;
            }

            try
            {
                return PromptBuilder.LoadAssetList(listPath);
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 빌더", $"에셋 목록을 불러올 수 없습니다.\n{e.Message}", "확인");
                return null;
            }
        }

        private void RunTemplateBuild()
        {
            AssetListDocument list = LoadSelectedAssetList(out string listPath);
            if (list == null)
            {
                return;
            }

            PromptTemplate template = SelectedTemplate();
            _document = PromptBuilder.Build(list, template);
            _document.assetListPath = listPath;
            _selectedRowIndex = -1;
            _statusMessage = $"템플릿 초안 생성 완료 — 항목 {_document.items.Count}개 (템플릿: {template.templateName}). " +
                             "묘사가 단순하므로 AI 연동으로 다듬는 것을 권장합니다.";
        }

        private void CopyAiPrompt()
        {
            AssetListDocument list = LoadSelectedAssetList(out string listPath);
            if (list == null)
            {
                return;
            }

            try
            {
                EditorGUIUtility.systemCopyBuffer = PromptSetPromptBuilder.BuildPrompt(list, SelectedTemplate());
                _statusMessage = $"AI용 프롬프트를 클립보드에 복사했습니다 (목록: {listPath}, 항목 {list.items.Count}개). " +
                                 "AI에 붙여넣고, 응답 JSON을 [AI 응답 JSON 불러오기]로 반영하세요.";
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 빌더", $"프롬프트 생성 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        private string SelectedAiOption()
        {
            return _aiOptions.Length == 0
                ? CopyOnlyOption
                : _aiOptions[Mathf.Clamp(_selectedAiIndex, 0, _aiOptions.Length - 1)];
        }

        private void RefreshAiToolList(bool forceRefresh)
        {
            _aiTools = AiCliRunner.GetInstalledTools(forceRefresh);

            var options = new List<string>();
            foreach (AiCliTool tool in _aiTools)
            {
                options.Add(tool.displayName);
            }

            options.Add(CustomOption);
            options.Add(CopyOnlyOption);
            _aiOptions = options.ToArray();

            string saved = EditorPrefs.GetString(PrefKeySelectedAi, string.Empty);
            int savedIndex = System.Array.IndexOf(_aiOptions, saved);
            _selectedAiIndex = savedIndex >= 0 ? savedIndex : 0;

            // AI CLI가 하나도 감지되지 않으면 수동 방식 Foldout을 자동으로 펼친다.
            if (_aiTools.Count == 0 && !_manualFoldout)
            {
                _manualFoldout = true;
                EditorPrefs.SetBool(PrefKeyManualFoldout, true);
            }

            Repaint();
        }

        private void CancelAiRun()
        {
            if (_aiCancelSource != null)
            {
                _aiCancelSource.Cancel();
            }
        }

        private async void RunSelectedAi()
        {
            string option = SelectedAiOption();
            if (option == CopyOnlyOption)
            {
                CopyAiPrompt();
                return;
            }

            string command;
            if (option == CustomOption)
            {
                command = _customAiCommand.Trim();
                if (string.IsNullOrEmpty(command))
                {
                    EditorUtility.DisplayDialog("AI 연동", "실행 커맨드를 입력해주세요. (예: mytool --flag)", "확인");
                    return;
                }
            }
            else
            {
                int toolIndex = _selectedAiIndex; // 감지된 도구는 목록 앞쪽에 순서대로 배치됨
                if (_aiTools == null || toolIndex < 0 || toolIndex >= _aiTools.Count)
                {
                    EditorUtility.DisplayDialog("AI 연동", "선택한 AI 도구를 찾을 수 없습니다. [다시 검색] 후 다시 시도해주세요.", "확인");
                    return;
                }

                command = _aiTools[toolIndex].command;
            }

            AssetListDocument list = LoadSelectedAssetList(out string listPath);
            if (list == null)
            {
                return;
            }

            string prompt;
            try
            {
                PromptTemplate template = SelectedTemplate();
                prompt = _aiAllowExplore
                    ? PromptSetPromptBuilder.BuildExplorationPrompt(list, template)
                    : PromptSetPromptBuilder.BuildPrompt(list, template);
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("AI 연동", $"프롬프트 생성 중 오류가 발생했습니다.\n{e.Message}", "확인");
                return;
            }

            // 탐색 모드: 프로젝트 루트를 작업 디렉터리로 지정해 상대 경로(Assets/...) 읽기를 가능하게 한다.
            string projectRoot = Path.GetDirectoryName(Application.dataPath);

            _aiRunning = true;
            _statusMessage = _aiAllowExplore
                ? $"AI 실행 중 (프로젝트 탐색 모드): {option}"
                : $"AI 실행 중: {option}";
            _aiCancelSource = new CancellationTokenSource();
            AiCliResult result;
            try
            {
                result = await AiCliRunner.RunAsync(command, prompt, _aiTimeoutSeconds, _aiCancelSource.Token,
                    _aiAllowExplore, _aiAllowExplore ? projectRoot : null);
            }
            finally
            {
                _aiRunning = false;
                _aiCancelSource.Dispose();
                _aiCancelSource = null;
                Repaint();
            }

            if (this == null)
            {
                return; // 실행 중 창이 닫힌 경우
            }

            if (result.canceled)
            {
                _statusMessage = "AI 실행을 취소했습니다.";
                return;
            }

            if (!result.success)
            {
                _statusMessage = "AI 실행 실패.";
                string detail = Summarize(result.stderr, 400);
                if (string.IsNullOrEmpty(detail))
                {
                    detail = Summarize(result.stdout, 400);
                }

                EditorUtility.DisplayDialog("AI 실행 실패",
                    $"{result.errorMessage}\n\n{detail}", "확인");

                if (!string.IsNullOrEmpty(result.stdout))
                {
                    PromptSetJsonImportWindow.Open(this, result.stdout); // 수동 보정 경로
                }

                return;
            }

            TryApplyAiResponse(result.stdout, listPath);
        }

        private void TryApplyAiResponse(string responseText, string listPath)
        {
            List<PromptItem> items;
            try
            {
                items = PromptSetPromptBuilder.ParseItemsJson(responseText);
            }
            catch (System.Exception e)
            {
                _statusMessage = "AI 응답 파싱 실패 — [AI 응답 JSON 불러오기] 창에서 수동 보정하세요.";
                EditorUtility.DisplayDialog("AI 응답을 해석할 수 없습니다",
                    $"{e.Message}\n\n응답 원문을 [AI 응답 JSON 불러오기] 창에 넣어두었으니 JSON 부분만 남기고 다시 시도해주세요.", "확인");
                PromptSetJsonImportWindow.Open(this, responseText);
                return;
            }

            bool hasExisting = _document != null && _document.items.Count > 0;
            if (hasExisting)
            {
                int choice = EditorUtility.DisplayDialogComplex("AI 응답 반영",
                    $"AI가 {items.Count}개 항목의 프롬프트를 생성했습니다.\n기존 목록({_document.items.Count}개)에 어떻게 반영할까요?",
                    "교체", "취소", "병합");
                if (choice == 1)
                {
                    _statusMessage = "AI 응답 반영을 취소했습니다.";
                    return;
                }

                ApplyImportedItems(items, choice == 2);
            }
            else
            {
                ApplyImportedItems(items, false);
            }
        }

        private static string Summarize(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = text.Trim();
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + " ...";
        }

        /// <summary>
        /// AI 응답 JSON에서 파싱된 프롬프트 항목을 현재 목록에 반영합니다 (보조 창에서 호출).
        /// </summary>
        /// <param name="items">파싱된 항목 목록.</param>
        /// <param name="merge">true면 같은 id 항목을 덮어쓰고 새 항목은 뒤에 추가, false면 목록 교체.</param>
        internal void ApplyImportedItems(List<PromptItem> items, bool merge)
        {
            if (_document == null || !merge)
            {
                _document = new PromptSetDocument
                {
                    assetListPath = SelectedAssetListPath() ?? string.Empty,
                    templateName = _templateNames[Mathf.Clamp(_selectedTemplateIndex, 0, _templateNames.Length - 1)],
                    createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };

                if (merge)
                {
                    merge = false; // 기존 문서가 없으면 교체와 동일
                }

                _selectedRowIndex = -1; // 목록이 새로 만들어지므로 선택 초기화
            }

            foreach (PromptItem item in items)
            {
                // 병합: 같은 id 항목이 있으면 프롬프트를 덮어쓴다 (id는 1단계 목록과 1:1 대응).
                PromptItem existing = merge && !string.IsNullOrEmpty(item.id)
                    ? _document.items.FirstOrDefault(i => i.id == item.id)
                    : null;
                if (existing != null)
                {
                    int index = _document.items.IndexOf(existing);
                    _document.items[index] = item;
                }
                else
                {
                    _document.items.Add(item);
                }
            }

            for (int i = 0; i < _document.items.Count; i++)
            {
                if (string.IsNullOrEmpty(_document.items[i].id))
                {
                    _document.items[i].id = $"item_{i + 1:000}";
                }
            }

            _statusMessage = merge
                ? $"AI 응답 {items.Count}개 항목을 기존 목록에 병합했습니다 (총 {_document.items.Count}개)."
                : $"AI 응답 {items.Count}개 항목으로 목록을 교체했습니다.";
            Repaint();
        }

        private void SaveDocument()
        {
            if (_document == null || _document.items.Count == 0)
            {
                EditorUtility.DisplayDialog("프롬프트 빌더", "저장할 항목이 없습니다.", "확인");
                return;
            }

            // positive 프롬프트가 비어 있는 항목은 차단하지 않고 확인 후 저장한다 (Task 1 정책과 동일).
            List<PromptItem> incomplete = _document.items
                .Where(i => string.IsNullOrEmpty(i.positive))
                .ToList();
            if (incomplete.Count > 0)
            {
                const int maxShown = 5;
                string examples = string.Join(", ", incomplete.Take(maxShown)
                    .Select(i => string.IsNullOrEmpty(i.name) ? i.id : i.name));
                if (incomplete.Count > maxShown)
                {
                    examples += $" 외 {incomplete.Count - maxShown}건";
                }

                bool proceed = EditorUtility.DisplayDialog("빈 프롬프트 확인",
                    $"positive 프롬프트가 비어 있는 항목 {incomplete.Count}건이 있습니다.\n({examples})\n\n" +
                    "그래도 저장할까요? (3단계 생성 전에 채워야 합니다)",
                    "저장", "취소");
                if (!proceed)
                {
                    return;
                }
            }

            try
            {
                string savedPath = PromptBuilder.Save(_document);
                _statusMessage = $"저장 완료: {savedPath}";
                ShowNotification(new GUIContent("프롬프트 목록을 저장했습니다."));
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 빌더", $"저장 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }
    }

    /// <summary>
    /// AI가 출력한 프롬프트 목록 JSON 배열을 붙여넣거나 파일로 불러와
    /// <see cref="PromptBuilderWindow"/>의 목록에 반영하는 보조 창입니다.
    /// </summary>
    public class PromptSetJsonImportWindow : EditorWindow
    {
        private PromptBuilderWindow _owner;
        private string _jsonText = string.Empty;
        private Vector2 _scroll;

        /// <summary>보조 창을 엽니다.</summary>
        /// <param name="owner">항목을 반영할 프롬프트 빌더 창.</param>
        /// <param name="initialText">미리 채워 넣을 응답 원문 (선택). AI 실행 실패 시 수동 보정용.</param>
        public static void Open(PromptBuilderWindow owner, string initialText = null)
        {
            var window = GetWindow<PromptSetJsonImportWindow>(true);
            window.titleContent = new GUIContent("AI 응답 JSON 불러오기");
            window.minSize = new Vector2(480f, 360f);
            window._owner = owner;
            if (initialText != null)
            {
                window._jsonText = initialText;
            }

            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("AI가 출력한 JSON 배열을 붙여넣으세요.", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("마크다운 코드 펜스(```)가 포함되어 있어도 자동으로 제거됩니다.",
                EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _jsonText = EditorGUILayout.TextArea(_jsonText, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("파일에서 불러오기"))
                {
                    string path = EditorUtility.OpenFilePanel("AI 응답 JSON 선택", "Assets", "json,txt");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _jsonText = File.ReadAllText(path);
                    }
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("목록 교체", GUILayout.Width(90f)))
                {
                    Import(false);
                }

                if (GUILayout.Button("목록에 병합", GUILayout.Width(90f)))
                {
                    Import(true);
                }
            }
        }

        private void Import(bool merge)
        {
            if (_owner == null)
            {
                EditorUtility.DisplayDialog("AI 응답 불러오기",
                    "프롬프트 빌더 창을 찾을 수 없습니다. Tools/MCP/2. Prompt Builder 창을 연 뒤 다시 시도해주세요.", "확인");
                return;
            }

            try
            {
                List<PromptItem> items = PromptSetPromptBuilder.ParseItemsJson(_jsonText);
                _owner.ApplyImportedItems(items, merge);
                Close();
            }
            catch (System.FormatException e)
            {
                EditorUtility.DisplayDialog("AI 응답을 해석할 수 없습니다",
                    $"{e.Message}\n\nAI 응답에서 JSON 배열 부분만 복사해 다시 시도해주세요.", "확인");
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("AI 응답 불러오기", $"오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }
    }
}
