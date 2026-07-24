using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSLaserBodyRole : MonoBehaviour
{
    private const float DistanceTieTolerance = 0.0001f;
    private const string OwnerIdPrefix = "laser_segment";
    private const string DefaultTelegraphPoolKey = "effect_laser_telegraph";
    private const string DefaultBeamPoolKey = "effect_laser_beam";
    private const float BeamVisualDuration = 0.12f;
    private const float TelegraphCompletionTolerance = 0.0001f;
    private static readonly Vector2 DefaultDirection = Vector2.right;

    [Header("References")]
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSPoolRegistry poolRegistry;
    [SerializeField] private OSGameSessionController gameSession;

    [Header("Effects")]
    [SerializeField] private string telegraphPoolKey = DefaultTelegraphPoolKey;
    [SerializeField] private string beamPoolKey = DefaultBeamPoolKey;
    [SerializeField] private float sideTargetHalfAngle = 60f;

    private readonly List<OSLaserSegmentRuntime> lasers = new List<OSLaserSegmentRuntime>(16);
    private readonly List<OSLaserTelegraphRuntime> telegraphs = new List<OSLaserTelegraphRuntime>(16);
    private readonly List<OSLaserEffectRuntime> activeEffects = new List<OSLaserEffectRuntime>(16);
    private readonly List<OSEnemyController> damageCandidates = new List<OSEnemyController>(32);
    private int laserSequence;
    private bool subscribedToChain;
    private bool combatEnabledForTests;

    public event Action<OSLaserTelegraphResult> LaserTelegraphStarted;
    public event Action<OSLaserFireResult> LaserFired;

    public int RegisteredLaserCount => lasers.Count;
    public int ActiveTelegraphCount => telegraphs.Count;
    public string TelegraphPoolKey => telegraphPoolKey;
    public string BeamPoolKey => beamPoolKey;

    public void ConfigureForTests(
        OSBodyBalanceData balance,
        OSBodyChain chain,
        OSPoolRegistry pool = null,
        OSGameSessionController session = null,
        string telegraphKey = DefaultTelegraphPoolKey,
        string beamKey = DefaultBeamPoolKey)
    {
        bodyBalance = balance;
        bodyChain = chain;
        poolRegistry = pool;
        gameSession = session;
        telegraphPoolKey = string.IsNullOrWhiteSpace(telegraphKey) ? DefaultTelegraphPoolKey : telegraphKey;
        beamPoolKey = string.IsNullOrWhiteSpace(beamKey) ? DefaultBeamPoolKey : beamKey;
        SubscribeToChain();
        SyncFromChain();
    }

    public void SetCombatEnabledForTests(bool isEnabled)
    {
        combatEnabledForTests = isEnabled;
    }

    public void SetCooldownForTests(int stableId, float remaining)
    {
        int index = FindLaserIndex(stableId);
        if (index < 0)
        {
            return;
        }

        OSLaserSegmentRuntime laser = lasers[index];
        laser.CooldownRemaining = Mathf.Max(0f, remaining);
        lasers[index] = laser;
    }

    public OSRuleResult<OSLaserSegmentSnapshot> GetLaserSnapshot(int stableId)
    {
        int index = FindLaserIndex(stableId);
        if (index < 0)
        {
            return OSRuleResult<OSLaserSegmentSnapshot>.Rejected(OSResultCode.RejectedState, "laser_segment_missing");
        }

        OSLaserSegmentRuntime laser = lasers[index];
        return OSRuleResult<OSLaserSegmentSnapshot>.Accept(new OSLaserSegmentSnapshot(
            laser.StableId,
            laser.Position,
            laser.Direction,
            laser.CooldownRemaining,
            HasTelegraph(laser.StableId)));
    }

    public OSRuleResult<OSLaserBodyTickResult> Tick(float deltaTime)
    {
        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSLaserBodyTickResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
        {
            return OSRuleResult<OSLaserBodyTickResult>.Rejected(OSResultCode.ConfigurationError, "laser_body_delta_invalid");
        }

        UpdateEffectLifetimes(deltaTime);

        if (!CanProgress())
        {
            return OSRuleResult<OSLaserBodyTickResult>.Rejected(OSResultCode.RejectedState, "laser_body_state_blocked");
        }

        SyncFromChain();
        int firedCount = UpdateTelegraphs(deltaTime);
        int startedCount = UpdateCooldownsAndStartTelegraphs(deltaTime);

        return OSRuleResult<OSLaserBodyTickResult>.Accept(new OSLaserBodyTickResult(
            startedCount > 0,
            firedCount > 0,
            startedCount,
            firedCount,
            lasers.Count,
            telegraphs.Count));
    }

    public OSRuleResult<OSEnemyController> SelectTargetForTests(
        Vector2 origin,
        IReadOnlyList<OSEnemyController> candidates)
    {
        return SelectTarget(origin, DefaultDirection, candidates);
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
        CancelAllTelegraphs();
        ReturnAllEffects();
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

    private void SyncFromSnapshot(OSBodyChainSnapshot snapshot)
    {
        for (int i = lasers.Count - 1; i >= 0; i--)
        {
            if (!SnapshotContainsLaser(snapshot, lasers[i].StableId))
            {
                CancelTelegraphsForStableId(lasers[i].StableId);
                lasers.RemoveAt(i);
            }
        }

        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.RoleType != OSBodyRoleType.Laser)
            {
                continue;
            }

            Vector2 direction = CalculateSideAxis(snapshot, i);
            int existingIndex = FindLaserIndex(segment.StableId);
            if (existingIndex >= 0)
            {
                OSLaserSegmentRuntime existing = lasers[existingIndex];
                existing.Position = segment.Position;
                existing.Direction = direction;
                lasers[existingIndex] = existing;
                continue;
            }

            lasers.Add(new OSLaserSegmentRuntime(segment.StableId, segment.Position, direction, 0f));
        }
    }

    private int UpdateTelegraphs(float deltaTime)
    {
        int firedCount = 0;
        for (int i = telegraphs.Count - 1; i >= 0; i--)
        {
            OSLaserTelegraphRuntime telegraph = telegraphs[i];
            telegraph.RemainingSeconds = Mathf.Max(0f, telegraph.RemainingSeconds - deltaTime);
            if (telegraph.RemainingSeconds > TelegraphCompletionTolerance)
            {
                telegraphs[i] = telegraph;
                continue;
            }

            ReturnEffect(telegraph.TelegraphEffect);
            telegraphs.RemoveAt(i);
            if (!HasLaser(telegraph.StableId))
            {
                continue;
            }

            FireLaser(telegraph);
            firedCount++;
        }

        return firedCount;
    }

    private int UpdateCooldownsAndStartTelegraphs(float deltaTime)
    {
        int startedCount = 0;
        for (int i = 0; i < lasers.Count; i++)
        {
            OSLaserSegmentRuntime laser = lasers[i];
            if (HasTelegraph(laser.StableId))
            {
                continue;
            }

            if (laser.CooldownRemaining > 0f)
            {
                laser.CooldownRemaining = Mathf.Max(0f, laser.CooldownRemaining - deltaTime);
                lasers[i] = laser;
                if (laser.CooldownRemaining > 0f)
                {
                    continue;
                }
            }

            laser.CooldownRemaining = bodyBalance.Laser.Cooldown;
            lasers[i] = laser;
            StartSideTelegraph(laser.StableId, laser.Position, laser.Direction);
            StartSideTelegraph(laser.StableId, laser.Position, -laser.Direction);
            startedCount += 2;
        }

        return startedCount;
    }

    private void StartTelegraph(
        int stableId,
        Vector2 origin,
        Vector2 direction,
        OSEnemyController target)
    {
        laserSequence++;
        string ownerId = $"{OwnerIdPrefix}_{stableId:000}";
        string beamId = $"laser_{laserSequence:0000}_{stableId:000}";
        GameObject effect = RentAndPlaceEffect(telegraphPoolKey, origin, direction, bodyBalance.Laser.Length, bodyBalance.Laser.Width);
        telegraphs.Add(new OSLaserTelegraphRuntime(
            stableId,
            beamId,
            ownerId,
            target == null ? string.Empty : target.RuntimeId,
            target == null ? string.Empty : target.EnemyId,
            origin,
            direction,
            bodyBalance.Laser.TelegraphDuration,
            effect));

        LaserTelegraphStarted?.Invoke(new OSLaserTelegraphResult(
            stableId,
            beamId,
            target == null ? string.Empty : target.RuntimeId,
            target == null ? string.Empty : target.EnemyId,
            origin,
            direction,
            bodyBalance.Laser.Width,
            bodyBalance.Laser.Length,
            bodyBalance.Laser.TelegraphDuration));
    }

    private void StartSideTelegraph(int stableId, Vector2 origin, Vector2 sideDirection)
    {
        Vector2 direction = sideDirection.sqrMagnitude > 0.000001f ? sideDirection.normalized : DefaultDirection;
        OSEnemyController target = null;
        OSRuleResult<OSEnemyController> targetResult = SelectTarget(origin, direction, OSEnemyController.ActiveEnemies);
        if (targetResult.IsAccepted)
        {
            Vector2 toTarget = (Vector2)targetResult.Payload.transform.position - origin;
            if (toTarget.sqrMagnitude > 0.000001f)
            {
                direction = toTarget.normalized;
                target = targetResult.Payload;
            }
        }

        StartTelegraph(stableId, origin, direction, target);
    }

    private void FireLaser(OSLaserTelegraphRuntime telegraph)
    {
        GameObject beamEffect = RentAndPlaceEffect(
            beamPoolKey,
            telegraph.Origin,
            telegraph.Direction,
            bodyBalance.Laser.Length,
            bodyBalance.Laser.Width);
        if (beamEffect != null)
        {
            activeEffects.Add(new OSLaserEffectRuntime(beamEffect, BeamVisualDuration));
        }

        damageCandidates.Clear();
        for (int i = 0; i < OSEnemyController.ActiveEnemies.Count; i++)
        {
            OSEnemyController enemy = OSEnemyController.ActiveEnemies[i];
            if (!IsTargetCandidate(enemy) || !IsEnemyOnBeam(telegraph.Origin, telegraph.Direction, enemy.transform.position))
            {
                continue;
            }

            damageCandidates.Add(enemy);
        }

        int hitCount = 0;
        for (int i = 0; i < damageCandidates.Count; i++)
        {
            OSEnemyController enemy = damageCandidates[i];
            string damageEventId = $"{telegraph.BeamId}_{enemy.RuntimeId}";
            OSRuleResult<OSEnemyDamageResult> damageResult = enemy.ApplyDamage(new OSDamageEvent(
                damageEventId,
                OSCombatEventType.HeadDamage,
                bodyBalance.Laser.Damage,
                telegraph.OwnerId,
                enemy.RuntimeId));
            if (damageResult.IsAccepted)
            {
                hitCount++;
            }
        }

        damageCandidates.Clear();

        LaserFired?.Invoke(new OSLaserFireResult(
            telegraph.StableId,
            telegraph.BeamId,
            telegraph.TargetRuntimeId,
            telegraph.TargetEnemyId,
            telegraph.Origin,
            telegraph.Direction,
            bodyBalance.Laser.Width,
            bodyBalance.Laser.Length,
            bodyBalance.Laser.Damage,
            hitCount));
    }

    private OSRuleResult<OSEnemyController> SelectTarget(
        Vector2 origin,
        Vector2 sideAxis,
        IReadOnlyList<OSEnemyController> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "laser_target_missing");
        }

        float rangeSqr = bodyBalance.Laser.Range * bodyBalance.Laser.Range;
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
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedState, "laser_target_missing");
        }

        return OSRuleResult<OSEnemyController>.Accept(selected);
    }

    private bool IsEnemyOnBeam(Vector2 origin, Vector2 direction, Vector2 enemyPosition)
    {
        Vector2 toEnemy = enemyPosition - origin;
        float projected = Vector2.Dot(toEnemy, direction.normalized);
        if (projected < 0f || projected > bodyBalance.Laser.Length)
        {
            return false;
        }

        Vector2 closest = origin + direction.normalized * projected;
        float halfWidth = bodyBalance.Laser.Width * 0.5f;
        return ((Vector2)enemyPosition - closest).sqrMagnitude <= halfWidth * halfWidth;
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

        return DefaultDirection;
    }

    private GameObject RentAndPlaceEffect(
        string poolKey,
        Vector2 origin,
        Vector2 direction,
        float length,
        float width)
    {
        if (poolRegistry == null || string.IsNullOrWhiteSpace(poolKey))
        {
            return null;
        }

        OSRuleResult<GameObject> rentResult = poolRegistry.Rent(poolKey);
        if (!rentResult.IsAccepted)
        {
            return null;
        }

        GameObject effect = rentResult.Payload;
        Vector2 normalizedDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : DefaultDirection;
        effect.transform.position = origin + normalizedDirection * (length * 0.5f);
        effect.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(normalizedDirection.y, normalizedDirection.x) * Mathf.Rad2Deg);
        effect.transform.localScale = new Vector3(length, width, 1f);
        return effect;
    }

    private void UpdateEffectLifetimes(float deltaTime)
    {
        for (int i = activeEffects.Count - 1; i >= 0; i--)
        {
            OSLaserEffectRuntime effect = activeEffects[i];
            effect.RemainingSeconds = Mathf.Max(0f, effect.RemainingSeconds - deltaTime);
            if (effect.RemainingSeconds > 0f)
            {
                activeEffects[i] = effect;
                continue;
            }

            ReturnEffect(effect.Effect);
            activeEffects.RemoveAt(i);
        }
    }

    private void CancelAllTelegraphs()
    {
        for (int i = telegraphs.Count - 1; i >= 0; i--)
        {
            ReturnEffect(telegraphs[i].TelegraphEffect);
        }

        telegraphs.Clear();
    }

    private void CancelTelegraphsForStableId(int stableId)
    {
        for (int i = telegraphs.Count - 1; i >= 0; i--)
        {
            if (telegraphs[i].StableId != stableId)
            {
                continue;
            }

            ReturnEffect(telegraphs[i].TelegraphEffect);
            telegraphs.RemoveAt(i);
        }
    }

    private void ReturnAllEffects()
    {
        for (int i = activeEffects.Count - 1; i >= 0; i--)
        {
            ReturnEffect(activeEffects[i].Effect);
        }

        activeEffects.Clear();
    }

    private void ReturnEffect(GameObject effect)
    {
        if (effect == null)
        {
            return;
        }

        if (poolRegistry != null)
        {
            poolRegistry.Return(effect);
            return;
        }

        effect.SetActive(false);
    }

    private OSRuleResult<int> ValidateConfiguration()
    {
        if (bodyBalance == null ||
            bodyChain == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            string.IsNullOrWhiteSpace(telegraphPoolKey) ||
            string.IsNullOrWhiteSpace(beamPoolKey) ||
            sideTargetHalfAngle <= 0f ||
            sideTargetHalfAngle >= 90f ||
            float.IsNaN(sideTargetHalfAngle) ||
            float.IsInfinity(sideTargetHalfAngle))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "laser_body_configuration_invalid");
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

    private bool HasTelegraph(int stableId)
    {
        for (int i = 0; i < telegraphs.Count; i++)
        {
            if (telegraphs[i].StableId == stableId)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasLaser(int stableId)
    {
        return FindLaserIndex(stableId) >= 0;
    }

    private int FindLaserIndex(int stableId)
    {
        for (int i = 0; i < lasers.Count; i++)
        {
            if (lasers[i].StableId == stableId)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool SnapshotContainsLaser(OSBodyChainSnapshot snapshot, int stableId)
    {
        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.StableId == stableId && segment.RoleType == OSBodyRoleType.Laser)
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

internal struct OSLaserSegmentRuntime
{
    public OSLaserSegmentRuntime(int stableId, Vector2 position, Vector2 direction, float cooldownRemaining)
    {
        StableId = stableId;
        Position = position;
        Direction = direction;
        CooldownRemaining = cooldownRemaining;
    }

    public int StableId { get; }
    public Vector2 Position { get; set; }
    public Vector2 Direction { get; set; }
    public float CooldownRemaining { get; set; }
}

internal struct OSLaserTelegraphRuntime
{
    public OSLaserTelegraphRuntime(
        int stableId,
        string beamId,
        string ownerId,
        string targetRuntimeId,
        string targetEnemyId,
        Vector2 origin,
        Vector2 direction,
        float remainingSeconds,
        GameObject telegraphEffect)
    {
        StableId = stableId;
        BeamId = beamId ?? string.Empty;
        OwnerId = ownerId ?? string.Empty;
        TargetRuntimeId = targetRuntimeId ?? string.Empty;
        TargetEnemyId = targetEnemyId ?? string.Empty;
        Origin = origin;
        Direction = direction;
        RemainingSeconds = remainingSeconds;
        TelegraphEffect = telegraphEffect;
    }

    public int StableId { get; }
    public string BeamId { get; }
    public string OwnerId { get; }
    public string TargetRuntimeId { get; }
    public string TargetEnemyId { get; }
    public Vector2 Origin { get; }
    public Vector2 Direction { get; }
    public float RemainingSeconds { get; set; }
    public GameObject TelegraphEffect { get; }
}

internal struct OSLaserEffectRuntime
{
    public OSLaserEffectRuntime(GameObject effect, float remainingSeconds)
    {
        Effect = effect;
        RemainingSeconds = remainingSeconds;
    }

    public GameObject Effect { get; }
    public float RemainingSeconds { get; set; }
}

public readonly struct OSLaserSegmentSnapshot
{
    public OSLaserSegmentSnapshot(
        int stableId,
        Vector2 position,
        Vector2 direction,
        float cooldownRemaining,
        bool isTelegraphing)
    {
        StableId = stableId;
        Position = position;
        Direction = direction;
        CooldownRemaining = cooldownRemaining;
        IsTelegraphing = isTelegraphing;
    }

    public int StableId { get; }
    public Vector2 Position { get; }
    public Vector2 Direction { get; }
    public float CooldownRemaining { get; }
    public bool IsTelegraphing { get; }
}

public readonly struct OSLaserBodyTickResult
{
    public OSLaserBodyTickResult(
        bool didStartTelegraph,
        bool didFire,
        int startedTelegraphCount,
        int firedCount,
        int registeredLaserCount,
        int activeTelegraphCount)
    {
        DidStartTelegraph = didStartTelegraph;
        DidFire = didFire;
        StartedTelegraphCount = startedTelegraphCount;
        FiredCount = firedCount;
        RegisteredLaserCount = registeredLaserCount;
        ActiveTelegraphCount = activeTelegraphCount;
    }

    public bool DidStartTelegraph { get; }
    public bool DidFire { get; }
    public int StartedTelegraphCount { get; }
    public int FiredCount { get; }
    public int RegisteredLaserCount { get; }
    public int ActiveTelegraphCount { get; }
}

public readonly struct OSLaserTelegraphResult
{
    public OSLaserTelegraphResult(
        int segmentStableId,
        string beamId,
        string targetRuntimeId,
        string targetEnemyId,
        Vector2 origin,
        Vector2 direction,
        float width,
        float length,
        float telegraphDuration)
    {
        SegmentStableId = segmentStableId;
        BeamId = beamId ?? string.Empty;
        TargetRuntimeId = targetRuntimeId ?? string.Empty;
        TargetEnemyId = targetEnemyId ?? string.Empty;
        Origin = origin;
        Direction = direction;
        Width = width;
        Length = length;
        TelegraphDuration = telegraphDuration;
    }

    public int SegmentStableId { get; }
    public string BeamId { get; }
    public string TargetRuntimeId { get; }
    public string TargetEnemyId { get; }
    public Vector2 Origin { get; }
    public Vector2 Direction { get; }
    public float Width { get; }
    public float Length { get; }
    public float TelegraphDuration { get; }
}

public readonly struct OSLaserFireResult
{
    public OSLaserFireResult(
        int segmentStableId,
        string beamId,
        string targetRuntimeId,
        string targetEnemyId,
        Vector2 origin,
        Vector2 direction,
        float width,
        float length,
        float damage,
        int hitCount)
    {
        SegmentStableId = segmentStableId;
        BeamId = beamId ?? string.Empty;
        TargetRuntimeId = targetRuntimeId ?? string.Empty;
        TargetEnemyId = targetEnemyId ?? string.Empty;
        Origin = origin;
        Direction = direction;
        Width = width;
        Length = length;
        Damage = damage;
        HitCount = hitCount;
    }

    public int SegmentStableId { get; }
    public string BeamId { get; }
    public string TargetRuntimeId { get; }
    public string TargetEnemyId { get; }
    public Vector2 Origin { get; }
    public Vector2 Direction { get; }
    public float Width { get; }
    public float Length { get; }
    public float Damage { get; }
    public int HitCount { get; }
}
