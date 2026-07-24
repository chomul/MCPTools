using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>
    /// 3단계 생성 MCP 도구를 등록합니다. 생성/조회/확정 3분할 + Job 방식:
    /// - mcptools_generate_candidates: 생성 Job 시작 (즉시 status:"started" 반환, 완료까지 대기하지 않음).
    /// - mcptools_list_candidates: Job 상태(running/completed/failed/idle)와 후보 목록 반환.
    /// - mcptools_select_candidate: 후보 확정 (창과 동일한 <see cref="CandidateGenerator.ConfirmCandidate"/> 공용 로직).
    /// </summary>
    [InitializeOnLoad]
    public static class ComfyUIGeneratorTool
    {
        /// <summary>진행 중/완료된 생성 Job 1건의 상태입니다.</summary>
        private sealed class GenerationJob
        {
            public string Status = "running"; // running / completed / failed
            public string Message = string.Empty;
            public List<CandidateInfo> Candidates = new List<CandidateInfo>();
        }

        // assetItemId → 최근 Job 상태 (도메인 리로드 시 초기화되며, 후보 파일 자체는 디스크에 남는다)
        private static readonly Dictionary<string, GenerationJob> Jobs = new Dictionary<string, GenerationJob>();

        static ComfyUIGeneratorTool()
        {
            McpToolRegistry.Register(
                "mcptools_generate_candidates",
                "3단계 후보 생성 Job을 브리지 서버 경유로 시작합니다 (비동기, 완료까지 기다리지 않음). 기준 시드부터 " +
                "+1씩 증가시키며 후보를 생성해 Assets/Generated/3_Candidates/{assetItemId}/에 저장합니다. " +
                "완료 여부와 결과는 mcptools_list_candidates로 폴링하세요. " +
                "파라미터: promptSetPath(필수, Assets/ 상대 PromptSet JSON), assetItemId(필수), " +
                "workflowName(선택: GenerateImage|GenerateImageFlux|UI|StyleChange|Audio, 기본은 항목 종류별 자동 선택), " +
                "variables(선택, {\"nodeId.field\": 값} 객체 — 워크플로 노드 inputs 덮어쓰기), baseSeed(선택, 기본 무작위). " +
                "반환 data: { status:\"started\", assetItemId, candidateFolder }.",
                ExecuteGenerate);

            McpToolRegistry.Register(
                "mcptools_list_candidates",
                "3단계 후보 생성 Job 상태와 후보 목록을 반환합니다. " +
                "파라미터: assetItemId(필수). " +
                "반환 data: { status: \"running\"|\"completed\"|\"failed\"|\"idle\", message, candidates:[{path,seed}] }. " +
                "idle은 이 세션에 Job 기록이 없는 상태로, 디스크의 기존 후보 목록을 반환합니다.",
                ExecuteList);

            McpToolRegistry.Register(
                "mcptools_select_candidate",
                "3단계 후보 1개를 확정합니다: Assets/Generated/3_Confirmed/Images/(오디오는 Audio/)로 복사하고 " +
                "GenerationResults.json에 기록하며, 이미지 항목은 Sprite 임포트 설정을 적용합니다(RawImage 대상은 Texture 유지). " +
                "파라미터: assetItemId(필수), candidatePath(필수, mcptools_list_candidates가 반환한 후보 경로). " +
                "반환 data: { selectedPath }.",
                ExecuteSelect);
        }

        private static object ExecuteGenerate(Dictionary<string, object> parameters)
        {
            string promptSetPath = GetString(parameters, "promptSetPath");
            string assetItemId = GetString(parameters, "assetItemId");
            if (string.IsNullOrEmpty(promptSetPath) || string.IsNullOrEmpty(assetItemId))
            {
                throw new ArgumentException(
                    "promptSetPath(2단계 PromptSet JSON의 Assets/ 상대 경로)와 assetItemId 파라미터가 필요합니다.");
            }

            GenerationJob existing;
            if (Jobs.TryGetValue(assetItemId, out existing) && existing.Status == "running")
            {
                throw new InvalidOperationException(
                    $"항목 \"{assetItemId}\"의 생성 Job이 이미 실행 중입니다. mcptools_list_candidates로 완료를 확인해주세요.");
            }

            if (!File.Exists(promptSetPath))
            {
                throw new FileNotFoundException($"PromptSet JSON을 찾을 수 없습니다: \"{promptSetPath}\"", promptSetPath);
            }

            var dict = MiniJson.Deserialize(File.ReadAllText(promptSetPath)) as Dictionary<string, object>;
            PromptSetDocument doc = PromptSetDocument.FromDictionary(dict);
            PromptItem item = doc != null ? doc.items.FirstOrDefault(i => i.id == assetItemId) : null;
            if (item == null)
            {
                throw new ArgumentException(
                    $"PromptSet JSON(\"{promptSetPath}\")에서 항목 \"{assetItemId}\"를 찾을 수 없습니다.");
            }

            string workflowName = GetString(parameters, "workflowName");
            long? baseSeed = GetLong(parameters, "baseSeed");
            Dictionary<string, object> variables = null;
            if (parameters != null && parameters.TryGetValue("variables", out object varsObj))
            {
                variables = varsObj as Dictionary<string, object>;
                if (variables == null && varsObj != null)
                {
                    throw new ArgumentException(
                        "variables 파라미터는 {\"nodeId.field\": 값} 형태의 객체여야 합니다.");
                }
            }

            var settings = MCPToolSettings.GetOrCreate();
            var job = new GenerationJob();
            Jobs[assetItemId] = job;

            // Job 시작: 완료까지 기다리지 않고 상태만 갱신한다 (에디터 메인스레드 데드락 방지).
            RunJobAsync(job, settings, item, workflowName, variables, baseSeed);

            return new Dictionary<string, object>
            {
                { "status", "started" },
                { "assetItemId", assetItemId },
                { "candidateFolder", CandidateGenerator.GetCandidateFolder(settings, assetItemId) }
            };
        }

        private static async void RunJobAsync(
            GenerationJob job, MCPToolSettings settings, PromptItem item, string workflowName,
            Dictionary<string, object> variables, long? baseSeed)
        {
            try
            {
                List<CandidateInfo> candidates =
                    await CandidateGenerator.GenerateAsync(settings, item, workflowName, variables, baseSeed);
                job.Candidates = candidates;
                job.Status = "completed";
                job.Message = $"후보 {candidates.Count}개 생성 완료.";
            }
            catch (Exception e)
            {
                job.Status = "failed";
                job.Message = e.Message;
            }
        }

        private static object ExecuteList(Dictionary<string, object> parameters)
        {
            string assetItemId = GetString(parameters, "assetItemId");
            if (string.IsNullOrEmpty(assetItemId))
            {
                throw new ArgumentException("assetItemId 파라미터가 필요합니다.");
            }

            var settings = MCPToolSettings.GetOrCreate();

            string status = "idle";
            string message = string.Empty;
            List<CandidateInfo> candidates;

            GenerationJob job;
            if (Jobs.TryGetValue(assetItemId, out job))
            {
                status = job.Status;
                message = job.Message;
                candidates = job.Status == "completed"
                    ? job.Candidates
                    : CandidateGenerator.ListCandidates(settings, assetItemId);
            }
            else
            {
                candidates = CandidateGenerator.ListCandidates(settings, assetItemId);
                message = candidates.Count > 0
                    ? "이 세션의 Job 기록은 없지만 디스크에 기존 후보가 있습니다."
                    : "생성된 후보가 없습니다. mcptools_generate_candidates로 먼저 생성하세요.";
            }

            var candidateList = new List<object>();
            foreach (CandidateInfo candidate in candidates)
            {
                candidateList.Add(new Dictionary<string, object>
                {
                    { "path", candidate.path },
                    { "seed", candidate.seed }
                });
            }

            return new Dictionary<string, object>
            {
                { "status", status },
                { "message", message },
                { "candidates", candidateList }
            };
        }

        private static object ExecuteSelect(Dictionary<string, object> parameters)
        {
            string assetItemId = GetString(parameters, "assetItemId");
            string candidatePath = GetString(parameters, "candidatePath");
            if (string.IsNullOrEmpty(assetItemId) || string.IsNullOrEmpty(candidatePath))
            {
                throw new ArgumentException("assetItemId와 candidatePath 파라미터가 필요합니다.");
            }

            var settings = MCPToolSettings.GetOrCreate();
            string selectedPath = CandidateGenerator.ConfirmCandidate(settings, assetItemId, candidatePath);

            return new Dictionary<string, object>
            {
                { "selectedPath", selectedPath }
            };
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }

        private static long? GetLong(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out object v) || v == null)
            {
                return null;
            }

            if (v is long l)
            {
                return l;
            }

            if (v is int i)
            {
                return i;
            }

            if (v is double d)
            {
                return (long)d;
            }

            if (v is string s && long.TryParse(s, out long parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
