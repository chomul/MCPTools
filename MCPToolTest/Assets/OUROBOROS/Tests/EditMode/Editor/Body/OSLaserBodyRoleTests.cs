#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSLaserBodyRoleTests
{
    private GameObject roleHost;
    private OSLaserBodyRole laserRole;
    private OSBodyBalanceData bodyBalance;
    private GameObject bodyChainHost;
    private OSBodyChain bodyChain;
    private GameObject poolHost;
    private OSPoolRegistry pool;
    private GameObject telegraphPrefab;
    private GameObject beamPrefab;
    private GameObject[] enemyHosts;
    private OSLaserTelegraphResult[] telegraphs;
    private OSLaserFireResult[] firedLasers;

    [SetUp]
    public void SetUp()
    {
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        bodyChainHost = new GameObject("BodyChain");
        bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);

        telegraphPrefab = CreateEffectPrefab("LaserTelegraphPrefab");
        beamPrefab = CreateEffectPrefab("LaserBeamPrefab");
        poolHost = new GameObject("PoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[]
            {
                new OSPoolEntry("effect_laser_telegraph", OSPoolCategory.Effect, telegraphPrefab, 4),
                new OSPoolEntry("effect_laser_beam", OSPoolCategory.Effect, beamPrefab, 4)
            },
            8,
            8,
            8,
            8);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        roleHost = new GameObject("LaserBodyRole");
        laserRole = roleHost.AddComponent<OSLaserBodyRole>();
        laserRole.ConfigureForTests(bodyBalance, bodyChain, pool);
        roleHost.SetActive(false);
        laserRole.SetCombatEnabledForTests(true);
        enemyHosts = new GameObject[0];
        telegraphs = new OSLaserTelegraphResult[0];
        firedLasers = new OSLaserFireResult[0];
        laserRole.LaserTelegraphStarted += AddTelegraph;
        laserRole.LaserFired += AddFiredLaser;
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
        Object.DestroyImmediate(telegraphPrefab);
        Object.DestroyImmediate(beamPrefab);
        Object.DestroyImmediate(bodyChainHost);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void ChainChanges_RegisterAndUnregisterOnlyLaserSegments()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        bodyChain.AppendSegment(OSBodyRoleType.Laser);

        Assert.That(laserRole.RegisteredLaserCount, Is.EqualTo(2));
        Assert.That(laserRole.GetLaserSnapshot(1).IsAccepted, Is.True);
        Assert.That(laserRole.GetLaserSnapshot(3).IsAccepted, Is.True);

        bodyChain.TryCutFrom(2);

        Assert.That(laserRole.RegisteredLaserCount, Is.EqualTo(1));
        Assert.That(laserRole.GetLaserSnapshot(1).IsAccepted, Is.True);
        Assert.That(laserRole.GetLaserSnapshot(3).Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    [Test]
    public void Tick_NoEnemyStillStartsDefaultTelegraphAndBeam()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);

        OSRuleResult<OSLaserBodyTickResult> start = laserRole.Tick(0f);
        OSRuleResult<OSLaserSegmentSnapshot> snapshot = laserRole.GetLaserSnapshot(1);
        OSRuleResult<OSPoolUsage> afterTelegraph = pool.GetUsage("effect_laser_telegraph");
        OSRuleResult<OSLaserBodyTickResult> fire = laserRole.Tick(0.2f);
        OSRuleResult<OSPoolUsage> afterFireTelegraph = pool.GetUsage("effect_laser_telegraph");
        OSRuleResult<OSPoolUsage> afterFireBeam = pool.GetUsage("effect_laser_beam");

        Assert.That(start.IsAccepted, Is.True);
        Assert.That(start.Payload.StartedTelegraphCount, Is.EqualTo(2));
        Assert.That(start.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(start.Payload.ActiveTelegraphCount, Is.EqualTo(2));
        Assert.That(snapshot.Payload.CooldownRemaining, Is.EqualTo(2.5f).Within(0.0001f));
        Assert.That(afterTelegraph.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(telegraphs[0].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(Mathf.Abs(telegraphs[0].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(Mathf.Abs(telegraphs[1].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(telegraphs[0].Direction.y, Is.Not.EqualTo(telegraphs[1].Direction.y).Within(0.0001f));

        Assert.That(fire.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(TotalHitCount(), Is.EqualTo(0));
        Assert.That(afterFireTelegraph.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(afterFireBeam.Payload.ActiveCount, Is.EqualTo(2));
    }

    [Test]
    public void Tick_StartsTelegraphThenPiercesEnemiesOnSameBeam()
    {
        OSRuleResult<OSBodySegmentSnapshot> segment = bodyChain.AppendSegment(OSBodyRoleType.Laser);
        float beamX = segment.Payload.Position.x;
        OSEnemyController first = CreateEnemy("enemy_a", new Vector2(beamX, 3f), 24f);
        OSEnemyController second = CreateEnemy("enemy_b", new Vector2(beamX, 5f), 24f);
        OSEnemyController outsideWidth = CreateEnemy("enemy_c", new Vector2(beamX + 1f, 5f), 24f);
        OSEnemyController front = CreateEnemy("enemy_d", new Vector2(beamX + 3f, 0f), 24f);

        OSRuleResult<OSLaserBodyTickResult> start = laserRole.Tick(0f);
        OSRuleResult<OSPoolUsage> afterTelegraph = pool.GetUsage("effect_laser_telegraph");
        OSRuleResult<OSLaserBodyTickResult> beforeDamage = laserRole.Tick(0f);
        OSRuleResult<OSLaserBodyTickResult> fire = laserRole.Tick(bodyBalance.Laser.TelegraphDuration + 0.001f);
        OSRuleResult<OSPoolUsage> telegraphUsageAfterFire = pool.GetUsage("effect_laser_telegraph");
        OSRuleResult<OSPoolUsage> beamUsageAfterFire = pool.GetUsage("effect_laser_beam");

        Assert.That(start.IsAccepted, Is.True);
        Assert.That(start.Payload.StartedTelegraphCount, Is.EqualTo(2));
        Assert.That(afterTelegraph.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(CountTargetedTelegraphs(first.RuntimeId), Is.EqualTo(1));
        Assert.That(telegraphs[0].Width, Is.EqualTo(0.35f).Within(0.0001f));
        Assert.That(telegraphs[0].Length, Is.EqualTo(7f).Within(0.0001f));
        Assert.That(beforeDamage.Payload.FiredCount, Is.EqualTo(0));

        Assert.That(fire.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(TotalHitCount(), Is.EqualTo(2));
        Assert.That(first.CurrentHp, Is.EqualTo(12f).Within(0.0001f));
        Assert.That(second.CurrentHp, Is.EqualTo(12f).Within(0.0001f));
        Assert.That(outsideWidth.CurrentHp, Is.EqualTo(24f).Within(0.0001f), "outside_width");
        Assert.That(front.CurrentHp, Is.EqualTo(24f).Within(0.0001f), "front_enemy");
        Assert.That(telegraphUsageAfterFire.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(beamUsageAfterFire.Payload.ActiveCount, Is.EqualTo(2));

        laserRole.Tick(0.12f);
        Assert.That(pool.GetUsage("effect_laser_beam").Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_OutOfRangeEnemyFallsBackToDefaultBeamWithoutTargeting()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        OSEnemyController far = CreateEnemy("enemy_far", new Vector2(0f, 8f), 24f);

        OSRuleResult<OSLaserBodyTickResult> start = laserRole.Tick(0f);
        OSRuleResult<OSLaserBodyTickResult> fire = laserRole.Tick(0.2f);

        Assert.That(start.IsAccepted, Is.True);
        Assert.That(start.Payload.StartedTelegraphCount, Is.EqualTo(2));
        Assert.That(telegraphs[0].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(Mathf.Abs(telegraphs[0].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(fire.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(TotalHitCount(), Is.EqualTo(0));
        Assert.That(far.CurrentHp, Is.EqualTo(24f).Within(0.0001f));
    }

    [Test]
    public void Tick_DifferentLaserSegmentsCanDamageSameEnemyIndependently()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        OSEnemyController enemy = CreateEnemy("enemy_shared", new Vector2(0f, 3f), 30f);

        OSRuleResult<OSLaserBodyTickResult> start = laserRole.Tick(0f);
        OSRuleResult<OSLaserBodyTickResult> fire = laserRole.Tick(0.2f);

        Assert.That(start.Payload.StartedTelegraphCount, Is.EqualTo(4));
        Assert.That(fire.Payload.FiredCount, Is.EqualTo(4));
        Assert.That(firedLasers.Length, Is.EqualTo(4));
        Assert.That(enemy.CurrentHp, Is.EqualTo(6f).Within(0.0001f));
    }

    [Test]
    public void Tick_RemovedSegmentDuringTelegraphCancelsDamage()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        OSEnemyController enemy = CreateEnemy("enemy_a", new Vector2(3f, 0f), 24f);

        OSRuleResult<OSLaserBodyTickResult> start = laserRole.Tick(0f);
        bodyChain.TryCutFrom(0);
        OSRuleResult<OSLaserBodyTickResult> afterCut = laserRole.Tick(0.2f);

        Assert.That(start.Payload.StartedTelegraphCount, Is.EqualTo(2));
        Assert.That(afterCut.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(laserRole.RegisteredLaserCount, Is.EqualTo(0));
        Assert.That(laserRole.ActiveTelegraphCount, Is.EqualTo(0));
        Assert.That(enemy.CurrentHp, Is.EqualTo(24f).Within(0.0001f));
        Assert.That(firedLasers.Length, Is.EqualTo(0));
        Assert.That(pool.GetUsage("effect_laser_telegraph").Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_CooldownBlocksImmediateSecondTelegraphAndAllowsAfterTwoPointFiveSeconds()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Laser);
        CreateEnemy("enemy_a", new Vector2(3f, 0f), 100f);

        OSRuleResult<OSLaserBodyTickResult> first = laserRole.Tick(0f);
        OSRuleResult<OSLaserBodyTickResult> immediateSecond = laserRole.Tick(0f);
        laserRole.Tick(0.2f);
        OSRuleResult<OSLaserBodyTickResult> beforeCooldown = laserRole.Tick(2.29f);
        OSRuleResult<OSLaserBodyTickResult> afterCooldown = laserRole.Tick(0.01f);

        Assert.That(first.Payload.StartedTelegraphCount, Is.EqualTo(2));
        Assert.That(immediateSecond.Payload.StartedTelegraphCount, Is.EqualTo(0));
        Assert.That(beforeCooldown.Payload.StartedTelegraphCount, Is.EqualTo(0));
        Assert.That(afterCooldown.Payload.StartedTelegraphCount, Is.EqualTo(2));
    }

    private int CountTargetedTelegraphs(string runtimeId)
    {
        int count = 0;
        for (int i = 0; i < telegraphs.Length; i++)
        {
            if (telegraphs[i].TargetRuntimeId == runtimeId)
            {
                count++;
            }
        }

        return count;
    }

    private int TotalHitCount()
    {
        int count = 0;
        for (int i = 0; i < firedLasers.Length; i++)
        {
            count += firedLasers[i].HitCount;
        }

        return count;
    }

    private void AddTelegraph(OSLaserTelegraphResult telegraph)
    {
        OSLaserTelegraphResult[] next = new OSLaserTelegraphResult[telegraphs.Length + 1];
        for (int i = 0; i < telegraphs.Length; i++)
        {
            next[i] = telegraphs[i];
        }

        next[next.Length - 1] = telegraph;
        telegraphs = next;
    }

    private void AddFiredLaser(OSLaserFireResult fired)
    {
        OSLaserFireResult[] next = new OSLaserFireResult[firedLasers.Length + 1];
        for (int i = 0; i < firedLasers.Length; i++)
        {
            next[i] = firedLasers[i];
        }

        next[next.Length - 1] = fired;
        firedLasers = next;
    }

    private OSEnemyController CreateEnemy(string runtimeId, Vector2 position, float hp)
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
                hp,
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

    private static GameObject CreateEffectPrefab(string name)
    {
        GameObject prefab = new GameObject(name);
        prefab.AddComponent<SpriteRenderer>();
        return prefab;
    }
}
#endif
