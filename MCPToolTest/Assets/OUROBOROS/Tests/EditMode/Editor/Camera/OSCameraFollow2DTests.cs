#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSCameraFollow2DTests
{
    private GameObject cameraHost;
    private GameObject targetHost;
    private OSCameraFollow2D follow;

    [SetUp]
    public void SetUp()
    {
        cameraHost = new GameObject("Camera");
        targetHost = new GameObject("Target");
        follow = cameraHost.AddComponent<OSCameraFollow2D>();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(cameraHost);
        Object.DestroyImmediate(targetHost);
    }

    [Test]
    public void SnapToTarget_KeepsCameraDepthAndAppliesOffset()
    {
        cameraHost.transform.position = new Vector3(0f, 0f, -10f);
        targetHost.transform.position = new Vector3(4f, -2f, 3f);
        follow.ConfigureForTests(targetHost.transform, new Vector2(1f, 2f), 0f);

        OSRuleResult<Vector3> result = follow.SnapToTarget();

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(cameraHost.transform.position, Is.EqualTo(new Vector3(5f, 0f, -10f)));
    }

    [Test]
    public void Follow_WithPositiveSmoothingMovesTowardTarget()
    {
        cameraHost.transform.position = new Vector3(0f, 0f, -10f);
        targetHost.transform.position = new Vector3(10f, 0f, 0f);
        follow.ConfigureForTests(targetHost.transform, Vector2.zero, 8f);

        OSRuleResult<Vector3> result = follow.Follow(0.1f);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(cameraHost.transform.position.x, Is.GreaterThan(0f));
        Assert.That(cameraHost.transform.position.x, Is.LessThan(10f));
        Assert.That(cameraHost.transform.position.z, Is.EqualTo(-10f));
    }

    [Test]
    public void Follow_RejectsMissingTarget()
    {
        follow.ConfigureForTests(null, Vector2.zero);

        OSRuleResult<Vector3> result = follow.Follow(0.1f);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.ConfigurationError));
    }
}
#endif
