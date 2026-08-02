using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 스프라이트 시트 창입니다. (메뉴: Tools/AI Asset Pipeline/Sprite Sheet)
    /// 레퍼런스 이미지 첨부 전제의 멀티 행(동작별 Row) 통합 시트 프롬프트를 조립해
    /// 미리보기/클립보드 복사/JSON 저장하고, 외부 AI가 생성한 시트 png를
    /// 같은 행 정의로 배경 제거·정규화 임포트하며, 슬라이스된 시트에서 동작별
    /// AnimationClip(+ AnimatorController·프리팹 연결)을 만드는 섹션을 제공합니다.
    /// </summary>
    public class SpriteSheetPromptWizard : EditorWindow
    {
        private static readonly string[] ActionLabels = { "걷기 (walk)", "달리기 (run)", "공격 (attack)", "대기 (idle)", "사망 (death)", "직접 입력..." };
        private static readonly string[] DirectionLabels = { "오른쪽 (RIGHT)", "왼쪽 (LEFT)", "정면 (FRONT)" };
        private static readonly string[] BackgroundLabels = { "흰색 단색 (임포트 시 제거)", "투명" };

        /// <summary>검출 결과 표에서 동작명을 채워 넣는 프리셋 선택 목록입니다. (0번은 선택 안 함)</summary>
        private static readonly string[] ActionPresetOptions =
            { "프리셋...", "walk", "run", "attack", "idle", "death" };

        private const float PrimaryButtonHeight = 30f;

        /// <summary>검출 결과 표의 프레임 썸네일 한 변 크기(px)입니다.</summary>
        private const float ThumbnailSize = 56f;

        /// <summary>행 편집 UI 상태 (프리셋 인덱스 + 직접 입력값 + 프레임 수).</summary>
        [Serializable]
        private class RowEntry
        {
            public int presetIndex;
            public string customAction = string.Empty;
            public int frameCount = 8;
        }

        // 질문 폼 상태
        private bool _useReferenceImage = true;
        private string _characterDescription = string.Empty;
        private string _gameGenre = string.Empty;
        private string _artStyle = string.Empty;
        private string _extraNotes = string.Empty;
        private List<RowEntry> _rows;
        private int _cellSize = 256;
        private int _directionIndex; // DirectionLabels 인덱스 (0=RIGHT, 1=LEFT, 2=FRONT) — SpriteSheetFacing과 순서 일치
        private int _backgroundIndex; // 0=흰색

        // AI CLI 연동 상태
        private List<AiCliTool> _aiTools;
        private string[] _aiOptions = new string[0];
        private int _selectedAiIndex;
        private int _aiTimeoutSeconds = 300;
        private bool _aiRunning;
        private CancellationTokenSource _aiCancelSource;
        private string _aiStatusMessage = string.Empty;

        // 결과 상태
        private string _prompt = string.Empty;
        private string _savedPromptPath = string.Empty;
        private Vector2 _promptScroll;
        private Vector2 _scroll;

        // 임포트 섹션 상태
        private string _importImagePath = string.Empty;
        private string _importResultMessage = string.Empty;

        /// <summary>임포트 전용 행 목록입니다. 1번 프롬프트 섹션의 행 목록과 분리되어 있습니다.</summary>
        private List<RowEntry> _importRows;

        /// <summary>[배경 제거 + 격자 검출] 결과입니다. [슬라이스 적용] 전까지 프로젝트에 아무것도 기록하지 않습니다.</summary>
        private SpriteSheetDetection _detection;

        /// <summary>검출 결과 표의 프레임 썸네일입니다. (행→셀 순서로 평탄화, 창이 닫히거나 재검출 시 해제)</summary>
        private readonly List<Texture2D> _thumbnails = new List<Texture2D>();

        private string _detectMessage = string.Empty;

        // 애니메이션 섹션 상태
        /// <summary>클립 커브가 가리킬 대상 컴포넌트 선택 라벨입니다.</summary>
        private static readonly string[] ClipComponentLabels = { "SpriteRenderer (2D)", "Image (uGUI)" };

        /// <summary>클립을 만들 슬라이스 완료 시트입니다. (슬라이스 적용 직후 자동으로 채워집니다)</summary>
        private Texture2D _clipSheet;

        /// <summary>스캔한 동작(=클립) 목록입니다. null이면 아직 스캔하지 않은 상태입니다.</summary>
        private SpriteSheetClipPlan _clipPlan;

        private string _clipScanMessage = string.Empty;
        private string _clipResultMessage = string.Empty;
        private int _clipFrameRate = 12;
        private int _clipComponentIndex;
        private GameObject _clipTargetPrefab;

        /// <summary>대상 프리팹의 계층 경로 목록입니다 (0번은 루트 = 빈 경로).</summary>
        private string[] _clipObjectLabels = { "(프리팹 루트)" };

        private int _clipObjectIndex;

        /// <summary>
        /// 버튼 클릭으로 예약된 임포트 섹션 동작입니다. 행·표가 늘고 줄어 IMGUI 레이아웃이 어긋나지 않도록
        /// 다음 Layout 이벤트에서 실행합니다.
        /// </summary>
        private Action _pendingImportAction;

        /// <summary>스프라이트 시트 창(프롬프트 생성 + 시트 임포트)을 엽니다.</summary>
        [MenuItem("Tools/AI Asset Pipeline/Sprite Sheet", false, 50)]
        public static void Open()
        {
            var window = GetWindow<SpriteSheetPromptWizard>();
            window.titleContent = new GUIContent("스프라이트 시트");
            window.minSize = new Vector2(500f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            if (_rows == null || _rows.Count == 0)
            {
                _rows = new List<RowEntry>
                {
                    new RowEntry { presetIndex = 0, frameCount = 8 },  // walk
                    new RowEntry { presetIndex = 1, frameCount = 8 },  // run
                    new RowEntry { presetIndex = 2, frameCount = 8 },  // attack
                    new RowEntry { presetIndex = 4, frameCount = 10 }  // death
                };
            }

            if (_importRows == null || _importRows.Count == 0)
            {
                // 임포트 행 목록은 1번 섹션과 독립이다. 실제로 임포트할 시트에 맞춰 사용자가 지정한다.
                _importRows = new List<RowEntry> { new RowEntry { presetIndex = 0, frameCount = 8 } };
            }

            _clipFrameRate = Mathf.Max(1, AIAssetPipelineSettings.GetOrCreate().spriteAnimationFrameRate);
            RefreshAiToolList(false);
        }

        private void OnDisable()
        {
            // 창이 닫히면 진행 중인 AI 실행을 취소한다.
            if (_aiCancelSource != null)
            {
                _aiCancelSource.Cancel();
            }

            ClearDetection();
        }

        private void OnGUI()
        {
            // 예약된 임포트 동작은 레이아웃이 확정되기 전(Layout 이벤트)에 처리한다.
            if (_pendingImportAction != null && Event.current.type == EventType.Layout)
            {
                Action action = _pendingImportAction;
                _pendingImportAction = null;
                action();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawPromptForm();
            EditorGUILayout.Space(6f);
            DrawPromptResult();
            EditorGUILayout.Space(10f);
            DrawImportSection();
            EditorGUILayout.Space(10f);
            DrawAnimationSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPromptForm()
        {
            EditorGUILayout.LabelField("1. 프롬프트 설정", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _useReferenceImage = EditorGUILayout.Toggle("레퍼런스 이미지 사용", _useReferenceImage);
                EditorGUILayout.LabelField(_useReferenceImage
                    ? "캐릭터 특징 서술 (선택 — 보존할 핵심 디자인 특징)"
                    : "캐릭터 설명 (필수 — 레퍼런스 미사용 시)");
                _characterDescription = EditorGUILayout.TextArea(_characterDescription, GUILayout.MinHeight(40f));

                if (_useReferenceImage)
                {
                    EditorGUILayout.HelpBox(
                        "프롬프트와 함께 캐릭터 레퍼런스 이미지를 외부 AI에 첨부해야 합니다.", MessageType.None);
                }

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("게임 컨텍스트", EditorStyles.boldLabel);
                _gameGenre = EditorGUILayout.TextField(
                    new GUIContent("게임 장르", "예: 사이드스크롤 액션, 플랫포머 (영어 권장)"), _gameGenre);
                _artStyle = EditorGUILayout.TextField(
                    new GUIContent("아트 스타일/분위기", "예: SD/치비, 다크 판타지 (영어 권장)"), _artStyle);
                EditorGUILayout.LabelField("추가 참고 사항 (선택)");
                _extraNotes = EditorGUILayout.TextArea(_extraNotes, GUILayout.MinHeight(30f));

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("동작 행 목록 (시트의 위 행부터 순서대로)", EditorStyles.boldLabel);
                DrawRowList(_rows, true, false);

                EditorGUILayout.Space(4f);
                _cellSize = Mathf.Max(32, EditorGUILayout.IntField("셀 크기 (px)", _cellSize));
                _directionIndex = EditorGUILayout.Popup(
                    new GUIContent("바라보는 방향",
                        "오른쪽/왼쪽은 측면(side view) 시트로, 정면은 카메라를 바라보는 front view 시트로 프롬프트가 조립됩니다."),
                    _directionIndex, DirectionLabels);
                _backgroundIndex = EditorGUILayout.Popup("배경", _backgroundIndex, BackgroundLabels);

                EditorGUILayout.Space(6f);
                DrawAiSection();
            }
        }

        /// <summary>AI CLI 선택 + AI 프롬프트 생성 / 템플릿 생성 버튼 영역.</summary>
        private void DrawAiSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _selectedAiIndex = EditorGUILayout.Popup("AI CLI", _selectedAiIndex, _aiOptions);
                if (GUILayout.Button("새로고침", GUILayout.Width(70f)))
                {
                    RefreshAiToolList(true);
                }
            }

            _aiTimeoutSeconds = Mathf.Max(30, EditorGUILayout.IntField("AI 타임아웃 (초)", _aiTimeoutSeconds));

            if (_aiTools == null || _aiTools.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "설치된 AI CLI(claude/codex/gemini 등)가 감지되지 않았습니다. " +
                    "[AI로 프롬프트 생성]을 누르면 템플릿 방식으로 생성합니다.", MessageType.None);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_aiRunning)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        GUILayout.Button("AI 실행 중...", GUILayout.Height(PrimaryButtonHeight));
                    }

                    if (GUILayout.Button("취소", GUILayout.Height(PrimaryButtonHeight), GUILayout.Width(80f)))
                    {
                        _aiCancelSource?.Cancel();
                    }
                }
                else
                {
                    if (GUILayout.Button("AI로 프롬프트 생성", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        RunAiGenerate();
                    }

                    if (GUILayout.Button("프롬프트 생성 (템플릿)", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        GeneratePrompt();
                    }
                }
            }

            if (!string.IsNullOrEmpty(_aiStatusMessage))
            {
                EditorGUILayout.HelpBox(_aiStatusMessage, MessageType.None);
            }
        }

        private void RefreshAiToolList(bool forceRefresh)
        {
            _aiTools = AiCliRunner.GetInstalledTools(forceRefresh);
            var options = new List<string>();
            foreach (AiCliTool tool in _aiTools)
            {
                options.Add(tool.displayName);
            }

            if (options.Count == 0)
            {
                options.Add("(감지된 AI CLI 없음)");
            }

            _aiOptions = options.ToArray();
            _selectedAiIndex = Mathf.Clamp(_selectedAiIndex, 0, _aiOptions.Length - 1);
            Repaint();
        }

        /// <summary>행 편집 목록을 그립니다. (프롬프트용/임포트용 공용)</summary>
        /// <param name="rows">편집할 행 목록.</param>
        /// <param name="showFrameCount">프레임 수 입력란 표시 여부 (임포트 행은 격자 검출이 프레임 수를 정하므로 false).</param>
        /// <param name="showReorder">▲▼ 순서 변경 버튼 표시 여부.</param>
        private void DrawRowList(List<RowEntry> rows, bool showFrameCount, bool showReorder)
        {
            int removeIndex = -1;
            int moveIndex = -1;
            int moveOffset = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                RowEntry row = rows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"행 {i + 1}", GUILayout.Width(36f));
                    row.presetIndex = EditorGUILayout.Popup(row.presetIndex, ActionLabels);
                    if (row.presetIndex == ActionLabels.Length - 1)
                    {
                        row.customAction = EditorGUILayout.TextField(row.customAction, GUILayout.MinWidth(80f));
                    }

                    if (showFrameCount)
                    {
                        EditorGUILayout.LabelField("프레임", GUILayout.Width(44f));
                        row.frameCount = Mathf.Clamp(EditorGUILayout.IntField(row.frameCount, GUILayout.Width(40f)), 1, SpriteSheetPromptBuilder.MaxFrameCount);
                    }

                    if (showReorder)
                    {
                        using (new EditorGUI.DisabledScope(i == 0))
                        {
                            if (GUILayout.Button("▲", GUILayout.Width(24f)))
                            {
                                moveIndex = i;
                                moveOffset = -1;
                            }
                        }

                        using (new EditorGUI.DisabledScope(i == rows.Count - 1))
                        {
                            if (GUILayout.Button("▼", GUILayout.Width(24f)))
                            {
                                moveIndex = i;
                                moveOffset = 1;
                            }
                        }
                    }

                    using (new EditorGUI.DisabledScope(rows.Count <= 1))
                    {
                        if (GUILayout.Button("-", GUILayout.Width(24f)))
                        {
                            removeIndex = i;
                        }
                    }
                }
            }

            if (removeIndex >= 0)
            {
                rows.RemoveAt(removeIndex);
            }
            else if (moveIndex >= 0)
            {
                RowEntry moved = rows[moveIndex];
                rows.RemoveAt(moveIndex);
                rows.Insert(Mathf.Clamp(moveIndex + moveOffset, 0, rows.Count), moved);
            }

            if (GUILayout.Button("+ 행 추가"))
            {
                rows.Add(new RowEntry { presetIndex = 0, frameCount = 8 });
            }
        }

        /// <summary>행 편집 항목의 동작명을 구합니다. (직접 입력 선택 시 입력값, 비어 있으면 빈 문자열)</summary>
        private static string ResolveAction(RowEntry row)
        {
            string action = row.presetIndex == ActionLabels.Length - 1
                ? row.customAction
                : SpriteSheetPromptBuilder.ActionPresets[row.presetIndex];
            return string.IsNullOrWhiteSpace(action) ? string.Empty : action.Trim();
        }

        /// <summary>1번 프롬프트 섹션의 행 목록을 빌더용 행 정의로 변환합니다.</summary>
        private List<SpriteSheetRowDef> BuildRowDefs()
        {
            var defs = new List<SpriteSheetRowDef>();
            for (int i = 0; i < _rows.Count; i++)
            {
                RowEntry row = _rows[i];
                string action = ResolveAction(row);
                if (string.IsNullOrEmpty(action))
                {
                    throw new InvalidOperationException($"행 {i + 1}의 동작명을 입력해주세요 (직접 입력 선택 시 필수).");
                }

                defs.Add(new SpriteSheetRowDef(action, row.frameCount));
            }

            return defs;
        }

        /// <summary>방향 드롭다운 선택값을 <see cref="SpriteSheetFacing"/>으로 바꿉니다. (인덱스 순서가 enum과 일치)</summary>
        private SpriteSheetFacing CurrentFacing()
        {
            return (SpriteSheetFacing)Mathf.Clamp(_directionIndex, 0, DirectionLabels.Length - 1);
        }

        /// <summary>템플릿 방식으로 프롬프트를 생성·저장합니다. (빠른 생성 경로 및 AI 실패 시 폴백)</summary>
        /// <param name="fallbackReason">AI 폴백으로 호출된 경우 그 사유 (안내 문구에 포함). null이면 일반 템플릿 생성.</param>
        private void GeneratePrompt(string fallbackReason = null)
        {
            try
            {
                List<SpriteSheetRowDef> rows = BuildRowDefs();
                SpriteSheetFacing facing = CurrentFacing();
                bool whiteBackground = _backgroundIndex == 0;

                _prompt = SpriteSheetPromptBuilder.BuildPrompt(
                    _useReferenceImage, _characterDescription, rows, _cellSize, facing, whiteBackground,
                    _gameGenre, _artStyle, _extraNotes);
                _savedPromptPath = SpriteSheetPromptBuilder.SavePromptJson(
                    _useReferenceImage, _characterDescription, rows, _cellSize, facing, whiteBackground, _prompt,
                    _gameGenre, _artStyle, _extraNotes, "template");

                _aiStatusMessage = fallbackReason == null
                    ? "템플릿 방식으로 프롬프트를 생성했습니다."
                    : $"{fallbackReason} 템플릿 방식으로 대신 생성했습니다.";
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 생성 실패", e.Message, "확인");
            }
        }

        /// <summary>
        /// 선택한 AI CLI로 메타 프롬프트를 실행해 게임 컨텍스트가 반영된 프롬프트를 생성합니다.
        /// CLI 미설치/실행 실패/빈 응답 시 템플릿 방식으로 폴백합니다. (async — 에디터 블로킹 없음)
        /// </summary>
        private async void RunAiGenerate()
        {
            if (_aiRunning)
            {
                return;
            }

            if (_aiTools == null || _aiTools.Count == 0)
            {
                GeneratePrompt("설치된 AI CLI가 감지되지 않았습니다.");
                return;
            }

            string metaPrompt;
            List<SpriteSheetRowDef> rows;
            SpriteSheetFacing facing = CurrentFacing();
            bool whiteBackground = _backgroundIndex == 0;
            try
            {
                rows = BuildRowDefs();
                metaPrompt = SpriteSheetPromptBuilder.BuildMetaPrompt(
                    _useReferenceImage, _characterDescription, rows, _cellSize, facing, whiteBackground,
                    _gameGenre, _artStyle, _extraNotes);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 생성 실패", e.Message, "확인");
                return;
            }

            AiCliTool tool = _aiTools[Mathf.Clamp(_selectedAiIndex, 0, _aiTools.Count - 1)];
            _aiRunning = true;
            _aiStatusMessage = $"AI 실행 중: {tool.displayName} (완료까지 수십 초~수 분 걸릴 수 있습니다)";
            _aiCancelSource = new CancellationTokenSource();
            AiCliResult result;
            try
            {
                result = await AiCliRunner.RunAsync(
                    tool.command, metaPrompt, _aiTimeoutSeconds, _aiCancelSource.Token);
            }
            finally
            {
                _aiRunning = false;
                _aiCancelSource.Dispose();
                _aiCancelSource = null;
            }

            if (this == null)
            {
                return; // 실행 중 창이 닫힌 경우
            }

            Repaint();

            if (result.canceled)
            {
                _aiStatusMessage = "AI 실행을 취소했습니다.";
                return;
            }

            string cleaned = SpriteSheetPromptBuilder.CleanAiOutput(result.stdout);
            if (!result.success || string.IsNullOrEmpty(cleaned))
            {
                string reason = result.timedOut
                    ? "AI 실행이 타임아웃되었습니다."
                    : !result.success
                        ? $"AI 실행이 실패했습니다 ({result.errorMessage})."
                        : "AI 응답이 비어 있습니다.";
                GeneratePrompt(reason);
                return;
            }

            try
            {
                _prompt = cleaned;
                _savedPromptPath = SpriteSheetPromptBuilder.SavePromptJson(
                    _useReferenceImage, _characterDescription, rows, _cellSize, facing, whiteBackground, _prompt,
                    _gameGenre, _artStyle, _extraNotes, $"ai-cli:{tool.command}");
                _aiStatusMessage = $"AI({tool.displayName})로 프롬프트를 생성했습니다.";
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("프롬프트 저장 실패", e.Message, "확인");
            }
        }

        private void DrawPromptResult()
        {
            EditorGUILayout.LabelField("2. 프롬프트 미리보기", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _promptScroll = EditorGUILayout.BeginScrollView(_promptScroll, GUILayout.MinHeight(90f), GUILayout.MaxHeight(180f));
                _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_prompt)))
                {
                    if (GUILayout.Button("클립보드 복사", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        EditorGUIUtility.systemCopyBuffer = _prompt;
                        ShowNotification(new GUIContent("프롬프트를 클립보드에 복사했습니다."));
                    }
                }

                if (!string.IsNullOrEmpty(_savedPromptPath))
                {
                    EditorGUILayout.HelpBox($"저장됨: {_savedPromptPath}", MessageType.Info);
                }

                EditorGUILayout.HelpBox(
                    "복사한 프롬프트를 레퍼런스 이미지와 함께 외부 AI(ChatGPT/Codex 이미지 생성 등)에 붙여넣어 " +
                    "멀티 행 시트 이미지를 생성한 뒤, 아래 임포트 섹션에서 png를 가져오세요.", MessageType.None);
            }
        }

        private void DrawImportSection()
        {
            EditorGUILayout.LabelField("3. 시트 임포트 (외부 AI 결과물)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _importImagePath = EditorGUILayout.TextField("시트 png 경로", _importImagePath);
                    if (GUILayout.Button("찾기...", GUILayout.Width(64f)))
                    {
                        string picked = EditorUtility.OpenFilePanel("스프라이트 시트 이미지 선택", string.Empty, "png");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            _importImagePath = picked;
                        }
                    }
                }

                EditorGUILayout.HelpBox(
                    "배경 설정은 위 1번 섹션 값을 사용하고, 동작 행 목록은 아래 임포트 전용 목록을 사용합니다.\n" +
                    "배경이 흰색이면 외곽 flood-fill로 배경·격자선만 투명화한 뒤, " +
                    "시트에 그려진 격자 간격대로 셀을 나누고 셀 내 위치를 보존해 임포트합니다.", MessageType.None);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("임포트 행 목록 (시트의 위 행부터 순서대로)", EditorStyles.boldLabel);
                DrawRowList(_importRows, false, true);
                if (GUILayout.Button("1번 행 목록 가져오기"))
                {
                    _pendingImportAction = CopyRowsFromPromptSection;
                }

                EditorGUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_importImagePath)))
                {
                    if (GUILayout.Button("배경 제거 + 격자 검출", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        _pendingImportAction = RunDetect;
                    }
                }

                if (!string.IsNullOrEmpty(_detectMessage))
                {
                    EditorGUILayout.HelpBox(_detectMessage, MessageType.None);
                }

                if (_detection != null)
                {
                    DrawDetectionTable();

                    string problem = SpriteSheetImporter.ValidateForApply(_detection);
                    if (problem != null)
                    {
                        EditorGUILayout.HelpBox(problem, MessageType.Warning);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(problem != null))
                        {
                            if (GUILayout.Button("슬라이스 적용", GUILayout.Height(PrimaryButtonHeight)))
                            {
                                _pendingImportAction = RunApplySlices;
                            }
                        }

                        if (GUILayout.Button("검출 결과 지우기", GUILayout.Height(PrimaryButtonHeight), GUILayout.Width(120f)))
                        {
                            _pendingImportAction = ClearDetectionAndMessage;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_importResultMessage))
                {
                    EditorGUILayout.HelpBox(_importResultMessage, MessageType.Info);
                }
            }
        }

        /// <summary>
        /// 4번 애니메이션 섹션: 슬라이스된 시트의 동작별 AnimationClip 생성(+ AnimatorController·프리팹 연결) UI입니다.
        /// </summary>
        private void DrawAnimationSection()
        {
            EditorGUILayout.LabelField("4. 애니메이션 (클립 / Animator)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (Texture2D)EditorGUILayout.ObjectField(
                        "슬라이스된 시트", _clipSheet, typeof(Texture2D), false);
                    if (picked != _clipSheet)
                    {
                        _clipSheet = picked;
                        _pendingImportAction = ScanClipActions;
                    }

                    using (new EditorGUI.DisabledScope(_clipSheet == null))
                    {
                        if (GUILayout.Button("동작 스캔", GUILayout.Width(80f)))
                        {
                            _pendingImportAction = ScanClipActions;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_clipScanMessage))
                {
                    EditorGUILayout.HelpBox(_clipScanMessage, MessageType.None);
                }

                if (_clipPlan == null)
                {
                    EditorGUILayout.HelpBox(
                        "슬라이스가 끝난 시트를 지정하면 스프라이트 이름({동작}_{번호})을 읽어 동작 목록을 만듭니다. " +
                        "위 [슬라이스 적용]을 실행하면 그 시트가 자동으로 지정됩니다.", MessageType.None);
                    return;
                }

                DrawClipActionTable();

                EditorGUILayout.Space(4f);
                _clipFrameRate = Mathf.Clamp(EditorGUILayout.IntField("프레임 레이트 (fps)", _clipFrameRate), 1, 120);
                _clipComponentIndex = EditorGUILayout.Popup("대상 컴포넌트", _clipComponentIndex, ClipComponentLabels);

                var prefab = (GameObject)EditorGUILayout.ObjectField(
                    "대상 프리팹 (선택)", _clipTargetPrefab, typeof(GameObject), false);
                if (prefab != _clipTargetPrefab)
                {
                    _clipTargetPrefab = prefab;
                    _pendingImportAction = RefreshClipObjectList;
                }

                using (new EditorGUI.DisabledScope(_clipTargetPrefab == null))
                {
                    _clipObjectIndex = EditorGUILayout.Popup(
                        "대상 오브젝트", Mathf.Clamp(_clipObjectIndex, 0, _clipObjectLabels.Length - 1), _clipObjectLabels);
                }

                EditorGUILayout.HelpBox(
                    "클립은 Assets/Generated/3_Confirmed/Animations/{시트이름}/{동작}.anim에 만들어집니다. " +
                    "같은 경로에 클립이 있으면 새로 만들지 않고 커브·프레임 레이트·루프 설정만 덮어씁니다.\n" +
                    "Animator는 프리팹 루트에 붙고, 스프라이트 커브 경로는 위에서 고른 대상 오브젝트(루트 기준 경로)가 됩니다.",
                    MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("클립 생성", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        _pendingImportAction = () => RunBuildClips(false);
                    }

                    if (GUILayout.Button("클립 + Animator 생성", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        _pendingImportAction = () => RunBuildClips(true);
                    }
                }

                if (!string.IsNullOrEmpty(_clipResultMessage))
                {
                    EditorGUILayout.HelpBox(_clipResultMessage, MessageType.Info);
                }
            }
        }

        /// <summary>동작 표: 동작명 / 프레임 수 / 루프 토글 / 클립 생성 체크박스를 그립니다.</summary>
        private void DrawClipActionTable()
        {
            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("동작", EditorStyles.miniBoldLabel, GUILayout.MinWidth(80f));
                EditorGUILayout.LabelField("프레임", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
                EditorGUILayout.LabelField("루프", EditorStyles.miniBoldLabel, GUILayout.Width(46f));
                EditorGUILayout.LabelField("생성", EditorStyles.miniBoldLabel, GUILayout.Width(46f));
                EditorGUILayout.LabelField("기존 클립", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
            }

            foreach (SpriteSheetActionGroup group in _clipPlan.groups)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(group.action, GUILayout.MinWidth(80f));
                    EditorGUILayout.LabelField($"{group.frameCount}개", GUILayout.Width(50f));
                    group.loop = EditorGUILayout.Toggle(group.loop, GUILayout.Width(46f));
                    group.include = EditorGUILayout.Toggle(group.include, GUILayout.Width(46f));
                    EditorGUILayout.LabelField(
                        group.clipExists ? "덮어씀" : "새로 만듦", EditorStyles.miniLabel, GUILayout.Width(70f));
                }
            }

            if (_clipPlan.skipped.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "건너뛴 항목:\n- " + string.Join("\n- ", _clipPlan.skipped), MessageType.Warning);
            }
        }

        /// <summary>지정한 시트의 서브 스프라이트를 훑어 동작 목록을 만듭니다. (프로젝트에는 기록하지 않습니다)</summary>
        private void ScanClipActions()
        {
            _clipPlan = null;
            _clipResultMessage = string.Empty;
            if (_clipSheet == null)
            {
                _clipScanMessage = string.Empty;
                return;
            }

            try
            {
                _clipPlan = SpriteSheetClipBuilder.Scan(AssetDatabase.GetAssetPath(_clipSheet));
                var summaries = new List<string>();
                foreach (SpriteSheetActionGroup group in _clipPlan.groups)
                {
                    summaries.Add($"{group.action} {group.frameCount}");
                }

                _clipScanMessage =
                    $"동작 {_clipPlan.groups.Count}개 검출: {string.Join(", ", summaries)}\n" +
                    $"출력 폴더: {_clipPlan.outputFolder}";
            }
            catch (Exception e)
            {
                _clipScanMessage = string.Empty;
                EditorUtility.DisplayDialog("동작 스캔 실패", e.Message, "확인");
            }
        }

        /// <summary>대상 프리팹의 계층 경로 목록을 다시 만듭니다. (0번은 루트 = 빈 경로)</summary>
        private void RefreshClipObjectList()
        {
            _clipObjectIndex = 0;
            if (_clipTargetPrefab == null)
            {
                _clipObjectLabels = new[] { "(프리팹 루트)" };
                return;
            }

            var labels = new List<string> { "(프리팹 루트)" };
            foreach (Transform child in _clipTargetPrefab.GetComponentsInChildren<Transform>(true))
            {
                if (child == _clipTargetPrefab.transform)
                {
                    continue;
                }

                var path = new List<string>();
                for (Transform current = child; current != null && current != _clipTargetPrefab.transform;
                     current = current.parent)
                {
                    path.Insert(0, current.name);
                }

                labels.Add(string.Join("/", path));
            }

            _clipObjectLabels = labels.ToArray();
        }

        /// <summary>동작 표대로 클립을 만듭니다. 기존 클립이 있으면 덮어쓰기 확인을 먼저 받습니다.</summary>
        /// <param name="createController">true면 AnimatorController 생성 + (지정 시) 프리팹 연결까지 수행합니다.</param>
        private void RunBuildClips(bool createController)
        {
            if (_clipPlan == null)
            {
                return;
            }

            try
            {
                List<string> existing = _clipPlan.ExistingClipPaths();
                if (existing.Count > 0 && !EditorUtility.DisplayDialog(
                        "기존 클립 덮어쓰기",
                        $"이미 있는 클립 {existing.Count}개의 커브·프레임 레이트·루프 설정을 덮어씁니다.\n" +
                        "(클립 에셋 자체와 붙여 둔 애니메이션 이벤트는 유지됩니다)\n\n" +
                        string.Join("\n", existing),
                        "덮어쓰기", "취소"))
                {
                    return;
                }

                string prefabPath = _clipTargetPrefab != null
                    ? AssetDatabase.GetAssetPath(_clipTargetPrefab)
                    : string.Empty;
                string objectPath = _clipTargetPrefab != null && _clipObjectIndex > 0 &&
                                    _clipObjectIndex < _clipObjectLabels.Length
                    ? _clipObjectLabels[_clipObjectIndex]
                    : string.Empty;
                string component = _clipComponentIndex == 1
                    ? SpriteSheetClipBuilder.ImageComponent
                    : SpriteSheetClipBuilder.SpriteRendererComponent;

                SpriteSheetClipBuildResult result = SpriteSheetClipBuilder.Build(
                    _clipPlan, _clipFrameRate, component, createController, prefabPath, objectPath);

                var lines = new List<string>();
                foreach (SpriteSheetActionGroup group in result.groups)
                {
                    lines.Add($"{(group.created ? "생성" : "갱신")}: {group.clipPath} " +
                              $"({group.frameCount}프레임, 루프 {(group.loop ? "ON" : "OFF")})");
                }

                if (!string.IsNullOrEmpty(result.controllerPath))
                {
                    lines.Add($"컨트롤러: {result.controllerPath}" +
                              (result.addedStates.Count > 0
                                  ? $" (State 추가: {string.Join(", ", result.addedStates)})"
                                  : " (추가된 State 없음)"));
                }

                if (result.prefabLinked)
                {
                    lines.Add($"프리팹 연결: {result.prefabPath} (Animator + 컨트롤러)");
                }

                if (result.skipped.Count > 0)
                {
                    lines.Add("건너뜀:\n- " + string.Join("\n- ", result.skipped));
                }

                _clipResultMessage = string.Join("\n", lines);
                EditorGUIUtility.PingObject(
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(result.groups[0].clipPath));
            }
            catch (Exception e)
            {
                _clipResultMessage = string.Empty;
                EditorUtility.DisplayDialog("클립 생성 실패", e.Message, "확인");
            }
        }

        /// <summary>1번 프롬프트 섹션의 행 목록을 임포트 행 목록으로 복사합니다. (동작명·프레임 수 그대로)</summary>
        private void CopyRowsFromPromptSection()
        {
            _importRows.Clear();
            foreach (RowEntry row in _rows)
            {
                _importRows.Add(new RowEntry
                {
                    presetIndex = row.presetIndex,
                    customAction = row.customAction,
                    frameCount = row.frameCount
                });
            }

            if (_importRows.Count == 0)
            {
                _importRows.Add(new RowEntry { presetIndex = 0, frameCount = 8 });
            }

            GUI.FocusControl(null);
        }

        /// <summary>검출 결과 표: 행별 프레임 수·동작명 입력·프레임 썸네일과 포함 체크박스를 그립니다.</summary>
        private void DrawDetectionTable()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("검출 결과 (확인 후 [슬라이스 적용])", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "\"비어 보임\" 프레임은 자동으로 제외됩니다. 실제 콘텐츠면 체크해 되살리세요.",
                EditorStyles.miniLabel);

            int thumbIndex = 0;
            for (int r = 0; r < _detection.rows.Count; r++)
            {
                SpriteSheetDetectedRow row = _detection.rows[r];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            $"행 {r + 1}  프레임 {row.IncludedFrameCount}/{row.cells.Count}", GUILayout.Width(120f));
                        EditorGUILayout.LabelField("동작명", GUILayout.Width(44f));
                        row.action = EditorGUILayout.TextField(row.action ?? string.Empty);
                        int preset = EditorGUILayout.Popup(0, ActionPresetOptions, GUILayout.Width(80f));
                        if (preset > 0)
                        {
                            row.action = ActionPresetOptions[preset];
                            GUI.FocusControl(null);
                        }
                    }

                    int perLine = Mathf.Max(1, (int)((position.width - 60f) / (ThumbnailSize + 14f)));
                    int frameNo = 0;
                    for (int i = 0; i < row.cells.Count; i++)
                    {
                        if (i % perLine == 0)
                        {
                            EditorGUILayout.BeginHorizontal();
                        }

                        SpriteSheetDetectedCell cell = row.cells[i];
                        if (cell.include)
                        {
                            frameNo++;
                        }

                        DrawDetectedCell(cell, thumbIndex, frameNo);
                        thumbIndex++;

                        if (i % perLine == perLine - 1 || i == row.cells.Count - 1)
                        {
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 프레임 셀 1개(썸네일 + 포함 체크박스 + 콘텐츠 비율/"비어 보임" 표시)를 그립니다.
        /// "비어 보임" 셀은 검출 단계에서 이미 자동 제외된 상태이며, 체크하면 다시 포함됩니다.
        /// </summary>
        private void DrawDetectedCell(SpriteSheetDetectedCell cell, int thumbIndex, int frameNo)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ThumbnailSize + 10f)))
            {
                Rect rect = GUILayoutUtility.GetRect(
                    ThumbnailSize, ThumbnailSize, GUILayout.Width(ThumbnailSize), GUILayout.Height(ThumbnailSize));
                EditorGUI.DrawRect(rect, new Color(0.22f, 0.22f, 0.22f, 1f)); // 투명 영역 확인용 배경
                Texture2D thumb = thumbIndex < _thumbnails.Count ? _thumbnails[thumbIndex] : null;
                if (thumb != null)
                {
                    GUI.DrawTexture(rect, thumb, ScaleMode.ScaleToFit, true);
                }

                cell.include = EditorGUILayout.ToggleLeft(
                    cell.include ? $"{frameNo:00}" : "제외", cell.include, GUILayout.Width(ThumbnailSize + 8f));
                EditorGUILayout.LabelField(
                    // 비어 보이는 셀은 검출 시 자동 제외되므로 토글이 "제외"로 함께 표시된다.
                    cell.looksEmpty ? "비어 보임" : $"{cell.contentRatio * 100f:0.#}%",
                    EditorStyles.miniLabel, GUILayout.Width(ThumbnailSize + 8f));
            }
        }

        /// <summary>배경 제거 + 격자 검출만 수행합니다. (프로젝트에는 아직 아무것도 기록하지 않습니다)</summary>
        private void RunDetect()
        {
            try
            {
                ClearDetection();
                bool whiteBackground = _backgroundIndex == 0;
                _detection = SpriteSheetImporter.Detect(_importImagePath, whiteBackground, true);

                // 임포트 행 목록의 동작명을 위에서부터 순서대로 채운다. 남는 행은 비워 두고 사용자가 입력한다.
                // "비어 보임" 자동 제외로 프레임이 하나도 남지 않은 행(여백 밴드 등)은 슬라이스되지 않으므로 건너뛴다.
                int nameIndex = 0;
                foreach (SpriteSheetDetectedRow row in _detection.rows)
                {
                    if (row.IncludedFrameCount == 0)
                    {
                        row.action = string.Empty;
                        continue;
                    }

                    row.action = nameIndex < _importRows.Count
                        ? ResolveAction(_importRows[nameIndex])
                        : string.Empty;
                    nameIndex++;
                }

                foreach (SpriteSheetDetectedRow row in _detection.rows)
                {
                    foreach (SpriteSheetDetectedCell cell in row.cells)
                    {
                        _thumbnails.Add(SpriteSheetImporter.CreateCellThumbnail(_detection, cell, (int)ThumbnailSize));
                    }
                }

                var counts = new List<string>();
                foreach (SpriteSheetDetectedRow row in _detection.rows)
                {
                    counts.Add(row.IncludedFrameCount.ToString());
                }

                int emptyCount = _detection.LooksEmptyFrameCount;
                int includedRows = _detection.IncludedRowCount;

                _importResultMessage = string.Empty;
                _detectMessage =
                    $"검출 완료: 행 {includedRows}개 / 프레임 총 {_detection.TotalFrameCount - emptyCount}개 " +
                    $"(행별: {string.Join(", ", counts)}), 셀 {_detection.cellWidth}x{_detection.cellHeight}px\n" +
                    (emptyCount > 0
                        ? $"※ 비어 보이는 프레임 {emptyCount}개를 자동 제외했습니다. " +
                          "실제 콘텐츠였다면 해당 셀을 다시 체크해주세요.\n"
                        : string.Empty) +
                    "행별 동작명을 확인한 뒤 [슬라이스 적용]을 눌러주세요. " +
                    (includedRows > _importRows.Count
                        ? "※ 검출된 행이 임포트 행 목록보다 많아 이름이 빈 행이 있습니다."
                        : string.Empty);
            }
            catch (Exception e)
            {
                ClearDetection();
                _detectMessage = string.Empty;
                EditorUtility.DisplayDialog("격자 검출 실패", e.Message, "확인");
            }
        }

        /// <summary>확정된 행 이름·포함 프레임으로 시트를 저장하고 슬라이스를 적용합니다.</summary>
        private void RunApplySlices()
        {
            try
            {
                // 창 경로는 대화형 — 기존 시트를 덮어쓰기 전에 확인을 받는다 (Task 10 R5).
                SpriteSheetImportResult result =
                    SpriteSheetImporter.ApplySlices(_detection, false, true, true);
                if (result.canceled)
                {
                    _importResultMessage =
                        $"덮어쓰기를 취소했습니다: {result.assetPath}\n" +
                        "다른 이름으로 저장하려면 원본 이미지 파일명을 바꾼 뒤 다시 검출해주세요.";
                    return;
                }

                _importResultMessage =
                    (result.overwroteExisting ? "임포트 완료 (기존 시트를 덮어썼습니다): " : "임포트 완료: ")
                    + $"{result.assetPath}\n" +
                    $"행 {result.rowCount}개 / 프레임 총 {result.totalFrameCount}개 " +
                    $"(행별: {string.Join(", ", result.framesPerRow)}), 셀 {result.cellWidth}x{result.cellHeight}px\n" +
                    $"슬라이스 이름: {string.Join(", ", result.rowActions)} (Sprite Mode=Multiple)";
                EditorGUIUtility.PingObject(
                    AssetDatabase.LoadAssetAtPath<Texture2D>(result.assetPath));

                // 방금 슬라이스한 시트를 4번 애니메이션 섹션의 기본 대상으로 잡아 준다.
                _clipSheet = AssetDatabase.LoadAssetAtPath<Texture2D>(result.assetPath);
                ScanClipActions();
            }
            catch (Exception e)
            {
                _importResultMessage = string.Empty;
                EditorUtility.DisplayDialog("슬라이스 적용 실패", e.Message, "확인");
            }
        }

        /// <summary>검출 결과와 안내 문구를 함께 지웁니다. ([검출 결과 지우기] 버튼)</summary>
        private void ClearDetectionAndMessage()
        {
            ClearDetection();
            _detectMessage = string.Empty;
        }

        /// <summary>검출 결과와 썸네일 텍스처를 해제합니다.</summary>
        private void ClearDetection()
        {
            foreach (Texture2D thumb in _thumbnails)
            {
                if (thumb != null)
                {
                    DestroyImmediate(thumb);
                }
            }

            _thumbnails.Clear();
            _detection = null;
        }
    }
}
