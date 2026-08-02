using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 1단계 에셋 리스트업 MCP 도구를 등록합니다.
    /// - aiassetpipeline_asset_scan: 기획서 원문 + 프리팹 스캔 결과 + 항목 스키마/지침 반환 (AI가 분석해 목록 작성).
    /// - aiassetpipeline_asset_list_save: AI가 작성한 items 배열을 검증 후 목록 JSON으로 저장.
    /// </summary>
    [InitializeOnLoad]
    public static class AssetListupTool
    {
        static AssetListupTool()
        {
            AIAssetPipelineToolRegistry.Register(
                "aiassetpipeline_asset_scan",
                "1단계 에셋 리스트업의 분석 입력을 수집합니다. 기획서 원문과 프리팹 슬롯 스캔 결과" +
                "(Image/RawImage/SpriteRenderer/AudioSource 슬롯 + 커스텀 스크립트의 직렬화 Sprite/AudioClip " +
                "필드 슬롯 — fieldPath/fieldAssetType이 채워짐), " +
                "항목 스키마(itemSchema)와 작성 지침(instructions)을 반환하며, AI가 이를 분석해 목록을 만든 뒤 " +
                "aiassetpipeline_asset_list_save로 저장합니다. " +
                "파라미터: designDocPath(선택, Assets/ 상대 .md/.txt), scanRootPath(기본 \"Assets\"), " +
                "scenePaths(선택, Assets/ 상대 씬 경로 문자열 배열 — 지정한 씬의 직접 배치 오브젝트 슬롯도 스캔), " +
                "scanOnly(선택, 기본 false — true면 기획서에서 항목을 추출하지 않고 현재 열린 씬 슬롯과 " +
                "scanRootPath 아래 모든 프리팹 슬롯을 병합 스캔해(겹치는 프리팹은 중복 제거), 대상 경로가 채워진 " +
                "완성 items를 함께 반환. designDocPath는 이때도 컨텍스트로 기록·반환되며, items는 그대로 " +
                "aiassetpipeline_asset_list_save에 넘길 수 있음).",
                ExecuteScan);

            AIAssetPipelineToolRegistry.Register(
                "aiassetpipeline_asset_list_save",
                "AI가 작성한 에셋 목록(items 배열, aiassetpipeline_asset_scan의 itemSchema 형식)을 검증하고 " +
                "에셋 목록 JSON으로 저장합니다. 경고가 있어도 저장은 수행되며 warnings로 반환됩니다. " +
                "파라미터: items(필수, 객체 배열), outputPath(선택, Assets/ 상대).",
                ExecuteSave);
        }

        private static object ExecuteScan(Dictionary<string, object> parameters)
        {
            string designDocPath = GetString(parameters, "designDocPath");
            string scanRootPath = GetString(parameters, "scanRootPath");
            if (string.IsNullOrEmpty(scanRootPath))
            {
                scanRootPath = "Assets";
            }

            var data = new Dictionary<string, object>
            {
                { "scanRootPath", scanRootPath },
                { "itemSchema", AssetListPromptBuilder.GetItemSchema() },
                // MCP 클라이언트가 프로젝트 파일을 직접 읽을 수 있는 경우의 역할 추론 안내 포함
                { "instructions", AssetListPromptBuilder.GetInstructions(true) }
            };

            // 기획서는 scanOnly 여부와 무관하게 컨텍스트로 읽어 반환한다
            // (scanOnly 모드에서도 후속 단계의 에셋 묘사에 참고할 수 있게 함).
            if (!string.IsNullOrEmpty(designDocPath))
            {
                if (!File.Exists(designDocPath))
                {
                    throw new FileNotFoundException($"기획서 파일을 찾을 수 없습니다: {designDocPath}");
                }

                data["designDocPath"] = designDocPath.Replace('\\', '/');
                data["designDocText"] = File.ReadAllText(designDocPath);
            }

            // scanOnly: 기획서에서 항목을 추출하지 않고 현재 열린 씬 슬롯 + 스캔 루트 전체 프리팹 슬롯을
            // 병합 스캔해(겹치는 프리팹은 중복 제거), 대상 경로가 채워진 완성 items를 함께 반환한다
            // (designDocPath는 items 문서에 컨텍스트로 기록). 씬이 비어 있어도 프리팹으로 목록이 만들어진다.
            if (GetBool(parameters, "scanOnly"))
            {
                List<ScanEntry> scanOnlyEntries = ProjectScanner.ScanOpenScenesAndPrefabs(scanRootPath);
                data["scanOnly"] = true;
                data["scanEntries"] = AssetListPromptBuilder.ScanEntriesToDictionaries(scanOnlyEntries);

                AssetListDocument scanDoc =
                    AssetListBuilder.BuildFromScan(scanOnlyEntries, scanRootPath, designDocPath);
                var itemDicts = new List<object>();
                foreach (AssetListItem item in scanDoc.items)
                {
                    itemDicts.Add(item.ToDictionary());
                }

                data["items"] = itemDicts; // 그대로 aiassetpipeline_asset_list_save의 items로 전달 가능
                return data;
            }

            List<ScanEntry> entries = ProjectScanner.ScanPrefabs(scanRootPath);

            // scenePaths(문자열 배열) 지정 시, 해당 씬의 직접 배치 오브젝트 슬롯도 스캔해 병합한다.
            List<string> scenePaths = GetStringList(parameters, "scenePaths");
            if (scenePaths.Count > 0)
            {
                entries.AddRange(ProjectScanner.ScanScenes(scenePaths));
            }

            data["scanEntries"] = AssetListPromptBuilder.ScanEntriesToDictionaries(entries);
            return data;
        }

        private static object ExecuteSave(Dictionary<string, object> parameters)
        {
            if (parameters == null || !parameters.TryGetValue("items", out object itemsObj) ||
                !(itemsObj is List<object> rawItems))
            {
                throw new System.ArgumentException(
                    "items 파라미터가 필요합니다 (aiassetpipeline_asset_scan의 itemSchema 형식 객체 배열).");
            }

            List<AssetListItem> items = AssetListPromptBuilder.ItemsFromObjects(rawItems);
            if (items.Count == 0)
            {
                throw new System.ArgumentException("items 배열에 유효한 항목 객체가 없습니다.");
            }

            var doc = new AssetListDocument
            {
                designDocPath = GetString(parameters, "designDocPath"),
                scanRootPath = string.IsNullOrEmpty(GetString(parameters, "scanRootPath"))
                    ? "Assets"
                    : GetString(parameters, "scanRootPath"),
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

            List<string> warnings = AssetListBuilder.Validate(doc);
            string outputPath = GetString(parameters, "outputPath");
            string savedPath = AssetListBuilder.Save(doc, string.IsNullOrEmpty(outputPath) ? null : outputPath);

            return new Dictionary<string, object>
            {
                { "outputPath", savedPath },
                { "itemCount", doc.items.Count },
                { "warnings", warnings } // 대상 프리팹/UI 여부 미기록 항목 안내 (에디터 창에서 보완 필요)
            };
        }

        private static List<string> GetStringList(Dictionary<string, object> parameters, string key)
        {
            var result = new List<string>();
            if (parameters != null && parameters.TryGetValue(key, out object v) && v is List<object> list)
            {
                foreach (object entry in list)
                {
                    if (entry is string s && !string.IsNullOrEmpty(s))
                    {
                        result.Add(s);
                    }
                }
            }

            return result;
        }

        private static bool GetBool(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is bool b && b;
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }
    }
}
