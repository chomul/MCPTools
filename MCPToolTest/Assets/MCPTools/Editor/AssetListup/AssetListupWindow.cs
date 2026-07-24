using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 1단계 에셋 리스트업 창입니다. (메뉴: Tools/MCP/1. Asset Listup)
    /// 기획서 선택 → 스캔/AI 분석 → 표 형태 목록 편집 → JSON 저장 흐름을 제공합니다.
    /// </summary>
    public class AssetListupWindow : EditorWindow
    {
        private static readonly string[] AssetTypeOptions = { "image", "ui", "audio" };
        private static readonly string[] UiFlagOptions = { "미지정", "UI 아님", "UI" };

        // 표 열 너비 (헤더와 행이 공유)
        private const float ColId = 64f;
        private const float ColName = 130f;
        private const float ColType = 70f;
        private const float ColUi = 80f;
        private const float ColPrefab = 240f;
        private const float ColScene = 200f;
        private const float ColObject = 180f;
        private const float ColDesc = 260f;
        private const float ColStatus = 80f;
        private const float ColDelete = 44f;
        private const float RowHeight = 22f;

        private const string PrefKeySelectedAi = "MCPTools.AssetListup.SelectedAiTool";
        private const string PrefKeyCustomCommand = "MCPTools.AssetListup.CustomAiCommand";
        private const string PrefKeyTimeout = "MCPTools.AssetListup.AiTimeoutSeconds";
        private const string PrefKeyAllowExplore = "MCPTools.AssetListup.AiAllowProjectExplore";
        private const string PrefKeyManualFoldout = "MCPTools.AssetListup.ManualFoldout";
        private const string PrefKeyExtractFromDoc = "MCPTools.AssetListup.ExtractFromDoc";

        // 버튼 크기 일관화
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const string CopyOnlyOption = "클립보드 복사만";
        private const string CustomOption = "직접 입력...";

        private MCPToolSettings _settings;
        private string[] _designDocPaths = new string[0];
        private int _selectedDocIndex;
        private string _scanRootPath = "Assets";
        private readonly List<SceneAsset> _scanScenes = new List<SceneAsset>();
        private AssetListDocument _document;
        private Vector2 _scroll;
        private string _statusMessage = string.Empty;

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

        // ON = 기획서에서 항목을 추측해 추가(기존 추출 방식), OFF(기본) = 스캔 항목만 + 기획서는 설명 참고용.
        private bool _extractFromDoc;

        // 스캔 전용(OFF) 모드에서 AI로 설명을 채우는 동안 임시로 보관하는 스캔 결과 문서.
        private AssetListDocument _pendingScannedDoc;

        // 위 스캔 결과의 구성 요약 (씬/프리팹 슬롯 수). 완료 상태 메시지에 함께 표시한다.
        private string _pendingScanSummary = string.Empty;

        /// <summary>에셋 리스트업 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/1. Asset Listup", false, 1)]
        public static void Open()
        {
            var window = GetWindow<AssetListupWindow>();
            window.titleContent = new GUIContent("에셋 리스트업");
            window.minSize = new Vector2(640f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();

            // 처음 여는 프로젝트에서 기획서/출력 폴더가 없어 사용자가 직접 만들어야 하는 것을 막는다 (D12).
            MCPToolFolders.EnsureWorkFolders(_settings);
            RefreshDesignDocList();

            _customAiCommand = EditorPrefs.GetString(PrefKeyCustomCommand, string.Empty);
            _aiTimeoutSeconds = EditorPrefs.GetInt(PrefKeyTimeout, 300);
            _aiAllowExplore = EditorPrefs.GetBool(PrefKeyAllowExplore, true);
            _manualFoldout = EditorPrefs.GetBool(PrefKeyManualFoldout, false);
            _extractFromDoc = EditorPrefs.GetBool(PrefKeyExtractFromDoc, false);
            RefreshAiToolList(false);
        }

        private void OnDisable()
        {
            CancelAiRun();
        }

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            DrawScanOnlyToggle();
            EditorGUILayout.Space(4f);
            DrawAiSection();
            EditorGUILayout.Space(6f);
            DrawManualSection();

            EditorGUILayout.Space(6f);
            DrawItemTable();
            EditorGUILayout.Space(4f);
            DrawBottomBar();
        }

        /// <summary>
        /// "스캔 대상 씬" 목록 UI를 그립니다. 여기서 지정한 씬만 씬 직접 배치 오브젝트 스캔 대상이 되며,
        /// 스캔 실행 시 프리팹 스캔 결과와 병합됩니다.
        /// </summary>
        private void DrawSceneListSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent("스캔 대상 씬",
                        "지정한 씬에 직접 배치된(프리팹 인스턴스가 아닌) 오브젝트의 슬롯도 스캔합니다. " +
                        "지정하지 않으면 프리팹만 스캔합니다."),
                    GUILayout.Width(EditorGUIUtility.labelWidth));

                if (GUILayout.Button("+ 추가", GUILayout.Width(SmallButtonWidth)))
                {
                    _scanScenes.Add(null);
                }
            }

            int removeSceneIndex = -1;
            for (int i = 0; i < _scanScenes.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    _scanScenes[i] = (SceneAsset)EditorGUILayout.ObjectField(
                        _scanScenes[i], typeof(SceneAsset), false);
                    if (GUILayout.Button("제거", GUILayout.Width(44f)))
                    {
                        removeSceneIndex = i;
                    }
                }
            }

            if (removeSceneIndex >= 0)
            {
                _scanScenes.RemoveAt(removeSceneIndex);
            }
        }

        /// <summary>지정된 스캔 대상 씬들의 에셋 경로 목록을 반환합니다 (미지정 슬롯 제외).</summary>
        private List<string> SceneScanPaths()
        {
            var paths = new List<string>();
            foreach (SceneAsset scene in _scanScenes)
            {
                if (scene == null)
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(scene);
                if (!string.IsNullOrEmpty(path) && !paths.Contains(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        /// <summary>프리팹 스캔과 지정 씬 스캔 결과를 병합해 반환합니다.</summary>
        private List<ScanEntry> CollectScanEntries()
        {
            List<ScanEntry> entries = ProjectScanner.ScanPrefabs(_scanRootPath);
            entries.AddRange(ProjectScanner.ScanScenes(SceneScanPaths()));
            return entries;
        }

        /// <summary>
        /// 스캔 전용(OFF) 경로의 슬롯 수집입니다. 열린 씬의 슬롯 유무와 무관하게 스캔 루트
        /// (<see cref="_scanRootPath"/>) 아래 프리팹 슬롯을 항상 함께 수집하며, 겹치는 프리팹은
        /// <see cref="ProjectScanner.ScanOpenScenesAndPrefabs"/>에서 한 번만 담깁니다.
        /// 결과가 0건이면 안내 다이얼로그를 띄우고 false를 반환합니다.
        /// </summary>
        /// <param name="entries">수집된 슬롯 목록.</param>
        /// <param name="summary">상태 메시지에 넣을 결과 구성 요약 (씬/프리팹 슬롯 수, 스캔 루트).</param>
        private bool TryCollectScanOnlyEntries(out List<ScanEntry> entries, out string summary)
        {
            string root = string.IsNullOrEmpty(_scanRootPath) ? "Assets" : _scanRootPath;
            entries = ProjectScanner.ScanOpenScenesAndPrefabs(root);

            int sceneSlots = 0;
            foreach (ScanEntry entry in entries)
            {
                if (string.IsNullOrEmpty(entry.prefabPath))
                {
                    sceneSlots++;
                }
            }

            summary = $"열린 씬 슬롯 {sceneSlots}개 + 프리팹 슬롯 {entries.Count - sceneSlots}개(스캔 루트 \"{root}\", 중복 제거) = 총 {entries.Count}개";

            if (entries.Count == 0)
            {
                EditorUtility.DisplayDialog("에셋 리스트업",
                    "스캔 가능한 슬롯(Image/RawImage/SpriteRenderer/AudioSource)을 찾지 못했습니다.\n" +
                    $"열려 있는 씬과 스캔 루트(\"{root}\")의 프리팹 어디에도 슬롯이 없습니다.\n" +
                    "슬롯이 있는 프리팹이 든 폴더로 스캔 루트를 바꾼 뒤 다시 실행해주세요. " +
                    "(저장되지 않은 임시 씬은 제외됩니다)", "확인");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 상단 "기획서에서 항목 추측해 추가" 토글을 그립니다 (레이아웃을 바꾸거나 다른 섹션을 숨기지 않습니다).
        /// 켜면(ON) 목록 생성 버튼이 기획서에서 항목을 추측해 추가하는 기존 추출 방식으로 동작합니다.
        /// 끄면(OFF, 기본) 항목은 열린 씬 슬롯 + 스캔 루트 전체의 프리팹 스캔 결과에서만 만들어지고, 선택한 기획서는
        /// 새 항목을 추가하지 않고 각 스캔 항목의 설명/용도를 채우는 참고 자료로만 쓰입니다. 상태는 EditorPrefs에 유지됩니다.
        /// </summary>
        private void DrawScanOnlyToggle()
        {
            bool newValue = EditorGUILayout.ToggleLeft(
                new GUIContent("기획서에서 항목 추측해 추가 (끄면 스캔 항목만 · 기획서는 설명 참고용)",
                    "켜면 기획서를 읽어 스캔에 없는 항목까지 추측해 추가합니다(기존 AI/휴리스틱 추출). " +
                    "끄면(기본) 항목은 열린 씬 슬롯과 스캔 루트 아래 모든 프리팹의 스캔 결과에서만 만들고, 선택한 기획서는 " +
                    "새 항목을 추가하지 않은 채 각 스캔 항목의 설명/용도를 채우는 참고 자료로만 사용합니다."),
                _extractFromDoc, EditorStyles.boldLabel);
            if (newValue != _extractFromDoc)
            {
                _extractFromDoc = newValue;
                EditorPrefs.SetBool(PrefKeyExtractFromDoc, _extractFromDoc);
            }
        }

        /// <summary>기획서 파일 드롭다운 + [새로고침] 한 줄을 그립니다 (AI 연동/스캔 전용 모드 공용).</summary>
        private void DrawDesignDocPicker(string label, string emptyMessage)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_designDocPaths.Length == 0)
                {
                    EditorGUILayout.HelpBox(emptyMessage, MessageType.Warning);
                }
                else
                {
                    _selectedDocIndex = EditorGUILayout.Popup(label,
                        Mathf.Clamp(_selectedDocIndex, 0, _designDocPaths.Length - 1), _designDocPaths);
                }

                if (GUILayout.Button("새로고침", GUILayout.Width(SmallButtonWidth)))
                {
                    RefreshDesignDocList();
                }
            }
        }

        /// <summary>
        /// 열린 씬 슬롯 + 스캔 루트 전체의 프리팹 스캔 결과만으로 목록 문서를 생성합니다
        /// (기존 목록이 있으면 교체 확인). 선택된 기획서 경로는 항목 추출 없이 문서의 designDocPath로만 기록됩니다.
        /// </summary>
        private void RunScanOnlyBuild()
        {
            try
            {
                if (!TryCollectScanOnlyEntries(out List<ScanEntry> entries, out string scanSummary))
                {
                    return;
                }

                if (_document != null && _document.items.Count > 0 &&
                    !EditorUtility.DisplayDialog("에셋 리스트업",
                        $"기존 목록({_document.items.Count}개)을 스캔 결과로 교체할까요?", "교체", "취소"))
                {
                    return;
                }

                _document = AssetListBuilder.BuildFromScan(entries, _scanRootPath, SelectedDocPath());
                _statusMessage = $"스캔 완료 — {scanSummary}로 항목 {_document.items.Count}개를 생성했습니다. " +
                                 (string.IsNullOrEmpty(_document.designDocPath)
                                     ? string.Empty
                                     : $"(기획서 컨텍스트: {_document.designDocPath}) ") +
                                 "이름/종류를 확인 후 [저장]하고, 프롬프트는 2단계에서 작성하세요.";
            }
            catch (System.Exception e)
            {
                _statusMessage = string.Empty;
                EditorUtility.DisplayDialog("에셋 리스트업", $"스캔 중 오류가 발생했습니다.\n{e.Message}", "확인");
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
                    if (GUILayout.Button("스캔 + 휴리스틱 추출(보조)", GUILayout.Height(ButtonHeight)))
                    {
                        RunBuild();
                    }

                    if (GUILayout.Button("AI용 프롬프트 복사", GUILayout.Height(ButtonHeight)))
                    {
                        CopyAiPrompt();
                    }

                    if (GUILayout.Button("AI 응답 JSON 불러오기", GUILayout.Height(ButtonHeight)))
                    {
                        AssetListJsonImportWindow.Open(this);
                    }
                }
            }
        }

        private void DrawItemTable()
        {
            if (_document == null)
            {
                EditorGUILayout.HelpBox(
                    "[선택한 AI로 목록 생성]을 실행하세요. 기본(상단 토글 OFF)은 열린 씬 슬롯 + 스캔 루트 전체의 프리팹 " +
                    "스캔으로 항목을 만들고 " +
                    "선택한 기획서로 각 항목의 설명을 채웁니다(AI 없이 [스캔 + 휴리스틱 추출]을 쓰면 설명은 수동 보완). " +
                    "상단 [기획서에서 항목 추측해 추가]를 켜면 기획서에서 항목까지 추측해 추가합니다.",
                    MessageType.Info);
                GUILayout.FlexibleSpace();
                return;
            }

            EditorGUILayout.LabelField($"항목 목록 ({_document.items.Count}개)", EditorStyles.boldLabel);

            // 가로+세로 스크롤 하나에 헤더와 행을 함께 넣어 가로 스크롤 시 열이 어긋나지 않게 한다.
            // 표가 창의 남은 공간을 모두 차지하도록 ExpandHeight를 지정한다.
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
            }

            EditorGUILayout.EndScrollView();
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
            HeaderCell("UI 여부", ColUi);
            HeaderCell("대상 프리팹", ColPrefab);
            HeaderCell("대상 씬", ColScene);
            HeaderCell("대상 오브젝트", ColObject);
            HeaderCell("설명/용도", ColDesc);
            HeaderCell("상태", ColStatus);
            HeaderCell("삭제", ColDelete);
            EditorGUILayout.EndHorizontal();
        }

        private static void HeaderCell(string label, float width)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(width));
        }

        private bool DrawItemRow(AssetListItem item, int index)
        {
            bool incomplete = (string.IsNullOrEmpty(item.targetPrefabPath) && string.IsNullOrEmpty(item.targetScenePath))
                              || !item.IsUISpecified;

            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(RowHeight));
            if (Event.current.type == EventType.Repaint)
            {
                if (incomplete)
                {
                    // 미기록 항목은 옅은 경고색
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
                new GUIContent(item.id, incomplete ? "대상(프리팹/씬) 경로 또는 UI 여부가 비어 있습니다." : item.id),
                EditorStyles.miniLabel, GUILayout.Width(ColId));

            item.name = EditorGUILayout.TextField(item.name, GUILayout.Width(ColName));

            int typeIndex = System.Array.IndexOf(AssetTypeOptions, item.assetType);
            typeIndex = EditorGUILayout.Popup(Mathf.Max(0, typeIndex), AssetTypeOptions, GUILayout.Width(ColType));
            item.assetType = AssetTypeOptions[typeIndex];

            int uiIndex = Mathf.Clamp(item.uiFlag + 1, 0, 2); // -1/0/1 → 0/1/2
            uiIndex = EditorGUILayout.Popup(uiIndex, UiFlagOptions, GUILayout.Width(ColUi));
            item.uiFlag = uiIndex - 1;

            item.targetPrefabPath = EditorGUILayout.TextField(item.targetPrefabPath, GUILayout.Width(ColPrefab));
            item.targetScenePath = EditorGUILayout.TextField(item.targetScenePath, GUILayout.Width(ColScene));
            item.targetObjectPath = EditorGUILayout.TextField(item.targetObjectPath, GUILayout.Width(ColObject));
            item.description = EditorGUILayout.TextField(item.description, GUILayout.Width(ColDesc));
            item.status = EditorGUILayout.TextField(item.status, GUILayout.Width(ColStatus));

            bool remove = GUILayout.Button("삭제", EditorStyles.miniButton, GUILayout.Width(ColDelete));
            EditorGUILayout.EndHorizontal();
            return remove;
        }

        private void DrawBottomBar()
        {
            // 하단 고정 영역: 상태 메시지 + 항목 추가/저장
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_document == null))
                    {
                        if (GUILayout.Button("항목 추가", GUILayout.Height(ButtonHeight)))
                        {
                            _document.items.Add(new AssetListItem { id = NextItemId() });
                        }

                        if (GUILayout.Button("저장", GUILayout.Height(ButtonHeight)))
                        {
                            SaveDocument();
                        }
                    }
                }
            }
        }

        private void RefreshDesignDocList()
        {
            string docsRoot = _settings != null ? _settings.docsRootPath : "Assets/Docs";
            if (!Directory.Exists(docsRoot))
            {
                _designDocPaths = new string[0];
                return;
            }

            _designDocPaths = Directory.GetFiles(docsRoot, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".md") || p.EndsWith(".txt"))
                .Select(p => p.Replace('\\', '/'))
                .ToArray();
        }

        private string SelectedDocPath()
        {
            return _designDocPaths.Length == 0
                ? null
                : _designDocPaths[Mathf.Clamp(_selectedDocIndex, 0, _designDocPaths.Length - 1)];
        }

        private string NextItemId()
        {
            int max = 0;
            foreach (AssetListItem item in _document.items)
            {
                if (!string.IsNullOrEmpty(item.id) && item.id.StartsWith("item_") &&
                    int.TryParse(item.id.Substring(5), out int n) && n > max)
                {
                    max = n;
                }
            }

            return $"item_{max + 1:000}";
        }

        private void RunBuild()
        {
            // 스캔 전용(OFF) 모드: 기획서 휴리스틱 추출 대신 열린 씬 스캔으로 목록을 만든다
            // (수동/휴리스틱 경로에서는 AI 없이 스캔 항목 + 기획서를 컨텍스트로만 기록한다).
            if (!_extractFromDoc)
            {
                RunScanOnlyBuild();
                return;
            }

            if (_designDocPaths.Length == 0)
            {
                EditorUtility.DisplayDialog("에셋 리스트업",
                    $"기획서 파일이 없습니다.\n{_settings.docsRootPath} 폴더에 .md 또는 .txt 기획서를 넣어주세요.", "확인");
                return;
            }

            try
            {
                string docPath = SelectedDocPath();
                List<ScanEntry> entries = CollectScanEntries();
                _document = AssetListBuilder.Build(docPath, entries, _scanRootPath);
                _statusMessage = $"휴리스틱 추출 완료 — 스캔 슬롯 {entries.Count}개, 항목 {_document.items.Count}개. " +
                                 "정확한 분석은 AI 연동(프롬프트 복사/MCP)을 권장합니다.";
            }
            catch (System.Exception e)
            {
                _statusMessage = string.Empty;
                EditorUtility.DisplayDialog("에셋 리스트업", $"목록 생성 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        private void CopyAiPrompt()
        {
            try
            {
                string docPath = SelectedDocPath();
                string docText = null;
                if (!string.IsNullOrEmpty(docPath) && File.Exists(docPath))
                {
                    docText = File.ReadAllText(docPath);
                }

                List<ScanEntry> entries = CollectScanEntries();
                EditorGUIUtility.systemCopyBuffer = AssetListPromptBuilder.BuildPrompt(docText, entries);

                _statusMessage = docText == null
                    ? $"AI용 프롬프트를 클립보드에 복사했습니다 (기획서 없음, 스캔 슬롯 {entries.Count}개). " +
                      "AI에 붙여넣고, 응답 JSON을 [AI 응답 JSON 불러오기]로 반영하세요."
                    : $"AI용 프롬프트를 클립보드에 복사했습니다 (기획서: {docPath}, 스캔 슬롯 {entries.Count}개). " +
                      "AI에 붙여넣고, 응답 JSON을 [AI 응답 JSON 불러오기]로 반영하세요.";
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("에셋 리스트업", $"프롬프트 생성 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        private void DrawAiSection()
        {
            EditorGUILayout.LabelField("AI 연동", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // 입력: 기획서 + 스캔 루트
                DrawDesignDocPicker("기획서 파일", $"{_settings.docsRootPath} 폴더에 .md/.txt 기획서가 없습니다.");

                _scanRootPath = EditorGUILayout.TextField("스캔 루트", _scanRootPath);
                DrawSceneListSection();

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
                            "켜면 AI CLI를 프로젝트 루트에서 읽기 전용 도구 허용으로 실행해, Assets/ 아래 스크립트·씬·프리팹을 " +
                            "직접 읽으며 각 에셋의 역할을 추론합니다 (더 정확하지만 느리고 토큰 소모가 큼). " +
                            "끄면 기획서+스캔 요약만 프롬프트에 담아 일회성으로 묻습니다 (빠름). 파일 쓰기/명령 실행은 어느 쪽도 허용하지 않습니다."),
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
                else if (GUILayout.Button("선택한 AI로 목록 생성", GUILayout.Height(PrimaryButtonHeight)))
                {
                    RunSelectedAi();
                }
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

            // 스캔 전용(OFF) 모드: 항목은 스캔으로만 만들고, AI는 기획서로 각 항목 설명을 채운다.
            // AI를 쓸 수 없는 경우(복사 전용/기획서 미선택)는 스캔만 수행하는 폴백으로 처리한다.
            bool describeMode = !_extractFromDoc;
            if (describeMode && (option == CopyOnlyOption || string.IsNullOrEmpty(SelectedDocPath())))
            {
                RunScanOnlyBuild();
                return;
            }

            if (!describeMode && option == CopyOnlyOption)
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

            string prompt;
            _pendingScannedDoc = null;
            _pendingScanSummary = string.Empty;
            try
            {
                string docPath = SelectedDocPath();
                string docText = !string.IsNullOrEmpty(docPath) && File.Exists(docPath)
                    ? File.ReadAllText(docPath)
                    : null;

                if (describeMode)
                {
                    // 스캔 결과로 고정 항목을 만들고(기획서는 designDocPath 컨텍스트로 기록),
                    // AI에는 그 항목들의 설명만 기획서로 채우게 한다.
                    if (!TryCollectScanOnlyEntries(out List<ScanEntry> scanEntries, out string scanSummary))
                    {
                        return;
                    }

                    _pendingScanSummary = scanSummary;
                    _pendingScannedDoc = AssetListBuilder.BuildFromScan(scanEntries, _scanRootPath, docPath);
                    prompt = AssetListPromptBuilder.BuildDescribeScannedPrompt(docText, _pendingScannedDoc.items);
                }
                else
                {
                    List<ScanEntry> entries = CollectScanEntries();
                    prompt = _aiAllowExplore
                        ? AssetListPromptBuilder.BuildExplorationPrompt(docText, entries)
                        : AssetListPromptBuilder.BuildPrompt(docText, entries);
                }
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("AI 연동", $"프롬프트 생성 중 오류가 발생했습니다.\n{e.Message}", "확인");
                return;
            }

            // 탐색 모드: 프로젝트 루트를 작업 디렉터리로 지정해 상대 경로(Assets/...) 읽기를 가능하게 한다.
            // Editor 코드이므로 Application.dataPath의 부모가 항상 Unity 프로젝트 루트다.
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

                if (describeMode && _pendingScannedDoc != null)
                {
                    // 설명 채우기는 실패했지만 스캔 항목은 살린다 (기획서는 컨텍스트로 이미 기록됨).
                    _document = _pendingScannedDoc;
                    _pendingScannedDoc = null;
                    _statusMessage = $"AI 설명 채우기에 실패해 스캔 항목만 반영했습니다 ({_pendingScanSummary}). " +
                                     "설명은 수동으로 보완하거나 다시 실행하세요.";
                    _pendingScanSummary = string.Empty;
                }
                else if (!string.IsNullOrEmpty(result.stdout))
                {
                    AssetListJsonImportWindow.Open(this, result.stdout); // 수동 보정 경로
                }

                return;
            }

            if (describeMode)
            {
                ApplyScanDescriptions(result.stdout);
            }
            else
            {
                TryApplyAiResponse(result.stdout);
            }
        }

        /// <summary>
        /// 스캔 전용(OFF) 모드에서 AI가 반환한 설명({id, description})을 스캔 항목의 description에 반영합니다.
        /// 항목 자체는 스캔 결과(<see cref="_pendingScannedDoc"/>)를 그대로 사용하며, 파싱 실패 시에도
        /// 스캔 항목은 유지합니다.
        /// </summary>
        private void ApplyScanDescriptions(string responseText)
        {
            if (_pendingScannedDoc == null)
            {
                return;
            }

            int filled = 0;
            try
            {
                List<AssetListItem> described = AssetListPromptBuilder.ParseItemsJson(responseText);
                var byId = new Dictionary<string, string>();
                foreach (AssetListItem d in described)
                {
                    if (!string.IsNullOrEmpty(d.id) && !string.IsNullOrEmpty(d.description))
                    {
                        byId[d.id] = d.description;
                    }
                }

                foreach (AssetListItem item in _pendingScannedDoc.items)
                {
                    if (byId.TryGetValue(item.id, out string desc))
                    {
                        item.description = desc;
                        filled++;
                    }
                }

                _document = _pendingScannedDoc;
                _statusMessage = $"스캔 항목 {_document.items.Count}개 생성({_pendingScanSummary}) · " +
                                 $"기획서 참고 설명 {filled}개 반영 완료. 확인 후 [저장]하세요.";
            }
            catch (System.Exception e)
            {
                // 설명 파싱 실패: 스캔 항목은 살리고 원문을 보정 창에 넘겨 안내한다.
                _document = _pendingScannedDoc;
                _statusMessage = $"AI 설명 응답 파싱 실패 — 스캔 항목만 반영했습니다 ({_pendingScanSummary}).";
                EditorUtility.DisplayDialog("AI 설명 응답을 해석할 수 없습니다",
                    $"{e.Message}\n\n스캔 항목은 반영했으니 설명은 수동으로 보완하거나 다시 실행하세요.", "확인");
            }
            finally
            {
                _pendingScannedDoc = null;
                _pendingScanSummary = string.Empty;
                Repaint();
            }
        }

        private void TryApplyAiResponse(string responseText)
        {
            List<AssetListItem> items;
            try
            {
                items = AssetListPromptBuilder.ParseItemsJson(responseText);
            }
            catch (System.Exception e)
            {
                _statusMessage = "AI 응답 파싱 실패 — [AI 응답 JSON 불러오기] 창에서 수동 보정하세요.";
                EditorUtility.DisplayDialog("AI 응답을 해석할 수 없습니다",
                    $"{e.Message}\n\n응답 원문을 [AI 응답 JSON 불러오기] 창에 넣어두었으니 JSON 부분만 남기고 다시 시도해주세요.", "확인");
                AssetListJsonImportWindow.Open(this, responseText);
                return;
            }

            bool hasExisting = _document != null && _document.items.Count > 0;
            if (hasExisting)
            {
                int choice = EditorUtility.DisplayDialogComplex("AI 응답 반영",
                    $"AI가 {items.Count}개 항목을 생성했습니다.\n기존 목록({_document.items.Count}개)에 어떻게 반영할까요?",
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
        /// AI 응답 JSON에서 파싱된 항목을 현재 목록에 반영합니다 (보조 창에서 호출).
        /// </summary>
        /// <param name="items">파싱된 항목 목록.</param>
        /// <param name="merge">true면 기존 목록 뒤에 병합, false면 교체.</param>
        internal void ApplyImportedItems(List<AssetListItem> items, bool merge)
        {
            if (_document == null || !merge)
            {
                _document = new AssetListDocument
                {
                    designDocPath = SelectedDocPath() ?? string.Empty,
                    scanRootPath = _scanRootPath,
                    createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
            }

            foreach (AssetListItem item in items)
            {
                if (string.IsNullOrEmpty(item.id))
                {
                    item.id = string.Empty; // 아래에서 재부여
                }

                _document.items.Add(item);
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
                EditorUtility.DisplayDialog("에셋 리스트업", "저장할 항목이 없습니다.", "확인");
                return;
            }

            // 대상 프리팹 경로 또는 UI 여부가 미기록된 항목은 차단하지 않고, 확인 후 상태="대상 미정"으로 저장한다.
            List<AssetListItem> incomplete = _document.items
                .Where(i => (string.IsNullOrEmpty(i.targetPrefabPath) && string.IsNullOrEmpty(i.targetScenePath))
                            || !i.IsUISpecified)
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

                bool proceed = EditorUtility.DisplayDialog("대상 미기록 항목 확인",
                    $"대상 미기록 항목 {incomplete.Count}건이 있습니다.\n({examples})\n\n" +
                    "해당 항목은 상태가 \"대상 미정\"으로 기록됩니다. 그래도 저장할까요?",
                    "저장", "취소");
                if (!proceed)
                {
                    return;
                }

                foreach (AssetListItem item in incomplete)
                {
                    item.status = "대상 미정"; // 후속 단계에서 식별용
                }
            }

            try
            {
                string savedPath = AssetListBuilder.Save(_document);
                _statusMessage = $"저장 완료: {savedPath}";
                ShowNotification(new GUIContent("목록을 저장했습니다."));
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("에셋 리스트업", $"저장 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }
    }

    /// <summary>
    /// AI가 출력한 에셋 목록 JSON 배열을 붙여넣거나 파일로 불러와
    /// <see cref="AssetListupWindow"/>의 목록에 반영하는 보조 창입니다.
    /// </summary>
    public class AssetListJsonImportWindow : EditorWindow
    {
        private AssetListupWindow _owner;
        private string _jsonText = string.Empty;
        private Vector2 _scroll;

        /// <summary>보조 창을 엽니다.</summary>
        /// <param name="owner">항목을 반영할 에셋 리스트업 창.</param>
        /// <param name="initialText">미리 채워 넣을 응답 원문 (선택). AI 실행 실패 시 수동 보정용.</param>
        public static void Open(AssetListupWindow owner, string initialText = null)
        {
            var window = GetWindow<AssetListJsonImportWindow>(true);
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
                    "에셋 리스트업 창을 찾을 수 없습니다. Tools/MCP/1. Asset Listup 창을 연 뒤 다시 시도해주세요.", "확인");
                return;
            }

            try
            {
                List<AssetListItem> items = AssetListPromptBuilder.ParseItemsJson(_jsonText);
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
