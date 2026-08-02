using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIAssetPipeline.Editor
{
    /// <summary>브리지 서버 /health 응답입니다.</summary>
    public class BridgeHealth
    {
        /// <summary>브리지 서버 자체 응답 여부.</summary>
        public bool ok;

        /// <summary>브리지가 바라보는 ComfyUI 주소.</summary>
        public string comfyUrl = string.Empty;

        /// <summary>ComfyUI 서버 생존 여부 (브리지가 /system_stats로 확인).</summary>
        public bool comfyAlive;

        /// <summary>
        /// 지금 이 포트에 떠 있는 브리지의 <c>bridge_server.py</c> 절대 경로입니다.
        /// 다른 프로젝트가 띄운 서버를 종료하기 전에 "어느 서버인지" 보여주는 데 씁니다.
        /// </summary>
        public string scriptPath = string.Empty;
    }

    /// <summary>변수 표시 조건 1건입니다 (다른 bool 변수의 현재 값과 비교, AND 결합).</summary>
    public class BridgeVisibleCondition
    {
        /// <summary>비교 대상 변수의 노드 id.</summary>
        public string nodeId = string.Empty;

        /// <summary>비교 대상 변수의 필드명.</summary>
        public string field = string.Empty;

        /// <summary>이 값과 같을 때 표시합니다.</summary>
        public bool equals;
    }

    /// <summary>워크플로 조정 변수 1건의 매니페스트입니다 (variables.json 기반).</summary>
    public class BridgeVariable
    {
        /// <summary>워크플로 노드 id (예: "5").</summary>
        public string nodeId = string.Empty;

        /// <summary>노드 inputs의 필드명 (예: "steps").</summary>
        public string field = string.Empty;

        /// <summary>UI에 표시할 라벨.</summary>
        public string label = string.Empty;

        /// <summary>변수 설명 (UI 툴팁으로 표시).</summary>
        public string description = string.Empty;

        /// <summary>값 타입: string | int | float | bool | image.</summary>
        public string type = "string";

        /// <summary>프롬프트 역할 힌트: positive | negative | 빈 문자열.</summary>
        public string role = string.Empty;

        /// <summary>원본 워크플로 JSON의 기본값.</summary>
        public object defaultValue;

        /// <summary>최소값 (숫자 타입, 없으면 null).</summary>
        public double? min;

        /// <summary>최대값 (숫자 타입, 없으면 null).</summary>
        public double? max;

        /// <summary>string 타입 선택지 목록 (ComfyUI object_info 기반, 없으면 null).</summary>
        public List<string> options;

        /// <summary>표시 조건 목록 (AND 결합, 없으면 항상 표시).</summary>
        public List<BridgeVisibleCondition> visibleWhen;

        /// <summary>변수 키 ("nodeId.field")를 반환합니다.</summary>
        public string Key => $"{nodeId}.{field}";
    }

    /// <summary>워크플로 1개의 이름과 조정 변수 목록입니다.</summary>
    public class BridgeWorkflowInfo
    {
        /// <summary>워크플로 이름 (확장자 제외, 예: "GenerateImage").</summary>
        public string name = string.Empty;

        /// <summary>조정 가능한 변수 매니페스트.</summary>
        public List<BridgeVariable> variables = new List<BridgeVariable>();

        /// <summary>
        /// 이 워크플로가 요구하지만 ComfyUI에 설치되지 않은 노드 class_type 목록.
        /// ComfyUI 미연결(comfyReachable=false) 시에는 검증이 생략되어 항상 빈 목록입니다.
        /// </summary>
        public List<string> missingNodes = new List<string>();
    }

    /// <summary>GET /workflows 응답입니다 (워크플로 목록 + ComfyUI 연결 여부).</summary>
    public class BridgeWorkflowsResult
    {
        /// <summary>
        /// 브리지가 ComfyUI /object_info 조회에 성공했는지 여부.
        /// false면 각 워크플로의 missingNodes 검증과 변수 options 첨부가 생략된 상태입니다.
        /// </summary>
        public bool comfyReachable;

        /// <summary>워크플로 정보 목록.</summary>
        public List<BridgeWorkflowInfo> workflows = new List<BridgeWorkflowInfo>();
    }

    /// <summary>사전 검증(/preflight)에서 발견된 잘못된 입력 1건입니다 (선택지 목록에 없는 값).</summary>
    public class BridgePreflightInvalidInput
    {
        /// <summary>워크플로 노드 id.</summary>
        public string node = string.Empty;

        /// <summary>노드 class_type (예: "CheckpointLoaderSimple").</summary>
        public string classType = string.Empty;

        /// <summary>입력 필드명 (예: "ckpt_name").</summary>
        public string field = string.Empty;

        /// <summary>선택지에 없는 현재 값 (누락된 모델 파일명 등).</summary>
        public string value = string.Empty;

        /// <summary>ComfyUI에 설치된 선택지 예시 (최대 10개).</summary>
        public List<string> availableSample = new List<string>();

        /// <summary>ComfyUI에 설치된 선택지 전체 개수.</summary>
        public int availableCount;
    }

    /// <summary>POST /preflight 응답입니다 (생성 제출 전 사전 검증 결과).</summary>
    public class BridgePreflightResult
    {
        /// <summary>브리지 요청 처리 성공 여부.</summary>
        public bool ok;

        /// <summary>
        /// ComfyUI /object_info 조회 성공 여부. false면 검증이 생략된 것이므로
        /// missingNodes/invalidInputs가 비어 있어도 통과로 간주하면 안 되며,
        /// 이후 생성 경로의 연결 오류 안내에 맡깁니다.
        /// </summary>
        public bool comfyReachable;

        /// <summary>ComfyUI에 설치되지 않은 노드 class_type 목록.</summary>
        public List<string> missingNodes = new List<string>();

        /// <summary>선택지 목록에 없는 값이 지정된 입력 목록 (모델 파일명·참조 이미지 등).</summary>
        public List<BridgePreflightInvalidInput> invalidInputs = new List<BridgePreflightInvalidInput>();
    }

    /// <summary>생성 결과 파일 1건입니다 (브리지 /view로 다운로드).</summary>
    public class BridgeResultFile
    {
        /// <summary>생성에 사용된 시드.</summary>
        public long seed;

        /// <summary>ComfyUI 출력 파일명.</summary>
        public string filename = string.Empty;

        /// <summary>출력 하위 폴더.</summary>
        public string subfolder = string.Empty;

        /// <summary>출력 종류 (일반적으로 "output").</summary>
        public string type = "output";
    }

    /// <summary>생성 Job 상태입니다 (GET /job/{jobId} 응답).</summary>
    public class BridgeJobStatus
    {
        /// <summary>running | completed | failed.</summary>
        public string status = "running";

        /// <summary>0~1 진행률.</summary>
        public float progress;

        /// <summary>상태/오류 메시지.</summary>
        public string message = string.Empty;

        /// <summary>경고 메시지 (예: 워크플로에 seed 필드가 없어 후보가 중복 제거될 수 있음).</summary>
        public string warning = string.Empty;

        /// <summary>완료된 결과 파일 목록.</summary>
        public List<BridgeResultFile> results = new List<BridgeResultFile>();
    }

    /// <summary>
    /// 브리지 서버 REST API 래퍼입니다. 모든 호출은 async/await 기반이며
    /// 동기 블로킹(.Result / .Wait())을 사용하지 않습니다.
    /// </summary>
    public sealed class BridgeClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _serverUrl;

        /// <summary>
        /// 설정을 기반으로 클라이언트를 생성합니다.
        /// </summary>
        /// <param name="settings">브리지 서버 주소가 담긴 설정 객체.</param>
        /// <exception cref="ArgumentNullException">settings가 null인 경우.</exception>
        /// <exception cref="InvalidOperationException">서버 주소가 올바른 URL 형식이 아닌 경우.</exception>
        public BridgeClient(AIAssetPipelineSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _serverUrl = (settings.bridgeServerUrl ?? string.Empty).TrimEnd('/');

            Uri baseUri;
            if (!Uri.TryCreate(_serverUrl, UriKind.Absolute, out baseUri))
            {
                throw new InvalidOperationException(
                    $"브리지 서버 주소가 올바르지 않습니다: \"{settings.bridgeServerUrl}\". " +
                    "Tools/AI Asset Pipeline/Settings에서 주소를 확인해주세요. (예: http://127.0.0.1:8189)");
            }

            _http = new HttpClient
            {
                BaseAddress = baseUri,
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        /// GET /health 로 브리지·ComfyUI 상태를 확인합니다.
        /// 연결 실패 시 예외 대신 ok=false를 반환합니다.
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>브리지/ComfyUI 생존 상태.</returns>
        public async Task<BridgeHealth> GetHealthAsync(CancellationToken ct = default)
        {
            try
            {
                var dict = await GetJsonAsync("/health", 10, ct).ConfigureAwait(false);
                return new BridgeHealth
                {
                    ok = GetBool(dict, "ok"),
                    comfyUrl = GetString(dict, "comfyUrl"),
                    comfyAlive = GetBool(dict, "comfyAlive"),
                    scriptPath = GetString(dict, "scriptPath")
                };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new BridgeHealth { ok = false };
            }
        }

        /// <summary>
        /// POST /shutdown 으로 브리지 서버가 스스로 종료하도록 요청합니다.
        /// 다른 Unity 프로젝트나 이전 에디터 세션이 시작해 PID를 모르는 서버를 끄기 위한 경로입니다.
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>
        /// 종료 요청이 받아들여지면 true. 서버가 <c>/shutdown</c>을 모르는 구버전(HTTP 404)이면 false.
        /// </returns>
        /// <exception cref="InvalidOperationException">연결 실패 등 그 외 오류 시.</exception>
        public async Task<bool> ShutdownAsync(CancellationToken ct = default)
        {
            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, 10))
                using (var content = new StringContent("{}", Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync("/shutdown", content, timeoutCts.Token).ConfigureAwait(false))
                {
                    // 구버전 브리지는 이 경로를 모르고 404를 준다. 이 경우만 false로 구분해
                    // 호출부가 "콘솔 창을 닫아주세요" 안내로 넘어갈 수 있게 한다.
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return false;
                    }

                    await ParseResponseAsync(response).ConfigureAwait(false);
                    return true;
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        /// <summary>
        /// GET /workflows 로 워크플로 목록과 변수 매니페스트를 조회합니다.
        /// 각 워크플로에는 ComfyUI 미설치 커스텀 노드 목록(missingNodes)이 포함됩니다
        /// (ComfyUI 미연결 시 comfyReachable=false, 검증 생략).
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>ComfyUI 연결 여부 + 워크플로 정보 목록.</returns>
        /// <exception cref="InvalidOperationException">연결 실패/응답 형식 오류 시.</exception>
        public async Task<BridgeWorkflowsResult> GetWorkflowsAsync(CancellationToken ct = default)
        {
            var dict = await GetJsonAsync("/workflows", 15, ct).ConfigureAwait(false);
            var result = new BridgeWorkflowsResult
            {
                comfyReachable = GetBool(dict, "comfyReachable")
            };

            if (!(GetValue(dict, "workflows") is List<object> workflowList))
            {
                throw new InvalidOperationException("브리지 서버 /workflows 응답 형식이 올바르지 않습니다.");
            }

            foreach (object entry in workflowList)
            {
                if (!(entry is Dictionary<string, object> wfDict))
                {
                    continue;
                }

                var info = new BridgeWorkflowInfo { name = GetString(wfDict, "name") };

                // missingNodes: ComfyUI에 설치되지 않은 커스텀 노드 목록 (미연결 시 빈 목록)
                if (GetValue(wfDict, "missingNodes") is List<object> missingList)
                {
                    info.missingNodes = missingList.OfType<string>().ToList();
                }

                if (GetValue(wfDict, "variables") is List<object> varList)
                {
                    foreach (object varObj in varList)
                    {
                        if (!(varObj is Dictionary<string, object> varDict))
                        {
                            continue;
                        }

                        var variable = new BridgeVariable
                        {
                            nodeId = GetString(varDict, "nodeId"),
                            field = GetString(varDict, "field"),
                            label = GetString(varDict, "label"),
                            description = GetString(varDict, "description"),
                            type = GetString(varDict, "type"),
                            role = GetString(varDict, "role"),
                            defaultValue = GetValue(varDict, "default"),
                            min = GetDouble(varDict, "min"),
                            max = GetDouble(varDict, "max")
                        };

                        // options: ComfyUI 설치 파일 목록 (없으면 null 유지 → TextField로 표시)
                        if (GetValue(varDict, "options") is List<object> optionList)
                        {
                            var options = optionList.OfType<string>().ToList();
                            if (options.Count > 0)
                            {
                                variable.options = options;
                            }
                        }

                        // visibleWhen: 표시 조건 목록 (AND 결합)
                        if (GetValue(varDict, "visibleWhen") is List<object> condList)
                        {
                            var conditions = new List<BridgeVisibleCondition>();
                            foreach (object condObj in condList)
                            {
                                if (condObj is Dictionary<string, object> condDict)
                                {
                                    conditions.Add(new BridgeVisibleCondition
                                    {
                                        nodeId = GetString(condDict, "nodeId"),
                                        field = GetString(condDict, "field"),
                                        equals = GetBool(condDict, "equals")
                                    });
                                }
                            }

                            if (conditions.Count > 0)
                            {
                                variable.visibleWhen = conditions;
                            }
                        }

                        info.variables.Add(variable);
                    }
                }

                result.workflows.Add(info);
            }

            return result;
        }

        /// <summary>
        /// POST /preflight 로 생성 제출 전 사전 검증을 수행합니다.
        /// 브리지가 /generate와 동일한 변수 치환 로직으로 최종 워크플로를 만든 뒤,
        /// 커스텀 노드 존재 여부와 choice 입력 값(모델 파일명·참조 이미지 등)의
        /// 유효성을 ComfyUI /object_info와 대조해 반환합니다.
        /// </summary>
        /// <param name="workflowName">워크플로 이름 (확장자 제외).</param>
        /// <param name="variables">덮어쓸 변수 맵 ("nodeId.field" → 값). null 허용.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>사전 검증 결과 (comfyReachable=false면 검증 생략).</returns>
        /// <exception cref="InvalidOperationException">브리지 연결 실패/워크플로 없음/변수 적용 실패 시.</exception>
        public async Task<BridgePreflightResult> PreflightAsync(
            string workflowName, Dictionary<string, object> variables, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object>
            {
                { "workflow", workflowName },
                { "variables", variables ?? new Dictionary<string, object>() }
            };

            var dict = await PostJsonAsync("/preflight", MiniJson.Serialize(body), 30, ct).ConfigureAwait(false);

            var result = new BridgePreflightResult
            {
                ok = GetBool(dict, "ok"),
                comfyReachable = GetBool(dict, "comfyReachable")
            };

            if (GetValue(dict, "missingNodes") is List<object> missingList)
            {
                result.missingNodes = missingList.OfType<string>().ToList();
            }

            if (GetValue(dict, "invalidInputs") is List<object> invalidList)
            {
                foreach (object entryObj in invalidList)
                {
                    if (!(entryObj is Dictionary<string, object> entryDict))
                    {
                        continue;
                    }

                    var invalid = new BridgePreflightInvalidInput
                    {
                        node = GetString(entryDict, "node"),
                        classType = GetString(entryDict, "classType"),
                        field = GetString(entryDict, "field"),
                        value = GetString(entryDict, "value"),
                        availableCount = (int)(GetDouble(entryDict, "availableCount") ?? 0.0)
                    };

                    if (GetValue(entryDict, "availableSample") is List<object> sampleList)
                    {
                        invalid.availableSample = sampleList.OfType<string>().ToList();
                    }

                    result.invalidInputs.Add(invalid);
                }
            }

            return result;
        }

        /// <summary>
        /// POST /generate 로 생성 Job을 시작하고 jobId를 반환합니다.
        /// </summary>
        /// <param name="workflowName">워크플로 이름 (확장자 제외).</param>
        /// <param name="variables">덮어쓸 변수 맵 ("nodeId.field" → 값). null 허용.</param>
        /// <param name="count">생성 개수.</param>
        /// <param name="baseSeed">기준 시드 (null이면 서버에서 무작위).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>브리지가 발급한 jobId.</returns>
        /// <exception cref="InvalidOperationException">ComfyUI 미기동/워크플로 거부 등 (원인 메시지 포함).</exception>
        public async Task<string> GenerateAsync(
            string workflowName, Dictionary<string, object> variables, int count, long? baseSeed,
            CancellationToken ct = default)
        {
            var body = new Dictionary<string, object>
            {
                { "workflow", workflowName },
                { "variables", variables ?? new Dictionary<string, object>() },
                { "count", count }
            };
            if (baseSeed.HasValue)
            {
                body["baseSeed"] = baseSeed.Value;
            }

            var dict = await PostJsonAsync("/generate", MiniJson.Serialize(body), 60, ct).ConfigureAwait(false);
            string jobId = GetString(dict, "jobId");
            if (string.IsNullOrEmpty(jobId))
            {
                throw new InvalidOperationException("브리지 서버 응답에서 jobId를 찾을 수 없습니다.");
            }

            return jobId;
        }

        /// <summary>
        /// GET /job/{jobId} 로 생성 Job 상태를 조회합니다.
        /// </summary>
        /// <param name="jobId">GenerateAsync가 반환한 jobId.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>Job 상태 (status/progress/message/results).</returns>
        /// <exception cref="InvalidOperationException">연결 실패/Job 없음 등.</exception>
        public async Task<BridgeJobStatus> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            var dict = await GetJsonAsync("/job/" + Uri.EscapeDataString(jobId ?? string.Empty), 15, ct)
                .ConfigureAwait(false);

            var status = new BridgeJobStatus
            {
                status = GetString(dict, "status"),
                progress = (float)(GetDouble(dict, "progress") ?? 0.0),
                message = GetString(dict, "message"),
                warning = GetString(dict, "warning")
            };

            if (GetValue(dict, "results") is List<object> resultList)
            {
                foreach (object resultObj in resultList)
                {
                    if (!(resultObj is Dictionary<string, object> fileDict))
                    {
                        continue;
                    }

                    status.results.Add(new BridgeResultFile
                    {
                        seed = (long)(GetDouble(fileDict, "seed") ?? 0.0),
                        filename = GetString(fileDict, "filename"),
                        subfolder = GetString(fileDict, "subfolder"),
                        type = GetString(fileDict, "type")
                    });
                }
            }

            return status;
        }

        /// <summary>
        /// GET /view 프록시로 결과 파일을 다운로드합니다.
        /// </summary>
        /// <param name="file">Job 결과 파일 정보.</param>
        /// <param name="timeoutSeconds">다운로드 타임아웃(초).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>파일 바이트 배열.</returns>
        /// <exception cref="InvalidOperationException">연결 실패 또는 HTTP 오류 시.</exception>
        public async Task<byte[]> DownloadAsync(
            BridgeResultFile file, int timeoutSeconds, CancellationToken ct = default)
        {
            string url = "/view?filename=" + Uri.EscapeDataString(file.filename ?? string.Empty) +
                         "&subfolder=" + Uri.EscapeDataString(file.subfolder ?? string.Empty) +
                         "&type=" + Uri.EscapeDataString(file.type ?? string.Empty);

            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, timeoutSeconds))
                using (var response = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"결과 파일 다운로드에 실패했습니다 (HTTP {(int)response.StatusCode}, filename: {file.filename}).");
                    }

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        /// <summary>
        /// POST /upload 로 이미지를 업로드하고 ComfyUI가 부여한 파일명을 반환합니다
        /// (LoadImage 노드의 image 필드 값으로 사용).
        /// </summary>
        /// <param name="filePath">업로드할 로컬 이미지 파일 경로.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>ComfyUI input 폴더에 저장된 파일명.</returns>
        /// <exception cref="FileNotFoundException">파일이 없는 경우.</exception>
        /// <exception cref="InvalidOperationException">업로드 실패 시.</exception>
        public async Task<string> UploadImageAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException($"업로드할 이미지 파일을 찾을 수 없습니다: \"{filePath}\"", filePath);
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            string fileName = Path.GetFileName(filePath);

            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, 120))
                using (var form = new MultipartFormDataContent())
                {
                    var fileContent = new ByteArrayContent(bytes);
                    form.Add(fileContent, "image", fileName);

                    using (var response = await _http.PostAsync("/upload", form, timeoutCts.Token).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var dict = MiniJson.Deserialize(text) as Dictionary<string, object>;

                        if (!response.IsSuccessStatusCode || dict == null || !GetBool(dict, "ok"))
                        {
                            throw new InvalidOperationException(
                                $"이미지 업로드에 실패했습니다: {(dict != null ? GetString(dict, "error") : text)}");
                        }

                        string name = GetString(dict, "name");
                        if (string.IsNullOrEmpty(name))
                        {
                            throw new InvalidOperationException("업로드 응답에서 파일명(name)을 찾을 수 없습니다.");
                        }

                        return name;
                    }
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        /// <summary>
        /// POST /free 로 ComfyUI에 로드된 모델을 언로드하고 메모리를 해제합니다.
        /// </summary>
        /// <param name="ct">취소 토큰.</param>
        /// <exception cref="InvalidOperationException">브리지/ComfyUI 연결 실패 시.</exception>
        public async Task FreeMemoryAsync(CancellationToken ct = default)
        {
            await PostJsonAsync("/free", "{}", 60, ct).ConfigureAwait(false);
        }

        /// <summary>내부 HttpClient를 해제합니다.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        // ─────────────────────────── 내부 헬퍼 ───────────────────────────

        private async Task<Dictionary<string, object>> GetJsonAsync(
            string path, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, timeoutSeconds))
                using (var response = await _http.GetAsync(path, timeoutCts.Token).ConfigureAwait(false))
                {
                    return await ParseResponseAsync(response).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        private async Task<Dictionary<string, object>> PostJsonAsync(
            string path, string json, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                using (var timeoutCts = CreateTimeoutCts(ct, timeoutSeconds))
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync(path, content, timeoutCts.Token).ConfigureAwait(false))
                {
                    return await ParseResponseAsync(response).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException(BuildConnectionErrorMessage(), e);
            }
        }

        private static async Task<Dictionary<string, object>> ParseResponseAsync(HttpResponseMessage response)
        {
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var dict = MiniJson.Deserialize(text) as Dictionary<string, object>;

            if (!response.IsSuccessStatusCode)
            {
                string error = dict != null ? GetString(dict, "error") : string.Empty;
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(error)
                        ? $"브리지 서버 요청이 실패했습니다 (HTTP {(int)response.StatusCode}): {text}"
                        : error);
            }

            if (dict == null)
            {
                throw new InvalidOperationException($"브리지 서버 응답을 파싱할 수 없습니다: {text}");
            }

            return dict;
        }

        private static CancellationTokenSource CreateTimeoutCts(CancellationToken ct, int timeoutSeconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            return cts;
        }

        private string BuildConnectionErrorMessage()
        {
            return $"브리지 서버({_serverUrl})에 연결할 수 없습니다. " +
                   "ComfyUI Generator 창의 [서버 시작] 버튼으로 브리지 서버를 시작한 뒤 다시 시도해주세요. (Python 3 필요)";
        }

        private static object GetValue(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            return GetValue(dict, key) as string ?? string.Empty;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            return GetValue(dict, key) is bool b && b;
        }

        private static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            object value = GetValue(dict, key);
            if (value is double d)
            {
                return d;
            }

            if (value is long l)
            {
                return l;
            }

            if (value is int i)
            {
                return i;
            }

            return null;
        }
    }
}
