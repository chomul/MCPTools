#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class OSBodyRoleSelectionPanelTests
{
    private GameObject sessionHost;
    private GameObject inputHost;
    private GameObject chainHost;
    private GameObject panelHost;
    private GameObject panelRoot;
    private OSGameSessionController sessionController;
    private OSInputRouter inputRouter;
    private OSBodyChain bodyChain;
    private OSBodyRoleSelectionPanel panel;
    private InputActionAsset inputAsset;
    private InputActionMap playerMap;
    private InputActionMap uiMap;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private Button shieldButton;
    private Button attackButton;
    private Button laserButton;
    private Button controlButton;
    private Text shieldLabel;
    private Text attackLabel;
    private Text laserLabel;
    private Text controlLabel;

    [SetUp]
    public void SetUp()
    {
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        inputHost = new GameObject("InputRouter");
        inputRouter = inputHost.AddComponent<OSInputRouter>();
        ConfigureInputRouter();

        sessionHost = new GameObject("GameSession");
        sessionController = sessionHost.AddComponent<OSGameSessionController>();
        sessionController.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog, inputRouter);

        chainHost = new GameObject("BodyChain");
        bodyChain = chainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);

        panelHost = new GameObject("BodyRoleSelectionPanel");
        panel = panelHost.AddComponent<OSBodyRoleSelectionPanel>();
        panelRoot = new GameObject("PanelRoot");
        panelRoot.transform.SetParent(panelHost.transform);
        shieldButton = CreateButton("Shield", out shieldLabel);
        attackButton = CreateButton("Attack", out attackLabel);
        laserButton = CreateButton("Laser", out laserLabel);
        controlButton = CreateButton("Control", out controlLabel);

        panel.ConfigureForTests(
            panelRoot,
            sessionController,
            bodyChain,
            inputRouter,
            shieldButton,
            attackButton,
            laserButton,
            controlButton,
            shieldLabel,
            attackLabel,
            laserLabel,
            controlLabel);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(panelHost);
        Object.DestroyImmediate(chainHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(inputHost);
        Object.DestroyImmediate(inputAsset);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void Open_DisplaysFourRoleCardsEveryTime()
    {
        sessionController.StartSession();

        OSRuleResult<OSSelectionRequest> openResult = panel.Open(sessionController.SelectionQueue.CurrentRequest);

        Assert.That(openResult.IsAccepted, Is.True);
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(shieldButton.gameObject.activeSelf, Is.True);
        Assert.That(attackButton.gameObject.activeSelf, Is.True);
        Assert.That(laserButton.gameObject.activeSelf, Is.True);
        Assert.That(controlButton.gameObject.activeSelf, Is.True);
        Assert.That(shieldButton.interactable, Is.True);
        Assert.That(attackButton.interactable, Is.True);
        Assert.That(laserButton.interactable, Is.True);
        Assert.That(controlButton.interactable, Is.True);
        Assert.That(shieldLabel.text, Is.EqualTo("방패"));
        Assert.That(attackLabel.text, Is.EqualTo("공격"));
        Assert.That(laserLabel.text, Is.EqualTo("레이저"));
        Assert.That(controlLabel.text, Is.EqualTo("제어"));
    }

    [Test]
    public void StartSession_UsesUiMapWhileBodySelectionIsOpen()
    {
        sessionController.StartSession();
        panel.SyncToSessionState();

        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(inputRouter.IsPlayerMapActive, Is.False);
        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.True);
    }

    [Test]
    public void ConfirmSelection_AppendsBodyAndOpensSecondBodyRequestSerially()
    {
        sessionController.StartSession();
        panel.Open(sessionController.SelectionQueue.CurrentRequest);
        string firstRequestId = panel.CurrentRequestId;

        OSRuleResult<OSBodyRoleSelectionResult> first = panel.ConfirmSelection(1);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.RequestId, Is.EqualTo(firstRequestId));
        Assert.That(first.Payload.RoleType, Is.EqualTo(OSBodyRoleType.Attack));
        Assert.That(first.Payload.SegmentStableId, Is.EqualTo(1));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(1));
        Assert.That(bodyChain.GetSegmentAt(0).RoleType, Is.EqualTo(OSBodyRoleType.Attack));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(panel.CurrentRequestId, Is.Not.EqualTo(firstRequestId));

        OSRuleResult<OSBodyRoleSelectionResult> second = panel.ConfirmSelection(2);

        Assert.That(second.IsAccepted, Is.True);
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(2));
        Assert.That(bodyChain.GetSegmentAt(1).RoleType, Is.EqualTo(OSBodyRoleType.Laser));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(panel.IsVisible, Is.False);
    }

    [Test]
    public void ConfirmSelection_RejectsStaleRequestIdWithoutAppendingBody()
    {
        sessionController.StartSession();
        OSSelectionRequest staleRequest = sessionController.SelectionQueue.CurrentRequest;
        panel.Open(staleRequest);

        Assert.That(sessionController.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));

        panel.Open(staleRequest);
        OSRuleResult<OSBodyRoleSelectionResult> result = panel.ConfirmSelection(0);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(0));
        Assert.That(sessionController.SelectionQueue.CurrentRequest.RequestId, Is.Not.EqualTo(staleRequest.RequestId));
    }

    [Test]
    public void AppendFailure_KeepsCurrentRequestOpen()
    {
        for (int i = 0; i < bodyBalance.TechnicalSegmentLimit; i++)
        {
            Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
        }

        sessionController.StartSession();
        panel.Open(sessionController.SelectionQueue.CurrentRequest);
        string requestId = panel.CurrentRequestId;

        OSRuleResult<OSBodyRoleSelectionResult> result = panel.ConfirmSelection(0);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(bodyBalance.TechnicalSegmentLimit));
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(panel.CurrentRequestId, Is.EqualTo(requestId));
        Assert.That(sessionController.SelectionQueue.CurrentRequest.RequestId, Is.EqualTo(requestId));
    }

    [Test]
    public void ConfirmSelection_RejectsInvalidOption()
    {
        sessionController.StartSession();
        panel.Open(sessionController.SelectionQueue.CurrentRequest);

        OSRuleResult<OSBodyRoleSelectionResult> result = panel.ConfirmSelection(4);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(0));
        Assert.That(panel.IsVisible, Is.True);
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

    private Button CreateButton(string name, out Text label)
    {
        GameObject buttonObject = new GameObject(name);
        buttonObject.transform.SetParent(panelRoot.transform);
        Button button = buttonObject.AddComponent<Button>();
        GameObject textObject = new GameObject($"{name}Label");
        textObject.transform.SetParent(buttonObject.transform);
        label = textObject.AddComponent<Text>();
        return button;
    }
}
#endif

