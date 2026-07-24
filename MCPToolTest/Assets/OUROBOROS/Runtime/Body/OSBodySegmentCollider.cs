using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSBodySegmentCollider : MonoBehaviour
{
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private int stableId;
    [SerializeField] private OSBodyRoleType roleType;

    public OSBodyChain BodyChain => bodyChain;
    public int StableId => stableId;
    public OSBodyRoleType RoleType => roleType;
    public bool IsBound => bodyChain != null && stableId > 0;

    public void Bind(OSBodyChain chain, int segmentStableId, OSBodyRoleType segmentRoleType)
    {
        bodyChain = chain;
        stableId = segmentStableId;
        roleType = segmentRoleType;
    }

    public OSRuleResult<OSBodyCutResult> TryApplyBodyHit(OSDamageEvent damageEvent)
    {
        if (!IsBound)
        {
            return OSRuleResult<OSBodyCutResult>.Rejected(OSResultCode.RejectedState, "body_segment_unbound");
        }

        if (damageEvent.Type != OSCombatEventType.BodyDamage ||
            string.IsNullOrWhiteSpace(damageEvent.EventId) ||
            damageEvent.Amount <= 0f ||
            float.IsNaN(damageEvent.Amount) ||
            float.IsInfinity(damageEvent.Amount))
        {
            return OSRuleResult<OSBodyCutResult>.Rejected(OSResultCode.ConfigurationError, "body_damage_event_invalid");
        }

        return bodyChain.TryCutFromStableId(stableId);
    }
}
