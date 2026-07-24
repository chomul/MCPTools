#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class OSInputRouterTests
{
    private GameObject host;
    private OSInputRouter router;
    private InputActionAsset asset;
    private InputActionMap playerMap;
    private InputActionMap uiMap;
    private InputAction move;
    private InputAction explosion;
    private InputAction point;
    private InputAction click;
    private InputAction navigate;
    private InputAction submit;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("InputRouter");
        host.SetActive(false);
        router = host.AddComponent<OSInputRouter>();

        asset = ScriptableObject.CreateInstance<InputActionAsset>();
        playerMap = asset.AddActionMap("Player");
        uiMap = asset.AddActionMap("UI");

        move = playerMap.AddAction("Move", InputActionType.Value);
        explosion = playerMap.AddAction("Explosion", InputActionType.Button);
        point = uiMap.AddAction("Point", InputActionType.Value);
        click = uiMap.AddAction("Click", InputActionType.Button);
        navigate = uiMap.AddAction("Navigate", InputActionType.Value);
        submit = uiMap.AddAction("Submit", InputActionType.Button);

        router.ConfigureForTests(move, explosion, point, click, navigate, submit);
    }

    [TearDown]
    public void TearDown()
    {
        if (asset != null)
        {
            ScriptableObject.DestroyImmediate(asset);
        }

        if (host != null)
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    [Test]
    public void InspectorFieldsUseInputActionReferenceOnly()
    {
        AssertFieldType("moveAction");
        AssertFieldType("explosionAction");
        AssertFieldType("pointAction");
        AssertFieldType("clickAction");
        AssertFieldType("navigateAction");
        AssertFieldType("submitAction");
    }

    [Test]
    public void ValidateConfiguration_RequiresPlayerAndUiActionMaps()
    {
        OSRuleResult<int> result = router.ValidateConfiguration();

        Assert.That(result.IsAccepted, Is.True);
    }

    [Test]
    public void PlayerAndUiActionMaps_AreMutuallyExclusive()
    {
        GetLifecycleMethod("OnEnable").Invoke(router, null);

        Assert.That(playerMap.enabled, Is.True);
        Assert.That(uiMap.enabled, Is.False);
        Assert.That(router.IsPlayerMapActive, Is.True);

        router.ActivateUiMap();

        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.True);
        Assert.That(router.IsPlayerMapActive, Is.False);
        Assert.That(router.LatestMove, Is.EqualTo(Vector2.zero));

        router.ActivatePlayerMap();

        Assert.That(playerMap.enabled, Is.True);
        Assert.That(uiMap.enabled, Is.False);
        Assert.That(router.IsPlayerMapActive, Is.True);
    }

    [Test]
    public void RepeatedOnEnableAndOnDisable_DoNotLeaveDuplicateSubscriptionState()
    {
        MethodInfo onEnable = GetLifecycleMethod("OnEnable");
        MethodInfo onDisable = GetLifecycleMethod("OnDisable");

        onEnable.Invoke(router, null);
        onEnable.Invoke(router, null);

        Assert.That(router.IsSubscribed, Is.True);

        onDisable.Invoke(router, null);
        onDisable.Invoke(router, null);

        Assert.That(router.IsSubscribed, Is.False);
        Assert.That(playerMap.enabled, Is.False);
        Assert.That(uiMap.enabled, Is.False);
    }

    [Test]
    public void SelectingUiMapClearsQueuedPlayerMoveValue()
    {
        SetPrivateField("latestMove", new Vector2(1f, -1f));

        router.ActivateUiMap();

        Assert.That(router.LatestMove, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void SourceDoesNotUseLegacyInputApi()
    {
        string source = File.ReadAllText("Assets/OUROBOROS/Runtime/Input/OSInputRouter.cs");

        Assert.That(source, Does.Not.Contain("UnityEngine.Input."));
        Assert.That(source, Does.Not.Contain("GetKey"));
        Assert.That(source, Does.Not.Contain("GetAxis"));
        Assert.That(source, Does.Not.Contain("GetButton"));
    }

    private static void AssertFieldType(string fieldName)
    {
        FieldInfo field = typeof(OSInputRouter).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field: {fieldName}");
        Assert.That(field.FieldType, Is.EqualTo(typeof(InputActionReference)));
    }

    private static MethodInfo GetLifecycleMethod(string methodName)
    {
        MethodInfo method = typeof(OSInputRouter).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing lifecycle method: {methodName}");
        return method;
    }

    private void SetPrivateField(string fieldName, object value)
    {
        FieldInfo field = typeof(OSInputRouter).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field: {fieldName}");
        field.SetValue(router, value);
    }
}
#endif
