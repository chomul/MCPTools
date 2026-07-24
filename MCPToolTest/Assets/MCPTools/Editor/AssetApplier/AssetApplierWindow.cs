using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 4단계 적용 창입니다. (메뉴: Tools/MCP/4. Asset Applier)
    /// AssetList JSON을 로드해 항목별 상태(확정본 없음/검증 실패/적용 준비/적용됨)를 표시하고,
    /// 현재 값과 새 확정본을 비교 미리보기한 뒤 개별/일괄 적용합니다.
    /// </summary>
    public class AssetApplierWindow : EditorWindow
    {
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const float ThumbnailSize = 140f;
        private const float LeftColumnWidth = 380f;

        /// <summary>항목 1개의 표시 상태입니다.</summary>
        private class ItemState
        {
            public AssetListItem item;
            public string confirmedPath;      // 확정본 경로 (null이면 없음)
            public List<string> reasons = new List<string>();
            public bool applied;

            /// <summary>상태 배지 문자열입니다.</summary>
            public string Badge
            {
                get
                {
                    if (applied) { return "적용됨"; }
                    if (string.IsNullOrEmpty(confirmedPath)) { return "확정본 없음"; }
                    return reasons.Count > 0 ? "검증 실패" : "적용 준비";
                }
            }

            /// <summary>검증 통과·미적용 여부 (일괄 적용 대상).</summary>
            public bool ReadyToApply
            {
                get { return !applied && !string.IsNullOrEmpty(confirmedPath) && reasons.Count == 0; }
            }
        }

        private MCPToolSettings _settings;
        private string[] _assetListPaths = new string[0];
        private int _selectedListIndex;
        private AssetListDocument _document;
        private readonly List<ItemState> _states = new List<ItemState>();
        private int _selectedIndex = -1;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _statusMessage = string.Empty;
        private string _loadedListPath;
        private bool _targetsDirty;

        /// <summary>적용 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/4. Asset Applier", false, 4)]
        public static void Open()
        {
            var window = GetWindow<AssetApplierWindow>();
            window.titleContent = new GUIContent("에셋 적용");
            window.minSize = new Vector2(860f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();
            RefreshAssetListPaths();
        }

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            DrawInputSection();
            EditorGUILayout.Space(6f);

            if (_document != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(LeftColumnWidth)))
                    {
                        DrawItemList();
                    }

                    using (new EditorGUILayout.VerticalScope())
                    {
                        _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
                        DrawDetailSection();
                        EditorGUILayout.EndScrollView();
                    }
                }

                DrawApplyButtons();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                }
            }
        }

        // ─────────────────────────── 입력 ───────────────────────────

        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("입력", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_assetListPaths.Length == 0)
                    {
                        EditorGUILayout.HelpBox(
                            $"{MCPToolFolders.AssetListDir(_settings)} 폴더에 AssetList_*.json이 없습니다. " +
                            "1단계(Tools/MCP/1. Asset Listup)에서 먼저 저장해주세요.", MessageType.Warning);
                    }
                    else
                    {
                        _selectedListIndex = EditorGUILayout.Popup("AssetList JSON",
                            Mathf.Clamp(_selectedListIndex, 0, _assetListPaths.Length - 1), _assetListPaths);
                    }

                    if (GUILayout.Button("새로고침", GUILayout.Width(SmallButtonWidth)))
                    {
                        RefreshAssetListPaths();
                    }

                    using (new EditorGUI.DisabledScope(_assetListPaths.Length == 0))
                    {
                        if (GUILayout.Button("로드", GUILayout.Width(SmallButtonWidth)))
                        {
                            LoadSelectedAssetList();
                        }
                    }
                }
            }
        }

        // ─────────────────────────── 항목 목록 ───────────────────────────

        private void DrawItemList()
        {
            EditorGUILayout.LabelField($"항목 ({_states.Count}개)", EditorStyles.boldLabel);

            var rowStyle = new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };
            var badgeStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _states.Count; i++)
            {
                ItemState state = _states[i];
                bool selected = i == _selectedIndex;

                using (new EditorGUILayout.HorizontalScope())
                {
                    string rowLabel =
                        $"{(selected ? "▶ " : "   ")}{state.item.id}  {state.item.name}  " +
                        $"[{state.item.assetType}{(state.item.IsUI ? "/UI" : string.Empty)}{(state.item.IsSceneItem ? "/씬" : string.Empty)}]";
                    if (GUILayout.Button(rowLabel, rowStyle, GUILayout.Height(20f), GUILayout.ExpandWidth(true)))
                    {
                        _selectedIndex = i;
                        GUI.FocusControl(null);
                    }

                    badgeStyle.normal.textColor = BadgeColor(state);
                    EditorGUILayout.LabelField(state.Badge, badgeStyle, GUILayout.Width(80f), GUILayout.Height(20f));
                }
            }

            EditorGUILayout.EndScrollView();

            int ready = _states.Count(s => s.ReadyToApply);
            int applied = _states.Count(s => s.applied);
            EditorGUILayout.LabelField($"적용 준비 {ready}개 · 적용됨 {applied}개 / 전체 {_states.Count}개",
                EditorStyles.miniLabel);
        }

        private static Color BadgeColor(ItemState state)
        {
            if (state.applied) { return new Color(0.2f, 0.75f, 0.3f); }
            if (string.IsNullOrEmpty(state.confirmedPath)) { return Color.gray; }
            return state.reasons.Count > 0
                ? new Color(0.85f, 0.3f, 0.25f)
                : new Color(0.35f, 0.6f, 0.95f);
        }

        // ─────────────────────────── 상세/미리보기 ───────────────────────────

        private void DrawDetailSection()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _states.Count)
            {
                EditorGUILayout.HelpBox("왼쪽 목록에서 항목을 선택하면 미리보기가 표시됩니다.", MessageType.Info);
                return;
            }

            ItemState state = _states[_selectedIndex];
            AssetListItem item = state.item;

            EditorGUILayout.LabelField("적용 대상 (수정 가능)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();

                if (item.IsSceneItem)
                {
                    EditorGUILayout.LabelField("씬", item.targetScenePath);
                    item.targetObjectPath = EditorGUILayout.TextField(
                        new GUIContent("내부 경로", "씬 루트 오브젝트 기준 계층 경로 (예: Canvas/HpBar/Fill)."),
                        item.targetObjectPath);
                }
                else
                {
                    DrawPrefabTargetFields(item);
                }

                if (item.assetType == "audio")
                {
                    item.targetComponent = EditorGUILayout.TextField(
                        new GUIContent("대상 컴포넌트", "(선택) AudioSource.clip 대신 적용할 컴포넌트 타입 이름 (예: PlayerController). " +
                                                    "대상 필드와 함께 지정해야 합니다."),
                        item.targetComponent);
                    item.targetField = EditorGUILayout.TextField(
                        new GUIContent("대상 필드", "(선택) 위 컴포넌트의 직렬화 AudioClip 필드 경로 (예: jumpSound)."),
                        item.targetField);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    _targetsDirty = true;
                    RevalidateState(state);
                }

                EditorGUILayout.LabelField("기대 컴포넌트", string.Join(" / ", AssetApplier.ExpectedComponentNames(item)));
                EditorGUILayout.LabelField("확정본", string.IsNullOrEmpty(state.confirmedPath) ? "(없음)" : state.confirmedPath);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (_targetsDirty)
                    {
                        EditorGUILayout.LabelField("수정됨 (저장되지 않음)", EditorStyles.miniLabel, GUILayout.Width(140f));
                    }

                    using (new EditorGUI.DisabledScope(!_targetsDirty))
                    {
                        if (GUILayout.Button("저장", GUILayout.Width(SmallButtonWidth), GUILayout.Height(ButtonHeight)))
                        {
                            SaveTargetEdits();
                        }
                    }
                }
            }

            if (state.reasons.Count > 0)
            {
                EditorGUILayout.HelpBox("검증 실패:\n- " + string.Join("\n- ", state.reasons), MessageType.Error);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("미리보기 (현재 → 새 확정본)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                DrawPreviewCell("현재 값", AssetApplier.GetCurrentValue(item));

                GUILayout.Space(12f);
                EditorGUILayout.LabelField("→", GUILayout.Width(20f), GUILayout.Height(ThumbnailSize * 0.5f));
                GUILayout.Space(12f);

                UnityEngine.Object newAsset = null;
                if (!string.IsNullOrEmpty(state.confirmedPath))
                {
                    newAsset = item.assetType == "audio"
                        ? AssetDatabase.LoadAssetAtPath<AudioClip>(state.confirmedPath)
                        : (UnityEngine.Object)AssetDatabase.LoadAssetAtPath<Texture2D>(state.confirmedPath);
                }

                DrawPreviewCell("새 확정본", newAsset);
                GUILayout.FlexibleSpace();
            }
        }

        /// <summary>
        /// 프리팹 항목의 대상 프리팹(ObjectField)·내부 경로(계층 경로 드롭다운, 프리팹에 없는 값이면 텍스트 필드)를 그립니다.
        /// </summary>
        private void DrawPrefabTargetFields(AssetListItem item)
        {
            var currentPrefab = string.IsNullOrEmpty(item.targetPrefabPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);

            var newPrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("프리팹", "적용 대상 프리팹 에셋입니다. 변경 시 경로가 자동으로 반영됩니다."),
                currentPrefab, typeof(GameObject), false);
            if (newPrefab != currentPrefab)
            {
                item.targetPrefabPath = newPrefab != null
                    ? AssetDatabase.GetAssetPath(newPrefab).Replace('\\', '/')
                    : string.Empty;
                GUI.changed = true;
            }

            EditorGUILayout.LabelField("프리팹 경로",
                string.IsNullOrEmpty(item.targetPrefabPath) ? "(미지정)" : item.targetPrefabPath,
                EditorStyles.miniLabel);

            GameObject prefab = string.IsNullOrEmpty(item.targetPrefabPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);

            if (prefab == null)
            {
                item.targetObjectPath = EditorGUILayout.TextField(
                    new GUIContent("내부 경로", "프리팹 루트 기준 계층 경로. 프리팹을 지정하면 드롭다운으로 선택할 수 있습니다."),
                    item.targetObjectPath);
                return;
            }

            // 프리팹 계층 경로 드롭다운 ("(루트)" = 빈 경로)
            List<string> values = ObjectPathOptions(prefab);
            var labels = new List<string>(values.Count + 1) { "(루트)" };
            labels.AddRange(values);
            values.Insert(0, string.Empty);

            int index = values.IndexOf(item.targetObjectPath ?? string.Empty);
            if (index < 0)
            {
                // 현재 값이 프리팹 계층에 없는 경우: 텍스트 필드 유지 + 드롭다운으로 교체 선택 가능
                item.targetObjectPath = EditorGUILayout.TextField(
                    new GUIContent("내부 경로", "현재 값이 프리팹 계층에 없습니다. 직접 수정하거나 아래에서 선택하세요."),
                    item.targetObjectPath);
                int picked = EditorGUILayout.Popup(new GUIContent("경로 선택"), 0, PrependChoose(labels));
                if (picked > 0)
                {
                    item.targetObjectPath = values[picked - 1];
                    GUI.changed = true;
                }
            }
            else
            {
                int newIndex = EditorGUILayout.Popup(new GUIContent("내부 경로"), index, labels.ToArray());
                if (newIndex != index)
                {
                    item.targetObjectPath = values[newIndex];
                    GUI.changed = true;
                }
            }
        }

        /// <summary>드롭다운 앞에 "(선택...)" 항목을 붙인 라벨 배열을 만듭니다.</summary>
        private static string[] PrependChoose(List<string> labels)
        {
            var result = new List<string>(labels.Count + 1) { "(선택...)" };
            result.AddRange(labels);
            return result.ToArray();
        }

        /// <summary>
        /// 프리팹의 모든 Transform 계층 경로 목록을 만듭니다 (루트 이름 포함, 스캐너·적용 로직과 같은 규칙).
        /// </summary>
        private static List<string> ObjectPathOptions(GameObject prefab)
        {
            var options = new List<string>();
            foreach (Transform transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                var segments = new List<string>();
                Transform current = transform;
                while (current != null)
                {
                    segments.Add(current.name);
                    current = current.parent;
                }

                segments.Reverse();
                options.Add(string.Join("/", segments));
            }

            return options;
        }

        /// <summary>선택 항목의 확정본 탐색·검증을 다시 수행합니다 (대상 수정 직후 호출).</summary>
        private void RevalidateState(ItemState state)
        {
            state.confirmedPath = AssetApplier.FindConfirmedAssetPath(_settings, state.item);
            state.reasons = string.IsNullOrEmpty(state.confirmedPath)
                ? new List<string>()
                : AssetApplier.ValidateItem(state.item, state.confirmedPath);
        }

        /// <summary>수정된 적용 대상 값을 로드했던 AssetList JSON 파일에 저장합니다.</summary>
        private void SaveTargetEdits()
        {
            if (_document == null || string.IsNullOrEmpty(_loadedListPath))
            {
                return;
            }

            try
            {
                string savedPath = AssetListBuilder.Save(_document, _loadedListPath);
                _targetsDirty = false;
                _statusMessage = $"적용 대상 수정 저장 완료: {savedPath}";
                ShowNotification(new GUIContent("대상 수정을 저장했습니다."));
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("에셋 적용",
                    $"AssetList JSON 저장 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        /// <summary>미리보기 셀 1개를 그립니다 (텍스처/스프라이트는 썸네일, 그 외는 이름 표시).</summary>
        private static void DrawPreviewCell(string label, UnityEngine.Object value)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ThumbnailSize + 8f)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(ThumbnailSize));
                Rect rect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize,
                    GUILayout.Width(ThumbnailSize), GUILayout.Height(ThumbnailSize));

                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f),
                        new Color(0f, 0f, 0f, 0.25f));
                }

                Texture texture = null;
                if (value is Texture t)
                {
                    texture = t;
                }
                else if (value is Sprite sprite)
                {
                    texture = sprite.texture;
                }

                if (texture != null)
                {
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
                }
                else
                {
                    GUI.Box(rect, value != null ? value.name : "(없음)");
                }

                EditorGUILayout.LabelField(value != null ? value.name : "(없음)", EditorStyles.miniLabel,
                    GUILayout.Width(ThumbnailSize));
            }
        }

        // ─────────────────────────── 적용 ───────────────────────────

        private void DrawApplyButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                ItemState selected = _selectedIndex >= 0 && _selectedIndex < _states.Count
                    ? _states[_selectedIndex]
                    : null;

                using (new EditorGUI.DisabledScope(selected == null || !selected.ReadyToApply))
                {
                    if (GUILayout.Button("선택 적용", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        ApplyStates(new List<ItemState> { selected });
                    }
                }

                List<ItemState> ready = _states.Where(s => s.ReadyToApply).ToList();
                using (new EditorGUI.DisabledScope(ready.Count == 0))
                {
                    if (GUILayout.Button($"일괄 적용 (검증 통과 {ready.Count}개)", GUILayout.Height(PrimaryButtonHeight),
                            GUILayout.Width(220f)))
                    {
                        ApplyStates(ready);
                    }
                }
            }
        }

        /// <summary>
        /// 대상 항목들을 적용하고 성공/실패 요약을 표시합니다.
        /// 씬 항목은 프리팹 항목과 함께 <see cref="AssetApplier.ApplyBatch"/>로 처리되며,
        /// 같은 씬 항목은 묶어서 씬을 한 번만 열어 적용합니다.
        /// </summary>
        private void ApplyStates(List<ItemState> targets)
        {
            var failures = new List<string>();
            int done = 0;

            List<AssetListItem> items = targets.Select(s => s.item).ToList();
            List<string> assetPaths = targets.Select(s => s.confirmedPath).ToList();
            List<ApplyResult> results = AssetApplier.ApplyBatch(items, assetPaths);

            for (int i = 0; i < targets.Count; i++)
            {
                ItemState state = targets[i];
                ApplyResult result = results[i];
                if (result != null && result.success)
                {
                    state.applied = true;
                    done++;
                }
                else
                {
                    failures.Add($"{state.item.id} ({state.item.name}): {(result != null ? result.message : "(결과 없음)")}");
                }
            }

            AssetDatabase.SaveAssets();

            _statusMessage = failures.Count == 0
                ? $"적용 완료: {done}/{targets.Count}개 성공. (Ctrl+Z로 되돌릴 수 있습니다)"
                : $"적용 완료: 성공 {done}개, 실패 {failures.Count}개.";

            if (failures.Count > 0)
            {
                EditorUtility.DisplayDialog("에셋 적용 — 실패 항목 요약",
                    $"성공 {done}개 / 실패 {failures.Count}개\n\n실패 항목:\n" + string.Join("\n", failures), "확인");
            }

            Repaint();
        }

        // ─────────────────────────── 헬퍼 ───────────────────────────

        private void RefreshAssetListPaths()
        {
            // 새 하위 폴더(1_AssetList)와 구 위치(Docs 루트)를 함께 훑는다. 최신 파일이 앞에 온다.
            _assetListPaths = MCPToolFolders.FindDocuments(
                MCPToolFolders.DocsRoot(_settings), MCPToolFolders.AssetListFolder, "AssetList_*.json");
        }

        private void LoadSelectedAssetList()
        {
            string path = _assetListPaths[Mathf.Clamp(_selectedListIndex, 0, _assetListPaths.Length - 1)];
            try
            {
                var dict = MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
                AssetListDocument doc = AssetListDocument.FromDictionary(dict);
                if (doc == null || doc.items.Count == 0)
                {
                    EditorUtility.DisplayDialog("에셋 적용",
                        $"AssetList JSON에서 항목을 읽지 못했습니다: {path}", "확인");
                    return;
                }

                _document = doc;
                _loadedListPath = path;
                _targetsDirty = false;
                _selectedIndex = -1;
                RebuildStates();
                _statusMessage = $"로드 완료: {path} (항목 {doc.items.Count}개)";
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("에셋 적용",
                    $"AssetList JSON 로드 중 오류가 발생했습니다.\n{e.Message}", "확인");
            }
        }

        /// <summary>항목별 확정본 탐색·검증을 다시 수행합니다.</summary>
        private void RebuildStates()
        {
            _states.Clear();
            if (_document == null)
            {
                return;
            }

            foreach (AssetListItem item in _document.items)
            {
                var state = new ItemState { item = item };
                state.confirmedPath = AssetApplier.FindConfirmedAssetPath(_settings, item);
                if (!string.IsNullOrEmpty(state.confirmedPath))
                {
                    state.reasons = AssetApplier.ValidateItem(item, state.confirmedPath);
                }

                _states.Add(state);
            }
        }
    }
}
