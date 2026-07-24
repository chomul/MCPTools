using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSBodyChain : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSPlayerController playerController;
    [SerializeField] private GameObject segmentPrefab;

    private readonly List<OSBodySegmentRuntime> segments = new List<OSBodySegmentRuntime>(64);
    private readonly List<int> reservedTailIds = new List<int>(24);
    private readonly List<Vector2> headPath = new List<Vector2>(160);
    private int nextStableId = 1;
    private Vector2 currentHeadPosition;
    private Vector2 currentHeadDirection = Vector2.right;
    private bool subscribedToPlayer;

    public event Action<OSBodySegmentSnapshot> SegmentAdded;
    public event Action<OSBodySegmentSnapshot> SegmentRemoved;
    public event Action<OSBodyChainSnapshot> ChainChanged;

    public int ActiveSegmentCount => segments.Count;
    public int ReservedTailCount => reservedTailIds.Count;
    public Vector2 CurrentHeadPosition => currentHeadPosition;

    public void ConfigureForTests(
        OSBodyBalanceData balance,
        OSPlayerController player = null,
        GameObject prefab = null)
    {
        bodyBalance = balance;
        playerController = player;
        segmentPrefab = prefab;
    }

    public OSRuleResult<OSBodySegmentSnapshot> AppendSegment(OSBodyRoleType roleType)
    {
        OSRuleResult<int> validation = ValidateChainConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSBodySegmentSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (segments.Count >= bodyBalance.TechnicalSegmentLimit)
        {
            return OSRuleResult<OSBodySegmentSnapshot>.Rejected(OSResultCode.RejectedCapacity, "body_segment_limit");
        }

        int stableId = nextStableId;
        nextStableId++;

        Vector2 position = GetFollowPositionForIndex(segments.Count);
        GameObject view = CreateSegmentView(stableId, roleType, position);
        OSBodySegmentRuntime segment = new OSBodySegmentRuntime(stableId, roleType, position, view);
        segments.Add(segment);

        OSBodySegmentSnapshot snapshot = segment.CreateSnapshot(IsReserved(stableId));
        SegmentAdded?.Invoke(snapshot);
        RaiseChainChanged();
        return OSRuleResult<OSBodySegmentSnapshot>.Accept(snapshot);
    }

    public OSRuleResult<OSBodyCutResult> TryCutFrom(int segmentIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= segments.Count)
        {
            return OSRuleResult<OSBodyCutResult>.Rejected(OSResultCode.RejectedState, "body_cut_index_invalid");
        }

        int removedCount = segments.Count - segmentIndex;
        int firstRemovedStableId = segments[segmentIndex].StableId;
        for (int i = segments.Count - 1; i >= segmentIndex; i--)
        {
            RemoveSegmentAt(i);
        }

        ClearMissingReservations();
        RaiseChainChanged();
        return OSRuleResult<OSBodyCutResult>.Accept(new OSBodyCutResult(firstRemovedStableId, removedCount, segments.Count));
    }

    public OSRuleResult<OSBodyCutResult> TryCutFromStableId(int stableId)
    {
        int segmentIndex = FindSegmentIndexByStableId(stableId);
        if (segmentIndex < 0)
        {
            return OSRuleResult<OSBodyCutResult>.Rejected(OSResultCode.RejectedState, "body_cut_stable_id_missing");
        }

        return TryCutFrom(segmentIndex);
    }

    public OSRuleResult<OSBodyReservationSnapshot> ReserveTail(int requestedCount)
    {
        if (requestedCount <= 0)
        {
            return OSRuleResult<OSBodyReservationSnapshot>.Rejected(OSResultCode.ConfigurationError, "reservation_count_invalid");
        }

        if (requestedCount > segments.Count)
        {
            return OSRuleResult<OSBodyReservationSnapshot>.Rejected(OSResultCode.RejectedState, "reservation_count_exceeds_chain");
        }

        reservedTailIds.Clear();
        for (int i = segments.Count - 1; i >= 0 && reservedTailIds.Count < requestedCount; i--)
        {
            reservedTailIds.Add(segments[i].StableId);
        }

        RaiseChainChanged();
        return OSRuleResult<OSBodyReservationSnapshot>.Accept(CreateReservationSnapshot());
    }

    public OSRuleResult<OSBodyConsumeResult> ConsumeReservedTail()
    {
        if (reservedTailIds.Count == 0)
        {
            return OSRuleResult<OSBodyConsumeResult>.Accept(new OSBodyConsumeResult(0, segments.Count));
        }

        int removedCount = 0;
        for (int i = reservedTailIds.Count - 1; i >= 0; i--)
        {
            int segmentIndex = FindSegmentIndexByStableId(reservedTailIds[i]);
            if (segmentIndex < 0)
            {
                continue;
            }

            RemoveSegmentAt(segmentIndex);
            removedCount++;
        }

        reservedTailIds.Clear();
        RaiseChainChanged();
        return OSRuleResult<OSBodyConsumeResult>.Accept(new OSBodyConsumeResult(removedCount, segments.Count));
    }

    public OSBodySegmentSnapshot GetSegmentAt(int index)
    {
        if (index < 0 || index >= segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return segments[index].CreateSnapshot(IsReserved(segments[index].StableId));
    }

    public OSBodyChainSnapshot CreateSnapshot()
    {
        OSBodySegmentSnapshot[] snapshots = new OSBodySegmentSnapshot[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            snapshots[i] = segments[i].CreateSnapshot(IsReserved(segments[i].StableId));
        }

        return new OSBodyChainSnapshot(snapshots, CreateReservationSnapshot());
    }

    public void RecordHeadPosition(Vector2 position, Vector2 direction)
    {
        if (!IsFinite(position))
        {
            return;
        }

        currentHeadPosition = position;
        if (IsFinite(direction) && direction != Vector2.zero)
        {
            currentHeadDirection = Vector2.ClampMagnitude(direction, 1f);
        }

        if (headPath.Count == 0 || Vector2.Distance(headPath[0], position) > 0.0001f)
        {
            headPath.Insert(0, position);
            TrimHeadPath();
        }

        UpdateSegmentPositions();
    }

    private void Awake()
    {
        if (playerController != null)
        {
            currentHeadPosition = playerController.transform.position;
        }

        if (headPath.Count == 0)
        {
            headPath.Add(currentHeadPosition);
        }
    }

    private void OnEnable()
    {
        SubscribeToPlayer();
    }

    private void OnDisable()
    {
        UnsubscribeFromPlayer();
    }

    private void SubscribeToPlayer()
    {
        if (playerController == null || subscribedToPlayer)
        {
            return;
        }

        playerController.PositionAdvanced += RecordHeadPosition;
        subscribedToPlayer = true;
    }

    private void UnsubscribeFromPlayer()
    {
        if (playerController == null || !subscribedToPlayer)
        {
            return;
        }

        playerController.PositionAdvanced -= RecordHeadPosition;
        subscribedToPlayer = false;
    }

    private OSRuleResult<int> ValidateChainConfiguration()
    {
        if (bodyBalance == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            bodyBalance.SegmentFollowSpacing <= 0f ||
            bodyBalance.TechnicalSegmentLimit <= 0)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "body_chain_configuration_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private Vector2 GetFollowPositionForIndex(int segmentIndex)
    {
        float targetDistance = bodyBalance != null
            ? bodyBalance.SegmentFollowSpacing * (segmentIndex + 1)
            : 0f;

        if (headPath.Count == 0)
        {
            return currentHeadPosition - currentHeadDirection * targetDistance;
        }

        float traveled = 0f;
        for (int i = 0; i < headPath.Count - 1; i++)
        {
            Vector2 from = headPath[i];
            Vector2 to = headPath[i + 1];
            float segmentDistance = Vector2.Distance(from, to);
            if (traveled + segmentDistance >= targetDistance)
            {
                float t = segmentDistance <= 0f ? 0f : (targetDistance - traveled) / segmentDistance;
                return Vector2.Lerp(from, to, t);
            }

            traveled += segmentDistance;
        }

        return headPath[headPath.Count - 1];
    }

    private void UpdateSegmentPositions()
    {
        for (int i = 0; i < segments.Count; i++)
        {
            OSBodySegmentRuntime segment = segments[i];
            segment.Position = GetFollowPositionForIndex(i);
            if (segment.View != null)
            {
                segment.View.transform.position = segment.Position;
            }

            segments[i] = segment;
        }
    }

    private GameObject CreateSegmentView(int stableId, OSBodyRoleType roleType, Vector2 position)
    {
        if (segmentPrefab == null)
        {
            return null;
        }

        GameObject view = Instantiate(segmentPrefab, transform);
        view.name = $"Body Segment {stableId:000} {roleType}";
        view.transform.position = position;
        OSBodySegmentCollider segmentCollider = view.GetComponent<OSBodySegmentCollider>();
        if (segmentCollider == null)
        {
            segmentCollider = view.AddComponent<OSBodySegmentCollider>();
        }

        segmentCollider.Bind(this, stableId, roleType);
        view.SetActive(true);
        return view;
    }

    private void RemoveSegmentAt(int index)
    {
        OSBodySegmentRuntime removed = segments[index];
        segments.RemoveAt(index);
        RemoveReservation(removed.StableId);

        if (removed.View != null)
        {
            DestroyImmediateSafe(removed.View);
        }

        SegmentRemoved?.Invoke(removed.CreateSnapshot(false));
    }

    private void ClearMissingReservations()
    {
        for (int i = reservedTailIds.Count - 1; i >= 0; i--)
        {
            if (FindSegmentIndexByStableId(reservedTailIds[i]) < 0)
            {
                reservedTailIds.RemoveAt(i);
            }
        }
    }

    private void RemoveReservation(int stableId)
    {
        for (int i = reservedTailIds.Count - 1; i >= 0; i--)
        {
            if (reservedTailIds[i] == stableId)
            {
                reservedTailIds.RemoveAt(i);
            }
        }
    }

    private int FindSegmentIndexByStableId(int stableId)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].StableId == stableId)
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsReserved(int stableId)
    {
        for (int i = 0; i < reservedTailIds.Count; i++)
        {
            if (reservedTailIds[i] == stableId)
            {
                return true;
            }
        }

        return false;
    }

    private OSBodyReservationSnapshot CreateReservationSnapshot()
    {
        int[] ids = new int[reservedTailIds.Count];
        for (int i = 0; i < reservedTailIds.Count; i++)
        {
            ids[i] = reservedTailIds[i];
        }

        return new OSBodyReservationSnapshot(ids);
    }

    private void RaiseChainChanged()
    {
        ChainChanged?.Invoke(CreateSnapshot());
    }

    private void TrimHeadPath()
    {
        int maxSamples = Mathf.Max(8, bodyBalance == null ? 160 : bodyBalance.TechnicalSegmentLimit * 4);
        while (headPath.Count > maxSamples)
        {
            headPath.RemoveAt(headPath.Count - 1);
        }
    }

    private static void DestroyImmediateSafe(GameObject target)
    {
        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y);
    }
}

internal struct OSBodySegmentRuntime
{
    public OSBodySegmentRuntime(int stableId, OSBodyRoleType roleType, Vector2 position, GameObject view)
    {
        StableId = stableId;
        RoleType = roleType;
        Position = position;
        View = view;
    }

    public int StableId { get; }
    public OSBodyRoleType RoleType { get; }
    public Vector2 Position { get; set; }
    public GameObject View { get; }

    public OSBodySegmentSnapshot CreateSnapshot(bool isReserved)
    {
        return new OSBodySegmentSnapshot(StableId, RoleType, Position, isReserved);
    }
}

public readonly struct OSBodySegmentSnapshot
{
    public OSBodySegmentSnapshot(int stableId, OSBodyRoleType roleType, Vector2 position, bool isReserved)
    {
        StableId = stableId;
        RoleType = roleType;
        Position = position;
        IsReserved = isReserved;
    }

    public int StableId { get; }
    public OSBodyRoleType RoleType { get; }
    public Vector2 Position { get; }
    public bool IsReserved { get; }
}

public readonly struct OSBodyChainSnapshot
{
    public OSBodyChainSnapshot(OSBodySegmentSnapshot[] segments, OSBodyReservationSnapshot reservation)
    {
        Segments = segments ?? Array.Empty<OSBodySegmentSnapshot>();
        Reservation = reservation;
    }

    public OSBodySegmentSnapshot[] Segments { get; }
    public OSBodyReservationSnapshot Reservation { get; }
    public int ActiveSegmentCount => Segments.Length;
}

public readonly struct OSBodyReservationSnapshot
{
    public OSBodyReservationSnapshot(int[] stableIds)
    {
        StableIds = stableIds ?? Array.Empty<int>();
    }

    public int[] StableIds { get; }
    public int Count => StableIds.Length;
}

public readonly struct OSBodyCutResult
{
    public OSBodyCutResult(int firstRemovedStableId, int removedCount, int remainingCount)
    {
        FirstRemovedStableId = firstRemovedStableId;
        RemovedCount = removedCount;
        RemainingCount = remainingCount;
    }

    public int FirstRemovedStableId { get; }
    public int RemovedCount { get; }
    public int RemainingCount { get; }
}

public readonly struct OSBodyConsumeResult
{
    public OSBodyConsumeResult(int removedCount, int remainingCount)
    {
        RemovedCount = removedCount;
        RemainingCount = remainingCount;
    }

    public int RemovedCount { get; }
    public int RemainingCount { get; }
}
