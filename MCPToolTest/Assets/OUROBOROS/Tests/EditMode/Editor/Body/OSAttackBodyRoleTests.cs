#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSAttackBodyRoleTests
{
    private GameObject roleHost;
    private OSAttackBodyRole attackRole;
    private OSBodyBalanceData bodyBalance;
    private GameObject bodyChainHost;
    private OSBodyChain bodyChain;
    private GameObject poolHost;
    private OSPoolRegistry pool;
    private GameObject projectilePrefab;
    private GameObject[] enemyHosts;
    private OSAttackBodyShotResult[] firedShots;

    [SetUp]
    public void SetUp()
    {
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        bodyChainHost = new GameObject("BodyChain");
        bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);

        projectilePrefab = CreateProjectilePrefab();
        poolHost = new GameObject("PoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_body_basic", OSPoolCategory.Projectile, projectilePrefab, 8) },
            8,
            8);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        roleHost = new GameObject("AttackBodyRole");
        attackRole = roleHost.AddComponent<OSAttackBodyRole>();
        attackRole.ConfigureForTests(bodyBalance, bodyChain, pool);
        attackRole.SetCombatEnabledForTests(true);
        enemyHosts = new GameObject[0];
        firedShots = new OSAttackBodyShotResult[0];
        attackRole.AttackShotFired += AddFiredShot;
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < enemyHosts.Length; i++)
        {
            if (enemyHosts[i] != null)
            {
                Object.DestroyImmediate(enemyHosts[i]);
            }
        }

        Object.DestroyImmediate(roleHost);
        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(projectilePrefab);
        Object.DestroyImmediate(bodyChainHost);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void Tick_AttackSegmentsFireBodyProjectilesAtClosestEnemyInRange()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        OSEnemyController farther = CreateEnemy("enemy_b", new Vector2(0f, 4f));
        OSEnemyController closer = CreateEnemy("enemy_a", new Vector2(0f, 2f));

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(4));
        Assert.That(result.Payload.RegisteredAttackCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(4));
        Assert.That(attackRole.RegisteredAttackCount, Is.EqualTo(2));
        Assert.That(closer.RuntimeId, Is.Not.EqualTo(farther.RuntimeId));
        Assert.That(CountTargetedShots(closer.RuntimeId), Is.EqualTo(2));
    }

    [Test]
    public void Tick_NoEnemyStillFiresBothSideProjectiles()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(firedShots[0].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(firedShots[1].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(Mathf.Abs(firedShots[0].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(Mathf.Abs(firedShots[1].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(firedShots[0].Direction.y, Is.Not.EqualTo(firedShots[1].Direction.y).Within(0.0001f));
    }

    [Test]
    public void Tick_NonAttackSegmentsDoNotFire()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        CreateEnemy("enemy_a", new Vector2(0f, 2f));

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.False);
        Assert.That(result.Payload.RegisteredAttackCount, Is.EqualTo(0));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_OutOfRangeEnemyFallsBackToSideFire()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        CreateEnemy("enemy_far", new Vector2(0f, 7f));

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(CountTargetedShots("enemy_far"), Is.EqualTo(0));
    }

    [Test]
    public void Tick_FrontEnemyInsideRangeFallsBackToSideFireWithoutTargeting()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        CreateEnemy("enemy_front", new Vector2(2f, 0f));

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(CountTargetedShots("enemy_front"), Is.EqualTo(0));
    }

    [Test]
    public void Tick_UsesAttackDamageWithoutHeadBodyBonus()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        CreateEnemy("enemy_a", new Vector2(0f, 2f));

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(firedShots[0].Damage, Is.EqualTo(6f).Within(0.0001f));
    }

    [Test]
    public void Tick_RemovedAttackSegmentStopsFiring()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        CreateEnemy("enemy_a", new Vector2(0f, 2f));
        bodyChain.TryCutFrom(0);

        OSRuleResult<OSAttackBodyTickResult> result = attackRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.RegisteredAttackCount, Is.EqualTo(0));
        Assert.That(result.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_CooldownBlocksImmediateSecondShotAndAllowsAfterOneSecond()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        CreateEnemy("enemy_a", new Vector2(0f, 2f));

        OSRuleResult<OSAttackBodyTickResult> first = attackRole.Tick(0f);
        OSRuleResult<OSAttackBodyTickResult> immediateSecond = attackRole.Tick(0f);
        OSRuleResult<OSAttackBodyTickResult> afterCooldown = attackRole.Tick(1f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_body_basic");

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(immediateSecond.IsAccepted, Is.True);
        Assert.That(immediateSecond.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(afterCooldown.IsAccepted, Is.True);
        Assert.That(afterCooldown.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(4));
    }

    private void AddFiredShot(OSAttackBodyShotResult shot)
    {
        OSAttackBodyShotResult[] next = new OSAttackBodyShotResult[firedShots.Length + 1];
        for (int i = 0; i < firedShots.Length; i++)
        {
            next[i] = firedShots[i];
        }

        next[next.Length - 1] = shot;
        firedShots = next;
    }

    private int CountTargetedShots(string runtimeId)
    {
        int count = 0;
        for (int i = 0; i < firedShots.Length; i++)
        {
            if (firedShots[i].TargetRuntimeId == runtimeId)
            {
                count++;
            }
        }

        return count;
    }

    private OSEnemyController CreateEnemy(string runtimeId, Vector2 position)
    {
        GameObject host = new GameObject(runtimeId);
        host.transform.position = position;
        OSEnemyController enemy = host.AddComponent<OSEnemyController>();
        OSRuleResult<OSEnemySnapshot> result = enemy.Initialize(
            runtimeId,
            new OSEnemyPrototypeSnapshot(
                "enemy_chaser",
                OSEnemyClass.Normal,
                "enemy_chaser",
                24f,
                2f,
                8f,
                1,
                1,
                0f));

        Assert.That(result.IsAccepted, Is.True);
        AddEnemyHost(host);
        return enemy;
    }

    private void AddEnemyHost(GameObject host)
    {
        GameObject[] next = new GameObject[enemyHosts.Length + 1];
        for (int i = 0; i < enemyHosts.Length; i++)
        {
            next[i] = enemyHosts[i];
        }

        next[next.Length - 1] = host;
        enemyHosts = next;
    }

    private static GameObject CreateProjectilePrefab()
    {
        GameObject prefab = new GameObject("ProjectilePrefab");
        prefab.AddComponent<OSProjectile>();
        return prefab;
    }
}
#endif
