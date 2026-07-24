using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSCameraFollow2D : MonoBehaviour
{
    [Header("Follow")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector2 offset;
    [SerializeField] private bool snapOnEnable = true;
    [SerializeField] private float followSmoothing = 12f;

    public Transform Target => target;
    public Vector2 Offset => offset;
    public float FollowSmoothing => followSmoothing;

    public void ConfigureForTests(Transform followTarget, Vector2 followOffset, float smoothing = 12f)
    {
        target = followTarget;
        offset = followOffset;
        followSmoothing = smoothing;
    }

    public OSRuleResult<Vector3> SnapToTarget()
    {
        if (target == null)
        {
            return OSRuleResult<Vector3>.Rejected(OSResultCode.ConfigurationError, "camera_follow_target_missing");
        }

        Vector3 nextPosition = CreateTargetPosition();
        transform.position = nextPosition;
        return OSRuleResult<Vector3>.Accept(nextPosition);
    }

    public OSRuleResult<Vector3> Follow(float deltaTime)
    {
        if (target == null)
        {
            return OSRuleResult<Vector3>.Rejected(OSResultCode.ConfigurationError, "camera_follow_target_missing");
        }

        if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
        {
            return OSRuleResult<Vector3>.Rejected(OSResultCode.ConfigurationError, "camera_follow_delta_invalid");
        }

        Vector3 targetPosition = CreateTargetPosition();
        if (followSmoothing <= 0f || deltaTime <= 0f)
        {
            transform.position = targetPosition;
            return OSRuleResult<Vector3>.Accept(targetPosition);
        }

        float t = 1f - Mathf.Exp(-followSmoothing * deltaTime);
        Vector3 nextPosition = Vector3.Lerp(transform.position, targetPosition, t);
        transform.position = nextPosition;
        return OSRuleResult<Vector3>.Accept(nextPosition);
    }

    private void OnEnable()
    {
        if (snapOnEnable)
        {
            SnapToTarget();
        }
    }

    private void LateUpdate()
    {
        Follow(Time.deltaTime);
    }

    private Vector3 CreateTargetPosition()
    {
        Vector3 current = transform.position;
        Vector3 followed = target.position;
        return new Vector3(followed.x + offset.x, followed.y + offset.y, current.z);
    }
}
