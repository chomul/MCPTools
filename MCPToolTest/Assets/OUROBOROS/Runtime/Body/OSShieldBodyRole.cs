using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSShieldBodyRole : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSBodyChain bodyChain;

    [Header("Visuals")]
    [SerializeField] private GameObject shieldMaskPrefab;
    [SerializeField] private GameObject blockEffectPrefab;
    [SerializeField] private float shieldRadiusOverride = 0.9f;
    [SerializeField] private float blockEffectDuration = 0.25f;

    private readonly List<OSShieldRuntime> shields = new List<OSShieldRuntime>(16);
    private Func<float> timeProvider;
    private bool subscribedToChain;

    public event Action<OSShieldSnapshot> ShieldRegistered;
    public event Action<int> ShieldUnregistered;
    public event Action<OSShieldBlockResult> ShieldBlocked;
    public event Action<OSShieldSnapshot> ShieldRecharged;

    public int RegisteredShieldCount => shields.Count;
    public GameObject ShieldMaskPrefab => shieldMaskPrefab;
    public GameObject BlockEffectPrefab => blockEffectPrefab;
    public float EffectiveShieldRadius => shieldRadiusOverride > 0f ? shieldRadiusOverride : bodyBalance.Shield.Radius;

    public void ConfigureForTests(
        OSBodyBalanceData balance,
        OSBodyChain chain,
        Func<float> clock = null)
    {
        bodyBalance = balance;
        bodyChain = chain;
        timeProvider = clock;
        SubscribeToChain();
        SyncFromChain();
    }

    public OSRuleResult<OSShieldSnapshot> RegisterSegment(OSBodySegmentSnapshot segment)
    {
        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSShieldSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (segment.RoleType != OSBodyRoleType.Shield || segment.StableId <= 0)
        {
            return OSRuleResult<OSShieldSnapshot>.Rejected(OSResultCode.ConfigurationError, "shield_segment_invalid");
        }

        int existingIndex = FindShieldIndex(segment.StableId);
        if (existingIndex >= 0)
        {
            OSShieldRuntime existing = shields[existingIndex];
            existing.Position = segment.Position;
            shields[existingIndex] = existing;
            UpdateShieldVisual(existing);
            return OSRuleResult<OSShieldSnapshot>.Accept(existing.CreateSnapshot(GetTime()));
        }

        OSShieldRuntime runtime = new OSShieldRuntime(
            segment.StableId,
            segment.Position,
            bodyBalance.Shield.Charges,
            0f,
            CreateShieldVisual(segment.StableId, segment.Position));

        shields.Add(runtime);
        UpdateShieldVisual(runtime);
        OSShieldSnapshot snapshot = runtime.CreateSnapshot(GetTime());
        ShieldRegistered?.Invoke(snapshot);
        return OSRuleResult<OSShieldSnapshot>.Accept(snapshot);
    }

    public OSRuleResult<int> UnregisterSegment(int stableId)
    {
        int index = FindShieldIndex(stableId);
        if (index < 0)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "shield_segment_missing");
        }

        DestroyShieldVisual(shields[index].MaskView);
        shields.RemoveAt(index);
        ShieldUnregistered?.Invoke(stableId);
        return OSRuleResult<int>.Accept(stableId);
    }

    public OSRuleResult<OSShieldBlockResult> TryBlock(OSDamageEvent damageEvent, Vector2 hitPosition)
    {
        OSRuleResult<int> validation = ValidateBlockRequest(damageEvent, hitPosition);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSShieldBlockResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        SyncFromChain();
        RefreshRecharges();
        int selectedIndex = FindNearestChargedShield(hitPosition);
        if (selectedIndex < 0)
        {
            return OSRuleResult<OSShieldBlockResult>.Rejected(OSResultCode.RejectedState, "shield_not_available");
        }

        OSShieldRuntime selected = shields[selectedIndex];
        int previousCharges = selected.Charges;
        selected.Charges = Mathf.Max(0, selected.Charges - 1);
        if (selected.Charges < bodyBalance.Shield.Charges)
        {
            selected.RechargeReadyAt = GetTime() + bodyBalance.Shield.RechargeDuration;
        }

        shields[selectedIndex] = selected;
        UpdateShieldVisual(selected);

        OSShieldBlockResult result = new OSShieldBlockResult(
            selected.StableId,
            damageEvent.EventId,
            hitPosition,
            selected.Position,
            previousCharges,
            selected.Charges,
            selected.RechargeReadyAt);

        SpawnBlockEffect(selected.Position);
        ShieldBlocked?.Invoke(result);
        return OSRuleResult<OSShieldBlockResult>.Accept(result);
    }

    public OSRuleResult<OSShieldSnapshot> GetShieldSnapshot(int stableId)
    {
        int index = FindShieldIndex(stableId);
        if (index < 0)
        {
            return OSRuleResult<OSShieldSnapshot>.Rejected(OSResultCode.RejectedState, "shield_segment_missing");
        }

        RefreshShieldRecharge(index);
        return OSRuleResult<OSShieldSnapshot>.Accept(shields[index].CreateSnapshot(GetTime()));
    }

    public void SyncFromChainForTests()
    {
        SyncFromChain();
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

    private void LateUpdate()
    {
        if (shields.Count == 0)
        {
            return;
        }

        SyncFromChain();
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
        for (int i = shields.Count - 1; i >= 0; i--)
        {
            if (!SnapshotContainsShield(snapshot, shields[i].StableId))
            {
                int stableId = shields[i].StableId;
                DestroyShieldVisual(shields[i].MaskView);
                shields.RemoveAt(i);
                ShieldUnregistered?.Invoke(stableId);
            }
        }

        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.RoleType == OSBodyRoleType.Shield)
            {
                RegisterSegment(segment);
            }
        }
    }

    private void RefreshRecharges()
    {
        for (int i = 0; i < shields.Count; i++)
        {
            RefreshShieldRecharge(i);
        }
    }

    private void RefreshShieldRecharge(int index)
    {
        OSShieldRuntime shield = shields[index];
        if (shield.Charges < bodyBalance.Shield.Charges && GetTime() >= shield.RechargeReadyAt)
        {
            shield.Charges = bodyBalance.Shield.Charges;
            shield.RechargeReadyAt = 0f;
            shields[index] = shield;
            UpdateShieldVisual(shield);
            ShieldRecharged?.Invoke(shield.CreateSnapshot(GetTime()));
        }
    }

    private int FindNearestChargedShield(Vector2 hitPosition)
    {
        float radius = EffectiveShieldRadius;
        float radiusSqr = radius * radius;
        float nearestDistanceSqr = float.PositiveInfinity;
        int selectedIndex = -1;

        for (int i = 0; i < shields.Count; i++)
        {
            OSShieldRuntime shield = shields[i];
            if (shield.Charges <= 0)
            {
                continue;
            }

            float distanceSqr = (shield.Position - hitPosition).sqrMagnitude;
            if (distanceSqr > radiusSqr)
            {
                continue;
            }

            if (distanceSqr < nearestDistanceSqr ||
                (Mathf.Approximately(distanceSqr, nearestDistanceSqr) &&
                    selectedIndex >= 0 &&
                    shield.StableId < shields[selectedIndex].StableId))
            {
                nearestDistanceSqr = distanceSqr;
                selectedIndex = i;
            }
        }

        return selectedIndex;
    }

    private GameObject CreateShieldVisual(int stableId, Vector2 position)
    {
        if (shieldMaskPrefab == null)
        {
            return null;
        }

        GameObject view = Instantiate(shieldMaskPrefab, transform);
        view.name = $"Shield Range {stableId:000}";
        view.transform.position = position;
        return view;
    }

    private void UpdateShieldVisual(OSShieldRuntime shield)
    {
        if (shield.MaskView == null)
        {
            return;
        }

        float diameter = EffectiveShieldRadius * 2f;
        shield.MaskView.transform.position = shield.Position;
        shield.MaskView.transform.localScale = new Vector3(diameter, diameter, 1f);
        shield.MaskView.SetActive(shield.Charges > 0);
    }

    private void SpawnBlockEffect(Vector2 position)
    {
        if (blockEffectPrefab == null)
        {
            return;
        }

        GameObject effect = Instantiate(blockEffectPrefab, transform);
        effect.name = "Shield Block Effect";
        effect.transform.position = position;
        float diameter = EffectiveShieldRadius * 2.2f;
        effect.transform.localScale = new Vector3(diameter, diameter, 1f);

        if (Application.isPlaying)
        {
            Destroy(effect, blockEffectDuration);
        }
        else
        {
            DestroyImmediate(effect);
        }
    }

    private static void DestroyShieldVisual(GameObject view)
    {
        if (view == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(view);
        }
        else
        {
            DestroyImmediate(view);
        }
    }

    private OSRuleResult<int> ValidateBlockRequest(OSDamageEvent damageEvent, Vector2 hitPosition)
    {
        OSRuleResult<int> configuration = ValidateConfiguration();
        if (!configuration.IsAccepted)
        {
            return configuration;
        }

        if (string.IsNullOrWhiteSpace(damageEvent.EventId) ||
            damageEvent.Amount <= 0f ||
            float.IsNaN(damageEvent.Amount) ||
            float.IsInfinity(damageEvent.Amount) ||
            !IsFinite(hitPosition))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "shield_block_request_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private OSRuleResult<int> ValidateConfiguration()
    {
        if (bodyBalance == null ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "shield_configuration_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private int FindShieldIndex(int stableId)
    {
        for (int i = 0; i < shields.Count; i++)
        {
            if (shields[i].StableId == stableId)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool SnapshotContainsShield(OSBodyChainSnapshot snapshot, int stableId)
    {
        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            OSBodySegmentSnapshot segment = snapshot.Segments[i];
            if (segment.StableId == stableId && segment.RoleType == OSBodyRoleType.Shield)
            {
                return true;
            }
        }

        return false;
    }

    private float GetTime()
    {
        return timeProvider != null ? timeProvider() : Time.time;
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y);
    }
}

internal struct OSShieldRuntime
{
    public OSShieldRuntime(int stableId, Vector2 position, int charges, float rechargeReadyAt, GameObject maskView)
    {
        StableId = stableId;
        Position = position;
        Charges = charges;
        RechargeReadyAt = rechargeReadyAt;
        MaskView = maskView;
    }

    public int StableId { get; }
    public Vector2 Position { get; set; }
    public int Charges { get; set; }
    public float RechargeReadyAt { get; set; }
    public GameObject MaskView { get; }

    public OSShieldSnapshot CreateSnapshot(float now)
    {
        return new OSShieldSnapshot(StableId, Position, Charges, RechargeReadyAt, Charges > 0, now);
    }
}

public readonly struct OSShieldSnapshot
{
    public OSShieldSnapshot(
        int stableId,
        Vector2 position,
        int charges,
        float rechargeReadyAt,
        bool isCharged,
        float observedAt)
    {
        StableId = stableId;
        Position = position;
        Charges = charges;
        RechargeReadyAt = rechargeReadyAt;
        IsCharged = isCharged;
        ObservedAt = observedAt;
    }

    public int StableId { get; }
    public Vector2 Position { get; }
    public int Charges { get; }
    public float RechargeReadyAt { get; }
    public bool IsCharged { get; }
    public float ObservedAt { get; }
}

public readonly struct OSShieldBlockResult
{
    public OSShieldBlockResult(
        int shieldStableId,
        string damageEventId,
        Vector2 hitPosition,
        Vector2 shieldPosition,
        int previousCharges,
        int remainingCharges,
        float rechargeReadyAt)
    {
        ShieldStableId = shieldStableId;
        DamageEventId = damageEventId ?? string.Empty;
        HitPosition = hitPosition;
        ShieldPosition = shieldPosition;
        PreviousCharges = previousCharges;
        RemainingCharges = remainingCharges;
        RechargeReadyAt = rechargeReadyAt;
    }

    public int ShieldStableId { get; }
    public string DamageEventId { get; }
    public Vector2 HitPosition { get; }
    public Vector2 ShieldPosition { get; }
    public int PreviousCharges { get; }
    public int RemainingCharges { get; }
    public float RechargeReadyAt { get; }
}
