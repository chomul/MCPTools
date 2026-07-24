#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class OSUpgradeCatalogTests
{
    [Test]
    public void AssetMenu_UsesExpectedUpgradeCatalogPath()
    {
        CreateAssetMenuAttribute attribute = (CreateAssetMenuAttribute)Attribute.GetCustomAttribute(
            typeof(OSUpgradeCatalog),
            typeof(CreateAssetMenuAttribute));

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute.fileName, Is.EqualTo("UpgradeCatalog"));
        Assert.That(attribute.menuName, Is.EqualTo("OUROBOROS/Data/Upgrade Catalog"));
    }

    [Test]
    public void DefaultConfiguration_IsAcceptedAndContainsMvpUpgradeSet()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        Assert.That(catalog.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(catalog.UpgradeCount, Is.EqualTo(14));
        Assert.That(CountFamily(catalog, OSUpgradeFamily.Firepower), Is.EqualTo(3));
        Assert.That(CountFamily(catalog, OSUpgradeFamily.Body), Is.EqualTo(2));
        Assert.That(CountFamily(catalog, OSUpgradeFamily.Explosion), Is.EqualTo(3));
        Assert.That(CountFamily(catalog, OSUpgradeFamily.Survival), Is.EqualTo(3));
        Assert.That(CountFamily(catalog, OSUpgradeFamily.Utility), Is.EqualTo(3));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void DefaultDefinitions_ExposeIdFamilyMaxLevelConditionAndOperation()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        for (int i = 0; i < catalog.UpgradeCount; i++)
        {
            OSUpgradeDefinition upgrade = catalog.GetUpgradeAt(i);
            Assert.That(upgrade, Is.Not.Null);
            Assert.That(upgrade.Id, Is.Not.Empty);
            Assert.That(upgrade.MaxLevel, Is.GreaterThan(0));
            Assert.That(upgrade.ValuePerLevel, Is.Not.EqualTo(0f));
            Assert.That(upgrade.RequiredPlayerLevel, Is.GreaterThan(0));
            Assert.That(upgrade.IsP0Candidate, Is.True);
        }

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void CandidateSet_ReturnsThreeUniqueCandidates()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        OSConfigurationValidationResult result = catalog.BuildCandidateSet(
            Array.Empty<OSUpgradeProgressSnapshot>(),
            1,
            out OSUpgradeDefinitionSnapshot[] candidates);

        Assert.That(result, Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(candidates, Has.Length.EqualTo(3));
        Assert.That(candidates[0].Id, Is.EqualTo("head_damage_boost"));
        Assert.That(candidates[1].Id, Is.EqualTo("head_fire_rate_boost"));
        Assert.That(candidates[2].Id, Is.EqualTo("body_fragment_discount"));
        Assert.That(candidates[0].Id, Is.Not.EqualTo(candidates[1].Id));
        Assert.That(candidates[0].Id, Is.Not.EqualTo(candidates[2].Id));
        Assert.That(candidates[1].Id, Is.Not.EqualTo(candidates[2].Id));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void CandidateSet_ExcludesMaxLevelUpgrades()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();
        OSUpgradeProgressSnapshot[] progress =
        {
            new OSUpgradeProgressSnapshot("head_damage_boost", 3),
            new OSUpgradeProgressSnapshot("head_fire_rate_boost", 3)
        };

        OSConfigurationValidationResult result = catalog.BuildCandidateSet(
            progress,
            2,
            out OSUpgradeDefinitionSnapshot[] candidates);

        Assert.That(result, Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(candidates, Has.Length.EqualTo(3));
        Assert.That(ContainsCandidate(candidates, "head_damage_boost"), Is.False);
        Assert.That(ContainsCandidate(candidates, "head_fire_rate_boost"), Is.False);
        Assert.That(ContainsCandidate(candidates, "head_pierce_add"), Is.True);

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void CandidateSet_ReturnsConfigurationErrorWhenCandidatesAreInsufficient()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();
        OSUpgradeProgressSnapshot[] progress = BuildMaxedProgress(catalog, exceptIds: "elite_target_priority");

        OSConfigurationValidationResult result = catalog.BuildCandidateSet(
            progress,
            3,
            out OSUpgradeDefinitionSnapshot[] candidates);

        Assert.That(result, Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));
        Assert.That(candidates, Is.Empty);

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void DuplicateUpgradeIds_AreRejected()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();
        OSUpgradeDefinition[] upgrades = GetUpgradeArray(catalog);
        upgrades[1] = new OSUpgradeDefinition(
            upgrades[0].Id,
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadFireRateMultiplier,
            3,
            0.12f,
            true,
            1);

        Assert.That(catalog.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void InvalidUpgradeValues_AreRejected()
    {
        AssertCatalogValueRejected(new OSUpgradeDefinition(
            string.Empty,
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            3,
            0.15f,
            true,
            1));

        AssertCatalogValueRejected(new OSUpgradeDefinition(
            "invalid_max",
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            0,
            0.15f,
            true,
            1));

        AssertCatalogValueRejected(new OSUpgradeDefinition(
            "invalid_value",
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            3,
            float.NaN,
            true,
            1));

        AssertCatalogValueRejected(new OSUpgradeDefinition(
            "invalid_required_level",
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            3,
            0.15f,
            true,
            0));
    }

    [Test]
    public void MaximumBodyUpgrade_IsNotP0Eligible()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        for (int i = 0; i < catalog.UpgradeCount; i++)
        {
            OSUpgradeDefinition upgrade = catalog.GetUpgradeAt(i);
            Assert.That(upgrade.Operation, Is.Not.EqualTo(OSUpgradeOperation.BodyMaxSegmentsAdd));
            Assert.That(upgrade.Id, Is.Not.EqualTo("body_max_segments_add"));
            Assert.That(upgrade.Id, Is.Not.EqualTo("max_body_segments_add"));
        }

        OSUpgradeDefinition[] upgrades = GetUpgradeArray(catalog);
        upgrades[0] = new OSUpgradeDefinition(
            "body_max_segments_add",
            OSUpgradeFamily.Body,
            OSUpgradeOperation.BodyMaxSegmentsAdd,
            2,
            3f,
            true,
            1);

        Assert.That(catalog.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void InvalidProgressInput_IsRejected()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        Assert.That(catalog.BuildCandidateSet(
            new[] { new OSUpgradeProgressSnapshot("head_damage_boost", -1) },
            1,
            out _), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        Assert.That(catalog.BuildCandidateSet(
            new[]
            {
                new OSUpgradeProgressSnapshot("head_damage_boost", 1),
                new OSUpgradeProgressSnapshot("head_damage_boost", 2)
            },
            1,
            out _), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        Assert.That(catalog.BuildCandidateSet(
            new[] { new OSUpgradeProgressSnapshot("unknown_upgrade", 1) },
            1,
            out _), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void Snapshot_IsValueCopyAndDoesNotMutateSourceAsset()
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();
        OSUpgradeCatalogSnapshot snapshot = catalog.CreateSnapshot();

        OSUpgradeDefinition[] upgrades = GetUpgradeArray(catalog);
        upgrades[0] = new OSUpgradeDefinition(
            "changed",
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            3,
            0.15f,
            true,
            1);

        Assert.That(snapshot.Upgrades.Length, Is.EqualTo(14));
        Assert.That(snapshot.Upgrades[0].Id, Is.EqualTo("head_damage_boost"));
        Assert.That(catalog.GetUpgradeAt(0).Id, Is.EqualTo("changed"));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    private static int CountFamily(OSUpgradeCatalog catalog, OSUpgradeFamily family)
    {
        int count = 0;

        for (int i = 0; i < catalog.UpgradeCount; i++)
        {
            if (catalog.GetUpgradeAt(i).Family == family)
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsCandidate(OSUpgradeDefinitionSnapshot[] candidates, string id)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private static OSUpgradeProgressSnapshot[] BuildMaxedProgress(OSUpgradeCatalog catalog, string exceptIds)
    {
        OSUpgradeProgressSnapshot[] progress = new OSUpgradeProgressSnapshot[catalog.UpgradeCount - 1];
        int count = 0;

        for (int i = 0; i < catalog.UpgradeCount; i++)
        {
            OSUpgradeDefinition upgrade = catalog.GetUpgradeAt(i);
            if (upgrade.Id == exceptIds)
            {
                continue;
            }

            progress[count] = new OSUpgradeProgressSnapshot(upgrade.Id, upgrade.MaxLevel);
            count++;
        }

        return progress;
    }

    private static void AssertCatalogValueRejected(OSUpgradeDefinition replacement)
    {
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();
        OSUpgradeDefinition[] upgrades = GetUpgradeArray(catalog);
        upgrades[0] = replacement;

        Assert.That(catalog.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(catalog);
    }

    private static OSUpgradeDefinition[] GetUpgradeArray(OSUpgradeCatalog catalog)
    {
        FieldInfo field = typeof(OSUpgradeCatalog).GetField("upgrades", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        return (OSUpgradeDefinition[])field.GetValue(catalog);
    }
}
#endif
