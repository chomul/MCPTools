// unity-mcp(MCP for Unity, com.coplaydev.unity-mcp) 브리지 노출은 Editor/McpForUnityBridge/의
// 별도 어셈블리(MCPTools.Editor.McpForUnity)에서 처리한다. 패키지 API 의존은 그 어셈블리에만
// 격리되어 있으며(어댑터 패턴 + defineConstraints), 패키지가 없는 프로젝트에서는 어셈블리 자체가
// 컴파일 대상에서 제외된다. 다른 코드는 이 레지스트리의 Register/Execute만 사용한다.

using System;
using System.Collections.Generic;
using MCPTools.Runtime;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// MCP 도구 이름 → 핸들러 등록/실행 레지스트리입니다.
    /// unity-mcp 브리지 의존을 이 파일 하나에 격리하는 어댑터 지점입니다.
    /// </summary>
    [InitializeOnLoad]
    public static class McpToolRegistry
    {
        private sealed class ToolEntry
        {
            public string Description;
            public Func<Dictionary<string, object>, object> Handler;
        }

        private static readonly Dictionary<string, ToolEntry> Tools = new Dictionary<string, ToolEntry>();

        static McpToolRegistry()
        {
            Register(
                "mcptools_ping",
                "MCP Tools 연결 진단용 도구. 버전·Unity 버전·ComfyUI 서버 주소를 반환합니다.",
                _ =>
                {
                    var settings = MCPToolSettings.GetOrCreate();
                    return new Dictionary<string, object>
                    {
                        { "version", MCPToolsInfo.Version },
                        { "unityVersion", Application.unityVersion },
                        { "serverUrl", settings.comfyUIServerUrl }
                    };
                });
        }

        /// <summary>등록된 MCP 도구 이름 목록입니다.</summary>
        public static IReadOnlyCollection<string> ToolNames
        {
            get { return Tools.Keys; }
        }

        /// <summary>
        /// MCP 도구를 등록합니다. 같은 이름이 이미 있으면 덮어씁니다.
        /// </summary>
        /// <param name="toolName">도구 이름 (예: "mcptools_ping").</param>
        /// <param name="description">도구 설명.</param>
        /// <param name="handler">파라미터 딕셔너리를 받아 결과 객체(JSON 직렬화 가능)를 반환하는 핸들러.</param>
        /// <exception cref="ArgumentException">toolName이 비어 있는 경우.</exception>
        /// <exception cref="ArgumentNullException">handler가 null인 경우.</exception>
        public static void Register(string toolName, string description, Func<Dictionary<string, object>, object> handler)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                throw new ArgumentException("도구 이름이 비어 있습니다.", nameof(toolName));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            Tools[toolName] = new ToolEntry { Description = description, Handler = handler };
        }

        /// <summary>
        /// 등록된 도구를 실행하고 결과를 공통 포맷 JSON으로 반환합니다.
        /// 예외는 전파하지 않고 success:false 응답으로 변환합니다.
        /// </summary>
        /// <param name="toolName">실행할 도구 이름.</param>
        /// <param name="paramsJson">파라미터 JSON 객체 문자열 (null/빈 문자열이면 빈 파라미터).</param>
        /// <returns>{"success":bool,"message":string,"data":{...}} 형태의 JSON 문자열.</returns>
        public static string Execute(string toolName, string paramsJson)
        {
            try
            {
                ToolEntry entry;
                if (string.IsNullOrEmpty(toolName) || !Tools.TryGetValue(toolName, out entry))
                {
                    return MakeResult(false, $"등록되지 않은 MCP 도구입니다: \"{toolName}\"", null);
                }

                Dictionary<string, object> parameters = null;
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    parameters = MiniJson.Deserialize(paramsJson) as Dictionary<string, object>;
                    if (parameters == null && paramsJson.Trim().Length > 0 && paramsJson.Trim() != "{}")
                    {
                        return MakeResult(false, "파라미터 JSON을 객체로 파싱할 수 없습니다. JSON 객체 형식({...})인지 확인해주세요.", null);
                    }
                }

                if (parameters == null)
                {
                    parameters = new Dictionary<string, object>();
                }

                object data = entry.Handler(parameters);
                return MakeResult(true, string.Empty, data);
            }
            catch (Exception e)
            {
                return MakeResult(false, e.Message, null);
            }
        }

        private static string MakeResult(bool success, string message, object data)
        {
            var result = new Dictionary<string, object>
            {
                { "success", success },
                { "message", message ?? string.Empty },
                { "data", data ?? new Dictionary<string, object>() }
            };
            return MiniJson.Serialize(result);
        }

        /// <summary>로컬 검증용: mcptools_ping을 실행해 결과를 콘솔에 출력합니다.</summary>
        [MenuItem("Tools/MCP/Ping (Local Test)", false, 101)]
        private static void PingLocalTest()
        {
            string result = Execute("mcptools_ping", "{}");
            Debug.Log($"[MCPTools] mcptools_ping 결과: {result}");
        }
    }
}
