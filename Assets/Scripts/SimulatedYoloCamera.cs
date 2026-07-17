using System;
using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera simulation")]
    public float maxVisibleDistance = 30f;
    public float horizontalFOV = 40f;

    [Header("Object properties")]
    [Tooltip("Радиус мяча в метрах (используется для вычисления площади проекции)")]
    public float ballRadius = 0.005f;
    [Tooltip("Соотношение сторон изображения (ширина/высота)")]
    public float aspectRatio = 320f / 240f; // 1.333f

    [Header("References")]
    public Transform targetBall;
    public LayerMask obstacleLayer;

    /// <summary>
    /// Возвращает информацию о цели: нормализованный угол, долю площади изображения,
    /// занимаемую проекцией мяча, и флаг видимости.
    /// </summary>
    public (float angle, float area, bool visible) GetTargetInfo()
    {
        if (targetBall == null || maxVisibleDistance <= 0f || horizontalFOV <= 0f)
            return (-1f, 0f, false);

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
            return (-1f, 0f, false);

        // Нормализуем угол
        float normalizedAngle = Mathf.Clamp(angleDegrees / halfFov, -1f, 1f);

        // Вычисляем площадь проекции мяча как долю от всей площади изображения
        float fovRad = horizontalFOV * 0.5f * Mathf.Deg2Rad;
        float viewWidth = 2f * distance * Mathf.Tan(fovRad);
        float viewHeight = viewWidth / aspectRatio;
        float imageArea = viewWidth * viewHeight;          // площадь видимой области в мировых единицах
        float projectedArea = Mathf.PI * ballRadius * ballRadius;
        float areaFraction = projectedArea / imageArea;
        areaFraction = Mathf.Clamp01(areaFraction);        // обрезаем, чтобы не выходило за [0,1]

        return (normalizedAngle, areaFraction, true);
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
