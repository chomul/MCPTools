using System;
using System.Collections.Generic;
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
    /// </summary>
    public static class CandidateGenerator
    {
        /// <summary>확정 결과 누적 문서 파일명입니다 (확정본 폴더 바로 아래).</summary>
        public const string ResultsFileName = "GenerationResults.json";

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
        /// 항목의 후보 저장 폴더 경로를 반환합니다 (Assets/ 기준 상대 경로).
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID.</param>
        /// <returns>예: "Assets/Generated/3_Candidates/item_001".</returns>
        /// <remarks>
        /// 하위 폴더 도입 이전에 만들어진 후보는 "Generated/Candidates/{id}"에 있다.
        /// 새 위치에 폴더가 없고 구 위치에만 있으면 구 위치를 돌려줘 기존 후보를 계속 볼 수 있게 한다.
        /// </remarks>
        public static string GetCandidateFolder(MCPToolSettings settings, string assetItemId)
        {
            string id = SanitizeId(assetItemId);
            string current = $"{MCPToolFolders.CandidatesRoot(settings)}/{id}";
            if (Directory.Exists(current))
            {
                return current;
            }

            string legacy = $"{MCPToolFolders.GeneratedRoot(settings)}/{MCPToolFolders.LegacyCandidatesFolder}/{id}";
            return Directory.Exists(legacy) ? legacy : current;
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
        /// <param name="progress">0~1 전체 진행률 콜백.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>생성된 후보 목록 (경로 + 시드).</returns>
        /// <exception cref="InvalidOperationException">브리지/ComfyUI 미기동, 워크플로 거부 등 생성 실패 시.</exception>
        /// <exception cref="OperationCanceledException">취소된 경우.</exception>
        public static async Task<List<CandidateInfo>> GenerateAsync(
            MCPToolSettings settings, PromptItem item, string workflowName,
            Dictionary<string, object> variables, long? baseSeed,
            IProgress<float> progress = null, CancellationToken ct = default)
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

            string folder = GetCandidateFolder(settings, item.id);

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
                await RunPreflightAsync(client, workflow, mergedVariables, ct);

                ClearFolder(folder);
                Directory.CreateDirectory(folder);

                string jobId = await client.GenerateAsync(workflow, mergedVariables, count, baseSeed, ct);

                // 브리지 Job 폴링 (다운로드 몫으로 진행률 90%까지만 보고)
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

                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
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

            AssetDatabase.Refresh();
            return results.OrderBy(c => c.seed).ToList();
        }

        /// <summary>
        /// 항목의 후보 파일 목록을 디스크에서 조회합니다 (메타 JSON 제외, 시드 오름차순).
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="assetItemId">항목 ID.</param>
        /// <returns>후보 목록. 폴더가 없으면 빈 목록.</returns>
        public static List<CandidateInfo> ListCandidates(MCPToolSettings settings, string assetItemId)
        {
            var results = new List<CandidateInfo>();
            string folder = GetCandidateFolder(settings, assetItemId);
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
        /// <returns>확정된 항목 ID → 확정본 경로(Assets/ 기준 상대 경로) 맵. 기록 파일이 없으면 빈 맵.</returns>
        public static Dictionary<string, string> GetConfirmedOutputPaths(MCPToolSettings settings)
        {
            var paths = new Dictionary<string, string>();
            if (settings == null)
            {
                return paths;
            }

            string resultsPath = ResolveResultsPathForRead(settings);
            if (!File.Exists(resultsPath))
            {
                return paths;
            }

            var doc = MiniJson.Deserialize(File.ReadAllText(resultsPath)) as Dictionary<string, object>;
            if (doc != null && doc.TryGetValue("results", out object listObj) && listObj is List<object> list)
            {
                foreach (object entry in list)
                {
                    var dict = entry as Dictionary<string, object>;
                    string id = dict != null ? GetMetaString(dict, "assetItemId") : string.Empty;
                    if (!string.IsNullOrEmpty(id))
                    {
                        paths[id] = GetMetaString(dict, "outputPath");
                    }
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
        /// <returns>확정본 경로 (Assets/ 기준 상대 경로).</returns>
        /// <exception cref="FileNotFoundException">후보 파일이 없는 경우.</exception>
        public static string ConfirmCandidate(MCPToolSettings settings, string assetItemId, string candidatePath)
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

            Dictionary<string, object> meta = ReadCandidateMeta(settings, assetItemId);

            string ext = Path.GetExtension(candidatePath).ToLowerInvariant();
            bool isAudio = ext == ".flac" || ext == ".wav" || ext == ".mp3" || ext == ".ogg";
            string root = MCPToolFolders.ConfirmedRoot(settings);
            string destFolder = isAudio ? $"{root}/Audio" : $"{root}/Images";
            Directory.CreateDirectory(destFolder);

            string destPath = $"{destFolder}/{SanitizeId(assetItemId)}{ext}";
            File.Copy(candidatePath, destPath, true);
            AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);

            if (!isAudio)
            {
                ApplyImageImportSettings(settings, destPath, meta);
            }

            long seed;
            long.TryParse(Path.GetFileNameWithoutExtension(candidatePath), out seed);
            RecordResult(settings, assetItemId, candidatePath, destPath, seed, meta);

            AssetDatabase.Refresh();
            return destPath;
        }

        /// <summary>
        /// 생성 제출 전 브리지 /preflight 로 사전 검증합니다 (D1/D2).
        /// 커스텀 노드 누락 또는 choice 입력 값(모델 파일명·참조 이미지 등) 누락이 있으면
        /// 원인·조치를 다이얼로그로 안내하고 생성을 중단(OperationCanceledException)합니다.
        /// ComfyUI 미연결(comfyReachable=false)이거나 preflight 자체를 수행할 수 없으면
        /// (구버전 브리지 등) 경고 없이 통과시켜 기존 오류 경로에 맡깁니다.
        /// </summary>
        private static async Task RunPreflightAsync(
            BridgeClient client, string workflow, Dictionary<string, object> variables, CancellationToken ct)
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
        private static Dictionary<string, object> ReadCandidateMeta(MCPToolSettings settings, string assetItemId)
        {
            string folder = GetCandidateFolder(settings, assetItemId);
            if (!Directory.Exists(folder))
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

        /// <summary>확정 결과를 GenerationResults.json에 누적 기록합니다 (같은 항목은 갱신).</summary>
        private static void RecordResult(
            MCPToolSettings settings, string assetItemId, string candidatePath, string outputPath,
            long seed, Dictionary<string, object> meta)
        {
            string root = MCPToolFolders.ConfirmedRoot(settings);
            Directory.CreateDirectory(root);
            string resultsPath = $"{root}/{ResultsFileName}";

            // 새 위치에 아직 문서가 없으면 구 위치의 내용을 이어받아 기록이 끊기지 않게 한다.
            // (이후 저장은 새 위치로만 이뤄지고, 읽기도 새 위치를 우선한다.)
            string readPath = ResolveResultsPathForRead(settings);

            var results = new List<object>();
            if (File.Exists(readPath))
            {
                var doc = MiniJson.Deserialize(File.ReadAllText(readPath)) as Dictionary<string, object>;
                if (doc != null && doc.TryGetValue("results", out object listObj) && listObj is List<object> list)
                {
                    foreach (object entry in list)
                    {
                        var dict = entry as Dictionary<string, object>;
                        if (dict == null || GetMetaString(dict, "assetItemId") == assetItemId)
                        {
                            continue; // 같은 항목의 기존 기록은 새 기록으로 대체
                        }

                        results.Add(dict);
                    }
                }
            }

            results.Add(new Dictionary<string, object>
            {
                { "assetItemId", assetItemId },
                { "sourceCandidate", candidatePath },
                { "outputPath", outputPath },
                { "seed", seed },
                { "workflow", meta != null ? GetMetaString(meta, "workflow") : string.Empty },
                { "targetPrefabPath", meta != null ? GetMetaString(meta, "targetPrefabPath") : string.Empty },
                { "targetObjectPath", meta != null ? GetMetaString(meta, "targetObjectPath") : string.Empty },
                { "confirmedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
            });

            File.WriteAllText(resultsPath, MiniJson.Serialize(new Dictionary<string, object> { { "results", results } }));
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

        private static string GetMetaString(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) && value is string s ? s : string.Empty;
        }
    }
}
