using System;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class OSLevelUpPanel : MonoBehaviour
{
    private const int CandidateCount = OSSelectionRequest.LevelUpOptionCount;

    [Header("References")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private OSUpgradeCatalog upgradeCatalog;
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private OSInputRouter inputRouter;

    [Header("Upgrade Cards")]
    [SerializeField] private Button firstButton;
    [SerializeField] private Button secondButton;
    [SerializeField] private Button thirdButton;

    [Header("Labels")]
    [SerializeField] private Text firstLabel;
    [SerializeField] private Text secondLabel;
    [SerializeField] private Text thirdLabel;

    private OSSelectionRequest currentRequest;
    private bool hasCurrentRequest;
    private bool isSubmitting;
    private int focusedOptionIndex;

    public event Action<OSLevelUpSelectionResult> LevelUpSelectionCompleted;

    public bool IsVisible => panelRoot != null && panelRoot.activeSelf;
    public bool IsOpen => IsVisible &&
        hasCurrentRequest &&
        sessionController != null &&
        sessionController.CurrentState == OSSessionState.LevelUpSelection;
    public bool HasCurrentRequest => hasCurrentRequest;
    public string CurrentRequestId => hasCurrentRequest ? currentRequest.RequestId : string.Empty;
    public int FocusedOptionIndex => focusedOptionIndex;

    public void ConfigureForTests(
        GameObject root,
        OSUpgradeCatalog catalog,
        OSGameSessionController session,
        OSInputRouter router,
        Button first,
        Button second,
        Button third,
        Text firstText = null,
        Text secondText = null,
        Text thirdText = null)
    {
        UnregisterButtonCallbacks();
        Unsubscribe();

        panelRoot = root;
        upgradeCatalog = catalog;
        sessionController = session;
        inputRouter = router;
        firstButton = first;
        secondButton = second;
        thirdButton = third;
        firstLabel = firstText;
        secondLabel = secondText;
        thirdLabel = thirdText;

        ApplyRequestView();
        Subscribe();
        RegisterButtonCallbacks();
    }

    public OSRuleResult<OSSelectionRequest> RequestLevelUpSelectionFromCatalog()
    {
        if (upgradeCatalog == null ||
            upgradeCatalog.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            sessionController == null)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(
                OSResultCode.ConfigurationError,
                "level_up_candidate_source_invalid");
        }

        return sessionController.EnqueueGeneratedLevelUpSelection();
    }

    public OSRuleResult<OSSelectionRequest> Open(OSSelectionRequest request)
    {
        if (!request.IsLevelUp)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "selection_request_not_level_up");
        }

        OSRuleResult<int> validation = ValidateRequestCandidates(request);
        if (!validation.IsAccepted)
        {
            Close();
            return OSRuleResult<OSSelectionRequest>.Rejected(validation.Code, validation.ReasonKey);
        }

        currentRequest = request;
        hasCurrentRequest = true;
        isSubmitting = false;
        focusedOptionIndex = 0;
        ApplyRequestView();
        SetPanelVisible(true);
        SetButtonsInteractable(true);
        return OSRuleResult<OSSelectionRequest>.Accept(request);
    }

    public void SyncToSessionState()
    {
        if (sessionController == null ||
            sessionController.CurrentState != OSSessionState.LevelUpSelection ||
            sessionController.SelectionQueue == null ||
            !sessionController.SelectionQueue.HasCurrentRequest ||
            !sessionController.SelectionQueue.CurrentRequest.IsLevelUp)
        {
            Close();
            return;
        }

        Open(sessionController.SelectionQueue.CurrentRequest);
    }

    public void SelectFirst()
    {
        ConfirmSelection(0);
    }

    public void SelectSecond()
    {
        ConfirmSelection(1);
    }

    public void SelectThird()
    {
        ConfirmSelection(2);
    }

    public OSRuleResult<OSLevelUpSelectionResult> ConfirmSelection(int optionIndex)
    {
        if (isSubmitting)
        {
            return OSRuleResult<OSLevelUpSelectionResult>.Rejected(OSResultCode.Duplicate, "level_up_selection_already_submitting");
        }

        OSRuleResult<int> validation = ValidateSelection(optionIndex);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSLevelUpSelectionResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        string upgradeId = currentRequest.GetLevelUpOptionId(optionIndex);
        int previousLevel = sessionController.RuntimeState.GetUpgradeLevel(upgradeId);
        focusedOptionIndex = optionIndex;
        isSubmitting = true;
        SetButtonsInteractable(false);

        OSRuleResult<OSSelectionRequest> completeResult = sessionController.CompleteCurrentSelection(
            currentRequest.RequestId,
            optionIndex);

        if (!completeResult.IsAccepted)
        {
            isSubmitting = false;
            SetButtonsInteractable(true);
            return OSRuleResult<OSLevelUpSelectionResult>.Rejected(completeResult.Code, completeResult.ReasonKey);
        }

        int appliedLevel = sessionController.RuntimeState.GetUpgradeLevel(upgradeId);
        OSLevelUpSelectionResult result = new OSLevelUpSelectionResult(
            completeResult.Payload.RequestId,
            upgradeId,
            previousLevel,
            appliedLevel);

        LevelUpSelectionCompleted?.Invoke(result);
        isSubmitting = false;
        SyncToSessionState();
        return OSRuleResult<OSLevelUpSelectionResult>.Accept(result);
    }

    private void Awake()
    {
        ApplyRequestView();
        SetPanelVisible(false);
    }

    private void OnEnable()
    {
        Subscribe();
        RegisterButtonCallbacks();
    }

    private void Start()
    {
        SyncToSessionState();
    }

    private void OnDisable()
    {
        UnregisterButtonCallbacks();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (sessionController == null)
        {
            return;
        }

        sessionController.SelectionOpened += OnSelectionOpened;
        sessionController.StateChanged += OnStateChanged;

        if (inputRouter != null)
        {
            inputRouter.UiNavigateChanged += OnNavigate;
            inputRouter.UiSubmitPressed += OnSubmit;
        }
    }

    private void Unsubscribe()
    {
        if (sessionController == null)
        {
            return;
        }

        sessionController.SelectionOpened -= OnSelectionOpened;
        sessionController.StateChanged -= OnStateChanged;

        if (inputRouter != null)
        {
            inputRouter.UiNavigateChanged -= OnNavigate;
            inputRouter.UiSubmitPressed -= OnSubmit;
        }
    }

    private void RegisterButtonCallbacks()
    {
        if (firstButton != null)
        {
            firstButton.onClick.AddListener(SelectFirst);
        }

        if (secondButton != null)
        {
            secondButton.onClick.AddListener(SelectSecond);
        }

        if (thirdButton != null)
        {
            thirdButton.onClick.AddListener(SelectThird);
        }
    }

    private void UnregisterButtonCallbacks()
    {
        if (firstButton != null)
        {
            firstButton.onClick.RemoveListener(SelectFirst);
        }

        if (secondButton != null)
        {
            secondButton.onClick.RemoveListener(SelectSecond);
        }

        if (thirdButton != null)
        {
            thirdButton.onClick.RemoveListener(SelectThird);
        }
    }

    private void OnSelectionOpened(OSSelectionRequest request)
    {
        if (request.IsLevelUp)
        {
            Open(request);
            return;
        }

        Close();
    }

    private void OnStateChanged(OSSessionState state)
    {
        if (state != OSSessionState.LevelUpSelection)
        {
            Close();
            return;
        }

        SyncToSessionState();
    }

    private void OnNavigate(Vector2 navigate)
    {
        if (!IsOpen || navigate == Vector2.zero)
        {
            return;
        }

        int nextIndex = focusedOptionIndex;
        if (Mathf.Abs(navigate.x) >= Mathf.Abs(navigate.y))
        {
            nextIndex += navigate.x > 0f ? 1 : -1;
        }
        else
        {
            nextIndex += navigate.y > 0f ? -1 : 1;
        }

        focusedOptionIndex = Mathf.Clamp(nextIndex, 0, CandidateCount - 1);
    }

    private void OnSubmit()
    {
        if (IsOpen)
        {
            ConfirmSelection(focusedOptionIndex);
        }
    }

    private OSRuleResult<int> ValidateSelection(int optionIndex)
    {
        if (!hasCurrentRequest || !currentRequest.IsLevelUp)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "level_up_selection_request_missing");
        }

        if (!currentRequest.IsValidOptionIndex(optionIndex))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "level_up_selection_option_invalid");
        }

        if (sessionController == null ||
            sessionController.SelectionQueue == null ||
            !sessionController.SelectionQueue.HasCurrentRequest ||
            sessionController.RuntimeState == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "selection_request_missing");
        }

        OSSelectionRequest sessionRequest = sessionController.SelectionQueue.CurrentRequest;
        if (!sessionRequest.IsLevelUp || sessionRequest.RequestId != currentRequest.RequestId)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.Duplicate, "selection_request_stale");
        }

        if (sessionController.CurrentState != OSSessionState.LevelUpSelection)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_not_level_up_selection");
        }

        return ValidateRequestCandidates(currentRequest);
    }

    private OSRuleResult<int> ValidateRequestCandidates(OSSelectionRequest request)
    {
        if (sessionController == null || sessionController.RuntimeState == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_missing");
        }

        string first = request.FirstUpgradeId;
        string second = request.SecondUpgradeId;
        string third = request.ThirdUpgradeId;
        if (string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second) ||
            string.IsNullOrWhiteSpace(third) ||
            first == second ||
            first == third ||
            second == third)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "level_up_options_invalid");
        }

        for (int i = 0; i < CandidateCount; i++)
        {
            string upgradeId = request.GetLevelUpOptionId(i);
            if (!TryFindUpgrade(upgradeId, out OSUpgradeDefinitionSnapshot upgrade))
            {
                return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "level_up_candidate_unknown");
            }

            if (sessionController.RuntimeState.GetUpgradeLevel(upgrade.Id) >= upgrade.MaxLevel)
            {
                return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "level_up_candidate_max_level");
            }
        }

        return OSRuleResult<int>.Accept(1);
    }

    private bool TryFindUpgrade(string upgradeId, out OSUpgradeDefinitionSnapshot upgrade)
    {
        if (sessionController == null || sessionController.RuntimeState == null)
        {
            upgrade = default;
            return false;
        }

        OSUpgradeCatalogSnapshot snapshot = sessionController.RuntimeState.UpgradeCatalog;
        for (int i = 0; i < snapshot.Upgrades.Length; i++)
        {
            if (snapshot.Upgrades[i].Id == upgradeId)
            {
                upgrade = snapshot.Upgrades[i];
                return true;
            }
        }

        upgrade = default;
        return false;
    }

    private void ApplyRequestView()
    {
        ApplyCard(firstButton, firstLabel, 0);
        ApplyCard(secondButton, secondLabel, 1);
        ApplyCard(thirdButton, thirdLabel, 2);
    }

    private void ApplyCard(Button button, Text label, int optionIndex)
    {
        SetButtonActive(button, true);

        if (label == null)
        {
            return;
        }

        if (!hasCurrentRequest || !currentRequest.IsLevelUp || !currentRequest.IsValidOptionIndex(optionIndex))
        {
            label.text = "Upgrade";
            return;
        }

        string upgradeId = currentRequest.GetLevelUpOptionId(optionIndex);
        if (!TryFindUpgrade(upgradeId, out OSUpgradeDefinitionSnapshot upgrade))
        {
            label.text = upgradeId;
            return;
        }

        int nextLevel = sessionController.RuntimeState.GetUpgradeLevel(upgrade.Id) + 1;
        label.text = $"{GetUpgradeLabel(upgrade.Id)}\n{GetFamilyLabel(upgrade.Family)} {nextLevel}/{upgrade.MaxLevel}";
    }

    private void Close()
    {
        hasCurrentRequest = false;
        isSubmitting = false;
        focusedOptionIndex = 0;
        SetPanelVisible(false);
        SetButtonsInteractable(false);
    }

    private void SetPanelVisible(bool visible)
    {
        if (panelRoot != null)
        {
            panelRoot.SetActive(visible);
        }
    }

    private void SetButtonsInteractable(bool interactable)
    {
        SetButtonInteractable(firstButton, interactable);
        SetButtonInteractable(secondButton, interactable);
        SetButtonInteractable(thirdButton, interactable);
    }

    private static void SetButtonActive(Button button, bool active)
    {
        if (button != null)
        {
            button.gameObject.SetActive(active);
        }
    }

    private static void SetButtonInteractable(Button button, bool interactable)
    {
        if (button != null)
        {
            button.interactable = interactable;
        }
    }

    private static string GetFamilyLabel(OSUpgradeFamily family)
    {
        switch (family)
        {
            case OSUpgradeFamily.Firepower:
                return "화력";
            case OSUpgradeFamily.Body:
                return "몸통";
            case OSUpgradeFamily.Explosion:
                return "폭발";
            case OSUpgradeFamily.Survival:
                return "생존";
            case OSUpgradeFamily.Utility:
                return "유틸";
            default:
                return "기타";
        }
    }

    private static string GetUpgradeLabel(string upgradeId)
    {
        switch (upgradeId)
        {
            case "head_damage_boost":
                return "머리 공격력 증가";
            case "head_fire_rate_boost":
                return "머리 연사 증가";
            case "head_pierce_add":
                return "머리 관통 추가";
            case "body_fragment_discount":
                return "몸통 성장 비용 감소";
            case "body_damage_bonus_add":
                return "몸통 화력 보너스";
            case "explosion_radius_boost":
                return "폭발 범위 증가";
            case "explosion_damage_boost":
                return "폭발 피해 증가";
            case "explosion_consumption_discount":
                return "폭발 소모 감소";
            case "max_hp_boost":
                return "최대 체력 증가";
            case "move_speed_boost":
                return "이동 속도 증가";
            case "heal_gain_boost":
                return "회복량 증가";
            case "magnet_radius_boost":
                return "흡수 범위 증가";
            case "experience_gain_boost":
                return "경험치 획득 증가";
            case "elite_target_priority":
                return "정예 우선 조준";
            default:
                return string.IsNullOrWhiteSpace(upgradeId) ? "업그레이드" : upgradeId;
        }
    }
}

public readonly struct OSLevelUpSelectionResult
{
    public OSLevelUpSelectionResult(string requestId, string upgradeId, int previousLevel, int appliedLevel)
    {
        RequestId = requestId ?? string.Empty;
        UpgradeId = upgradeId ?? string.Empty;
        PreviousLevel = previousLevel;
        AppliedLevel = appliedLevel;
    }

    public string RequestId { get; }
    public string UpgradeId { get; }
    public int PreviousLevel { get; }
    public int AppliedLevel { get; }
}
