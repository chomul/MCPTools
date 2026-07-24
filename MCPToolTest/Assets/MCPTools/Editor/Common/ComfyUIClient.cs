using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MCPTools.Editor
{
    /// <summary>
    /// ComfyUI 로컬 서버 REST API 래퍼입니다. 모든 호출은 async/await 기반이며
    /// 동기 블로킹(.Result / .Wait())을 사용하지 않습니다.
    /// </summary>
    public sealed class ComfyUIClient : IDisposable
    {
        /// <summary>에디터 세션당 1개 발급되는 ComfyUI client_id입니다.</summary>
        private static readonly string SessionClientId = Guid.NewGuid().ToString("N");

        private readonly MCPToolSettings _settings;
        private readonly HttpClient _http;
        private readonly string _serverUrl;

        /// <summary>
        /// 설정을 기반으로 클라이언트를 생성합니다.
        /// </summary>
        /// <param name="settings">서버 주소·타임아웃이 담긴 설정 객체.</param>
        /// <exception cref="ArgumentNullException">settings가 null인 경우.</exception>
        /// <exception cref="InvalidOperationException">서버 주소가 올바른 URL 형식이 아닌 경우.</exception>
        public ComfyUIClient(MCPToolSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _serverUrl = (settings.comfyUIServerUrl ?? string.Empty).TrimEnd('/');

            Uri baseUri;
            if (!Uri.TryCreate(_serverUrl, UriKind.Absolute, out baseUri))
            {
                throw new InvalidOperationException(
                    $"ComfyUI 서버 주소가 올바르지 않습니다: \"{settings.comfyUIServerUrl}\". " +
                    "Tools/MCP/Settings에서 주소를 확인해주세요. (예: http://127.0.0.1:8188)");
            }

            _http = new HttpClient
            {
                BaseAddress = baseUri,
                // 타임아웃은 요청별 CancellationToken으로 처리합니다.
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        /// GET /system_stats 로 서버 생존 여부를 확인합니다.
        /// 연결 실패(HttpRequestException)는 예외를 전파하지 않고 false를 반환합니다.
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>서버가 200 OK로 응답하면 true.</returns>
        public async Task<bool> CheckServerAsync(CancellationToken ct = default)
        {
            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, 10))
                using (var response = await _http.GetAsync("/system_stats", timeoutCts.Token).ConfigureAwait(false))
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 내부 타임아웃에 의한 취소 → 서버 미응답으로 간주
                return false;
            }
        }

        /// <summary>
        /// POST /prompt 로 워크플로를 큐에 등록하고 prompt_id를 반환합니다.
        /// </summary>
        /// <param name="workflowJson">API Format 워크플로 JSON 문자열.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>ComfyUI가 발급한 prompt_id.</returns>
        /// <exception cref="InvalidOperationException">
        /// 워크플로 JSON 파싱 실패, HTTP 400(노드 검증 오류 — 응답 본문 포함), 연결 실패,
        /// 응답에서 prompt_id를 찾지 못한 경우.
        /// </exception>
        public async Task<string> QueuePromptAsync(string workflowJson, CancellationToken ct = default)
        {
            object workflow = MiniJson.Deserialize(workflowJson);
            if (!(workflow is Dictionary<string, object>))
            {
                throw new InvalidOperationException(
                    "워크플로 JSON을 파싱할 수 없습니다. API Format(노드 id → {class_type, inputs} 맵) JSON인지 확인해주세요.");
            }

            var body = new Dictionary<string, object>
            {
                { "prompt", workflow },
                { "client_id", SessionClientId }
            };
            string bodyJson = MiniJson.Serialize(body);

            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, _settings.requestTimeoutSeconds))
                using (var content = new StringContent(bodyJson, Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync("/prompt", content, timeoutCts.Token).ConfigureAwait(false))
                {
                    string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        throw new InvalidOperationException(
                            $"ComfyUI가 워크플로를 거부했습니다 (HTTP 400, 노드 검증 오류). 응답 본문: {responseText}");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"ComfyUI /prompt 요청이 실패했습니다 (HTTP {(int)response.StatusCode}). 응답 본문: {responseText}");
                    }

                    var responseDict = MiniJson.Deserialize(responseText) as Dictionary<string, object>;
                    object promptId;
                    if (responseDict == null ||
                        !responseDict.TryGetValue("prompt_id", out promptId) ||
                        string.IsNullOrEmpty(promptId as string))
                    {
                        throw new InvalidOperationException(
                            $"ComfyUI 응답에서 prompt_id를 찾을 수 없습니다. 응답 본문: {responseText}");
                    }

                    return (string)promptId;
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        /// <summary>
        /// GET /history/{promptId} 를 1초 간격으로 폴링하여 생성 완료를 기다립니다.
        /// </summary>
        /// <param name="promptId">QueuePromptAsync가 반환한 prompt_id.</param>
        /// <param name="progress">0~1 진행률 콜백 (경과 시간 기반 추정치).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>출력 파일 목록이 담긴 <see cref="ComfyUIHistoryEntry"/>.</returns>
        /// <exception cref="TimeoutException">settings.requestTimeoutSeconds 초과 시.</exception>
        /// <exception cref="InvalidOperationException">폴링 중 연결이 끊긴 경우.</exception>
        public async Task<ComfyUIHistoryEntry> WaitForCompletionAsync(
            string promptId, IProgress<float> progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(promptId))
            {
                throw new ArgumentException("promptId가 비어 있습니다.", nameof(promptId));
            }

            int timeoutSeconds = Math.Max(1, _settings.requestTimeoutSeconds);
            DateTime start = DateTime.UtcNow;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                if (elapsed > timeoutSeconds)
                {
                    throw new TimeoutException(
                        $"ComfyUI 생성 대기가 제한 시간({timeoutSeconds}초)을 초과했습니다. " +
                        "서버 부하 상태를 확인하거나 Tools/MCP/Settings에서 타임아웃을 늘려주세요.");
                }

                progress?.Report(Math.Min((float)(elapsed / timeoutSeconds), 0.95f));

                Dictionary<string, object> history;
                try
                {
                    using (var response = await _http.GetAsync("/history/" + Uri.EscapeDataString(promptId), ct)
                        .ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        history = MiniJson.Deserialize(text) as Dictionary<string, object>;
                    }
                }
                catch (HttpRequestException e)
                {
                    throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
                }

                object entryObj;
                if (history != null && history.TryGetValue(promptId, out entryObj) &&
                    entryObj is Dictionary<string, object> entryDict)
                {
                    bool completed = false;

                    object statusObj;
                    if (entryDict.TryGetValue("status", out statusObj) &&
                        statusObj is Dictionary<string, object> statusDict)
                    {
                        object completedObj;
                        if (statusDict.TryGetValue("completed", out completedObj) && completedObj is bool b && b)
                        {
                            completed = true;
                        }
                    }

                    var outputs = CollectOutputs(entryDict);
                    if (completed || outputs.Count > 0)
                    {
                        progress?.Report(1f);
                        return new ComfyUIHistoryEntry { outputs = outputs };
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// GET /view 로 생성 결과 파일을 다운로드합니다.
        /// </summary>
        /// <param name="filename">출력 파일명.</param>
        /// <param name="subfolder">출력 하위 폴더 (없으면 빈 문자열).</param>
        /// <param name="type">출력 종류 (일반적으로 "output").</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>파일 바이트 배열.</returns>
        /// <exception cref="InvalidOperationException">연결 실패 또는 HTTP 오류 시.</exception>
        public async Task<byte[]> DownloadOutputAsync(
            string filename, string subfolder, string type, CancellationToken ct = default)
        {
            string url = "/view?filename=" + Uri.EscapeDataString(filename ?? string.Empty) +
                         "&subfolder=" + Uri.EscapeDataString(subfolder ?? string.Empty) +
                         "&type=" + Uri.EscapeDataString(type ?? string.Empty);

            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, _settings.requestTimeoutSeconds))
                using (var response = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"출력 파일 다운로드에 실패했습니다 (HTTP {(int)response.StatusCode}, filename: {filename}).");
                    }

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        /// <summary>내부 HttpClient를 해제합니다.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        /// <summary>
        /// history 항목의 outputs.{nodeId}.images[] (없으면 audio[] / gifs[])에서 출력 파일 정보를 수집합니다.
        /// </summary>
        private static List<ComfyUIOutputFile> CollectOutputs(Dictionary<string, object> entryDict)
        {
            var result = new List<ComfyUIOutputFile>();

            object outputsObj;
            if (!entryDict.TryGetValue("outputs", out outputsObj) ||
                !(outputsObj is Dictionary<string, object> outputsDict))
            {
                return result;
            }

            foreach (var nodePair in outputsDict)
            {
                if (!(nodePair.Value is Dictionary<string, object> nodeDict))
                {
                    continue;
                }

                foreach (string listKey in new[] { "images", "audio", "gifs" })
                {
                    object listObj;
                    if (!nodeDict.TryGetValue(listKey, out listObj) || !(listObj is List<object> fileList))
                    {
                        continue;
                    }

                    foreach (object fileObj in fileList)
                    {
                        if (!(fileObj is Dictionary<string, object> fileDict))
                        {
                            continue;
                        }

                        result.Add(new ComfyUIOutputFile
                        {
                            filename = GetString(fileDict, "filename"),
                            subfolder = GetString(fileDict, "subfolder"),
                            type = GetString(fileDict, "type"),
                            nodeId = nodePair.Key
                        });
                    }
                }
            }

            return result;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) ? value as string ?? string.Empty : string.Empty;
        }

        private CancellationTokenSource CreateTimeoutCts(CancellationToken ct, int timeoutSeconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            return cts;
        }

        private string BuildConnectionErrorMessage()
        {
            return $"ComfyUI 서버({_serverUrl})에 연결할 수 없습니다. " +
                   "ComfyUI를 실행한 뒤 Tools/MCP/Settings에서 연결 테스트를 해주세요.";
        }
    }

    /// <summary>
    /// GET /history/{promptId} 응답에서 수집한 완료 항목입니다.
    /// </summary>
    public class ComfyUIHistoryEntry
    {
        /// <summary>생성된 출력 파일 목록입니다.</summary>
        public List<ComfyUIOutputFile> outputs;
    }

    /// <summary>
    /// ComfyUI 출력 파일 1건의 메타 정보입니다 (GET /view 다운로드에 사용).
    /// </summary>
    public class ComfyUIOutputFile
    {
        /// <summary>출력 파일명.</summary>
        public string filename;

        /// <summary>출력 하위 폴더 (없으면 빈 문자열).</summary>
        public string subfolder;

        /// <summary>출력 종류 (일반적으로 "output").</summary>
        public string type;

        /// <summary>해당 출력을 생성한 워크플로 노드 id.</summary>
        public string nodeId;
    }
}
