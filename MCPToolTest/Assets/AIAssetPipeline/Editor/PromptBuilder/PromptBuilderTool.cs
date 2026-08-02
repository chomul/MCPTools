using System.Collections.Generic;
using UnityEditor;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 2단계 프롬프트 제작 MCP 도구를 등록합니다.
    /// - aiassetpipeline_prompt_scan: 1단계 목록 항목 + 템플릿 + 프롬프트 스키마/지침 반환 (AI가 프롬프트 작성).
    /// - aiassetpipeline_prompt_save: AI가 작성한 items 배열을 검증 후 PromptSet JSON으로 저장.
    /// </summary>
    [InitializeOnLoad]
    public static class PromptBuilderTool
    {
        static PromptBuilderTool()
        {
            AIAssetPipelineToolRegistry.Register(
                "aiassetpipeline_prompt_scan",
                "2단계 프롬프트 제작의 입력 재료를 수집합니다. 1단계 에셋 목록 항목(assetItems), " +
                "프롬프트 템플릿(template), 항목 스키마(promptSchema), 작성 지침(instructions)을 반환하며, " +
                "AI가 이를 참고해 항목별 positive/negative 프롬프트를 작성한 뒤 aiassetpipeline_prompt_save로 저장합니다. " +
                "파라미터: assetListPath(필수, Assets/ 상대 AssetList JSON), templateName(선택, 기본 \"default\").",
                ExecuteScan);

            AIAssetPipelineToolRegistry.Register(
                "aiassetpipeline_prompt_save",
                "AI가 작성한 프롬프트 목록(items 배열, aiassetpipeline_prompt_scan의 promptSchema 형식)을 검증하고 " +
                "PromptSet JSON으로 저장합니다. 경고가 있어도 저장은 수행되며 warnings로 반환됩니다. " +
                "파라미터: items(필수, 객체 배열), assetListPath(선택, 문서 메타 기록용), outputPath(선택, Assets/ 상대).",
                ExecuteSave);
        }

        private static object ExecuteScan(Dictionary<string, object> parameters)
        {
            string assetListPath = GetString(parameters, "assetListPath");
            if (string.IsNullOrEmpty(assetListPath))
            {
                throw new System.ArgumentException(
                    "assetListPath 파라미터가 필요합니다 (1단계 산출물 AssetList JSON의 Assets/ 기준 상대 경로).");
            }

            AssetListDocument list = PromptBuilder.LoadAssetList(assetListPath);
            PromptTemplate template = PromptTemplate.LoadByName(GetString(parameters, "templateName"));

            return new Dictionary<string, object>
            {
                { "assetListPath", assetListPath.Replace('\\', '/') },
                { "assetItems", PromptSetPromptBuilder.AssetItemsToDictionaries(list.items) },
                { "template", template.ToDictionary() },
                { "promptSchema", PromptSetPromptBuilder.GetPromptSchema() },
                // MCP 클라이언트가 프로젝트 파일을 직접 읽을 수 있는 경우의 구체화 안내 포함
                { "instructions", PromptSetPromptBuilder.GetInstructions(true) }
            };
        }

        private static object ExecuteSave(Dictionary<string, object> parameters)
        {
            if (parameters == null || !parameters.TryGetValue("items", out object itemsObj) ||
                !(itemsObj is List<object> rawItems))
            {
                throw new System.ArgumentException(
                    "items 파라미터가 필요합니다 (aiassetpipeline_prompt_scan의 promptSchema 형식 객체 배열).");
            }

            List<PromptItem> items = PromptSetPromptBuilder.ItemsFromObjects(rawItems);
            if (items.Count == 0)
            {
                throw new System.ArgumentException("items 배열에 유효한 항목 객체가 없습니다.");
            }

            var doc = new PromptSetDocument
            {
                assetListPath = GetString(parameters, "assetListPath") ?? string.Empty,
                templateName = string.IsNullOrEmpty(GetString(parameters, "templateName"))
                    ? PromptTemplate.DefaultName
                    : GetString(parameters, "templateName"),
                createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                items = items
            };

            for (int i = 0; i < doc.items.Count; i++)
            {
                if (string.IsNullOrEmpty(doc.items[i].id))
                {
                    doc.items[i].id = $"item_{i + 1:000}";
                }
            }

            List<string> warnings = PromptBuilder.Validate(doc);
            string outputPath = GetString(parameters, "outputPath");
            string savedPath = PromptBuilder.Save(doc, string.IsNullOrEmpty(outputPath) ? null : outputPath);

            return new Dictionary<string, object>
            {
                { "outputPath", savedPath },
                { "itemCount", doc.items.Count },
                { "warnings", warnings } // 빈 프롬프트/대상 미기록 안내 (에디터 창에서 보완 가능)
            };
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }
    }
}
