using System;

public sealed class OSSessionRuntimeState
{
    private const int InitialLevel = 1;
    private const int InitialExperienceRequirement = 15;
    private const float ExperienceRequirementGrowth = 1.18f;

    private readonly OSPlayerBalanceSnapshot playerBalance;
    private readonly OSBodyBalanceSnapshot bodyBalance;
    private readonly OSEncounterBalanceSnapshot encounterBalance;
    private readonly OSUpgradeCatalogSnapshot upgradeCatalog;
    private readonly int[] upgradeLevels;

    private OSSessionRuntimeState(
        OSPlayerBalanceSnapshot playerBalance,
        OSBodyBalanceSnapshot bodyBalance,
        OSEncounterBalanceSnapshot encounterBalance,
        OSUpgradeCatalogSnapshot upgradeCatalog)
    {
        this.playerBalance = playerBalance;
        this.bodyBalance = bodyBalance;
        this.encounterBalance = CopyEncounterSnapshot(encounterBalance);
        this.upgradeCatalog = CopyUpgradeCatalogSnapshot(upgradeCatalog);
        upgradeLevels = new int[this.upgradeCatalog.Upgrades.Length];

        State = OSSessionState.Boot;
        MaxHp = playerBalance.Hp;
        CurrentHp = playerBalance.Hp;
        Level = InitialLevel;
        ExperienceToNextLevel = InitialExperienceRequirement;
    }

    public event Action<OSSessionRuntimeState> RuntimeStateChanged;

    public OSSessionState State { get; private set; }
    public float CurrentHp { get; private set; }
    public float MaxHp { get; private set; }
    public int Experience { get; private set; }
    public int ExperienceToNextLevel { get; private set; }
    public int BodyFragments { get; private set; }
    public int Level { get; private set; }
    public int PendingBodyRequests { get; private set; }
    public int PendingLevelUpRequests { get; private set; }
    public int TotalExperienceCollected { get; private set; }
    public int TotalBodyFragmentsCollected { get; private set; }
    public int TotalHealingCollected { get; private set; }
    public int UpgradesApplied { get; private set; }
    public int MaxActiveBodySegments { get; private set; }
    public int ExplosionKillCount { get; private set; }
    public bool BossDefeated { get; private set; }
    public float SurvivalTimeSeconds { get; private set; }
    public OSSessionSummary LastSummary { get; private set; }
    public OSPlayerBalanceSnapshot PlayerBalance => playerBalance;
    public OSBodyBalanceSnapshot BodyBalance => bodyBalance;
    public OSEncounterBalanceSnapshot EncounterBalance => CopyEncounterSnapshot(encounterBalance);
    public OSUpgradeCatalogSnapshot UpgradeCatalog => CopyUpgradeCatalogSnapshot(upgradeCatalog);

    public static OSRuleResult<OSSessionRuntimeState> InitializeFrom(
        OSPlayerBalanceData playerBalance,
        OSBodyBalanceData bodyBalance,
        OSEncounterBalanceData encounterBalance,
        OSUpgradeCatalog upgradeCatalog)
    {
        if (playerBalance == null ||
            bodyBalance == null ||
            encounterBalance == null ||
            upgradeCatalog == null ||
            playerBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            encounterBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            upgradeCatalog.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<OSSessionRuntimeState>.Rejected(
                OSResultCode.ConfigurationError,
                "configuration_invalid");
        }

        OSSessionRuntimeState runtimeState = new OSSessionRuntimeState(
            playerBalance.CreateSnapshot(),
            bodyBalance.CreateSnapshot(),
            encounterBalance.CreateSnapshot(),
            upgradeCatalog.CreateSnapshot());

        return OSRuleResult<OSSessionRuntimeState>.Accept(runtimeState);
    }

    public OSRuleResult<OSSessionState> SetState(OSSessionState state)
    {
        if (State == OSSessionState.Dead && state != OSSessionState.Dead)
        {
            return OSRuleResult<OSSessionState>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (State == state)
        {
            return OSRuleResult<OSSessionState>.Accept(State);
        }

        State = state;
        RaiseRuntimeStateChanged();
        return OSRuleResult<OSSessionState>.Accept(State);
    }

    public OSRuleResult<OSPickupApplyResult> ApplyPickup(OSPickupType pickupType, int amount)
    {
        if (State == OSSessionState.Dead)
        {
            return OSRuleResult<OSPickupApplyResult>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (amount <= 0)
        {
            return OSRuleResult<OSPickupApplyResult>.Rejected(OSResultCode.ConfigurationError, "pickup_amount_invalid");
        }

        int levelUpsBefore = PendingLevelUpRequests;
        int bodyRequestsBefore = PendingBodyRequests;

        switch (pickupType)
        {
            case OSPickupType.Experience:
                ApplyExperience(amount);
                break;
            case OSPickupType.BodyFragment:
                ApplyBodyFragments(amount);
                break;
            case OSPickupType.Heal:
                ApplyHealInternal(amount);
                break;
            default:
                return OSRuleResult<OSPickupApplyResult>.Rejected(OSResultCode.ConfigurationError, "pickup_type_invalid");
        }

        OSPickupApplyResult result = new OSPickupApplyResult(
            pickupType,
            amount,
            PendingLevelUpRequests - levelUpsBefore,
            PendingBodyRequests - bodyRequestsBefore,
            CurrentHp,
            Experience,
            BodyFragments);

        RaiseRuntimeStateChanged();
        return OSRuleResult<OSPickupApplyResult>.Accept(result);
    }

    public OSRuleResult<float> ApplyHeadDamage(float amount)
    {
        if (State == OSSessionState.Dead)
        {
            return OSRuleResult<float>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (!IsPositiveFinite(amount))
        {
            return OSRuleResult<float>.Rejected(OSResultCode.ConfigurationError, "head_damage_amount_invalid");
        }

        CurrentHp = Math.Max(0f, CurrentHp - amount);
        RaiseRuntimeStateChanged();
        return OSRuleResult<float>.Accept(CurrentHp);
    }

    public OSRuleResult<float> ApplyHeal(int amount)
    {
        if (State == OSSessionState.Dead)
        {
            return OSRuleResult<float>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (amount <= 0)
        {
            return OSRuleResult<float>.Rejected(OSResultCode.ConfigurationError, "heal_amount_invalid");
        }

        ApplyHealInternal(amount);
        RaiseRuntimeStateChanged();
        return OSRuleResult<float>.Accept(CurrentHp);
    }

    public OSRuleResult<int> ApplyUpgrade(string upgradeId)
    {
        if (State == OSSessionState.Dead)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        int index = FindUpgradeIndex(upgradeId);
        if (index < 0)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "upgrade_unknown");
        }

        OSUpgradeDefinitionSnapshot upgrade = upgradeCatalog.Upgrades[index];
        if (upgradeLevels[index] >= upgrade.MaxLevel)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "upgrade_max_level");
        }

        upgradeLevels[index]++;
        UpgradesApplied++;
        ApplyUpgradeOperation(upgrade);
        RaiseRuntimeStateChanged();

        return OSRuleResult<int>.Accept(upgradeLevels[index]);
    }

    public OSUpgradeProgressSnapshot[] CreateUpgradeProgressSnapshots()
    {
        OSUpgradeProgressSnapshot[] progress = new OSUpgradeProgressSnapshot[upgradeCatalog.Upgrades.Length];
        for (int i = 0; i < upgradeCatalog.Upgrades.Length; i++)
        {
            progress[i] = new OSUpgradeProgressSnapshot(upgradeCatalog.Upgrades[i].Id, upgradeLevels[i]);
        }

        return progress;
    }

    public int GetUpgradeLevel(string upgradeId)
    {
        int index = FindUpgradeIndex(upgradeId);
        return index < 0 ? 0 : upgradeLevels[index];
    }

    public void RecordActiveBodySegments(int activeBodySegments)
    {
        if (activeBodySegments > MaxActiveBodySegments)
        {
            MaxActiveBodySegments = activeBodySegments;
            RaiseRuntimeStateChanged();
        }
    }

    public void RecordExplosionKills(int killCount)
    {
        if (killCount <= 0)
        {
            return;
        }

        ExplosionKillCount += killCount;
        RaiseRuntimeStateChanged();
    }

    public void RecordBossDefeated()
    {
        if (BossDefeated)
        {
            return;
        }

        BossDefeated = true;
        RaiseRuntimeStateChanged();
    }

    public OSRuleResult<OSSessionSummary> BuildSummary(
        OSResultCode resultCode,
        float survivalTimeSeconds,
        string reasonKey)
    {
        if (survivalTimeSeconds < 0f || float.IsNaN(survivalTimeSeconds) || float.IsInfinity(survivalTimeSeconds))
        {
            return OSRuleResult<OSSessionSummary>.Rejected(OSResultCode.ConfigurationError, "survival_time_invalid");
        }

        SurvivalTimeSeconds = survivalTimeSeconds;
        if (resultCode != OSResultCode.Accepted)
        {
            State = OSSessionState.Dead;
        }

        LastSummary = new OSSessionSummary(
            resultCode,
            reasonKey,
            SurvivalTimeSeconds,
            Level,
            CurrentHp,
            MaxHp,
            MaxActiveBodySegments,
            ExplosionKillCount,
            BossDefeated,
            TotalExperienceCollected,
            TotalBodyFragmentsCollected,
            UpgradesApplied);

        RaiseRuntimeStateChanged();
        return OSRuleResult<OSSessionSummary>.Accept(LastSummary);
    }

    private void ApplyExperience(int amount)
    {
        TotalExperienceCollected += amount;
        Experience += amount;

        while (Experience >= ExperienceToNextLevel)
        {
            Experience -= ExperienceToNextLevel;
            Level++;
            PendingLevelUpRequests++;
            ExperienceToNextLevel = CalculateNextExperienceRequirement(ExperienceToNextLevel);
        }
    }

    private void ApplyBodyFragments(int amount)
    {
        TotalBodyFragmentsCollected += amount;
        BodyFragments += amount;

        while (BodyFragments >= bodyBalance.BodyFragmentsPerSegment)
        {
            BodyFragments -= bodyBalance.BodyFragmentsPerSegment;
            PendingBodyRequests++;
        }
    }

    private void ApplyHealInternal(int amount)
    {
        TotalHealingCollected += amount;
        CurrentHp = Math.Min(MaxHp, CurrentHp + amount);
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void ApplyUpgradeOperation(OSUpgradeDefinitionSnapshot upgrade)
    {
        if (upgrade.Operation == OSUpgradeOperation.MaxHpMultiplier)
        {
            float previousMaxHp = MaxHp;
            MaxHp += playerBalance.Hp * upgrade.ValuePerLevel;
            CurrentHp += MaxHp - previousMaxHp;
        }
    }

    private int CalculateNextExperienceRequirement(int currentRequirement)
    {
        return Math.Max(1, (int)Math.Ceiling(currentRequirement * ExperienceRequirementGrowth));
    }

    private int FindUpgradeIndex(string upgradeId)
    {
        if (string.IsNullOrWhiteSpace(upgradeId))
        {
            return -1;
        }

        for (int i = 0; i < upgradeCatalog.Upgrades.Length; i++)
        {
            if (upgradeCatalog.Upgrades[i].Id == upgradeId)
            {
                return i;
            }
        }

        return -1;
    }

    private void RaiseRuntimeStateChanged()
    {
        RuntimeStateChanged?.Invoke(this);
    }

    private static OSEncounterBalanceSnapshot CopyEncounterSnapshot(OSEncounterBalanceSnapshot source)
    {
        OSEnemyPrototypeSnapshot[] enemies = new OSEnemyPrototypeSnapshot[source.EnemyPrototypes.Length];
        Array.Copy(source.EnemyPrototypes, enemies, enemies.Length);

        OSEncounterWaveSnapshot[] waves = new OSEncounterWaveSnapshot[source.Waves.Length];
        Array.Copy(source.Waves, waves, waves.Length);

        return new OSEncounterBalanceSnapshot(
            source.ActiveEnemyLimit,
            source.ActiveProjectileLimit,
            source.HeadProjectilePrefabKey,
            source.BodyProjectilePrefabKey,
            source.ControlProjectilePrefabKey,
            source.ExperiencePickupPrefabKey,
            source.BodyFragmentPickupPrefabKey,
            source.HealPickupPrefabKey,
            enemies,
            waves);
    }

    private static OSUpgradeCatalogSnapshot CopyUpgradeCatalogSnapshot(OSUpgradeCatalogSnapshot source)
    {
        OSUpgradeDefinitionSnapshot[] upgrades = new OSUpgradeDefinitionSnapshot[source.Upgrades.Length];
        Array.Copy(source.Upgrades, upgrades, upgrades.Length);
        return new OSUpgradeCatalogSnapshot(upgrades);
    }
}

public enum OSSessionState
{
    Boot,
    BodyRoleSelection,
    Combat,
    ExplosionTelegraph,
    LevelUpSelection,
    Dead
}

public enum OSResultCode
{
    Accepted,
    RejectedState,
    RejectedCapacity,
    Duplicate,
    ConfigurationError
}

public enum OSPickupType
{
    Experience,
    BodyFragment,
    Heal
}

public readonly struct OSRuleResult<T>
{
    private OSRuleResult(OSResultCode code, string reasonKey, T payload)
    {
        Code = code;
        ReasonKey = reasonKey;
        Payload = payload;
    }

    public OSResultCode Code { get; }
    public string ReasonKey { get; }
    public T Payload { get; }
    public bool IsAccepted => Code == OSResultCode.Accepted;

    public static OSRuleResult<T> Accept(T payload)
    {
        return new OSRuleResult<T>(OSResultCode.Accepted, string.Empty, payload);
    }

    public static OSRuleResult<T> Rejected(OSResultCode code, string reasonKey)
    {
        return new OSRuleResult<T>(code, reasonKey, default);
    }
}

public readonly struct OSPickupApplyResult
{
    public OSPickupApplyResult(
        OSPickupType pickupType,
        int amount,
        int levelUpRequestsCreated,
        int bodyRequestsCreated,
        float currentHp,
        int remainingExperience,
        int remainingBodyFragments)
    {
        PickupType = pickupType;
        Amount = amount;
        LevelUpRequestsCreated = levelUpRequestsCreated;
        BodyRequestsCreated = bodyRequestsCreated;
        CurrentHp = currentHp;
        RemainingExperience = remainingExperience;
        RemainingBodyFragments = remainingBodyFragments;
    }

    public OSPickupType PickupType { get; }
    public int Amount { get; }
    public int LevelUpRequestsCreated { get; }
    public int BodyRequestsCreated { get; }
    public float CurrentHp { get; }
    public int RemainingExperience { get; }
    public int RemainingBodyFragments { get; }
}

public readonly struct OSSessionSummary
{
    public OSSessionSummary(
        OSResultCode resultCode,
        string reasonKey,
        float survivalTimeSeconds,
        int level,
        float currentHp,
        float maxHp,
        int maxActiveBodySegments,
        int explosionKillCount,
        bool bossDefeated,
        int totalExperienceCollected,
        int totalBodyFragmentsCollected,
        int upgradesApplied)
    {
        ResultCode = resultCode;
        ReasonKey = reasonKey;
        SurvivalTimeSeconds = survivalTimeSeconds;
        Level = level;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        MaxActiveBodySegments = maxActiveBodySegments;
        ExplosionKillCount = explosionKillCount;
        BossDefeated = bossDefeated;
        TotalExperienceCollected = totalExperienceCollected;
        TotalBodyFragmentsCollected = totalBodyFragmentsCollected;
        UpgradesApplied = upgradesApplied;
    }

    public OSResultCode ResultCode { get; }
    public string ReasonKey { get; }
    public float SurvivalTimeSeconds { get; }
    public int Level { get; }
    public float CurrentHp { get; }
    public float MaxHp { get; }
    public int MaxActiveBodySegments { get; }
    public int ExplosionKillCount { get; }
    public bool BossDefeated { get; }
    public int TotalExperienceCollected { get; }
    public int TotalBodyFragmentsCollected { get; }
    public int UpgradesApplied { get; }
}
