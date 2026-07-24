#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSSessionRuntimeStateTests
{
    [Test]
    public void InitializeFrom_DeepCopiesBalanceAndCatalogSnapshots()
    {
        OSSessionRuntimeState state = CreateState();

        Assert.That(state.CurrentHp, Is.EqualTo(100f));
        Assert.That(state.MaxHp, Is.EqualTo(100f));
        Assert.That(state.PlayerBalance.MoveSpeed, Is.EqualTo(5.5f));
        Assert.That(state.BodyBalance.BodyFragmentsPerSegment, Is.EqualTo(12));
        Assert.That(state.EncounterBalance.EnemyPrototypes.Length, Is.EqualTo(6));
        Assert.That(state.UpgradeCatalog.Upgrades.Length, Is.EqualTo(14));

        OSEncounterBalanceSnapshot encounter = state.EncounterBalance;
        encounter.EnemyPrototypes[0] = new OSEnemyPrototypeSnapshot(
            "changed",
            OSEnemyClass.Normal,
            "changed",
            1f,
            1f,
            1f,
            0,
            0,
            0f);

        OSUpgradeCatalogSnapshot catalog = state.UpgradeCatalog;
        catalog.Upgrades[0] = new OSUpgradeDefinitionSnapshot(
            "changed",
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            1,
            1f,
            true,
            1);

        Assert.That(state.EncounterBalance.EnemyPrototypes[0].Id, Is.EqualTo("enemy_chaser"));
        Assert.That(state.UpgradeCatalog.Upgrades[0].Id, Is.EqualTo("head_damage_boost"));
    }

    [Test]
    public void ApplyPickup_TracksExperienceFragmentsHealthAndStatisticsIndependently()
    {
        OSSessionRuntimeState state = CreateState();
        int eventCount = 0;
        state.RuntimeStateChanged += _ => eventCount++;

        OSRuleResult<OSPickupApplyResult> experience = state.ApplyPickup(OSPickupType.Experience, 35);
        OSRuleResult<OSPickupApplyResult> fragments = state.ApplyPickup(OSPickupType.BodyFragment, 25);
        OSRuleResult<OSPickupApplyResult> heal = state.ApplyPickup(OSPickupType.Heal, 20);

        Assert.That(experience.IsAccepted, Is.True);
        Assert.That(experience.Payload.LevelUpRequestsCreated, Is.EqualTo(2));
        Assert.That(state.Level, Is.EqualTo(3));
        Assert.That(state.Experience, Is.EqualTo(2));
        Assert.That(state.ExperienceToNextLevel, Is.EqualTo(22));
        Assert.That(fragments.Payload.BodyRequestsCreated, Is.EqualTo(2));
        Assert.That(state.BodyFragments, Is.EqualTo(1));
        Assert.That(heal.Payload.CurrentHp, Is.EqualTo(100f));
        Assert.That(state.TotalExperienceCollected, Is.EqualTo(35));
        Assert.That(state.TotalBodyFragmentsCollected, Is.EqualTo(25));
        Assert.That(state.TotalHealingCollected, Is.EqualTo(20));
        Assert.That(eventCount, Is.EqualTo(3));
    }

    [Test]
    public void ApplyUpgrade_TracksLevelAndDoesNotMutateCatalog()
    {
        OSSessionRuntimeState state = CreateState();
        int eventCount = 0;
        state.RuntimeStateChanged += _ => eventCount++;

        OSRuleResult<int> result = state.ApplyUpgrade("max_hp_boost");
        OSUpgradeProgressSnapshot[] progress = state.CreateUpgradeProgressSnapshots();

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload, Is.EqualTo(1));
        Assert.That(state.GetUpgradeLevel("max_hp_boost"), Is.EqualTo(1));
        Assert.That(state.MaxHp, Is.EqualTo(120f));
        Assert.That(state.CurrentHp, Is.EqualTo(120f));
        Assert.That(progress, Has.Length.EqualTo(14));
        Assert.That(FindProgress(progress, "max_hp_boost").CurrentLevel, Is.EqualTo(1));
        Assert.That(state.UpgradeCatalog.Upgrades[8].Id, Is.EqualTo("max_hp_boost"));
        Assert.That(eventCount, Is.EqualTo(1));
    }

    [Test]
    public void ApplyUpgrade_RejectsUnknownAndMaxLevelWithoutChangingState()
    {
        OSSessionRuntimeState state = CreateState();
        int eventCount = 0;
        state.RuntimeStateChanged += _ => eventCount++;

        Assert.That(state.ApplyUpgrade("missing").Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(state.ApplyUpgrade("body_fragment_discount").IsAccepted, Is.True);
        Assert.That(state.ApplyUpgrade("body_fragment_discount").IsAccepted, Is.True);

        OSRuleResult<int> maxed = state.ApplyUpgrade("body_fragment_discount");

        Assert.That(maxed.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(state.GetUpgradeLevel("body_fragment_discount"), Is.EqualTo(2));
        Assert.That(eventCount, Is.EqualTo(2));
    }

    [Test]
    public void IncludedCommonTypes_AreUsable()
    {
        OSRuleResult<int> accepted = OSRuleResult<int>.Accept(7);
        OSRuleResult<int> rejected = OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "bad");
        OSSessionSummary summary = new OSSessionSummary(
            OSResultCode.Accepted,
            "clear",
            600f,
            3,
            50f,
            100f,
            12,
            20,
            true,
            80,
            24,
            4);

        Assert.That(OSSessionState.Combat, Is.EqualTo(OSSessionState.Combat));
        Assert.That(accepted.IsAccepted, Is.True);
        Assert.That(accepted.Payload, Is.EqualTo(7));
        Assert.That(rejected.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(rejected.ReasonKey, Is.EqualTo("bad"));
        Assert.That(summary.BossDefeated, Is.True);
        Assert.That(summary.SurvivalTimeSeconds, Is.EqualTo(600f));
    }

    [Test]
    public void RestartCreatesIndependentRuntimeState()
    {
        OSSessionRuntimeState first = CreateState();
        OSSessionRuntimeState second = CreateState();

        first.ApplyPickup(OSPickupType.Experience, 15);
        first.ApplyUpgrade("max_hp_boost");

        Assert.That(first, Is.Not.SameAs(second));
        Assert.That(first.Level, Is.EqualTo(2));
        Assert.That(second.Level, Is.EqualTo(1));
        Assert.That(first.MaxHp, Is.EqualTo(120f));
        Assert.That(second.MaxHp, Is.EqualTo(100f));
    }

    [Test]
    public void BuildSummary_StoresResultAndRaisesOneEvent()
    {
        OSSessionRuntimeState state = CreateState();
        int eventCount = 0;
        state.RuntimeStateChanged += _ => eventCount++;

        state.ApplyPickup(OSPickupType.Experience, 15);
        state.RecordActiveBodySegments(9);
        state.RecordExplosionKills(12);
        state.RecordBossDefeated();
        eventCount = 0;

        OSRuleResult<OSSessionSummary> result = state.BuildSummary(OSResultCode.Accepted, 601f, "boss_clear");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.SurvivalTimeSeconds, Is.EqualTo(601f));
        Assert.That(result.Payload.Level, Is.EqualTo(2));
        Assert.That(result.Payload.MaxActiveBodySegments, Is.EqualTo(9));
        Assert.That(result.Payload.ExplosionKillCount, Is.EqualTo(12));
        Assert.That(result.Payload.BossDefeated, Is.True);
        Assert.That(state.LastSummary.ReasonKey, Is.EqualTo("boss_clear"));
        Assert.That(eventCount, Is.EqualTo(1));
    }

    [Test]
    public void InitializeFrom_DoesNotMutateSourceScriptableObjects()
    {
        OSPlayerBalanceData player = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        OSBodyBalanceData body = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        OSEncounterBalanceData encounter = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        OSSessionRuntimeState state = OSSessionRuntimeState.InitializeFrom(player, body, encounter, catalog).Payload;
        state.ApplyPickup(OSPickupType.Experience, 35);
        state.ApplyPickup(OSPickupType.BodyFragment, 25);
        state.ApplyUpgrade("max_hp_boost");

        Assert.That(player.Hp, Is.EqualTo(100f));
        Assert.That(body.BodyFragmentsPerSegment, Is.EqualTo(12));
        Assert.That(encounter.GetEnemyPrototypeAt(0).Id, Is.EqualTo("enemy_chaser"));
        Assert.That(catalog.GetUpgradeAt(8).Id, Is.EqualTo("max_hp_boost"));
        Assert.That(catalog.GetUpgradeAt(8).MaxLevel, Is.EqualTo(2));

        Object.DestroyImmediate(player);
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(encounter);
        Object.DestroyImmediate(catalog);
    }

    [Test]
    public void DeadStateRejectsPickupAndUpgrade()
    {
        OSSessionRuntimeState state = CreateState();
        state.BuildSummary(OSResultCode.RejectedState, 20f, "dead");

        Assert.That(state.State, Is.EqualTo(OSSessionState.Dead));
        Assert.That(state.ApplyPickup(OSPickupType.Experience, 1).Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(state.ApplyUpgrade("max_hp_boost").Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    private static OSSessionRuntimeState CreateState()
    {
        OSPlayerBalanceData player = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        OSBodyBalanceData body = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        OSEncounterBalanceData encounter = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSUpgradeCatalog catalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        OSRuleResult<OSSessionRuntimeState> result = OSSessionRuntimeState.InitializeFrom(player, body, encounter, catalog);

        Object.DestroyImmediate(player);
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(encounter);
        Object.DestroyImmediate(catalog);

        Assert.That(result.IsAccepted, Is.True);
        return result.Payload;
    }

    private static OSUpgradeProgressSnapshot FindProgress(OSUpgradeProgressSnapshot[] progress, string id)
    {
        for (int i = 0; i < progress.Length; i++)
        {
            if (progress[i].Id == id)
            {
                return progress[i];
            }
        }

        Assert.Fail($"Missing progress: {id}");
        return default;
    }
}
#endif
