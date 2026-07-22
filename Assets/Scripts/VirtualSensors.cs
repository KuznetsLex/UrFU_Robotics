using UnityEngine;

public class VirtualSensors : MonoBehaviour
{
    public Transform leftIRPoint;
    public Transform rightIRPoint;
    [Tooltip("Legacy reference kept for existing scenes. The third IR channel now uses Gripper IR Point.")]
    public Transform centerIRPoint;
    public Transform gripperIRPoint;
    public Transform ultrasonicPoint;

    [Tooltip("Дальность левого и правого ИК-датчиков: 2.4 = 24 см при масштабе сцены 1 unit = 1 дм.")]
    [Min(0f)] public float irDistance = 2.4f;
    [Tooltip("Дальность центрального ИК-датчика, перенесённого на захват.")]
    [Min(0f)] public float gripperDistance = 0.7f;
    [Tooltip("Максимальная дальность УЗ в Unity units. При выдаче наблюдения переводится в метры через EnvironmentManager.globalScale.")]
    public float ultrasonicMaxDistance = 50f;
    public int ultrasonicRayCount = 10;
    public float ultrasonicAngle = 45f;
    public LayerMask obstacleLayer;
    [Tooltip("Слои, которые обнаруживает ИК-датчик на захвате. По умолчанию — TargetBall.")]
    public LayerMask gripperDetectionMask;

    private RobotBrain robotBrain;

    private void Awake()
    {
        TrainingConfig.ApplyOverrides(this, "VirtualSensors");
        if (obstacleLayer == 0)
            obstacleLayer = LayerMask.GetMask("Obstacle");
        if (gripperDetectionMask == 0)
            gripperDetectionMask = LayerMask.GetMask("TargetBall");
        robotBrain = GetComponent<RobotBrain>();
    }

    // === Методы для получения показаний датчиков ===

    public float GetLeftIR() => CastRay(leftIRPoint, irDistance) ? 1f : 0f;
    public float GetRightIR() => CastRay(rightIRPoint, irDistance) ? 1f : 0f;
    public float GetCenterIR() => GetGripperIR();
    public float GetGripperIR() => CastRay(gripperIRPoint, gripperDistance, gripperDetectionMask) ? 1f : 0f;

    /// <summary>
    /// Возвращает расстояние до ближайшего препятствия в физических метрах
    /// (без нормализации для policy).
    /// Используется для наблюдений агента.
    /// </summary>
    public float GetUltrasonicDistance()
    {
        return GetUltrasonicMinDistanceMeters();
    }

    private float GetUltrasonicMinDistanceMeters()
    {
        float worldUnitsPerMeter = GetWorldUnitsPerMeter();
        if (ultrasonicPoint == null) return ultrasonicMaxDistance / worldUnitsPerMeter;
        Vector3 origin = ultrasonicPoint.position;
        Vector3 forward = ultrasonicPoint.forward;
        Vector3 up = ultrasonicPoint.up;
        float halfAngle = ultrasonicAngle * 0.5f;
        int count = ultrasonicRayCount;
        float minDist = ultrasonicMaxDistance;
        for (int i = 0; i < count; i++)
        {
            float t = (count > 1) ? (2f * i / (count - 1) - 1f) : 0f;
            float angle = halfAngle * t;
            Quaternion rot = Quaternion.AngleAxis(angle, up);
            Vector3 dir = rot * forward;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, ultrasonicMaxDistance, obstacleLayer))
            {
                if (hit.distance < minDist)
                    minDist = hit.distance;
            }
        }
        return minDist / worldUnitsPerMeter;
    }

    private float GetWorldUnitsPerMeter()
    {
        EnvironmentManager environment = robotBrain != null
            ? robotBrain.environmentManager
            : null;
        return environment != null
            ? Mathf.Max(Mathf.Epsilon, environment.globalScale)
            : 1f;
    }

    public bool TryReadSimulationSensors(
        out float ultrasonicMeters,
        out bool leftIrTriggered,
        out bool rightIrTriggered,
        out bool gripperMountedIrTriggered)
    {
        bool hasSensorPoints = ultrasonicPoint != null ||
            leftIRPoint != null ||
            rightIRPoint != null ||
            gripperIRPoint != null;

        ultrasonicMeters = GetUltrasonicMinDistanceMeters();
        leftIrTriggered = CastRay(leftIRPoint, irDistance);
        gripperMountedIrTriggered = CastRay(gripperIRPoint, gripperDistance, gripperDetectionMask);
        rightIrTriggered = CastRay(rightIRPoint, irDistance);
        return hasSensorPoints;
    }

    private bool CastRay(Transform point, float maxDistance)
    {
        return CastRay(point, maxDistance, obstacleLayer);
    }

    private static bool CastRay(Transform point, float maxDistance, LayerMask layerMask)
    {
        if (point == null) return false;
        return Physics.Raycast(point.position, point.forward, maxDistance, layerMask);
    }

    // === Визуализация в редакторе (Gizmos) ===

    private void OnDrawGizmos()
    {
        DrawGizmosRay(leftIRPoint, irDistance);
        DrawGizmosRay(rightIRPoint, irDistance);
        DrawGizmosRay(gripperIRPoint, gripperDistance, gripperDetectionMask);
        DrawGizmosUltrasonic(ultrasonicPoint, ultrasonicMaxDistance);
    }

    private void DrawGizmosRay(Transform point, float maxDistance)
    {
        DrawGizmosRay(point, maxDistance, obstacleLayer);
    }

    private static void DrawGizmosRay(Transform point, float maxDistance, LayerMask layerMask)
    {
        if (point == null) return;
        Vector3 origin = point.position;
        Vector3 direction = point.forward;
        bool isHit = Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, layerMask);
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
        float halfAngle = ultrasonicAngle * 0.5f;
        int count = ultrasonicRayCount;
        for (int i = 0; i < count; i++)
        {
            float t = (count > 1) ? (2f * i / (count - 1) - 1f) : 0f;
            float angle = halfAngle * t;
            Quaternion rot = Quaternion.AngleAxis(angle, up);
            Vector3 dir = rot * forward;
            bool isHit = Physics.Raycast(origin, dir, out RaycastHit hit, maxDistance, obstacleLayer);
            Gizmos.color = isHit ? Color.red : Color.white;
            Gizmos.DrawRay(origin, dir * maxDistance);
            if (isHit)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(hit.point, 0.01f);
            }
        }
    }

    // === Визуализация во время игры (для отладки) ===
    // Пропускается при Is Training = true на RobotBrain — эти лучи только для
    // визуальной отладки, а лишние Physics.Raycast и Debug.DrawRay каждый кадр
    // на десятках арен заметно грузят обучение.

    private void Update()
    {
        if (robotBrain != null && robotBrain.isTraining)
            return;

        DrawDebugRay(leftIRPoint, irDistance);
        DrawDebugRay(rightIRPoint, irDistance);
        DrawDebugRay(gripperIRPoint, gripperDistance, gripperDetectionMask);
        DrawDebugUltrasonic(ultrasonicPoint, ultrasonicMaxDistance);
    }

    private void DrawDebugRay(Transform point, float maxDistance)
    {
        DrawDebugRay(point, maxDistance, obstacleLayer);
    }

    private static void DrawDebugRay(Transform point, float maxDistance, LayerMask layerMask)
    {
        if (point == null) return;
        bool isHit = Physics.Raycast(point.position, point.forward, out RaycastHit hit, maxDistance, layerMask);
        Color rayColor = isHit ? Color.red : Color.white;
        Debug.DrawRay(point.position, point.forward * maxDistance, rayColor);
    }

    private void DrawDebugUltrasonic(Transform point, float maxDistance)
    {
        if (point == null) return;
        Vector3 origin = point.position;
        Vector3 forward = point.forward;
        Vector3 up = point.up;
        float halfAngle = ultrasonicAngle * 0.5f;
        int count = ultrasonicRayCount;
        for (int i = 0; i < count; i++)
        {
            float t = (count > 1) ? (2f * i / (count - 1) - 1f) : 0f;
            float angle = halfAngle * t;
            Quaternion rot = Quaternion.AngleAxis(angle, up);
            Vector3 dir = rot * forward;
            bool isHit = Physics.Raycast(origin, dir, out RaycastHit hit, maxDistance, obstacleLayer);
            Color rayColor = isHit ? Color.red : Color.white;
            Debug.DrawRay(origin, dir * maxDistance, rayColor);
        }
    }
}
