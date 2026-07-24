using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSExplosionController : MonoBehaviour
{
    private const string SourceId = "explosion_controller";
    private const string DefaultTelegraphPoolKey = "effect_explosion_telegraph";
    private const string DefaultBlastPoolKey = "effect_explosion_blast";
    private const float BlastEffectDuration = 0.16f;
    private const float DefaultEncircleClosureDistance = 1.25f;
    private const float DefaultMinimumEncircleArea = 0.2f;
    private const float DefaultEncircledEnemyMargin = 0.25f;

    [Header("References")]
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSInputRouter inputRouter;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSPlayerHealth playerHealth;
    [SerializeField] private OSGameSessionController gameSession;
    [SerializeField] private OSPoolRegistry poolRegistry;

    [Header("Encircle Trigger")]
    [SerializeField] private bool autoTriggerOnEncircle = true;
    [SerializeField] private bool enableManualExplosionInput;
    [SerializeField] private float encircleClosureDistance = DefaultEncircleClosureDistance;
    [SerializeField] private float minimumEncircleArea = DefaultMinimumEncircleArea;
    [SerializeField] private float encircledEnemyMargin = DefaultEncircledEnemyMargin;

    [Header("Effects")]
    [SerializeField] private string telegraphPoolKey = DefaultTelegraphPoolKey;
    [SerializeField] private string blastPoolKey = DefaultBlastPoolKey;

    private readonly HashSet<string> damagedEnemyIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<OSEnemyController> enemiesToDamage = new List<OSEnemyController>(180);
    private readonly List<Vector2> encircleLoopPoints = new List<Vector2>(65);
    private readonly List<GameObject> telegraphEffects = new List<GameObject>(24);
    private readonly List<OSExplosionEffectRuntime> blastEffects = new List<OSExplosionEffectRuntime>(24);
    private OSExplosionPending pending;
    private bool hasPending;
    private int nextExplosionIndex = 1;
    private bool subscribedToInput;
    private bool wasEncircleCandidate;

    public event Action<OSExplosionSnapshot> ExplosionTelegraphStarted;
    public event Action<OSExplosionCompletionResult> ExplosionCompleted;

    public bool HasPendingExplosion => hasPending;
    public OSExplosionSnapshot CurrentSnapshot => CreateSnapshot();

    public void ConfigureForTests(
        OSBodyBalanceData balance,
        OSBodyChain chain,
        OSPlayerHealth health = null,
        OSGameSessionController session = null,
        OSInputRouter router = null,
        OSPoolRegistry pool = null,
        string telegraphKey = DefaultTelegraphPoolKey,
        string blastKey = DefaultBlastPoolKey)
    {
        if (subscribedToInput)
        {
            UnsubscribeFromInput();
        }

        bodyBalance = balance;
        bodyChain = chain;
        playerHealth = health;
        gameSession = session;
        inputRouter = router;
        poolRegistry = pool;
        telegraphPoolKey = string.IsNullOrWhiteSpace(telegraphKey) ? DefaultTelegraphPoolKey : telegraphKey;
        blastPoolKey = string.IsNullOrWhiteSpace(blastKey) ? DefaultBlastPoolKey : blastKey;
        SubscribeToInput();
    }

    public OSRuleResult<OSExplosionSnapshot> TryRequestExplosion()
    {
        OSRuleResult<int> validation = ValidateReadyForRequest();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSExplosionSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        int activeSegmentCount = bodyChain.ActiveSegmentCount;
        if (!bodyBalance.CanExplode(activeSegmentCount))
        {
            return OSRuleResult<OSExplosionSnapshot>.Rejected(OSResultCode.RejectedState, "explosion_segment_count_too_low");
        }

        int consumedSegmentCount = bodyBalance.CalculateExplosionConsumedSegments(activeSegmentCount);
        if (gameSession != null)
        {
            OSRuleResult<OSSessionState> stateResult = gameSession.EnterExplosionTelegraph();
            if (!stateResult.IsAccepted)
            {
                return OSRuleResult<OSExplosionSnapshot>.Rejected(stateResult.Code, stateResult.ReasonKey);
            }
        }

        OSRuleResult<OSBodyReservationSnapshot> reservation = bodyChain.ReserveTail(consumedSegmentCount);
        if (!reservation.IsAccepted)
        {
            return OSRuleResult<OSExplosionSnapshot>.Rejected(reservation.Code, reservation.ReasonKey);
        }

        string eventId = $"explosion_{nextExplosionIndex:000}";
        nextExplosionIndex++;

        OSExplosionSegmentSnapshot[] reservedSegments = CaptureReservedSegments(
            bodyChain.CreateSnapshot(),
            reservation.Payload.StableIds);

        pending = new OSExplosionPending(
            eventId,
            activeSegmentCount,
            consumedSegmentCount,
            bodyBalance.ExplosionDamagePerSegment * consumedSegmentCount,
            bodyBalance.ExplosionRadiusPerSegment,
            bodyBalance.ExplosionTelegraphDuration,
            reservedSegments);

        hasPending = true;
        StartTelegraphEffects(reservedSegments);
        OSExplosionSnapshot snapshot = CreateSnapshot();
        ExplosionTelegraphStarted?.Invoke(snapshot);
        return OSRuleResult<OSExplosionSnapshot>.Accept(snapshot);
    }

    public OSRuleResult<OSExplosionSnapshot> TryRequestEncircleExplosion()
    {
        if (!autoTriggerOnEncircle)
        {
            return OSRuleResult<OSExplosionSnapshot>.Rejected(OSResultCode.RejectedState, "explosion_auto_trigger_disabled");
        }

        if (!TryFindEncircledEnemy(out _))
        {
            wasEncircleCandidate = false;
            return OSRuleResult<OSExplosionSnapshot>.Rejected(OSResultCode.RejectedState, "explosion_encircle_condition_missing");
        }

        OSRuleResult<OSExplosionSnapshot> result = TryRequestExplosion();
        wasEncircleCandidate = result.IsAccepted;
        return result;
    }

    public OSRuleResult<OSExplosionTickResult> Tick(float deltaTime)
    {
        if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
        {
            return OSRuleResult<OSExplosionTickResult>.Rejected(OSResultCode.ConfigurationError, "explosion_delta_time_invalid");
        }

        UpdateBlastEffects(deltaTime);

        if (!hasPending)
        {
            return OSRuleResult<OSExplosionTickResult>.Accept(
                new OSExplosionTickResult(false, CreateSnapshot(), default));
        }

        pending.TelegraphRemaining = Mathf.Max(0f, pending.TelegraphRemaining - deltaTime);
        if (pending.TelegraphRemaining > 0f)
        {
            return OSRuleResult<OSExplosionTickResult>.Accept(
                new OSExplosionTickResult(false, CreateSnapshot(), default));
        }

        OSExplosionCompletionResult completion = CompletePendingExplosion();
        return OSRuleResult<OSExplosionTickResult>.Accept(
            new OSExplosionTickResult(true, CreateSnapshot(), completion));
    }

    private void OnEnable()
    {
        SubscribeToInput();
    }

    private void OnDisable()
    {
        UnsubscribeFromInput();
        ReturnTelegraphEffects();
        ReturnBlastEffects();
    }

    private void Update()
    {
        Tick(Time.deltaTime);

        if (!hasPending)
        {
            TryAutoRequestEncircleExplosion();
        }
    }

    private void OnExplosionPressed()
    {
        TryRequestExplosion();
    }

    private void TryAutoRequestEncircleExplosion()
    {
        if (!autoTriggerOnEncircle)
        {
            wasEncircleCandidate = false;
            return;
        }

        bool isEncircleCandidate = TryFindEncircledEnemy(out _);
        if (!isEncircleCandidate)
        {
            wasEncircleCandidate = false;
            return;
        }

        if (wasEncircleCandidate)
        {
            return;
        }

        OSRuleResult<OSExplosionSnapshot> result = TryRequestExplosion();
        wasEncircleCandidate = result.IsAccepted;
    }

    private bool TryFindEncircledEnemy(out OSEnemyController encircledEnemy)
    {
        encircledEnemy = null;
        if (bodyBalance == null ||
            bodyChain == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            (gameSession != null && gameSession.CurrentState != OSSessionState.Combat) ||
            !bodyBalance.CanExplode(bodyChain.ActiveSegmentCount))
        {
            return false;
        }

        OSBodyChainSnapshot chainSnapshot = bodyChain.CreateSnapshot();
        if (!TryBuildEncircleLoop(chainSnapshot, encircleLoopPoints))
        {
            return false;
        }

        IReadOnlyList<OSEnemyController> activeEnemies = OSEnemyController.ActiveEnemies;
        for (int i = 0; i < activeEnemies.Count; i++)
        {
            OSEnemyController enemy = activeEnemies[i];
            if (enemy == null || !enemy.IsInitialized || enemy.IsDead)
            {
                continue;
            }

            Vector2 enemyPosition = enemy.transform.position;
            if (IsPointInsidePolygon(enemyPosition, encircleLoopPoints) ||
                IsPointNearPolygonEdge(enemyPosition, encircleLoopPoints, encircledEnemyMargin))
            {
                encircledEnemy = enemy;
                return true;
            }
        }

        return false;
    }

    private bool TryBuildEncircleLoop(OSBodyChainSnapshot chainSnapshot, List<Vector2> loopPoints)
    {
        loopPoints.Clear();
        if (chainSnapshot.Segments.Length < bodyBalance.MinimumExplosionSegments)
        {
            return false;
        }

        loopPoints.Add(bodyChain.CurrentHeadPosition);
        for (int i = 0; i < chainSnapshot.Segments.Length; i++)
        {
            Vector2 position = chainSnapshot.Segments[i].Position;
            if (Vector2.Distance(loopPoints[loopPoints.Count - 1], position) > 0.0001f)
            {
                loopPoints.Add(position);
            }
        }

        if (loopPoints.Count < 4)
        {
            return false;
        }

        float closureDistance = encircleClosureDistance > 0f
            ? encircleClosureDistance
            : Mathf.Max(DefaultEncircleClosureDistance, bodyBalance.SegmentFollowSpacing * 2.75f);
        if (Vector2.Distance(loopPoints[0], loopPoints[loopPoints.Count - 1]) > closureDistance)
        {
            return false;
        }

        float requiredArea = minimumEncircleArea > 0f ? minimumEncircleArea : DefaultMinimumEncircleArea;
        return Mathf.Abs(CalculatePolygonArea(loopPoints)) >= requiredArea;
    }

    private static float CalculatePolygonArea(IReadOnlyList<Vector2> points)
    {
        float area = 0f;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            area += (points[j].x * points[i].y) - (points[i].x * points[j].y);
        }

        return area * 0.5f;
    }

    private static bool IsPointInsidePolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 current = polygon[i];
            Vector2 previous = polygon[j];
            bool crosses = (current.y > point.y) != (previous.y > point.y);
            if (!crosses)
            {
                continue;
            }

            float intersectionX = (previous.x - current.x) * (point.y - current.y) /
                (previous.y - current.y) + current.x;
            if (point.x < intersectionX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsPointNearPolygonEdge(Vector2 point, IReadOnlyList<Vector2> polygon, float margin)
    {
        if (margin <= 0f || polygon == null || polygon.Count < 2)
        {
            return false;
        }

        float sqrMargin = margin * margin;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if (DistanceToSegmentSquared(point, polygon[j], polygon[i]) <= sqrMargin)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistanceToSegmentSquared(Vector2 point, Vector2 from, Vector2 to)
    {
        Vector2 segment = to - from;
        float sqrMagnitude = segment.sqrMagnitude;
        if (sqrMagnitude <= 0.000001f)
        {
            return (point - from).sqrMagnitude;
        }

        float t = Mathf.Clamp01(Vector2.Dot(point - from, segment) / sqrMagnitude);
        Vector2 closest = from + segment * t;
        return (point - closest).sqrMagnitude;
    }

    private OSExplosionCompletionResult CompletePendingExplosion()
    {
        OSExplosionPending completed = pending;
        hasPending = false;
        pending = default;
        ReturnTelegraphEffects();

        OSExplosionSegmentSnapshot[] liveReservedSegments = CollectLiveReservedSegments(completed.ReservedSegments);
        float damage = bodyBalance.ExplosionDamagePerSegment * liveReservedSegments.Length;
        SpawnBlastEffects(liveReservedSegments);

        int hitCount = DamageEnemiesOnce(
            liveReservedSegments,
            completed.EventId,
            damage,
            completed.Radius);
        int killCount = CountKillsFromLastDamage();
        OSRuleResult<OSBodyConsumeResult> consumeResult = bodyChain.ConsumeReservedTail();
        OSResultCode invulnerabilityCode = playerHealth != null
            ? playerHealth.ApplyExplosionInvulnerability().Code
            : OSResultCode.Accepted;
        OSResultCode combatEventCode = gameSession != null
            ? gameSession.EnqueueExplosionCompleted(completed.EventId, SourceId).Code
            : OSResultCode.Accepted;

        if (gameSession != null && gameSession.RuntimeState != null)
        {
            gameSession.RuntimeState.RecordExplosionKills(killCount);
        }

        OSExplosionCompletionResult result = new OSExplosionCompletionResult(
            completed.EventId,
            liveReservedSegments,
            consumeResult.IsAccepted ? consumeResult.Payload.RemovedCount : 0,
            consumeResult.IsAccepted ? consumeResult.Payload.RemainingCount : bodyChain.ActiveSegmentCount,
            damage,
            hitCount,
            killCount,
            invulnerabilityCode,
            combatEventCode);

        ExplosionCompleted?.Invoke(result);
        return result;
    }

    private int DamageEnemiesOnce(
        OSExplosionSegmentSnapshot[] reservedSegments,
        string eventId,
        float damage,
        float radius)
    {
        damagedEnemyIds.Clear();
        enemiesToDamage.Clear();

        IReadOnlyList<OSEnemyController> activeEnemies = OSEnemyController.ActiveEnemies;
        for (int i = 0; i < reservedSegments.Length; i++)
        {
            Vector2 origin = reservedSegments[i].Position;
            for (int j = 0; j < activeEnemies.Count; j++)
            {
                OSEnemyController enemy = activeEnemies[j];
                if (enemy == null ||
                    !enemy.IsInitialized ||
                    enemy.IsDead ||
                    damagedEnemyIds.Contains(enemy.RuntimeId))
                {
                    continue;
                }

                if (Vector2.Distance(origin, enemy.transform.position) > radius)
                {
                    continue;
                }

                damagedEnemyIds.Add(enemy.RuntimeId);
                enemiesToDamage.Add(enemy);
            }
        }

        for (int i = 0; i < enemiesToDamage.Count; i++)
        {
            OSEnemyController enemy = enemiesToDamage[i];
            enemy.ApplyDamage(new OSDamageEvent(
                $"{eventId}:{enemy.RuntimeId}",
                OSCombatEventType.HeadDamage,
                damage,
                SourceId,
                enemy.RuntimeId));
        }

        return enemiesToDamage.Count;
    }

    private int CountKillsFromLastDamage()
    {
        int killCount = 0;
        for (int i = 0; i < enemiesToDamage.Count; i++)
        {
            if (enemiesToDamage[i] != null && enemiesToDamage[i].IsDead)
            {
                killCount++;
            }
        }

        return killCount;
    }

    private OSRuleResult<int> ValidateReadyForRequest()
    {
        if (hasPending)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "explosion_already_pending");
        }

        if (bodyBalance == null ||
            bodyChain == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "explosion_configuration_invalid");
        }

        if (gameSession != null && gameSession.CurrentState != OSSessionState.Combat)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_not_in_combat");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private void SubscribeToInput()
    {
        if (!enableManualExplosionInput || inputRouter == null || subscribedToInput)
        {
            return;
        }

        inputRouter.ExplosionPressed += OnExplosionPressed;
        subscribedToInput = true;
    }

    private void UnsubscribeFromInput()
    {
        if (inputRouter == null || !subscribedToInput)
        {
            return;
        }

        inputRouter.ExplosionPressed -= OnExplosionPressed;
        subscribedToInput = false;
    }

    private OSExplosionSnapshot CreateSnapshot()
    {
        if (!hasPending)
        {
            return new OSExplosionSnapshot(
                string.Empty,
                false,
                0,
                0,
                0f,
                0f,
                0f,
                0f,
                Array.Empty<OSExplosionSegmentSnapshot>());
        }

        return new OSExplosionSnapshot(
            pending.EventId,
            true,
            pending.ActiveSegmentCountAtRequest,
            pending.ConsumedSegmentCount,
            pending.Damage,
            pending.Radius,
            pending.TelegraphDuration,
            pending.TelegraphRemaining,
            CopySegments(pending.ReservedSegments));
    }

    private static OSExplosionSegmentSnapshot[] CaptureReservedSegments(
        OSBodyChainSnapshot chainSnapshot,
        int[] reservedStableIds)
    {
        OSExplosionSegmentSnapshot[] segments = new OSExplosionSegmentSnapshot[reservedStableIds.Length];
        for (int i = 0; i < reservedStableIds.Length; i++)
        {
            OSBodySegmentSnapshot segment = FindSegment(chainSnapshot, reservedStableIds[i]);
            segments[i] = new OSExplosionSegmentSnapshot(segment.StableId, segment.RoleType, segment.Position);
        }

        return segments;
    }

    private OSExplosionSegmentSnapshot[] CollectLiveReservedSegments(OSExplosionSegmentSnapshot[] requestedSegments)
    {
        OSBodyChainSnapshot chainSnapshot = bodyChain.CreateSnapshot();
        List<OSExplosionSegmentSnapshot> liveSegments = new List<OSExplosionSegmentSnapshot>(requestedSegments.Length);
        for (int i = 0; i < requestedSegments.Length; i++)
        {
            OSBodySegmentSnapshot current = FindSegment(chainSnapshot, requestedSegments[i].StableId);
            if (current.StableId != requestedSegments[i].StableId || !current.IsReserved)
            {
                continue;
            }

            liveSegments.Add(requestedSegments[i]);
        }

        return liveSegments.ToArray();
    }

    private void StartTelegraphEffects(OSExplosionSegmentSnapshot[] reservedSegments)
    {
        ReturnTelegraphEffects();
        for (int i = 0; i < reservedSegments.Length; i++)
        {
            GameObject effect = RentAndPlaceEffect(
                telegraphPoolKey,
                reservedSegments[i].Position,
                bodyBalance.ExplosionRadiusPerSegment);
            if (effect != null)
            {
                telegraphEffects.Add(effect);
            }
        }
    }

    private void SpawnBlastEffects(OSExplosionSegmentSnapshot[] reservedSegments)
    {
        for (int i = 0; i < reservedSegments.Length; i++)
        {
            GameObject effect = RentAndPlaceEffect(
                blastPoolKey,
                reservedSegments[i].Position,
                bodyBalance.ExplosionRadiusPerSegment);
            if (effect != null)
            {
                blastEffects.Add(new OSExplosionEffectRuntime(effect, BlastEffectDuration));
            }
        }
    }

    private GameObject RentAndPlaceEffect(string poolKey, Vector2 position, float radius)
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
        effect.transform.position = position;
        effect.transform.rotation = Quaternion.identity;
        float diameter = Mathf.Max(0.01f, radius * 2f);
        effect.transform.localScale = new Vector3(diameter, diameter, 1f);
        return effect;
    }

    private void UpdateBlastEffects(float deltaTime)
    {
        for (int i = blastEffects.Count - 1; i >= 0; i--)
        {
            OSExplosionEffectRuntime effect = blastEffects[i];
            effect.RemainingSeconds = Mathf.Max(0f, effect.RemainingSeconds - deltaTime);
            if (effect.RemainingSeconds > 0f)
            {
                blastEffects[i] = effect;
                continue;
            }

            ReturnEffect(effect.Effect);
            blastEffects.RemoveAt(i);
        }
    }

    private void ReturnTelegraphEffects()
    {
        for (int i = telegraphEffects.Count - 1; i >= 0; i--)
        {
            ReturnEffect(telegraphEffects[i]);
        }

        telegraphEffects.Clear();
    }

    private void ReturnBlastEffects()
    {
        for (int i = blastEffects.Count - 1; i >= 0; i--)
        {
            ReturnEffect(blastEffects[i].Effect);
        }

        blastEffects.Clear();
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

    private static OSBodySegmentSnapshot FindSegment(OSBodyChainSnapshot chainSnapshot, int stableId)
    {
        for (int i = 0; i < chainSnapshot.Segments.Length; i++)
        {
            if (chainSnapshot.Segments[i].StableId == stableId)
            {
                return chainSnapshot.Segments[i];
            }
        }

        return default;
    }

    private static OSExplosionSegmentSnapshot[] CopySegments(OSExplosionSegmentSnapshot[] source)
    {
        OSExplosionSegmentSnapshot[] copy = new OSExplosionSegmentSnapshot[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private struct OSExplosionPending
    {
        public OSExplosionPending(
            string eventId,
            int activeSegmentCountAtRequest,
            int consumedSegmentCount,
            float damage,
            float radius,
            float telegraphDuration,
            OSExplosionSegmentSnapshot[] reservedSegments)
        {
            EventId = eventId;
            ActiveSegmentCountAtRequest = activeSegmentCountAtRequest;
            ConsumedSegmentCount = consumedSegmentCount;
            Damage = damage;
            Radius = radius;
            TelegraphDuration = telegraphDuration;
            TelegraphRemaining = telegraphDuration;
            ReservedSegments = reservedSegments ?? Array.Empty<OSExplosionSegmentSnapshot>();
        }

        public string EventId { get; }
        public int ActiveSegmentCountAtRequest { get; }
        public int ConsumedSegmentCount { get; }
        public float Damage { get; }
        public float Radius { get; }
        public float TelegraphDuration { get; }
        public float TelegraphRemaining { get; set; }
        public OSExplosionSegmentSnapshot[] ReservedSegments { get; }
    }

    private struct OSExplosionEffectRuntime
    {
        public OSExplosionEffectRuntime(GameObject effect, float remainingSeconds)
        {
            Effect = effect;
            RemainingSeconds = remainingSeconds;
        }

        public GameObject Effect { get; }
        public float RemainingSeconds { get; set; }
    }
}

public readonly struct OSExplosionSnapshot
{
    public OSExplosionSnapshot(
        string eventId,
        bool isPending,
        int activeSegmentCountAtRequest,
        int consumedSegmentCount,
        float damage,
        float radius,
        float telegraphDuration,
        float telegraphRemaining,
        OSExplosionSegmentSnapshot[] reservedSegments)
    {
        EventId = eventId ?? string.Empty;
        IsPending = isPending;
        ActiveSegmentCountAtRequest = activeSegmentCountAtRequest;
        ConsumedSegmentCount = consumedSegmentCount;
        Damage = damage;
        Radius = radius;
        TelegraphDuration = telegraphDuration;
        TelegraphRemaining = telegraphRemaining;
        ReservedSegments = reservedSegments ?? Array.Empty<OSExplosionSegmentSnapshot>();
    }

    public string EventId { get; }
    public bool IsPending { get; }
    public int ActiveSegmentCountAtRequest { get; }
    public int ConsumedSegmentCount { get; }
    public float Damage { get; }
    public float Radius { get; }
    public float TelegraphDuration { get; }
    public float TelegraphRemaining { get; }
    public OSExplosionSegmentSnapshot[] ReservedSegments { get; }
}

public readonly struct OSExplosionSegmentSnapshot
{
    public OSExplosionSegmentSnapshot(int stableId, OSBodyRoleType roleType, Vector2 position)
    {
        StableId = stableId;
        RoleType = roleType;
        Position = position;
    }

    public int StableId { get; }
    public OSBodyRoleType RoleType { get; }
    public Vector2 Position { get; }
}

public readonly struct OSExplosionTickResult
{
    public OSExplosionTickResult(
        bool didComplete,
        OSExplosionSnapshot snapshot,
        OSExplosionCompletionResult completion)
    {
        DidComplete = didComplete;
        Snapshot = snapshot;
        Completion = completion;
    }

    public bool DidComplete { get; }
    public OSExplosionSnapshot Snapshot { get; }
    public OSExplosionCompletionResult Completion { get; }
}

public readonly struct OSExplosionCompletionResult
{
    public OSExplosionCompletionResult(
        string eventId,
        OSExplosionSegmentSnapshot[] reservedSegments,
        int consumedSegmentCount,
        int remainingSegmentCount,
        float damagePerEnemy,
        int enemyHitCount,
        int enemyKillCount,
        OSResultCode invulnerabilityResultCode,
        OSResultCode combatEventResultCode)
    {
        EventId = eventId ?? string.Empty;
        ReservedSegments = reservedSegments ?? Array.Empty<OSExplosionSegmentSnapshot>();
        ConsumedSegmentCount = consumedSegmentCount;
        RemainingSegmentCount = remainingSegmentCount;
        DamagePerEnemy = damagePerEnemy;
        EnemyHitCount = enemyHitCount;
        EnemyKillCount = enemyKillCount;
        InvulnerabilityResultCode = invulnerabilityResultCode;
        CombatEventResultCode = combatEventResultCode;
    }

    public string EventId { get; }
    public OSExplosionSegmentSnapshot[] ReservedSegments { get; }
    public int ReservedSegmentCount => ReservedSegments.Length;
    public int ConsumedSegmentCount { get; }
    public int RemainingSegmentCount { get; }
    public float DamagePerEnemy { get; }
    public int EnemyHitCount { get; }
    public int EnemyKillCount { get; }
    public OSResultCode InvulnerabilityResultCode { get; }
    public OSResultCode CombatEventResultCode { get; }
}
