using System;
using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera simulation")]
    public float maxVisibleDistance = 30f;
    public float horizontalFOV = 40f;

    [Header("References")]
    public Transform targetBall;
    public LayerMask obstacleLayer;

    public (float angle, float distance, bool visible) GetTargetInfo()
    {
        if (targetBall == null || maxVisibleDistance <= 0f || horizontalFOV <= 0f)
            return (0f, 1f, false);

        Vector3 toTarget = targetBall.position - transform.position;
        float distance = toTarget.magnitude;
        if (distance <= Mathf.Epsilon)
            return (0f, 0f, true);

        Vector3 up = transform.up;
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, up).normalized;
        Vector3 flatTarget = Vector3.ProjectOnPlane(toTarget, up).normalized;
        float angleDegrees = Vector3.SignedAngle(flatForward, flatTarget, up);
        float halfFov = horizontalFOV * 0.5f;

        bool inRange = distance <= maxVisibleDistance;
        bool inFov = Mathf.Abs(angleDegrees) <= halfFov;
        bool hasLineOfSight = inRange && inFov && HasLineOfSight(toTarget / distance, distance);

        float normalizedAngle = Mathf.Clamp(angleDegrees / halfFov, -1f, 1f);
        float normalizedDistance = Mathf.Clamp01(distance / maxVisibleDistance);
        return (normalizedAngle, normalizedDistance, hasLineOfSight);
    }

    private bool HasLineOfSight(Vector3 direction, float distance)
    {
        RaycastHit[] hits = Physics.RaycastAll(
            transform.position,
            direction,
            distance,
            obstacleLayer,
            QueryTriggerInteraction.Ignore);

        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.transform.root == transform.root)
                continue;
            if (hit.transform == targetBall || hit.transform.IsChildOf(targetBall))
                return true;
            return false;
        }

        return true;
    }
}
