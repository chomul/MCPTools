using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSGameSessionController : MonoBehaviour
{
    private const int StartingBodySelectionRequests = 2;
    private const int LevelUpCandidateCount = OSSelectionRequest.LevelUpOptionCount;

    private static readonly OSUpgradeFamily[] EarlyLevelFamilyPriority =
    {
        OSUpgradeFamily.Firepower,
        OSUpgradeFamily.Body,
        OSUpgradeFamily.Survival
    };

    [Header("Balance")]
    [SerializeField] private OSPlayerBalanceData playerBalance;
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSEncounterBalanceData encounterBalance;
    [SerializeField] private OSUpgradeCatalog upgradeCatalog;

    [Header("Runtime")]
    [SerializeField] private OSInputRouter inputRouter;
    [SerializeField] private OSPlayerHealth playerHealth;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private bool startOnAwake;
    [SerializeField] private bool pauseGameplayDuringSelections = true;

    private readonly List<OSCombatEvent> drainedCombatEvents = new List<OSCombatEvent>(32);
    private OSSessionRuntimeState runtimeState;
    private OSCombatEventBuffer combatEventBuffer;
    private OSSelectionQueue selectionQueue;
    private int fixedTick;
    private bool isSessionRunning;
    private bool hasPausedGameplayTimeScale;
    private float previousGameplayTimeScale = 1f;

    public event Action<OSSessionRuntimeState> SessionStarted;
    public event Action<OSSessionSummary> SessionEnded;
    public event Action<OSSessionState> StateChanged;
    public event Action<OSSelectionRequest> SelectionOpened;
    public event Action<OSSelectionRequest> SelectionCompleted;
    public event Action<OSBodyRoleType> BodyRoleSelected;
    public event Action<string, int> LevelUpSelected;
    public event Action<OSCombatEvent> CombatEventDrained;

    public OSSessionRuntimeState RuntimeState => runtimeState;
    public OSCombatEventBuffer CombatEventBuffer => combatEventBuffer;
    public OSSelectionQueue SelectionQueue => selectionQueue;
    public OSSessionState CurrentState => runtimeState == null ? OSSessionState.Boot : runtimeState.State;
    public bool IsSessionRunning => isSessionRunning;
    public int FixedTick => fixedTick;
    public bool IsGameplayTimeScalePaused => hasPausedGameplayTimeScale;

    public void ConfigureForTests(
        OSPlayerBalanceData player,
        OSBodyBalanceData body,
        OSEncounterBalanceData encounter,
        OSUpgradeCatalog catalog,
        OSInputRouter router = null,
        OSPlayerHealth health = null,
        OSBodyChain chain = null)
    {
        playerBalance = player;
        bodyBalance = body;
        encounterBalance = encounter;
        upgradeCatalog = catalog;
        inputRouter = router;
        playerHealth = health;
        bodyChain = chain;
    }

    public OSRuleResult<OSSessionRuntimeState> StartSession()
    {
        OSRuleResult<OSSessionRuntimeState> initializeResult = OSSessionRuntimeState.InitializeFrom(
            playerBalance,
            bodyBalance,
            encounterBalance,
            upgradeCatalog);

        if (!initializeResult.IsAccepted)
        {
            return initializeResult;
        }

        if (inputRouter != null)
        {
            OSRuleResult<int> inputValidation = inputRouter.ValidateConfiguration();
            if (!inputValidation.IsAccepted)
            {
                return OSRuleResult<OSSessionRuntimeState>.Rejected(
                    inputValidation.Code,
                    inputValidation.ReasonKey);
            }
        }

        runtimeState = initializeResult.Payload;
        combatEventBuffer = new OSCombatEventBuffer();
        selectionQueue = new OSSelectionQueue();
        fixedTick = 0;
        isSessionRunning = true;
        combatEventBuffer.BeginTick(fixedTick);

        if (playerHealth != null)
        {
            OSRuleResult<OSPlayerHealthSnapshot> healthResult = playerHealth.BindRuntimeState(runtimeState, this);
            if (!healthResult.IsAccepted)
            {
                isSessionRunning = false;
                return OSRuleResult<OSSessionRuntimeState>.Rejected(
                    healthResult.Code,
                    healthResult.ReasonKey);
            }
        }

        for (int i = 0; i < StartingBodySelectionRequests; i++)
        {
            OSRuleResult<OSSelectionRequest> enqueueResult = selectionQueue.EnqueueBody();
            if (!enqueueResult.IsAccepted)
            {
                isSessionRunning = false;
                return OSRuleResult<OSSessionRuntimeState>.Rejected(
                    enqueueResult.Code,
                    enqueueResult.ReasonKey);
            }
        }

        SessionStarted?.Invoke(runtimeState);
        OpenNextSelectionOrCombat();
        return OSRuleResult<OSSessionRuntimeState>.Accept(runtimeState);
    }

    public OSRuleResult<OSSessionRuntimeState> RestartSession()
    {
        return StartSession();
    }

    public OSRuleResult<OSSessionSummary> RequestDeath(string reasonKey = "dead")
    {
        if (runtimeState == null)
        {
            return OSRuleResult<OSSessionSummary>.Rejected(OSResultCode.RejectedState, "session_missing");
        }

        isSessionRunning = false;
        combatEventBuffer?.Clear();
        selectionQueue?.CancelAll();
        ChangeState(OSSessionState.Dead);

        OSRuleResult<OSSessionSummary> summaryResult = runtimeState.BuildSummary(
            OSResultCode.RejectedState,
            Time.timeSinceLevelLoad,
            string.IsNullOrWhiteSpace(reasonKey) ? "dead" : reasonKey);

        if (summaryResult.IsAccepted)
        {
            SessionEnded?.Invoke(summaryResult.Payload);
        }

        return summaryResult;
    }

    public OSRuleResult<OSCombatEvent> EnqueueCombatEvent(OSCombatEvent combatEvent)
    {
        if (!CanAcceptCombatEvent())
        {
            return OSRuleResult<OSCombatEvent>.Rejected(OSResultCode.RejectedState, "session_not_in_combat");
        }

        return combatEventBuffer.Enqueue(combatEvent);
    }

    public OSRuleResult<OSCombatEvent> EnqueueDamageEvent(OSDamageEvent damageEvent)
    {
        return EnqueueCombatEvent(new OSCombatEvent(damageEvent));
    }

    public OSRuleResult<OSCombatEvent> EnqueuePickupEvent(OSPickupEvent pickupEvent)
    {
        return EnqueueCombatEvent(new OSCombatEvent(pickupEvent));
    }

    public OSRuleResult<OSCombatEvent> EnqueueExplosionCompleted(string eventId, string sourceId = "")
    {
        if (!CanAcceptCombatEvent())
        {
            return OSRuleResult<OSCombatEvent>.Rejected(OSResultCode.RejectedState, "session_not_in_combat");
        }

        return combatEventBuffer.EnqueueExplosionCompleted(eventId, sourceId);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueBodySelection()
    {
        if (!CanQueueSelection())
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "session_not_running");
        }

        return selectionQueue.EnqueueBody();
    }

    public OSRuleResult<OSSelectionRequest> EnqueueLevelUpSelection(
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        if (!CanQueueSelection())
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "session_not_running");
        }

        return selectionQueue.EnqueueLevelUp(firstUpgradeId, secondUpgradeId, thirdUpgradeId);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueGeneratedLevelUpSelection()
    {
        if (!CanQueueSelection())
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "session_not_running");
        }

        OSRuleResult<OSUpgradeDefinitionSnapshot[]> candidatesResult = BuildGeneratedLevelUpCandidates();
        if (!candidatesResult.IsAccepted)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(
                candidatesResult.Code,
                candidatesResult.ReasonKey);
        }

        OSUpgradeDefinitionSnapshot[] candidates = candidatesResult.Payload;
        return selectionQueue.EnqueueLevelUp(candidates);
    }

    public OSRuleResult<OSSelectionRequest> CompleteCurrentSelection(int selectedOptionIndex)
    {
        if (selectionQueue == null || !selectionQueue.HasCurrentRequest)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "selection_request_missing");
        }

        return CompleteCurrentSelection(selectionQueue.CurrentRequest.RequestId, selectedOptionIndex);
    }

    public OSRuleResult<OSSelectionRequest> CompleteCurrentSelection(string requestId, int selectedOptionIndex)
    {
        if (runtimeState == null || selectionQueue == null)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "session_missing");
        }

        if (runtimeState.State == OSSessionState.Dead)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (!selectionQueue.HasCurrentRequest)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "selection_request_missing");
        }

        OSSelectionRequest currentRequest = selectionQueue.CurrentRequest;
        OSRuleResult<OSSelectionRequest> completeResult = selectionQueue.CompleteCurrent(
            requestId,
            selectedOptionIndex);

        if (!completeResult.IsAccepted)
        {
            return completeResult;
        }

        if (currentRequest.IsBody)
        {
            BodyRoleSelected?.Invoke(currentRequest.GetBodyRoleOption(selectedOptionIndex));
        }
        else
        {
            string upgradeId = currentRequest.GetLevelUpOptionId(selectedOptionIndex);
            OSRuleResult<int> upgradeResult = runtimeState.ApplyUpgrade(upgradeId);
            if (!upgradeResult.IsAccepted)
            {
                return OSRuleResult<OSSelectionRequest>.Rejected(upgradeResult.Code, upgradeResult.ReasonKey);
            }

            LevelUpSelected?.Invoke(upgradeId, upgradeResult.Payload);
        }

        SelectionCompleted?.Invoke(currentRequest);
        OpenNextSelectionOrCombat();
        return completeResult;
    }

    public OSRuleResult<int> ProcessFixedUpdate()
    {
        if (runtimeState == null || combatEventBuffer == null || selectionQueue == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_missing");
        }

        if (runtimeState.State == OSSessionState.Dead)
        {
            combatEventBuffer.Clear();
            selectionQueue.CancelAll();
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        int drainedCount = combatEventBuffer.DrainInPriorityOrder(drainedCombatEvents);
        for (int i = 0; i < drainedCombatEvents.Count; i++)
        {
            OSCombatEvent combatEvent = drainedCombatEvents[i];
            CombatEventDrained?.Invoke(combatEvent);

            if (runtimeState.State == OSSessionState.Dead)
            {
                break;
            }

            if (combatEvent.Type == OSCombatEventType.HeadDamage)
            {
                if (playerHealth != null)
                {
                    playerHealth.TryApplyHeadHit(combatEvent.DamageEvent);
                    if (runtimeState.State == OSSessionState.Dead)
                    {
                        break;
                    }
                }
                else if (combatEvent.DamageEvent.Amount >= runtimeState.CurrentHp)
                {
                    RequestDeath("head_damage");
                    break;
                }
            }

            if (combatEvent.Type == OSCombatEventType.BodyDamage)
            {
                ApplyBodyDamageEvent(combatEvent.DamageEvent);
            }

            if (combatEvent.Type == OSCombatEventType.Pickup)
            {
                ApplyPickupEvent(combatEvent.PickupEvent);
            }
        }

        drainedCombatEvents.Clear();
        if (runtimeState.State != OSSessionState.Dead)
        {
            OpenNextSelectionOrCombat();
            fixedTick++;
            combatEventBuffer.BeginTick(fixedTick);
        }

        return OSRuleResult<int>.Accept(drainedCount);
    }

    public OSRuleResult<OSSessionState> EnterExplosionTelegraph()
    {
        if (runtimeState == null || runtimeState.State == OSSessionState.Dead)
        {
            return OSRuleResult<OSSessionState>.Rejected(OSResultCode.RejectedState, "session_not_running");
        }

        return ChangeState(OSSessionState.ExplosionTelegraph);
    }

    public OSRuleResult<OSSessionState> ExitExplosionTelegraph()
    {
        if (runtimeState == null || runtimeState.State == OSSessionState.Dead)
        {
            return OSRuleResult<OSSessionState>.Rejected(OSResultCode.RejectedState, "session_not_running");
        }

        if (runtimeState.State != OSSessionState.ExplosionTelegraph)
        {
            return OSRuleResult<OSSessionState>.Rejected(OSResultCode.RejectedState, "session_not_in_explosion_telegraph");
        }

        return ChangeState(OSSessionState.Combat);
    }

    private void Awake()
    {
        if (startOnAwake)
        {
            StartSession();
        }
    }

    private void ApplyBodyDamageEvent(OSDamageEvent damageEvent)
    {
        if (bodyChain == null)
        {
            return;
        }

        if (!TryParseBodySegmentStableId(damageEvent.TargetId, out int stableId))
        {
            return;
        }

        bodyChain.TryCutFromStableId(stableId);
    }

    private void OnDisable()
    {
        RestoreGameplayTimeScale();
    }

    private void FixedUpdate()
    {
        if (isSessionRunning)
        {
            ProcessFixedUpdate();
        }
    }

    private void ApplyPickupEvent(OSPickupEvent pickupEvent)
    {
        if (pickupEvent.PickupType == OSPickupType.Heal && playerHealth != null)
        {
            playerHealth.ApplyHeal(pickupEvent.Amount);
            return;
        }

        OSRuleResult<OSPickupApplyResult> pickupResult = runtimeState.ApplyPickup(
            pickupEvent.PickupType,
            pickupEvent.Amount);

        if (!pickupResult.IsAccepted)
        {
            return;
        }

        for (int i = 0; i < pickupResult.Payload.BodyRequestsCreated; i++)
        {
            selectionQueue.EnqueueBody();
        }

        for (int i = 0; i < pickupResult.Payload.LevelUpRequestsCreated; i++)
        {
            TryEnqueueGeneratedLevelUp();
        }
    }

    private void TryEnqueueGeneratedLevelUp()
    {
        EnqueueGeneratedLevelUpSelection();
    }

    private OSRuleResult<OSUpgradeDefinitionSnapshot[]> BuildGeneratedLevelUpCandidates()
    {
        if (runtimeState == null)
        {
            return OSRuleResult<OSUpgradeDefinitionSnapshot[]>.Rejected(
                OSResultCode.RejectedState,
                "runtime_state_missing");
        }

        OSUpgradeCatalogSnapshot snapshot = runtimeState.UpgradeCatalog;
        OSUpgradeDefinitionSnapshot[] candidates = new OSUpgradeDefinitionSnapshot[LevelUpCandidateCount];
        int found = 0;

        if (runtimeState.Level <= 3)
        {
            for (int i = 0; i < EarlyLevelFamilyPriority.Length && found < LevelUpCandidateCount; i++)
            {
                if (TryFindFirstEligibleByFamily(
                    snapshot,
                    EarlyLevelFamilyPriority[i],
                    candidates,
                    found,
                    out OSUpgradeDefinitionSnapshot candidate))
                {
                    candidates[found] = candidate;
                    found++;
                }
            }
        }

        for (int i = 0; i < snapshot.Upgrades.Length && found < LevelUpCandidateCount; i++)
        {
            OSUpgradeDefinitionSnapshot upgrade = snapshot.Upgrades[i];
            if (!IsEligibleLevelUpCandidate(upgrade) || ContainsCandidate(candidates, found, upgrade.Id))
            {
                continue;
            }

            candidates[found] = upgrade;
            found++;
        }

        if (found < LevelUpCandidateCount)
        {
            return OSRuleResult<OSUpgradeDefinitionSnapshot[]>.Rejected(
                OSResultCode.ConfigurationError,
                "level_up_candidates_insufficient");
        }

        return OSRuleResult<OSUpgradeDefinitionSnapshot[]>.Accept(candidates);
    }

    private bool TryFindFirstEligibleByFamily(
        OSUpgradeCatalogSnapshot snapshot,
        OSUpgradeFamily family,
        OSUpgradeDefinitionSnapshot[] selected,
        int selectedCount,
        out OSUpgradeDefinitionSnapshot candidate)
    {
        for (int i = 0; i < snapshot.Upgrades.Length; i++)
        {
            OSUpgradeDefinitionSnapshot upgrade = snapshot.Upgrades[i];
            if (upgrade.Family == family &&
                IsEligibleLevelUpCandidate(upgrade) &&
                !ContainsCandidate(selected, selectedCount, upgrade.Id))
            {
                candidate = upgrade;
                return true;
            }
        }

        candidate = default;
        return false;
    }

    private bool IsEligibleLevelUpCandidate(OSUpgradeDefinitionSnapshot upgrade)
    {
        return upgrade.IsP0Candidate &&
            upgrade.RequiredPlayerLevel <= runtimeState.Level &&
            runtimeState.GetUpgradeLevel(upgrade.Id) < upgrade.MaxLevel;
    }

    private static bool ContainsCandidate(OSUpgradeDefinitionSnapshot[] candidates, int count, string upgradeId)
    {
        for (int i = 0; i < count; i++)
        {
            if (candidates[i].Id == upgradeId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBodySegmentStableId(string targetId, out int stableId)
    {
        stableId = 0;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        const string prefix = "body_segment:";
        string value = targetId.StartsWith(prefix, StringComparison.Ordinal)
            ? targetId.Substring(prefix.Length)
            : targetId;
        return int.TryParse(value, out stableId) && stableId > 0;
    }

    private void OpenNextSelectionOrCombat()
    {
        if (runtimeState == null || selectionQueue == null || runtimeState.State == OSSessionState.Dead)
        {
            return;
        }

        if (selectionQueue.HasCurrentRequest)
        {
            return;
        }

        if (selectionQueue.TryOpenNext(out OSSelectionRequest request))
        {
            ChangeState(request.IsBody ? OSSessionState.BodyRoleSelection : OSSessionState.LevelUpSelection);
            SelectionOpened?.Invoke(request);
            return;
        }

        ChangeState(OSSessionState.Combat);
    }

    private OSRuleResult<OSSessionState> ChangeState(OSSessionState nextState)
    {
        if (runtimeState == null)
        {
            return OSRuleResult<OSSessionState>.Rejected(OSResultCode.RejectedState, "session_missing");
        }

        OSSessionState previousState = runtimeState.State;
        OSRuleResult<OSSessionState> result = runtimeState.SetState(nextState);
        if (!result.IsAccepted)
        {
            return result;
        }

        if (previousState != nextState)
        {
            ApplyInputMapForState(nextState);
            ApplyGameplayPauseForState(nextState);
            StateChanged?.Invoke(nextState);
        }

        return result;
    }

    private void ApplyInputMapForState(OSSessionState state)
    {
        if (inputRouter == null)
        {
            return;
        }

        switch (state)
        {
            case OSSessionState.Combat:
            case OSSessionState.ExplosionTelegraph:
                inputRouter.ActivatePlayerMap();
                break;
            case OSSessionState.BodyRoleSelection:
            case OSSessionState.LevelUpSelection:
            case OSSessionState.Dead:
                inputRouter.ActivateUiMap();
                break;
        }
    }

    private void ApplyGameplayPauseForState(OSSessionState state)
    {
        if (!pauseGameplayDuringSelections)
        {
            RestoreGameplayTimeScale();
            return;
        }

        if (state == OSSessionState.BodyRoleSelection || state == OSSessionState.LevelUpSelection)
        {
            PauseGameplayTimeScale();
            return;
        }

        RestoreGameplayTimeScale();
    }

    private void PauseGameplayTimeScale()
    {
        if (hasPausedGameplayTimeScale)
        {
            return;
        }

        previousGameplayTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        Time.timeScale = 0f;
        hasPausedGameplayTimeScale = true;
    }

    private void RestoreGameplayTimeScale()
    {
        if (!hasPausedGameplayTimeScale)
        {
            return;
        }

        Time.timeScale = previousGameplayTimeScale > 0f ? previousGameplayTimeScale : 1f;
        hasPausedGameplayTimeScale = false;
    }

    private bool CanAcceptCombatEvent()
    {
        return runtimeState != null &&
            combatEventBuffer != null &&
            runtimeState.State != OSSessionState.Dead &&
            (runtimeState.State == OSSessionState.Combat || runtimeState.State == OSSessionState.ExplosionTelegraph);
    }

    private bool CanQueueSelection()
    {
        return runtimeState != null &&
            selectionQueue != null &&
            runtimeState.State != OSSessionState.Dead;
    }
}
