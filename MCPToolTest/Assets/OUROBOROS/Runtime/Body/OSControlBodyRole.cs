using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSControlBodyRole : MonoBehaviour
{
    private const float DistanceTieTolerance = 0.0001f;
    private const string OwnerIdPrefix = "control_segment";
    private const string DefaultProjectilePoolKey = "projectile_control";
    private static readonly Vector2 DefaultConnectionDirection = Vector2.right;

    [Header("References")]
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSPoolRegistry poolRegistry;
    [SerializeField] private OSGameSessionController gameSession;

    [Header("Projectile")]
    [SerializeField] private string projectilePoolKey = DefaultProjectilePoolKey;
    [SerializeField] private float projectileSpeed = 10f;
    [SerializeField] private float sideTargetHalfAngle = 60f;

    private readonly List<OSControlSegmentRuntime> controls = new List<OSControlSegmentRuntime>(16);
    private readonly List<OSControlShotRequest> pendingShots = new List<OSControlShotRequest>(16);
    private int shotSequence;
    private bool subscribedToChain;
    private bool combatEnabledForTests;

    public event Action<OSControlBodyShotResult> ControlShotFired;

    public int RegisteredControlCount => controls.Count;
    public string ProjectilePoolKey => projectilePoolKey;

    public void ConfigureForTests(
        OSBodyBalanceData balance,
        OSBodyChain chain,
        OSPoolRegistry pool,
        OSGameSessionController session = null,
        string poolKey = DefaultProjectilePoolKey,
        float speed = 10f)
    {
        bodyBalance = balance;
        bodyChain = chain;
        poolRegistry = pool;
        gameSession = session;
        projectilePoolKey = string.IsNullOrWhiteSpace(poolKey) ? DefaultProjectilePoolKey : poolKey;
        projectileSpeed = speed;
        SubscribeToChain();
        SyncFromChain();
    }

    public void SetCombatEnabledForTests(bool isEnabled)
    {
        combatEnabledForTests = isEnabled;
    }

    public void SetCooldownForTests(int stableId, float remaining)
    {
        int index = FindControlIndex(stableId);
        if (index < 0)
        {
            return;
        }

        OSControlSegmentRuntime control = controls[index];
        control.CooldownRemaining = Mathf.Max(0f, remaining);
        controls[index] = control;
    }

    public OSRuleResult<OSControlBodyTickResult> Tick(float deltaTime)
    {
        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSControlBodyTickResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
        {
            return OSRuleResult<OSControlBodyTickResult>.Rejected(OSResultCode.ConfigurationError, "control_body_delta_invalid");
        }

        if (!CanProgress())
        {
            return OSRuleResult<OSControlBodyTickResult>.Rejected(OSResultCode.RejectedState, "control_body_state_blocked");
        }

        SyncFromChain();
        pendingShots.Clear();
        for (int i = 0; i < controls.Count; i++)
        {
            OSControlSegmentRuntime control = controls[i];
            if (control.CooldownRemaining > 0f)
            {
                control.CooldownRemaining = Mathf.Max(0f, control.CooldownRemaining - deltaTime);
                controls[i] = control;
                if (control.CooldownRemaining > 0f)
                {
                    continue;
                }
            }

            QueueSideShot(i, control, control.SideAxis);
            QueueSideShot(i, control, -control.SideAxis);
        }

        if (pendingShots.Count == 0)
        {
            return OSRuleResult<OSControlBodyTickResult>.Accept(OSControlBodyTickResult.NotFired(controls.Count));
        }

        OSRuleResult<int> capacityResult = ValidateProjectileCapacity(pendingShots.Count);
        if (!capacityResult.IsAccepted)
        {
            return OSRuleResult<OSControlBodyTickResult>.Rejected(capacityResult.Code, capacityResult.ReasonKey);
        }

        int firedCount = 0;
        float lifetime = Mathf.Max(0.01f, bodyBalance.Control.Range / projectileSpeed);
        for (int i = 0; i < pendingShots.Count; i++)
        {
            OSControlShotRequest shot = pendingShots[i];
            OSRuleResult<OSControlBodyShotResult> fireResult = FireProjectile(shot, lifetime);
            if (!fireResult.IsAccepted)
            {
                return OSRuleResult<OSControlBodyTickResult>.Rejected(fireResult.Code, fireResult.ReasonKey);
            }

            OSControlSegmentRuntime control = controls[shot.ControlIndex];
            control.CooldownRemaining = bodyBalance.Control.Cooldown;
            controls[shot.ControlIndex] = control;
            firedCount++;
            ControlShotFired?.Invoke(fireResult.Payload);
        }

        return OSRuleResult<OSControlBodyTickResult>.Accept(new OSControlBodyTickResult(true, firedCount, controls.Count));
    }

    public OSRuleResult<OSEnemyController> SelectTargetForTests(
        Vector2 origin,
        Vector2 sideAxis,
        IReadOnlyList<OSEnemyController> candidates)
    {
        return SelectTarget(origin, sideAxis, candidates);
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    private void OnEnable()
    {
        SubscribeToChain();
        SyncFromChain();
    }

    private void OnDisable()
    {
        UnsubscribeFromChain();
    }

    private void OnChainChanged(OSBodyChainSnapshot snapshot)
    {
        SyncFromSnapshot(snapshot);
    }

    private void SubscribeToChain()
    {
        if (bodyChain == null || subscribedToChain)
        {
            return;
        }

        bodyChain.ChainChanged += OnChainChanged;
        subscribedToChain = true;
    }

    private void UnsubscribeFromChain()
    {
        if (bodyChain == null || !subscribedToChain)
        {
            return;
        }

        bodyChain.ChainChanged -= OnChainChanged;
        subscribedToChain = false;
    }

    private void SyncFromChain()
    {
        if (bodyChain == null)
        {
            return;
        }

        SyncFromSnapshot(bodyChain.CreateSnapshot());
    }

    private void QueueSideShot(int controlIndex, OSControlSegmentRuntime control, Vector2 sideDirection)
    {
        Vector2 shotDirection = sideDirection.normalized;
        OSEnemyController target = null;
        OSRuleResult<OSEnemyController> targetResult = SelectTarget(
            control.Position,
            shotDirection,
            OSEnemyController.ActiveEnemies);
        if (targetResult.IsAccepted)
        {
            Vector2 toTarget = (Vector2)targetResult.Payload.transform.position - control.Position;
            if (toTarget.sqrMagnitude > 0.000001f)
            {
                shotDirection = toTarget.normalized;
                target = targetResult.Payload;
            }
        }

        pendingShots.Add(new OSControlShotRequest(
            controlIndex,
            control.StableId,
            control.Position,
            shotDirection,
            target));
    }

    private void SyncFromSnapshot(OSBodyChainSnapshot snapshot)
    {
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            if (!SnapshotContainsControl(snapshot, controls[i].StableId))
            {
                controls.RemoveAt(i);
            }
        }

        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.RoleType != OSBodyRoleType.Control)
            {
                continue;
            }

            Vector2 sideAxis = CalculateSideAxis(snapshot, i);
            int existingIndex = FindControlIndex(segment.StableId);
            if (existingIndex >= 0)
            {
                OSControlSegmentRuntime existing = controls[existingIndex];
                existing.Position = segment.Position;
                existing.SideAxis = sideAxis;
                controls[existingIndex] = existing;
                continue;
            }

            controls.Add(new OSControlSegmentRuntime(segment.StableId, segment.Position, sideAxis, 0f));
        }
    }

    private OSRuleResult<OSEnemyController> SelectTarget(
        Vector2 origin,
        Vector2 sideAxis,
        IReadOnlyList<OSEnemyController> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "control_body_target_missing");
        }

        float rangeSqr = bodyBalance.Control.Range * bodyBalance.Control.Range;
        float sideDotThreshold = Mathf.Cos(Mathf.Clamp(sideTargetHalfAngle, 1f, 89f) * Mathf.Deg2Rad);
        float closestDistanceSqr = float.PositiveInfinity;
        OSEnemyController selected = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            OSEnemyController candidate = candidates[i];
            if (!IsTargetCandidate(candidate))
            {
                continue;
            }

            Vector2 toCandidate = (Vector2)candidate.transform.position - origin;
            float distanceSqr = toCandidate.sqrMagnitude;
            if (distanceSqr > rangeSqr)
            {
                continue;
            }

            if (distanceSqr <= 0.000001f ||
                Vector2.Dot(toCandidate.normalized, sideAxis.normalized) < sideDotThreshold)
            {
                continue;
            }

            if (distanceSqr < closestDistanceSqr ||
                (Mathf.Abs(distanceSqr - closestDistanceSqr) <= DistanceTieTolerance &&
                    (selected == null || string.CompareOrdinal(candidate.RuntimeId, selected.RuntimeId) < 0)))
            {
                closestDistanceSqr = distanceSqr;
                selected = candidate;
            }
        }

        if (selected == null)
        {
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "control_body_target_missing");
        }

        return OSRuleResult<OSEnemyController>.Accept(selected);
    }

    private Vector2 CalculateSideAxis(OSBodyChainSnapshot snapshot, int segmentIndex)
    {
        Vector2 connectionDirection = CalculateConnectionDirection(snapshot, segmentIndex);
        return new Vector2(-connectionDirection.y, connectionDirection.x).normalized;
    }

    private Vector2 CalculateConnectionDirection(OSBodyChainSnapshot snapshot, int segmentIndex)
    {
        OSBodySegmentSnapshot segment = snapshot.Segments[segmentIndex];
        Vector2 anchor = segmentIndex == 0
            ? bodyChain.CurrentHeadPosition
            : snapshot.Segments[segmentIndex - 1].Position;
        Vector2 direction = anchor - segment.Position;
        if (direction.sqrMagnitude > 0.000001f)
        {
            return direction.normalized;
        }

        if (segmentIndex + 1 < snapshot.Segments.Length)
        {
            direction = segment.Position - snapshot.Segments[segmentIndex + 1].Position;
            if (direction.sqrMagnitude > 0.000001f)
            {
                return direction.normalized;
            }
        }

        return DefaultConnectionDirection;
    }

    private OSRuleResult<OSControlBodyShotResult> FireProjectile(OSControlShotRequest shot, float lifetime)
    {
        OSRuleResult<GameObject> rentResult = poolRegistry.Rent(projectilePoolKey);
        if (!rentResult.IsAccepted)
        {
            return OSRuleResult<OSControlBodyShotResult>.Rejected(rentResult.Code, rentResult.ReasonKey);
        }

        OSProjectile projectile = rentResult.Payload.GetComponent<OSProjectile>();
        if (projectile == null)
        {
            poolRegistry.Return(rentResult.Payload);
            return OSRuleResult<OSControlBodyShotResult>.Rejected(OSResultCode.ConfigurationError, "control_body_projectile_missing");
        }

        shotSequence++;
        string ownerId = $"{OwnerIdPrefix}_{shot.StableId:000}";
        string eventId = $"control_{shotSequence:0000}_{shot.StableId:000}";
        OSRuleResult<OSProjectileSnapshot> initializeResult = projectile.Initialize(
            ownerId,
            eventId,
            shot.Origin,
            shot.Direction * projectileSpeed,
            lifetime,
            OSProjectilePayload.CreateControl(
                bodyBalance.Control.NormalLockDuration,
                bodyBalance.Control.EliteBossLockDuration,
                bodyBalance.Control.ProjectileDamage),
            poolRegistry);

        if (!initializeResult.IsAccepted)
        {
            projectile.ReturnToPool("initialize_failed");
            return OSRuleResult<OSControlBodyShotResult>.Rejected(initializeResult.Code, initializeResult.ReasonKey);
        }

        return OSRuleResult<OSControlBodyShotResult>.Accept(new OSControlBodyShotResult(
            shot.StableId,
            eventId,
            shot.Target == null ? string.Empty : shot.Target.RuntimeId,
            shot.Target == null ? string.Empty : shot.Target.EnemyId,
            shot.Origin,
            shot.Direction,
            bodyBalance.Control.ProjectileDamage,
            bodyBalance.Control.NormalLockDuration,
            bodyBalance.Control.EliteBossLockDuration,
            bodyBalance.Control.Cooldown));
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
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "control_body_pool_capacity");
        }

        return OSRuleResult<int>.Accept(projectileCount);
    }

    private OSRuleResult<int> ValidateConfiguration()
    {
        if (bodyBalance == null ||
            bodyChain == null ||
            poolRegistry == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            string.IsNullOrWhiteSpace(projectilePoolKey) ||
            projectileSpeed <= 0f ||
            float.IsNaN(projectileSpeed) ||
            float.IsInfinity(projectileSpeed) ||
            sideTargetHalfAngle <= 0f ||
            sideTargetHalfAngle >= 90f ||
            float.IsNaN(sideTargetHalfAngle) ||
            float.IsInfinity(sideTargetHalfAngle))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "control_body_configuration_invalid");
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

    private int FindControlIndex(int stableId)
    {
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i].StableId == stableId)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool SnapshotContainsControl(OSBodyChainSnapshot snapshot, int stableId)
    {
        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.StableId == stableId && segment.RoleType == OSBodyRoleType.Control)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTargetCandidate(OSEnemyController candidate)
    {
        return candidate != null && candidate.IsInitialized && !candidate.IsDead;
    }
}

internal struct OSControlSegmentRuntime
{
    public OSControlSegmentRuntime(int stableId, Vector2 position, Vector2 sideAxis, float cooldownRemaining)
    {
        StableId = stableId;
        Position = position;
        SideAxis = sideAxis;
        CooldownRemaining = cooldownRemaining;
    }

    public int StableId { get; }
    public Vector2 Position { get; set; }
    public Vector2 SideAxis { get; set; }
    public float CooldownRemaining { get; set; }
}

internal readonly struct OSControlShotRequest
{
    public OSControlShotRequest(
        int controlIndex,
        int stableId,
        Vector2 origin,
        Vector2 direction,
        OSEnemyController target)
    {
        ControlIndex = controlIndex;
        StableId = stableId;
        Origin = origin;
        Direction = direction;
        Target = target;
    }

    public int ControlIndex { get; }
    public int StableId { get; }
    public Vector2 Origin { get; }
    public Vector2 Direction { get; }
    public OSEnemyController Target { get; }
}

public readonly struct OSControlBodyTickResult
{
    public OSControlBodyTickResult(bool didFire, int firedCount, int registeredControlCount)
    {
        DidFire = didFire;
        FiredCount = firedCount;
        RegisteredControlCount = registeredControlCount;
    }

    public bool DidFire { get; }
    public int FiredCount { get; }
    public int RegisteredControlCount { get; }

    public static OSControlBodyTickResult NotFired(int registeredControlCount)
    {
        return new OSControlBodyTickResult(false, 0, registeredControlCount);
    }
}

public readonly struct OSControlBodyShotResult
{
    public OSControlBodyShotResult(
        int segmentStableId,
        string eventId,
        string targetRuntimeId,
        string targetEnemyId,
        Vector2 origin,
        Vector2 direction,
        float damage,
        float normalLockDuration,
        float eliteBossLockDuration,
        float cooldownRemaining)
    {
        SegmentStableId = segmentStableId;
        EventId = eventId ?? string.Empty;
        TargetRuntimeId = targetRuntimeId ?? string.Empty;
        TargetEnemyId = targetEnemyId ?? string.Empty;
        Origin = origin;
        Direction = direction;
        Damage = damage;
        NormalLockDuration = normalLockDuration;
        EliteBossLockDuration = eliteBossLockDuration;
        CooldownRemaining = cooldownRemaining;
    }

    public int SegmentStableId { get; }
    public string EventId { get; }
    public string TargetRuntimeId { get; }
    public string TargetEnemyId { get; }
    public Vector2 Origin { get; }
    public Vector2 Direction { get; }
    public float Damage { get; }
    public float NormalLockDuration { get; }
    public float EliteBossLockDuration { get; }
    public float CooldownRemaining { get; }
}
