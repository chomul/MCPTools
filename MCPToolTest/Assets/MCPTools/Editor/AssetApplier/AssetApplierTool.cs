using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>
    /// 4단계 적용 MCP 도구를 등록합니다.
    /// - mcptools_apply_asset: 항목 1개 적용 (assetPath 생략 시 확정본 자동 탐색).
    /// - mcptools_apply_all: 목록 전체 일괄 적용 (검증 실패 항목은 failed에 사유와 함께 반환).
    /// 창과 동일한 <see cref="AssetApplier"/> 공용 로직을 사용합니다.
    /// </summary>
    [InitializeOnLoad]
    public static class AssetApplierTool
    {
        static AssetApplierTool()
        {
            McpToolRegistry.Register(
                "mcptools_apply_asset",
                "4단계 적용: AssetList 항목 1개의 확정본을 대상 프리팹(또는 targetScenePath 지정 시 씬 직접 배치 오브젝트)의 " +
                "컴포넌트(Image.sprite/RawImage.texture/SpriteRenderer.sprite/AudioSource.clip)에 적용합니다. " +
                "항목에 targetComponent(컴포넌트 타입 이름)+targetField(직렬화 필드 경로, 배열 원소는 " +
                "\"필드명.Array.data[i]\")가 지정되어 있으면 기본 컴포넌트 대신 해당 컴포넌트의 직렬화 필드에 적용합니다 " +
                "(이미지 항목=Sprite 필드, 오디오 항목=AudioClip 필드). " +
                "항목에 animatorControllerPath가 있으면(또는 파라미터로 넘기면) 프리팹 루트에 Animator를 붙이고 " +
                "그 컨트롤러를 함께 연결합니다(씬 항목은 미지원). " +
                "파라미터: assetListPath(필수, Assets/ 상대 AssetList JSON), assetItemId(필수), " +
                "assetPath(선택, 생략 시 확정본 자동 탐색 — 항목에 spriteSheetPath가 있으면 그 시트를 사용), " +
                "spriteName(선택, 스프라이트 시트 안의 서브 스프라이트 이름 예: \"walk_03\". " +
                "지정하면 항목의 spriteName을 덮어써 해당 프레임을 Image/SpriteRenderer에 적용합니다. " +
                "생략하면 항목에 저장된 값을 사용하고, 그 값도 비어 있으면 에셋 전체를 스프라이트로, " +
                "시트처럼 전체 스프라이트가 없으면 첫 번째 프레임을 적용합니다), " +
                "animatorControllerPath(선택, Assets/ 상대 .controller 경로 — 이번 호출에 한해 항목 값을 덮어씁니다). " +
                "반환 data: { prefabPath, scenePath, objectPath, appliedAssetPath, spriteName, linkedControllerPath }.",
                ExecuteApplyAsset);

            McpToolRegistry.Register(
                "mcptools_apply_all",
                "4단계 일괄 적용: AssetList의 모든 항목에 대해 확정본을 대상 프리팹/씬에 적용합니다 " +
                "(씬 항목은 같은 씬끼리 묶어 씬을 한 번만 열어 처리). " +
                "검증 실패/확정본 없는 항목은 건너뛰고 사유와 함께 failed로 반환합니다. " +
                "항목에 animatorControllerPath가 있으면 프리팹 루트 Animator 연결까지 함께 수행합니다. " +
                "파라미터: assetListPath(필수, Assets/ 상대 AssetList JSON). " +
                "반환 data: { applied:[{id,prefabPath,objectPath,appliedAssetPath,linkedControllerPath}], failed:[{id,reason}] }.",
                ExecuteApplyAll);
        }

        private static object ExecuteApplyAsset(Dictionary<string, object> parameters)
        {
            string assetListPath = GetString(parameters, "assetListPath");
            string assetItemId = GetString(parameters, "assetItemId");
            if (string.IsNullOrEmpty(assetListPath) || string.IsNullOrEmpty(assetItemId))
            {
                throw new ArgumentException(
                    "assetListPath(1단계 AssetList JSON의 Assets/ 상대 경로)와 assetItemId 파라미터가 필요합니다.");
            }

            AssetListDocument doc = LoadDocument(assetListPath);
            AssetListItem item = doc.items.FirstOrDefault(i => i.id == assetItemId);
            if (item == null)
            {
                throw new ArgumentException(
                    $"AssetList JSON(\"{assetListPath}\")에서 항목 \"{assetItemId}\"를 찾을 수 없습니다.");
            }

            string assetPath = GetString(parameters, "assetPath");

            // spriteName·animatorControllerPath가 넘어오면 이번 호출에 한해 항목 값을 덮어쓴다
            // (AssetList JSON은 수정하지 않는다).
            string spriteName = GetString(parameters, "spriteName");
            if (spriteName != null)
            {
                item.spriteName = spriteName;
            }

            string controllerPath = GetString(parameters, "animatorControllerPath");
            if (controllerPath != null)
            {
                item.animatorControllerPath = controllerPath;
            }

            // 씬 항목(targetScenePath 지정)은 자동으로 씬 적용으로 분기된다.
            ApplyResult result = AssetApplier.Apply(item, assetPath);
            if (!result.success)
            {
                throw new InvalidOperationException(result.message);
            }

            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "prefabPath", result.prefabPath },
                { "scenePath", result.scenePath },
                { "objectPath", result.objectPath },
                { "appliedAssetPath", result.appliedAssetPath },
                { "spriteName", item.spriteName ?? string.Empty },
                { "linkedControllerPath", result.linkedControllerPath }
            };
        }

        private static object ExecuteApplyAll(Dictionary<string, object> parameters)
        {
            string assetListPath = GetString(parameters, "assetListPath");
            if (string.IsNullOrEmpty(assetListPath))
            {
                throw new ArgumentException("assetListPath(1단계 AssetList JSON의 Assets/ 상대 경로) 파라미터가 필요합니다.");
            }

            AssetListDocument doc = LoadDocument(assetListPath);
            var settings = MCPToolSettings.GetOrCreate();

            var applied = new List<object>();
            var failed = new List<object>();

            // 확정본을 먼저 탐색하고, 확정본이 있는 항목만 일괄 적용한다
            // (씬 항목은 같은 씬끼리 묶여 씬을 한 번만 열어 처리된다).
            var targets = new List<AssetListItem>();
            var assetPaths = new List<string>();
            foreach (AssetListItem item in doc.items)
            {
                string confirmedPath = AssetApplier.FindConfirmedAssetPath(settings, item);
                if (string.IsNullOrEmpty(confirmedPath))
                {
                    failed.Add(MakeFailed(item.id,
                        "확정본을 찾을 수 없습니다. 3단계(mcptools_select_candidate)에서 먼저 확정해주세요."));
                    continue;
                }

                targets.Add(item);
                assetPaths.Add(confirmedPath);
            }

            List<ApplyResult> results = AssetApplier.ApplyBatch(targets, assetPaths);
            for (int i = 0; i < targets.Count; i++)
            {
                ApplyResult result = results[i];
                if (result != null && result.success)
                {
                    applied.Add(new Dictionary<string, object>
                    {
                        { "id", targets[i].id },
                        { "prefabPath", result.prefabPath },
                        { "scenePath", result.scenePath },
                        { "objectPath", result.objectPath },
                        { "appliedAssetPath", result.appliedAssetPath },
                        { "linkedControllerPath", result.linkedControllerPath }
                    });
                }
                else
                {
                    failed.Add(MakeFailed(targets[i].id, result != null ? result.message : "(결과 없음)"));
                }
            }

            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "applied", applied },
                { "failed", failed }
            };
        }

        private static AssetListDocument LoadDocument(string assetListPath)
        {
            if (!File.Exists(assetListPath))
            {
                throw new FileNotFoundException(
                    $"AssetList JSON을 찾을 수 없습니다: \"{assetListPath}\"", assetListPath);
            }

            var dict = MiniJson.Deserialize(File.ReadAllText(assetListPath)) as Dictionary<string, object>;
            AssetListDocument doc = AssetListDocument.FromDictionary(dict);
            if (doc == null || doc.items.Count == 0)
            {
                throw new InvalidOperationException(
                    $"AssetList JSON(\"{assetListPath}\")에서 항목을 읽지 못했습니다.");
            }

            return doc;
        }

        private static Dictionary<string, object> MakeFailed(string id, string reason)
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "reason", reason }
            };
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }
    }
}
