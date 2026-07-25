using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 에셋 리스트업 1단계 산출물의 항목 1개입니다.
    /// MiniJson 직렬화를 위해 <see cref="ToDictionary"/>/<see cref="FromDictionary"/>를 제공합니다.
    /// </summary>
    [Serializable]
    public class AssetListItem
    {
        /// <summary>항목 고유 ID (예: "item_001").</summary>
        public string id = string.Empty;

        /// <summary>에셋 이름.</summary>
        public string name = string.Empty;

        /// <summary>설명/용도.</summary>
        public string description = string.Empty;

        /// <summary>에셋 종류: "image" / "ui" / "audio".</summary>
        public string assetType = "image";

        /// <summary>적용 대상 프리팹 경로 (Assets/ 기준 상대 경로). 비어 있으면 미지정.</summary>
        public string targetPrefabPath = string.Empty;

        /// <summary>
        /// 적용 대상 씬 경로 (Assets/ 기준 상대 경로). 씬에 직접 배치된 오브젝트 대상 항목이면
        /// 비어 있지 않으며, <see cref="targetPrefabPath"/>와 상호 배타입니다
        /// (씬 항목은 targetObjectPath가 씬 루트 기준 계층 경로).
        /// </summary>
        public string targetScenePath = string.Empty;

        /// <summary>적용 대상 GameObject 계층 경로 (프리팹 항목은 프리팹 루트 기준, 씬 항목은 씬 루트 기준).</summary>
        public string targetObjectPath = string.Empty;

        /// <summary>
        /// (선택, 오디오 항목 전용) 적용 대상 컴포넌트 타입 이름 (예: "PlayerController").
        /// <see cref="targetField"/>와 함께 지정하면 AudioSource.clip 대신 해당 컴포넌트의
        /// 직렬화된 AudioClip 필드에 적용합니다. 비어 있으면 기존 AudioSource.clip 동작.
        /// </summary>
        public string targetComponent = string.Empty;

        /// <summary>
        /// (선택, 오디오 항목 전용) 적용 대상 직렬화 필드 경로 (예: "jumpSound").
        /// <see cref="targetComponent"/>와 함께 지정해야 합니다.
        /// </summary>
        public string targetField = string.Empty;

        /// <summary>
        /// (선택, 이미지/UI 항목 전용) 적용할 서브 스프라이트 이름 (예: "walk_03").
        /// 비어 있으면 에셋 전체를 Sprite로 로드하는 기존 동작을 사용하고,
        /// 값이 있으면 스프라이트 시트 안에서 이름이 일치하는 스프라이트를 찾아 적용합니다.
        /// </summary>
        public string spriteName = string.Empty;

        /// <summary>
        /// (선택) 스프라이트를 가져올 시트 텍스처 경로 (Assets/ 기준 상대 경로).
        /// 지정하면 확정본 자동 탐색 대신 이 경로를 적용 대상 에셋으로 사용합니다.
        /// </summary>
        public string spriteSheetPath = string.Empty;

        /// <summary>
        /// (선택, 프리팹 항목 전용) 대상 프리팹 루트의 Animator에 연결할 AnimatorController 경로
        /// (Assets/ 기준 상대 경로). 지정하면 적용 시 프리팹 루트에 Animator를 붙이고(없을 때만) 이 컨트롤러를 연결합니다.
        /// 씬 항목에는 지원하지 않습니다.
        /// </summary>
        public string animatorControllerPath = string.Empty;

        /// <summary>
        /// 오디오 항목이 임의 컴포넌트의 AudioClip 필드를 대상으로 지정했는지 여부입니다
        /// (targetComponent와 targetField가 모두 지정된 경우).
        /// </summary>
        public bool HasCustomAudioTarget
        {
            get
            {
                return assetType == "audio"
                    && !string.IsNullOrEmpty(targetComponent)
                    && !string.IsNullOrEmpty(targetField);
            }
        }

        /// <summary>씬에 직접 배치된 오브젝트 대상 항목인지 여부입니다.</summary>
        public bool IsSceneItem
        {
            get { return !string.IsNullOrEmpty(targetScenePath); }
        }

        /// <summary>UI 여부 지정 값: -1 미지정, 0 아니오, 1 예. 저장 전 반드시 0/1로 확정해야 합니다.</summary>
        public int uiFlag = -1;

        /// <summary>항목 상태 (예: "pending", "prompted", "generated", "applied").</summary>
        public string status = "pending";

        /// <summary>UI 여부가 지정되었는지 여부입니다.</summary>
        public bool IsUISpecified
        {
            get { return uiFlag >= 0; }
        }

        /// <summary>UI 여부입니다 (미지정이면 false).</summary>
        public bool IsUI
        {
            get { return uiFlag == 1; }
        }

        /// <summary>MiniJson 직렬화용 딕셔너리로 변환합니다.</summary>
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "name", name },
                { "description", description },
                { "assetType", assetType },
                { "targetPrefabPath", targetPrefabPath },
                { "targetScenePath", targetScenePath },
                { "targetObjectPath", targetObjectPath },
                { "targetComponent", targetComponent },
                { "targetField", targetField },
                { "spriteName", spriteName },
                { "spriteSheetPath", spriteSheetPath },
                { "animatorControllerPath", animatorControllerPath },
                { "isUI", uiFlag == 1 },
                { "isUISpecified", uiFlag >= 0 },
                { "status", status }
            };
        }

        /// <summary>MiniJson 역직렬화 딕셔너리에서 항목을 복원합니다.</summary>
        /// <param name="dict">MiniJson.Deserialize 결과 딕셔너리.</param>
        /// <returns>복원된 항목. dict가 null이면 null.</returns>
        public static AssetListItem FromDictionary(Dictionary<string, object> dict)
        {
            if (dict == null)
            {
                return null;
            }

            var item = new AssetListItem
            {
                id = GetString(dict, "id"),
                name = GetString(dict, "name"),
                description = GetString(dict, "description"),
                assetType = GetString(dict, "assetType", "image"),
                targetPrefabPath = GetString(dict, "targetPrefabPath"),
                targetScenePath = GetString(dict, "targetScenePath"),
                targetObjectPath = GetString(dict, "targetObjectPath"),
                targetComponent = GetString(dict, "targetComponent"),
                targetField = GetString(dict, "targetField"),
                // 구 버전 JSON에는 없는 키다. 없으면 빈 문자열이 되어 기존 동작을 그대로 유지한다.
                spriteName = GetString(dict, "spriteName"),
                spriteSheetPath = GetString(dict, "spriteSheetPath"),
                animatorControllerPath = GetString(dict, "animatorControllerPath"),
                status = GetString(dict, "status", "pending")
            };

            bool specified = !dict.ContainsKey("isUISpecified") || (dict["isUISpecified"] is bool s && s);
            bool isUI = dict.TryGetValue("isUI", out object v) && v is bool b && b;
            item.uiFlag = specified ? (isUI ? 1 : 0) : -1;
            return item;
        }

        private static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            return dict.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }
    }

    /// <summary>
    /// 에셋 리스트업 1단계 산출물 문서입니다. `Assets/Docs/1_AssetList/AssetList_{yyyyMMdd_HHmm}.json`으로 저장됩니다.
    /// </summary>
    [Serializable]
    public class AssetListDocument
    {
        /// <summary>
        /// 이 도구가 저장하는 AssetList 문서의 스키마 버전입니다. 저장 시 항상 이 값을 기록하며,
        /// 문서에 <c>schemaVersion</c> 키가 없으면 로드 시 이 버전(1)으로 간주합니다.
        /// 문서의 기존 키 의미가 바뀌는 변경을 할 때 함께 올립니다.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>입력으로 사용한 기획서 경로 (Assets/ 기준 상대 경로).</summary>
        public string designDocPath = string.Empty;

        /// <summary>스캔 루트 경로 (Assets/ 기준 상대 경로).</summary>
        public string scanRootPath = "Assets";

        /// <summary>문서 생성 시각 (yyyy-MM-dd HH:mm).</summary>
        public string createdAt = string.Empty;

        /// <summary>에셋 항목 목록입니다.</summary>
        public List<AssetListItem> items = new List<AssetListItem>();

        /// <summary>MiniJson 직렬화용 딕셔너리로 변환합니다.</summary>
        public Dictionary<string, object> ToDictionary()
        {
            var itemList = new List<object>();
            foreach (AssetListItem item in items)
            {
                itemList.Add(item.ToDictionary());
            }

            return new Dictionary<string, object>
            {
                { "schemaVersion", CurrentSchemaVersion },
                { "designDocPath", designDocPath },
                { "scanRootPath", scanRootPath },
                { "createdAt", createdAt },
                { "items", itemList }
            };
        }

        /// <summary>MiniJson 역직렬화 딕셔너리에서 문서를 복원합니다.</summary>
        /// <param name="dict">MiniJson.Deserialize 결과 딕셔너리.</param>
        /// <returns>복원된 문서. dict가 null이면 null.</returns>
        public static AssetListDocument FromDictionary(Dictionary<string, object> dict)
        {
            if (dict == null)
            {
                return null;
            }

            WarnIfNewerSchemaVersion(dict);

            var doc = new AssetListDocument
            {
                designDocPath = dict.TryGetValue("designDocPath", out object d) && d is string ds ? ds : string.Empty,
                scanRootPath = dict.TryGetValue("scanRootPath", out object r) && r is string rs ? rs : "Assets",
                createdAt = dict.TryGetValue("createdAt", out object c) && c is string cs ? cs : string.Empty
            };

            if (dict.TryGetValue("items", out object itemsObj) && itemsObj is List<object> list)
            {
                foreach (object entry in list)
                {
                    AssetListItem item = AssetListItem.FromDictionary(entry as Dictionary<string, object>);
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
                    $"[MCPTools] AssetList 문서의 schemaVersion이 {version}입니다 " +
                    $"(이 도구가 아는 최신 버전: {CurrentSchemaVersion}). " +
                    "더 새 버전의 MCPTools가 저장한 문서일 수 있어 일부 값이 다르게 해석될 수 있습니다. " +
                    "로드는 그대로 계속하며, 결과가 이상하면 MCPTools를 최신 버전으로 업데이트해주세요.");
            }
        }
    }
}
