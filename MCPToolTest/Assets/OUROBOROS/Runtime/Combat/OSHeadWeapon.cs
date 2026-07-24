using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSHeadWeapon : MonoBehaviour
{
    private const float DistanceTieTolerance = 0.0001f;
    private const string OwnerId = "player_head";

    [Header("References")]
    [SerializeField] private OSPlayerBalanceData playerBalance;
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSPoolRegistry poolRegistry;
    [SerializeField] private OSGameSessionController gameSession;
    [SerializeField] private Transform firePoint;

    [Header("Projectile")]
    [SerializeField] private string projectilePoolKey = "projectile_head_basic";
    [SerializeField] private float projectileSpeed = 12f;

    private OSEnemyController currentTarget;
    private float cooldownRemaining;
    private int shotSequence;
    private bool combatEnabledForTests;

    public event Action<OSHeadShotResult> HeadShotFired;

    public OSEnemyController CurrentTarget => currentTarget;
    public float CooldownRemaining => cooldownRemaining;
    public string ProjectilePoolKey => projectilePoolKey;

    public void ConfigureForTests(
        OSPlayerBalanceData player,
        OSBodyBalanceData body,
        OSBodyChain chain,
        OSPoolRegistry pool,
        Transform spawnPoint = null,
        OSGameSessionController session = null,
        string poolKey = "projectile_head_basic",
        float speed = 12f)
    {
        playerBalance = player;
        bodyBalance = body;
        bodyChain = chain;
        poolRegistry = pool;
        firePoint = spawnPoint;
        gameSession = session;
        projectilePoolKey = string.IsNullOrWhiteSpace(poolKey) ? "projectile_head_basic" : poolKey;
        projectileSpeed = speed;
    }

    public void SetCombatEnabledForTests(bool isEnabled)
    {
        combatEnabledForTests = isEnabled;
    }

    public void SetCooldownForTests(float remaining)
    {
        cooldownRemaining = Mathf.Max(0f, remaining);
    }

    public void SetCurrentTargetForTests(OSEnemyController target)
    {
        currentTarget = target;
    }

    public OSRuleResult<OSEnemyController> SelectTargetForTests(IReadOnlyList<OSEnemyController> candidates)
    {
        return SelectTarget(candidates);
    }

    public OSRuleResult<OSHeadShotResult> Tick(float deltaTime)
    {
        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(OSResultCode.ConfigurationError, "head_weapon_delta_invalid");
        }

        if (!CanProgress())
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(OSResultCode.RejectedState, "head_weapon_state_blocked");
        }

        if (cooldownRemaining > 0f)
        {
            cooldownRemaining = Mathf.Max(0f, cooldownRemaining - deltaTime);
            return OSRuleResult<OSHeadShotResult>.Accept(OSHeadShotResult.NotFired(cooldownRemaining));
        }

        OSRuleResult<OSEnemyController> targetResult = SelectTarget(OSEnemyController.ActiveEnemies);
        if (!targetResult.IsAccepted)
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(targetResult.Code, targetResult.ReasonKey);
        }

        OSEnemyController target = targetResult.Payload;
        Vector2 origin = firePoint != null ? (Vector2)firePoint.position : (Vector2)transform.position;
        Vector2 toTarget = (Vector2)target.transform.position - origin;
        if (toTarget.sqrMagnitude <= 0.000001f)
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(OSResultCode.RejectedState, "head_weapon_target_overlap");
        }

        int bodyCount = bodyChain != null ? bodyChain.ActiveSegmentCount : 0;
        int projectileCount = CalculateProjectileCount(bodyCount);
        OSRuleResult<int> poolCapacity = ValidateProjectileCapacity(projectileCount);
        if (!poolCapacity.IsAccepted)
        {
            return OSRuleResult<OSHeadShotResult>.Rejected(poolCapacity.Code, poolCapacity.ReasonKey);
        }

        float damage = CalculateDamage(bodyCount);
        float lifetime = Mathf.Max(0.01f, playerBalance.HeadRange / projectileSpeed);
        Vector2 direction = toTarget.normalized;
        shotSequence++;

        for (int i = 0; i < projectileCount; i++)
        {
            OSRuleResult<GameObject> rentResult = poolRegistry.Rent(projectilePoolKey);
            if (!rentResult.IsAccepted)
            {
                return OSRuleResult<OSHeadShotResult>.Rejected(rentResult.Code, rentResult.ReasonKey);
            }

            OSProjectile projectile = rentResult.Payload.GetComponent<OSProjectile>();
            if (projectile == null)
            {
                poolRegistry.Return(rentResult.Payload);
                return OSRuleResult<OSHeadShotResult>.Rejected(OSResultCode.ConfigurationError, "head_weapon_projectile_missing");
            }

            Vector2 shotDirection = GetProjectileDirection(direction, i, projectileCount);
            OSRuleResult<OSProjectileSnapshot> initializeResult = projectile.Initialize(
                OwnerId,
                $"head_{shotSequence:0000}_{i:00}",
                origin,
                shotDirection * projectileSpeed,
                lifetime,
                OSProjectilePayload.CreateDamage(damage),
                poolRegistry);

            if (!initializeResult.IsAccepted)
            {
                projectile.ReturnToPool("initialize_failed");
                return OSRuleResult<OSHeadShotResult>.Rejected(initializeResult.Code, initializeResult.ReasonKey);
            }
        }

        currentTarget = target;
        cooldownRemaining = playerBalance.HeadAttackInterval;
        OSHeadShotResult result = new OSHeadShotResult(
            true,
            target.RuntimeId,
            target.EnemyId,
            bodyCount,
            projectileCount,
            damage,
            cooldownRemaining);

        HeadShotFired?.Invoke(result);
        return OSRuleResult<OSHeadShotResult>.Accept(result);
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    private OSRuleResult<OSEnemyController> SelectTarget(IReadOnlyList<OSEnemyController> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            currentTarget = null;
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "head_weapon_target_missing");
        }

        Vector2 origin = firePoint != null ? (Vector2)firePoint.position : (Vector2)transform.position;
        float rangeSqr = playerBalance.HeadRange * playerBalance.HeadRange;
        float closestDistanceSqr = float.PositiveInfinity;

        for (int i = 0; i < candidates.Count; i++)
        {
            OSEnemyController candidate = candidates[i];
            if (!IsTargetCandidate(candidate))
            {
                continue;
            }

            float distanceSqr = ((Vector2)candidate.transform.position - origin).sqrMagnitude;
            if (distanceSqr > rangeSqr)
            {
                continue;
            }

            if (distanceSqr < closestDistanceSqr)
            {
                closestDistanceSqr = distanceSqr;
            }
        }

        if (float.IsPositiveInfinity(closestDistanceSqr))
        {
            currentTarget = null;
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "head_weapon_target_missing");
        }

        if (IsTargetCandidate(currentTarget))
        {
            float currentDistanceSqr = ((Vector2)currentTarget.transform.position - origin).sqrMagnitude;
            if (currentDistanceSqr <= rangeSqr && IsDistanceTie(currentDistanceSqr, closestDistanceSqr))
            {
                return OSRuleResult<OSEnemyController>.Accept(currentTarget);
            }
        }

        OSEnemyController selected = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            OSEnemyController candidate = candidates[i];
            if (!IsTargetCandidate(candidate))
            {
                continue;
            }

            float distanceSqr = ((Vector2)candidate.transform.position - origin).sqrMagnitude;
            if (!IsDistanceTie(distanceSqr, closestDistanceSqr))
            {
                continue;
            }

            if (selected == null || string.CompareOrdinal(candidate.RuntimeId, selected.RuntimeId) < 0)
            {
                selected = candidate;
            }
        }

        if (selected == null)
        {
            currentTarget = null;
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "head_weapon_target_missing");
        }

        return OSRuleResult<OSEnemyController>.Accept(selected);
    }

    private OSRuleResult<int> ValidateProjectileCapacity(int projectileCount)
    {
        OSRuleResult<OSPoolUsage> usageResult = poolRegistry.GetUsage(projectilePoolKey);
        if (!usageResult.IsAccepted)
        {
            return OSRuleResult<int>.Rejected(usageResult.Code, usageResult.ReasonKey);
        }

        if (usageResult.Payload.Category != OSPoolCategory.Projectile ||
            usageResult.Payload.InactiveCount < projectileCount)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "head_weapon_pool_capacity");
        }

        return OSRuleResult<int>.Accept(projectileCount);
    }

    private OSRuleResult<int> ValidateConfiguration()
    {
        if (playerBalance == null ||
            bodyBalance == null ||
            poolRegistry == null ||
            playerBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            string.IsNullOrWhiteSpace(projectilePoolKey) ||
            projectileSpeed <= 0f ||
            float.IsNaN(projectileSpeed) ||
            float.IsInfinity(projectileSpeed))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "head_weapon_configuration_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private bool CanProgress()
    {
        if (gameSession == null)
        {
            return combatEnabledForTests;
        }

        return gameSession.CurrentState == OSSessionState.Combat ||
            gameSession.CurrentState == OSSessionState.ExplosionTelegraph;
    }

    private int CalculateProjectileCount(int bodyCount)
    {
        int interval = Mathf.Max(1, bodyBalance.AuxiliaryProjectileInterval);
        return 1 + Mathf.Max(0, bodyCount) / interval;
    }

    private float CalculateDamage(int bodyCount)
    {
        return playerBalance.HeadDamage *
            (1f + Mathf.Max(0, bodyCount) * bodyBalance.HeadDamageMultiplierPerSegment);
    }

    private static Vector2 GetProjectileDirection(Vector2 forward, int projectileIndex, int projectileCount)
    {
        if (projectileCount <= 1)
        {
            return forward;
        }

        float center = (projectileCount - 1) * 0.5f;
        float angle = (projectileIndex - center) * 6f;
        return Quaternion.Euler(0f, 0f, angle) * forward;
    }

    private static bool IsTargetCandidate(OSEnemyController candidate)
    {
        return candidate != null && candidate.IsInitialized && !candidate.IsDead;
    }

    private static bool IsDistanceTie(float first, float second)
    {
        return Mathf.Abs(first - second) <= DistanceTieTolerance;
    }
}

public readonly struct OSHeadShotResult
{
    public OSHeadShotResult(
        bool didFire,
        string targetRuntimeId,
        string targetEnemyId,
        int bodyCount,
        int projectileCount,
        float damage,
        float cooldownRemaining)
    {
        DidFire = didFire;
        TargetRuntimeId = targetRuntimeId ?? string.Empty;
        TargetEnemyId = targetEnemyId ?? string.Empty;
        BodyCount = bodyCount;
        ProjectileCount = projectileCount;
        Damage = damage;
        CooldownRemaining = cooldownRemaining;
    }

    public bool DidFire { get; }
    public string TargetRuntimeId { get; }
    public string TargetEnemyId { get; }
    public int BodyCount { get; }
    public int ProjectileCount { get; }
    public float Damage { get; }
    public float CooldownRemaining { get; }

    public static OSHeadShotResult NotFired(float cooldownRemaining)
    {
        return new OSHeadShotResult(false, string.Empty, string.Empty, 0, 0, 0f, cooldownRemaining);
    }
}
