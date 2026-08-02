using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AIAssetPipeline.Editor
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
        /// <summary>
        /// 이 도구가 저장하는 PromptSet 문서의 스키마 버전입니다. 저장 시 항상 이 값을 기록하며,
        /// 문서에 <c>schemaVersion</c> 키가 없으면 로드 시 이 버전(1)으로 간주합니다.
        /// 문서의 기존 키 의미가 바뀌는 변경을 할 때 함께 올립니다.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

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
                { "schemaVersion", CurrentSchemaVersion },
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

            WarnIfNewerSchemaVersion(dict);

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

        /// <summary>
        /// 문서의 <c>schemaVersion</c>이 이 도구가 아는 버전보다 크면 경고만 남깁니다(로드는 막지 않습니다).
        /// 키가 없거나 정수로 해석할 수 없는 값이면 <see cref="CurrentSchemaVersion"/>으로 간주하고 조용히 진행합니다.
        /// </summary>
        private static void WarnIfNewerSchemaVersion(Dictionary<string, object> dict)
        {
            if (!dict.TryGetValue("schemaVersion", out object value) || value == null)
            {
                // 구 문서(키 없음) → 버전 1로 간주하고 기존과 동일하게 로드한다.
                return;
            }

            // MiniJson은 JSON 정수를 long, 실수를 double로 돌려준다. 손수 작성한 문서를 위해 문자열도 받아준다.
            long version;
            if (value is long asLong)
            {
                version = asLong;
            }
            else if (value is int asInt)
            {
                version = asInt;
            }
            else if (value is double asDouble)
            {
                version = (long)asDouble;
            }
            else if (!long.TryParse(
                         value as string ?? string.Empty,
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out version))
            {
                // 정수로 해석할 수 없는 값 → 버전 정보가 없는 것으로 보고 조용히 진행한다.
                return;
            }

            if (version > CurrentSchemaVersion)
            {
                Debug.LogWarning(
                    $"[AIAssetPipeline] PromptSet 문서의 schemaVersion이 {version}입니다 " +
                    $"(이 도구가 아는 최신 버전: {CurrentSchemaVersion}). " +
                    "더 새 버전의 AIAssetPipeline가 저장한 문서일 수 있어 일부 값이 다르게 해석될 수 있습니다. " +
                    "로드는 그대로 계속하며, 결과가 이상하면 AIAssetPipeline를 최신 버전으로 업데이트해주세요.");
            }
        }
    }
}
