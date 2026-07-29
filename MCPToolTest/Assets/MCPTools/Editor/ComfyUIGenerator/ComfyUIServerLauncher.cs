using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>
    /// 로컬 브리지 서버(Server~/bridge_server.py) 프로세스를 시작/종료하는 유틸리티입니다.
    /// 브리지 서버는 Unity와 ComfyUI 사이에서 워크플로 JSON 로드·변수 치환·큐잉/폴링을 담당합니다.
    /// Python 실행 파일은 <see cref="MCPToolSettings.pythonExecutable"/>을 우선 사용하되,
    /// 찾지 못하면 PATH·잘 알려진 설치 위치를 순서대로 탐지하고 실제로 실행해 3.7 이상인지 검증합니다(하드코딩 금지).
    /// 프로세스 PID와 탐지 결과는 SessionState에 저장해 도메인 리로드 후에도 재사용됩니다.
    /// </summary>
    public static class ComfyUIServerLauncher
    {
        private const string SessionKeyPid = "MCPTools.Bridge.ServerPid";
        private const string SessionKeyPythonPath = "MCPTools.Bridge.PythonPath";
        private const string SessionKeyPythonArgs = "MCPTools.Bridge.PythonArgs";
        private const string SessionKeyPythonVersion = "MCPTools.Bridge.PythonVersion";
        private const string SessionKeyPythonSetting = "MCPTools.Bridge.PythonSetting";
        private const string SessionKeyIdentityChecked = "MCPTools.Bridge.IdentityChecked";

        /// <summary>브리지 서버 스크립트의 설치 루트 기준 상대 경로입니다.</summary>
        private const string ScriptRelativePath = "Editor/ComfyUIGenerator/Server~/bridge_server.py";

        /// <summary>
        /// 사용자 워크플로/변수 오버라이드 폴더입니다 (에셋 경로 기준, 고정: "Assets/MCPTools.User/ComfyUI").
        /// 브리지 서버 시작 시 --user-dir 인자로 절대 경로가 전달되며,
        /// "&lt;이 폴더&gt;/workflows/*.json"과 "&lt;이 폴더&gt;/variables.json"이 있으면 패키지 동봉본보다 우선 사용됩니다.
        /// 폴더/파일이 없으면 브리지가 조용히 패키지 기본값을 사용하므로 미리 만들 필요는 없습니다.
        /// </summary>
        public const string UserComfyDirAssetPath = "Assets/MCPTools.User/ComfyUI";

        /// <summary>Python 후보 검증에 사용할 인자 (major/minor/실행 파일 경로를 한 줄씩 출력).</summary>
        private const string VersionProbeArguments =
            "-c \"import sys; print(sys.version_info[0]); print(sys.version_info[1]); print(sys.executable)\"";

        /// <summary>지원하는 최소 Python 버전(major).</summary>
        private const int MinimumPythonMajor = 3;

        /// <summary>지원하는 최소 Python 버전(minor).</summary>
        private const int MinimumPythonMinor = 7;

        /// <summary>
        /// 이 길이 이상의 경로는 Windows 기본 최대 경로 길이(MAX_PATH = 260자)에 근접한 것으로 보고
        /// 실패 안내에 긴 경로 항목을 덧붙입니다.
        /// UPM(git URL) 설치본은 Library/PackageCache/&lt;패키지명&gt;@&lt;40자 커밋 해시&gt;/ 아래에 놓이므로
        /// 프로젝트가 깊은 폴더에 있으면 이 한계를 쉽게 넘습니다.
        /// </summary>
        private const int LongPathThreshold = 250;

        /// <summary>
        /// 브리지 서버 스크립트의 전체 경로를 계산합니다.
        /// Server~는 Unity가 임포트하지 않는 폴더라 AssetDatabase로는 찾을 수 없으므로 항상 파일시스템 경로로 접근합니다.
        /// ① 설치 루트가 UPM 패키지(Packages/&lt;패키지명&gt;/...)면
        /// <c>PackageInfo.FindForAssetPath</c>가 돌려주는 패키지의 실제 디스크 경로(resolvedPath)를 기준으로 조합하고,
        /// ② Assets/ 아래 설치면 설치 루트의 선행 "Assets"을 <c>Application.dataPath</c>로 치환해 조합합니다.
        /// (package.json이 없는 Assets 설치에서는 FindForAssetPath가 null을 돌려주므로 기존 동작이 유지됩니다.)
        /// </summary>
        /// <returns>bridge_server.py의 절대 경로.</returns>
        public static string GetScriptPath()
        {
            string root = MCPToolSettings.InstallRoot.Replace('\\', '/').TrimEnd('/');

            // ① UPM 패키지 설치: "Packages/<패키지명>/..." 형태의 에셋 경로는 PackageInfo로
            //    실제 디스크 경로(Library/PackageCache/... 또는 Packages/...)를 얻는다.
            //    Assets/ 아래 경로에서는 FindForAssetPath가 null을 돌려주므로 아래 ②로 넘어간다.
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(root);
            if (packageInfo != null)
            {
                string packageAssetPath = packageInfo.assetPath.Replace('\\', '/').TrimEnd('/'); // "Packages/<패키지명>"
                string resolvedPath = packageInfo.resolvedPath.Replace('\\', '/').TrimEnd('/'); // 패키지 실제 디스크 경로

                // 설치 루트가 패키지 루트보다 깊으면 (예: "Packages/<패키지명>/MCPTools") 그 하위 경로를 보존한다.
                string underPackage = string.Empty;
                if (root.Length > packageAssetPath.Length &&
                    root.StartsWith(packageAssetPath + "/", StringComparison.Ordinal))
                {
                    underPackage = root.Substring(packageAssetPath.Length + 1);
                }

                string installAbsoluteInPackage = string.IsNullOrEmpty(underPackage)
                    ? resolvedPath
                    : resolvedPath + "/" + underPackage;
                return installAbsoluteInPackage + "/" + ScriptRelativePath;
            }

            // ② Assets/ 설치: Application.dataPath는 "<프로젝트>/Assets"이므로 선행 "Assets"을 이것으로 치환한다.
            string dataPath = UnityEngine.Application.dataPath.Replace('\\', '/').TrimEnd('/');

            string underAssets;
            if (string.Equals(root, "Assets", StringComparison.Ordinal))
            {
                underAssets = string.Empty;
            }
            else if (root.StartsWith("Assets/", StringComparison.Ordinal))
            {
                underAssets = root.Substring("Assets/".Length);
            }
            else
            {
                underAssets = root; // 예상 밖의 형태면 그대로 이어 붙인다.
            }

            string installAbsolute = string.IsNullOrEmpty(underAssets) ? dataPath : dataPath + "/" + underAssets;
            return installAbsolute + "/" + ScriptRelativePath;
        }

        /// <summary>
        /// 브리지 서버 스크립트를 찾지 못했을 때의 원인·조치 안내 메시지를 만듭니다.
        /// 탐색한 절대 경로, 인식된 설치 루트, 설치 형태(Assets 설치 / UPM 패키지) 판정을 포함합니다.
        /// </summary>
        private static string BuildScriptNotFoundMessage(string scriptPath)
        {
            string root = MCPToolSettings.InstallRoot;
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                root.Replace('\\', '/').TrimEnd('/'));
            string installKind = packageInfo != null
                ? $"UPM 패키지 (\"{packageInfo.name}\", 실제 위치: \"{packageInfo.resolvedPath}\")"
                : "Assets 폴더 설치";

            var message = new StringBuilder();
            message.AppendLine("브리지 서버 스크립트를 찾을 수 없습니다.");
            message.AppendLine($"탐색한 절대 경로: \"{scriptPath}\"");
            message.AppendLine($"인식된 MCPTools 설치 루트: \"{root}\"");
            message.AppendLine($"설치 형태: {installKind}");
            message.AppendLine();
            message.AppendLine("조치:");
            message.AppendLine($"1. 위 설치 루트 아래에 \"{ScriptRelativePath}\"가 있는지 확인해주세요 " +
                               "(Unity는 ~로 끝나는 폴더를 임포트하지 않아 프로젝트 창에는 보이지 않습니다. 파일 탐색기로 확인하세요).");
            if (packageInfo != null)
            {
                message.AppendLine("2. UPM 패키지 설치라면 패키지가 불완전하게 받아졌을 수 있습니다. " +
                                   "Window > Package Manager에서 이 패키지를 Remove한 뒤 다시 추가(재설치)해주세요.");
            }
            else
            {
                message.AppendLine("2. Assets 설치라면 MCPTools 폴더를 옮기거나 복사할 때 Server~ 폴더가 함께 복사됐는지 확인해주세요 " +
                                   "(~ 폴더는 .unitypackage로 내보낼 때도 빠지므로, 배포본 폴더를 통째로 넣어야 합니다).");
            }

            // 경로가 MAX_PATH에 근접하면 "파일 없음"이 아니라 경로 길이 때문일 수 있으므로 함께 안내한다.
            if (IsLongPath(scriptPath))
            {
                message.AppendLine("3. " + BuildLongPathNote(scriptPath));
            }

            return message.ToString().TrimEnd();
        }

        /// <summary>경로 길이가 Windows 최대 경로 길이(260자)에 근접하는지 판정합니다.</summary>
        private static bool IsLongPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.Length >= LongPathThreshold;
        }

        /// <summary>긴 경로가 원인일 수 있음을 알리는 안내 문구를 만듭니다 (경로 길이 포함).</summary>
        private static string BuildLongPathNote(string path)
        {
            return $"경로 길이가 {path.Length}자로 Windows 기본 최대 경로 길이(260자)에 근접합니다. " +
                   "경로가 너무 길어 파일을 열지 못했을 수 있습니다. " +
                   "Unity 프로젝트를 더 짧은 경로(예: C:\\Unity\\<프로젝트명>)로 옮기거나, " +
                   "Windows의 긴 경로 지원을 켜주세요 " +
                   "(레지스트리 HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem 의 LongPathsEnabled를 1로 설정한 뒤 재부팅). " +
                   "Package Manager(git URL) 설치본은 Library/PackageCache/<패키지명>@<커밋 해시>/ 아래에 놓여 경로가 특히 길어집니다.";
        }

        // ──────────────────────────── 실행 중인 브리지 신원 확인 ────────────────────────────

        /// <summary>
        /// 에디터 로드 시 실행 중인 브리지의 신원 확인을 1회 예약합니다.
        /// 브리지가 응답하지 않으면 확인 완료로 표시하지 않으므로, 나중에 브리지가 뜨면
        /// 다음 도메인 리로드에서 다시 확인합니다(경고 자체는 세션당 1회).
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ScheduleBridgeIdentityCheck()
        {
            // 에디터 종료 시 브리지 정리도 여기서 함께 등록한다 (구독은 도메인 단위라 중복되지 않는다).
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;

            if (SessionState.GetBool(SessionKeyIdentityChecked, false))
            {
                return;
            }

            // 도메인 로드 중에는 AssetDatabase 조회를 피하고 다음 에디터 틱으로 미룬다.
            // 확인 자체가 실패해도 도구 사용에는 영향이 없으므로 결과를 기다리지 않는다(예외는 내부에서 처리).
            EditorApplication.delayCall += () => _ = WarnIfForeignBridgeAsync();
        }

        /// <summary>
        /// 지금 브리지 서버 주소에서 응답하는 브리지가 이 프로젝트의 설치본인지 확인하고,
        /// 다른 위치의 브리지라면 경고 로그를 남깁니다(세션당 1회).
        /// 여러 Unity 프로젝트가 같은 포트를 공유하면 다른 프로젝트의 워크플로·변수를
        /// 사용하게 되므로 이를 사용자에게 알리기 위한 확인입니다.
        /// 브리지가 응답하지 않으면 아무 것도 하지 않습니다(조용한 폴백).
        /// </summary>
        /// <returns>다른 위치의 브리지가 감지되어 경고를 남겼으면 true.</returns>
        public static async Task<bool> WarnIfForeignBridgeAsync()
        {
            if (SessionState.GetBool(SessionKeyIdentityChecked, false))
            {
                return false;
            }

            string bridgeUrl = ReadConfiguredBridgeUrl();
            if (string.IsNullOrEmpty(bridgeUrl))
            {
                return false; // 설정 에셋이 아직 없음 — 에셋을 만들지 않고 조용히 건너뛴다.
            }

            string json;
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    json = await http.GetStringAsync(bridgeUrl.TrimEnd('/') + "/health");
                }
            }
            catch (Exception)
            {
                // 브리지 미기동/응답 없음 — 확인 완료로 표시하지 않아 다음 리로드에서 재시도한다.
                return false;
            }

            SessionState.SetBool(SessionKeyIdentityChecked, true);

            string runningScriptPath = string.Empty;
            string runningVersion = string.Empty;
            try
            {
                var dict = MiniJson.Deserialize(json) as Dictionary<string, object>;
                if (dict != null)
                {
                    object value;
                    if (dict.TryGetValue("scriptPath", out value) && value != null)
                    {
                        runningScriptPath = value.ToString();
                    }

                    if (dict.TryGetValue("version", out value) && value != null)
                    {
                        runningVersion = value.ToString();
                    }
                }
            }
            catch (Exception)
            {
                return false; // 브리지가 아닌 다른 서버의 응답 등 — 판별 불가
            }

            if (string.IsNullOrEmpty(runningScriptPath))
            {
                // scriptPath를 알려주지 않는 구버전 브리지 — 판별 불가이므로 경고하지 않는다.
                return false;
            }

            string ownScriptPath = GetScriptPath();
            if (IsSamePath(runningScriptPath, ownScriptPath))
            {
                return false;
            }

            string versionNote = string.IsNullOrEmpty(runningVersion) ? string.Empty : $", 버전 {runningVersion}";
            UnityEngine.Debug.LogWarning(
                $"[MCPTools] 다른 위치의 브리지 서버가 실행 중입니다 " +
                $"(실행 중: \"{runningScriptPath}\"{versionNote} / 이 프로젝트: \"{ownScriptPath}\"). " +
                "워크플로·변수가 이 프로젝트의 것과 다를 수 있습니다. " +
                $"다른 Unity 프로젝트의 브리지를 종료하거나, Tools/MCP/Settings에서 브리지 서버 주소({bridgeUrl})의 " +
                "포트를 이 프로젝트 전용으로 바꿔주세요.");
            return true;
        }

        /// <summary>
        /// Assets 아래에 이미 존재하는 설정 에셋에서 브리지 서버 주소를 읽습니다.
        /// (에셋이 없으면 새로 만들지 않고 빈 문자열을 반환합니다.)
        /// </summary>
        private static string ReadConfiguredBridgeUrl()
        {
            string[] guids = AssetDatabase.FindAssets("t:MCPToolSettings", new[] { "Assets" });
            if (guids == null || guids.Length == 0)
            {
                return string.Empty;
            }

            var settings = AssetDatabase.LoadAssetAtPath<MCPToolSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return settings == null ? string.Empty : (settings.bridgeServerUrl ?? string.Empty).Trim();
        }

        /// <summary>
        /// 두 파일 경로가 같은 파일을 가리키는지 비교합니다
        /// (절대 경로 정규화 + 구분자 통일, Windows에서는 대소문자 무시).
        /// </summary>
        private static bool IsSamePath(string left, string right)
        {
            string normalizedLeft = NormalizePath(left);
            string normalizedRight = NormalizePath(right);
            if (string.IsNullOrEmpty(normalizedLeft) || string.IsNullOrEmpty(normalizedRight))
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight,
                IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>경로를 절대 경로로 만들고 구분자를 "/"로 통일합니다 (실패 시 원본을 최대한 정규화).</summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string result = path.Trim();
            try
            {
                result = Path.GetFullPath(result);
            }
            catch (Exception)
            {
                // 잘못된 문자 등 — 원본 문자열 기준으로만 비교한다.
            }

            return result.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// 이 도구로 시작한 브리지 서버 프로세스가 아직 살아 있는지 확인합니다.
        /// (외부에서 직접 실행한 서버는 감지하지 못하므로 HTTP 생존 확인과 병행 사용을 권장합니다.)
        /// </summary>
        /// <returns>보관된 PID의 프로세스가 살아 있으면 true.</returns>
        public static bool IsLaunchedProcessAlive()
        {
            int pid = SessionState.GetInt(SessionKeyPid, 0);
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    SessionState.EraseInt(SessionKeyPid);
                    return false;
                }

                return true;
            }
            catch (ArgumentException)
            {
                // 해당 PID의 프로세스가 없음
                SessionState.EraseInt(SessionKeyPid);
                return false;
            }
            catch (InvalidOperationException)
            {
                SessionState.EraseInt(SessionKeyPid);
                return false;
            }
        }

        /// <summary>
        /// 세션에 캐시된 Python 탐지 결과를 지웁니다.
        /// (설정의 Python 실행 파일을 바꿨거나 Python을 새로 설치한 뒤 다시 탐지하려면 호출하세요.)
        /// </summary>
        public static void ClearPythonCache()
        {
            SessionState.EraseString(SessionKeyPythonPath);
            SessionState.EraseString(SessionKeyPythonArgs);
            SessionState.EraseString(SessionKeyPythonVersion);
            SessionState.EraseString(SessionKeyPythonSetting);
        }

        /// <summary>
        /// 사용할 Python 3 실행 파일을 탐지합니다. 후보를 순서대로 만들고 각 후보를 실제로 실행해
        /// 3.7 이상인지 검증한 뒤 첫 성공 후보를 반환합니다(Windows 스토어 별칭 스텁·Python 2는 검증에서 탈락).
        /// 탐지 순서: 설정값 → 명령어 이름(py -3 / python / python3) → 잘 알려진 설치 폴더 →
        /// Anaconda/Miniconda 설치본 → PATH 디렉터리 → 표준 경로.
        /// 결과는 SessionState에 캐시되며, 설정의 Python 실행 파일이 바뀌면 캐시를 무시합니다.
        /// </summary>
        /// <param name="settings">Python 실행 파일 설정이 담긴 설정 객체(null 허용).</param>
        /// <param name="argumentPrefix">실행 시 스크립트 경로 앞에 붙일 인자(py 런처의 "-3" 등). 없으면 빈 문자열.</param>
        /// <param name="version">검증된 Python 버전 문자열(예: "3.12"). 실패 시 빈 문자열.</param>
        /// <param name="diagnostics">시도한 후보와 실패 사유 목록(사용자 안내 메시지에 덧붙임).</param>
        /// <returns>검증에 성공한 Python 실행 파일의 절대 경로(또는 명령어 이름). 찾지 못하면 null.</returns>
        public static string ResolvePython(MCPToolSettings settings, out string argumentPrefix, out string version, out string diagnostics)
        {
            argumentPrefix = string.Empty;
            version = string.Empty;
            diagnostics = string.Empty;

            string configured = settings == null || string.IsNullOrEmpty(settings.pythonExecutable)
                ? string.Empty
                : settings.pythonExecutable.Trim();

            // 캐시 사용 (설정값이 바뀌었으면 무시)
            string cachedPath = SessionState.GetString(SessionKeyPythonPath, string.Empty);
            if (!string.IsNullOrEmpty(cachedPath) &&
                string.Equals(SessionState.GetString(SessionKeyPythonSetting, string.Empty), configured, StringComparison.Ordinal))
            {
                if (!ContainsPathSeparator(cachedPath) || File.Exists(cachedPath))
                {
                    argumentPrefix = SessionState.GetString(SessionKeyPythonArgs, string.Empty);
                    version = SessionState.GetString(SessionKeyPythonVersion, string.Empty);
                    return cachedPath;
                }

                // 캐시된 실행 파일이 사라짐 (Python 제거/이동)
                ClearPythonCache();
            }

            var log = new StringBuilder();
            List<KeyValuePair<string, string>> candidates = BuildPythonCandidates(configured);
            foreach (KeyValuePair<string, string> candidate in candidates)
            {
                string detectedVersion;
                string detectedExecutable;
                string failure;
                if (VerifyPython(candidate.Key, candidate.Value, out detectedVersion, out detectedExecutable, out failure))
                {
                    // sys.executable을 알 수 있으면 절대 경로를 그대로 사용해 인자 접두사 없이 실행한다.
                    string resolved = candidate.Key;
                    string prefix = candidate.Value;
                    if (!string.IsNullOrEmpty(detectedExecutable) && File.Exists(detectedExecutable))
                    {
                        resolved = detectedExecutable;
                        prefix = string.Empty;
                    }

                    SessionState.SetString(SessionKeyPythonPath, resolved);
                    SessionState.SetString(SessionKeyPythonArgs, prefix);
                    SessionState.SetString(SessionKeyPythonVersion, detectedVersion);
                    SessionState.SetString(SessionKeyPythonSetting, configured);

                    argumentPrefix = prefix;
                    version = detectedVersion;
                    diagnostics = log.ToString().TrimEnd();
                    return resolved;
                }

                log.AppendLine($"- {DescribeCandidate(candidate)} → {failure}");
            }

            if (candidates.Count == 0)
            {
                log.AppendLine("- 검사할 Python 후보를 찾지 못했습니다.");
            }

            diagnostics = log.ToString().TrimEnd();
            return null;
        }

        /// <summary>
        /// Python 3.7 이상을 찾지 못했을 때 사용자에게 보여줄 원인·조치 안내 메시지를 만듭니다.
        /// </summary>
        /// <param name="diagnostics"><see cref="ResolvePython"/>이 돌려준 후보별 실패 사유(비어 있어도 됨).</param>
        /// <returns>다이얼로그/예외 메시지로 그대로 쓸 수 있는 한국어 안내문.</returns>
        public static string BuildPythonNotFoundMessage(string diagnostics)
        {
            var message = new StringBuilder();
            message.AppendLine("Python 3.7 이상을 찾지 못했습니다. 브리지 서버(3단계 생성)를 실행하려면 Python 3가 필요합니다.");
            message.AppendLine();
            message.AppendLine("다음을 순서대로 확인해주세요.");
            message.AppendLine("1. python.org에서 Python 3를 설치하세요. 설치 첫 화면의 \"Add python.exe to PATH\" 체크박스를 반드시 켜야 합니다.");
            message.AppendLine("2. Unity를 실행한 뒤에 Python을 설치했다면 Unity 에디터와 Unity Hub를 모두 종료했다가 다시 실행하세요. " +
                               "실행 중인 프로세스는 설치 전의 옛 PATH 환경변수를 계속 사용합니다.");
            message.AppendLine("3. Windows에서 python을 실행하면 Microsoft Store 창만 뜨는 경우: " +
                               "설정 > 앱 > 고급 앱 설정 > 앱 실행 별칭에서 python.exe / python3.exe 항목을 끄세요.");
            message.AppendLine("4. Anaconda/Miniconda만 설치한 경우: conda는 설치 시 PATH에 등록하지 않는 것이 기본이라 " +
                               "python 명령이 인식되지 않습니다. 기본 설치 폴더는 자동으로 찾지만 다른 위치에 설치했다면 " +
                               "아래 5번에서 conda 폴더의 python.exe 경로를 직접 지정하세요 (예: %USERPROFILE%\\anaconda3\\python.exe).");
            message.AppendLine("5. 그래도 안 되면 Tools/MCP/Settings의 \"Python 실행 파일\"에 python.exe의 절대 경로를 직접 입력하세요 " +
                               "(예: %LOCALAPPDATA%\\Programs\\Python\\Python312\\python.exe). 같은 창의 [자동 탐지] 버튼으로 채울 수도 있습니다.");

            if (!string.IsNullOrEmpty(diagnostics))
            {
                message.AppendLine();
                message.AppendLine("시도한 후보와 실패 사유:");
                message.Append(diagnostics);
            }

            return message.ToString();
        }

        /// <summary>
        /// 브리지 서버를 시작합니다. 바인딩 주소(호스트)와 포트는 설정의 bridgeServerUrl에서,
        /// ComfyUI 주소는 comfyUIServerUrl에서, Job 타임아웃은 jobTimeoutSeconds에서 읽어 실행 인자로 전달하고,
        /// 사용자 오버라이드 폴더(<see cref="UserComfyDirAssetPath"/>)의 절대 경로를 --user-dir로 항상 전달합니다.
        /// Python은 -B 옵션으로 실행해 읽기 전용 패키지 폴더에 __pycache__가 생성되지 않게 합니다.
        /// Python 실행 파일은 <see cref="ResolvePython"/>으로 탐지·검증하며,
        /// 시작 직후 프로세스가 곧바로 종료되면 로그 꼬리와 함께 원인 안내 예외를 던집니다.
        /// </summary>
        /// <param name="settings">Python 실행 파일·주소가 담긴 설정 객체.</param>
        /// <exception cref="InvalidOperationException">스크립트 누락/Python 미설치/시작 실패/즉시 종료 시 (원인·조치 안내 포함).</exception>
        public static void Start(MCPToolSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (IsLaunchedProcessAlive())
            {
                throw new InvalidOperationException("이미 이 도구로 시작한 브리지 서버가 실행 중입니다.");
            }

            string scriptPath = GetScriptPath();
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException(BuildScriptNotFoundMessage(scriptPath));
            }

            string argumentPrefix;
            string pythonVersion;
            string diagnostics;
            string python = ResolvePython(settings, out argumentPrefix, out pythonVersion, out diagnostics);
            if (string.IsNullOrEmpty(python))
            {
                throw new InvalidOperationException(BuildPythonNotFoundMessage(diagnostics));
            }

            int port = ExtractPort(settings.bridgeServerUrl, 8189);
            // 설정의 브리지 주소에 적힌 호스트를 그대로 바인딩 주소로 넘긴다.
            // (127.0.0.1이 아닌 주소를 넣으면 브리지가 시작 로그에 로컬 외 바인딩 경고를 남긴다.)
            string host = ExtractHost(settings.bridgeServerUrl, "127.0.0.1");

            // 사용자 워크플로/변수 오버라이드 폴더 (없어도 브리지가 조용히 기본값을 사용하므로 항상 전달).
            string userDir = Path.GetFullPath(UserComfyDirAssetPath).Replace('\\', '/');

            string arguments = string.IsNullOrEmpty(argumentPrefix) ? string.Empty : argumentPrefix + " ";
            // -B: 바이트코드(__pycache__) 생성 금지 — 읽기 전용 UPM 패키지/embed 폴더 오염 방지.
            arguments += $"-B \"{scriptPath}\" --host \"{host}\" --port {port} --comfy-url \"{settings.comfyUIServerUrl}\"";
            arguments += $" --user-dir \"{userDir}\"";
            arguments += $" --job-timeout {Math.Max(1, settings.jobTimeoutSeconds)}";
            string logPath = string.Empty;
            if (!settings.showBridgeConsole)
            {
                // 콘솔 창 없이 실행: 로그는 시스템 임시 폴더의 파일로 남긴다 (환경 독립 경로).
                logPath = Path.Combine(Path.GetTempPath(), "mcptools_bridge_server.log").Replace('\\', '/');
                arguments += $" --log-file \"{logPath}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(scriptPath),
                // showBridgeConsole=true면 콘솔 창을 띄워 로그를 직접 확인할 수 있게 한다.
                UseShellExecute = settings.showBridgeConsole,
                CreateNoWindow = !settings.showBridgeConsole
            };

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception e)
            {
                // 스크립트 경로가 지나치게 길면 프로세스 시작 자체가 실패할 수 있으므로 같은 판정을 적용한다.
                string longPathNote = IsLongPath(scriptPath) ? "\n" + BuildLongPathNote(scriptPath) : string.Empty;
                throw new InvalidOperationException(
                    $"브리지 서버 시작에 실패했습니다: {e.Message}\n" +
                    $"실행 대상: \"{python}\"\n" +
                    "Tools/MCP/Settings에서 [자동 탐지] 버튼을 누르거나 python.exe의 절대 경로를 직접 지정해주세요." +
                    longPathNote, e);
            }

            if (process == null)
            {
                throw new InvalidOperationException(
                    $"브리지 서버 프로세스를 시작하지 못했습니다. 실행 대상: {python}");
            }

            // 시작 직후 조기 종료 감지: 파이썬이 곧바로 죽으면(포트 충돌·스크립트 오류 등) 사용자에게 원인을 알린다.
            if (WaitedAndExited(process, 1000))
            {
                SessionState.EraseInt(SessionKeyPid);
                throw new InvalidOperationException(BuildEarlyExitMessage(process, python, port, logPath));
            }

            SessionState.SetInt(SessionKeyPid, process.Id);
            string versionNote = string.IsNullOrEmpty(pythonVersion) ? string.Empty : $", Python {pythonVersion}";
            UnityEngine.Debug.Log(string.IsNullOrEmpty(logPath)
                ? $"[MCPTools] 브리지 서버를 시작했습니다 (PID {process.Id}, 포트 {port}{versionNote}, 실행 파일 {python})."
                : $"[MCPTools] 브리지 서버를 시작했습니다 (PID {process.Id}, 포트 {port}{versionNote}, 실행 파일 {python}, 로그: {logPath}).");
        }

        /// <summary>
        /// 에디터 종료 직전에 이 도구로 시작한 브리지 서버를 정리합니다.
        /// 남겨두면 다음 에디터 실행이나 다른 프로젝트에서 포트를 점유한 채 제어할 수 없는 상태가 됩니다(D18).
        /// 외부에서 직접 실행한 서버나 다른 프로젝트가 띄운 서버는 PID를 모르므로 건드리지 않습니다.
        /// </summary>
        private static void OnEditorQuitting()
        {
            if (!IsLaunchedProcessAlive())
            {
                return;
            }

            // 설정 로드 자체가 실패해도(에셋 임포트 중 종료 등) 종료 흐름을 막지 않는다.
            try
            {
                if (!MCPToolSettings.GetOrCreate().shutdownBridgeOnEditorQuit)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 설정을 읽을 수 없으면 기본 동작(종료)을 따른다.
            }

            try
            {
                Stop();
            }
            catch (Exception e)
            {
                // 종료 중이라 다이얼로그는 띄울 수 없다. 로그만 남긴다.
                UnityEngine.Debug.LogWarning(
                    $"[MCPTools] 에디터 종료 시 브리지 서버를 정리하지 못했습니다: {e.Message}");
            }
        }

        /// <summary>
        /// 이 도구로 시작한 브리지 서버 프로세스 트리를 종료합니다 (Windows: taskkill /T /F).
        /// </summary>
        /// <exception cref="InvalidOperationException">이 도구로 시작한 프로세스가 없는 경우.</exception>
        public static void Stop()
        {
            int pid = SessionState.GetInt(SessionKeyPid, 0);
            if (pid <= 0 || !IsLaunchedProcessAlive())
            {
                SessionState.EraseInt(SessionKeyPid);
                throw new InvalidOperationException(
                    "이 도구로 시작한 브리지 서버 프로세스가 없습니다. " +
                    "외부에서 직접 실행한 서버는 해당 콘솔 창에서 종료해주세요.");
            }

            try
            {
#if UNITY_EDITOR_WIN
                // 콘솔 창 실행 시 python 자식 프로세스까지 함께 종료해야 하므로 프로세스 트리 종료를 사용한다.
                var killInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process kill = Process.Start(killInfo))
                {
                    if (kill != null)
                    {
                        kill.WaitForExit(10000);
                    }
                }
#else
                Process.GetProcessById(pid).Kill();
#endif
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"브리지 서버 종료 중 오류가 발생했습니다: {e.Message}", e);
            }
            finally
            {
                SessionState.EraseInt(SessionKeyPid);
            }

            UnityEngine.Debug.Log($"[MCPTools] 브리지 서버를 종료했습니다 (PID {pid}).");
        }

        /// <summary>URL에서 포트 번호를 추출합니다 (실패 시 기본값).</summary>
        private static int ExtractPort(string url, int fallback)
        {
            Uri uri;
            return Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri) && uri.Port > 0
                ? uri.Port
                : fallback;
        }

        /// <summary>
        /// URL에서 호스트를 추출합니다 (실패 시 기본값).
        /// IPv6 리터럴("[::1]")은 대괄호를 벗겨 바인딩 주소 형식으로 돌려줍니다.
        /// </summary>
        private static string ExtractHost(string url, string fallback)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri) ||
                string.IsNullOrEmpty(uri.Host))
            {
                return fallback;
            }

            return uri.Host.Trim('[', ']');
        }

        // ──────────────────────────── Python 탐지 ────────────────────────────

        /// <summary>현재 에디터가 Windows에서 동작 중인지 여부입니다.</summary>
        private static bool IsWindows
        {
            get { return UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor; }
        }

        /// <summary>경로 구분자를 포함하는지(=명령어 이름이 아니라 경로인지) 확인합니다.</summary>
        private static bool ContainsPathSeparator(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0);
        }

        /// <summary>후보 목록(실행 파일 → 인자 접두사)을 탐지 우선순위대로 만듭니다.</summary>
        private static List<KeyValuePair<string, string>> BuildPythonCandidates(string configured)
        {
            var candidates = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 설정값
            if (!string.IsNullOrEmpty(configured))
            {
                if (ContainsPathSeparator(configured))
                {
                    if (File.Exists(configured))
                    {
                        AddCandidate(candidates, seen, configured, string.Empty);
                    }
                }
                else
                {
                    AddCandidate(candidates, seen, configured, string.Empty);
                }
            }

            // 2) 명령어 이름 (PATH 위임)
            if (IsWindows)
            {
                AddCandidate(candidates, seen, "py", "-3");
                AddCandidate(candidates, seen, "python", string.Empty);
                AddCandidate(candidates, seen, "python3", string.Empty);
            }
            else
            {
                AddCandidate(candidates, seen, "python3", string.Empty);
                AddCandidate(candidates, seen, "python", string.Empty);
            }

            // 3) Windows 잘 알려진 설치 위치 (환경변수 조합, 드라이브 문자 하드코딩 없음)
            if (IsWindows)
            {
                AddInstalledPythonCandidates(candidates, seen,
                    CombineSafe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"));
                AddInstalledPythonCandidates(candidates, seen, Environment.GetEnvironmentVariable("ProgramFiles"));
                AddInstalledPythonCandidates(candidates, seen, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
                AddInstalledPythonCandidates(candidates, seen, DriveRoot(Environment.GetEnvironmentVariable("SystemDrive")));
            }

            // 4) Anaconda/Miniconda 설치본
            //    conda 설치 관리자는 PATH 등록이 기본 해제이고 py 런처도 conda 설치본을 인식하지 못하므로,
            //    conda만 설치된 환경에서는 위 1~3번이 모두 탈락한다.
            AddCondaCandidates(candidates, seen);

            // 5) PATH 환경변수 직접 파싱 → 절대 경로 후보
            AddPathEnvironmentCandidates(candidates, seen);

            // 6) Windows 외 표준 경로
            if (!IsWindows)
            {
                AddFileCandidate(candidates, seen, "/usr/bin/python3");
                AddFileCandidate(candidates, seen, "/usr/local/bin/python3");
                AddFileCandidate(candidates, seen, "/opt/homebrew/bin/python3");
            }

            return candidates;
        }

        /// <summary>중복을 걸러 후보를 추가합니다.</summary>
        private static void AddCandidate(List<KeyValuePair<string, string>> candidates, HashSet<string> seen, string fileName, string argumentPrefix)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            string key = fileName + "|" + argumentPrefix;
            if (seen.Add(key))
            {
                candidates.Add(new KeyValuePair<string, string>(fileName, argumentPrefix));
            }
        }

        /// <summary>파일이 존재할 때만 후보로 추가합니다.</summary>
        private static void AddFileCandidate(List<KeyValuePair<string, string>> candidates, HashSet<string> seen, string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                AddCandidate(candidates, seen, path, string.Empty);
            }
        }

        /// <summary>
        /// 지정한 상위 폴더에서 "Python3*" 하위 폴더를 글로빙해 python.exe 후보를 추가합니다.
        /// (상위 폴더가 없으면 조용히 건너뜁니다. 최신 버전 폴더가 앞에 오도록 내림차순.)
        /// </summary>
        private static void AddInstalledPythonCandidates(List<KeyValuePair<string, string>> candidates, HashSet<string> seen, string parentDirectory)
        {
            if (string.IsNullOrEmpty(parentDirectory) || !Directory.Exists(parentDirectory))
            {
                return;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(parentDirectory, "Python3*");
            }
            catch (Exception)
            {
                // 접근 권한 없음 등 — 조용히 건너뜀
                return;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int i = directories.Length - 1; i >= 0; i--)
            {
                AddFileCandidate(candidates, seen, Path.Combine(directories[i], "python.exe"));
            }
        }

        /// <summary>
        /// Anaconda/Miniconda/Miniforge 설치 루트에서 python 실행 파일을 후보로 추가합니다.
        /// conda 설치 관리자는 PATH 등록을 권장하지 않아 기본 해제이고, Windows의 py 런처도
        /// conda 설치본을 인식하지 못하므로 conda만 설치된 환경에서는 이 탐색이 유일한 경로입니다.
        /// 활성 환경(CONDA_PREFIX)·conda 실행 파일 위치(CONDA_EXE) → 기본 설치 폴더 순으로 base 환경을 먼저 보고,
        /// base가 3.7 미만이거나 없을 때를 대비해 각 루트의 <c>envs/*</c>를 뒤이어 추가합니다.
        /// (드라이브 문자·사용자명 하드코딩 없이 환경변수와 특수 폴더 경로만 사용합니다.)
        /// </summary>
        private static void AddCondaCandidates(List<KeyValuePair<string, string>> candidates, HashSet<string> seen)
        {
            var roots = new List<string>();
            var rootSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // (1) 지금 활성화된 conda 환경과 conda 실행 파일 위치에서 역산한 설치 루트.
            //     Unity를 conda 셸에서 실행한 경우 이 둘만으로 바로 찾을 수 있다.
            AddCondaRoot(roots, rootSeen, Environment.GetEnvironmentVariable("CONDA_PREFIX"));
            AddCondaRoot(roots, rootSeen, GrandparentDirectory(Environment.GetEnvironmentVariable("CONDA_EXE")));

            // (2) 설치 관리자가 기본으로 제안하는 폴더들.
            foreach (string parent in CondaParentDirectories())
            {
                foreach (string name in CondaRootNames)
                {
                    AddCondaRoot(roots, rootSeen, CombineSafe(parent, name));
                }
            }

            // (3) base 환경을 먼저 전부 추가한 뒤 envs 하위 환경을 추가한다 (base 우선).
            foreach (string root in roots)
            {
                AddFileCandidate(candidates, seen, CondaPythonPath(root));
            }

            foreach (string root in roots)
            {
                string envsDirectory = CombineSafe(root, "envs");
                if (string.IsNullOrEmpty(envsDirectory) || !Directory.Exists(envsDirectory))
                {
                    continue;
                }

                string[] environments;
                try
                {
                    environments = Directory.GetDirectories(envsDirectory);
                }
                catch (Exception)
                {
                    // 접근 권한 없음 등 — 조용히 건너뜀
                    continue;
                }

                Array.Sort(environments, StringComparer.OrdinalIgnoreCase);
                foreach (string environment in environments)
                {
                    AddFileCandidate(candidates, seen, CondaPythonPath(environment));
                }
            }
        }

        /// <summary>conda 설치 폴더에 흔히 쓰이는 이름들입니다 (대소문자가 다른 구버전 설치 포함).</summary>
        private static readonly string[] CondaRootNames =
        {
            "anaconda3", "miniconda3", "miniforge3", "mambaforge",
            "Anaconda3", "Miniconda3", "Miniforge3"
        };

        /// <summary>conda 설치 루트가 놓이는 상위 폴더들을 플랫폼별로 돌려줍니다.</summary>
        private static IEnumerable<string> CondaParentDirectories()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!IsWindows)
            {
                return new[] { userProfile, "/opt" };
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                userProfile,                                        // 기본값: %USERPROFILE%\anaconda3
                localAppData,                                       // "Just Me" 설치 일부 버전
                CombineSafe(localAppData, "Continuum"),             // 구버전 Anaconda
                Environment.GetEnvironmentVariable("ProgramData"),  // "All Users" 설치
                Environment.GetEnvironmentVariable("ProgramFiles"),
                DriveRoot(Environment.GetEnvironmentVariable("SystemDrive"))
            };
        }

        /// <summary>conda 환경 폴더 안의 python 실행 파일 경로를 만듭니다 (Windows는 루트 바로 아래, 그 외는 bin/).</summary>
        private static string CondaPythonPath(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return string.Empty;
            }

            return IsWindows
                ? CombineSafe(root, "python.exe")
                : CombineSafe(CombineSafe(root, "bin"), "python3");
        }

        /// <summary>실제로 존재하는 conda 루트만 중복 없이 목록에 추가합니다.</summary>
        private static void AddCondaRoot(List<string> roots, HashSet<string> rootSeen, string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }

            if (rootSeen.Add(root.TrimEnd('/', '\\')))
            {
                roots.Add(root);
            }
        }

        /// <summary>"&lt;루트&gt;/Scripts/conda.exe"처럼 두 단계 아래 있는 파일에서 설치 루트를 역산합니다.</summary>
        private static string GrandparentDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            try
            {
                string parent = Path.GetDirectoryName(filePath);
                return string.IsNullOrEmpty(parent) ? string.Empty : (Path.GetDirectoryName(parent) ?? string.Empty);
            }
            catch (ArgumentException)
            {
                // 잘못된 문자가 섞인 환경변수 값 — 건너뜀
                return string.Empty;
            }
        }

        /// <summary>PATH 환경변수의 각 디렉터리에서 python 실행 파일을 찾아 절대 경로 후보로 추가합니다.</summary>
        private static void AddPathEnvironmentCandidates(List<KeyValuePair<string, string>> candidates, HashSet<string> seen)
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return;
            }

            string[] fileNames = IsWindows
                ? new[] { "python.exe", "python3.exe" }
                : new[] { "python3", "python" };

            foreach (string rawDirectory in pathVariable.Split(Path.PathSeparator))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }

                foreach (string fileName in fileNames)
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.Combine(directory, fileName);
                    }
                    catch (ArgumentException)
                    {
                        // PATH에 잘못된 문자가 섞인 항목 — 건너뜀
                        break;
                    }

                    AddFileCandidate(candidates, seen, fullPath);
                }
            }
        }

        /// <summary>두 조각을 이어 경로를 만듭니다 (조각이 비었거나 잘못된 문자가 섞였으면 빈 문자열).</summary>
        private static string CombineSafe(string root, string child)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(child))
            {
                return string.Empty;
            }

            try
            {
                return Path.Combine(root, child);
            }
            catch (ArgumentException)
            {
                // 환경변수에 잘못된 경로 문자가 섞인 경우 — 건너뜀
                return string.Empty;
            }
        }

        /// <summary>여러 조각을 이어 경로를 만듭니다 (첫 조각이 비어 있으면 빈 문자열).</summary>
        private static string CombineSafe(string root, string first, string second)
        {
            if (string.IsNullOrEmpty(root))
            {
                return string.Empty;
            }

            return Path.Combine(Path.Combine(root, first), second);
        }

        /// <summary>"C:" 형태의 SystemDrive 값을 루트 경로("C:\")로 보정합니다.</summary>
        private static string DriveRoot(string systemDrive)
        {
            if (string.IsNullOrEmpty(systemDrive))
            {
                return string.Empty;
            }

            return systemDrive.EndsWith(":", StringComparison.Ordinal)
                ? systemDrive + Path.DirectorySeparatorChar
                : systemDrive;
        }

        /// <summary>안내 메시지에 표시할 후보 설명 문자열을 만듭니다.</summary>
        private static string DescribeCandidate(KeyValuePair<string, string> candidate)
        {
            return string.IsNullOrEmpty(candidate.Value)
                ? $"\"{candidate.Key}\""
                : $"\"{candidate.Key} {candidate.Value}\"";
        }

        /// <summary>
        /// 후보를 실제로 실행해 Python 3.7 이상인지 검증합니다.
        /// (스토어 앱 실행 별칭 스텁·Python 2·다른 런타임은 이 검증에서 탈락합니다.)
        /// </summary>
        private static bool VerifyPython(string fileName, string argumentPrefix, out string version, out string executablePath, out string failure)
        {
            version = string.Empty;
            executablePath = string.Empty;
            failure = string.Empty;

            string arguments = string.IsNullOrEmpty(argumentPrefix)
                ? VersionProbeArguments
                : argumentPrefix + " " + VersionProbeArguments;

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        failure = "프로세스를 시작하지 못했습니다.";
                        return false;
                    }

                    // 출력이 몇 바이트뿐이라 파이프가 가득 찰 일이 없어 먼저 종료를 기다려도 안전하다.
                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                            // 이미 종료된 경우 무시
                        }

                        failure = "5초 안에 응답하지 않아 중단했습니다 (Microsoft Store 앱 실행 별칭일 수 있습니다).";
                        return false;
                    }

                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();

                    if (process.ExitCode != 0)
                    {
                        failure = $"종료 코드 {process.ExitCode}. {FirstLine(standardError)}".TrimEnd();
                        return false;
                    }

                    string[] lines = standardOutput.Replace("\r\n", "\n").Split('\n');
                    int major;
                    int minor;
                    if (lines.Length < 2 ||
                        !int.TryParse(lines[0].Trim(), out major) ||
                        !int.TryParse(lines[1].Trim(), out minor))
                    {
                        failure = "Python 버전 출력을 해석하지 못했습니다 (Python이 아닌 실행 파일일 수 있습니다).";
                        return false;
                    }

                    if (major != MinimumPythonMajor || minor < MinimumPythonMinor)
                    {
                        failure = $"Python {major}.{minor} — 3.7 이상이 필요합니다.";
                        return false;
                    }

                    version = $"{major}.{minor}";
                    executablePath = lines.Length >= 3 ? lines[2].Trim() : string.Empty;
                    return true;
                }
            }
            catch (Exception e)
            {
                failure = e.Message;
                return false;
            }
        }

        /// <summary>문자열의 첫 줄만 돌려줍니다 (안내 메시지를 짧게 유지).</summary>
        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string[] lines = value.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                if (!string.IsNullOrEmpty(line.Trim()))
                {
                    return line.Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>지정 시간만큼 기다린 뒤 프로세스가 이미 종료되었는지 확인합니다.</summary>
        private static bool WaitedAndExited(Process process, int milliseconds)
        {
            try
            {
                return process.WaitForExit(milliseconds);
            }
            catch (Exception)
            {
                // 권한 등으로 확인 불가 — 정상 실행 중으로 간주
                return false;
            }
        }

        /// <summary>브리지 서버가 시작 직후 종료됐을 때의 원인·조치 안내 메시지를 만듭니다.</summary>
        private static string BuildEarlyExitMessage(Process process, string python, int port, string logPath)
        {
            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = -1;
            }

            var message = new StringBuilder();
            message.AppendLine($"브리지 서버가 시작 직후 종료되었습니다 (종료 코드 {exitCode}).");
            message.AppendLine($"실행 대상: \"{python}\", 포트: {port}");
            message.AppendLine();
            message.AppendLine($"포트 {port}가 이미 사용 중일 수 있습니다. 이미 실행 중인 브리지 서버(콘솔 창)를 닫거나, " +
                               "설정의 브리지 서버 주소에서 다른 포트를 지정한 뒤 다시 시도해주세요.");
            message.AppendLine("Python 실행 파일이 올바른지도 확인해주세요 (Tools/MCP/Settings의 [자동 탐지] 버튼).");

            string logTail = ReadLogTail(logPath, 2000);
            if (!string.IsNullOrEmpty(logTail))
            {
                message.AppendLine();
                message.AppendLine($"로그 파일 마지막 내용 ({logPath}):");
                message.Append(logTail);
            }
            else if (!string.IsNullOrEmpty(logPath))
            {
                message.AppendLine();
                message.AppendLine($"로그 파일: {logPath} (내용 없음)");
            }
            else
            {
                message.AppendLine();
                message.AppendLine("설정의 \"브리지 콘솔 창 표시\"를 끄면 로그가 임시 폴더의 mcptools_bridge_server.log에 기록되어 원인을 확인할 수 있습니다.");
            }

            return message.ToString();
        }

        /// <summary>로그 파일의 마지막 일부(최대 maxChars 문자)를 읽습니다. 실패하면 빈 문자열.</summary>
        private static string ReadLogTail(string logPath, int maxChars)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                return string.Empty;
            }

            try
            {
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length > maxChars)
                    {
                        stream.Seek(stream.Length - maxChars, SeekOrigin.Begin);
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd().Trim();
                    }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
