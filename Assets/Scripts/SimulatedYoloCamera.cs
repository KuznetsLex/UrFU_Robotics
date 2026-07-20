using System;
using System.Collections.Generic;
using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera simulation")]
    public float maxVisibleDistance = 30f;
    [Tooltip("Current horizontal FOV; randomized per training episode when enabled.")]
    public float horizontalFOV = 40f;
    [Tooltip("Physical camera frame width / height. The robot stream is 320 / 240.")]
    [Min(0.1f)] public float cameraAspectRatio = 4f / 3f;

    [Header("Domain randomization")]
    [InspectorName("Randomize FOV And Vertical Tilt")]
    [Tooltip("Randomize horizontal FOV and vertical camera tilt together.")]
    public bool randomizeHorizontalFOV = true;
    [Tooltip("Horizontal FOV range sampled once at the start of each training episode.")]
    public Vector2 horizontalFOVRange = new Vector2(35f, 55f);
    [Tooltip("Vertical camera tilt range in degrees, sampled with the FOV.")]
    public Vector2 verticalTiltRange = new Vector2(-5f, 5f);

    private bool baseParametersCaptured;
    private float baseHorizontalFOV;
    private Quaternion baseLocalRotation;

    [Header("References")]
    public Transform targetBall;
    public LayerMask obstacleLayer;

    // Переиспользуемый буфер для RaycastNonAlloc — на RaycastAll здесь раньше
    // аллоцировался новый массив каждый вызов (каждое решение агента, на каждой
    // арене), это заметно нагружало GC при обучении с несколькими аренами.
    private const int MaxLineOfSightHits = 24;
    private readonly RaycastHit[] losHitsBuffer = new RaycastHit[MaxLineOfSightHits];
    private static readonly Comparison<RaycastHit> ByDistance =
        (left, right) => left.distance.CompareTo(right.distance);

    public virtual void RandomizeDomainParameters()
    {
        CaptureBaseParameters();

        if (!randomizeHorizontalFOV)
        {
            ResetDomainParameters();
            return;
        }

        float minimumFov = Mathf.Clamp(
            Mathf.Min(horizontalFOVRange.x, horizontalFOVRange.y),
            1f,
            179f);
        float maximumFov = Mathf.Clamp(
            Mathf.Max(horizontalFOVRange.x, horizontalFOVRange.y),
            minimumFov,
            179f);
        horizontalFOV = UnityEngine.Random.Range(minimumFov, maximumFov);

        float minimumTilt = Mathf.Clamp(
            Mathf.Min(verticalTiltRange.x, verticalTiltRange.y),
            -89f,
            89f);
        float maximumTilt = Mathf.Clamp(
            Mathf.Max(verticalTiltRange.x, verticalTiltRange.y),
            minimumTilt,
            89f);
        float verticalTilt = UnityEngine.Random.Range(minimumTilt, maximumTilt);
        transform.localRotation = baseLocalRotation * Quaternion.Euler(verticalTilt, 0f, 0f);
    }

    public virtual void ResetDomainParameters()
    {
        CaptureBaseParameters();
        horizontalFOV = baseHorizontalFOV;
        transform.localRotation = baseLocalRotation;
    }

    private void CaptureBaseParameters()
    {
        if (baseParametersCaptured)
        {
            return;
        }

        baseHorizontalFOV = horizontalFOV;
        baseLocalRotation = transform.localRotation;
        baseParametersCaptured = true;
    }

    public virtual (float angle, float areaRatio, float aspectRatio, bool visible) GetTargetInfo()
    {
        ResolveSceneTarget();

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

    private void ResolveSceneTarget()
    {
        if (targetBall != null &&
            targetBall.gameObject.scene.IsValid() &&
            targetBall.gameObject.scene.isLoaded)
        {
            return;
        }

        GameObject sceneTarget = GameObject.FindWithTag("TargetBall");
        targetBall = sceneTarget != null ? sceneTarget.transform : null;
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
        // boxWidth/boxHeight is measured in normalized viewport units. Multiplying
        // by frame width/height makes it identical to the real pixel bbox ratio.
        aspectRatio = boxHeight > 0.0001f
            ? boxWidth * cameraAspectRatio / boxHeight
            : 0f;
        return boxWidth > 0f && boxHeight > 0f;
    }

    private bool HasLineOfSight(Vector3 direction, float distance)
    {
        int hitCount = Physics.RaycastNonAlloc(
            transform.position,
            direction,
            losHitsBuffer,
            distance,
            obstacleLayer,
            QueryTriggerInteraction.Ignore);

        // Буфер переполнен (крайне маловероятно) — трактуем как "не видно",
        // это безопаснее, чем случайно решить, что мяч виден, пропустив препятствие.
        if (hitCount >= losHitsBuffer.Length)
            return false;

        if (hitCount > 1)
            Array.Sort(losHitsBuffer, 0, hitCount, Comparer<RaycastHit>.Create(ByDistance));

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = losHitsBuffer[i];
            if (hit.transform.root == transform.root)
                continue;
            if (hit.transform == targetBall || hit.transform.IsChildOf(targetBall))
                return true;
            return false;
        }

        return true;
    }
}
