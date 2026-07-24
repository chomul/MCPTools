#if UNITY_EDITOR
using NUnit.Framework;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public sealed class OSShieldBodyRoleTests
{
    private GameObject chainHost;
    private OSBodyChain bodyChain;
    private OSBodyBalanceData bodyBalance;
    private GameObject roleHost;
    private OSShieldBodyRole shieldRole;
    private float now;

    [SetUp]
    public void SetUp()
    {
        now = 0f;
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        chainHost = new GameObject("BodyChain");
        bodyChain = chainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);

        roleHost = new GameObject("ShieldBodyRole");
        shieldRole = roleHost.AddComponent<OSShieldBodyRole>();
        shieldRole.ConfigureForTests(bodyBalance, bodyChain, () => now);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(roleHost);
        Object.DestroyImmediate(chainHost);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void ChainChanges_RegisterAndUnregisterOnlyShieldSegments()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        bodyChain.AppendSegment(OSBodyRoleType.Shield);

        Assert.That(shieldRole.RegisteredShieldCount, Is.EqualTo(2));
        Assert.That(shieldRole.GetShieldSnapshot(1).IsAccepted, Is.True);
        Assert.That(shieldRole.GetShieldSnapshot(3).IsAccepted, Is.True);

        bodyChain.TryCutFrom(2);

        Assert.That(shieldRole.RegisteredShieldCount, Is.EqualTo(1));
        Assert.That(shieldRole.GetShieldSnapshot(1).IsAccepted, Is.True);
        Assert.That(shieldRole.GetShieldSnapshot(3).Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    [Test]
    public void TryBlock_ConsumesOneChargeAndDoesNotChangeHpOrBodyCount()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        int bodyCountBefore = bodyChain.ActiveSegmentCount;

        OSRuleResult<OSShieldBlockResult> result = shieldRole.TryBlock(
            new OSDamageEvent("hit_001", OSCombatEventType.HeadDamage, 25f),
            new Vector2(-0.45f, 0f));
        OSRuleResult<OSShieldSnapshot> snapshot = shieldRole.GetShieldSnapshot(1);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.ShieldStableId, Is.EqualTo(1));
        Assert.That(result.Payload.PreviousCharges, Is.EqualTo(1));
        Assert.That(result.Payload.RemainingCharges, Is.EqualTo(0));
        Assert.That(snapshot.Payload.IsCharged, Is.False);
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(bodyCountBefore));
    }

    [Test]
    public void TryBlock_OverlappingShieldsUsesNearestChargedOnly()
    {
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        bodyChain.RecordHeadPosition(new Vector2(1f, 0f), Vector2.right);
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        shieldRole.SyncFromChainForTests();

        OSRuleResult<OSShieldBlockResult> result = shieldRole.TryBlock(
            new OSDamageEvent("hit_near", OSCombatEventType.HeadDamage, 10f),
            new Vector2(0.05f, 0f));

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.ShieldStableId, Is.EqualTo(2));
        Assert.That(shieldRole.GetShieldSnapshot(1).Payload.IsCharged, Is.True);
        Assert.That(shieldRole.GetShieldSnapshot(2).Payload.IsCharged, Is.False);
    }

    [Test]
    public void TryBlock_RechargesAfterSixSeconds()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);

        OSRuleResult<OSShieldBlockResult> first = shieldRole.TryBlock(
            new OSDamageEvent("hit_first", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));
        OSRuleResult<OSShieldBlockResult> immediateSecond = shieldRole.TryBlock(
            new OSDamageEvent("hit_second", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        now = 5.99f;
        OSRuleResult<OSShieldBlockResult> beforeRecharge = shieldRole.TryBlock(
            new OSDamageEvent("hit_before", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        now = 6f;
        OSRuleResult<OSShieldBlockResult> afterRecharge = shieldRole.TryBlock(
            new OSDamageEvent("hit_after", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.RechargeReadyAt, Is.EqualTo(6f).Within(0.0001f));
        Assert.That(immediateSecond.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(beforeRecharge.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(afterRecharge.IsAccepted, Is.True);
    }

    [Test]
    public void RemovedRechargingShieldLosesTimer()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        shieldRole.TryBlock(
            new OSDamageEvent("hit_remove", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        bodyChain.TryCutFrom(0);
        now = 6f;

        OSRuleResult<OSShieldBlockResult> result = shieldRole.TryBlock(
            new OSDamageEvent("hit_after_remove", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        Assert.That(shieldRole.RegisteredShieldCount, Is.EqualTo(0));
        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    [Test]
    public void TryBlock_OutOfRangeOrInvalidHitRejectsWithoutConsumingCharge()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Shield);

        OSRuleResult<OSShieldBlockResult> outOfRange = shieldRole.TryBlock(
            new OSDamageEvent("hit_far", OSCombatEventType.HeadDamage, 10f),
            new Vector2(100f, 0f));
        OSRuleResult<OSShieldBlockResult> invalid = shieldRole.TryBlock(
            new OSDamageEvent("", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));
        OSRuleResult<OSShieldSnapshot> snapshot = shieldRole.GetShieldSnapshot(1);

        Assert.That(outOfRange.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(invalid.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(snapshot.Payload.Charges, Is.EqualTo(1));
        Assert.That(snapshot.Payload.IsCharged, Is.True);
    }

    [Test]
    public void ShieldVisual_FollowsChargeStateAndUsesOneSegmentSideRadius()
    {
        GameObject maskPrefab = new GameObject("MaskPrefab");
        maskPrefab.AddComponent<SpriteRenderer>();
        SerializedObject roleObject = new SerializedObject(shieldRole);
        roleObject.FindProperty("shieldMaskPrefab").objectReferenceValue = maskPrefab;
        roleObject.ApplyModifiedProperties();

        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        Transform mask = roleHost.transform.Find("Shield Range 001");

        Assert.That(mask, Is.Not.Null);
        Assert.That(mask.gameObject.activeSelf, Is.True);
        Assert.That(mask.localScale.x, Is.EqualTo(1.8f).Within(0.0001f));
        Assert.That(mask.localScale.y, Is.EqualTo(1.8f).Within(0.0001f));

        shieldRole.TryBlock(
            new OSDamageEvent("hit_visual", OSCombatEventType.HeadDamage, 10f),
            new Vector2(-0.45f, 0f));

        Assert.That(mask.gameObject.activeSelf, Is.False);

        now = 6f;
        shieldRole.GetShieldSnapshot(1);

        Assert.That(mask.gameObject.activeSelf, Is.True);

        Object.DestroyImmediate(maskPrefab);
    }

    [Test]
    public void ShieldVisual_FollowsBodySegmentMovementOnLateUpdate()
    {
        GameObject maskPrefab = new GameObject("MaskPrefab");
        maskPrefab.AddComponent<SpriteRenderer>();
        SerializedObject roleObject = new SerializedObject(shieldRole);
        roleObject.FindProperty("shieldMaskPrefab").objectReferenceValue = maskPrefab;
        roleObject.ApplyModifiedProperties();

        bodyChain.AppendSegment(OSBodyRoleType.Shield);
        Transform mask = roleHost.transform.Find("Shield Range 001");
        Vector3 initialPosition = mask.position;

        bodyChain.RecordHeadPosition(new Vector2(2f, 0f), Vector2.right);
        InvokeLateUpdate();

        Assert.That(mask.position.x, Is.Not.EqualTo(initialPosition.x).Within(0.0001f));
        Assert.That(mask.position.x, Is.EqualTo(bodyChain.GetSegmentAt(0).Position.x).Within(0.0001f));
        Assert.That(mask.position.y, Is.EqualTo(bodyChain.GetSegmentAt(0).Position.y).Within(0.0001f));

        Object.DestroyImmediate(maskPrefab);
    }

    private void InvokeLateUpdate()
    {
        MethodInfo lateUpdate = typeof(OSShieldBodyRole).GetMethod(
            "LateUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic);

        lateUpdate.Invoke(shieldRole, null);
    }
}
#endif
