using System.Collections.Generic;
using System.Text;

namespace MCPTools.Editor
{
    /// <summary>
    /// 기획서 분석을 AI에 위임하기 위한 스키마/지침/프롬프트 생성 유틸리티입니다.
    /// MCP 도구(mcptools_asset_scan)와 에셋 리스트업 창의 [AI용 프롬프트 복사]가 같은 내용을 공유합니다.
    /// </summary>
    public static class AssetListPromptBuilder
    {
        /// <summary>
        /// AI가 에셋 목록 작성 시 따라야 할 항목 필드 스키마(필드명 → 한국어 설명)를 반환합니다.
        /// </summary>
        public static Dictionary<string, object> GetItemSchema()
        {
            return new Dictionary<string, object>
            {
                { "id", "항목 고유 ID (선택, 예: \"item_001\". 생략 시 자동 부여)" },
                { "name", "에셋 이름 (필수, 짧고 명확하게)" },
                { "description", "설명/용도 (생성 AI 프롬프트 작성에 쓰이므로 구체적으로)" },
                { "assetType", "에셋 종류 (필수): \"image\" | \"ui\" | \"audio\"" },
                { "targetPrefabPath", "적용 대상 프리팹 경로 (Assets/ 기준 상대 경로, 스캔 결과의 prefabPath에서 선택. 대상이 없으면 빈 문자열)" },
                { "targetScenePath", "적용 대상 씬 경로 (Assets/ 기준 상대 경로, 스캔 결과의 scenePath에서 선택). " +
                                     "씬에 직접 배치된 오브젝트 대상 항목이면 채우고, 이때 targetPrefabPath는 빈 문자열로 둡니다 (상호 배타)" },
                { "targetObjectPath", "적용 대상 GameObject 계층 경로 (스캔 결과의 objectPath에서 선택. 없으면 빈 문자열. " +
                                      "프리팹 항목은 프리팹 루트 기준, 씬 항목은 씬 루트 오브젝트부터의 경로)" },
                { "isUI", "UI 여부 (true/false). 판단 불가 시 필드를 생략하면 '미지정'으로 처리됨" },
                { "status", "항목 상태 (선택, 기본 \"pending\")" }
            };
        }

        /// <summary>AI에게 주는 에셋 목록 작성 지침(한국어)을 반환합니다.</summary>
        /// <param name="includeFileExplorationHint">
        /// true면 "프로젝트 파일을 직접 읽을 수 있다면 스크립트·씬·프리팹을 참고해 역할을 추론하라"는
        /// 안내 단락을 덧붙입니다 (MCP 클라이언트/탐색 모드용). 클립보드 복사용 프롬프트는 false 유지.
        /// </param>
        public static string GetInstructions(bool includeFileExplorationHint = false)
        {
            string extra = includeFileExplorationHint
                ? "\n\nMCP 클라이언트인 당신이 프로젝트 파일을 직접 읽을 수 있다면, 스캔 결과의 프리팹과 " +
                  "관련된 C# 스크립트·씬·프리팹 파일을 직접 참고해 각 에셋이 어떤 컴포넌트/기획 요소에 " +
                  "쓰이는지 역할을 추론하고, description과 대상 경로(targetPrefabPath/targetObjectPath)를 " +
                  "더 정확히 채우세요. 단, 어떤 파일도 수정하지 마세요."
                : string.Empty;

            return
                "당신은 Unity 게임 프로젝트의 AI 에셋 생성 파이프라인 1단계(에셋 리스트업) 분석가입니다.\n" +
                "아래 기획서 원문과 프로젝트 프리팹 스캔 결과(에셋 적용 가능한 슬롯 목록)를 읽고, " +
                "생성해야 할 이미지/UI/사운드 에셋 목록을 itemSchema 형식의 JSON 배열로 작성하세요.\n" +
                "규칙:\n" +
                "1. 기획서에서 시각/청각 에셋이 필요한 요소를 빠짐없이 추출합니다 (캐릭터, 배경, UI 요소, 아이콘, 효과음 등).\n" +
                "2. 각 항목은 가능한 한 스캔 결과의 슬롯(prefabPath/objectPath)과 매칭해 targetPrefabPath, targetObjectPath, isUI를 채웁니다. " +
                "스캔 결과에 있는 경로를 그대로 사용하고, 임의의 경로를 지어내지 마세요.\n" +
                "3. 매칭할 슬롯이 없는 항목은 targetPrefabPath/targetScenePath를 빈 문자열로 두고 isUI만 판단해 기록합니다.\n" +
                "3-1. 스캔 결과의 슬롯 중 scenePath가 채워진 것은 씬에 직접 배치된 오브젝트입니다. 이런 슬롯과 매칭한 항목은 " +
                "targetScenePath에 scenePath를, targetObjectPath에 objectPath를 기록하고 targetPrefabPath는 빈 문자열로 둡니다. " +
                "prefabPath가 채워진 슬롯은 반대로 targetPrefabPath만 채웁니다 (두 필드는 상호 배타).\n" +
                "4. Image/RawImage 슬롯은 isUI=true, SpriteRenderer는 isUI=false, AudioSource는 assetType=\"audio\"입니다.\n" +
                "5. description은 이후 생성 프롬프트 제작에 쓰이므로 기획서 맥락(용도, 분위기, 색감 등)을 담아 구체적으로 씁니다.\n" +
                "6. 스캔 결과에 있으나 기획서에 언급되지 않은 슬롯도 필요해 보이면 항목으로 포함할 수 있습니다." +
                extra;
        }

        /// <summary>
        /// 스캔 결과를 JSON 직렬화 가능한 딕셔너리 목록으로 변환합니다.
        /// </summary>
        /// <param name="entries">프리팹 스캔 결과 (null 허용).</param>
        public static List<object> ScanEntriesToDictionaries(List<ScanEntry> entries)
        {
            var list = new List<object>();
            if (entries == null)
            {
                return list;
            }

            foreach (ScanEntry entry in entries)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "prefabPath", entry.prefabPath },
                    { "scenePath", entry.scenePath },
                    { "objectPath", entry.objectPath },
                    { "componentType", entry.componentType },
                    { "currentAssetName", entry.currentAssetName },
                    { "isUI", entry.isUI }
                });
            }

            return list;
        }

        /// <summary>
        /// MCP 없는 AI(웹 등)에 붙여넣을 단일 프롬프트 문자열을 만듭니다.
        /// 기획서 원문 + 스캔 결과 요약 + itemSchema + 출력 지침(JSON 배열만 출력)을 포함합니다.
        /// </summary>
        /// <param name="designDocText">기획서 원문 (null/빈 문자열이면 "없음"으로 표기).</param>
        /// <param name="scanEntries">프리팹 스캔 결과 (null 허용).</param>
        public static string BuildPrompt(string designDocText, List<ScanEntry> scanEntries)
        {
            return BuildPromptCore(designDocText, scanEntries, false);
        }

        /// <summary>
        /// "프로젝트 탐색 모드" 프롬프트를 만듭니다 — 헤드리스 AI CLI를 Unity 프로젝트 루트에서
        /// 읽기 전용 도구 허용으로 실행할 때 사용합니다. 기존 재료(기획서 원문+스캔 요약+스키마)에
        /// 더해, Assets/ 아래 스크립트·씬·프리팹을 직접 읽어 역할을 추론하라는 지시를 포함합니다.
        /// 클립보드 복사용(탐색 불가 전제)은 <see cref="BuildPrompt"/>를 그대로 사용하세요.
        /// </summary>
        /// <param name="designDocText">기획서 원문 (null/빈 문자열이면 "없음"으로 표기).</param>
        /// <param name="scanEntries">프리팹 스캔 결과 (null 허용).</param>
        public static string BuildExplorationPrompt(string designDocText, List<ScanEntry> scanEntries)
        {
            return BuildPromptCore(designDocText, scanEntries, true);
        }

        private static string BuildPromptCore(string designDocText, List<ScanEntry> scanEntries, bool exploration)
        {
            var sb = new StringBuilder();
            sb.AppendLine(GetInstructions(exploration));
            sb.AppendLine();

            if (exploration)
            {
                sb.AppendLine("## 프로젝트 탐색 (읽기 전용)");
                sb.AppendLine(
                    "현재 작업 폴더가 Unity 프로젝트 루트다. 필요하면 Assets/ 아래 C# 스크립트·씬·프리팹 " +
                    "파일을 직접 읽어 각 에셋의 용도·역할(어떤 컴포넌트/기획 요소에 쓰이는지)을 추론해 " +
                    "설명(description)과 대상 경로(targetPrefabPath/targetObjectPath)를 더 정확히 채워라. " +
                    "단, 어떤 파일도 수정하거나 생성하지 말라. 최종 출력은 JSON 배열만 출력하라.");
                sb.AppendLine();
            }

            sb.AppendLine("## itemSchema (각 항목의 필드)");
            foreach (KeyValuePair<string, object> pair in GetItemSchema())
            {
                sb.AppendLine($"- {pair.Key}: {pair.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("## 프로젝트 스캔 결과 (에셋 적용 가능 슬롯 — prefabPath 또는 scenePath 중 하나만 채워짐)");
            if (scanEntries == null || scanEntries.Count == 0)
            {
                sb.AppendLine("(스캔된 슬롯 없음)");
            }
            else
            {
                sb.AppendLine("prefabPath | scenePath | objectPath | componentType | currentAssetName | isUI");
                foreach (ScanEntry entry in scanEntries)
                {
                    sb.AppendLine(
                        $"{entry.prefabPath} | {entry.scenePath} | {entry.objectPath} | {entry.componentType} | " +
                        $"{(string.IsNullOrEmpty(entry.currentAssetName) ? "(비어 있음)" : entry.currentAssetName)} | {entry.isUI}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 기획서 원문");
            sb.AppendLine(string.IsNullOrEmpty(designDocText) ? "(기획서 없음 — 스캔 결과만으로 작성)" : designDocText);

            sb.AppendLine();
            sb.AppendLine("## 출력 형식 (중요)");
            sb.AppendLine("다른 설명이나 마크다운 코드 펜스 없이, itemSchema 형식의 객체들로 이루어진 JSON 배열 하나만 출력하세요.");
            sb.AppendLine("예: [{\"name\":\"플레이어 캐릭터\",\"description\":\"...\",\"assetType\":\"image\",\"targetPrefabPath\":\"Assets/...\",\"targetObjectPath\":\"...\",\"isUI\":false}]");
            return sb.ToString();
        }

        /// <summary>
        /// 스캔으로 확정된 항목 목록의 설명(description/용도)만 기획서를 참고해 채우도록 지시하는 프롬프트를 만듭니다.
        /// 항목을 추가/삭제/이름변경하지 않고, 각 항목의 id를 그대로 유지한 채 description만 반환하게 합니다
        /// (스캔 전용 모드에서 기획서를 설명 참고용으로만 쓰는 용도).
        /// </summary>
        /// <param name="designDocText">기획서 원문 (설명 작성의 참고 자료).</param>
        /// <param name="scannedItems">스캔으로 만든 고정 항목 목록 (id가 부여되어 있어야 함).</param>
        public static string BuildDescribeScannedPrompt(string designDocText, List<AssetListItem> scannedItems)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "당신은 Unity 게임 프로젝트의 AI 에셋 생성 파이프라인 1단계(에셋 리스트업) 분석가입니다.\n" +
                "아래는 씬/프리팹 스캔으로 이미 확정된 에셋 항목 목록입니다. 항목을 추가하거나 삭제하거나 " +
                "이름·대상 경로를 바꾸지 말고, 각 항목의 description(설명/용도)만 기획서를 참고해 채우세요.\n" +
                "규칙:\n" +
                "1. 제공된 항목의 id를 그대로 유지합니다. 새 항목을 만들지 마세요.\n" +
                "2. description은 이후 생성 프롬프트 제작에 쓰이므로 기획서 맥락(용도, 분위기, 색감 등)을 담아 구체적으로 씁니다.\n" +
                "3. 기획서에서 해당 항목과 관련된 내용을 찾지 못하면 이름·종류로 추론해 간단히 채웁니다.");
            sb.AppendLine();

            sb.AppendLine("## 스캔으로 확정된 항목 (id / name / assetType / 대상)");
            foreach (AssetListItem item in scannedItems)
            {
                string target = string.IsNullOrEmpty(item.targetScenePath) ? item.targetPrefabPath : item.targetScenePath;
                sb.AppendLine($"- {item.id} | {item.name} | {item.assetType} | {target} :: {item.targetObjectPath}");
            }

            sb.AppendLine();
            sb.AppendLine("## 기획서 원문 (설명 참고용)");
            sb.AppendLine(string.IsNullOrEmpty(designDocText) ? "(기획서 없음)" : designDocText);

            sb.AppendLine();
            sb.AppendLine("## 출력 형식 (중요)");
            sb.AppendLine("다른 설명이나 마크다운 코드 펜스 없이, 각 항목의 {\"id\":..., \"description\":...} 객체로 이루어진 JSON 배열 하나만 출력하세요.");
            sb.AppendLine("예: [{\"id\":\"item_001\",\"description\":\"...\"},{\"id\":\"item_002\",\"description\":\"...\"}]");
            return sb.ToString();
        }

        /// <summary>
        /// AI가 출력한 JSON(배열 또는 {"items":[...]} 객체)을 파싱해 항목 목록으로 변환합니다.
        /// 마크다운 코드 펜스(```)는 자동으로 제거합니다.
        /// </summary>
        /// <param name="json">AI 응답 JSON 문자열.</param>
        /// <returns>파싱된 항목 목록.</returns>
        /// <exception cref="System.FormatException">JSON을 항목 배열로 해석할 수 없는 경우 (한국어 안내 포함).</exception>
        public static List<AssetListItem> ParseItemsJson(string json)
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

            List<AssetListItem> items = ItemsFromObjects(rawItems);
            if (items.Count == 0)
            {
                throw new System.FormatException("JSON 배열에 유효한 항목 객체가 없습니다. 각 항목은 name 필드를 가진 객체여야 합니다.");
            }

            return items;
        }

        /// <summary>
        /// MiniJson으로 파싱된 객체 목록을 항목 목록으로 변환합니다 (MCP items 파라미터 처리용).
        /// isUI 필드가 없으면 uiFlag는 미지정(-1)으로 둡니다.
        /// </summary>
        /// <param name="rawItems">MiniJson.Deserialize 결과의 List&lt;object&gt;.</param>
        public static List<AssetListItem> ItemsFromObjects(List<object> rawItems)
        {
            var items = new List<AssetListItem>();
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

                var item = new AssetListItem
                {
                    id = GetString(dict, "id"),
                    name = GetString(dict, "name"),
                    description = GetString(dict, "description"),
                    assetType = NormalizeAssetType(GetString(dict, "assetType", "image")),
                    targetPrefabPath = GetString(dict, "targetPrefabPath"),
                    targetScenePath = GetString(dict, "targetScenePath"),
                    targetObjectPath = GetString(dict, "targetObjectPath"),
                    status = GetString(dict, "status", "pending")
                };

                if (dict.TryGetValue("isUI", out object ui) && ui is bool isUI)
                {
                    item.uiFlag = isUI ? 1 : 0;
                }
                else
                {
                    item.uiFlag = -1;
                }

                if (string.IsNullOrEmpty(item.status))
                {
                    item.status = "pending";
                }

                items.Add(item);
            }

            return items;
        }

        private static string NormalizeAssetType(string value)
        {
            switch (string.IsNullOrEmpty(value) ? "image" : value.ToLowerInvariant())
            {
                case "ui": return "ui";
                case "audio": return "audio";
                default: return "image";
            }
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

        private static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            return dict.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }
    }
}
