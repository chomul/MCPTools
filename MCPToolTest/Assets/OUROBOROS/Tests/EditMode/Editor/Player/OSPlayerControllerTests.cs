#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public sealed class OSPlayerControllerTests
{
    private GameObject host;
    private Rigidbody2D body;
    private CircleCollider2D bodyCollider;
    private OSPlayerController controller;
    private OSPlayerBalanceData playerBalance;

    [SetUp]
    public void SetUp()
    {
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        host = new GameObject("Player Head");
        body = host.AddComponent<Rigidbody2D>();
        bodyCollider = host.AddComponent<CircleCollider2D>();
        controller = host.AddComponent<OSPlayerController>();
        controller.ConfigureForTests(body, bodyCollider, playerBalance);
        controller.SetSimulationEnabled(true);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(host);
        Object.DestroyImmediate(playerBalance);
    }

    [Test]
    public void SetMoveInput_ClampsMagnitudeToOne()
    {
        OSRuleResult<Vector2> result = controller.SetMoveInput(new Vector2(2f, 2f));

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(controller.MoveInput.magnitude, Is.LessThanOrEqualTo(1.0001f));
        Assert.That(controller.MoveInput.x, Is.EqualTo(controller.MoveInput.y).Within(0.0001f));
    }

    [Test]
    public void DiagonalMovement_HasSameSpeedAsStraightMovement()
    {
        controller.SetMoveInput(Vector2.right);
        OSRuleResult<Vector2> straight = controller.AdvanceMovement(1f);

        body.position = Vector2.zero;
        controller.SetMoveInput(new Vector2(1f, 1f));
        OSRuleResult<Vector2> diagonal = controller.AdvanceMovement(1f);

        Assert.That(straight.IsAccepted, Is.True);
        Assert.That(diagonal.IsAccepted, Is.True);
        Assert.That(diagonal.Payload.magnitude, Is.EqualTo(straight.Payload.magnitude).Within(0.0001f));
    }

    [Test]
    public void ZeroVector_DoesNotOverwriteLastValidDirection()
    {
        controller.SetMoveInput(Vector2.up);
        controller.AdvanceMovement(0.1f);

        controller.SetMoveInput(Vector2.zero);
        OSRuleResult<Vector2> result = controller.AdvanceMovement(0.1f);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload, Is.EqualTo(Vector2.zero));
        Assert.That(controller.LastValidDirection, Is.EqualTo(Vector2.up));
    }

    [Test]
    public void InvalidInput_IsRejectedAndClearsMoveVector()
    {
        OSRuleResult<Vector2> result = controller.SetMoveInput(new Vector2(float.NaN, 1f));

        Assert.That(result.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(controller.MoveInput, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void SelectionState_BlocksMovementAndKeepsLastDirection()
    {
        OSGameSessionController session = CreateSessionController();
        session.StartSession();
        controller.ConfigureForTests(body, bodyCollider, playerBalance, null, session);
        controller.SetSimulationEnabled(true);

        controller.SetMoveInput(Vector2.up);
        OSRuleResult<Vector2> result = controller.AdvanceMovement(1f);

        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(body.position, Is.EqualTo(Vector2.zero));
        Assert.That(controller.LastValidDirection, Is.EqualTo(Vector2.right));

        Object.DestroyImmediate(session.gameObject);
    }

    [Test]
    public void CombatState_AllowsMovementAndRaisesConfirmedPosition()
    {
        OSGameSessionController session = CreateSessionController();
        session.StartSession();
        session.CompleteCurrentSelection(0);
        session.CompleteCurrentSelection(1);
        controller.ConfigureForTests(body, bodyCollider, playerBalance, null, session);
        controller.SetSimulationEnabled(true);
        List<Vector2> positions = new List<Vector2>();
        controller.PositionAdvanced += (position, direction) => positions.Add(position);

        controller.SetMoveInput(Vector2.right);
        OSRuleResult<Vector2> result = controller.AdvanceMovement(0.5f);

        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.Combat));
        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload, Is.EqualTo(Vector2.right * playerBalance.MoveSpeed * 0.5f));
        Assert.That(positions, Has.Count.EqualTo(1));
        Assert.That(positions[0], Is.EqualTo(Vector2.right * playerBalance.MoveSpeed * 0.5f));

        Object.DestroyImmediate(session.gameObject);
    }

    [Test]
    public void SourceDoesNotUseLegacyInputApi()
    {
        string source = File.ReadAllText("Assets/OUROBOROS/Runtime/Player/OSPlayerController.cs");

        Assert.That(source, Does.Not.Contain("UnityEngine.Input."));
        Assert.That(source, Does.Not.Contain("GetKey"));
        Assert.That(source, Does.Not.Contain("GetAxis"));
        Assert.That(source, Does.Not.Contain("GetButton"));
    }

    private static OSGameSessionController CreateSessionController()
    {
        GameObject sessionHost = new GameObject("GameSession");
        OSGameSessionController session = sessionHost.AddComponent<OSGameSessionController>();
        session.ConfigureForTests(
            ScriptableObject.CreateInstance<OSPlayerBalanceData>(),
            ScriptableObject.CreateInstance<OSBodyBalanceData>(),
            ScriptableObject.CreateInstance<OSEncounterBalanceData>(),
            ScriptableObject.CreateInstance<OSUpgradeCatalog>());

        return session;
    }
}
#endif
