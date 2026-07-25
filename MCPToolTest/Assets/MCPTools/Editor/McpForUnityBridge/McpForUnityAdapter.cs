// MCP for Unity(com.coplaydev.unity-mcp) 패키지 의존은 이 어셈블리(MCPTools.Editor.McpForUnity)에 격리한다(어댑터 패턴).
// 패키지가 설치된 프로젝트에서만 versionDefines로 MCPTOOLS_HAS_MCPFORUNITY 심볼이 정의되고,
// asmdef의 defineConstraints가 이 심볼을 요구하므로 패키지가 없는 배포 대상 프로젝트에서는
// 어셈블리 자체가 컴파일 대상에서 제외된다(참조 해석 전에 제외되므로 참조 누락 오류도 발생하지 않는다).
// 의존 방향은 McpForUnity → MCPTools.Editor 단방향이며, 본체는 이 어댑터를 호출하지 않는다.
#if MCPTOOLS_HAS_MCPFORUNITY
using System.Collections.Generic;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace MCPTools.Editor
{
    /// <summary>
    /// MCP for Unity 브리지에 MCPTools 도구를 노출하는 어댑터입니다.
    /// 파라미터(JObject)를 JSON 문자열로 변환해 <see cref="McpToolRegistry.Execute"/>에 위임하고,
    /// 공통 응답 포맷({"success","message","data"}) JSON을 그대로 반환합니다.
    /// </summary>
    internal static class McpForUnityAdapter
    {
        /// <summary>
        /// 레지스트리 도구를 실행하고 결과 JSON을 MCP 응답 객체(JObject)로 반환합니다.
        /// </summary>
        /// <param name="toolName">McpToolRegistry에 등록된 도구 이름.</param>
        /// <param name="params">MCP 클라이언트가 전달한 파라미터 (null 허용).</param>
        internal static object Handle(string toolName, JObject @params)
        {
            string paramsJson = @params != null ? @params.ToString() : "{}";
            string resultJson = McpToolRegistry.Execute(toolName, paramsJson);
            return JObject.Parse(resultJson);
        }
    }

    /// <summary>
    /// MCP Tools 연결 진단 도구(mcptools_ping)를 MCP for Unity에 노출합니다.
    /// 버전·Unity 버전·ComfyUI 서버 주소를 반환합니다.
    /// </summary>
    [McpForUnityTool("mcptools_ping",
        Description = "MCP Tools 연결 진단용 도구. 버전, Unity 버전, ComfyUI 서버 주소를 반환합니다.")]
    public static class McpToolsPingTool
    {
        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (사용하지 않음).</param>
        /// <returns>{"success":bool,"message":string,"data":{...}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_ping", @params);
        }
    }

    /// <summary>
    /// 1단계 분석 입력 수집 도구(mcptools_asset_scan)를 MCP for Unity에 노출합니다.
    /// 기획서 원문·프리팹 슬롯 스캔 결과·항목 스키마·작성 지침을 반환하며, AI가 이를 분석해 목록을 작성합니다.
    /// </summary>
    [McpForUnityTool("mcptools_asset_scan",
        Description = "1단계 에셋 리스트업 분석 입력 수집. 기획서 원문, 프리팹 슬롯 스캔 결과, itemSchema, " +
                      "instructions를 반환하며 AI가 분석해 목록 작성 후 mcptools_asset_list_save로 저장합니다. " +
                      "파라미터: designDocPath(선택), scanRootPath(기본 Assets), " +
                      "scenePaths(선택, 씬 경로 배열 — 지정 시 해당 씬의 직접 배치 오브젝트 슬롯도 스캔), " +
                      "scanOnly(선택, 기본 false — true면 기획서 항목 추출 없이 열린 씬+포함 프리팹만 스캔해 완성 items를 반환. " +
                      "designDocPath는 이때도 컨텍스트로 기록·반환됨).")]
    public static class McpToolsAssetScanTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>기획서 파일 경로 (Assets/ 기준 상대 경로).</summary>
            [ToolParameter("기획서 파일 경로 (Assets/ 기준 상대 경로). 생략하면 설정의 기본 경로를 사용합니다.", Required = false)]
            public string designDocPath { get; set; }

            /// <summary>프리팹 슬롯 스캔 루트 경로.</summary>
            [ToolParameter("프리팹 슬롯 스캔 루트 경로.", Required = false, DefaultValue = "Assets")]
            public string scanRootPath { get; set; }

            /// <summary>씬 직접 배치 오브젝트 슬롯을 스캔할 씬 경로 목록.</summary>
            [ToolParameter("스캔할 씬 경로 목록 (Assets/ 기준 상대 경로 문자열 배열, 예: [\"Assets/Scenes/Main.unity\"]). " +
                           "지정한 씬에 직접 배치된(프리팹 인스턴스가 아닌) 오브젝트의 슬롯을 scanEntries에 포함합니다.",
                Required = false)]
            public List<string> scenePaths { get; set; }

            /// <summary>기획서 항목 추출 없이 열린 씬+포함 프리팹만 스캔하는 모드 여부 (기본 false).</summary>
            [ToolParameter("true면 기획서에서 항목을 추출하지 않고 현재 열린 씬과 그 씬에 포함된 프리팹만 스캔해, " +
                           "대상 경로가 채워진 완성 items를 함께 반환합니다 (mcptools_asset_list_save에 그대로 전달 가능). " +
                           "designDocPath는 이때도 컨텍스트로 기록·반환됩니다. 기본 false.",
                Required = false)]
            public bool scanOnly { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (designDocPath, scanRootPath).</param>
        /// <returns>{"success":bool,"message":string,"data":{designDocText,scanEntries,itemSchema,instructions}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_asset_scan", @params);
        }
    }

    /// <summary>
    /// 1단계 목록 저장 도구(mcptools_asset_list_save)를 MCP for Unity에 노출합니다.
    /// AI가 작성한 items 배열을 검증 후 에셋 목록 JSON으로 저장합니다.
    /// </summary>
    [McpForUnityTool("mcptools_asset_list_save",
        Description = "AI가 작성한 에셋 목록(items 배열, itemSchema 형식)을 검증하고 목록 JSON으로 저장합니다. " +
                      "경고가 있어도 저장은 수행되며 warnings로 반환됩니다. " +
                      "파라미터: items(필수, 객체 배열), outputPath(선택).")]
    public static class McpToolsAssetListSaveTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>저장할 에셋 목록 항목 배열 (itemSchema 형식의 객체 배열).</summary>
            [ToolParameter("저장할 에셋 목록 항목 배열. mcptools_asset_scan이 반환한 itemSchema 형식의 객체 배열.", Required = true)]
            public List<object> items { get; set; }

            /// <summary>목록 JSON 저장 경로 (Assets/ 기준 상대 경로).</summary>
            [ToolParameter("목록 JSON 저장 경로 (Assets/ 기준 상대 경로). 생략하면 기본 경로에 저장합니다.", Required = false)]
            public string outputPath { get; set; }

            /// <summary>목록 문서에 기록할 기획서 경로.</summary>
            [ToolParameter("목록 문서에 기록할 기획서 경로 (Assets/ 기준 상대 경로).", Required = false)]
            public string designDocPath { get; set; }

            /// <summary>목록 문서에 기록할 스캔 루트 경로.</summary>
            [ToolParameter("목록 문서에 기록할 스캔 루트 경로.", Required = false)]
            public string scanRootPath { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (items, outputPath).</param>
        /// <returns>{"success":bool,"message":string,"data":{outputPath,itemCount,warnings}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_asset_list_save", @params);
        }
    }

    /// <summary>
    /// 2단계 프롬프트 재료 수집 도구(mcptools_prompt_scan)를 MCP for Unity에 노출합니다.
    /// 1단계 목록 항목·템플릿·프롬프트 스키마·작성 지침을 반환하며, AI가 이를 참고해 프롬프트를 작성합니다.
    /// </summary>
    [McpForUnityTool("mcptools_prompt_scan",
        Description = "2단계 프롬프트 제작 입력 수집. 1단계 에셋 목록 항목(assetItems), 프롬프트 템플릿(template), " +
                      "promptSchema, instructions를 반환하며 AI가 항목별 positive/negative 프롬프트 작성 후 " +
                      "mcptools_prompt_save로 저장합니다. " +
                      "파라미터: assetListPath(필수, Assets/ 상대 AssetList JSON), templateName(선택, 기본 default).")]
    public static class McpToolsPromptScanTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>1단계 산출물 AssetList JSON 경로.</summary>
            [ToolParameter("1단계 산출물 AssetList JSON 경로 (Assets/ 기준 상대 경로, 예: Assets/Docs/1_AssetList/AssetList_20260721_1200.json).", Required = true)]
            public string assetListPath { get; set; }

            /// <summary>프롬프트 템플릿 이름.</summary>
            [ToolParameter("프롬프트 템플릿 이름. 생략하면 기본 템플릿(default)을 사용합니다.", Required = false, DefaultValue = "default")]
            public string templateName { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (assetListPath, templateName).</param>
        /// <returns>{"success":bool,"message":string,"data":{assetItems,template,promptSchema,instructions}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_prompt_scan", @params);
        }
    }

    /// <summary>
    /// 2단계 프롬프트 저장 도구(mcptools_prompt_save)를 MCP for Unity에 노출합니다.
    /// AI가 작성한 items 배열을 검증 후 PromptSet JSON으로 저장합니다.
    /// </summary>
    [McpForUnityTool("mcptools_prompt_save",
        Description = "AI가 작성한 프롬프트 목록(items 배열, promptSchema 형식)을 검증하고 PromptSet JSON으로 저장합니다. " +
                      "경고(빈 프롬프트 등)가 있어도 저장은 수행되며 warnings로 반환됩니다. " +
                      "파라미터: items(필수, 객체 배열), assetListPath(선택), outputPath(선택).")]
    public static class McpToolsPromptSaveTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>저장할 프롬프트 항목 배열 (promptSchema 형식의 객체 배열).</summary>
            [ToolParameter("저장할 프롬프트 항목 배열. mcptools_prompt_scan이 반환한 promptSchema 형식의 객체 배열.", Required = true)]
            public List<object> items { get; set; }

            /// <summary>문서 메타에 기록할 1단계 AssetList JSON 경로.</summary>
            [ToolParameter("문서 메타에 기록할 1단계 AssetList JSON 경로 (Assets/ 기준 상대 경로).", Required = false)]
            public string assetListPath { get; set; }

            /// <summary>PromptSet JSON 저장 경로 (Assets/ 기준 상대 경로).</summary>
            [ToolParameter("PromptSet JSON 저장 경로 (Assets/ 기준 상대 경로). 생략하면 기본 경로(Assets/Docs/2_PromptSet/PromptSet_{시각}.json)에 저장합니다.", Required = false)]
            public string outputPath { get; set; }

            /// <summary>문서 메타에 기록할 템플릿 이름.</summary>
            [ToolParameter("문서 메타에 기록할 프롬프트 템플릿 이름.", Required = false)]
            public string templateName { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (items, assetListPath, outputPath, templateName).</param>
        /// <returns>{"success":bool,"message":string,"data":{outputPath,itemCount,warnings}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_prompt_save", @params);
        }
    }

    /// <summary>
    /// 3단계 후보 생성 도구(mcptools_generate_candidates)를 MCP for Unity에 노출합니다.
    /// 생성 Job을 시작하고 즉시 반환하며, 완료 여부는 mcptools_list_candidates로 폴링합니다.
    /// </summary>
    [McpForUnityTool("mcptools_generate_candidates",
        Description = "3단계 후보 생성 Job을 브리지 서버 경유로 시작합니다 (비동기, 완료까지 기다리지 않음). 기준 시드부터 " +
                      "+1씩 증가시키며 후보를 생성해 Assets/Generated/3_Candidates/{assetItemId}/에 저장합니다. " +
                      "완료 여부와 결과는 mcptools_list_candidates로 폴링하세요. " +
                      "파라미터: promptSetPath(필수), assetItemId(필수), " +
                      "workflowName(선택: GenerateImage|GenerateImageFlux|UI|StyleChange|Audio), " +
                      "variables(선택, {\"nodeId.field\": 값} 객체), baseSeed(선택).")]
    public static class McpToolsGenerateCandidatesTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>2단계 산출물 PromptSet JSON 경로.</summary>
            [ToolParameter("2단계 산출물 PromptSet JSON 경로 (Assets/ 기준 상대 경로).", Required = true)]
            public string promptSetPath { get; set; }

            /// <summary>후보를 생성할 항목 id.</summary>
            [ToolParameter("후보를 생성할 항목 id (PromptSet items[].id).", Required = true)]
            public string assetItemId { get; set; }

            /// <summary>사용할 워크플로 이름.</summary>
            [ToolParameter("워크플로 이름 (GenerateImage | GenerateImageFlux | UI | StyleChange | Audio). 생략하면 항목 종류별 기본 워크플로를 사용합니다.", Required = false)]
            public string workflowName { get; set; }

            /// <summary>워크플로 노드 inputs에 덮어쓸 변수 맵.</summary>
            [ToolParameter("워크플로 변수 덮어쓰기 객체 ({\"nodeId.field\": 값}, 예: {\"5.steps\": 20, \"6.width\": 512}). " +
                           "사용 가능한 변수는 브리지 서버 variables.json 매니페스트를 따릅니다.", Required = false)]
            public JObject variables { get; set; }

            /// <summary>기준 시드.</summary>
            [ToolParameter("기준 시드. 생략하면 무작위로 생성합니다.", Required = false)]
            public long? baseSeed { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (promptSetPath, assetItemId, workflowName, baseSeed).</param>
        /// <returns>{"success":bool,"message":string,"data":{status,assetItemId,candidateFolder}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_generate_candidates", @params);
        }
    }

    /// <summary>
    /// 3단계 후보 조회 도구(mcptools_list_candidates)를 MCP for Unity에 노출합니다.
    /// 생성 Job 상태와 후보 목록을 반환합니다.
    /// </summary>
    [McpForUnityTool("mcptools_list_candidates",
        Description = "3단계 후보 생성 Job 상태와 후보 목록을 반환합니다. " +
                      "파라미터: assetItemId(필수). " +
                      "반환 data: { status: running|completed|failed|idle, message, candidates:[{path,seed}] }.")]
    public static class McpToolsListCandidatesTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>조회할 항목 id.</summary>
            [ToolParameter("조회할 항목 id.", Required = true)]
            public string assetItemId { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (assetItemId).</param>
        /// <returns>{"success":bool,"message":string,"data":{status,message,candidates}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_list_candidates", @params);
        }
    }

    /// <summary>
    /// 3단계 후보 확정 도구(mcptools_select_candidate)를 MCP for Unity에 노출합니다.
    /// 선택한 후보를 확정 경로로 복사하고 임포트 설정을 적용합니다.
    /// </summary>
    [McpForUnityTool("mcptools_select_candidate",
        Description = "3단계 후보 1개를 확정합니다: Assets/Generated/3_Confirmed/Images/(오디오는 Audio/)로 복사하고 " +
                      "GenerationResults.json에 기록하며, 이미지 항목은 Sprite 임포트 설정을 적용합니다. " +
                      "파라미터: assetItemId(필수), candidatePath(필수).")]
    public static class McpToolsSelectCandidateTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>확정할 항목 id.</summary>
            [ToolParameter("확정할 항목 id.", Required = true)]
            public string assetItemId { get; set; }

            /// <summary>확정할 후보 파일 경로.</summary>
            [ToolParameter("확정할 후보 파일 경로 (mcptools_list_candidates가 반환한 path).", Required = true)]
            public string candidatePath { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (assetItemId, candidatePath).</param>
        /// <returns>{"success":bool,"message":string,"data":{selectedPath}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_select_candidate", @params);
        }
    }

    /// <summary>
    /// 4단계 단건 적용 도구(mcptools_apply_asset)를 MCP for Unity에 노출합니다.
    /// AssetList 항목 1개의 확정본을 대상 프리팹의 컴포넌트에 적용합니다.
    /// </summary>
    [McpForUnityTool("mcptools_apply_asset",
        Description = "4단계 적용: AssetList 항목 1개의 확정본을 대상 프리팹(targetScenePath 지정 시 씬 직접 배치 오브젝트)의 컴포넌트" +
                      "(Image.sprite/RawImage.texture/SpriteRenderer.sprite/AudioSource.clip)에 적용합니다. " +
                      "항목(또는 파라미터)에 animatorControllerPath가 있으면 프리팹 루트에 Animator를 붙이고 컨트롤러를 함께 연결합니다. " +
                      "파라미터: assetListPath(필수), assetItemId(필수), assetPath(선택, 생략 시 확정본 자동 탐색), " +
                      "spriteName(선택), animatorControllerPath(선택).")]
    public static class McpToolsApplyAssetTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>1단계 산출물 AssetList JSON 경로.</summary>
            [ToolParameter("1단계 산출물 AssetList JSON 경로 (Assets/ 기준 상대 경로).", Required = true)]
            public string assetListPath { get; set; }

            /// <summary>적용할 항목 id.</summary>
            [ToolParameter("적용할 항목 id (AssetList items[].id).", Required = true)]
            public string assetItemId { get; set; }

            /// <summary>적용할 에셋 경로.</summary>
            [ToolParameter("적용할 에셋 경로 (Assets/ 기준 상대 경로). 생략하면 GenerationResults.json 기록과 " +
                           "Assets/Generated/3_Confirmed/Images(Audio)/{id}.* 규칙 경로에서 확정본을 자동 탐색합니다.", Required = false)]
            public string assetPath { get; set; }

            /// <summary>시트 안에서 적용할 서브 스프라이트 이름.</summary>
            [ToolParameter("(선택) 스프라이트 시트 안에서 적용할 서브 스프라이트 이름 (예: walk_03). " +
                           "생략하면 항목 값을 쓰고, 그 값도 비어 있으면 에셋 전체(없으면 시트의 첫 프레임)를 적용합니다.",
                Required = false)]
            public string spriteName { get; set; }

            /// <summary>프리팹 루트 Animator에 연결할 컨트롤러 경로.</summary>
            [ToolParameter("(선택) 프리팹 루트 Animator에 연결할 AnimatorController 경로 (Assets/ 기준 상대 경로). " +
                           "지정하면 이번 호출에 한해 항목 값을 덮어씁니다. 씬 항목에는 지원하지 않습니다.",
                Required = false)]
            public string animatorControllerPath { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (assetListPath, assetItemId, assetPath, spriteName, animatorControllerPath).</param>
        /// <returns>{"success":bool,"message":string,"data":{prefabPath,objectPath,appliedAssetPath}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_apply_asset", @params);
        }
    }

    /// <summary>
    /// 4단계 일괄 적용 도구(mcptools_apply_all)를 MCP for Unity에 노출합니다.
    /// AssetList 전체 항목을 순차 적용하고, 실패 항목은 사유와 함께 반환합니다.
    /// </summary>
    [McpForUnityTool("mcptools_apply_all",
        Description = "4단계 일괄 적용: AssetList의 모든 항목에 대해 확정본을 대상 프리팹/씬에 적용합니다 " +
                      "(씬 항목은 같은 씬끼리 묶어 한 번만 열어 처리). " +
                      "검증 실패/확정본 없는 항목은 건너뛰고 failed:[{id,reason}]로 반환합니다. " +
                      "파라미터: assetListPath(필수).")]
    public static class McpToolsApplyAllTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>1단계 산출물 AssetList JSON 경로.</summary>
            [ToolParameter("1단계 산출물 AssetList JSON 경로 (Assets/ 기준 상대 경로).", Required = true)]
            public string assetListPath { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (assetListPath).</param>
        /// <returns>{"success":bool,"message":string,"data":{applied,failed}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_apply_all", @params);
        }
    }

    /// <summary>
    /// 파이프라인 후반부 자동화 도구(mcptools_run_pipeline)를 MCP for Unity에 노출합니다.
    /// PromptSet(2단계 산출물)부터 3단계 생성 → (autoSelect="first"면) 확정 → 4단계 적용을 Job으로 실행하며,
    /// 호출은 즉시 status:"started"로 반환하고 진행·결과는 mcptools_status의 pipeline 블록으로 조회합니다.
    /// </summary>
    [McpForUnityTool("mcptools_run_pipeline",
        Description = "파이프라인 후반부 자동화 Job을 시작합니다 (비동기, 완료까지 기다리지 않음). PromptSet(2단계 산출물, " +
                      "AI가 이미 작성)의 각 항목에 대해 3단계 후보를 생성하고, autoSelect=\"first\"(기본)면 가장 낮은 시드 " +
                      "후보를 확정한 뒤 4단계로 대상에 일괄 적용합니다. " +
                      "autoSelect=\"none\"이면 후보만 생성하고 확정/적용은 하지 않으며, 후보 목록을 pendingSelections에 담습니다 " +
                      "(이후 mcptools_select_candidate + mcptools_apply_asset). " +
                      "1·2단계는 AI 중립 설계상 사전에 AI로 작성되어 있어야 합니다. " +
                      "파라미터: promptSetPath(필수), autoSelect(\"first\"|\"none\", 기본 \"first\"), workflowName(선택). " +
                      "반환 data: { status:\"started\", promptSetPath, assetListPath, itemCount, timeoutSeconds, statusNote }. " +
                      "진행 상황과 최종 결과(pendingSelections/applied/failed)는 mcptools_status의 pipeline 블록으로 폴링하세요. " +
                      "Job은 동시에 1개만 실행할 수 있습니다.")]
    public static class McpToolsRunPipelineTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>2단계 산출물 PromptSet JSON 경로.</summary>
            [ToolParameter("2단계 산출물 PromptSet JSON 경로 (Assets/ 기준 상대 경로).", Required = true)]
            public string promptSetPath { get; set; }

            /// <summary>후보 자동 선택 방식.</summary>
            [ToolParameter("후보 자동 선택 방식: \"first\"(각 항목 최저 시드 후보를 확정 후 적용) | \"none\"(후보만 생성). 기본 \"first\".",
                Required = false, DefaultValue = "first")]
            public string autoSelect { get; set; }

            /// <summary>사용할 워크플로 이름.</summary>
            [ToolParameter("워크플로 이름 (GenerateImage | GenerateImageFlux | UI | StyleChange | Audio). 생략하면 항목 종류별 기본 워크플로를 사용합니다.",
                Required = false)]
            public string workflowName { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (promptSetPath, autoSelect, workflowName).</param>
        /// <returns>{"success":bool,"message":string,"data":{status,promptSetPath,assetListPath,itemCount,timeoutSeconds}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_run_pipeline", @params);
        }
    }

    /// <summary>
    /// 파이프라인 진단 도구(mcptools_status)를 MCP for Unity에 노출합니다.
    /// 설정값·산출물 현황·버전과 mcptools_run_pipeline Job의 진행 상태(pipeline 블록)를 반환합니다.
    /// </summary>
    [McpForUnityTool("mcptools_status",
        Description = "MCP Tools 진단: 설정값(ComfyUI/브리지 주소, 경로, 후보 개수), 산출물 현황(AssetList/PromptSet 개수·최신 파일, " +
                      "3_Confirmed/Images·Audio 및 3_Candidates 개수, 확정 항목 수), 버전·Unity 버전, " +
                      "그리고 mcptools_run_pipeline Job의 진행 상태를 반환합니다. " +
                      "pipeline: { status: \"idle\"|\"running\"|\"completed\"|\"failed\", message, phase, promptSetPath, assetListPath, " +
                      "itemCount, currentIndex, currentItemId, elapsedSeconds, pendingSelections, applied, failed } — " +
                      "run_pipeline의 완료 여부와 결과를 이 블록으로 폴링하세요. " +
                      "서버 실시간 연결 확인은 하지 않습니다(3단계 창/브리지 /health 참조). 파라미터: 없음.")]
    public static class McpToolsStatusTool
    {
        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (사용하지 않음).</param>
        /// <returns>{"success":bool,"message":string,"data":{version,unityVersion,config,outputs,pipeline,serverHealthNote}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_status", @params);
        }
    }

    /// <summary>
    /// 스프라이트 시트 프롬프트 조립 도구(mcptools_spritesheet_build_prompt)를 MCP for Unity에 노출합니다.
    /// 멀티 행(동작별 Row) 통합 시트 프롬프트(영어)를 조립하고 JSON으로 저장합니다.
    /// </summary>
    [McpForUnityTool("mcptools_spritesheet_build_prompt",
        Description = "레퍼런스 이미지 첨부 전제의 멀티 행(동작별 Row) 통합 스프라이트 시트 프롬프트(영어)를 조립하고 " +
                      "Assets/Docs/SpriteSheetPrompt_{id}.json으로 저장합니다. 반환한 prompt를 레퍼런스 이미지와 함께 " +
                      "외부 AI(이미지 생성)에 붙여넣어 시트를 만든 뒤 mcptools_spritesheet_import로 가져옵니다. " +
                      "파라미터: rows(선택, \"walk:8,run:8,attack:8,death:10\" 형식), useReferenceImage(선택, 기본 true), " +
                      "characterDescription(선택 — useReferenceImage=false면 필수), genre(선택), artStyle(선택), notes(선택), " +
                      "cellSize(선택, 기본 256), direction(선택, right/left, 기본 right), background(선택, white/transparent, 기본 white).")]
    public static class McpToolsSpriteSheetBuildPromptTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>동작 행 목록 ("동작명:프레임수" 쉼표 나열).</summary>
            [ToolParameter("동작 행 목록 (\"walk:8,run:8,attack:8,death:10\" 형식 — 동작명:프레임수 쉼표 나열). 생략 시 기본값 동일.", Required = false)]
            public string rows { get; set; }

            /// <summary>레퍼런스 이미지 첨부 전제 여부.</summary>
            [ToolParameter("레퍼런스 이미지 첨부 전제 여부 (기본 true). false면 characterDescription이 필수.", Required = false, DefaultValue = "true")]
            public bool useReferenceImage { get; set; }

            /// <summary>보존할 캐릭터 특징 서술.</summary>
            [ToolParameter("보존할 캐릭터 특징 서술 (useReferenceImage=false면 필수).", Required = false)]
            public string characterDescription { get; set; }

            /// <summary>게임 장르 영어 자유 텍스트.</summary>
            [ToolParameter("게임 장르 영어 자유 텍스트 (예: \"side-scrolling action\").", Required = false)]
            public string genre { get; set; }

            /// <summary>아트 스타일/분위기 영어 자유 텍스트.</summary>
            [ToolParameter("아트 스타일/분위기 영어 자유 텍스트 (예: \"SD chibi, dark fantasy\").", Required = false)]
            public string artStyle { get; set; }

            /// <summary>추가 참고 사항.</summary>
            [ToolParameter("추가 참고 사항 (Important requirements에 부가 지시로 반영).", Required = false)]
            public string notes { get; set; }

            /// <summary>셀 크기(px).</summary>
            [ToolParameter("셀 크기(px). 기본 256.", Required = false, DefaultValue = "256")]
            public int cellSize { get; set; }

            /// <summary>캐릭터 방향.</summary>
            [ToolParameter("캐릭터 방향 (right/left). 기본 right.", Required = false, DefaultValue = "right")]
            public string direction { get; set; }

            /// <summary>배경.</summary>
            [ToolParameter("배경 (white/transparent). 기본 white.", Required = false, DefaultValue = "white")]
            public string background { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체.</param>
        /// <returns>{"success":bool,"message":string,"data":{prompt,savedPath,rows,background}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_spritesheet_build_prompt", @params);
        }
    }

    /// <summary>
    /// 스프라이트 시트 임포트 도구(mcptools_spritesheet_import)를 MCP for Unity에 노출합니다.
    /// 외부 AI가 만든 멀티 행 시트 png를 격자선 기준으로 슬라이스하고 Sprite Multiple을 적용합니다.
    /// </summary>
    [McpForUnityTool("mcptools_spritesheet_import",
        Description = "외부 AI가 생성한 멀티 행 스프라이트 시트 png를 격자선 기준으로 슬라이스합니다. 배경 모드가 white면 " +
                      "외곽 시드 BFS로 배경을 투명화(유채색 글로우 이펙트 보존)한 뒤 시트에 그려진 격자선을 직접 검출해 " +
                      "균일 셀 경계를 만들고 재조립 없이 원본 셀 위치 그대로 Assets/Generated/3_Confirmed/SpriteSheets/{name}_sheet.png로 저장하고 " +
                      "행 동작명 기반(walk_01~) Sprite Mode=Multiple 슬라이스를 적용합니다. 격자선이 곧 정답이므로 행/프레임 수가 " +
                      "rows와 달라도 검출된 격자 그대로 임포트하되, 자동 행 이름(rowN)은 붙이지 않습니다 — 검출 행 수보다 rows가 " +
                      "적으면 어느 행의 이름이 비었는지 알리며 실패합니다. 전경 픽셀이 거의 없어 비어 보이는 셀은 자동으로 " +
                      "제외되고, 그 결과 프레임이 하나도 남지 않은 행(여백 밴드 등)은 통째로 빠집니다. " +
                      "먼저 dryRun=true로 검출 결과를 받아 rows를 채운 뒤 다시 호출하세요. " +
                      "파라미터: imagePath(필수, 절대 또는 Assets/ 상대 png), rows(dryRun=false일 때 필수, " +
                      "\"walk:8,run:8,attack:8,death:10\" 형식 — 위 행부터 순서대로), " +
                      "dryRun(선택, 기본 false — true면 배경 제거·격자 검출까지만 하고 행 수/행별 프레임 수/셀 크기/자동 제외된 " +
                      "빈 셀 정보만 반환하며 파일 저장·슬라이스를 하지 않음), backgroundMode(선택, white/transparent, 기본 white), " +
                      "pivotMode(선택, center/bottom, 기본 center — bottom이면 피벗을 발밑에 두어 이동 애니메이션 흔들림 감소).")]
    public static class McpToolsSpriteSheetImportTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>시트 png 경로 (절대 또는 Assets/ 상대).</summary>
            [ToolParameter("시트 png 경로 (절대 경로 또는 Assets/ 기준 상대 경로).", Required = true)]
            public string imagePath { get; set; }

            /// <summary>행 정의 ("동작명:프레임수" 쉼표 나열, 위 행부터). dryRun=false일 때 필수.</summary>
            [ToolParameter("행 정의 (\"walk:8,run:8,attack:8,death:10\" 형식 — 시트의 위 행부터 순서대로). " +
                           "dryRun=false일 때 필수이며, 검출된 행 수보다 적으면 자동 이름 없이 실패합니다.", Required = false)]
            public string rows { get; set; }

            /// <summary>검출만 수행할지 여부.</summary>
            [ToolParameter("true면 배경 제거·격자 검출까지만 하고 검출 결과(행 수, 행별 프레임 수, 셀 크기, 자동 제외된 빈 셀)만 " +
                           "반환하며 파일 저장·슬라이스를 하지 않습니다. 기본 false.",
                Required = false, DefaultValue = "false")]
            public bool dryRun { get; set; }

            /// <summary>배경 모드.</summary>
            [ToolParameter("배경 모드 (white/transparent). 기본 white.", Required = false, DefaultValue = "white")]
            public string backgroundMode { get; set; }

            /// <summary>피벗 모드.</summary>
            [ToolParameter("피벗 모드 (center/bottom). 기본 center. bottom이면 각 스프라이트 피벗을 발밑(콘텐츠 수평 중앙+최하단)에 둠.",
                Required = false, DefaultValue = "center")]
            public string pivotMode { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (imagePath, rows, dryRun, backgroundMode, pivotMode).</param>
        /// <returns>
        /// {"success":bool,"message":string,"data":{assetPath,rowCount,totalFrameCount,framesPerRow,...}} 형태의 응답 객체.
        /// dryRun=true면 assetPath 없이 검출 결과(rowCount, framesPerRow, cellWidth/Height)만 담깁니다.
        /// </returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_spritesheet_import", @params);
        }
    }

    /// <summary>
    /// 스프라이트 시트 클립 생성 도구(mcptools_spritesheet_build_clips)를 MCP for Unity에 노출합니다.
    /// 슬라이스된 시트의 서브 스프라이트를 동작별로 묶어 AnimationClip을 만들고,
    /// 옵션에 따라 AnimatorController 구성 + 대상 프리팹 연결까지 수행합니다.
    /// </summary>
    [McpForUnityTool("mcptools_spritesheet_build_clips",
        Description = "슬라이스가 끝난 스프라이트 시트의 서브 스프라이트({동작}_{번호})를 동작별로 묶어 " +
                      "Assets/Generated/3_Confirmed/Animations/{시트이름}/{동작}.anim AnimationClip을 만듭니다. " +
                      "같은 경로에 클립이 있으면 새로 만들지 않고 커브·프레임 레이트·루프 설정만 덮어씁니다 " +
                      "(Animator 참조와 붙여 둔 애니메이션 이벤트 보존). createController=true면 {시트이름}.controller를 만들고 " +
                      "동작별 State를 배치하며(기존 컨트롤러는 없는 State만 추가, 트랜지션·파라미터는 유지), " +
                      "targetPrefabPath를 함께 주면 프리팹 루트에 Animator를 붙이고 컨트롤러를 할당합니다. " +
                      "파라미터: sheetPath(필수), frameRate(선택, 기본 12), targetComponent(선택, SpriteRenderer|Image), " +
                      "loopActions(선택, 루프 ON 동작명 쉼표 나열 — 미지정 시 idle/walk/run ON 기본 규칙), " +
                      "createController(선택, 기본 false), targetPrefabPath(선택), targetObjectPath(선택). " +
                      "반환 data: { sheetPath, frameRate, targetComponent, objectPath, clips:[{action,clipPath,frameCount,loop,created}], " +
                      "controllerPath, addedStates, prefabPath, prefabLinked, skipped }.")]
    public static class McpToolsSpriteSheetBuildClipsTool
    {
        /// <summary>MCP for Unity 스키마 생성용 파라미터 정의입니다(리플렉션으로 읽힘).</summary>
        public class Parameters
        {
            /// <summary>슬라이스된 시트 텍스처 경로 (Assets/ 기준 상대 경로).</summary>
            [ToolParameter("슬라이스가 끝난 시트 텍스처 경로 (Assets/ 기준 상대 경로, 예: " +
                           "Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png).", Required = true)]
            public string sheetPath { get; set; }

            /// <summary>클립 프레임 레이트(fps).</summary>
            [ToolParameter("클립 프레임 레이트(fps). 키 시간은 프레임 인덱스/frameRate로 배치됩니다. 기본 12.",
                Required = false, DefaultValue = "12")]
            public int frameRate { get; set; }

            /// <summary>스프라이트 커브 대상 컴포넌트.</summary>
            [ToolParameter("스프라이트 커브 대상 컴포넌트 (SpriteRenderer | Image). 기본 SpriteRenderer. " +
                           "Image는 uGUI 패키지(com.unity.ugui)가 설치되어 있어야 합니다.",
                Required = false, DefaultValue = "SpriteRenderer")]
            public string targetComponent { get; set; }

            /// <summary>루프 ON으로 둘 동작명 목록 (쉼표 구분).</summary>
            [ToolParameter("루프 재생으로 둘 동작명 쉼표 나열 (예: \"idle,walk,run\"). 지정하면 목록에 없는 동작은 루프 OFF가 되고, " +
                           "생략하면 기본 규칙(idle/walk/run ON, 그 외 OFF)을 사용합니다.", Required = false)]
            public string loopActions { get; set; }

            /// <summary>AnimatorController 생성 여부.</summary>
            [ToolParameter("true면 {시트이름}.controller를 만들고 동작별 State를 배치합니다. 기본 false(클립만 생성).",
                Required = false, DefaultValue = "false")]
            public bool createController { get; set; }

            /// <summary>Animator를 연결할 프리팹 경로.</summary>
            [ToolParameter("Animator + 컨트롤러를 연결할 프리팹 경로 (Assets/ 기준 상대 경로). " +
                           "createController=true일 때만 사용됩니다.", Required = false)]
            public string targetPrefabPath { get; set; }

            /// <summary>스프라이트 컴포넌트가 있는 오브젝트의 프리팹 루트 기준 경로.</summary>
            [ToolParameter("스프라이트 컴포넌트가 있는 오브젝트의 프리팹 루트 기준 계층 경로 (예: \"Body/Sprite\"). " +
                           "클립 커브 경로로도 사용되며, 생략하면 루트 자신을 대상으로 합니다.", Required = false)]
            public string targetObjectPath { get; set; }
        }

        /// <summary>MCP for Unity 브리지가 리플렉션으로 호출하는 핸들러입니다.</summary>
        /// <param name="params">파라미터 JSON 객체 (sheetPath, frameRate, targetComponent, loopActions, createController, targetPrefabPath, targetObjectPath).</param>
        /// <returns>{"success":bool,"message":string,"data":{clips,controllerPath,prefabLinked,skipped,...}} 형태의 응답 객체.</returns>
        public static object HandleCommand(JObject @params)
        {
            return McpForUnityAdapter.Handle("mcptools_spritesheet_build_clips", @params);
        }
    }
}
#endif
