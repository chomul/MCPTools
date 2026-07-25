using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MCPTools.Runtime;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 파이프라인 후반부 자동화·진단 MCP 도구를 등록합니다.
    /// - mcptools_run_pipeline: 2단계 산출물(PromptSet)부터 3단계 후보 생성 → (autoSelect="first"면) 확정 → 4단계 적용을
    ///   Job으로 실행합니다(즉시 status:"started" 반환, 완료까지 기다리지 않음).
    /// - mcptools_status: 설정값·산출물 현황·버전에 더해 파이프라인 Job의 진행 상태(pipeline 블록)를 반환.
    ///
    /// [run_pipeline Job 모델 근거]
    /// 이전 구현은 항목마다 Task.Run(...).GetAwaiter().GetResult()로 생성 완료를 기다려 에디터 메인 스레드를 통째로
    /// 블로킹했다(항목이 여러 개면 수 분). 진행률 표시도 취소도 불가능했다. 그래서 3단계 도구
    /// (<see cref="ComfyUIGeneratorTool"/>)와 동일한 Job 모델로 전환한다: 도구 호출은 즉시 반환하고 실제 작업은
    /// <see cref="RunPipelineJobAsync"/>(async void)에서 진행하며, 진행 상황과 최종 결과는 mcptools_status의
    /// pipeline 블록으로 조회한다. Job은 동시에 1개만 허용한다.
    ///
    /// [메인 스레드 보장 근거]
    /// MCP 핸들러는 <see cref="McpToolRegistry.Execute"/>를 통해 에디터 메인 스레드에서 호출되므로
    /// <see cref="RunPipelineJobAsync"/>는 Unity의 SynchronizationContext를 포착한 채 시작된다. 이 메서드의 await는
    /// ConfigureAwait(false)를 쓰지 않으므로 continuation이 항상 메인 스레드로 복귀하며, 따라서 에셋을 건드리는
    /// AssetDatabase 호출·<see cref="CandidateGenerator.ConfirmCandidate"/>·AssetApplier.ApplyBatch는
    /// 모두 메인 스레드에서 실행된다. 안전장치로 Job 시작 시 메인 스레드 ID를 기록해 확정·적용 직전에 검증한다
    /// (<see cref="EnsureMainThread"/>).
    ///
    /// [에셋 임포트 배치 처리]
    /// 생성 루프는 <see cref="CandidateGenerator.GenerateAsync"/>에 refreshAssets:false를 넘겨 항목마다 일어나던
    /// Refresh(프로젝트 전체 스캔)를 아예 발생시키지 않고, 루프가 끝난 뒤 1회만 Refresh한다. 결과적으로 항목 수와
    /// 무관하게 Refresh는 1회다. 확정(ConfirmCandidate)은 임포트 결과(TextureImporter)를 바로 읽어야 하므로
    /// 반드시 그 Refresh 이후에 수행한다(생성 전부 → Refresh → 확정·적용의 2단계 구조).
    ///
    /// 이 구간을 <see cref="AssetDatabase.StartAssetEditing"/>/<see cref="AssetDatabase.StopAssetEditing"/>으로
    /// 감싸지 않는 이유: 생성 루프는 항목마다 네트워크 I/O를 await하므로 수 분~수십 분 이어질 수 있다.
    /// StartAssetEditing은 짧은 동기 배치용이며, 그 구간 동안 에셋 임포트와 스크립트 컴파일이 멈춰
    /// "실행 중에도 에디터를 계속 쓸 수 있다"는 Job 모델의 목적을 무너뜨린다. 게다가 구간 중 도메인 리로드나
    /// 에디터 강제 종료가 일어나면 StopAssetEditing이 실행되지 않아 AssetDatabase가 정지된 채로 남는다
    /// (에디터 재시작 필요).
    /// </summary>
    [InitializeOnLoad]
    public static class PipelineTool
    {
        /// <summary>진행 중/완료된 파이프라인 Job 1건의 상태입니다 (동시에 1개만 존재).</summary>
        private sealed class PipelineJob
        {
            public string Status = "running"; // running / completed / failed
            public string Message = string.Empty;
            public string Phase = "generating"; // generating / confirming / applying / done
            public string PromptSetPath = string.Empty;
            public string AssetListPath = string.Empty;
            public string AutoSelect = "first";
            public int TotalCount;
            public int CurrentIndex; // 1-based, 현재 생성 중인 항목 번호 (0이면 아직 시작 전)
            public string CurrentItemId = string.Empty;
            public float CurrentItemProgress; // 현재 항목 생성 진행률 0~1
            public int TimeoutSeconds;
            public DateTime StartedAtUtc = DateTime.UtcNow;
            public int MainThreadId;
            public CancellationTokenSource Cts;
            public readonly List<object> PendingSelections = new List<object>();
            public readonly List<object> Applied = new List<object>();
            public readonly List<object> Failed = new List<object>();
        }

        /// <summary>
        /// 최근 파이프라인 Job 상태입니다 (도메인 리로드 시 초기화되며, 생성된 후보·확정본은 디스크에 남는다).
        /// 생성·갱신·조회가 모두 메인 스레드에서만 일어나므로 별도 동기화는 두지 않는다.
        /// </summary>
        private static PipelineJob _job;

        static PipelineTool()
        {
            McpToolRegistry.Register(
                "mcptools_run_pipeline",
                "파이프라인 후반부 자동화 Job을 시작합니다 (비동기, 완료까지 기다리지 않음). 2단계 산출물(PromptSet JSON, " +
                "AI가 이미 작성)을 입력으로 각 항목의 3단계 후보를 생성하고, autoSelect=\"first\"(기본)면 가장 낮은 시드의 " +
                "후보를 확정한 뒤 4단계로 대상에 일괄 적용합니다. " +
                "autoSelect=\"none\"이면 후보만 생성하고 확정/적용은 하지 않으며, 후보 목록을 pendingSelections에 담습니다 " +
                "(이후 mcptools_select_candidate + mcptools_apply_asset으로 진행). " +
                "1·2단계(AssetList/PromptSet 작성)는 AI 중립 설계상 사전에 AI로 작성되어 있어야 합니다. " +
                "파라미터: promptSetPath(필수, Assets/ 상대 PromptSet JSON), autoSelect(\"first\"|\"none\", 기본 \"first\"), workflowName(선택). " +
                "반환 data: { status:\"started\", promptSetPath, assetListPath, itemCount, timeoutSeconds }. " +
                "진행 상황과 최종 결과(pendingSelections/applied/failed)는 mcptools_status의 pipeline 블록으로 폴링하세요. " +
                "Job은 동시에 1개만 실행할 수 있습니다.",
                ExecuteRunPipeline);

            McpToolRegistry.Register(
                "mcptools_status",
                "MCP Tools 진단: 설정값(ComfyUI/브리지 주소, 경로, 후보 개수), 산출물 현황(AssetList/PromptSet 개수·최신 파일, " +
                "3_Confirmed/Images·Audio 및 3_Candidates 개수, 확정 항목 수), 버전·Unity 버전, " +
                "그리고 mcptools_run_pipeline Job의 진행 상태를 반환합니다. " +
                "pipeline: { status: \"idle\"|\"running\"|\"completed\"|\"failed\", message, phase, promptSetPath, assetListPath, " +
                "itemCount, currentIndex, currentItemId, elapsedSeconds, pendingSelections, applied, failed } — " +
                "run_pipeline의 완료 여부와 결과를 이 블록으로 폴링하세요. " +
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

            if (_job != null && _job.Status == "running")
            {
                throw new InvalidOperationException(
                    $"파이프라인 Job이 이미 실행 중입니다 (PromptSet \"{_job.PromptSetPath}\", " +
                    $"{_job.CurrentIndex}/{_job.TotalCount}번째 항목, 경과 {(int)(DateTime.UtcNow - _job.StartedAtUtc).TotalSeconds}초). " +
                    "동시에 1개만 실행할 수 있습니다. mcptools_status의 pipeline 블록으로 완료를 확인한 뒤 다시 호출해주세요 " +
                    $"(응답이 없으면 남은 타임아웃 {Math.Max(0, _job.TimeoutSeconds - (int)(DateTime.UtcNow - _job.StartedAtUtc).TotalSeconds)}초 뒤 자동 실패 처리됩니다).");
            }

            var settings = MCPToolSettings.GetOrCreate();

            List<PromptItem> items = doc.items.Where(i => i != null && !string.IsNullOrEmpty(i.id)).ToList();
            if (items.Count == 0)
            {
                throw new InvalidOperationException(
                    $"PromptSet JSON(\"{promptSetPath}\")에 id를 가진 항목이 없습니다.");
            }

            // 상한: 항목 수 × 브리지 Job 타임아웃(settings.jobTimeoutSeconds). 브리지가 멈춰도 무한 대기하지 않는다.
            int perItemTimeout = Mathf.Max(1, settings.jobTimeoutSeconds);
            int timeoutSeconds = (int)Math.Min(int.MaxValue / 1000L, (long)items.Count * perItemTimeout);

            var job = new PipelineJob
            {
                PromptSetPath = promptSetPath,
                AssetListPath = doc.assetListPath ?? string.Empty,
                AutoSelect = autoSelect,
                TotalCount = items.Count,
                TimeoutSeconds = timeoutSeconds,
                MainThreadId = Thread.CurrentThread.ManagedThreadId,
                Cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)),
                Message = $"항목 {items.Count}개의 후보 생성을 시작했습니다."
            };
            _job = job;

            // Job 시작: 완료까지 기다리지 않는다 (에디터 메인 스레드 블로킹 방지).
            RunPipelineJobAsync(job, settings, items, workflowName);

            return new Dictionary<string, object>
            {
                { "status", "started" },
                { "promptSetPath", promptSetPath },
                { "assetListPath", job.AssetListPath },
                { "itemCount", items.Count },
                { "timeoutSeconds", timeoutSeconds },
                { "statusNote", "진행 상황과 결과(pendingSelections/applied/failed)는 mcptools_status의 pipeline 블록으로 확인하세요." }
            };
        }

        /// <summary>
        /// 파이프라인 Job 본체입니다: ① 전 항목 후보 생성 → ② (autoSelect="first") 확정 → ③ 4단계 일괄 적용.
        /// async void지만 모든 예외를 잡아 Job 상태로 옮기므로 도메인 밖으로 전파되지 않습니다.
        /// 시작 시점의 SynchronizationContext(에디터 메인 스레드)를 포착하므로 await 이후 코드는 메인 스레드에서 실행됩니다.
        /// </summary>
        private static async void RunPipelineJobAsync(
            PipelineJob job, MCPToolSettings settings, List<PromptItem> items, string workflowName)
        {
            CancellationToken ct = job.Cts.Token;

            // 생성에 성공한 항목: 항목 → 후보 목록 (확정은 임포트 결과가 필요해 루프 뒤 Refresh 이후에 한다)
            var generated = new List<KeyValuePair<PromptItem, List<CandidateInfo>>>();

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    PromptItem item = items[i];
                    job.CurrentIndex = i + 1;
                    job.CurrentItemId = item.id;
                    job.CurrentItemProgress = 0f;
                    job.Message = $"후보 생성 중 ({job.CurrentIndex}/{job.TotalCount}): {item.id}";

                    var progress = new Progress<float>(p => job.CurrentItemProgress = Mathf.Clamp01(p));
                    try
                    {
                        // refreshAssets:false — 항목마다 Refresh(프로젝트 전체 스캔)하지 않는다.
                        // 후보 파일은 디스크에만 기록되며, 후보 조회(CandidateGenerator.ListCandidates)와
                        // 확정 복사(File.Copy)는 Directory/File API를 직접 쓰므로 미임포트 상태여도 문제없다.
                        List<CandidateInfo> candidates = await CandidateGenerator.GenerateAsync(
                            settings, item, workflowName, null, null,
                            interactive: false, refreshAssets: false, progress: progress, ct: ct);

                        if (candidates == null || candidates.Count == 0)
                        {
                            job.Failed.Add(MakeFailed(item.id, "생성된 후보가 없습니다."));
                            continue;
                        }

                        generated.Add(new KeyValuePair<PromptItem, List<CandidateInfo>>(item, candidates));
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // 타임아웃/취소는 항목 실패가 아니라 Job 중단
                    }
                    catch (Exception e)
                    {
                        job.Failed.Add(MakeFailed(item.id, "후보 생성 실패: " + e.Message));
                    }
                }

                // 미뤄 둔 임포트를 여기서 한 번에 처리한다 (항목 수와 무관하게 Refresh 1회).
                // 이후 ConfirmCandidate가 TextureImporter를 읽으므로 확정보다 반드시 앞서야 한다.
                AssetDatabase.Refresh();

                // 여기부터는 에셋을 수정한다. await 이후 메인 스레드 복귀를 안전장치로 검증한다.
                EnsureMainThread(job);

                if (job.AutoSelect == "none")
                {
                    job.Phase = "done";
                    foreach (KeyValuePair<PromptItem, List<CandidateInfo>> pair in generated)
                    {
                        var candList = new List<object>();
                        foreach (CandidateInfo c in pair.Value)
                        {
                            candList.Add(new Dictionary<string, object> { { "path", c.path }, { "seed", c.seed } });
                        }

                        job.PendingSelections.Add(new Dictionary<string, object>
                        {
                            { "assetItemId", pair.Key.id },
                            { "candidates", candList }
                        });
                    }
                }
                else
                {
                    // autoSelect="first": 가장 낮은 시드(리스트는 시드 오름차순)를 확정한다.
                    job.Phase = "confirming";
                    job.Message = "후보 확정 중...";
                    var confirmed = new Dictionary<string, string>();
                    foreach (KeyValuePair<PromptItem, List<CandidateInfo>> pair in generated)
                    {
                        try
                        {
                            confirmed[pair.Key.id] =
                                CandidateGenerator.ConfirmCandidate(settings, pair.Key.id, pair.Value[0].path);
                        }
                        catch (Exception e)
                        {
                            job.Failed.Add(MakeFailed(pair.Key.id, "확정 실패: " + e.Message));
                        }
                    }

                    if (confirmed.Count > 0)
                    {
                        job.Phase = "applying";
                        job.Message = "대상에 적용 중...";
                        ApplyConfirmed(settings, job.AssetListPath, confirmed, job.Applied, job.Failed);
                    }

                    job.Phase = "done";
                }

                job.Status = "completed";
                job.Message =
                    $"완료: 적용 {job.Applied.Count}건, 대기 {job.PendingSelections.Count}건, 실패 {job.Failed.Count}건.";
            }
            catch (OperationCanceledException)
            {
                job.Status = "failed";
                job.Message =
                    $"타임아웃({job.TimeoutSeconds}초 = 항목 {job.TotalCount}개 × 설정 jobTimeoutSeconds)으로 중단했습니다. " +
                    $"{job.CurrentIndex}/{job.TotalCount}번째 항목(\"{job.CurrentItemId}\")에서 멈췄습니다. " +
                    "브리지 서버와 ComfyUI 상태를 확인한 뒤 다시 실행해주세요.";
            }
            catch (Exception e)
            {
                job.Status = "failed";
                job.Message = e.Message;
            }
            finally
            {
                CancellationTokenSource cts = job.Cts;
                job.Cts = null;
                if (cts != null)
                {
                    cts.Dispose();
                }
            }
        }

        /// <summary>
        /// 에셋을 수정하기 직전에 메인 스레드 복귀를 확인합니다.
        /// (await continuation은 Unity SynchronizationContext로 돌아오므로 정상 경로에서는 항상 통과합니다.)
        /// </summary>
        private static void EnsureMainThread(PipelineJob job)
        {
            if (Thread.CurrentThread.ManagedThreadId != job.MainThreadId)
            {
                throw new InvalidOperationException(
                    "파이프라인 Job이 메인 스레드로 복귀하지 못해 에셋 수정을 중단했습니다. " +
                    "후보 파일은 디스크에 남아 있으므로 mcptools_list_candidates로 확인 후 " +
                    "mcptools_select_candidate/mcptools_apply_asset으로 이어서 진행해주세요.");
            }
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
                { "pipeline", BuildPipelineStatus() },
                { "serverHealthNote",
                    "서버 실시간 연결 확인은 이 도구가 수행하지 않습니다. Tools/MCP/3. ComfyUI Generator 창의 서버 상태 " +
                    "또는 브리지 서버 /health를 참조하세요." }
            };
        }

        /// <summary>
        /// mcptools_run_pipeline Job의 진행 상태를 반환합니다 (Job 기록이 없으면 status:"idle").
        /// 완료 시 기존 run_pipeline 반환 필드(promptSetPath, assetListPath, pendingSelections, applied, failed)를
        /// 같은 이름으로 그대로 담습니다.
        /// </summary>
        private static Dictionary<string, object> BuildPipelineStatus()
        {
            PipelineJob job = _job;
            if (job == null)
            {
                return new Dictionary<string, object>
                {
                    { "status", "idle" },
                    { "message", "이 세션에서 실행한 파이프라인 Job이 없습니다. mcptools_run_pipeline으로 시작하세요." }
                };
            }

            return new Dictionary<string, object>
            {
                { "status", job.Status },
                { "message", job.Message },
                { "phase", job.Phase },
                { "promptSetPath", job.PromptSetPath },
                { "assetListPath", job.AssetListPath },
                { "autoSelect", job.AutoSelect },
                { "itemCount", job.TotalCount },
                { "currentIndex", job.CurrentIndex },
                { "currentItemId", job.CurrentItemId },
                { "currentItemProgress", Mathf.Round(job.CurrentItemProgress * 100f) / 100f },
                { "elapsedSeconds", (int)(DateTime.UtcNow - job.StartedAtUtc).TotalSeconds },
                { "timeoutSeconds", job.TimeoutSeconds },
                { "pendingSelections", job.PendingSelections },
                { "applied", job.Applied },
                { "failed", job.Failed }
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
