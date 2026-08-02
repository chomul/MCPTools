using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 4단계 적용 창입니다. (메뉴: Tools/AI Asset Pipeline/4. Asset Applier)
    /// AssetList JSON을 로드해 항목별 상태(확정본 없음/검증 실패/적용 준비/적용됨)를 표시하고,
    /// 현재 값과 새 확정본을 비교 미리보기한 뒤 개별/일괄 적용합니다.
    /// </summary>
    public class AssetApplierWindow : EditorWindow
    {
        private const float ButtonHeight = 22f;
        private const float PrimaryButtonHeight = 30f;
        private const float SmallButtonWidth = 80f;
        private const float ThumbnailSize = 140f;
        private const float SheetThumbnailSize = 96f;
        private const float LeftColumnWidth = 380f;

        /// <summary>항목 1개의 표시 상태입니다.</summary>
        private class ItemState
        {
            public AssetListItem item;
            public string confirmedPath;      // 확정본 경로 (null이면 없음)
            public List<string> reasons = new List<string>();
            public bool applied;

            /// <summary>
            /// 대상 컴포넌트가 현재 참조하는 값의 캐시입니다 (미리보기 "현재 값").
            /// <see cref="AssetApplier.GetCurrentValue"/>는 프리팹 로드·계층 탐색·SerializedObject 생성을 동반하므로
            /// 리페인트마다 호출하지 않고 선택 변경·대상 편집·적용 시점에만 갱신합니다 (Task 8 C1/M1).
            /// </summary>
            public UnityEngine.Object cachedCurrentValue;

            /// <summary><see cref="cachedCurrentValue"/>가 계산된 값인지 여부입니다 (false면 다음 표시 때 1회 계산).</summary>
            public bool currentValueCached;

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

            /// <summary>
            /// 확정본이 있고 검증을 통과했는지 여부입니다 (개별 [선택 적용] 대상).
            /// 이미 적용된 항목도 <b>의도적으로 다시 적용</b>할 수 있어야 하므로 <see cref="applied"/>는 보지 않습니다.
            /// </summary>
            public bool CanApplyAgain
            {
                get { return !string.IsNullOrEmpty(confirmedPath) && reasons.Count == 0; }
            }

            /// <summary>
            /// 검증 통과·미적용 여부입니다 (일괄 적용 대상).
            /// 이미 적용된 항목은 프리팹을 다시 저장하지 않도록 일괄 대상에서만 빠집니다.
            /// </summary>
            public bool ReadyToApply
            {
                get { return !applied && CanApplyAgain; }
            }
        }

        private AIAssetPipelineSettings _settings;
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

        /// <summary>
        /// 검증을 통과한 미적용 항목 수(<see cref="ItemState.ReadyToApply"/>)와 적용된 항목 수의 캐시입니다.
        /// 리페인트마다 LINQ로 세지 않고 상태가 바뀌는 시점
        /// (<see cref="RebuildStates"/>/<see cref="RevalidateState"/>/<see cref="ApplyStates"/>)에만 갱신합니다 (Task 8 C7).
        /// </summary>
        private int _readyCount;

        private int _appliedCount;

        /// <summary>
        /// 프리팹 계층 경로 드롭다운 캐시입니다 (Task 8 C2). 프리팹 경로가 바뀔 때만 다시 만듭니다.
        /// <see cref="_pathOptionValues"/>[0]은 루트(빈 경로)이며 라벨 배열과 인덱스가 1:1로 대응합니다.
        /// </summary>
        private string _pathOptionsPrefabPath;

        private List<string> _pathOptionValues = new List<string>();
        private string[] _pathOptionLabels = new string[0];
        private string[] _pathOptionChooseLabels = new string[0];

        /// <summary>확정본 SpriteSheets 폴더에서 찾은 시트 png 목록 (썸네일 선택기 항목).</summary>
        private string[] _sheetPaths = new string[0];

        /// <summary>시트 썸네일 선택기를 펼쳐 두었는지 여부입니다.</summary>
        private bool _sheetPickerOpen;

        /// <summary>적용 창을 엽니다.</summary>
        [MenuItem("Tools/AI Asset Pipeline/4. Asset Applier", false, 4)]
        public static void Open()
        {
            var window = GetWindow<AssetApplierWindow>();
            window.titleContent = new GUIContent("에셋 적용");
            window.minSize = new Vector2(860f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = AIAssetPipelineSettings.GetOrCreate();
            RefreshAssetListPaths();
            RefreshSpriteAssetPaths();
        }

        /// <summary>다른 창에서 만든 시트·컨트롤러가 드롭다운에 바로 보이도록 포커스마다 목록을 다시 읽습니다.</summary>
        private void OnFocus()
        {
            if (_settings == null)
            {
                _settings = AIAssetPipelineSettings.GetOrCreate();
            }

            RefreshSpriteAssetPaths();

            // 다른 창에서 프리팹 계층이나 대상 값을 바꿨을 수 있으므로 캐시를 비워 다시 계산하게 한다.
            _pathOptionsPrefabPath = null;
            InvalidateCurrentValues();
        }

        /// <summary>
        /// Play Mode·컴파일/임포트로 실행 버튼을 막아야 하는 사유입니다 (막지 않아도 되면 null).
        /// MCP 도구와 같은 판정(<see cref="AIAssetPipelineToolRegistry.GetBlockedReason"/>)을 쓰며 OnGUI마다 한 번 갱신합니다.
        /// </summary>
        private string _blockedReason;

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = AIAssetPipelineSettings.GetOrCreate();
            }

            _blockedReason = AIAssetPipelineToolRegistry.GetBlockedReason();
            if (_blockedReason != null)
            {
                EditorGUILayout.HelpBox(_blockedReason, MessageType.Warning);
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
                            $"{AIAssetPipelineFolders.AssetListDir(_settings)} 폴더에 AssetList_*.json이 없습니다. " +
                            "1단계(Tools/AI Asset Pipeline/1. Asset Listup)에서 먼저 저장해주세요.", MessageType.Warning);
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

                        // 선택 시점에만 현재 값·경로 목록을 다시 구한다 (리페인트 비용 제거 — C1/C2).
                        state.currentValueCached = false;
                        _pathOptionsPrefabPath = null;
                    }

                    badgeStyle.normal.textColor = BadgeColor(state);
                    EditorGUILayout.LabelField(state.Badge, badgeStyle, GUILayout.Width(80f), GUILayout.Height(20f));
                }
            }

            EditorGUILayout.EndScrollView();

            // 카운트는 상태가 바뀔 때만 갱신된 캐시를 쓴다 (리페인트마다 LINQ 집계를 돌리지 않는다 — C7).
            EditorGUILayout.LabelField($"적용 준비 {_readyCount}개 · 적용됨 {_appliedCount}개 / 전체 {_states.Count}개",
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

                if (item.assetType != "audio")
                {
                    DrawSpriteSourceFields(item);
                }

                // 오디오 항목은 항상, 그 외 항목은 임의 필드 대상 값이 있을 때 표시한다
                // (스캔된 커스텀 스크립트 Sprite 필드 항목도 여기서 확인·수정할 수 있게 하되,
                //  일반 이미지 항목의 UI는 종전과 동일하게 유지).
                if (item.assetType == "audio"
                    || !string.IsNullOrEmpty(item.targetComponent)
                    || !string.IsNullOrEmpty(item.targetField))
                {
                    item.targetComponent = EditorGUILayout.TextField(
                        new GUIContent("대상 컴포넌트", "(선택) 기본 컴포넌트 대신 적용할 컴포넌트(스크립트) 타입 이름 " +
                                                    "(예: PlayerController). 대상 필드와 함께 지정해야 합니다."),
                        item.targetComponent);
                    item.targetField = EditorGUILayout.TextField(
                        new GUIContent("대상 필드", "(선택) 위 컴포넌트의 직렬화 필드 경로 — 오디오 항목은 AudioClip 필드(예: jumpSound), " +
                                                 "이미지 항목은 Sprite 필드. 배열 원소는 \"필드명.Array.data[인덱스]\" 형식."),
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

                    // 대상 편집 저장은 AssetList JSON을 다시 쓰므로 Play Mode·컴파일 중에는 막는다 (R2).
                    using (new EditorGUI.DisabledScope(!_targetsDirty || _blockedReason != null))
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
                // 현재 값은 캐시에서 읽는다 (리페인트마다 프리팹 로드·SerializedObject 생성을 하지 않기 위함 — C1/M1).
                // 캐시가 비어 있는 경우(로드 직후 첫 표시)에만 여기서 1회 계산한다.
                if (!state.currentValueCached)
                {
                    RefreshCurrentValue(state);
                }

                DrawPreviewCell("현재 값", state.cachedCurrentValue);

                GUILayout.Space(12f);
                EditorGUILayout.LabelField("→", GUILayout.Width(20f), GUILayout.Height(ThumbnailSize * 0.5f));
                GUILayout.Space(12f);

                UnityEngine.Object newAsset = null;
                if (!string.IsNullOrEmpty(state.confirmedPath))
                {
                    if (item.assetType == "audio")
                    {
                        newAsset = AssetDatabase.LoadAssetAtPath<AudioClip>(state.confirmedPath);
                    }
                    else
                    {
                        // 실제 적용과 같은 규칙(이름 지정 → 에셋 전체 → 시트 첫 프레임)으로 찾아
                        // 미리보기와 적용 결과가 어긋나지 않게 한다.
                        newAsset = AssetApplier.FindItemSprite(state.confirmedPath, item);
                        if (newAsset == null)
                        {
                            newAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(state.confirmedPath);
                        }
                    }
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

            // ObjectField가 돌려준 오브젝트가 곧 현재 대상 프리팹이다 (같은 경로를 다시 로드하지 않는다 — C6).
            GameObject prefab = newPrefab;

            if (prefab == null)
            {
                item.targetObjectPath = EditorGUILayout.TextField(
                    new GUIContent("내부 경로", "프리팹 루트 기준 계층 경로. 프리팹을 지정하면 드롭다운으로 선택할 수 있습니다."),
                    item.targetObjectPath);
                return;
            }

            // 프리팹 계층 경로 드롭다운 ("(루트)" = 빈 경로). 계층 순회·문자열 조립은 프리팹이 바뀔 때만 한다 (C2).
            EnsurePathOptions(item.targetPrefabPath, prefab);
            List<string> values = _pathOptionValues;

            int index = values.IndexOf(item.targetObjectPath ?? string.Empty);
            if (index < 0)
            {
                // 현재 값이 프리팹 계층에 없는 경우: 텍스트 필드 유지 + 드롭다운으로 교체 선택 가능
                item.targetObjectPath = EditorGUILayout.TextField(
                    new GUIContent("내부 경로", "현재 값이 프리팹 계층에 없습니다. 직접 수정하거나 아래에서 선택하세요."),
                    item.targetObjectPath);
                int picked = EditorGUILayout.Popup(new GUIContent("경로 선택"), 0, _pathOptionChooseLabels);
                if (picked > 0)
                {
                    item.targetObjectPath = values[picked - 1];
                    GUI.changed = true;
                }
            }
            else
            {
                int newIndex = EditorGUILayout.Popup(new GUIContent("내부 경로"), index, _pathOptionLabels);
                if (newIndex != index)
                {
                    item.targetObjectPath = values[newIndex];
                    GUI.changed = true;
                }
            }
        }

        /// <summary>
        /// 프리팹 계층 경로 드롭다운의 값·라벨 배열을 준비합니다.
        /// 같은 프리팹 경로로 이미 만들어 두었으면 그대로 재사용하고,
        /// 프리팹 경로가 바뀐 경우(또는 캐시를 비운 경우)에만 계층을 순회해 다시 만듭니다 (Task 8 C2).
        /// </summary>
        /// <param name="prefabPath">대상 프리팹 경로 (캐시 키).</param>
        /// <param name="prefab">대상 프리팹 루트.</param>
        private void EnsurePathOptions(string prefabPath, GameObject prefab)
        {
            if (_pathOptionsPrefabPath == prefabPath && _pathOptionValues.Count > 0)
            {
                return;
            }

            List<string> values = ObjectPathOptions(prefab);

            var labels = new List<string>(values.Count + 1) { "(루트)" };
            labels.AddRange(values);
            values.Insert(0, string.Empty);

            _pathOptionValues = values;
            _pathOptionLabels = labels.ToArray();
            _pathOptionChooseLabels = PrependChoose(labels);
            _pathOptionsPrefabPath = prefabPath;
        }

        /// <summary>
        /// 이미지/UI 항목의 스프라이트 시트 선택 UI를 그립니다.
        /// 확정본 SpriteSheets 폴더의 시트를 <b>썸네일 그리드</b>로 보여주고, 고른 시트의 첫 프레임이 적용됩니다
        /// (재생 중에는 Animator가 프레임을 덮어쓰므로 초기 표시용입니다).
        /// 시트를 고르면 그 시트에서 만든 AnimatorController가 <b>자동으로 함께 연결</b>되므로
        /// 컨트롤러를 따로 고르지 않고, 연결될 대상만 아래에 표시합니다.
        /// </summary>
        private void DrawSpriteSourceFields(AssetListItem item)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("스프라이트 시트",
                    $"{AIAssetPipelineFolders.SpriteSheetsDir(_settings)} 폴더의 시트를 썸네일로 고릅니다. " +
                    "고르면 확정본 자동 탐색 대신 이 시트의 첫 프레임을 적용하고, 시트의 컨트롤러도 함께 연결합니다."));
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(item.spriteSheetPath)
                        ? "(미지정 — 확정본 자동 탐색)"
                        : Path.GetFileNameWithoutExtension(item.spriteSheetPath));

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(item.spriteSheetPath)))
                {
                    bool changedBeforeClear = GUI.changed;
                    bool clear = GUILayout.Button("해제", GUILayout.Width(50f));
                    GUI.changed = changedBeforeClear;
                    if (clear)
                    {
                        SelectSheet(item, string.Empty);
                    }
                }
            }

            // 폴드아웃 자체는 항목 값을 바꾸지 않는다. GUI.changed를 되돌려 "수정됨"으로 잘못 표시되지 않게 한다.
            bool changedBeforeFoldout = GUI.changed;
            _sheetPickerOpen = EditorGUILayout.Foldout(_sheetPickerOpen, "시트 고르기 (썸네일)", true);
            GUI.changed = changedBeforeFoldout;

            if (_sheetPickerOpen)
            {
                DrawSheetGrid(item);
            }

            if (!string.IsNullOrEmpty(item.spriteSheetPath))
            {
                EditorGUILayout.LabelField("시트 경로", item.spriteSheetPath, EditorStyles.miniLabel);
            }

            DrawLinkedControllerInfo(item);
        }

        /// <summary>확정본 SpriteSheets 폴더의 시트를 썸네일 그리드로 그립니다 (클릭하면 선택).</summary>
        private void DrawSheetGrid(AssetListItem item)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_sheetPaths.Length == 0)
                {
                    EditorGUILayout.LabelField(
                        $"{AIAssetPipelineFolders.SpriteSheetsDir(_settings)} 폴더에 시트가 없습니다.\n" +
                        "스프라이트 시트 창(Tools/AI Asset Pipeline/Sprite Sheet)에서 시트를 먼저 슬라이스해주세요.",
                        EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                // 상세 패널 폭에 맞춰 한 줄에 넣을 개수를 계산한다 (좌측 목록 폭과 여백 제외).
                float available = position.width - LeftColumnWidth - 60f;
                int perLine = Mathf.Max(1, (int)(available / (SheetThumbnailSize + 14f)));
                for (int i = 0; i < _sheetPaths.Length; i++)
                {
                    if (i % perLine == 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                    }

                    DrawSheetCell(item, _sheetPaths[i]);

                    if (i % perLine == perLine - 1 || i == _sheetPaths.Length - 1)
                    {
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }

        /// <summary>시트 썸네일 1개(그림 + 이름)를 그립니다. 클릭하면 그 시트를 선택합니다.</summary>
        private void DrawSheetCell(AssetListItem item, string sheetPath)
        {
            bool selected = string.Equals(item.spriteSheetPath, sheetPath, StringComparison.OrdinalIgnoreCase);
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(SheetThumbnailSize + 10f)))
            {
                Rect rect = GUILayoutUtility.GetRect(
                    SheetThumbnailSize, SheetThumbnailSize,
                    GUILayout.Width(SheetThumbnailSize), GUILayout.Height(SheetThumbnailSize));

                // 선택 강조 테두리 + 투명 영역이 보이도록 어두운 배경을 깔고 그 위에 시트를 그린다.
                EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f),
                    selected ? new Color(0.35f, 0.6f, 0.95f) : new Color(0f, 0f, 0f, 0.25f));
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f, 1f));

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
                if (texture != null)
                {
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
                }

                // 클릭 자체로 GUI.changed가 서므로 되돌리고, 실제로 값이 바뀔 때만 SelectSheet가 다시 세운다
                // (이미 선택된 시트를 눌러도 "수정됨"이 되지 않게 한다).
                bool changedBeforeClick = GUI.changed;
                bool clicked = GUI.Button(rect, new GUIContent(string.Empty, sheetPath), GUIStyle.none);
                GUI.changed = changedBeforeClick;
                if (clicked)
                {
                    SelectSheet(item, sheetPath);
                }

                EditorGUILayout.LabelField(
                    Path.GetFileNameWithoutExtension(sheetPath),
                    selected ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel,
                    GUILayout.Width(SheetThumbnailSize + 8f));
            }
        }

        /// <summary>시트 선택을 항목에 반영합니다 (빈 경로면 해제). 프레임 이름은 첫 프레임 기본값으로 되돌립니다.</summary>
        private void SelectSheet(AssetListItem item, string sheetPath)
        {
            if (string.Equals(item.spriteSheetPath ?? string.Empty, sheetPath, StringComparison.Ordinal))
            {
                return;
            }

            item.spriteSheetPath = sheetPath;

            // 프레임 이름 지정 UI가 없으므로, 시트를 바꿀 때 옛 이름이 남아 검증 실패가 되지 않게 지운다.
            item.spriteName = string.Empty;

            // 컨트롤러는 시트에서 자동으로 찾으므로, 손으로 넣었던 값도 함께 비워 자동 연결을 따르게 한다.
            item.animatorControllerPath = string.Empty;
            GUI.changed = true;
        }

        /// <summary>적용 시 연결될 AnimatorController를 표시합니다 (시트에서 자동으로 찾습니다).</summary>
        private void DrawLinkedControllerInfo(AssetListItem item)
        {
            if (item.IsSceneItem)
            {
                return; // 씬 항목은 컨트롤러 자동 연결을 지원하지 않는다.
            }

            string controllerPath = AssetApplier.ResolveAnimatorControllerPath(item);
            if (!string.IsNullOrEmpty(controllerPath))
            {
                EditorGUILayout.LabelField("애니메이터", $"{controllerPath} (프리팹 루트에 자동 연결)",
                    EditorStyles.miniLabel);
                return;
            }

            if (!string.IsNullOrEmpty(item.spriteSheetPath))
            {
                EditorGUILayout.LabelField("애니메이터",
                    "이 시트의 컨트롤러가 없어 스프라이트만 적용합니다. " +
                    "(스프라이트 시트 창에서 [클립 + Animator 생성]을 먼저 실행하세요)",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>
        /// 확정본 SpriteSheets 폴더의 시트(png) 목록을 다시 읽습니다.
        /// (창을 열 때·포커스를 받을 때 호출 — 다른 창에서 방금 만든 시트가 바로 목록에 나타나게 합니다)
        /// </summary>
        private void RefreshSpriteAssetPaths()
        {
            _sheetPaths = CollectAssetPaths(AIAssetPipelineFolders.SpriteSheetsDir(_settings), "*.png");
        }

        /// <summary>폴더(하위 폴더 포함)에서 패턴에 맞는 파일을 이름순으로 모읍니다. 폴더가 없으면 빈 배열.</summary>
        private static string[] CollectAssetPaths(string folder, string searchPattern)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return new string[0];
            }

            string[] paths = Directory.GetFiles(folder, searchPattern, SearchOption.AllDirectories);
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = paths[i].Replace('\\', '/');
            }

            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            return paths;
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

            // 대상이 바뀌면 "현재 값"과 준비/적용 카운트도 함께 달라진다 (리페인트가 아니라 여기서 갱신 — C1/C7).
            RefreshCurrentValue(state);
            RecountStates();
        }

        /// <summary>
        /// 항목의 "현재 값"(대상 컴포넌트가 참조 중인 에셋)을 다시 읽어 캐시에 담습니다.
        /// 프리팹 로드·계층 탐색·SerializedObject 생성을 동반하므로 선택 변경·대상 편집·적용 시점에만 호출합니다.
        /// </summary>
        private static void RefreshCurrentValue(ItemState state)
        {
            state.cachedCurrentValue = AssetApplier.GetCurrentValue(state.item);
            state.currentValueCached = true;
        }

        /// <summary>모든 항목의 "현재 값" 캐시를 무효화합니다 (다음 표시 때 필요한 항목만 다시 계산).</summary>
        private void InvalidateCurrentValues()
        {
            foreach (ItemState state in _states)
            {
                state.currentValueCached = false;
            }
        }

        /// <summary>
        /// 적용 준비/적용됨 항목 수를 다시 셉니다 (Task 8 C7).
        /// 상태가 바뀌는 시점에서만 호출하며, 리페인트는 계산 없이 이 값만 읽습니다.
        /// </summary>
        private void RecountStates()
        {
            int ready = 0;
            int applied = 0;
            foreach (ItemState state in _states)
            {
                if (state.applied)
                {
                    applied++;
                }

                if (state.ReadyToApply)
                {
                    ready++;
                }
            }

            _readyCount = ready;
            _appliedCount = applied;
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

                // 개별 적용은 이미 적용된 항목에도 항상 가능하다 (CanApplyAgain).
                // Play Mode·컴파일 중에는 프리팹/씬 저장을 동반하는 적용을 막는다 (R2).
                using (new EditorGUI.DisabledScope(
                           selected == null || !selected.CanApplyAgain || _blockedReason != null))
                {
                    if (GUILayout.Button("선택 적용", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        ApplyStates(new List<ItemState> { selected });
                    }
                }

                // 일괄 적용은 이미 적용된 항목을 제외한다 (불필요한 프리팹 재저장 방지 — R3).
                // 대상 리스트는 실제로 버튼을 눌렀을 때만 만든다 (리페인트마다 LINQ 필터를 돌리지 않는다 — C7).
                using (new EditorGUI.DisabledScope(_readyCount == 0 || _blockedReason != null))
                {
                    if (GUILayout.Button($"일괄 적용 (검증 통과 {_readyCount}개)", GUILayout.Height(PrimaryButtonHeight),
                            GUILayout.Width(220f)))
                    {
                        ApplyStates(_states.Where(s => s.ReadyToApply).ToList());
                    }
                }
            }

            if (_appliedCount > 0)
            {
                EditorGUILayout.LabelField(
                    $"이미 적용됨 {_appliedCount}개 — 일괄 적용에서 제외됩니다. " +
                    "다시 적용하려면 항목을 선택하고 [선택 적용]을 눌러주세요.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>
        /// 대상 항목들을 적용하고 성공/실패 요약을 표시합니다.
        /// 씬 항목은 프리팹 항목과 함께 <see cref="AssetApplier.ApplyBatch"/>로 처리되며,
        /// 같은 씬 항목은 묶어서 씬을 한 번만 열어 적용하고, 같은 프리팹 항목은 묶어서 프리팹을 한 번만 저장합니다.
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

                // 적용으로 대상의 현재 값이 바뀌었다 — 미리보기 캐시를 다음 표시 때 다시 읽게 한다 (C1).
                state.currentValueCached = false;
            }

            // "적용됨" 배지·카운트가 적용 직후 즉시 반영되도록 여기서 다시 센다 (C7).
            RecountStates();

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
            _assetListPaths = AIAssetPipelineFolders.FindDocuments(
                AIAssetPipelineFolders.DocsRoot(_settings), AIAssetPipelineFolders.AssetListFolder, "AssetList_*.json");
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

        /// <summary>
        /// 항목별 확정본 탐색·검증을 다시 수행합니다.
        /// 적용 여부는 <see cref="ApplyHistory"/>(GenerationResults.json의 applications 기록)에서 복원하므로
        /// 창을 닫았다 다시 열어도 "적용됨" 상태가 유지됩니다 (R3).
        /// </summary>
        private void RebuildStates()
        {
            _states.Clear();

            // 목록이 바뀌었으므로 리페인트용 캐시(경로 목록·카운트)도 함께 초기화한다.
            _pathOptionsPrefabPath = null;
            RecountStates();

            if (_document == null)
            {
                return;
            }

            HashSet<string> appliedIds = ApplyHistory.LoadAppliedItemIds(_settings);

            foreach (AssetListItem item in _document.items)
            {
                var state = new ItemState { item = item };
                state.confirmedPath = AssetApplier.FindConfirmedAssetPath(_settings, item);
                if (!string.IsNullOrEmpty(state.confirmedPath))
                {
                    state.reasons = AssetApplier.ValidateItem(item, state.confirmedPath);
                }

                state.applied = !string.IsNullOrEmpty(item.id) && appliedIds.Contains(item.id);
                _states.Add(state);
            }

            RecountStates();
        }
    }
}
