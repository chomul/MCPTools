#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSPlayerHealthTests
{
    private GameObject sessionHost;
    private GameObject healthHost;
    private GameObject chainHost;
    private OSGameSessionController sessionController;
    private OSPlayerHealth playerHealth;
    private OSBodyChain bodyChain;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private float now;

    [SetUp]
    public void SetUp()
    {
        now = 1f;
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        healthHost = new GameObject("PlayerHealth");
        playerHealth = healthHost.AddComponent<OSPlayerHealth>();

        sessionHost = new GameObject("GameSession");
        sessionController = sessionHost.AddComponent<OSGameSessionController>();

        playerHealth.ConfigureForTests(playerBalance, bodyBalance, sessionController, () => now);
        sessionController.ConfigureForTests(
            playerBalance,
            bodyBalance,
            encounterBalance,
            upgradeCatalog,
            null,
            playerHealth);

        chainHost = new GameObject("BodyChain");
        bodyChain = chainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(chainHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(healthHost);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void HeadHit_ReducesHpWithoutChangingBodyChain()
    {
        StartCombatSession();
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        bodyChain.AppendSegment(OSBodyRoleType.Attack);

        OSRuleResult<OSPlayerHealthSnapshot> result = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_01", OSCombatEventType.HeadDamage, 25f));

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.CurrentHp, Is.EqualTo(75f));
        Assert.That(sessionController.RuntimeState.CurrentHp, Is.EqualTo(75f));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(2));
    }

    [Test]
    public void HeadHitAndExplosionInvulnerability_BlockDamageWithSeparateDurations()
    {
        StartCombatSession();

        OSRuleResult<OSPlayerHealthSnapshot> first = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_01", OSCombatEventType.HeadDamage, 10f));

        OSRuleResult<OSPlayerHealthSnapshot> blockedByHeadInvulnerability = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_02", OSCombatEventType.HeadDamage, 10f));

        now = 1.5f;
        OSRuleResult<OSPlayerInvulnerabilitySnapshot> explosionInvulnerability = playerHealth.ApplyExplosionInvulnerability();
        now = 1.7f;
        OSRuleResult<OSPlayerHealthSnapshot> blockedByExplosionInvulnerability = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_03", OSCombatEventType.HeadDamage, 10f));

        now = 2.0f;
        OSRuleResult<OSPlayerHealthSnapshot> afterBothInvulnerabilities = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_04", OSCombatEventType.HeadDamage, 10f));

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.CurrentHp, Is.EqualTo(90f));
        Assert.That(blockedByHeadInvulnerability.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(explosionInvulnerability.IsAccepted, Is.True);
        Assert.That(explosionInvulnerability.Payload.HeadHitInvulnerableUntil, Is.EqualTo(1.6f).Within(0.0001f));
        Assert.That(explosionInvulnerability.Payload.ExplosionInvulnerableUntil, Is.EqualTo(1.9f).Within(0.0001f));
        Assert.That(blockedByExplosionInvulnerability.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(afterBothInvulnerabilities.IsAccepted, Is.True);
        Assert.That(afterBothInvulnerabilities.Payload.CurrentHp, Is.EqualTo(80f));
    }

    [Test]
    public void ShieldBlockedHit_DoesNotChangeHpOrInvulnerability()
    {
        StartCombatSession();

        OSRuleResult<OSPlayerHealthSnapshot> result = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_blocked", OSCombatEventType.HeadDamage, 50f),
            true);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(playerHealth.CurrentHp, Is.EqualTo(100f));
        Assert.That(playerHealth.HeadHitInvulnerableUntil, Is.EqualTo(0f));
        Assert.That(playerHealth.ExplosionInvulnerableUntil, Is.EqualTo(0f));
    }

    [Test]
    public void ApplyHeal_ClampsToMaxHpAndRejectsAfterDeath()
    {
        StartCombatSession();
        playerHealth.TryApplyHeadHit(new OSDamageEvent("head_01", OSCombatEventType.HeadDamage, 30f));
        now = 2f;

        OSRuleResult<OSPlayerHealthSnapshot> healed = playerHealth.ApplyHeal(50);
        playerHealth.TryApplyHeadHit(new OSDamageEvent("head_lethal", OSCombatEventType.HeadDamage, 200f));
        OSRuleResult<OSPlayerHealthSnapshot> healAfterDeath = playerHealth.ApplyHeal(10);

        Assert.That(healed.IsAccepted, Is.True);
        Assert.That(healed.Payload.CurrentHp, Is.EqualTo(100f));
        Assert.That(healAfterDeath.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(playerHealth.CurrentHp, Is.EqualTo(0f));
    }

    [Test]
    public void LethalHeadHit_RaisesPlayerDiedOnceAndRequestsSessionDeath()
    {
        StartCombatSession();
        int playerDiedCount = 0;
        int healthChangedCount = 0;
        playerHealth.PlayerDied += _ => playerDiedCount++;
        playerHealth.HealthChanged += _ => healthChangedCount++;

        OSRuleResult<OSPlayerHealthSnapshot> lethal = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_lethal", OSCombatEventType.HeadDamage, 100f));
        OSRuleResult<OSPlayerHealthSnapshot> second = playerHealth.TryApplyHeadHit(
            new OSDamageEvent("head_after_death", OSCombatEventType.HeadDamage, 100f));

        Assert.That(lethal.IsAccepted, Is.True);
        Assert.That(second.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(playerDiedCount, Is.EqualTo(1));
        Assert.That(healthChangedCount, Is.EqualTo(1));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Dead));
        Assert.That(sessionController.IsSessionRunning, Is.False);
    }

    [Test]
    public void GameSession_LethalHeadDamageStopsSameTickHealPickup()
    {
        StartCombatSession();

        sessionController.EnqueuePickupEvent(new OSPickupEvent("heal_same_tick", OSPickupType.Heal, 20));
        sessionController.EnqueueDamageEvent(new OSDamageEvent("head_lethal", OSCombatEventType.HeadDamage, 100f));

        OSRuleResult<int> result = sessionController.ProcessFixedUpdate();

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Dead));
        Assert.That(sessionController.RuntimeState.TotalHealingCollected, Is.EqualTo(0));
        Assert.That(playerHealth.CurrentHp, Is.EqualTo(0f));
    }

    private void StartCombatSession()
    {
        Assert.That(sessionController.StartSession().IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }
}
#endif
