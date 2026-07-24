using System.Collections.Generic;
using System.Text;

namespace MCPTools.Editor
{
    /// <summary>
    /// 프롬프트 작성을 AI에 위임하기 위한 스키마/지침/프롬프트 생성 유틸리티입니다.
    /// MCP 도구(mcptools_prompt_scan)와 프롬프트 빌더 창의 [AI용 프롬프트 복사]가 같은 내용을 공유합니다.
    /// </summary>
    public static class PromptSetPromptBuilder
    {
        /// <summary>
        /// AI가 프롬프트 작성 시 따라야 할 항목 필드 스키마(필드명 → 한국어 설명)를 반환합니다.
        /// </summary>
        public static Dictionary<string, object> GetPromptSchema()
        {
            return new Dictionary<string, object>
            {
                { "id", "1단계 목록 항목의 id를 그대로 사용 (필수, 예: \"item_001\")" },
                { "name", "에셋 이름 (1단계 목록의 name 그대로)" },
                { "assetType", "에셋 종류 (1단계 목록 그대로): \"image\" | \"ui\" | \"audio\"" },
                { "isUI", "UI 여부 (true/false, 1단계 목록 그대로)" },
                { "targetPrefabPath", "적용 대상 프리팹 경로 (1단계 목록 그대로, 수정 금지)" },
                { "targetObjectPath", "적용 대상 GameObject 계층 경로 (1단계 목록 그대로)" },
                { "description", "설명/용도 (1단계 목록 그대로, 참고용)" },
                { "positive", "ComfyUI positive 프롬프트 (필수, 영어 쉼표 구분 태그. 스타일 접두어 + 대상 묘사 + 품질 태그)" },
                { "negative", "ComfyUI negative 프롬프트 (영어 쉼표 구분 태그)" }
            };
        }

        /// <summary>AI에게 주는 프롬프트 작성 지침(한국어)을 반환합니다.</summary>
        /// <param name="includeFileExplorationHint">
        /// true면 "프로젝트 파일을 직접 읽을 수 있다면 대상 프리팹·스크립트를 참고해 묘사를 구체화하라"는
        /// 안내 단락을 덧붙입니다 (MCP 클라이언트/탐색 모드용). 클립보드 복사용 프롬프트는 false 유지.
        /// </param>
        public static string GetInstructions(bool includeFileExplorationHint = false)
        {
            string extra = includeFileExplorationHint
                ? "\n\nMCP 클라이언트인 당신이 프로젝트 파일을 직접 읽을 수 있다면, 각 항목의 " +
                  "targetPrefabPath 프리팹과 관련 C# 스크립트·씬을 참고해 에셋의 실제 용도·크기·맥락을 파악하고 " +
                  "positive 프롬프트의 대상 묘사를 더 구체적으로 쓰세요. 단, 어떤 파일도 수정하지 마세요."
                : string.Empty;

            return
                "당신은 Unity 게임 프로젝트의 AI 에셋 생성 파이프라인 2단계(프롬프트 제작) 담당자입니다.\n" +
                "아래 1단계 에셋 목록(assetItems)과 프롬프트 템플릿(template)을 참고해, " +
                "각 항목의 ComfyUI 생성용 positive/negative 프롬프트를 promptSchema 형식의 JSON 배열로 작성하세요.\n" +
                "규칙:\n" +
                "1. 항목 수와 id를 1단계 목록과 1:1로 유지합니다. id/name/assetType/isUI/targetPrefabPath/" +
                "targetObjectPath/description은 목록 값을 그대로 복사하고 수정하지 마세요.\n" +
                "2. positive/negative 프롬프트는 영어 쉼표 구분 태그로 씁니다 (ComfyUI/Stable Diffusion 계열 형식).\n" +
                "3. 이미지 항목: 템플릿의 stylePrefix로 시작하고, name/description을 바탕으로 대상을 구체적으로 묘사한 뒤 " +
                "qualityTags로 끝냅니다. negative는 템플릿의 commonNegative를 기반으로 항목에 맞게 보강합니다.\n" +
                "4. UI 항목(isUI=true 또는 assetType=\"ui\"): positive에 템플릿의 uiExtraTags" +
                "(clean edges, transparent background, game ui icon 등)를 반드시 포함합니다.\n" +
                "5. 오디오 항목(assetType=\"audio\"): 이미지용 태그(스타일 접두어/품질 태그/UI 태그)를 쓰지 말고, " +
                "템플릿의 audioStylePrefix를 참고해 소리의 성격(음색, 길이, 분위기, 악기/효과음 종류)을 묘사합니다. " +
                "negative는 audioNegative를 참고합니다.\n" +
                "6. description의 맥락(용도, 분위기, 색감)을 프롬프트에 최대한 반영하고, 항목 간 스타일 일관성을 유지합니다." +
                extra;
        }

        /// <summary>
        /// 에셋 목록 항목을 JSON 직렬화 가능한 딕셔너리 목록으로 변환합니다 (MCP scan 반환/프롬프트 재료용).
        /// </summary>
        /// <param name="items">1단계 에셋 목록 항목 (null 허용).</param>
        public static List<object> AssetItemsToDictionaries(List<AssetListItem> items)
        {
            var list = new List<object>();
            if (items == null)
            {
                return list;
            }

            foreach (AssetListItem item in items)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "id", item.id },
                    { "name", item.name },
                    { "description", item.description },
                    { "assetType", item.assetType },
                    { "targetPrefabPath", item.targetPrefabPath },
                    { "targetObjectPath", item.targetObjectPath },
                    { "isUI", item.IsUI }
                });
            }

            return list;
        }

        /// <summary>
        /// MCP 없는 AI(웹 등)에 붙여넣을 단일 프롬프트 문자열을 만듭니다.
        /// 1단계 목록 요약 + 템플릿 + promptSchema + 출력 지침(JSON 배열만 출력)을 포함합니다.
        /// </summary>
        /// <param name="list">1단계 에셋 목록 문서.</param>
        /// <param name="template">프롬프트 템플릿 (null이면 기본 템플릿).</param>
        public static string BuildPrompt(AssetListDocument list, PromptTemplate template)
        {
            return BuildPromptCore(list, template, false);
        }

        /// <summary>
        /// "프로젝트 탐색 모드" 프롬프트를 만듭니다 — 헤드리스 AI CLI를 Unity 프로젝트 루트에서
        /// 읽기 전용 도구 허용으로 실행할 때 사용합니다. 클립보드 복사용은 <see cref="BuildPrompt"/>를 사용하세요.
        /// </summary>
        /// <param name="list">1단계 에셋 목록 문서.</param>
        /// <param name="template">프롬프트 템플릿 (null이면 기본 템플릿).</param>
        public static string BuildExplorationPrompt(AssetListDocument list, PromptTemplate template)
        {
            return BuildPromptCore(list, template, true);
        }

        private static string BuildPromptCore(AssetListDocument list, PromptTemplate template, bool exploration)
        {
            if (template == null)
            {
                template = PromptTemplate.CreateDefault();
            }

            var sb = new StringBuilder();
            sb.AppendLine(GetInstructions(exploration));
            sb.AppendLine();

            if (exploration)
            {
                sb.AppendLine("## 프로젝트 탐색 (읽기 전용)");
                sb.AppendLine(
                    "현재 작업 폴더가 Unity 프로젝트 루트다. 필요하면 각 항목의 targetPrefabPath 프리팹과 " +
                    "관련 C# 스크립트·씬 파일을 직접 읽어 에셋의 실제 용도·크기·맥락을 파악하고 " +
                    "positive 프롬프트의 대상 묘사를 더 구체적으로 작성하라. " +
                    "단, 어떤 파일도 수정하거나 생성하지 말라. 최종 출력은 JSON 배열만 출력하라.");
                sb.AppendLine();
            }

            sb.AppendLine("## promptSchema (각 항목의 필드)");
            foreach (KeyValuePair<string, object> pair in GetPromptSchema())
            {
                sb.AppendLine($"- {pair.Key}: {pair.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("## 프롬프트 템플릿 (template)");
            foreach (KeyValuePair<string, object> pair in template.ToDictionary())
            {
                sb.AppendLine($"- {pair.Key}: {pair.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("## 1단계 에셋 목록 (assetItems)");
            if (list == null || list.items.Count == 0)
            {
                sb.AppendLine("(항목 없음)");
            }
            else
            {
                sb.AppendLine("id | name | assetType | isUI | targetPrefabPath | targetObjectPath | description");
                foreach (AssetListItem item in list.items)
                {
                    sb.AppendLine(
                        $"{item.id} | {item.name} | {item.assetType} | {item.IsUI} | " +
                        $"{item.targetPrefabPath} | {item.targetObjectPath} | {item.description}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 출력 형식 (중요)");
            sb.AppendLine("다른 설명이나 마크다운 코드 펜스 없이, promptSchema 형식의 객체들로 이루어진 JSON 배열 하나만 출력하세요.");
            sb.AppendLine("예: [{\"id\":\"item_001\",\"name\":\"타이틀 로고\",\"assetType\":\"ui\",\"isUI\":true," +
                          "\"targetPrefabPath\":\"Assets/...\",\"targetObjectPath\":\"...\",\"description\":\"...\"," +
                          "\"positive\":\"game ui icon, ...\",\"negative\":\"text, watermark, ...\"}]");
            return sb.ToString();
        }

        /// <summary>
        /// AI가 출력한 JSON(배열 또는 {"items":[...]} 객체)을 파싱해 프롬프트 항목 목록으로 변환합니다.
        /// 마크다운 코드 펜스(```)는 자동으로 제거합니다.
        /// </summary>
        /// <param name="json">AI 응답 JSON 문자열.</param>
        /// <returns>파싱된 항목 목록.</returns>
        /// <exception cref="System.FormatException">JSON을 항목 배열로 해석할 수 없는 경우 (한국어 안내 포함).</exception>
        public static List<PromptItem> ParseItemsJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new System.FormatException("입력이 비어 있습니다. AI가 출력한 JSON 배열을 붙여넣어 주세요.");
            }

            string trimmed = StripCodeFence(json);
            object parsed = MiniJson.Deserialize(trimmed);

            List<object> rawItems = parsed as List<object>;
            if (rawItems == null && parsed is Dictionary<string, object> obj &&
                obj.TryGetValue("items", out object inner))
            {
                rawItems = inner as List<object>;
            }

            if (rawItems == null)
            {
                throw new System.FormatException(
                    "JSON을 항목 배열로 해석할 수 없습니다. [{...}, {...}] 형태의 JSON 배열(또는 {\"items\":[...]})인지 확인해주세요.");
            }

            List<PromptItem> items = ItemsFromObjects(rawItems);
            if (items.Count == 0)
            {
                throw new System.FormatException(
                    "JSON 배열에 유효한 항목 객체가 없습니다. 각 항목은 name 또는 id 필드를 가진 객체여야 합니다.");
            }

            return items;
        }

        /// <summary>
        /// MiniJson으로 파싱된 객체 목록을 프롬프트 항목 목록으로 변환합니다 (MCP items 파라미터 처리용).
        /// </summary>
        /// <param name="rawItems">MiniJson.Deserialize 결과의 List&lt;object&gt;.</param>
        public static List<PromptItem> ItemsFromObjects(List<object> rawItems)
        {
            var items = new List<PromptItem>();
            if (rawItems == null)
            {
                return items;
            }

            foreach (object raw in rawItems)
            {
                if (!(raw is Dictionary<string, object> dict))
                {
                    continue;
                }

                PromptItem item = PromptItem.FromDictionary(dict);
                if (item != null && (!string.IsNullOrEmpty(item.name) || !string.IsNullOrEmpty(item.id)))
                {
                    items.Add(item);
                }
            }

            return items;
        }

        private static string StripCodeFence(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith("```"))
            {
                int firstNewline = trimmed.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    trimmed = trimmed.Substring(firstNewline + 1);
                }

                int fenceEnd = trimmed.LastIndexOf("```", System.StringComparison.Ordinal);
                if (fenceEnd >= 0)
                {
                    trimmed = trimmed.Substring(0, fenceEnd);
                }
            }

            return trimmed.Trim();
        }
    }
}
