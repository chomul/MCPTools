using System;
using UnityEngine;

[CreateAssetMenu(fileName = "UpgradeCatalog", menuName = "OUROBOROS/Data/Upgrade Catalog")]
public sealed class OSUpgradeCatalog : ScriptableObject
{
    private const int CandidateCount = 3;

    [SerializeField] private OSUpgradeDefinition[] upgrades =
    {
        new OSUpgradeDefinition("head_damage_boost", OSUpgradeFamily.Firepower, OSUpgradeOperation.HeadDamageMultiplier, 3, 0.15f, true, 1),
        new OSUpgradeDefinition("head_fire_rate_boost", OSUpgradeFamily.Firepower, OSUpgradeOperation.HeadFireRateMultiplier, 3, 0.12f, true, 1),
        new OSUpgradeDefinition("head_pierce_add", OSUpgradeFamily.Firepower, OSUpgradeOperation.HeadPierceAdd, 3, 1f, true, 2),
        new OSUpgradeDefinition("body_fragment_discount", OSUpgradeFamily.Body, OSUpgradeOperation.BodyFragmentRequirementMultiplier, 2, -0.10f, true, 1),
        new OSUpgradeDefinition("body_damage_bonus_add", OSUpgradeFamily.Body, OSUpgradeOperation.BodyDamageBonusAdd, 2, 0.01f, true, 1),
        new OSUpgradeDefinition("explosion_radius_boost", OSUpgradeFamily.Explosion, OSUpgradeOperation.ExplosionRadiusMultiplier, 3, 0.15f, true, 1),
        new OSUpgradeDefinition("explosion_damage_boost", OSUpgradeFamily.Explosion, OSUpgradeOperation.ExplosionDamageMultiplier, 3, 0.20f, true, 1),
        new OSUpgradeDefinition("explosion_consumption_discount", OSUpgradeFamily.Explosion, OSUpgradeOperation.ExplosionConsumptionRatioAdd, 3, -0.10f, true, 2),
        new OSUpgradeDefinition("max_hp_boost", OSUpgradeFamily.Survival, OSUpgradeOperation.MaxHpMultiplier, 2, 0.20f, true, 1),
        new OSUpgradeDefinition("move_speed_boost", OSUpgradeFamily.Survival, OSUpgradeOperation.MoveSpeedMultiplier, 2, 0.08f, true, 1),
        new OSUpgradeDefinition("heal_gain_boost", OSUpgradeFamily.Survival, OSUpgradeOperation.HealGainMultiplier, 2, 0.25f, true, 1),
        new OSUpgradeDefinition("magnet_radius_boost", OSUpgradeFamily.Utility, OSUpgradeOperation.MagnetRadiusMultiplier, 2, 0.30f, true, 1),
        new OSUpgradeDefinition("experience_gain_boost", OSUpgradeFamily.Utility, OSUpgradeOperation.ExperienceGainMultiplier, 2, 0.10f, true, 1),
        new OSUpgradeDefinition("elite_target_priority", OSUpgradeFamily.Utility, OSUpgradeOperation.EliteTargetPriority, 2, 1f, true, 2)
    };

    public int UpgradeCount => upgrades?.Length ?? 0;

    public OSUpgradeDefinition GetUpgradeAt(int index)
    {
        if (upgrades == null || index < 0 || index >= upgrades.Length)
        {
            return null;
        }

        return upgrades[index];
    }

    public OSUpgradeDefinition GetUpgrade(string id)
    {
        if (upgrades == null)
        {
            return null;
        }

        for (int i = 0; i < upgrades.Length; i++)
        {
            OSUpgradeDefinition upgrade = upgrades[i];
            if (upgrade != null && upgrade.Id == id)
            {
                return upgrade;
            }
        }

        return null;
    }

    public OSConfigurationValidationResult BuildCandidateSet(
        OSUpgradeProgressSnapshot[] currentLevels,
        int playerLevel,
        out OSUpgradeDefinitionSnapshot[] candidates)
    {
        candidates = Array.Empty<OSUpgradeDefinitionSnapshot>();

        if (ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            playerLevel <= 0 ||
            HasDuplicateProgressIds(currentLevels) ||
            HasUnknownProgressId(currentLevels))
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        OSUpgradeDefinitionSnapshot[] buffer = new OSUpgradeDefinitionSnapshot[CandidateCount];
        int count = 0;

        for (int i = 0; i < upgrades.Length && count < CandidateCount; i++)
        {
            OSUpgradeDefinition upgrade = upgrades[i];
            if (IsEligible(upgrade, currentLevels, playerLevel))
            {
                buffer[count] = upgrade.CreateSnapshot();
                count++;
            }
        }

        if (count < CandidateCount)
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        candidates = buffer;
        return OSConfigurationValidationResult.Accepted;
    }

    public OSUpgradeCatalogSnapshot CreateSnapshot()
    {
        if (upgrades == null)
        {
            return new OSUpgradeCatalogSnapshot(Array.Empty<OSUpgradeDefinitionSnapshot>());
        }

        OSUpgradeDefinitionSnapshot[] snapshots = new OSUpgradeDefinitionSnapshot[upgrades.Length];
        for (int i = 0; i < upgrades.Length; i++)
        {
            snapshots[i] = upgrades[i].CreateSnapshot();
        }

        return new OSUpgradeCatalogSnapshot(snapshots);
    }

    public OSConfigurationValidationResult ValidateConfiguration()
    {
        if (upgrades == null || upgrades.Length < CandidateCount)
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        for (int i = 0; i < upgrades.Length; i++)
        {
            OSUpgradeDefinition upgrade = upgrades[i];
            if (upgrade == null ||
                !upgrade.IsValid() ||
                IsMaximumBodyUpgrade(upgrade) ||
                ContainsDuplicateUpgradeId(upgrade.Id, i))
            {
                return OSConfigurationValidationResult.ConfigurationError;
            }
        }

        return HasAtLeastThreeP0Candidates()
            ? OSConfigurationValidationResult.Accepted
            : OSConfigurationValidationResult.ConfigurationError;
    }

    private void OnValidate()
    {
        ValidateConfiguration();
    }

    private bool IsEligible(
        OSUpgradeDefinition upgrade,
        OSUpgradeProgressSnapshot[] currentLevels,
        int playerLevel)
    {
        if (upgrade == null || !upgrade.IsP0Candidate || upgrade.RequiredPlayerLevel > playerLevel)
        {
            return false;
        }

        return GetCurrentLevel(currentLevels, upgrade.Id) < upgrade.MaxLevel;
    }

    private int GetCurrentLevel(OSUpgradeProgressSnapshot[] currentLevels, string id)
    {
        if (currentLevels == null)
        {
            return 0;
        }

        for (int i = 0; i < currentLevels.Length; i++)
        {
            if (currentLevels[i].Id == id)
            {
                return currentLevels[i].CurrentLevel;
            }
        }

        return 0;
    }

    private bool HasDuplicateProgressIds(OSUpgradeProgressSnapshot[] currentLevels)
    {
        if (currentLevels == null)
        {
            return false;
        }

        for (int i = 0; i < currentLevels.Length; i++)
        {
            if (IsBlank(currentLevels[i].Id) || currentLevels[i].CurrentLevel < 0)
            {
                return true;
            }

            for (int j = i + 1; j < currentLevels.Length; j++)
            {
                if (currentLevels[i].Id == currentLevels[j].Id)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasUnknownProgressId(OSUpgradeProgressSnapshot[] currentLevels)
    {
        if (currentLevels == null)
        {
            return false;
        }

        for (int i = 0; i < currentLevels.Length; i++)
        {
            if (GetUpgrade(currentLevels[i].Id) == null)
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsDuplicateUpgradeId(string id, int currentIndex)
    {
        for (int i = 0; i < currentIndex; i++)
        {
            if (upgrades[i] != null && upgrades[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAtLeastThreeP0Candidates()
    {
        int count = 0;

        for (int i = 0; i < upgrades.Length; i++)
        {
            OSUpgradeDefinition upgrade = upgrades[i];
            if (upgrade != null && upgrade.IsP0Candidate && !IsMaximumBodyUpgrade(upgrade))
            {
                count++;
            }
        }

        return count >= CandidateCount;
    }

    private static bool IsMaximumBodyUpgrade(OSUpgradeDefinition upgrade)
    {
        return upgrade.Operation == OSUpgradeOperation.BodyMaxSegmentsAdd ||
            upgrade.Id == "body_max_segments_add" ||
            upgrade.Id == "max_body_segments_add";
    }

    internal static bool IsPositive(int value)
    {
        return value > 0;
    }

    internal static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool IsFiniteNonZero(float value)
    {
        return value != 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool IsBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value);
    }
}

public enum OSUpgradeFamily
{
    Firepower,
    Body,
    Explosion,
    Survival,
    Utility
}

public enum OSUpgradeOperation
{
    HeadDamageMultiplier,
    HeadFireRateMultiplier,
    HeadPierceAdd,
    BodyFragmentRequirementMultiplier,
    BodyDamageBonusAdd,
    BodyMaxSegmentsAdd,
    ExplosionRadiusMultiplier,
    ExplosionDamageMultiplier,
    ExplosionConsumptionRatioAdd,
    MaxHpMultiplier,
    MoveSpeedMultiplier,
    HealGainMultiplier,
    MagnetRadiusMultiplier,
    ExperienceGainMultiplier,
    EliteTargetPriority
}

[Serializable]
public sealed class OSUpgradeDefinition
{
    [SerializeField] private string id;
    [SerializeField] private OSUpgradeFamily family;
    [SerializeField] private OSUpgradeOperation operation;
    [SerializeField] private int maxLevel;
    [SerializeField] private float valuePerLevel;
    [SerializeField] private bool isP0Candidate;
    [SerializeField] private int requiredPlayerLevel;

    public OSUpgradeDefinition(
        string id,
        OSUpgradeFamily family,
        OSUpgradeOperation operation,
        int maxLevel,
        float valuePerLevel,
        bool isP0Candidate,
        int requiredPlayerLevel)
    {
        this.id = id;
        this.family = family;
        this.operation = operation;
        this.maxLevel = maxLevel;
        this.valuePerLevel = valuePerLevel;
        this.isP0Candidate = isP0Candidate;
        this.requiredPlayerLevel = requiredPlayerLevel;
    }

    public string Id => id;
    public OSUpgradeFamily Family => family;
    public OSUpgradeOperation Operation => operation;
    public int MaxLevel => maxLevel;
    public float ValuePerLevel => valuePerLevel;
    public bool IsP0Candidate => isP0Candidate;
    public int RequiredPlayerLevel => requiredPlayerLevel;

    public bool IsValid()
    {
        return !OSUpgradeCatalog.IsBlank(id) &&
            OSUpgradeCatalog.IsPositive(maxLevel) &&
            OSUpgradeCatalog.IsFiniteNonZero(valuePerLevel) &&
            OSUpgradeCatalog.IsPositive(requiredPlayerLevel);
    }

    public OSUpgradeDefinitionSnapshot CreateSnapshot()
    {
        return new OSUpgradeDefinitionSnapshot(
            id,
            family,
            operation,
            maxLevel,
            valuePerLevel,
            isP0Candidate,
            requiredPlayerLevel);
    }
}

[Serializable]
public readonly struct OSUpgradeProgressSnapshot
{
    public OSUpgradeProgressSnapshot(string id, int currentLevel)
    {
        Id = id;
        CurrentLevel = currentLevel;
    }

    public string Id { get; }
    public int CurrentLevel { get; }
}

[Serializable]
public readonly struct OSUpgradeCatalogSnapshot
{
    public OSUpgradeCatalogSnapshot(OSUpgradeDefinitionSnapshot[] upgrades)
    {
        Upgrades = upgrades;
    }

    public OSUpgradeDefinitionSnapshot[] Upgrades { get; }
}

[Serializable]
public readonly struct OSUpgradeDefinitionSnapshot
{
    public OSUpgradeDefinitionSnapshot(
        string id,
        OSUpgradeFamily family,
        OSUpgradeOperation operation,
        int maxLevel,
        float valuePerLevel,
        bool isP0Candidate,
        int requiredPlayerLevel)
    {
        Id = id;
        Family = family;
        Operation = operation;
        MaxLevel = maxLevel;
        ValuePerLevel = valuePerLevel;
        IsP0Candidate = isP0Candidate;
        RequiredPlayerLevel = requiredPlayerLevel;
    }

    public string Id { get; }
    public OSUpgradeFamily Family { get; }
    public OSUpgradeOperation Operation { get; }
    public int MaxLevel { get; }
    public float ValuePerLevel { get; }
    public bool IsP0Candidate { get; }
    public int RequiredPlayerLevel { get; }
}
