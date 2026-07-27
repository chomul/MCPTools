using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 후보 파일 1건의 정보입니다 (경로는 Assets/ 기준 상대 경로).
    /// </summary>
    public class CandidateInfo
    {
        /// <summary>후보 파일 경로 (Assets/ 기준 상대 경로).</summary>
        public string path;

        /// <summary>생성에 사용된 시드.</summary>
        public long seed;
    }

    /// <summary>
    /// 3단계 후보 생성 오케스트레이터입니다. 기준 시드에서 seed..seed+N-1 순차 큐잉으로
    /// 후보 N개(기본 4개)를 생성해 <c>Assets/Generated/3_Candidates/{assetItemId}/</c>에 저장하고,
    /// 선택된 후보를 <c>Assets/Generated/3_Confirmed/Images/</c>(오디오는 Audio/)로 확정 복사합니다.
    /// PromptSet 단위 스코프(<see cref="ScopeFromPromptSetPath"/>)를 지정하면 후보/확정본이
    /// <c>3_Candidates/{scope}/{id}/</c>·<c>3_Confirmed/{scope}/Images|Audio/</c>로 격리되어
    /// 문서마다 다시 시작하는 항목 id(item_001…)끼리 충돌하지 않습니다 (미지정 시 기존 위치 그대로).
    /// </summary>
    public static class CandidateGenerator
    {
        /// <summary>확정 결과 누적 문서 파일명입니다 (확정본 폴더 바로 아래).</summary>
        public const string ResultsFileName = "GenerationResults.json";

        /// <summary>
        /// 이 도구가 저장하는 GenerationResults 문서의 스키마 버전입니다. 저장 시 항상 이 값을 기록하며,
        /// 문서에 <c>schemaVersion</c> 키가 없으면 로드 시 이 버전(1)으로 간주합니다.
        /// 문서의 기존 키 의미가 바뀌는 변경을 할 때 함께 올립니다.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// 확정 결과 문서를 읽을 경로를 반환합니다. 새 위치(<c>3_Confirmed/</c>)를 우선하고,
        /// 없으면 하위 폴더 도입 이전 위치(생성 루트 바로 아래)를 사용합니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <returns>읽기에 사용할 경로 (Assets 기준 상대 경로).</returns>
        internal static string ResolveResultsPathForRead(MCPToolSettings settings)
        {
            return MCPToolFolders.ResolveForRead(
                $"{MCPToolFolders.ConfirmedRoot(settings)}/{ResultsFileName}",
                $"{MCPToolFolders.GeneratedRoot(settings)}/{ResultsFileName}");
        }

        /// <summary>
        /// PromptSet 문서 경로에서 저장 스코프 키를 만듭니다 (파일명에서 확장자를 뗀 뒤 금지 문자를 '_'로 치환).
        /// 항목 id(item_001…)는 문서마다 다시 시작하므로, 이 키로 후보/확정본 저장 위치를 PromptSet 단위로
        /// 격리해 서로 다른 문서의 같은 id끼리 충돌하지 않게 합니다.
        /// </summary>
        /// <param name="promptSetPath">PromptSet JSON 경로 (Assets/ 기준 상대 경로). null/빈 값 허용.</param>
        /// <returns>스코프 키 (예: "PromptSet_20260721_0512"). 경로가 비어 있으면 빈 문자열(스코프 없음).</returns>
        public static string ScopeFromPromptSetPath(string promptSetPath)
        {
            if (string.IsNullOrEmpty(promptSetPath))
            {
                return string.Empty;
            }

            return SanitizeScope(Path.GetFileNameWithoutExtension(promptSetPath.Replace('\\', '/')));
        }

        /// <summary>
        /// 스코프 키를 폴더 이름으로 쓸 수 있게 정규화합니다 (금지 문자 '_' 치환).
        /// 정규화 결과가 "." 만으로 이루어지면(".", ".." 등 상위 폴더 탈출 위험) 빈 스코프로 취급합니다.
        /// </summary>
        /// <param name="scope">스코프 키. null/빈 값 허용.</param>
        /// <returns>정규화된 스코프 키. 쓸 수 없는 값이면 빈 문자열.</returns>
        internal static string SanitizeScope(string scope)
        {
            if (string.IsNullOrEmpty(scope))
            {
                return string.Empty;
            }

            string sanitized = PreviewFileNameForId(scope);
            return sanitized.Trim('.').Length == 0 ? string.Empty : sanitized;
        }

        /// <summary>
        /// 항목의 후보 저장 폴더 경로를 반환합니다 (Assets/ 기준 상대 경로).
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID.</param>
        /// <param name="scope">
        /// PromptSet 단위 저장 스코프 키 (<see cref="ScopeFromPromptSetPath"/> 참조).
        /// null/빈 값이면 스코프 없이 기존 위치를 사용합니다 (기존 동작).
        /// </param>
        /// <returns>예: "Assets/Generated/3_Candidates/PromptSet_20260721_0512/item_001" (스코프 없으면 "3_Candidates/item_001").</returns>
        /// <remarks>
        /// 읽기 폴백 순서: 스코프 하위 폴더 → 스코프 없는 위치("3_Candidates/{id}") →
        /// 하위 폴더 도입 이전 위치("Candidates/{id}"). 어느 위치에도 폴더가 없으면
        /// 새로 만들 대상 경로(스코프 있으면 스코프 하위)를 돌려줍니다.
        /// </remarks>
        public static string GetCandidateFolder(MCPToolSettings settings, string assetItemId, string scope = null)
        {
            string id = SanitizeId(assetItemId);
            string root = MCPToolFolders.CandidatesRoot(settings);
            string legacy = $"{MCPToolFolders.GeneratedRoot(settings)}/{MCPToolFolders.LegacyCandidatesFolder}/{id}";

            string s = SanitizeScope(scope);
            if (s.Length > 0)
            {
                string scoped = $"{root}/{s}/{id}";
                if (Directory.Exists(scoped))
                {
                    return scoped;
                }

                // 하위 호환: 스코프 도입 이전에 만든 후보는 스코프 없는 위치에 있다. 읽기는 계속 폴백한다.
                string unscoped = $"{root}/{id}";
                if (Directory.Exists(unscoped))
                {
                    return unscoped;
                }

                return Directory.Exists(legacy) ? legacy : scoped;
            }

            string current = $"{root}/{id}";
            if (Directory.Exists(current))
            {
                return current;
            }

            return Directory.Exists(legacy) ? legacy : current;
        }

        /// <summary>
        /// 새 후보를 저장할 폴더를 결정합니다. 읽기(<see cref="GetCandidateFolder"/>)와 달리
        /// 스코프가 지정되면 구 위치에 폴더가 있어도 <b>항상 스코프 하위 폴더</b>를 씁니다 —
        /// 폴백을 따라가면 이어지는 ClearFolder가 다른 PromptSet의 후보를 지우기 때문입니다.
        /// </summary>
        internal static string GetCandidateWriteFolder(MCPToolSettings settings, string assetItemId, string scope)
        {
            string s = SanitizeScope(scope);
            if (s.Length == 0)
            {
                return GetCandidateFolder(settings, assetItemId);
            }

            return $"{MCPToolFolders.CandidatesRoot(settings)}/{s}/{SanitizeId(assetItemId)}";
        }

        /// <summary>
        /// 후보 파일 1개를 삭제합니다 (같은 시드의 메타 JSON과 .meta 포함).
        /// AssetDatabase 기준으로 지워 프로젝트 창에 바로 반영되며, 아직 임포트되지 않은 파일은
        /// 디스크에서 직접 지운 뒤 Refresh합니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID (오류 안내용).</param>
        /// <param name="candidatePath">삭제할 후보 파일 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>삭제한 후보 파일 경로 (구분자 '/').</returns>
        /// <exception cref="FileNotFoundException">후보 파일이 없는 경우.</exception>
        /// <exception cref="InvalidOperationException">후보 폴더 밖의 경로를 지운 경우 (안전장치).</exception>
        public static string DeleteCandidate(MCPToolSettings settings, string assetItemId, string candidatePath)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            candidatePath = (candidatePath ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrEmpty(candidatePath))
            {
                throw new ArgumentException("candidatePath가 비어 있습니다.", nameof(candidatePath));
            }

            if (!File.Exists(candidatePath))
            {
                throw new FileNotFoundException(
                    $"항목 \"{assetItemId}\"의 삭제할 후보 파일을 찾을 수 없습니다: \"{candidatePath}\". " +
                    "mcptools_list_candidates 또는 창의 후보 목록을 확인해주세요.",
                    candidatePath);
            }

            // 안전장치: 후보 폴더(신·구 위치) 밖의 파일은 지우지 않는다 — 잘못된 경로로 확정본·프로젝트
            // 에셋을 지우는 사고를 막는다.
            string candidatesRoot = $"{MCPToolFolders.CandidatesRoot(settings)}/";
            string legacyRoot =
                $"{MCPToolFolders.GeneratedRoot(settings)}/{MCPToolFolders.LegacyCandidatesFolder}/";
            bool escapesRoot = candidatePath.Contains("/../"); // "루트로 시작하지만 ..로 빠져나가는 경로" 차단
            if (escapesRoot ||
                (!candidatePath.StartsWith(candidatesRoot, StringComparison.OrdinalIgnoreCase) &&
                 !candidatePath.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"후보 폴더({candidatesRoot} 또는 {legacyRoot}) 밖의 파일은 삭제할 수 없습니다: \"{candidatePath}\"");
            }

            bool needsRefresh = DeleteFileAsset(candidatePath);

            // 같은 시드의 후보 메타 JSON도 함께 지운다 ({시드}.png ↔ {시드}.json).
            string folder = Path.GetDirectoryName(candidatePath);
            string metaJson =
                $"{(folder ?? string.Empty).Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(candidatePath)}.json";
            if (File.Exists(metaJson))
            {
                needsRefresh |= DeleteFileAsset(metaJson);
            }

            if (needsRefresh)
            {
                AssetDatabase.Refresh();
            }

            return candidatePath;
        }

        /// <summary>
        /// 파일 1개를 지웁니다. AssetDatabase가 아는 에셋이면 DeleteAsset(.meta 포함)으로,
        /// 아니면 File.Delete + .meta 직접 삭제로 지웁니다.
        /// </summary>
        /// <returns>File.Delete 폴백을 썼으면 true (호출자가 Refresh 필요).</returns>
        private static bool DeleteFileAsset(string path)
        {
            if (path.StartsWith("Assets/", StringComparison.Ordinal) && AssetDatabase.DeleteAsset(path))
            {
                return false;
            }

            File.Delete(path);
            string meta = path + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
            }

            return true;
        }

        /// <summary>
        /// 브리지 서버를 통해 후보를 생성합니다. 기존 후보는 삭제 후 새로 생성하며,
        /// 시드는 baseSeed(미지정 시 무작위)부터 candidateCount만큼 +1씩 증가시킵니다.
        /// 항목의 positive/negative 프롬프트는 워크플로 매니페스트의 role 정보로 자동 주입됩니다
        /// (variables에 같은 키가 있으면 사용자 값이 우선).
        /// </summary>
        /// <param name="settings">설정 객체 (브리지 주소·후보 개수·경로).</param>
        /// <param name="item">생성 대상 프롬프트 항목.</param>
        /// <param name="workflowName">사용할 워크플로 이름 (null이면 항목 종류별 기본 워크플로).</param>
        /// <param name="variables">덮어쓸 변수 맵 ("nodeId.field" → 값). null 허용.</param>
        /// <param name="baseSeed">기준 시드 (null이면 무작위).</param>
        /// <param name="interactive">
        /// 사전 검증 실패를 모달 다이얼로그로 안내할지 여부 (MCP 호출 시 false 권장).
        /// true면 다이얼로그로 안내한 뒤 <see cref="OperationCanceledException"/>을 던지고,
        /// false면 다이얼로그 없이 원인·조치가 담긴 <see cref="InvalidOperationException"/>만 던집니다.
        /// </param>
        /// <param name="refreshAssets">
        /// 생성 완료 후 <see cref="AssetDatabase.Refresh"/>를 호출해 후보 파일을 임포트할지 여부 (기본 true).
        /// 여러 항목을 연속 생성하는 호출자가 Refresh를 1회로 모으려면 false를 넘깁니다.
        /// <b>false로 넘긴 호출자는 반드시 나중에 직접 <see cref="AssetDatabase.Refresh"/>를 호출해야 합니다</b> —
        /// 그러지 않으면 반환된 경로의 후보 파일이 AssetDatabase에 등록되지 않아
        /// <see cref="ConfirmCandidate"/>의 임포트 설정이나 미리보기 로드가 실패합니다.
        /// </param>
        /// <param name="progress">0~1 전체 진행률 콜백.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <param name="scope">
        /// PromptSet 단위 저장 스코프 키 (<see cref="ScopeFromPromptSetPath"/> 참조).
        /// 지정하면 후보를 <c>3_Candidates/{scope}/{항목id}/</c>에 저장해 다른 PromptSet의 같은 id와
        /// 충돌하지 않습니다. null/빈 값이면 기존 위치(<c>3_Candidates/{항목id}/</c>)를 사용합니다.
        /// </param>
        /// <returns>생성된 후보 목록 (경로 + 시드).</returns>
        /// <exception cref="InvalidOperationException">
        /// 브리지/ComfyUI 미기동, 워크플로 거부 등 생성 실패 시. interactive=false에서는 사전 검증 실패도 포함합니다.
        /// </exception>
        /// <exception cref="OperationCanceledException">취소된 경우 (interactive=true에서는 사전 검증 실패 포함).</exception>
        public static async Task<List<CandidateInfo>> GenerateAsync(
            MCPToolSettings settings, PromptItem item, string workflowName,
            Dictionary<string, object> variables, long? baseSeed, bool interactive = false,
            bool refreshAssets = true,
            IProgress<float> progress = null, CancellationToken ct = default, string scope = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (item == null || string.IsNullOrEmpty(item.id))
            {
                throw new ArgumentException("생성할 프롬프트 항목(id 포함)이 필요합니다.", nameof(item));
            }

            string workflow = string.IsNullOrEmpty(workflowName) ? DefaultWorkflowFor(settings, item) : workflowName;
            int count = Mathf.Max(1, settings.candidateCount);

            // 쓰기 위치: 스코프가 있으면 항상 스코프 하위 폴더 (구 위치 폴백을 따라가면
            // 이어지는 ClearFolder가 다른 PromptSet의 후보를 지운다).
            string folder = GetCandidateWriteFolder(settings, item.id, scope);

            var results = new List<CandidateInfo>();

            using (var client = new BridgeClient(settings))
            {
                BridgeHealth health = await client.GetHealthAsync(ct);
                if (!health.ok)
                {
                    throw new InvalidOperationException(
                        $"브리지 서버({settings.bridgeServerUrl})가 응답하지 않습니다. " +
                        "ComfyUI Generator 창의 [서버 시작] 버튼으로 브리지 서버를 시작해주세요. (Python 3 필요)");
                }

                if (!health.comfyAlive)
                {
                    throw new InvalidOperationException(
                        $"ComfyUI 서버({health.comfyUrl})가 응답하지 않습니다. ComfyUI를 먼저 실행해주세요.");
                }

                // 프롬프트 자동 주입: 매니페스트의 role(positive/negative) 필드에 항목 프롬프트를 채운다.
                var mergedVariables = new Dictionary<string, object>(variables ?? new Dictionary<string, object>());
                List<BridgeWorkflowInfo> workflows = (await client.GetWorkflowsAsync(ct)).workflows;
                BridgeWorkflowInfo info = workflows.FirstOrDefault(w => w.name == workflow);
                if (info == null)
                {
                    throw new InvalidOperationException(
                        $"브리지 서버에 워크플로 \"{workflow}\"가 없습니다. 사용 가능: " +
                        string.Join(", ", workflows.Select(w => w.name)));
                }

                foreach (BridgeVariable variable in info.variables)
                {
                    if (mergedVariables.ContainsKey(variable.Key))
                    {
                        continue;
                    }

                    if (variable.role == "positive" && !string.IsNullOrEmpty(item.positive))
                    {
                        mergedVariables[variable.Key] = item.positive;
                    }
                    else if (variable.role == "negative" && !string.IsNullOrEmpty(item.negative))
                    {
                        mergedVariables[variable.Key] = item.negative;
                    }
                }

                bool hasPositive = info.variables.Any(v =>
                    v.role == "positive" && mergedVariables.ContainsKey(v.Key));
                if (!hasPositive && string.IsNullOrEmpty(item.positive))
                {
                    throw new InvalidOperationException(
                        $"항목 \"{item.id}\"의 positive 프롬프트가 비어 있습니다. 2단계(Prompt Builder)에서 먼저 채워주세요.");
                }

                // D1/D2 사전 검증: 모델 파일명·커스텀 노드 누락을 제출 전에 확인한다.
                // ComfyUI 미연결(comfyReachable=false)이면 검증이 생략된 것이므로 그대로 통과시키고
                // 기존 연결 오류 경로에 맡긴다 (이중 안내 금지).
                await RunPreflightAsync(client, workflow, mergedVariables, interactive, ct);

                // 이어지는 ClearFolder가 다른 항목의 후보를 지우는 상황(파일명 충돌)이면 먼저 알린다.
                WarnIfCandidateFolderTakenByOtherItem(item.id, folder);

                ClearFolder(folder);
                Directory.CreateDirectory(folder);

                string jobId = await client.GenerateAsync(workflow, mergedVariables, count, baseSeed, ct);

                // 브리지 Job 폴링 (다운로드 몫으로 진행률 90%까지만 보고)
                //
                // 폴링 간격: 0.3초에서 시작해 상태가 그대로면 1.5배씩 늘리고 1.5초에서 멈춘다.
                // - 시작값 0.3초: 브리지의 완료 감지 간격(POLL_INTERVAL_SEC)과 같은 값이라
                //   두 단이 겹쳐도 실제 완료 → 결과 표시 지연이 최대 0.6초 수준으로 줄어든다
                //   (기존에는 브리지 1초 + Unity 1초로 최대 2초).
                // - 상한 1.5초: 모델 로드 등으로 생성이 수 분 걸릴 때 요청이 과하게 쌓이지
                //   않게 한다. 10분 생성 기준 약 400회로, 고정 0.3초(2,000회)의 1/5이다.
                // - 진행률이 오르면(이미지 1장 완료) 간격을 초기값으로 되돌린다. 남은 장도
                //   비슷한 시간에 끝날 가능성이 높으므로, 마지막 장의 완료를 늘어난 간격
                //   때문에 늦게 잡는 일을 막는다.
                const float pollInitialSeconds = 0.3f;
                const float pollMaxSeconds = 1.5f;
                const float pollBackoffFactor = 1.5f;

                float pollDelaySeconds = pollInitialSeconds;
                float lastReportedProgress = -1f;
                BridgeJobStatus job;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    job = await client.GetJobAsync(jobId, ct);
                    progress?.Report(Mathf.Clamp01(job.progress) * 0.9f);

                    if (job.status == "failed")
                    {
                        throw new InvalidOperationException($"후보 생성에 실패했습니다: {job.message}");
                    }

                    if (job.status == "completed")
                    {
                        break;
                    }

                    if (job.progress > lastReportedProgress)
                    {
                        lastReportedProgress = job.progress;
                        pollDelaySeconds = pollInitialSeconds;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(pollDelaySeconds), ct);
                    pollDelaySeconds = Mathf.Min(pollDelaySeconds * pollBackoffFactor, pollMaxSeconds);
                }

                // 브리지가 남긴 경고(예: 워크플로에 seed 필드 없음 → 중복 제거로 후보 1개)를 사용자에게 알린다.
                if (!string.IsNullOrEmpty(job.warning))
                {
                    Debug.LogWarning($"[MCPTools] {job.warning}");
                }

                if (job.results.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"브리지 서버가 출력 파일을 반환하지 않았습니다 (항목 {item.id}). " +
                        "워크플로의 SaveImage/SaveAudio 노드와 모델 로드 상태를 확인해주세요.");
                }

                // 시드별 첫 파일만 후보로 저장한다 (동일 시드의 부가 출력은 무시).
                var savedSeeds = new HashSet<long>();
                foreach (BridgeResultFile file in job.results)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!savedSeeds.Add(file.seed))
                    {
                        continue;
                    }

                    byte[] bytes = await client.DownloadAsync(file, settings.requestTimeoutSeconds, ct);

                    string ext = Path.GetExtension(file.filename);
                    if (string.IsNullOrEmpty(ext))
                    {
                        ext = item.assetType == "audio" ? ".flac" : ".png";
                    }

                    string candidatePath = $"{folder}/{file.seed}{ext}";
                    File.WriteAllBytes(candidatePath, bytes);
                    WriteCandidateMeta(folder, item, file.seed, workflow, candidatePath);

                    results.Add(new CandidateInfo { path = candidatePath, seed = file.seed });
                    progress?.Report(0.9f + 0.1f * results.Count / Mathf.Max(1, count));
                }
            }

            // refreshAssets=false면 임포트를 호출자에게 맡긴다 (여러 항목 연속 생성 시 Refresh 1회로 모으기 위함).
            if (refreshAssets)
            {
                AssetDatabase.Refresh();
            }

            return results.OrderBy(c => c.seed).ToList();
        }

        /// <summary>
        /// 항목의 후보 파일 목록을 디스크에서 조회합니다 (메타 JSON 제외, 시드 오름차순).
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID.</param>
        /// <param name="scope">
        /// PromptSet 단위 저장 스코프 키. 지정하면 스코프 하위 폴더를 우선 조회하고,
        /// 없으면 구 위치로 폴백합니다 (<see cref="GetCandidateFolder"/> 규칙). null이면 기존 동작.
        /// </param>
        /// <returns>후보 목록. 폴더가 없으면 빈 목록.</returns>
        public static List<CandidateInfo> ListCandidates(
            MCPToolSettings settings, string assetItemId, string scope = null)
        {
            var results = new List<CandidateInfo>();
            string folder = GetCandidateFolder(settings, assetItemId, scope);
            if (!Directory.Exists(folder))
            {
                return results;
            }

            foreach (string file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".json" || ext == ".meta")
                {
                    continue;
                }

                long seed;
                long.TryParse(Path.GetFileNameWithoutExtension(file), out seed);
                results.Add(new CandidateInfo { path = file.Replace('\\', '/'), seed = seed });
            }

            return results.OrderBy(c => c.seed).ToList();
        }

        /// <summary>
        /// GenerationResults.json의 확정 기록을 "항목 ID → 확정본 경로(outputPath)" 맵으로 반환합니다.
        /// 창의 항목 목록 상태 배지("확정됨")와 확정 결과 미리보기 표시에 사용합니다.
        /// 에디터/Unity 재시작 후에도 이 파일 기록으로 확정 상태가 복원됩니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="scope">
        /// PromptSet 단위 저장 스코프 키. <b>null이면 스코프 무관 전체 기록</b>을 반환합니다 (기존 동작 —
        /// 같은 id가 여러 스코프에 있으면 나중 기록이 이깁니다). 값을 주면(빈 문자열 포함) 그 스코프의
        /// 기록과 스코프 없이 남긴 구 기록만 반환합니다 (하위 호환: 스코프 도입 이전 확정도 계속 표시).
        /// </param>
        /// <returns>확정된 항목 ID → 확정본 경로(Assets/ 기준 상대 경로) 맵. 기록 파일이 없으면 빈 맵.</returns>
        public static Dictionary<string, string> GetConfirmedOutputPaths(
            MCPToolSettings settings, string scope = null)
        {
            var paths = new Dictionary<string, string>();
            if (settings == null)
            {
                return paths;
            }

            string scopeFilter = scope == null ? null : SanitizeScope(scope);

            // 문서 읽기·스키마 버전 경고는 LoadResultsDocument 한 곳에 모아 둔다 (읽는 쪽이 갈라지지 않게).
            Dictionary<string, object> doc = LoadResultsDocument(settings);
            if (doc.TryGetValue("results", out object listObj) && listObj is List<object> list)
            {
                foreach (object entry in list)
                {
                    var dict = entry as Dictionary<string, object>;
                    string id = dict != null ? GetMetaString(dict, "assetItemId") : string.Empty;
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    // 스코프 필터: 지정 스코프의 기록 + 스코프 없는(구/공용) 기록만 남긴다.
                    string entryScope = GetMetaString(dict, "scope");
                    if (scopeFilter != null && entryScope.Length > 0 && entryScope != scopeFilter)
                    {
                        continue;
                    }

                    paths[id] = GetMetaString(dict, "outputPath");
                }
            }

            return paths;
        }

        /// <summary>
        /// 선택한 후보를 확정합니다: 최종 폴더로 복사, 임포트 설정 적용(이미지 항목 Sprite,
        /// RawImage 대상은 Texture 유지), GenerationResults.json에 기록.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID.</param>
        /// <param name="candidatePath">확정할 후보 파일 경로 (Assets/ 기준 상대 경로).</param>
        /// <param name="scope">
        /// PromptSet 단위 저장 스코프 키 (<see cref="ScopeFromPromptSetPath"/> 참조).
        /// 지정하면 확정본이 <c>3_Confirmed/{scope}/Images|Audio/</c>에 저장되어 다른 PromptSet의
        /// 같은 id 확정본을 덮어쓰지 않습니다. null/빈 값이면 기존 위치(<c>3_Confirmed/Images|Audio/</c>).
        /// </param>
        /// <returns>확정본 경로 (Assets/ 기준 상대 경로).</returns>
        /// <exception cref="FileNotFoundException">후보 파일이 없는 경우.</exception>
        public static string ConfirmCandidate(
            MCPToolSettings settings, string assetItemId, string candidatePath, string scope = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            candidatePath = (candidatePath ?? string.Empty).Replace('\\', '/');
            if (!File.Exists(candidatePath))
            {
                throw new FileNotFoundException(
                    $"후보 파일을 찾을 수 없습니다: \"{candidatePath}\". mcptools_list_candidates 또는 창의 후보 목록을 확인해주세요.",
                    candidatePath);
            }

            Dictionary<string, object> meta = ReadCandidateMeta(settings, assetItemId, scope);

            string ext = Path.GetExtension(candidatePath).ToLowerInvariant();
            bool isAudio = ext == ".flac" || ext == ".wav" || ext == ".mp3" || ext == ".ogg";
            string s = SanitizeScope(scope);
            string root = MCPToolFolders.ConfirmedRoot(settings);
            string scopedRoot = s.Length == 0 ? root : $"{root}/{s}";
            string destFolder = isAudio ? $"{scopedRoot}/Audio" : $"{scopedRoot}/Images";
            Directory.CreateDirectory(destFolder);

            string destPath = $"{destFolder}/{SanitizeId(assetItemId)}{ext}";

            // 다른 항목의 확정본을 덮어쓰는 상황(파일명 충돌)이면 알린다. 확정 자체는 막지 않는다.
            WarnIfConfirmDestinationTaken(settings, assetItemId, destPath);

            File.Copy(candidatePath, destPath, true);
            AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);

            if (!isAudio)
            {
                ApplyImageImportSettings(settings, destPath, meta);
            }

            long seed;
            long.TryParse(Path.GetFileNameWithoutExtension(candidatePath), out seed);
            RecordResult(settings, assetItemId, candidatePath, destPath, seed, meta, s);

            AssetDatabase.Refresh();
            return destPath;
        }

        /// <summary>
        /// 생성 제출 전 브리지 /preflight 로 사전 검증합니다 (D1/D2).
        /// 커스텀 노드 누락 또는 choice 입력 값(모델 파일명·참조 이미지 등) 누락이 있으면 생성을 중단합니다.
        /// interactive=true(창)면 원인·조치를 다이얼로그로 안내한 뒤 OperationCanceledException을 던지고,
        /// interactive=false(MCP·파이프라인)면 다이얼로그 없이 같은 내용을 담은 InvalidOperationException을 던집니다.
        /// (비대화형에서 OperationCanceledException을 쓰면 호출부의 취소 처리와 섞여 "사용자 취소"로 오인된다.)
        /// ComfyUI 미연결(comfyReachable=false)이거나 preflight 자체를 수행할 수 없으면
        /// (구버전 브리지 등) 경고 없이 통과시켜 기존 오류 경로에 맡깁니다.
        /// </summary>
        private static async Task RunPreflightAsync(
            BridgeClient client, string workflow, Dictionary<string, object> variables,
            bool interactive, CancellationToken ct)
        {
            BridgePreflightResult preflight;
            try
            {
                preflight = await client.PreflightAsync(workflow, variables, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // 엔드포인트가 없는 구버전 브리지·일시적 통신 오류 등 → 검증만 건너뛴다.
                Debug.LogWarning($"[MCPTools] 생성 사전 검증(/preflight)을 수행하지 못해 건너뜁니다: {e.Message}");
                return;
            }

            if (!preflight.comfyReachable ||
                (preflight.missingNodes.Count == 0 && preflight.invalidInputs.Count == 0))
            {
                return;
            }

            string message = BuildPreflightFailureMessage(workflow, preflight);
            if (!interactive)
            {
                // MCP·파이프라인 경로: 모달을 띄우면 에디터가 잠기거나(도구) 메인 스레드 위반 예외로
                // 안내가 변질된다(파이프라인). 원인·조치를 담은 예외만 던져 Job이 failed가 되게 한다.
                throw new InvalidOperationException(message);
            }

            EditorUtility.DisplayDialog("생성 사전 검증 실패", message, "확인");
            throw new OperationCanceledException(message);
        }

        /// <summary>사전 검증 실패 안내 메시지를 만듭니다 (누락 노드/필드/값 + 설치된 파일 예시 + 조치).</summary>
        private static string BuildPreflightFailureMessage(string workflow, BridgePreflightResult preflight)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"워크플로 \"{workflow}\"를 현재 ComfyUI 환경에서 실행할 수 없어 생성을 중단했습니다.");

            if (preflight.missingNodes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"■ 설치되지 않은 커스텀 노드 {preflight.missingNodes.Count}개");
                foreach (string node in preflight.missingNodes)
                {
                    sb.AppendLine($"  - {node}");
                }

                sb.AppendLine("  → ComfyUI-Manager에서 위 노드를 설치한 뒤 ComfyUI를 재시작해주세요.");
            }

            if (preflight.invalidInputs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"■ ComfyUI에 없는 파일/값 {preflight.invalidInputs.Count}건");
                foreach (BridgePreflightInvalidInput input in preflight.invalidInputs)
                {
                    sb.AppendLine($"  - 노드 #{input.node} ({input.classType}) {input.field} = \"{input.value}\"");
                    if (input.availableCount > 0)
                    {
                        int remaining = input.availableCount - input.availableSample.Count;
                        sb.AppendLine($"    설치된 값 예시: {string.Join(", ", input.availableSample)}" +
                                      (remaining > 0 ? $" 외 {remaining}개" : string.Empty));
                    }
                    else
                    {
                        sb.AppendLine("    설치된 값이 없습니다 (해당 종류의 파일/모델이 하나도 없음).");
                    }
                }

                sb.AppendLine("  → 3단계 창의 변수 UI에서 설치된 파일 목록(드롭다운)에서 선택하거나 파일명을 교체해주세요.");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>2단계 항목의 기본 워크플로 이름을 결정합니다 (오디오→Audio, UI→UI, 그 외 설정 기본값).</summary>
        private static string DefaultWorkflowFor(MCPToolSettings settings, PromptItem item)
        {
            if (item.assetType == "audio")
            {
                return "Audio";
            }

            if (item.isUI || item.assetType == "ui")
            {
                return "UI";
            }

            return string.IsNullOrEmpty(settings.defaultImageWorkflow) ? "GenerateImage" : settings.defaultImageWorkflow;
        }

        /// <summary>확정 이미지에 TextureImporter 설정을 적용합니다 (RawImage 대상만 Texture 유지).</summary>
        private static void ApplyImageImportSettings(
            MCPToolSettings settings, string assetPath, Dictionary<string, object> meta)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            bool rawImageTarget = IsRawImageTarget(meta);
            if (rawImageTarget)
            {
                importer.textureType = TextureImporterType.Default;
            }
            else
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = Mathf.Max(1, settings.spritePixelsPerUnit);
            }

            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// 항목의 적용 대상이 RawImage 컴포넌트인지 판정합니다.
        /// 후보 메타에 기록된 targetPrefabPath/targetObjectPath로 프리팹을 열어 확인합니다.
        /// </summary>
        private static bool IsRawImageTarget(Dictionary<string, object> meta)
        {
            if (meta == null)
            {
                return false;
            }

            string prefabPath = GetMetaString(meta, "targetPrefabPath");
            if (string.IsNullOrEmpty(prefabPath))
            {
                return false;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                return false;
            }

            Transform target = prefab.transform;
            string objectPath = GetMetaString(meta, "targetObjectPath");
            if (!string.IsNullOrEmpty(objectPath))
            {
                Transform found = prefab.transform.Find(objectPath);
                if (found == null)
                {
                    // 루트 이름이 경로에 포함된 형태(Root/Child/...)도 허용
                    int slash = objectPath.IndexOf('/');
                    if (slash > 0 && objectPath.Substring(0, slash) == prefab.name)
                    {
                        found = prefab.transform.Find(objectPath.Substring(slash + 1));
                    }
                }

                if (found == null)
                {
                    return false;
                }

                target = found;
            }

            // uGUI 어셈블리 참조 없이 타입 이름으로 판정한다.
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component != null && component.GetType().Name == "RawImage")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>후보 폴더의 공용 메타 JSON을 기록합니다 (항목·프롬프트·워크플로·대상 정보).</summary>
        private static void WriteCandidateMeta(
            string folder, PromptItem item, long seed, string workflowName, string candidatePath)
        {
            var meta = new Dictionary<string, object>
            {
                { "assetItemId", item.id },
                { "name", item.name },
                { "assetType", item.assetType },
                { "isUI", item.isUI },
                { "targetPrefabPath", item.targetPrefabPath },
                { "targetObjectPath", item.targetObjectPath },
                { "positive", item.positive },
                { "negative", item.negative },
                { "workflow", workflowName },
                { "seed", seed },
                { "file", candidatePath },
                { "createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            File.WriteAllText($"{folder}/{seed}.json", MiniJson.Serialize(meta));
        }

        /// <summary>후보 폴더에서 아무 메타 JSON 하나를 읽습니다 (항목 공통 필드 사용 목적).</summary>
        private static Dictionary<string, object> ReadCandidateMeta(
            MCPToolSettings settings, string assetItemId, string scope = null)
        {
            return ReadCandidateMetaInFolder(GetCandidateFolder(settings, assetItemId, scope));
        }

        /// <summary>지정한 후보 폴더에서 아무 메타 JSON 하나를 읽습니다.</summary>
        private static Dictionary<string, object> ReadCandidateMetaInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                var meta = MiniJson.Deserialize(File.ReadAllText(file)) as Dictionary<string, object>;
                if (meta != null)
                {
                    return meta;
                }
            }

            return null;
        }

        /// <summary>
        /// 확정 결과를 GenerationResults.json에 누적 기록합니다 (같은 항목·같은 스코프는 갱신).
        /// 스코프가 다른 같은 id의 기록은 서로 다른 확정본이므로 그대로 보존합니다.
        /// </summary>
        private static void RecordResult(
            MCPToolSettings settings, string assetItemId, string candidatePath, string outputPath,
            long seed, Dictionary<string, object> meta, string scope)
        {
            scope = scope ?? string.Empty;

            // 이 문서에는 4단계 적용 이력(applications, ApplyHistory)이 형제 키로 함께 들어 있다.
            // 문서 전체를 읽어 results만 갈아끼우고 나머지 키는 그대로 다시 써야 이력이 날아가지 않는다.
            Dictionary<string, object> doc = LoadResultsDocument(settings);

            var results = new List<object>();
            if (doc.TryGetValue("results", out object listObj) && listObj is List<object> list)
            {
                foreach (object entry in list)
                {
                    var dict = entry as Dictionary<string, object>;
                    if (dict == null ||
                        (GetMetaString(dict, "assetItemId") == assetItemId &&
                         GetMetaString(dict, "scope") == scope))
                    {
                        continue; // 같은 항목·같은 스코프의 기존 기록은 새 기록으로 대체
                    }

                    results.Add(dict);
                }
            }

            results.Add(new Dictionary<string, object>
            {
                { "assetItemId", assetItemId },
                { "scope", scope },
                { "sourceCandidate", candidatePath },
                { "outputPath", outputPath },
                { "seed", seed },
                { "workflow", meta != null ? GetMetaString(meta, "workflow") : string.Empty },
                { "targetPrefabPath", meta != null ? GetMetaString(meta, "targetPrefabPath") : string.Empty },
                { "targetObjectPath", meta != null ? GetMetaString(meta, "targetObjectPath") : string.Empty },
                { "confirmedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
            });

            doc["results"] = results;
            SaveResultsDocument(settings, doc);
        }

        /// <summary>
        /// 확정 결과 문서(<see cref="ResultsFileName"/>)를 통째로 읽습니다.
        /// 이 문서에는 3단계 확정 기록(<c>results</c>)과 4단계 적용 이력(<c>applications</c>,
        /// <see cref="ApplyHistory"/>)이 형제 키로 함께 들어가므로, 한쪽을 갱신할 때도 문서 전체를 읽어
        /// 나머지 키를 그대로 보존한 뒤 <see cref="SaveResultsDocument"/>로 다시 써야 합니다.
        /// 새 위치(<c>3_Confirmed/</c>)에 문서가 없으면 구 위치의 내용을 이어받습니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <returns>
        /// 문서 내용. 파일이 없거나 내용이 깨져 있으면 빈 사전을 반환하며 예외를 던지지 않습니다
        /// (기록 때문에 확정·적용 자체가 실패하지 않게 하기 위함).
        /// </returns>
        internal static Dictionary<string, object> LoadResultsDocument(MCPToolSettings settings)
        {
            var empty = new Dictionary<string, object>();
            if (settings == null)
            {
                return empty;
            }

            string readPath = ResolveResultsPathForRead(settings);
            if (!File.Exists(readPath))
            {
                return empty;
            }

            try
            {
                var doc = MiniJson.Deserialize(File.ReadAllText(readPath)) as Dictionary<string, object>;
                if (doc == null)
                {
                    Debug.LogWarning(
                        $"[MCPTools] {readPath} 를 JSON 문서로 읽지 못해 빈 문서로 진행합니다. " +
                        "파일이 손상되었다면 이전 확정·적용 기록이 다음 저장 때 사라질 수 있습니다.");
                    return empty;
                }

                WarnIfNewerSchemaVersion(doc);
                return doc;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[MCPTools] {readPath} 를 읽지 못해 빈 문서로 진행합니다: {e.Message}");
                return empty;
            }
        }

        /// <summary>
        /// 확정 결과 문서를 새 위치(<c>3_Confirmed/</c>)에 저장합니다.
        /// <c>schemaVersion</c>은 읽어 온 문서의 값과 <see cref="CurrentSchemaVersion"/> 중 <b>큰 값</b>을 기록합니다.
        /// (더 새 버전이 저장한 문서를 열어 일부만 갱신한 경우, 나머지 레코드는 원본 그대로 보존되므로
        /// 문서를 낮은 버전으로 되돌려 라벨링하면 사실과 어긋납니다.)
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="doc"><see cref="LoadResultsDocument"/>로 읽어 일부 키만 갱신한 문서.</param>
        internal static void SaveResultsDocument(MCPToolSettings settings, Dictionary<string, object> doc)
        {
            if (settings == null || doc == null)
            {
                throw new ArgumentNullException(settings == null ? nameof(settings) : nameof(doc));
            }

            string root = MCPToolFolders.ConfirmedRoot(settings);
            Directory.CreateDirectory(root);

            doc["schemaVersion"] = Math.Max(ReadSchemaVersion(doc), CurrentSchemaVersion);
            File.WriteAllText($"{root}/{ResultsFileName}", MiniJson.Serialize(doc));
        }

        /// <summary>
        /// 문서의 <c>schemaVersion</c> 값을 읽습니다. 키가 없거나 정수로 해석할 수 없으면
        /// <see cref="CurrentSchemaVersion"/>으로 간주합니다 (구 문서 = 버전 1).
        /// </summary>
        /// <param name="doc">확정 결과 문서.</param>
        /// <returns>문서의 스키마 버전.</returns>
        internal static long ReadSchemaVersion(Dictionary<string, object> doc)
        {
            object value;
            if (doc == null || !doc.TryGetValue("schemaVersion", out value) || value == null)
            {
                // 구 문서(키 없음) → 버전 1로 간주하고 기존과 동일하게 읽는다.
                return CurrentSchemaVersion;
            }

            // MiniJson은 JSON 정수를 long, 실수를 double로 돌려준다. 손수 작성한 문서를 위해 문자열도 받아준다.
            if (value is long asLong)
            {
                return asLong;
            }

            if (value is int asInt)
            {
                return asInt;
            }

            if (value is double asDouble)
            {
                return (long)asDouble;
            }

            long parsed;
            return long.TryParse(
                value as string ?? string.Empty,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed)
                ? parsed
                : CurrentSchemaVersion; // 정수로 해석할 수 없는 값 → 버전 정보가 없는 것으로 본다.
        }

        /// <summary>
        /// GenerationResults 문서의 <c>schemaVersion</c>이 이 도구가 아는 버전보다 크면 경고만 남깁니다
        /// (읽기를 막지 않습니다). 키가 없거나 정수로 해석할 수 없는 값이면
        /// <see cref="CurrentSchemaVersion"/>으로 간주하고 조용히 진행합니다.
        /// </summary>
        private static void WarnIfNewerSchemaVersion(Dictionary<string, object> doc)
        {
            long version = ReadSchemaVersion(doc);
            if (version > CurrentSchemaVersion)
            {
                Debug.LogWarning(
                    $"[MCPTools] {ResultsFileName} 문서의 schemaVersion이 {version}입니다 " +
                    $"(이 도구가 아는 최신 버전: {CurrentSchemaVersion}). " +
                    "더 새 버전의 MCPTools가 저장한 문서일 수 있어 일부 값이 다르게 해석될 수 있습니다. " +
                    "읽기는 그대로 계속하며, 결과가 이상하면 MCPTools를 최신 버전으로 업데이트해주세요.");
            }
        }

        /// <summary>후보 폴더의 파일을 전부 삭제합니다 (.meta 포함, 폴더 자체는 유지/재사용).</summary>
        private static void ClearFolder(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                File.Delete(file);
            }
        }

        private static string SanitizeId(string assetItemId)
        {
            if (string.IsNullOrEmpty(assetItemId))
            {
                throw new ArgumentException("assetItemId가 비어 있습니다.", nameof(assetItemId));
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                assetItemId = assetItemId.Replace(c, '_');
            }

            return assetItemId;
        }

        /// <summary>
        /// 항목 id가 후보 폴더명·확정본 파일명으로 바뀐 결과를 미리 계산합니다 (충돌 검사 전용, 파일을 만들지 않습니다).
        /// </summary>
        /// <param name="assetItemId">항목 ID.</param>
        /// <returns>파일명으로 쓸 수 없는 문자를 '_'로 바꾼 문자열. id가 비어 있으면 빈 문자열.</returns>
        /// <remarks>
        /// 실제 저장에 쓰는 규칙은 실행 중인 OS의 <see cref="Path.GetInvalidFileNameChars"/>를 따르지만,
        /// 1단계 산출물 문서는 다른 OS에서도 열리므로 여기서는 <b>현재 OS 기준 금지 문자 ∪ Windows 기준 금지 문자</b>
        /// 로 판정한다. 어느 OS에서 실행하더라도 실제로 일어날 충돌을 놓치지 않기 위함이며,
        /// 반대로 Unix에서만 쓰는 프로젝트에서는 실제로는 충돌하지 않는 조합까지 경고할 수 있다
        /// (경고만 남기고 저장·생성을 막지 않으므로 그쪽을 택한다).
        /// </remarks>
        internal static string PreviewFileNameForId(string assetItemId)
        {
            if (string.IsNullOrEmpty(assetItemId))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(assetItemId.Length);
            foreach (char c in assetItemId)
            {
                sb.Append(IsInvalidFileNameChar(c) ? '_' : c);
            }

            return sb.ToString();
        }

        /// <summary>파일명에 쓸 수 없는 문자인지 판정합니다 (현재 OS 기준 ∪ Windows 기준).</summary>
        private static bool IsInvalidFileNameChar(char c)
        {
            if (c < ' ')
            {
                return true; // 제어 문자
            }

            if ("\"<>|:*?\\/".IndexOf(c) >= 0)
            {
                return true; // Windows 기준 금지 문자 (Unix에서는 '/'만 금지)
            }

            return Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0;
        }

        /// <summary>
        /// 후보 폴더에 <b>다른 항목</b>의 후보가 들어 있으면 경고합니다 (생성은 그대로 진행).
        /// 서로 다른 항목 id가 <see cref="SanitizeId"/> 결과로 같은 폴더명이 되면
        /// 이어지는 <see cref="ClearFolder"/>가 다른 항목의 후보를 지우기 때문입니다.
        /// 폴더의 후보 메타 JSON에 기록된 원본 assetItemId로만 판정하므로,
        /// 메타가 없는 구 후보 폴더에서는 조용히 넘어갑니다.
        /// </summary>
        private static void WarnIfCandidateFolderTakenByOtherItem(string assetItemId, string folder)
        {
            Dictionary<string, object> meta = ReadCandidateMetaInFolder(folder);
            string owner = meta != null ? GetMetaString(meta, "assetItemId") : string.Empty;
            if (string.IsNullOrEmpty(owner) || owner == assetItemId)
            {
                return;
            }

            Debug.LogWarning(
                $"[MCPTools] 항목 \"{assetItemId}\"의 후보 폴더가 항목 \"{owner}\"의 후보 폴더와 같습니다 " +
                $"(\"{folder}\"). 두 id가 파일명으로 쓸 수 없는 문자 때문에 같은 이름으로 바뀝니다. " +
                $"이번 생성으로 항목 \"{owner}\"의 기존 후보가 지워집니다. " +
                "1단계 목록에서 id를 파일명에 쓸 수 있는 문자(영문·숫자·_·-)로 구분해주세요.");
        }

        /// <summary>
        /// 확정본 저장 경로에 <b>다른 항목</b>의 파일이 이미 있으면 경고합니다 (확정은 그대로 진행).
        /// 같은 항목의 재확정(덮어쓰기)은 정상 동작이므로 경고하지 않습니다.
        /// 소유 항목 판정은 <see cref="GetConfirmedOutputPaths"/>(GenerationResults.json 기록)로 합니다.
        /// </summary>
        private static void WarnIfConfirmDestinationTaken(
            MCPToolSettings settings, string assetItemId, string destPath)
        {
            if (!File.Exists(destPath))
            {
                return;
            }

            Dictionary<string, string> confirmed = GetConfirmedOutputPaths(settings);

            string ownPath;
            if (confirmed.TryGetValue(assetItemId, out ownPath) && IsSamePath(ownPath, destPath))
            {
                return; // 같은 항목의 재확정 — 정상 동작
            }

            var owners = new List<string>();
            foreach (KeyValuePair<string, string> pair in confirmed)
            {
                if (pair.Key != assetItemId && IsSamePath(pair.Value, destPath))
                {
                    owners.Add(pair.Key);
                }
            }

            if (owners.Count > 0)
            {
                string ownerList = string.Join("\", \"", owners.ToArray());
                Debug.LogWarning(
                    $"[MCPTools] 항목 \"{assetItemId}\"의 확정본이 항목 \"{ownerList}\"의 " +
                    $"확정본과 같은 경로(\"{destPath}\")에 저장되어 덮어씁니다. " +
                    "두 id가 파일명으로 쓸 수 없는 문자 때문에 같은 이름으로 바뀝니다. " +
                    "1단계 목록에서 id를 파일명에 쓸 수 있는 문자(영문·숫자·_·-)로 구분한 뒤 다시 확정해주세요.");
                return;
            }

            Debug.LogWarning(
                $"[MCPTools] 항목 \"{assetItemId}\"의 확정본 경로(\"{destPath}\")에 확정 기록이 없는 파일이 이미 있어 덮어씁니다. " +
                $"다른 항목이 만든 파일이거나 {ResultsFileName} 기록이 지워진 경우일 수 있습니다. " +
                "덮어쓰면 안 되는 파일이었다면 백업 후 항목 id를 바꿔 다시 확정해주세요.");
        }

        /// <summary>
        /// 두 에셋 경로가 같은 파일을 가리키는지 비교합니다 (구분자 통일 + 대소문자 무시).
        /// Windows 파일 시스템 기준으로 대소문자를 무시하므로, 대소문자만 다른 id는 같은 파일로 봅니다.
        /// </summary>
        private static bool IsSamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            return string.Equals(
                a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMetaString(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) && value is string s ? s : string.Empty;
        }
    }
}
