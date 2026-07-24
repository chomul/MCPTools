#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSPickupTests
{
    private GameObject pickupHost;
    private OSPickup pickup;
    private GameObject playerHost;
    private GameObject sessionHost;
    private OSGameSessionController session;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;

    [SetUp]
    public void SetUp()
    {
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        sessionHost = new GameObject("GameSession");
        session = sessionHost.AddComponent<OSGameSessionController>();
        session.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog);
        StartCombatSession();

        playerHost = new GameObject("Player Head");
        playerHost.AddComponent<OSPlayerController>();

        pickupHost = new GameObject("Pickup");
        pickup = pickupHost.AddComponent<OSPickup>();
        pickup.ConfigureForTests(session);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(pickupHost);
        Object.DestroyImmediate(playerHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void Initialize_SetsIdTypeAmountAndResetsCollectedState()
    {
        OSRuleResult<OSPickupSnapshot> result = pickup.Initialize(
            "xp_001",
            OSPickupType.Experience,
            15);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.PickupId, Is.EqualTo("xp_001"));
        Assert.That(result.Payload.PickupType, Is.EqualTo(OSPickupType.Experience));
        Assert.That(result.Payload.Amount, Is.EqualTo(15));
        Assert.That(result.Payload.IsCollected, Is.False);
        Assert.That(pickup.IsInitialized, Is.True);
        Assert.That(pickup.IsCollected, Is.False);
    }

    [Test]
    public void TryCollect_OnlyPlayerHeadCollectorEnqueuesPickupEvent()
    {
        GameObject nonPlayer = new GameObject("NonPlayer");
        pickup.Initialize("xp_head_only", OSPickupType.Experience, 15);

        OSRuleResult<OSPickupCollectResult> rejected = pickup.TryCollect(nonPlayer);
        OSRuleResult<OSPickupCollectResult> accepted = pickup.TryCollect(playerHost);
        session.ProcessFixedUpdate();

        Assert.That(rejected.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(rejected.ReasonKey, Is.EqualTo("pickup_collector_not_head"));
        Assert.That(accepted.IsAccepted, Is.True);
        Assert.That(session.RuntimeState.Level, Is.EqualTo(2));
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        Assert.That(session.SelectionQueue.CurrentRequest.FirstUpgradeId, Is.EqualTo("head_damage_boost"));
        Assert.That(session.SelectionQueue.CurrentRequest.SecondUpgradeId, Is.EqualTo("body_fragment_discount"));
        Assert.That(session.SelectionQueue.CurrentRequest.ThirdUpgradeId, Is.EqualTo("max_hp_boost"));

        Object.DestroyImmediate(nonPlayer);
    }

    [Test]
    public void TryCollect_DuplicateTouchIsRejectedAfterFirstCollection()
    {
        pickup.Initialize("fragment_once", OSPickupType.BodyFragment, 1);

        OSRuleResult<OSPickupCollectResult> first = pickup.TryCollect(playerHost);
        OSRuleResult<OSPickupCollectResult> second = pickup.TryCollect(playerHost);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(second.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(second.ReasonKey, Is.EqualTo("pickup_already_collected"));
    }

    [TestCase(11, OSSessionState.Combat, 11, 0, false)]
    [TestCase(12, OSSessionState.BodyRoleSelection, 0, 0, true)]
    [TestCase(23, OSSessionState.BodyRoleSelection, 11, 0, true)]
    [TestCase(24, OSSessionState.BodyRoleSelection, 0, 1, true)]
    public void BodyFragmentBoundaries_CreateExpectedBodyRequests(
        int fragmentAmount,
        OSSessionState expectedState,
        int expectedRemainder,
        int expectedPendingBody,
        bool expectCurrentBodyRequest)
    {
        pickup.Initialize($"fragment_{fragmentAmount}", OSPickupType.BodyFragment, fragmentAmount);

        Assert.That(pickup.TryCollect(playerHost).IsAccepted, Is.True);
        session.ProcessFixedUpdate();

        Assert.That(session.CurrentState, Is.EqualTo(expectedState));
        Assert.That(session.RuntimeState.BodyFragments, Is.EqualTo(expectedRemainder));
        Assert.That(session.SelectionQueue.PendingBodyCount, Is.EqualTo(expectedPendingBody));
        Assert.That(session.SelectionQueue.HasCurrentRequest, Is.EqualTo(expectCurrentBodyRequest));
        if (expectCurrentBodyRequest)
        {
            Assert.That(session.SelectionQueue.CurrentRequest.IsBody, Is.True);
        }
    }

    [Test]
    public void ExperiencePickup_CreatesMultipleLevelUpRequestsAtThresholds()
    {
        pickup.Initialize("xp_multi", OSPickupType.Experience, 35);

        Assert.That(pickup.TryCollect(playerHost).IsAccepted, Is.True);
        session.ProcessFixedUpdate();

        Assert.That(session.RuntimeState.Level, Is.EqualTo(3));
        Assert.That(session.RuntimeState.Experience, Is.EqualTo(2));
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        Assert.That(session.SelectionQueue.HasCurrentRequest, Is.True);
        Assert.That(session.SelectionQueue.CurrentRequest.IsLevelUp, Is.True);
        Assert.That(session.SelectionQueue.CurrentRequest.FirstUpgradeId, Is.EqualTo("head_damage_boost"));
        Assert.That(session.SelectionQueue.CurrentRequest.SecondUpgradeId, Is.EqualTo("body_fragment_discount"));
        Assert.That(session.SelectionQueue.CurrentRequest.ThirdUpgradeId, Is.EqualTo("max_hp_boost"));
        Assert.That(session.SelectionQueue.PendingLevelUpCount, Is.EqualTo(1));
    }

    [Test]
    public void HealPickup_ClampsAtMaxHp()
    {
        Assert.That(session.RuntimeState.ApplyHeadDamage(70f).IsAccepted, Is.True);
        pickup.Initialize("heal_large", OSPickupType.Heal, 500);

        Assert.That(pickup.TryCollect(playerHost).IsAccepted, Is.True);
        session.ProcessFixedUpdate();

        Assert.That(session.RuntimeState.CurrentHp, Is.EqualTo(session.RuntimeState.MaxHp));
        Assert.That(session.RuntimeState.TotalHealingCollected, Is.EqualTo(500));
    }

    [Test]
    public void Collect_ReturnsOwnedPickupToPool()
    {
        GameObject prefab = CreatePickupPrefab();
        GameObject poolHost = new GameObject("PoolRegistry");
        OSPoolRegistry pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("pickup_experience", OSPoolCategory.Pickup, prefab, 1) },
            1,
            1,
            1);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);
        GameObject pooledObject = pool.Rent("pickup_experience").Payload;
        OSPickup pooledPickup = pooledObject.GetComponent<OSPickup>();
        pooledPickup.Initialize("xp_pooled", OSPickupType.Experience, 1, session, pool);

        OSRuleResult<OSPickupCollectResult> result = pooledPickup.TryCollect(playerHost);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("pickup_experience");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.PoolReturnCode, Is.EqualTo(OSResultCode.Accepted));
        Assert.That(pooledObject.activeSelf, Is.False);
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(usage.Payload.InactiveCount, Is.EqualTo(1));

        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(prefab);
    }

    private void StartCombatSession()
    {
        Assert.That(session.StartSession().IsAccepted, Is.True);
        Assert.That(session.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(session.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }

    private static GameObject CreatePickupPrefab()
    {
        GameObject prefab = new GameObject("PickupPrefab");
        prefab.AddComponent<OSPickup>();
        return prefab;
    }
}
#endif
