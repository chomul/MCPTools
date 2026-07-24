using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 스프라이트 시트 창입니다. (메뉴: Tools/MCP/Sprite Sheet)
    /// 레퍼런스 이미지 첨부 전제의 멀티 행(동작별 Row) 통합 시트 프롬프트를 조립해
    /// 미리보기/클립보드 복사/JSON 저장하고, 외부 AI가 생성한 시트 png를
    /// 같은 행 정의로 배경 제거·정규화 임포트하는 섹션을 제공합니다.
    /// </summary>
    public class SpriteSheetPromptWizard : EditorWindow
    {
        private static readonly string[] ActionLabels = { "걷기 (walk)", "달리기 (run)", "공격 (attack)", "대기 (idle)", "사망 (death)", "직접 입력..." };
        private static readonly string[] DirectionLabels = { "오른쪽 (RIGHT)", "왼쪽 (LEFT)" };
        private static readonly string[] BackgroundLabels = { "흰색 단색 (임포트 시 제거)", "투명" };

        private const float PrimaryButtonHeight = 30f;

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
        private int _directionIndex; // 0=RIGHT
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

        /// <summary>스프라이트 시트 창(프롬프트 생성 + 시트 임포트)을 엽니다.</summary>
        [MenuItem("Tools/MCP/Sprite Sheet", false, 50)]
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

            RefreshAiToolList(false);
        }

        private void OnDisable()
        {
            // 창이 닫히면 진행 중인 AI 실행을 취소한다.
            if (_aiCancelSource != null)
            {
                _aiCancelSource.Cancel();
            }
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawPromptForm();
            EditorGUILayout.Space(6f);
            DrawPromptResult();
            EditorGUILayout.Space(10f);
            DrawImportSection();

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
                DrawRowList();

                EditorGUILayout.Space(4f);
                _cellSize = Mathf.Max(32, EditorGUILayout.IntField("셀 크기 (px)", _cellSize));
                _directionIndex = EditorGUILayout.Popup("바라보는 방향", _directionIndex, DirectionLabels);
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

        private void DrawRowList()
        {
            int removeIndex = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                RowEntry row = _rows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"행 {i + 1}", GUILayout.Width(36f));
                    row.presetIndex = EditorGUILayout.Popup(row.presetIndex, ActionLabels);
                    if (row.presetIndex == ActionLabels.Length - 1)
                    {
                        row.customAction = EditorGUILayout.TextField(row.customAction, GUILayout.MinWidth(80f));
                    }

                    EditorGUILayout.LabelField("프레임", GUILayout.Width(44f));
                    row.frameCount = Mathf.Clamp(EditorGUILayout.IntField(row.frameCount, GUILayout.Width(40f)), 1, SpriteSheetPromptBuilder.MaxFrameCount);

                    using (new EditorGUI.DisabledScope(_rows.Count <= 1))
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
                _rows.RemoveAt(removeIndex);
            }

            if (GUILayout.Button("+ 행 추가"))
            {
                _rows.Add(new RowEntry { presetIndex = 0, frameCount = 8 });
            }
        }

        /// <summary>UI 행 목록을 빌더/임포터 공용 행 정의로 변환합니다.</summary>
        private List<SpriteSheetRowDef> BuildRowDefs()
        {
            var defs = new List<SpriteSheetRowDef>();
            for (int i = 0; i < _rows.Count; i++)
            {
                RowEntry row = _rows[i];
                string action = row.presetIndex == ActionLabels.Length - 1
                    ? row.customAction
                    : SpriteSheetPromptBuilder.ActionPresets[row.presetIndex];
                if (string.IsNullOrWhiteSpace(action))
                {
                    throw new InvalidOperationException($"행 {i + 1}의 동작명을 입력해주세요 (직접 입력 선택 시 필수).");
                }

                defs.Add(new SpriteSheetRowDef(action.Trim(), row.frameCount));
            }

            return defs;
        }

        /// <summary>템플릿 방식으로 프롬프트를 생성·저장합니다. (빠른 생성 경로 및 AI 실패 시 폴백)</summary>
        /// <param name="fallbackReason">AI 폴백으로 호출된 경우 그 사유 (안내 문구에 포함). null이면 일반 템플릿 생성.</param>
        private void GeneratePrompt(string fallbackReason = null)
        {
            try
            {
                List<SpriteSheetRowDef> rows = BuildRowDefs();
                bool faceRight = _directionIndex == 0;
                bool whiteBackground = _backgroundIndex == 0;

                _prompt = SpriteSheetPromptBuilder.BuildPrompt(
                    _useReferenceImage, _characterDescription, rows, _cellSize, faceRight, whiteBackground,
                    _gameGenre, _artStyle, _extraNotes);
                _savedPromptPath = SpriteSheetPromptBuilder.SavePromptJson(
                    _useReferenceImage, _characterDescription, rows, _cellSize, faceRight, whiteBackground, _prompt,
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
            bool faceRight = _directionIndex == 0;
            bool whiteBackground = _backgroundIndex == 0;
            try
            {
                rows = BuildRowDefs();
                metaPrompt = SpriteSheetPromptBuilder.BuildMetaPrompt(
                    _useReferenceImage, _characterDescription, rows, _cellSize, faceRight, whiteBackground,
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
                    _useReferenceImage, _characterDescription, rows, _cellSize, faceRight, whiteBackground, _prompt,
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
                    "위 1번 섹션의 동작 행 목록과 배경 설정을 그대로 사용합니다.\n" +
                    "배경이 흰색이면 외곽 flood-fill로 배경·격자선만 투명화한 뒤, " +
                    "시트에 그려진 격자 간격대로 셀을 나누고 셀 내 위치를 보존해 임포트합니다.", MessageType.None);

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_importImagePath)))
                {
                    if (GUILayout.Button("배경 제거 + 정규화 + 임포트", GUILayout.Height(PrimaryButtonHeight)))
                    {
                        RunImport();
                    }
                }

                if (!string.IsNullOrEmpty(_importResultMessage))
                {
                    EditorGUILayout.HelpBox(_importResultMessage, MessageType.Info);
                }
            }
        }

        private void RunImport()
        {
            try
            {
                List<SpriteSheetRowDef> rows = BuildRowDefs();
                bool whiteBackground = _backgroundIndex == 0;

                SpriteSheetImportResult result = SpriteSheetImporter.Import(
                    _importImagePath, rows, whiteBackground, true);
                _importResultMessage =
                    $"임포트 완료: {result.assetPath}\n" +
                    $"행 {result.rowCount}개 / 프레임 총 {result.totalFrameCount}개 " +
                    $"(행별: {string.Join(", ", result.framesPerRow)}), 셀 {result.cellWidth}x{result.cellHeight}px, " +
                    "Sprite Mode=Multiple 동작명 기반 슬라이스 적용됨" +
                    (result.usedDetectedLayout
                        ? $"\n※ 검출된 격자 구성이 행 정의와 다릅니다 (행별 이름: {string.Join(", ", result.rowActions)})"
                        : string.Empty);
                EditorGUIUtility.PingObject(
                    AssetDatabase.LoadAssetAtPath<Texture2D>(result.assetPath));
            }
            catch (Exception e)
            {
                _importResultMessage = string.Empty;
                EditorUtility.DisplayDialog("스프라이트 시트 임포트 실패", e.Message, "확인");
            }
        }
    }
}
