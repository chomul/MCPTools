using System;
using System.Collections.Generic;

namespace MCPTools.Editor
{
    /// <summary>
    /// 2단계 프롬프트 제작 산출물의 항목 1개입니다.
    /// 1단계 <see cref="AssetListItem"/>의 참조 필드(id/name/assetType/isUI/targetPrefabPath 등)를 유지하고
    /// positive/negative 프롬프트를 추가합니다. MiniJson 직렬화를 위해
    /// <see cref="ToDictionary"/>/<see cref="FromDictionary"/>를 제공합니다.
    /// </summary>
    [Serializable]
    public class PromptItem
    {
        /// <summary>1단계 목록 항목의 고유 ID (예: "item_001").</summary>
        public string id = string.Empty;

        /// <summary>에셋 이름.</summary>
        public string name = string.Empty;

        /// <summary>에셋 종류: "image" / "ui" / "audio".</summary>
        public string assetType = "image";

        /// <summary>UI 여부입니다.</summary>
        public bool isUI;

        /// <summary>적용 대상 프리팹 경로 (Assets/ 기준 상대 경로).</summary>
        public string targetPrefabPath = string.Empty;

        /// <summary>적용 대상 GameObject 계층 경로 (프리팹 루트 기준).</summary>
        public string targetObjectPath = string.Empty;

        /// <summary>설명/용도 (1단계 목록에서 이어받음, 참고용).</summary>
        public string description = string.Empty;

        /// <summary>ComfyUI positive 프롬프트입니다.</summary>
        public string positive = string.Empty;

        /// <summary>ComfyUI negative 프롬프트입니다.</summary>
        public string negative = string.Empty;

        /// <summary>MiniJson 직렬화용 딕셔너리로 변환합니다.</summary>
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "name", name },
                { "assetType", assetType },
                { "isUI", isUI },
                { "targetPrefabPath", targetPrefabPath },
                { "targetObjectPath", targetObjectPath },
                { "description", description },
                { "positive", positive },
                { "negative", negative }
            };
        }

        /// <summary>MiniJson 역직렬화 딕셔너리에서 항목을 복원합니다.</summary>
        /// <param name="dict">MiniJson.Deserialize 결과 딕셔너리.</param>
        /// <returns>복원된 항목. dict가 null이면 null.</returns>
        public static PromptItem FromDictionary(Dictionary<string, object> dict)
        {
            if (dict == null)
            {
                return null;
            }

            return new PromptItem
            {
                id = GetString(dict, "id"),
                name = GetString(dict, "name"),
                assetType = GetString(dict, "assetType", "image"),
                isUI = dict.TryGetValue("isUI", out object ui) && ui is bool b && b,
                targetPrefabPath = GetString(dict, "targetPrefabPath"),
                targetObjectPath = GetString(dict, "targetObjectPath"),
                description = GetString(dict, "description"),
                positive = GetString(dict, "positive"),
                negative = GetString(dict, "negative")
            };
        }

        private static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            return dict.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }
    }

    /// <summary>
    /// 2단계 프롬프트 제작 산출물 문서입니다. `Assets/Docs/2_PromptSet/PromptSet_{yyyyMMdd_HHmm}.json`으로 저장됩니다.
    /// </summary>
    [Serializable]
    public class PromptSetDocument
    {
        /// <summary>입력으로 사용한 1단계 에셋 목록 JSON 경로 (Assets/ 기준 상대 경로).</summary>
        public string assetListPath = string.Empty;

        /// <summary>사용한 프롬프트 템플릿 이름 (기본 "default").</summary>
        public string templateName = "default";

        /// <summary>문서 생성 시각 (yyyy-MM-dd HH:mm).</summary>
        public string createdAt = string.Empty;

        /// <summary>프롬프트 항목 목록입니다.</summary>
        public List<PromptItem> items = new List<PromptItem>();

        /// <summary>MiniJson 직렬화용 딕셔너리로 변환합니다.</summary>
        public Dictionary<string, object> ToDictionary()
        {
            var itemList = new List<object>();
            foreach (PromptItem item in items)
            {
                itemList.Add(item.ToDictionary());
            }

            return new Dictionary<string, object>
            {
                { "assetListPath", assetListPath },
                { "templateName", templateName },
                { "createdAt", createdAt },
                { "items", itemList }
            };
        }

        /// <summary>MiniJson 역직렬화 딕셔너리에서 문서를 복원합니다.</summary>
        /// <param name="dict">MiniJson.Deserialize 결과 딕셔너리.</param>
        /// <returns>복원된 문서. dict가 null이면 null.</returns>
        public static PromptSetDocument FromDictionary(Dictionary<string, object> dict)
        {
            if (dict == null)
            {
                return null;
            }

            var doc = new PromptSetDocument
            {
                assetListPath = dict.TryGetValue("assetListPath", out object a) && a is string asp ? asp : string.Empty,
                templateName = dict.TryGetValue("templateName", out object t) && t is string ts ? ts : "default",
                createdAt = dict.TryGetValue("createdAt", out object c) && c is string cs ? cs : string.Empty
            };

            if (dict.TryGetValue("items", out object itemsObj) && itemsObj is List<object> list)
            {
                foreach (object entry in list)
                {
                    PromptItem item = PromptItem.FromDictionary(entry as Dictionary<string, object>);
                    if (item != null)
                    {
                        doc.items.Add(item);
                    }
                }
            }

            return doc;
        }
    }
}
