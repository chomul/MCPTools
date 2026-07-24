using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MCPTools.Runtime;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 파이프라인 후반부 자동화·진단 MCP 도구를 등록합니다.
    /// - mcptools_run_pipeline: 2단계 산출물(PromptSet)부터 3단계 후보 생성 → (autoSelect="first"면) 확정 → 4단계 적용을 순차 실행.
    /// - mcptools_status: 설정값·산출물 현황·버전 등 진단 정보 반환.
    ///
    /// [run_pipeline 비동기 처리 근거]
    /// <see cref="CandidateGenerator.GenerateAsync"/>는 내부에서 SynchronizationContext를 포착하는 await(ConfigureAwait 미사용)를
    /// 포함하므로, Unity 메인 스레드에서 그대로 .GetAwaiter().GetResult()로 블로킹하면 continuation이 블로킹된 메인 스레드로
    /// 되돌아와 데드락이 발생한다. 이를 피하기 위해 각 항목의 생성을 <see cref="Task.Run(System.Func{Task})"/>로 스레드풀에서
    /// 시작한 뒤 블로킹 대기한다(스레드풀 스레드에는 SynchronizationContext가 없어 continuation이 메인 스레드로 돌아오지 않는다).
    /// 확정(ConfirmCandidate)과 적용(ApplyBatch)은 대기가 끝난 뒤 메인 스레드에서 수행한다.
    /// 트레이드오프: 생성이 끝날 때까지 에디터 메인 스레드가 블로킹된다(수 초~수 분). 창(3단계)은 Job 방식으로 멈추지 않지만,
    /// MCP 자동화 경로인 run_pipeline은 순차 완료 결과를 한 번에 반환하기 위해 이 블로킹을 감수한다.
    /// </summary>
    [InitializeOnLoad]
    public static class PipelineTool
    {
        static PipelineTool()
        {
            McpToolRegistry.Register(
                "mcptools_run_pipeline",
                "파이프라인 후반부 자동화: 2단계 산출물(PromptSet JSON, AI가 이미 작성)을 입력으로 각 항목의 3단계 후보를 생성하고, " +
                "autoSelect=\"first\"(기본)면 가장 낮은 시드의 후보를 확정한 뒤 4단계로 대상에 일괄 적용합니다. " +
                "autoSelect=\"none\"이면 후보만 생성하고 확정/적용은 하지 않으며, 후보 목록을 pendingSelections로 반환합니다 " +
                "(이후 mcptools_select_candidate + mcptools_apply_asset으로 진행). " +
                "1·2단계(AssetList/PromptSet 작성)는 AI 중립 설계상 사전에 AI로 작성되어 있어야 합니다. " +
                "파라미터: promptSetPath(필수, Assets/ 상대 PromptSet JSON), autoSelect(\"first\"|\"none\", 기본 \"first\"), workflowName(선택). " +
                "반환 data: { promptSetPath, assetListPath, pendingSelections, applied, failed }. " +
                "주의: 생성 완료까지 에디터가 블로킹됩니다.",
                ExecuteRunPipeline);

            McpToolRegistry.Register(
                "mcptools_status",
                "MCP Tools 진단: 설정값(ComfyUI/브리지 주소, 경로, 후보 개수), 산출물 현황(AssetList/PromptSet 개수·최신 파일, " +
                "3_Confirmed/Images·Audio 및 3_Candidates 개수, 확정 항목 수), 버전·Unity 버전을 반환합니다. " +
                "서버 실시간 연결 확인은 하지 않습니다(동기 블로킹 방지) — 3단계 창 또는 브리지 /health를 참조하세요. " +
                "파라미터: 없음.",
                ExecuteStatus);
        }

        // ─────────────────────────── run_pipeline ───────────────────────────

        private static object ExecuteRunPipeline(Dictionary<string, object> parameters)
        {
            string promptSetPath = GetString(parameters, "promptSetPath");
            if (string.IsNullOrEmpty(promptSetPath))
            {
                throw new ArgumentException("promptSetPath(2단계 PromptSet JSON의 Assets/ 상대 경로) 파라미터가 필요합니다.");
            }

            if (!File.Exists(promptSetPath))
            {
                throw new FileNotFoundException($"PromptSet JSON을 찾을 수 없습니다: \"{promptSetPath}\"", promptSetPath);
            }

            string autoSelect = GetString(parameters, "autoSelect");
            autoSelect = string.IsNullOrEmpty(autoSelect) ? "first" : autoSelect.ToLowerInvariant();
            if (autoSelect != "first" && autoSelect != "none")
            {
                throw new ArgumentException("autoSelect 파라미터는 \"first\" 또는 \"none\"이어야 합니다.");
            }

            string workflowName = GetString(parameters, "workflowName");

            var dict = MiniJson.Deserialize(File.ReadAllText(promptSetPath)) as Dictionary<string, object>;
            PromptSetDocument doc = PromptSetDocument.FromDictionary(dict);
            if (doc == null || doc.items.Count == 0)
            {
                throw new InvalidOperationException(
                    $"PromptSet JSON(\"{promptSetPath}\")에서 항목을 읽지 못했습니다.");
            }

            var settings = MCPToolSettings.GetOrCreate();

            var pendingSelections = new List<object>();
            var failed = new List<object>();
            // autoSelect="first"에서 확정에 성공한 항목: id → 확정본 경로
            var confirmed = new Dictionary<string, string>();

            foreach (PromptItem item in doc.items)
            {
                if (item == null || string.IsNullOrEmpty(item.id))
                {
                    continue;
                }

                // 스레드풀에서 생성을 시작해 메인 스레드 데드락을 피하고 순차 완료를 대기한다(위 클래스 주석 참조).
                // 주의: GenerateAsync 말미의 AssetDatabase.Refresh()는 이 스레드풀 스레드에서 실행되어
                // UnityException을 던질 수 있으나, 후보 PNG/오디오 파일은 그 직전에 이미 디스크에 기록된다.
                // 따라서 예외 여부와 무관하게 메인 스레드에서 Refresh 후 디스크의 실제 후보를 다시 읽어 판정한다.
                List<CandidateInfo> candidates = null;
                Exception genEx = null;
                try
                {
                    candidates = Task.Run(() =>
                        CandidateGenerator.GenerateAsync(settings, item, workflowName, null, null)).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    genEx = e;
                }

                // 메인 스레드 Refresh + 디스크 사실 확인 (백그라운드 Refresh 실패 복구).
                AssetDatabase.Refresh();
                List<CandidateInfo> onDisk = CandidateGenerator.ListCandidates(settings, item.id);
                if (onDisk.Count > 0)
                {
                    candidates = onDisk;
                }
                else if (genEx != null)
                {
                    failed.Add(MakeFailed(item.id, "후보 생성 실패: " + genEx.Message));
                    continue;
                }
                else if (candidates == null || candidates.Count == 0)
                {
                    failed.Add(MakeFailed(item.id, "생성된 후보가 없습니다."));
                    continue;
                }

                if (autoSelect == "none")
                {
                    var candList = new List<object>();
                    foreach (CandidateInfo c in candidates)
                    {
                        candList.Add(new Dictionary<string, object> { { "path", c.path }, { "seed", c.seed } });
                    }

                    pendingSelections.Add(new Dictionary<string, object>
                    {
                        { "assetItemId", item.id },
                        { "candidates", candList }
                    });
                    continue;
                }

                // autoSelect="first": 가장 낮은 시드(리스트는 시드 오름차순)를 확정한다.
                try
                {
                    string selectedPath = CandidateGenerator.ConfirmCandidate(settings, item.id, candidates[0].path);
                    confirmed[item.id] = selectedPath;
                }
                catch (Exception e)
                {
                    failed.Add(MakeFailed(item.id, "확정 실패: " + e.Message));
                }
            }

            var applied = new List<object>();
            if (autoSelect == "first" && confirmed.Count > 0)
            {
                ApplyConfirmed(settings, doc.assetListPath, confirmed, applied, failed);
            }

            return new Dictionary<string, object>
            {
                { "promptSetPath", promptSetPath },
                { "assetListPath", doc.assetListPath },
                { "pendingSelections", pendingSelections },
                { "applied", applied },
                { "failed", failed }
            };
        }

        /// <summary>
        /// 확정된 항목들을 AssetList의 대상 정보로 4단계 일괄 적용합니다.
        /// AssetList 경로가 없거나 항목을 찾지 못하면 해당 항목을 failed에 담습니다.
        /// </summary>
        private static void ApplyConfirmed(
            MCPToolSettings settings, string assetListPath, Dictionary<string, string> confirmed,
            List<object> applied, List<object> failed)
        {
            if (string.IsNullOrEmpty(assetListPath) || !File.Exists(assetListPath))
            {
                foreach (KeyValuePair<string, string> pair in confirmed)
                {
                    failed.Add(MakeFailed(pair.Key,
                        "확정본은 생성했으나 적용할 수 없습니다: PromptSet의 assetListPath가 비어 있거나 파일이 없습니다 " +
                        $"(\"{assetListPath}\"). 확정본 경로: {pair.Value}"));
                }

                return;
            }

            var listDict = MiniJson.Deserialize(File.ReadAllText(assetListPath)) as Dictionary<string, object>;
            AssetListDocument listDoc = AssetListDocument.FromDictionary(listDict);
            if (listDoc == null || listDoc.items.Count == 0)
            {
                foreach (KeyValuePair<string, string> pair in confirmed)
                {
                    failed.Add(MakeFailed(pair.Key,
                        $"AssetList JSON(\"{assetListPath}\")에서 항목을 읽지 못해 적용할 수 없습니다. 확정본 경로: {pair.Value}"));
                }

                return;
            }

            var targets = new List<AssetListItem>();
            var assetPaths = new List<string>();
            foreach (KeyValuePair<string, string> pair in confirmed)
            {
                AssetListItem listItem = listDoc.items.FirstOrDefault(i => i.id == pair.Key);
                if (listItem == null)
                {
                    failed.Add(MakeFailed(pair.Key,
                        $"AssetList에 항목 \"{pair.Key}\"가 없어 적용 대상을 찾지 못했습니다. 확정본 경로: {pair.Value}"));
                    continue;
                }

                targets.Add(listItem);
                assetPaths.Add(pair.Value);
            }

            if (targets.Count == 0)
            {
                return;
            }

            List<ApplyResult> results = AssetApplier.ApplyBatch(targets, assetPaths);
            for (int i = 0; i < targets.Count; i++)
            {
                ApplyResult result = results[i];
                if (result != null && result.success)
                {
                    applied.Add(new Dictionary<string, object>
                    {
                        { "id", targets[i].id },
                        { "prefabPath", result.prefabPath },
                        { "scenePath", result.scenePath },
                        { "objectPath", result.objectPath },
                        { "appliedAssetPath", result.appliedAssetPath }
                    });
                }
                else
                {
                    failed.Add(MakeFailed(targets[i].id, result != null ? result.message : "(적용 결과 없음)"));
                }
            }

            AssetDatabase.SaveAssets();
        }

        // ─────────────────────────── status ───────────────────────────

        private static object ExecuteStatus(Dictionary<string, object> parameters)
        {
            var settings = MCPToolSettings.GetOrCreate();

            string docs = MCPToolFolders.DocsRoot(settings);
            string root = MCPToolFolders.GeneratedRoot(settings);
            string confirmed = MCPToolFolders.ConfirmedRoot(settings);

            // 단계별 하위 폴더 도입 이전 위치의 산출물도 함께 센다.
            string[] assetLists = MCPToolFolders.FindDocuments(docs, MCPToolFolders.AssetListFolder, "AssetList_*.json");
            string[] promptSets = MCPToolFolders.FindDocuments(docs, MCPToolFolders.PromptSetFolder, "PromptSet_*.json");

            var outputs = new Dictionary<string, object>
            {
                { "assetListCount", assetLists.Length },
                { "latestAssetList", LatestFileName(assetLists) },
                { "promptSetCount", promptSets.Length },
                { "latestPromptSet", LatestFileName(promptSets) },
                { "imageCount", CountAssetFiles($"{confirmed}/Images") + CountAssetFiles($"{root}/Images") },
                { "audioCount", CountAssetFiles($"{confirmed}/Audio") + CountAssetFiles($"{root}/Audio") },
                { "candidateFolderCount",
                    CountSubfolders($"{root}/{MCPToolFolders.CandidatesFolder}") +
                    CountSubfolders($"{root}/{MCPToolFolders.LegacyCandidatesFolder}") },
                { "confirmedCount", CandidateGenerator.GetConfirmedOutputPaths(settings).Count }
            };

            var config = new Dictionary<string, object>
            {
                { "comfyUIServerUrl", settings.comfyUIServerUrl },
                { "bridgeServerUrl", settings.bridgeServerUrl },
                { "generatedRootPath", settings.generatedRootPath },
                { "docsRootPath", settings.docsRootPath },
                { "defaultImageWorkflow", settings.defaultImageWorkflow },
                { "candidateCount", settings.candidateCount }
            };

            return new Dictionary<string, object>
            {
                { "version", MCPToolsInfo.Version },
                { "unityVersion", Application.unityVersion },
                { "config", config },
                { "outputs", outputs },
                { "serverHealthNote",
                    "서버 실시간 연결 확인은 이 도구가 수행하지 않습니다. Tools/MCP/3. ComfyUI Generator 창의 서버 상태 " +
                    "또는 브리지 서버 /health를 참조하세요." }
            };
        }

        // ─────────────────────────── 헬퍼 ───────────────────────────

        /// <summary>이미지/오디오 폴더의 에셋 파일 수를 셉니다(.meta 제외).</summary>
        private static int CountAssetFiles(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            return Directory.GetFiles(folder)
                .Count(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
        }

        private static int CountSubfolders(string folder)
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder).Length : 0;
        }

        /// <summary>경로 목록에서 가장 최신(파일명 내림차순) 파일명을 반환합니다(경로 제외). 없으면 빈 문자열.</summary>
        private static string LatestFileName(string[] paths)
        {
            string latest = paths
                .Select(Path.GetFileName)
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return latest ?? string.Empty;
        }

        private static Dictionary<string, object> MakeFailed(string id, string reason)
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "reason", reason }
            };
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }
    }
}
