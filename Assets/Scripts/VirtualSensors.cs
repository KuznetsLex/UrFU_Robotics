using UnityEngine;

public class VirtualSensors : MonoBehaviour
{
    public Transform leftIRPoint;
    public Transform rightIRPoint;
    public Transform centerIRPoint;
    public Transform gripperIRPoint;
    public Transform ultrasonicPoint;

    public float irDistance = 0.15f;
    public float gripperDistance = 0.07f;
    public float ultrasonicMaxDistance = 50f;
    public int ultrasonicRayCount = 10;
    public float ultrasonicAngle = 45f;
    public LayerMask obstacleLayer;
    [Header("Ultrasonic Cone Settings")]
    public int ultrasonicVerticalLayers = 6;        // количество слоёв (вееров)
    public float ultrasonicVerticalAngle = 0f;    // угол поворота вееров внутри конуса (градусы)

    private void Awake()
    {
        if (obstacleLayer == 0)
            obstacleLayer = LayerMask.GetMask("Default");
    }

    // === Методы для получения показаний датчиков ===

    public float GetLeftIR() => CastRay(leftIRPoint, irDistance) ? 1f : 0f;
    public float GetRightIR() => CastRay(rightIRPoint, irDistance) ? 1f : 0f;
    public float GetCenterIR() => CastRay(centerIRPoint, irDistance) ? 1f : 0f;
    public float GetGripperIR() => CastRay(gripperIRPoint, gripperDistance) ? 1f : 0f;

    /// <summary>
    /// Возвращает сырое расстояние до ближайшего препятствия в метрах (без нормализации).
    /// Используется для наблюдений агента.
    /// </summary>
    public float GetUltrasonicDistance()
    {
        return GetUltrasonicMinDistance();
    }

    private float GetUltrasonicMinDistance()
    {
        if (ultrasonicPoint == null) return ultrasonicMaxDistance;

        Vector3 origin = ultrasonicPoint.position;
        Vector3 forward = ultrasonicPoint.forward;
        Vector3 up = ultrasonicPoint.up;
        Vector3 right = ultrasonicPoint.right;

        int raysPerFan = ultrasonicRayCount;                // количество лучей в одном веере
        int fanRotations = ultrasonicVerticalLayers;        // количество положений веера при вращении
        float horizontalHalfAngle = ultrasonicAngle * 0.5f; // половинный горизонтальный угол веера
        float elevationAngle = ultrasonicVerticalAngle * 0.5f; // угол наклона веера вверх

        float minDist = ultrasonicMaxDistance;

        // Цикл по вращениям веера вокруг оси forward
        for (int r = 0; r < fanRotations; r++)
        {
            // Азимутальный угол (0..360) для текущего положения веера
            float azimuth = (r / (float)fanRotations) * 360f;
            Quaternion azimuthRot = Quaternion.AngleAxis(azimuth, forward);

            // Цикл по лучам внутри веера
            for (int i = 0; i < raysPerFan; i++)
            {
                // Горизонтальный угол в пределах веера (от -halfAngle до +halfAngle)
                float t = (raysPerFan > 1) ? (2f * i / (raysPerFan - 1) - 1f) : 0f;
                float hAngle = horizontalHalfAngle * t;

                // Строим направление:
                // 1. Сначала наклоняем веер вверх (вокруг оси right) на угол elevationAngle
                Quaternion tiltRot = Quaternion.AngleAxis(elevationAngle, right);
                Vector3 tiltedForward = tiltRot * forward;

                // 2. Затем разворачиваем горизонтально (вокруг оси up) на hAngle
                Quaternion fanRot = Quaternion.AngleAxis(hAngle, up);
                Vector3 fanDirection = fanRot * tiltedForward;

                // 3. Теперь поворачиваем весь веер вокруг оси forward на азимут
                Vector3 direction = azimuthRot * fanDirection;

                // Пускаем луч
                if (Physics.Raycast(origin, direction, out RaycastHit hit, ultrasonicMaxDistance, obstacleLayer))
                {
                    if (hit.distance < minDist)
                        minDist = hit.distance;
                }
            }
        }
        return minDist;
    }

    public bool TryReadSimulationSensors(
        out float ultrasonicMeters,
        out bool leftIrTriggered,
        out bool rightIrTriggered,
        out bool centerIrTriggered)
    {
        bool hasSensorPoints = ultrasonicPoint != null ||
            leftIRPoint != null ||
            rightIRPoint != null ||
            centerIRPoint != null;

        ultrasonicMeters = GetUltrasonicMinDistance();
        leftIrTriggered = CastRay(leftIRPoint, irDistance);
        centerIrTriggered = CastRay(centerIRPoint, irDistance);
        rightIrTriggered = CastRay(rightIRPoint, irDistance);
        return hasSensorPoints;
    }

    private bool CastRay(Transform point, float maxDistance)
    {
        if (point == null) return false;
        return Physics.Raycast(point.position, point.forward, maxDistance, obstacleLayer);
    }

    // === Визуализация в редакторе (Gizmos) ===

    private void OnDrawGizmos()
    {
        DrawGizmosRay(leftIRPoint, irDistance);
        DrawGizmosRay(rightIRPoint, irDistance);
        DrawGizmosRay(centerIRPoint, irDistance);
        DrawGizmosRay(gripperIRPoint, gripperDistance);
        DrawGizmosUltrasonic(ultrasonicPoint, ultrasonicMaxDistance);
    }

    private void DrawGizmosRay(Transform point, float maxDistance)
    {
        if (point == null) return;
        Vector3 origin = point.position;
        Vector3 direction = point.forward;
        bool isHit = Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, obstacleLayer);
        Gizmos.color = isHit ? Color.red : Color.white;
        Gizmos.DrawRay(origin, direction * maxDistance);
        if (isHit)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(hit.point, 0.01f);
        }
    }

    private void DrawGizmosUltrasonic(Transform point, float maxDistance)
    {
        if (point == null) return;

        Vector3 origin = point.position;
        Vector3 forward = point.forward;
        Vector3 up = point.up;
        Vector3 right = point.right;

        int raysPerFan = ultrasonicRayCount;
        int fanRotations = ultrasonicVerticalLayers;
        float horizontalHalfAngle = ultrasonicAngle * 0.5f;
        float elevationAngle = ultrasonicVerticalAngle * 0.5f;

        for (int r = 0; r < fanRotations; r++)
        {
            float azimuth = (r / (float)fanRotations) * 360f;
            Quaternion azimuthRot = Quaternion.AngleAxis(azimuth, forward);

            for (int i = 0; i < raysPerFan; i++)
            {
                float t = (raysPerFan > 1) ? (2f * i / (raysPerFan - 1) - 1f) : 0f;
                float hAngle = horizontalHalfAngle * t;

                Quaternion tiltRot = Quaternion.AngleAxis(elevationAngle, right);
                Vector3 tiltedForward = tiltRot * forward;
                Quaternion fanRot = Quaternion.AngleAxis(hAngle, up);
                Vector3 fanDirection = fanRot * tiltedForward;
                Vector3 direction = azimuthRot * fanDirection;

                bool isHit = Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, obstacleLayer);
                Gizmos.color = isHit ? Color.red : Color.white;
                Gizmos.DrawRay(origin, direction * maxDistance);
                if (isHit)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(hit.point, 0.01f);
                }
            }
        }
    }

    // === Визуализация во время игры (для отладки) ===

    private void Update()
    {
        DrawDebugRay(leftIRPoint, irDistance);
        DrawDebugRay(rightIRPoint, irDistance);
        DrawDebugRay(centerIRPoint, irDistance);
        DrawDebugRay(gripperIRPoint, gripperDistance);
        DrawDebugUltrasonic(ultrasonicPoint, ultrasonicMaxDistance);
    }

    private void DrawDebugRay(Transform point, float maxDistance)
    {
        if (point == null) return;
        bool isHit = Physics.Raycast(point.position, point.forward, out RaycastHit hit, maxDistance, obstacleLayer);
        Color rayColor = isHit ? Color.red : Color.white;
        Debug.DrawRay(point.position, point.forward * maxDistance, rayColor);
    }

    private void DrawDebugUltrasonic(Transform point, float maxDistance)
    {
        if (point == null) return;

        Vector3 origin = point.position;
        Vector3 forward = point.forward;
        Vector3 up = point.up;
        Vector3 right = point.right;

        int raysPerFan = ultrasonicRayCount;
        int fanRotations = ultrasonicVerticalLayers;
        float horizontalHalfAngle = ultrasonicAngle * 0.5f;
        float elevationAngle = ultrasonicVerticalAngle * 0.5f;

        for (int r = 0; r < fanRotations; r++)
        {
            float azimuth = (r / (float)fanRotations) * 360f;
            Quaternion azimuthRot = Quaternion.AngleAxis(azimuth, forward);

            for (int i = 0; i < raysPerFan; i++)
            {
                float t = (raysPerFan > 1) ? (2f * i / (raysPerFan - 1) - 1f) : 0f;
                float hAngle = horizontalHalfAngle * t;

                Quaternion tiltRot = Quaternion.AngleAxis(elevationAngle, right);
                Vector3 tiltedForward = tiltRot * forward;
                Quaternion fanRot = Quaternion.AngleAxis(hAngle, up);
                Vector3 fanDirection = fanRot * tiltedForward;
                Vector3 direction = azimuthRot * fanDirection;

                bool isHit = Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, obstacleLayer);
                Color rayColor = isHit ? Color.red : Color.white;
                Debug.DrawRay(origin, direction * maxDistance, rayColor);
            }
        }
    }
}
