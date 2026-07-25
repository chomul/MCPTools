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

        /// <summary>
        /// Play Mode·컴파일/임포트 중에도 실행을 허용하는 도구 이름입니다 (<see cref="GetBlockedReason"/> 화이트리스트).
        ///
        /// 허용 기준: 디스크·에셋에 아무것도 쓰지 않고 async 작업도 시작하지 않는 진단/폴링 도구만 넣는다.
        /// 프로젝트를 읽기만 해도 컴파일·임포트 중에는 AssetDatabase 상태가 불안정하므로 "쓰기 없음"이
        /// 확실한 것만 허용한다.
        /// - mcptools_ping: 설정값·버전 문자열만 반환한다.
        /// - mcptools_status: 설정값과 산출물 폴더의 파일 개수를 세어 반환한다 (Directory 열거만 사용).
        ///   여기에 더해 mcptools_run_pipeline Job의 진행 상태(pipeline 블록)를 메모리에서 읽어 담을 뿐이므로
        ///   여전히 부작용이 없다. 오히려 Job이 도는 동안 에이전트가 폴링하는 경로라 반드시 허용해야 한다.
        /// - mcptools_list_candidates: 후보 폴더를 열거해 Job 상태와 후보 목록만 반환한다. 생성 Job이
        ///   도는 동안 에이전트가 폴링하는 경로이므로 Play Mode에서도 막지 않는다.
        /// 세 도구 모두 <see cref="MCPToolSettings.GetOrCreate"/>를 거치므로 설정 에셋이 아직 없는
        /// 프로젝트에서 최초 1회만 설정 에셋을 만든다(도메인 리로드를 유발하지 않는 부트스트랩).
        ///
        /// 나머지 12개 도구는 파일 저장·에셋 임포트·프리팹 저장·씬 열기·async 생성 시작 중 하나 이상을
        /// 수행하므로 화이트리스트에 넣지 않는다 (mcptools_prompt_scan은 파일 읽기만 하지만,
        /// 창의 스캔 버튼과 판정을 맞추기 위해 함께 차단한다).
        /// </summary>
        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "mcptools_ping",
            "mcptools_status",
            "mcptools_list_candidates"
        };

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
        /// 지금 도구를 실행하면 안 되는 사유(Play Mode·스크립트 컴파일·에셋 임포트)를 반환합니다.
        /// 실행해도 되는 상태이거나 <paramref name="toolName"/>이 읽기 전용 화이트리스트에 있으면 null입니다.
        /// MCP 실행 진입점(<see cref="Execute"/>)과 각 도구 창의 실행 버튼이 같은 판정을 쓰기 위한 공용 헬퍼입니다.
        /// </summary>
        /// <param name="toolName">
        /// 판정할 도구 이름. 화이트리스트 확인과 메시지 말미의 "(도구: ...)" 표기에 사용합니다.
        /// null/빈 문자열이면(창에서 호출하는 경우) 화이트리스트를 적용하지 않고 상태만 판정합니다.
        /// </param>
        /// <returns>차단 사유와 조치를 담은 메시지. 차단 상태가 아니면 null.</returns>
        public static string GetBlockedReason(string toolName = null)
        {
            if (!string.IsNullOrEmpty(toolName) && ReadOnlyTools.Contains(toolName))
            {
                return null;
            }

            string suffix = string.IsNullOrEmpty(toolName) ? string.Empty : $" (도구: {toolName})";

            // Play Mode 중 AssetDatabase.Refresh()는 도메인 리로드를 유발해 진행 중인 async 생성과
            // 플레이 세션을 함께 끊을 수 있다. 재생 진입 예약(isPlayingOrWillChangePlaymode) 상태도 함께 막는다.
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "에디터가 Play Mode입니다. 재생을 멈춘 뒤 다시 호출해주세요." + suffix;
            }

            // 컴파일·임포트 중에는 AssetDatabase 상태가 불안정하고, 곧 이어질 도메인 리로드가
            // 실행 중인 작업을 끊는다.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return "에디터가 스크립트 컴파일/에셋 임포트 중입니다. 완료된 뒤 다시 호출해주세요." + suffix;
            }

            return null;
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

                // Play Mode·컴파일/임포트 중이면 핸들러를 실행하지 않고 사유·조치를 담아 실패로 반환한다
                // (읽기 전용 도구는 ReadOnlyTools 화이트리스트로 통과).
                string blockedReason = GetBlockedReason(toolName);
                if (blockedReason != null)
                {
                    return MakeResult(false, blockedReason, null);
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
