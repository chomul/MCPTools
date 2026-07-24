#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class OSLevelUpPanelTests
{
    private GameObject sessionHost;
    private GameObject inputHost;
    private GameObject panelHost;
    private GameObject panelRoot;
    private OSGameSessionController sessionController;
    private OSInputRouter inputRouter;
    private OSLevelUpPanel panel;
    private InputActionAsset inputAsset;
    private InputActionMap playerMap;
    private InputActionMap uiMap;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private Button firstButton;
    private Button secondButton;
    private Button thirdButton;
    private Text firstLabel;
    private Text secondLabel;
    private Text thirdLabel;

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

        panelHost = new GameObject("LevelUpPanel");
        panel = panelHost.AddComponent<OSLevelUpPanel>();
        panelRoot = new GameObject("PanelRoot");
        panelRoot.transform.SetParent(panelHost.transform);
        firstButton = CreateButton("First", out firstLabel);
        secondButton = CreateButton("Second", out secondLabel);
        thirdButton = CreateButton("Third", out thirdLabel);

        panel.ConfigureForTests(
            panelRoot,
            upgradeCatalog,
            sessionController,
            inputRouter,
            firstButton,
            secondButton,
            thirdButton,
            firstLabel,
            secondLabel,
            thirdLabel);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(panelHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(inputHost);
        Object.DestroyImmediate(inputAsset);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void Open_DisplaysThreeUpgradeCardsWithNextLevels()
    {
        OpenLevelUpSelection("head_damage_boost", "body_fragment_discount", "max_hp_boost");

        OSRuleResult<OSSelectionRequest> openResult = panel.Open(sessionController.SelectionQueue.CurrentRequest);

        Assert.That(openResult.IsAccepted, Is.True);
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(firstButton.gameObject.activeSelf, Is.True);
        Assert.That(secondButton.gameObject.activeSelf, Is.True);
        Assert.That(thirdButton.gameObject.activeSelf, Is.True);
        Assert.That(firstButton.interactable, Is.True);
        Assert.That(secondButton.interactable, Is.True);
        Assert.That(thirdButton.interactable, Is.True);
        Assert.That(firstLabel.text, Does.Contain("머리 공격력 증가"));
        Assert.That(firstLabel.text, Does.Contain("화력 1/3"));
        Assert.That(secondLabel.text, Does.Contain("몸통 성장 비용 감소"));
        Assert.That(secondLabel.text, Does.Contain("몸통 1/2"));
        Assert.That(thirdLabel.text, Does.Contain("최대 체력 증가"));
        Assert.That(thirdLabel.text, Does.Contain("생존 1/2"));
    }

    [Test]
    public void LevelUpSelection_UsesUiMapWhileOpen()
    {
        OpenLevelUpSelection("head_damage_boost", "body_fragment_discount", "max_hp_boost");

        panel.SyncToSessionState();

        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(inputRouter.IsPlayerMapActive, Is.False);
        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.True);
    }

    [Test]
    public void ConfirmSelection_AppliesUpgradeOnceAndReturnsCombat()
    {
        OpenLevelUpSelection("head_damage_boost", "body_fragment_discount", "max_hp_boost");
        panel.Open(sessionController.SelectionQueue.CurrentRequest);
        string requestId = panel.CurrentRequestId;

        OSRuleResult<OSLevelUpSelectionResult> result = panel.ConfirmSelection(0);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.RequestId, Is.EqualTo(requestId));
        Assert.That(result.Payload.UpgradeId, Is.EqualTo("head_damage_boost"));
        Assert.That(result.Payload.PreviousLevel, Is.EqualTo(0));
        Assert.That(result.Payload.AppliedLevel, Is.EqualTo(1));
        Assert.That(sessionController.RuntimeState.GetUpgradeLevel("head_damage_boost"), Is.EqualTo(1));
        Assert.That(sessionController.RuntimeState.UpgradesApplied, Is.EqualTo(1));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(panel.IsVisible, Is.False);

        OSRuleResult<OSLevelUpSelectionResult> duplicate = panel.ConfirmSelection(0);

        Assert.That(duplicate.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(sessionController.RuntimeState.GetUpgradeLevel("head_damage_boost"), Is.EqualTo(1));
        Assert.That(sessionController.RuntimeState.UpgradesApplied, Is.EqualTo(1));
    }

    [Test]
    public void Open_RejectsMaxLevelCandidateBeforeApply()
    {
        StartCombatSession();
        Assert.That(sessionController.RuntimeState.ApplyUpgrade("max_hp_boost").IsAccepted, Is.True);
        Assert.That(sessionController.RuntimeState.ApplyUpgrade("max_hp_boost").IsAccepted, Is.True);
        Assert.That(sessionController.EnqueueLevelUpSelection(
            "head_damage_boost",
            "body_fragment_discount",
            "max_hp_boost").IsAccepted, Is.True);
        Assert.That(sessionController.ProcessFixedUpdate().IsAccepted, Is.True);

        OSRuleResult<OSSelectionRequest> openResult = panel.Open(sessionController.SelectionQueue.CurrentRequest);

        Assert.That(openResult.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(openResult.ReasonKey, Is.EqualTo("level_up_candidate_max_level"));
        Assert.That(panel.IsVisible, Is.False);
        Assert.That(sessionController.RuntimeState.GetUpgradeLevel("max_hp_boost"), Is.EqualTo(2));
    }

    [Test]
    public void RequestLevelUpSelectionFromCatalog_UsesEarlyFamilyCorrection()
    {
        StartCombatSession();

        OSRuleResult<OSSelectionRequest> requestResult = panel.RequestLevelUpSelectionFromCatalog();

        Assert.That(requestResult.IsAccepted, Is.True);
        Assert.That(requestResult.Payload.FirstUpgradeId, Is.EqualTo("head_damage_boost"));
        Assert.That(requestResult.Payload.SecondUpgradeId, Is.EqualTo("body_fragment_discount"));
        Assert.That(requestResult.Payload.ThirdUpgradeId, Is.EqualTo("max_hp_boost"));
    }

    [Test]
    public void ConfirmSelection_RejectsInvalidOption()
    {
        OpenLevelUpSelection("head_damage_boost", "body_fragment_discount", "max_hp_boost");
        panel.Open(sessionController.SelectionQueue.CurrentRequest);

        OSRuleResult<OSLevelUpSelectionResult> result = panel.ConfirmSelection(3);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(sessionController.RuntimeState.UpgradesApplied, Is.EqualTo(0));
        Assert.That(panel.IsVisible, Is.True);
    }

    [Test]
    public void DeadState_ClosesPanelAndPreventsApply()
    {
        OpenLevelUpSelection("head_damage_boost", "body_fragment_discount", "max_hp_boost");
        panel.Open(sessionController.SelectionQueue.CurrentRequest);

        sessionController.RequestDeath("test_dead");
        panel.SyncToSessionState();
        OSRuleResult<OSLevelUpSelectionResult> result = panel.ConfirmSelection(0);

        Assert.That(panel.IsVisible, Is.False);
        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(sessionController.RuntimeState.UpgradesApplied, Is.EqualTo(0));
    }

    private void OpenLevelUpSelection(string first, string second, string third)
    {
        StartCombatSession();
        Assert.That(sessionController.EnqueueLevelUpSelection(first, second, third).IsAccepted, Is.True);
        Assert.That(sessionController.ProcessFixedUpdate().IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
    }

    private void StartCombatSession()
    {
        Assert.That(sessionController.StartSession().IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(sessionController.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(sessionController.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
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
