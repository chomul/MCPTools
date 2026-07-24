#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class OSGameSessionControllerTests
{
    private GameObject host;
    private GameObject inputHost;
    private OSGameSessionController controller;
    private OSInputRouter inputRouter;
    private InputActionAsset inputAsset;
    private InputActionMap playerMap;
    private InputActionMap uiMap;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private float previousTimeScale;

    [SetUp]
    public void SetUp()
    {
        previousTimeScale = Time.timeScale;
        Time.timeScale = 1f;
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        inputHost = new GameObject("InputRouter");
        inputRouter = inputHost.AddComponent<OSInputRouter>();
        ConfigureInputRouter();

        host = new GameObject("GameSession");
        controller = host.AddComponent<OSGameSessionController>();
        controller.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog, inputRouter);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(host);
        Object.DestroyImmediate(inputHost);
        Object.DestroyImmediate(inputAsset);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
        Time.timeScale = previousTimeScale;
    }

    [Test]
    public void StartSession_CreatesRuntimeAndOpensTwoBodySelectionsBeforeCombat()
    {
        List<OSSelectionRequest> opened = new List<OSSelectionRequest>();
        controller.SelectionOpened += opened.Add;

        OSRuleResult<OSSessionRuntimeState> startResult = controller.StartSession();

        Assert.That(startResult.IsAccepted, Is.True);
        Assert.That(controller.RuntimeState, Is.Not.Null);
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.SelectionQueue.HasCurrentRequest, Is.True);
        Assert.That(controller.SelectionQueue.CurrentRequest.IsBody, Is.True);
        Assert.That(controller.SelectionQueue.PendingBodyCount, Is.EqualTo(1));
        Assert.That(opened, Has.Count.EqualTo(1));

        OSRuleResult<OSSelectionRequest> first = controller.CompleteCurrentSelection(0);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.SelectionQueue.HasCurrentRequest, Is.True);
        Assert.That(controller.SelectionQueue.CurrentRequest.IsBody, Is.True);
        Assert.That(controller.SelectionQueue.PendingBodyCount, Is.EqualTo(0));

        OSRuleResult<OSSelectionRequest> second = controller.CompleteCurrentSelection(1);

        Assert.That(second.IsAccepted, Is.True);
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(controller.SelectionQueue.HasCurrentRequest, Is.False);
    }

    [Test]
    public void StateChanges_SwitchInputMapsForSelectionCombatExplosionAndDead()
    {
        controller.StartSession();

        Assert.That(inputRouter.IsPlayerMapActive, Is.False);
        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.True);

        CompleteStartingSelections();

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(inputRouter.IsPlayerMapActive, Is.True);
        Assert.That(playerMap.enabled, Is.True);
        Assert.That(uiMap.enabled, Is.False);

        Assert.That(controller.EnterExplosionTelegraph().IsAccepted, Is.True);
        Assert.That(inputRouter.IsPlayerMapActive, Is.True);
        Assert.That(playerMap.enabled, Is.True);
        Assert.That(uiMap.enabled, Is.False);

        controller.EnqueueLevelUpSelection("head_damage_boost", "body_damage_boost", "body_fragment_discount");
        controller.ProcessFixedUpdate();

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        Assert.That(inputRouter.IsPlayerMapActive, Is.False);
        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.True);

        controller.RequestDeath("test_dead");

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Dead));
        Assert.That(inputRouter.IsPlayerMapActive, Is.False);
        Assert.That(uiMap.enabled, Is.True);
    }

    [Test]
    public void SelectionStates_PauseGameplayTimeScaleAndCombatRestoresIt()
    {
        controller.StartSession();

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.True);
        Assert.That(Time.timeScale, Is.EqualTo(0f));

        Assert.That(controller.CompleteCurrentSelection(0).IsAccepted, Is.True);

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.True);
        Assert.That(Time.timeScale, Is.EqualTo(0f));

        Assert.That(controller.CompleteCurrentSelection(1).IsAccepted, Is.True);

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.False);
        Assert.That(Time.timeScale, Is.EqualTo(1f));

        Assert.That(controller.EnqueueGeneratedLevelUpSelection().IsAccepted, Is.True);
        Assert.That(controller.ProcessFixedUpdate().IsAccepted, Is.True);

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.True);
        Assert.That(Time.timeScale, Is.EqualTo(0f));

        Assert.That(controller.CompleteCurrentSelection(0).IsAccepted, Is.True);

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.False);
        Assert.That(Time.timeScale, Is.EqualTo(1f));
    }

    [Test]
    public void SelectionStates_WhenStartedFromZeroTimeScale_CombatRestoresPlayableSpeed()
    {
        Time.timeScale = 0f;

        controller.StartSession();

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.True);
        Assert.That(Time.timeScale, Is.EqualTo(0f));

        CompleteStartingSelections();

        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(controller.IsGameplayTimeScalePaused, Is.False);
        Assert.That(Time.timeScale, Is.EqualTo(1f));
    }

    [Test]
    public void ProcessFixedUpdate_DrainsCombatEventsOnceInPriorityOrder()
    {
        List<OSCombatEvent> drained = new List<OSCombatEvent>();
        controller.CombatEventDrained += drained.Add;

        controller.StartSession();
        CompleteStartingSelections();
        controller.EnqueuePickupEvent(new OSPickupEvent("pickup_b", OSPickupType.Heal, 1));
        controller.EnqueueDamageEvent(new OSDamageEvent("damage_a", OSCombatEventType.BodyDamage, 3f));

        OSRuleResult<int> firstTick = controller.ProcessFixedUpdate();
        OSRuleResult<int> secondTick = controller.ProcessFixedUpdate();

        Assert.That(firstTick.IsAccepted, Is.True);
        Assert.That(firstTick.Payload, Is.EqualTo(2));
        Assert.That(secondTick.IsAccepted, Is.True);
        Assert.That(secondTick.Payload, Is.EqualTo(0));
        Assert.That(drained, Has.Count.EqualTo(2));
        Assert.That(drained[0].Type, Is.EqualTo(OSCombatEventType.BodyDamage));
        Assert.That(drained[1].Type, Is.EqualTo(OSCombatEventType.Pickup));
    }

    [Test]
    public void ProcessFixedUpdate_BodyDamageCutsTargetSegmentThroughTail()
    {
        GameObject bodyChainHost = new GameObject("BodyChain");
        OSBodyChain bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        try
        {
            bodyChain.ConfigureForTests(bodyBalance);
            for (int i = 0; i < 4; i++)
            {
                Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
            }

            int hitStableId = bodyChain.GetSegmentAt(1).StableId;
            controller.ConfigureForTests(
                playerBalance,
                bodyBalance,
                encounterBalance,
                upgradeCatalog,
                inputRouter,
                null,
                bodyChain);
            controller.StartSession();
            CompleteStartingSelections();
            controller.EnqueueDamageEvent(new OSDamageEvent(
                "body_hit",
                OSCombatEventType.BodyDamage,
                3f,
                "enemy_chaser",
                $"body_segment:{hitStableId}"));

            OSRuleResult<int> tickResult = controller.ProcessFixedUpdate();

            Assert.That(tickResult.IsAccepted, Is.True);
            Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(1));
            Assert.That(bodyChain.GetSegmentAt(0).StableId, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(bodyChainHost);
        }
    }

    [Test]
    public void LethalHeadDamage_SkipsLaterPickupSelectionAndLeavesDeadState()
    {
        List<OSCombatEvent> drained = new List<OSCombatEvent>();
        controller.CombatEventDrained += drained.Add;

        controller.StartSession();
        CompleteStartingSelections();
        controller.EnqueuePickupEvent(new OSPickupEvent("pickup_xp", OSPickupType.Experience, 20));
        controller.EnqueueDamageEvent(new OSDamageEvent("head_hit", OSCombatEventType.HeadDamage, 200f));

        OSRuleResult<int> tickResult = controller.ProcessFixedUpdate();

        Assert.That(tickResult.IsAccepted, Is.True);
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Dead));
        Assert.That(controller.RuntimeState.TotalExperienceCollected, Is.EqualTo(0));
        Assert.That(controller.SelectionQueue.HasCurrentRequest, Is.False);
        Assert.That(controller.SelectionQueue.PendingCount, Is.EqualTo(0));
        Assert.That(drained, Has.Count.EqualTo(1));
        Assert.That(drained[0].Type, Is.EqualTo(OSCombatEventType.HeadDamage));
    }

    [Test]
    public void DeadState_DoesNotProcessCombatEventsOrSelections()
    {
        List<OSCombatEvent> drained = new List<OSCombatEvent>();
        controller.CombatEventDrained += drained.Add;

        controller.StartSession();
        CompleteStartingSelections();
        controller.EnqueuePickupEvent(new OSPickupEvent("pickup_xp", OSPickupType.Experience, 20));
        controller.RequestDeath("manual_dead");

        OSRuleResult<OSCombatEvent> enqueueAfterDeath = controller.EnqueuePickupEvent(
            new OSPickupEvent("pickup_after_death", OSPickupType.Heal, 1));
        OSRuleResult<OSSelectionRequest> queueAfterDeath = controller.EnqueueBodySelection();
        OSRuleResult<int> tickResult = controller.ProcessFixedUpdate();

        Assert.That(enqueueAfterDeath.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(queueAfterDeath.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(tickResult.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.Dead));
        Assert.That(controller.RuntimeState.TotalExperienceCollected, Is.EqualTo(0));
        Assert.That(drained, Is.Empty);
    }

    [Test]
    public void RestartSession_CreatesFreshRuntimeStateAndBodySelectionQueue()
    {
        controller.StartSession();
        OSSessionRuntimeState firstState = controller.RuntimeState;
        CompleteStartingSelections();
        controller.EnqueuePickupEvent(new OSPickupEvent("pickup_xp", OSPickupType.Experience, 20));
        controller.ProcessFixedUpdate();

        OSRuleResult<OSSessionRuntimeState> restartResult = controller.RestartSession();

        Assert.That(restartResult.IsAccepted, Is.True);
        Assert.That(controller.RuntimeState, Is.Not.SameAs(firstState));
        Assert.That(controller.RuntimeState.TotalExperienceCollected, Is.EqualTo(0));
        Assert.That(controller.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(controller.SelectionQueue.HasCurrentRequest, Is.True);
        Assert.That(controller.SelectionQueue.PendingBodyCount, Is.EqualTo(1));
        Assert.That(controller.FixedTick, Is.EqualTo(0));
    }

    [Test]
    public void GameSessionController_OnlyUsesDefinedSessionStates()
    {
        OSSessionState[] states = (OSSessionState[])System.Enum.GetValues(typeof(OSSessionState));

        Assert.That(states, Is.EqualTo(new[]
        {
            OSSessionState.Boot,
            OSSessionState.BodyRoleSelection,
            OSSessionState.Combat,
            OSSessionState.ExplosionTelegraph,
            OSSessionState.LevelUpSelection,
            OSSessionState.Dead
        }));
    }

    private void ConfigureInputRouter()
    {
        inputAsset = ScriptableObject.CreateInstance<InputActionAsset>();
        playerMap = inputAsset.AddActionMap("Player");
        uiMap = inputAsset.AddActionMap("UI");

        InputAction move = playerMap.AddAction("Move", InputActionType.Value);
        InputAction explosion = playerMap.AddAction("Explosion", InputActionType.Button);
        InputAction point = uiMap.AddAction("Point", InputActionType.Value);
        InputAction click = uiMap.AddAction("Click", InputActionType.Button);
        InputAction navigate = uiMap.AddAction("Navigate", InputActionType.Value);
        InputAction submit = uiMap.AddAction("Submit", InputActionType.Button);

        inputRouter.ConfigureForTests(move, explosion, point, click, navigate, submit);
    }

    private void CompleteStartingSelections()
    {
        Assert.That(controller.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(controller.CompleteCurrentSelection(1).IsAccepted, Is.True);
    }
}
#endif
