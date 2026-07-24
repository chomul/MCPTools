using System;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class OSBodyRoleSelectionPanel : MonoBehaviour
{
    private static readonly OSBodyRoleType[] RoleOptions =
    {
        OSBodyRoleType.Shield,
        OSBodyRoleType.Attack,
        OSBodyRoleType.Laser,
        OSBodyRoleType.Control
    };

    [Header("References")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSInputRouter inputRouter;

    [Header("Role Cards")]
    [SerializeField] private Button shieldButton;
    [SerializeField] private Button attackButton;
    [SerializeField] private Button laserButton;
    [SerializeField] private Button controlButton;

    [Header("Labels")]
    [SerializeField] private Text shieldLabel;
    [SerializeField] private Text attackLabel;
    [SerializeField] private Text laserLabel;
    [SerializeField] private Text controlLabel;

    private OSSelectionRequest currentRequest;
    private bool hasCurrentRequest;
    private bool isSubmitting;
    private int focusedOptionIndex;

    public event Action<OSBodyRoleSelectionResult> BodySelectionCompleted;

    public bool IsVisible => panelRoot != null && panelRoot.activeSelf;
    public bool IsOpen => IsVisible &&
        hasCurrentRequest &&
        sessionController != null &&
        sessionController.CurrentState == OSSessionState.BodyRoleSelection;
    public bool HasCurrentRequest => hasCurrentRequest;
    public string CurrentRequestId => hasCurrentRequest ? currentRequest.RequestId : string.Empty;
    public int FocusedOptionIndex => focusedOptionIndex;
    public OSBodyRoleType FocusedRole => RoleOptions[focusedOptionIndex];

    public void ConfigureForTests(
        GameObject root,
        OSGameSessionController session,
        OSBodyChain chain,
        OSInputRouter router,
        Button shield,
        Button attack,
        Button laser,
        Button control,
        Text shieldText = null,
        Text attackText = null,
        Text laserText = null,
        Text controlText = null)
    {
        panelRoot = root;
        sessionController = session;
        bodyChain = chain;
        inputRouter = router;
        shieldButton = shield;
        attackButton = attack;
        laserButton = laser;
        controlButton = control;
        shieldLabel = shieldText;
        attackLabel = attackText;
        laserLabel = laserText;
        controlLabel = controlText;

        ApplyStaticView();
    }

    public OSRuleResult<OSBodyRoleSelectionResult> SelectRole(int optionIndex)
    {
        return ConfirmSelection(optionIndex);
    }

    public OSRuleResult<OSBodyRoleSelectionResult> ConfirmSelection(string requestId, int optionIndex)
    {
        if (!hasCurrentRequest || currentRequest.RequestId != requestId)
        {
            return OSRuleResult<OSBodyRoleSelectionResult>.Rejected(
                OSResultCode.Duplicate,
                "selection_request_stale");
        }

        return ConfirmSelection(optionIndex);
    }

    public OSRuleResult<OSSelectionRequest> Open(OSSelectionRequest request)
    {
        if (!request.IsBody)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "selection_request_not_body");
        }

        currentRequest = request;
        hasCurrentRequest = true;
        isSubmitting = false;
        focusedOptionIndex = 0;
        ApplyStaticView();
        SetPanelVisible(true);
        SetButtonsInteractable(true);
        return OSRuleResult<OSSelectionRequest>.Accept(request);
    }

    public void SyncToSessionState()
    {
        if (sessionController == null ||
            sessionController.CurrentState != OSSessionState.BodyRoleSelection ||
            sessionController.SelectionQueue == null ||
            !sessionController.SelectionQueue.HasCurrentRequest ||
            !sessionController.SelectionQueue.CurrentRequest.IsBody)
        {
            Close();
            return;
        }

        Open(sessionController.SelectionQueue.CurrentRequest);
    }

    public void SelectShield()
    {
        ConfirmSelection(0);
    }

    public void SelectAttack()
    {
        ConfirmSelection(1);
    }

    public void SelectLaser()
    {
        ConfirmSelection(2);
    }

    public void SelectControl()
    {
        ConfirmSelection(3);
    }

    public OSRuleResult<OSBodyRoleSelectionResult> ConfirmSelection(int optionIndex)
    {
        if (isSubmitting)
        {
            return OSRuleResult<OSBodyRoleSelectionResult>.Rejected(OSResultCode.Duplicate, "body_selection_already_submitting");
        }

        OSRuleResult<int> validation = ValidateSelection(optionIndex);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSBodyRoleSelectionResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        OSBodyRoleType roleType = currentRequest.GetBodyRoleOption(optionIndex);
        focusedOptionIndex = optionIndex;
        isSubmitting = true;
        SetButtonsInteractable(false);

        OSRuleResult<OSBodySegmentSnapshot> appendResult = bodyChain.AppendSegment(roleType);
        if (!appendResult.IsAccepted)
        {
            isSubmitting = false;
            SetButtonsInteractable(true);
            return OSRuleResult<OSBodyRoleSelectionResult>.Rejected(appendResult.Code, appendResult.ReasonKey);
        }

        OSRuleResult<OSSelectionRequest> completeResult = sessionController.CompleteCurrentSelection(
            currentRequest.RequestId,
            optionIndex);
        if (!completeResult.IsAccepted)
        {
            isSubmitting = false;
            SetButtonsInteractable(true);
            return OSRuleResult<OSBodyRoleSelectionResult>.Rejected(completeResult.Code, completeResult.ReasonKey);
        }

        OSBodyRoleSelectionResult result = new OSBodyRoleSelectionResult(
            completeResult.Payload.RequestId,
            roleType,
            appendResult.Payload.StableId);

        BodySelectionCompleted?.Invoke(result);
        isSubmitting = false;
        SyncToSessionState();
        return OSRuleResult<OSBodyRoleSelectionResult>.Accept(result);
    }

    private void Awake()
    {
        ApplyStaticView();
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
        if (shieldButton != null)
        {
            shieldButton.onClick.AddListener(SelectShield);
        }

        if (attackButton != null)
        {
            attackButton.onClick.AddListener(SelectAttack);
        }

        if (laserButton != null)
        {
            laserButton.onClick.AddListener(SelectLaser);
        }

        if (controlButton != null)
        {
            controlButton.onClick.AddListener(SelectControl);
        }
    }

    private void UnregisterButtonCallbacks()
    {
        if (shieldButton != null)
        {
            shieldButton.onClick.RemoveListener(SelectShield);
        }

        if (attackButton != null)
        {
            attackButton.onClick.RemoveListener(SelectAttack);
        }

        if (laserButton != null)
        {
            laserButton.onClick.RemoveListener(SelectLaser);
        }

        if (controlButton != null)
        {
            controlButton.onClick.RemoveListener(SelectControl);
        }
    }

    private void OnSelectionOpened(OSSelectionRequest request)
    {
        if (request.IsBody)
        {
            Open(request);
            return;
        }

        Close();
    }

    private void OnStateChanged(OSSessionState state)
    {
        if (state != OSSessionState.BodyRoleSelection)
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
            nextIndex += navigate.y > 0f ? -2 : 2;
        }

        focusedOptionIndex = Mathf.Clamp(nextIndex, 0, RoleOptions.Length - 1);
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
        if (!hasCurrentRequest || !currentRequest.IsBody)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "body_selection_request_missing");
        }

        if (!currentRequest.IsValidOptionIndex(optionIndex))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "body_selection_option_invalid");
        }

        if (sessionController == null ||
            sessionController.SelectionQueue == null ||
            !sessionController.SelectionQueue.HasCurrentRequest)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "selection_request_missing");
        }

        OSSelectionRequest sessionRequest = sessionController.SelectionQueue.CurrentRequest;
        if (!sessionRequest.IsBody || sessionRequest.RequestId != currentRequest.RequestId)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.Duplicate, "selection_request_stale");
        }

        if (sessionController.CurrentState != OSSessionState.BodyRoleSelection)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_not_body_selection");
        }

        if (bodyChain == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "body_chain_missing");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private void ApplyStaticView()
    {
        SetLabel(shieldLabel, "방패");
        SetLabel(attackLabel, "공격");
        SetLabel(laserLabel, "레이저");
        SetLabel(controlLabel, "제어");
        SetButtonActive(shieldButton, true);
        SetButtonActive(attackButton, true);
        SetButtonActive(laserButton, true);
        SetButtonActive(controlButton, true);
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
        SetButtonInteractable(shieldButton, interactable);
        SetButtonInteractable(attackButton, interactable);
        SetButtonInteractable(laserButton, interactable);
        SetButtonInteractable(controlButton, interactable);
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

    private static void SetLabel(Text label, string text)
    {
        if (label != null)
        {
            label.text = text;
        }
    }
}

public readonly struct OSBodyRoleSelectionResult
{
    public OSBodyRoleSelectionResult(string requestId, OSBodyRoleType roleType, int segmentStableId)
    {
        RequestId = requestId ?? string.Empty;
        RoleType = roleType;
        SegmentStableId = segmentStableId;
    }

    public string RequestId { get; }
    public OSBodyRoleType RoleType { get; }
    public int SegmentStableId { get; }
}

