using System;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// MCP Tools 파이프라인 전역 설정을 담는 ScriptableObject입니다.
    /// <see cref="GetOrCreate"/>로 로드하며, 에셋이 없으면 기본값으로 자동 생성됩니다.
    /// <see cref="InstallRoot"/>로 MCPTools 폴더의 실제 설치 위치를 알 수 있으므로
    /// 도입 프로젝트가 폴더를 어디에 두든 동작합니다.
    /// </summary>
    public class MCPToolSettings : ScriptableObject
    {
        /// <summary>설치 루트를 찾지 못했을 때 사용하는 표준 설치 위치입니다.</summary>
        private const string DefaultInstallRoot = "Assets/MCPTools";

        /// <summary>설치 루트에서 이 스크립트까지의 하위 폴더 경로입니다.</summary>
        private const string CommonFolderSuffix = "/Editor/Common";

        /// <summary>
        /// 설정 에셋을 새로 생성할 때 사용하는 고정 경로입니다.
        /// UPM 패키지 설치(Packages/)는 읽기 전용이라 설치 루트에는 에셋을 만들 수 없으므로
        /// 항상 프로젝트의 Assets 아래에 생성합니다.
        /// </summary>
        private const string UserAssetPath = "Assets/MCPTools.User/MCPToolSettings.asset";

        /// <summary>설치 루트 계산 결과 캐시 (도메인 리로드마다 재계산).</summary>
        private static string _installRoot;

        /// <summary>
        /// MCPTools 폴더의 설치 루트 경로입니다
        /// (에셋 경로 기준, 예: "Assets/MCPTools" 또는 UPM 설치 시 "Packages/&lt;패키지명&gt;").
        /// 이 스크립트 파일의 실제 위치에서 런타임에 계산하므로, 도입 프로젝트가 폴더를
        /// 다른 위치(예: "Assets/Plugins/MCPTools")에 두거나 UPM 패키지로 설치해도 동작합니다.
        /// 확인할 수 없으면 표준 위치인 "Assets/MCPTools"로 폴백합니다.
        /// </summary>
        public static string InstallRoot
        {
            get
            {
                if (string.IsNullOrEmpty(_installRoot))
                {
                    _installRoot = ResolveInstallRoot();
                }

                return _installRoot;
            }
        }

        /// <summary>
        /// 설정 에셋을 새로 생성할 때 사용하는 경로입니다 (고정: "Assets/MCPTools.User/MCPToolSettings.asset").
        /// 설치 루트가 읽기 전용 UPM 패키지(Packages/)일 수 있으므로 설치 루트와 무관하게
        /// 항상 프로젝트의 Assets 아래에 둡니다. Assets 아래에 이미 존재하는 설정 에셋은
        /// 위치와 무관하게 <see cref="GetOrCreate"/>가 찾아 사용합니다.
        /// </summary>
        public static string AssetPath
        {
            get { return UserAssetPath; }
        }

        /// <summary>ComfyUI 서버 주소입니다.</summary>
        public string comfyUIServerUrl = "http://127.0.0.1:8188";

        /// <summary>ComfyUI 요청(생성 대기 포함) 타임아웃(초)입니다.</summary>
        public int requestTimeoutSeconds = 300;

        /// <summary>브리지 서버 주소입니다 (Unity와 ComfyUI 사이의 로컬 중간 서버).</summary>
        [Tooltip("브리지 서버 주소입니다. 여기 적은 호스트·포트가 서버 시작 시 바인딩 주소로 그대로 전달되므로, " +
                 "127.0.0.1(로컬 전용) 외의 주소를 넣으면 같은 네트워크의 다른 기기도 접속할 수 있게 됩니다.")]
        public string bridgeServerUrl = "http://127.0.0.1:8189";

        /// <summary>
        /// 브리지 서버의 생성 Job 타임아웃(초)입니다. 브리지 서버 시작 인자(--job-timeout)로 전달됩니다.
        /// </summary>
        [Tooltip("후보 1건 생성의 최대 대기 시간(초)입니다. 저사양 GPU에서 타임아웃이 나면 늘려주세요. " +
                 "변경 후 브리지 서버 재시작이 필요합니다.")]
        public int jobTimeoutSeconds = 600;

        /// <summary>브리지 서버 실행에 사용할 Python 실행 파일입니다 (PATH의 python 기본).</summary>
        public string pythonExecutable = "python";

        /// <summary>기본 이미지 생성 워크플로 이름입니다 (브리지 서버 workflows/ 기준, 확장자 제외).</summary>
        public string defaultImageWorkflow = "GenerateImage";

        /// <summary>생성 결과물 루트 경로 (Assets/ 기준 상대 경로)입니다.</summary>
        public string generatedRootPath = "Assets/Generated";

        /// <summary>기획서·목록 문서 루트 경로 (Assets/ 기준 상대 경로)입니다.</summary>
        public string docsRootPath = "Assets/Docs";

        /// <summary>한 항목당 생성할 후보 이미지 개수입니다.</summary>
        public int candidateCount = 4;

        /// <summary>스프라이트 시트 임포트 시 적용할 Pixels Per Unit 값입니다.</summary>
        public int spritePixelsPerUnit = 100;

        /// <summary>
        /// 스프라이트 시트에서 AnimationClip을 만들 때 사용할 기본 프레임 레이트(fps)입니다.
        /// 클립의 키 시간은 <c>프레임 인덱스 / 이 값</c>으로 배치됩니다.
        /// </summary>
        [Tooltip("스프라이트 시트에서 만드는 AnimationClip의 기본 프레임 레이트(fps)입니다. " +
                 "값이 클수록 애니메이션이 빨라집니다.")]
        public int spriteAnimationFrameRate = 12;

        /// <summary>
        /// 브리지 서버 시작 시 콘솔 창을 표시할지 여부입니다.
        /// false(기본)면 창 없이 실행되고 로그는 시스템 임시 폴더의 mcptools_bridge_server.log에 기록됩니다.
        /// </summary>
        public bool showBridgeConsole = false;

        /// <summary>
        /// Unity 에디터를 종료할 때 이 도구로 시작한 브리지 서버 프로세스를 함께 종료할지 여부입니다.
        /// 끄면 브리지가 남아 다음 에디터 실행에서 그대로 재사용됩니다(대신 다른 프로젝트와 포트가 겹칠 수 있음).
        /// </summary>
        [Tooltip("Unity를 종료할 때 이 도구로 시작한 브리지 서버도 함께 종료합니다. " +
                 "끄면 브리지가 계속 실행된 채 남습니다.")]
        public bool shutdownBridgeOnEditorQuit = true;

        /// <summary>
        /// 생성(단건/일괄) 완료 후 브리지 /free 로 ComfyUI 모델을 언로드해 메모리를 확보할지 여부입니다.
        /// </summary>
        [Tooltip("생성 완료 후 ComfyUI에 로드된 모델을 언로드해 VRAM/메모리를 확보합니다. " +
                 "다음 생성 시 모델을 다시 로드하므로 첫 생성이 느려질 수 있습니다.")]
        public bool unloadModelsAfterBatch = true;

        /// <summary>
        /// 설정 에셋을 로드합니다. 프로젝트의 Assets 아래 어디에 있든 기존 설정 에셋을 먼저 찾아 사용하고,
        /// 없을 때만 <see cref="AssetPath"/>("Assets/MCPTools.User/")에 기본값으로 새로 생성한 뒤 저장합니다.
        /// 조회 범위를 Assets로 한정하므로 패키지(Packages/)에 동봉된 설정 에셋이
        /// 사용자 설정을 가리지 않습니다.
        /// </summary>
        /// <returns>로드되었거나 새로 생성된 <see cref="MCPToolSettings"/> 인스턴스.</returns>
        public static MCPToolSettings GetOrCreate()
        {
            // Packages/에 동봉된 읽기 전용 에셋이 사용자 설정을 가리지 않도록 Assets 범위만 조회한다.
            string[] guids = AssetDatabase.FindAssets("t:MCPToolSettings", new[] { "Assets" });
            if (guids != null && guids.Length > 0)
            {
                string foundPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                var found = AssetDatabase.LoadAssetAtPath<MCPToolSettings>(foundPath);
                if (found != null)
                {
                    if (guids.Length > 1)
                    {
                        var paths = new string[guids.Length];
                        for (int i = 0; i < guids.Length; i++)
                        {
                            paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
                        }

                        Debug.LogWarning(
                            $"[MCPTools] 설정 에셋이 {guids.Length}개 발견되어 첫 번째(\"{foundPath}\")를 사용합니다. " +
                            "설정이 엇갈리지 않도록 사용하지 않는 에셋을 삭제해주세요:\n- " +
                            string.Join("\n- ", paths));
                    }

                    return found;
                }
            }

            string assetPath = AssetPath;
            int slash = assetPath.LastIndexOf('/');
            EnsureFolder(assetPath.Substring(0, slash));

            var settings = CreateInstance<MCPToolSettings>();
            AssetDatabase.CreateAsset(settings, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MCPTools] 설정 에셋을 새로 생성했습니다: {assetPath}");
            return settings;
        }

        /// <summary>
        /// 설치 루트를 계산합니다. ① 이 스크립트 파일 경로 → ② 설정 에셋 경로(최후 폴백) → ③ 표준 위치
        /// 순으로 시도하며, 경로가 "&lt;루트&gt;/Editor/Common/..." 형태가 아니면 다음 후보로 넘어갑니다.
        /// 스크립트 경로는 Assets/ 설치든 Packages/(UPM) 설치든 실제 위치를 그대로 반환하므로 1순위로 씁니다.
        /// </summary>
        private static string ResolveInstallRoot()
        {
            // ① 이 스크립트 파일(Editor/Common/MCPToolSettings.cs) 위치에서 역산한다.
            //    Assets/ 설치든 UPM 패키지(Packages/<패키지명>/...) 설치든 실제 경로가 그대로 나온다.
            string scriptPath = string.Empty;
            var probe = CreateInstance<MCPToolSettings>();
            try
            {
                MonoScript script = MonoScript.FromScriptableObject(probe);
                if (script != null)
                {
                    scriptPath = AssetDatabase.GetAssetPath(script);
                }
            }
            finally
            {
                DestroyImmediate(probe);
            }

            string fromScript = RootFromCommonFolderPath(scriptPath);
            if (!string.IsNullOrEmpty(fromScript))
            {
                return fromScript;
            }

            // ② 최후 폴백: Assets 아래 기존 설정 에셋 경로에서 역산한다.
            //    (새 설정 에셋은 설치 루트 밖 "Assets/MCPTools.User/"에 생성되므로 역산 근거로는 부적합하지만,
            //     구버전 설치본의 "<루트>/Editor/Common/MCPToolSettings.asset" 배치를 위해 유지한다.)
            foreach (string guid in AssetDatabase.FindAssets("t:MCPToolSettings", new[] { "Assets" }))
            {
                string root = RootFromCommonFolderPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!string.IsNullOrEmpty(root))
                {
                    return root;
                }
            }

            // ③ 폴백: 표준 설치 위치
            return DefaultInstallRoot;
        }

        /// <summary>
        /// "&lt;루트&gt;/Editor/Common/&lt;파일명&gt;" 형태의 에셋 경로에서 설치 루트를 역산합니다.
        /// 예상 구조가 아니면 빈 문자열을 반환합니다.
        /// </summary>
        private static string RootFromCommonFolderPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return string.Empty;
            }

            string normalized = assetPath.Replace('\\', '/');
            int fileSlash = normalized.LastIndexOf('/');
            if (fileSlash <= 0)
            {
                return string.Empty;
            }

            string folder = normalized.Substring(0, fileSlash);
            if (!folder.EndsWith(CommonFolderSuffix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            string root = folder.Substring(0, folder.Length - CommonFolderSuffix.Length);
            return string.IsNullOrEmpty(root) ? string.Empty : root;
        }

        /// <summary>중간 폴더가 여러 단계여도 안전하도록 상위 폴더부터 재귀적으로 생성합니다.</summary>
        private static void EnsureFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            int slash = folderPath.LastIndexOf('/');
            if (slash <= 0)
            {
                return; // "Assets" 등 더 거슬러 올라갈 상위 폴더가 없음
            }

            string parent = folderPath.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folderPath.Substring(slash + 1));
        }
    }
}
