using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 4단계 파이프라인 통합 안내 창입니다. (메뉴: Tools/MCP/Pipeline (All-in-One))
    /// 각 단계의 진행 상태(산출물 존재 여부)를 배지로 보여주고, 단계별 개별 창을 열어주며,
    /// 앞 단계 산출물(최신 AssetList_* / PromptSet_*)을 자동으로 찾아 다음 단계 입력으로 표시합니다.
    /// 개별 창의 UI를 재구현하지 않고 기존 창(<see cref="AssetListupWindow"/> 등)으로 안내·연결하는 스텝퍼입니다.
    /// </summary>
    public class PipelineWindow : EditorWindow
    {
        private MCPToolSettings _settings;
        private Vector2 _scroll;

        // 자동 연결된 최신 산출물 (OnFocus/Refresh 시 갱신)
        private string _latestAssetList;
        private string _latestPromptSet;
        private int _confirmedCount;

        /// <summary>파이프라인 통합 창을 엽니다.</summary>
        [MenuItem("Tools/MCP/Pipeline (All-in-One)", false, 20)]
        public static void Open()
        {
            var window = GetWindow<PipelineWindow>();
            window.titleContent = new GUIContent("MCP 파이프라인");
            window.minSize = new Vector2(560f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = MCPToolSettings.GetOrCreate();
            Refresh();
        }

        private void OnFocus()
        {
            // 개별 창에서 산출물을 만든 뒤 이 창으로 돌아오면 상태가 갱신되도록 한다.
            if (_settings != null)
            {
                Refresh();
            }
        }

        /// <summary>디스크를 다시 스캔해 각 단계의 최신 산출물과 확정 개수를 갱신합니다.</summary>
        private void Refresh()
        {
            _latestAssetList = FindLatest("AssetList_");
            _latestPromptSet = FindLatest("PromptSet_");
            _confirmedCount = CandidateGenerator.GetConfirmedOutputPaths(_settings).Count;
        }

        /// <summary>docsRootPath에서 접두사로 시작하는 최신(파일명 기준 내림차순) JSON 경로를 반환합니다.</summary>
        private string FindLatest(string prefix)
        {
            string docs = _settings.docsRootPath.TrimEnd('/');
            if (!Directory.Exists(docs))
            {
                return null;
            }

            return Directory.GetFiles(docs, prefix + "*.json")
                .Select(p => p.Replace('\\', '/'))
                .OrderByDescending(p => p, System.StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private void OnGUI()
        {
            if (_settings == null)
            {
                _settings = MCPToolSettings.GetOrCreate();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("기획서 기반 AI 에셋 생성 파이프라인", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "각 단계는 독립 창으로 실행합니다. 아래 순서대로 진행하거나, 앞 단계 산출물이 이미 있으면 중간 단계부터 시작할 수 있습니다. " +
                "앞 단계에서 만든 최신 산출물이 다음 단계 입력으로 자동 표시됩니다.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("상태 새로고침", GUILayout.Height(22f)))
                {
                    Refresh();
                }

                if (GUILayout.Button("설정 열기", GUILayout.Height(22f)))
                {
                    MCPSettingsWindow.Open();
                }
            }

            EditorGUILayout.Space(6f);

            // 1단계
            bool step1Done = !string.IsNullOrEmpty(_latestAssetList);
            DrawStep(
                "1. 에셋 리스트업 (Asset Listup)",
                step1Done ? StepState.Done : StepState.NotRun,
                "기획서 + 프로젝트 스캔 → 생성할 에셋 목록 JSON",
                step1Done ? "최신 목록: " + _latestAssetList : "아직 저장된 AssetList_*.json이 없습니다.",
                "1단계 창 열기",
                () => AssetListupWindow.Open());

            // 2단계
            bool step2Done = !string.IsNullOrEmpty(_latestPromptSet);
            DrawStep(
                "2. 프롬프트 제작 (Prompt Builder)",
                step2Done ? StepState.Done : (step1Done ? StepState.Ready : StepState.NotRun),
                "에셋 목록 → 항목별 ComfyUI 프롬프트(PromptSet JSON)",
                step2Done
                    ? "최신 프롬프트: " + _latestPromptSet
                    : (step1Done
                        ? "입력(자동 선택): " + _latestAssetList
                        : "먼저 1단계에서 AssetList를 저장하세요."),
                "2단계 창 열기",
                () => PromptBuilderWindow.Open());

            // 3단계
            var step3State = _confirmedCount > 0
                ? StepState.Done
                : (step2Done ? StepState.Ready : StepState.NotRun);
            DrawStep(
                "3. 생성 (ComfyUI Generator)",
                step3State,
                "프롬프트 → 후보 4개 생성 후 항목별 확정",
                step2Done
                    ? "입력(자동 선택): " + _latestPromptSet +
                      (_confirmedCount > 0 ? "  |  확정 항목: " + _confirmedCount + "건" : string.Empty)
                    : "먼저 2단계에서 PromptSet을 저장하세요.",
                "3단계 창 열기",
                () => ComfyUIGeneratorWindow.Open());

            // 4단계 (적용 여부는 안내 수준 — 확정본 유무만 판정)
            DrawStep(
                "4. 적용 (Asset Applier)",
                _confirmedCount > 0 ? StepState.Ready : StepState.NotRun,
                "확정 결과물 → 대상 프리팹/씬 컴포넌트에 적용",
                _confirmedCount > 0
                    ? "적용 대상 목록(자동 선택): " +
                      (string.IsNullOrEmpty(_latestAssetList) ? "(AssetList 없음 — 1단계 확인)" : _latestAssetList)
                    : "먼저 3단계에서 후보를 확정하세요.",
                "4단계 창 열기",
                () => AssetApplierWindow.Open());

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "MCP 도구로 후반부(3~4단계)를 자동화하려면 mcptools_run_pipeline(promptSetPath, autoSelect) 를 사용하세요. " +
                "설정·산출물 현황 진단은 mcptools_status 로 확인할 수 있습니다.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private enum StepState
        {
            NotRun,
            Ready,
            Done
        }

        /// <summary>단계 1개의 상태 배지 + 설명 + 입력 안내 + 창 열기 버튼을 그립니다.</summary>
        private void DrawStep(
            string title, StepState state, string summary, string inputInfo, string buttonLabel,
            System.Action openAction)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                    DrawBadge(state);
                }

                EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(inputInfo, EditorStyles.wordWrappedMiniLabel);

                // 개별 단계 창은 항상 열 수 있게 한다(중간 단계부터 재시작 지원).
                if (GUILayout.Button(buttonLabel, GUILayout.Height(24f)))
                {
                    openAction();
                }
            }

            EditorGUILayout.Space(4f);
        }

        /// <summary>상태 배지(미실행/준비/완료)를 색상과 함께 그립니다.</summary>
        private void DrawBadge(StepState state)
        {
            string label;
            Color color;
            switch (state)
            {
                case StepState.Done:
                    label = "완료";
                    color = new Color(0.30f, 0.70f, 0.35f);
                    break;
                case StepState.Ready:
                    label = "준비";
                    color = new Color(0.30f, 0.55f, 0.85f);
                    break;
                default:
                    label = "미실행";
                    color = new Color(0.55f, 0.55f, 0.55f);
                    break;
            }

            Color prev = GUI.color;
            GUI.color = color;
            GUILayout.Label(label, EditorStyles.miniButton, GUILayout.Width(56f));
            GUI.color = prev;
        }
    }
}
