using System;
using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera simulation")]
    public float maxVisibleDistance = 30f;
    public float horizontalFOV = 40f;
    [Min(0.1f)] public float cameraAspectRatio = 4f / 3f;

    [Header("References")]
    public Transform targetBall;
    public LayerMask obstacleLayer;

    public virtual (float angle, float areaRatio, float aspectRatio, bool visible) GetTargetInfo()
    {
        if (targetBall == null ||
            maxVisibleDistance <= 0f ||
            horizontalFOV <= 0f ||
            cameraAspectRatio <= 0f)
        {
            return (0f, 0f, 0f, false);
        }

        Vector3 toTarget = targetBall.position - transform.position;
        float distance = toTarget.magnitude;

        Vector3 up = transform.up;
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, up).normalized;
        Vector3 flatTarget = Vector3.ProjectOnPlane(toTarget, up).normalized;
        float angleDegrees = Vector3.SignedAngle(flatForward, flatTarget, up);
        float halfFov = horizontalFOV * 0.5f;

        bool inRange = distance <= maxVisibleDistance;
        bool inFov = Mathf.Abs(angleDegrees) <= halfFov;
        bool hasLineOfSight = inRange && inFov && HasLineOfSight(toTarget / distance, distance);

        if (!hasLineOfSight || !TryGetTargetBounds(out Bounds bounds))
        {
            return (0f, 0f, 0f, false);
        }

        return TryProjectBounds(bounds, out float angle, out float areaRatio, out float aspectRatio)
            ? (angle, areaRatio, aspectRatio, true)
            : (0f, 0f, 0f, false);
    }

    private bool TryGetTargetBounds(out Bounds bounds)
    {
        Renderer[] renderers = targetBall.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
            {
                bounds.Encapsulate(renderers[index].bounds);
            }
            return true;
        }

        Collider targetCollider = targetBall.GetComponentInChildren<Collider>();
        if (targetCollider != null)
        {
            bounds = targetCollider.bounds;
            return true;
        }

        bounds = default;
        return false;
    }

    private bool TryProjectBounds(
        Bounds bounds,
        out float normalizedAngle,
        out float areaRatio,
        out float aspectRatio)
    {
        float horizontalTangent = Mathf.Tan(horizontalFOV * 0.5f * Mathf.Deg2Rad);
        float verticalTangent = horizontalTangent / cameraAspectRatio;
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        bool hasProjectedCorner = false;

        for (int xSign = -1; xSign <= 1; xSign += 2)
        {
            for (int ySign = -1; ySign <= 1; ySign += 2)
            {
                for (int zSign = -1; zSign <= 1; zSign += 2)
                {
                    Vector3 worldCorner = bounds.center + Vector3.Scale(
                        bounds.extents,
                        new Vector3(xSign, ySign, zSign));
                    Vector3 localCorner = transform.InverseTransformPoint(worldCorner);
                    if (localCorner.z <= 0.001f)
                    {
                        continue;
                    }

                    float viewportX = 0.5f + localCorner.x / (2f * localCorner.z * horizontalTangent);
                    float viewportY = 0.5f + localCorner.y / (2f * localCorner.z * verticalTangent);
                    minX = Mathf.Min(minX, viewportX);
                    maxX = Mathf.Max(maxX, viewportX);
                    minY = Mathf.Min(minY, viewportY);
                    maxY = Mathf.Max(maxY, viewportY);
                    hasProjectedCorner = true;
                }
            }
        }

        if (!hasProjectedCorner)
        {
            normalizedAngle = 0f;
            areaRatio = 0f;
            aspectRatio = 0f;
            return false;
        }

        minX = Mathf.Clamp01(minX);
        maxX = Mathf.Clamp01(maxX);
        minY = Mathf.Clamp01(minY);
        maxY = Mathf.Clamp01(maxY);
        float boxWidth = Mathf.Max(0f, maxX - minX);
        float boxHeight = Mathf.Max(0f, maxY - minY);

        normalizedAngle = Mathf.Clamp((minX + maxX) - 1f, -1f, 1f);
        areaRatio = Mathf.Clamp01(boxWidth * boxHeight);
        aspectRatio = boxHeight > 0.0001f ? boxWidth / boxHeight : 0f;
        return boxWidth > 0f && boxHeight > 0f;
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
