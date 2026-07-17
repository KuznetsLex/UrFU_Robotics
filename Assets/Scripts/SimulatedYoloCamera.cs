using System;
using System.Collections.Generic;
using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera simulation")]
    public float maxVisibleDistance = 30f;
    public float horizontalFOV = 40f;

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

    public (float angle, float distance, bool visible) GetTargetInfo()
    {
        if (targetBall == null || maxVisibleDistance <= 0f || horizontalFOV <= 0f)
            return (-1f, -1f, false);

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

        if (!hasLineOfSight)
            return (-1f, -1f, false);

        float normalizedAngle = Mathf.Clamp(angleDegrees / halfFov, -1f, 1f);
        float normalizedDistance = Mathf.Clamp01(distance / maxVisibleDistance);
        return (normalizedAngle, normalizedDistance, hasLineOfSight);
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
