using System.Collections.Generic;
using NUnit.Framework;

namespace MCPTools.Editor.Tests
{
    /// <summary>
    /// 2단계 산출물(<see cref="PromptSetDocument"/>/<see cref="PromptItem"/>)의
    /// 딕셔너리 왕복과 구 JSON 호환을 고정하는 테스트입니다.
    /// <para>
    /// 이미 저장된 <c>PromptSet_*.json</c>이 키 누락 상태로도 예외 없이 로드돼야 하며,
    /// Task 8 리팩터가 이 문서 스키마를 흔들면 3·4단계 입력이 깨지므로 정답지로 고정합니다.
    /// </para>
    /// </summary>
    public class PromptSetDocumentTests
    {
        /// <summary>모든 필드가 채워진 항목 1개를 만듭니다.</summary>
        private static PromptItem FullItem()
        {
            return new PromptItem
            {
                id = "item_001",
                name = "주인공 스프라이트",
                assetType = "image",
                isUI = true,
                targetPrefabPath = "Assets/Prefabs/Hero.prefab",
                targetObjectPath = "Body/Sprite",
                description = "메인 캐릭터 기본 이미지",
                positive = "hero, side view, \"pixel art\"",
                negative = "blurry, text, watermark"
            };
        }

        private static void AssertSameItem(PromptItem expected, PromptItem actual)
        {
            Assert.AreEqual(expected.id, actual.id, "id");
            Assert.AreEqual(expected.name, actual.name, "name");
            Assert.AreEqual(expected.assetType, actual.assetType, "assetType");
            Assert.AreEqual(expected.isUI, actual.isUI, "isUI");
            Assert.AreEqual(expected.targetPrefabPath, actual.targetPrefabPath, "targetPrefabPath");
            Assert.AreEqual(expected.targetObjectPath, actual.targetObjectPath, "targetObjectPath");
            Assert.AreEqual(expected.description, actual.description, "description");
            Assert.AreEqual(expected.positive, actual.positive, "positive");
            Assert.AreEqual(expected.negative, actual.negative, "negative");
        }

        /// <summary>모든 필드가 ToDictionary → FromDictionary 왕복에서 보존되는지 고정합니다.</summary>
        [Test]
        public void Item_ToDictionary_FromDictionary_PreservesAllFields()
        {
            PromptItem source = FullItem();

            PromptItem restored = PromptItem.FromDictionary(source.ToDictionary());

            Assert.IsNotNull(restored);
            AssertSameItem(source, restored);
        }

        /// <summary>문서 전체(항목 목록 포함)가 왕복에서 보존되는지 고정합니다.</summary>
        [Test]
        public void Document_ToDictionary_FromDictionary_PreservesAllFields()
        {
            var source = new PromptSetDocument
            {
                assetListPath = "Assets/Docs/1_AssetList/AssetList_20260725_1830.json",
                templateName = "sdxl",
                createdAt = "2026-07-25 18:30",
                items = { FullItem(), new PromptItem { id = "item_002", assetType = "audio", isUI = false } }
            };

            PromptSetDocument restored = PromptSetDocument.FromDictionary(source.ToDictionary());

            Assert.IsNotNull(restored);
            Assert.AreEqual(source.assetListPath, restored.assetListPath);
            Assert.AreEqual(source.templateName, restored.templateName);
            Assert.AreEqual(source.createdAt, restored.createdAt);
            Assert.AreEqual(2, restored.items.Count);
            AssertSameItem(source.items[0], restored.items[0]);
            AssertSameItem(source.items[1], restored.items[1]);
        }

        /// <summary>
        /// 저장된 JSON 텍스트를 거치는 전체 경로(ToDictionary → MiniJson → FromDictionary)도
        /// 동일하게 왕복되는지 고정합니다. (따옴표가 들어간 프롬프트 문자열 포함)
        /// </summary>
        [Test]
        public void Document_RoundTripsThroughMiniJsonText()
        {
            var source = new PromptSetDocument
            {
                assetListPath = "Assets/Docs/1_AssetList/AssetList_20260725_1830.json",
                templateName = "default",
                createdAt = "2026-07-25 18:30",
                items = { FullItem() }
            };

            string json = MiniJson.Serialize(source.ToDictionary());
            var parsed = MiniJson.Deserialize(json) as Dictionary<string, object>;
            PromptSetDocument restored = PromptSetDocument.FromDictionary(parsed);

            Assert.IsNotNull(restored);
            Assert.AreEqual(1, restored.items.Count);
            AssertSameItem(source.items[0], restored.items[0]);
        }

        /// <summary>구 JSON 호환: 키가 빠진 항목도 예외 없이 기본값으로 로드되는지 고정합니다.</summary>
        [Test]
        public void Item_FromLegacyDictionary_MissingKeys_UseDefaults()
        {
            var legacy = new Dictionary<string, object>
            {
                { "id", "item_001" },
                { "positive", "hero" }
                // name / assetType / isUI / targetPrefabPath / targetObjectPath / description / negative 없음
            };

            PromptItem item = PromptItem.FromDictionary(legacy);

            Assert.IsNotNull(item);
            Assert.AreEqual("item_001", item.id);
            Assert.AreEqual("hero", item.positive);
            Assert.AreEqual(string.Empty, item.name);
            Assert.AreEqual("image", item.assetType, "assetType 기본값");
            Assert.IsFalse(item.isUI, "isUI 키가 없으면 false");
            Assert.AreEqual(string.Empty, item.targetPrefabPath);
            Assert.AreEqual(string.Empty, item.targetObjectPath);
            Assert.AreEqual(string.Empty, item.description);
            Assert.AreEqual(string.Empty, item.negative);
        }

        /// <summary>
        /// 구 JSON 호환: 문서 수준 키가 빠져도 기본값(templateName="default", items 빈 목록)으로 로드되고,
        /// 항목이 딕셔너리가 아니면 조용히 건너뛰는지 고정합니다.
        /// </summary>
        [Test]
        public void Document_FromLegacyDictionary_MissingOrMalformedKeys_UseDefaults()
        {
            var legacy = new Dictionary<string, object>
            {
                { "assetListPath", "Assets/Docs/AssetList_old.json" }
                // templateName / createdAt / items 없음
            };

            PromptSetDocument doc = PromptSetDocument.FromDictionary(legacy);

            Assert.IsNotNull(doc);
            Assert.AreEqual("Assets/Docs/AssetList_old.json", doc.assetListPath);
            Assert.AreEqual("default", doc.templateName, "templateName 기본값");
            Assert.AreEqual(string.Empty, doc.createdAt);
            Assert.AreEqual(0, doc.items.Count);

            var malformed = new Dictionary<string, object>
            {
                { "items", new List<object> { 3L, null, new Dictionary<string, object> { { "id", "ok" } } } }
            };

            PromptSetDocument doc2 = PromptSetDocument.FromDictionary(malformed);

            Assert.IsNotNull(doc2);
            Assert.AreEqual(1, doc2.items.Count, "딕셔너리가 아닌 항목은 조용히 건너뜁니다.");
            Assert.AreEqual("ok", doc2.items[0].id);
        }

        /// <summary>null 입력이 예외 대신 null을 돌려주는지 고정합니다.</summary>
        [Test]
        public void FromDictionary_Null_ReturnsNull()
        {
            Assert.IsNull(PromptItem.FromDictionary(null));
            Assert.IsNull(PromptSetDocument.FromDictionary(null));
        }
    }
}
