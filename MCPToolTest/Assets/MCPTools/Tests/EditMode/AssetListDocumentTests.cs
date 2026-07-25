using System.Collections.Generic;
using NUnit.Framework;

namespace MCPTools.Editor.Tests
{
    /// <summary>
    /// 1단계 산출물(<see cref="AssetListDocument"/>/<see cref="AssetListItem"/>)의
    /// 딕셔너리 왕복과 구 JSON 호환을 고정하는 테스트입니다.
    /// <para>
    /// 이미 저장된 <c>AssetList_*.json</c>이 나중에 추가된 키(spriteName·spriteSheetPath·
    /// animatorControllerPath·isUISpecified) 없이도 예외 없이 로드돼야 합니다.
    /// Task 8의 리팩터로 이 문서 스키마가 흔들리면 사용자의 기존 산출물이 깨지므로 정답지로 고정합니다.
    /// </para>
    /// </summary>
    public class AssetListDocumentTests
    {
        /// <summary>모든 필드가 채워진 항목 1개를 만듭니다.</summary>
        private static AssetListItem FullItem()
        {
            return new AssetListItem
            {
                id = "item_001",
                name = "주인공 스프라이트",
                description = "메인 캐릭터 기본 이미지",
                assetType = "image",
                targetPrefabPath = "Assets/Prefabs/Hero.prefab",
                targetScenePath = string.Empty,
                targetObjectPath = "Body/Sprite",
                targetComponent = "PlayerController",
                targetField = "jumpSound",
                spriteName = "walk_03",
                spriteSheetPath = "Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png",
                animatorControllerPath = "Assets/Generated/3_Confirmed/Animations/hero_sheet/hero_sheet.controller",
                uiFlag = 1,
                status = "generated"
            };
        }

        private static void AssertSameItem(AssetListItem expected, AssetListItem actual)
        {
            Assert.AreEqual(expected.id, actual.id, "id");
            Assert.AreEqual(expected.name, actual.name, "name");
            Assert.AreEqual(expected.description, actual.description, "description");
            Assert.AreEqual(expected.assetType, actual.assetType, "assetType");
            Assert.AreEqual(expected.targetPrefabPath, actual.targetPrefabPath, "targetPrefabPath");
            Assert.AreEqual(expected.targetScenePath, actual.targetScenePath, "targetScenePath");
            Assert.AreEqual(expected.targetObjectPath, actual.targetObjectPath, "targetObjectPath");
            Assert.AreEqual(expected.targetComponent, actual.targetComponent, "targetComponent");
            Assert.AreEqual(expected.targetField, actual.targetField, "targetField");
            Assert.AreEqual(expected.spriteName, actual.spriteName, "spriteName");
            Assert.AreEqual(expected.spriteSheetPath, actual.spriteSheetPath, "spriteSheetPath");
            Assert.AreEqual(expected.animatorControllerPath, actual.animatorControllerPath, "animatorControllerPath");
            Assert.AreEqual(expected.uiFlag, actual.uiFlag, "uiFlag");
            Assert.AreEqual(expected.status, actual.status, "status");
        }

        /// <summary>모든 필드가 ToDictionary → FromDictionary 왕복에서 보존되는지 고정합니다.</summary>
        [Test]
        public void Item_ToDictionary_FromDictionary_PreservesAllFields()
        {
            AssetListItem source = FullItem();

            AssetListItem restored = AssetListItem.FromDictionary(source.ToDictionary());

            Assert.IsNotNull(restored);
            AssertSameItem(source, restored);
        }

        /// <summary>
        /// uiFlag의 3가지 상태(-1 미지정 / 0 아니오 / 1 예)가 isUI·isUISpecified 두 bool로 나뉘었다가
        /// 그대로 복원되는지 고정합니다.
        /// </summary>
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        public void Item_UiFlag_RoundTripsThroughIsUiPair(int uiFlag)
        {
            var source = new AssetListItem { id = "x", uiFlag = uiFlag };

            Dictionary<string, object> dict = source.ToDictionary();
            Assert.AreEqual(uiFlag == 1, dict["isUI"], "isUI");
            Assert.AreEqual(uiFlag >= 0, dict["isUISpecified"], "isUISpecified");

            AssetListItem restored = AssetListItem.FromDictionary(dict);
            Assert.AreEqual(uiFlag, restored.uiFlag);
            Assert.AreEqual(uiFlag >= 0, restored.IsUISpecified);
            Assert.AreEqual(uiFlag == 1, restored.IsUI);
        }

        /// <summary>문서 전체(항목 목록 포함)가 왕복에서 보존되는지 고정합니다.</summary>
        [Test]
        public void Document_ToDictionary_FromDictionary_PreservesAllFields()
        {
            var source = new AssetListDocument
            {
                designDocPath = "Assets/Docs/기획서.md",
                scanRootPath = "Assets/Game",
                createdAt = "2026-07-25 18:30",
                items = { FullItem(), new AssetListItem { id = "item_002", assetType = "audio", uiFlag = 0 } }
            };

            AssetListDocument restored = AssetListDocument.FromDictionary(source.ToDictionary());

            Assert.IsNotNull(restored);
            Assert.AreEqual(source.designDocPath, restored.designDocPath);
            Assert.AreEqual(source.scanRootPath, restored.scanRootPath);
            Assert.AreEqual(source.createdAt, restored.createdAt);
            Assert.AreEqual(2, restored.items.Count);
            AssertSameItem(source.items[0], restored.items[0]);
            AssertSameItem(source.items[1], restored.items[1]);
        }

        /// <summary>
        /// 저장된 JSON 텍스트를 거치는 전체 경로(ToDictionary → MiniJson → FromDictionary)도
        /// 동일하게 왕복되는지 고정합니다. (MiniJson의 List&lt;object&gt;/Dictionary 매핑 포함)
        /// </summary>
        [Test]
        public void Document_RoundTripsThroughMiniJsonText()
        {
            var source = new AssetListDocument
            {
                designDocPath = "Assets/Docs/기획서.md",
                scanRootPath = "Assets",
                createdAt = "2026-07-25 18:30",
                items = { FullItem() }
            };

            string json = MiniJson.Serialize(source.ToDictionary());
            var parsed = MiniJson.Deserialize(json) as Dictionary<string, object>;
            AssetListDocument restored = AssetListDocument.FromDictionary(parsed);

            Assert.IsNotNull(restored);
            Assert.AreEqual(source.designDocPath, restored.designDocPath);
            Assert.AreEqual(1, restored.items.Count);
            AssertSameItem(source.items[0], restored.items[0]);
        }

        /// <summary>
        /// 구 JSON 호환: 나중에 추가된 키(spriteName·spriteSheetPath·animatorControllerPath·isUISpecified)가
        /// 아예 없는 항목도 예외 없이 기본값으로 로드돼야 합니다.
        /// 특히 isUISpecified 키가 없으면 "지정됨"으로 간주하는 현재 동작을 못박습니다
        /// (구 문서는 UI 여부가 항상 확정 상태였기 때문).
        /// </summary>
        [Test]
        public void Item_FromLegacyDictionary_MissingKeys_UseDefaults()
        {
            var legacy = new Dictionary<string, object>
            {
                { "id", "item_001" },
                { "name", "구버전 항목" },
                { "description", "설명" },
                { "assetType", "ui" },
                { "targetPrefabPath", "Assets/Prefabs/HUD.prefab" },
                { "targetObjectPath", "Root/Icon" },
                { "isUI", true },
                { "status", "applied" }
                // spriteName / spriteSheetPath / animatorControllerPath / targetScenePath /
                // targetComponent / targetField / isUISpecified 없음
            };

            AssetListItem item = AssetListItem.FromDictionary(legacy);

            Assert.IsNotNull(item);
            Assert.AreEqual("item_001", item.id);
            Assert.AreEqual("ui", item.assetType);
            Assert.AreEqual(string.Empty, item.spriteName);
            Assert.AreEqual(string.Empty, item.spriteSheetPath);
            Assert.AreEqual(string.Empty, item.animatorControllerPath);
            Assert.AreEqual(string.Empty, item.targetScenePath);
            Assert.AreEqual(string.Empty, item.targetComponent);
            Assert.AreEqual(string.Empty, item.targetField);
            Assert.AreEqual("applied", item.status);
            Assert.AreEqual(1, item.uiFlag, "isUISpecified가 없는 구 문서는 '지정됨'으로 간주합니다.");
            Assert.IsTrue(item.IsUISpecified);
            Assert.IsTrue(item.IsUI);
        }

        /// <summary>키가 하나도 없는 딕셔너리도 예외 없이 기본값 항목이 되는지 고정합니다.</summary>
        [Test]
        public void Item_FromEmptyDictionary_UsesTypeDefaults()
        {
            AssetListItem item = AssetListItem.FromDictionary(new Dictionary<string, object>());

            Assert.IsNotNull(item);
            Assert.AreEqual(string.Empty, item.id);
            Assert.AreEqual("image", item.assetType, "assetType 기본값");
            Assert.AreEqual("pending", item.status, "status 기본값");
            Assert.AreEqual(0, item.uiFlag, "isUISpecified가 없으면 '지정됨 + UI 아님'(0)입니다.");
        }

        /// <summary>구 문서에 items 키가 없거나 항목이 딕셔너리가 아니어도 예외 없이 로드되는지 고정합니다.</summary>
        [Test]
        public void Document_FromLegacyDictionary_MissingOrMalformedItems_LoadsWithDefaults()
        {
            var legacy = new Dictionary<string, object>
            {
                { "designDocPath", "Assets/Docs/old.md" }
                // scanRootPath / createdAt / items 없음
            };

            AssetListDocument doc = AssetListDocument.FromDictionary(legacy);

            Assert.IsNotNull(doc);
            Assert.AreEqual("Assets/Docs/old.md", doc.designDocPath);
            Assert.AreEqual("Assets", doc.scanRootPath, "scanRootPath 기본값");
            Assert.AreEqual(string.Empty, doc.createdAt);
            Assert.AreEqual(0, doc.items.Count);

            var malformed = new Dictionary<string, object>
            {
                { "items", new List<object> { "문자열 항목", null, new Dictionary<string, object> { { "id", "ok" } } } }
            };

            AssetListDocument doc2 = AssetListDocument.FromDictionary(malformed);

            Assert.IsNotNull(doc2);
            Assert.AreEqual(1, doc2.items.Count, "딕셔너리가 아닌 항목은 조용히 건너뜁니다.");
            Assert.AreEqual("ok", doc2.items[0].id);
        }

        /// <summary>null 입력이 예외 대신 null을 돌려주는지 고정합니다.</summary>
        [Test]
        public void FromDictionary_Null_ReturnsNull()
        {
            Assert.IsNull(AssetListItem.FromDictionary(null));
            Assert.IsNull(AssetListDocument.FromDictionary(null));
        }
    }
}
