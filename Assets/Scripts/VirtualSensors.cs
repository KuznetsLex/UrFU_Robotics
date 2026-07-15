using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class VirtualSensors : Agent
{
    // === Точки датчиков ===
    public Transform leftIRPoint;
    public Transform rightIRPoint;
    public Transform centerIRPoint;
    public Transform gripperIRPoint;
    public Transform ultrasonicPoint;

    // === Дальность ===
    public float irDistance = 0.15f;
    public float gripperDistance = 0.07f;
    [Header("Ультразвук")]
    public float ultrasonicDistance = 50.0f;          // дальность веера
    public int ultrasonicRayCount = 10;               // количество лучей
    public float ultrasonicAngle = 45f;               // полный угол веера (градусы)

    // === Слой препятствий ===
    public LayerMask obstacleLayer;

    private void Awake()
    {
        // Если слой не задан в инспекторе, используем Default
        if (obstacleLayer == 0)
            obstacleLayer = LayerMask.GetMask("Default");
    }

    // === Сбор наблюдений ===
    public override void CollectObservations(VectorSensor sensor)
    {
        // ИК-датчики (бинарные)
        sensor.AddObservation(CastRay(leftIRPoint, irDistance) ? 1f : 0f);
        sensor.AddObservation(CastRay(rightIRPoint, irDistance) ? 1f : 0f);
        sensor.AddObservation(CastRay(centerIRPoint, irDistance) ? 1f : 0f);
        sensor.AddObservation(CastRay(gripperIRPoint, gripperDistance) ? 1f : 0f);

        // Ультразвук (минимальное расстояние из веера)
        float ultrasonicMin = GetUltrasonicDistance(ultrasonicPoint, ultrasonicDistance);
        sensor.AddObservation(ultrasonicMin);
    }

    // === Вспомогательные методы ===
    private bool CastRay(Transform point, float maxDistance)
    {
        if (point == null) return false;
        return Physics.Raycast(point.position, point.forward, maxDistance, obstacleLayer);
    }

    // Ультразвуковой веер: возвращает минимальное расстояние среди всех лучей
    private float GetUltrasonicDistance(Transform point, float maxDistance)
    {
        if (point == null) return maxDistance;

        Vector3 origin = point.position;
        Vector3 forward = point.forward;
        Vector3 up = point.up;

        float halfAngle = ultrasonicAngle * 0.5f;
        int count = ultrasonicRayCount;
        float minDist = maxDistance;

        for (int i = 0; i < count; i++)
        {
            float t = (count > 1) ? (2f * i / (count - 1) - 1f) : 0f;
            float angle = halfAngle * t;
            Quaternion rotation = Quaternion.AngleAxis(angle, up);
            Vector3 direction = rotation * forward;

            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, maxDistance, obstacleLayer))
            {
                if (hit.distance < minDist)
                    minDist = hit.distance;
            }
        }

        // Вывод в консоль (для отладки)
        Debug.Log($"Ultrasonic min distance: {minDist:F2} m");
        return minDist;
    }


    // === Визуализация Gizmos (всегда видно в редакторе) ===
    private void OnDrawGizmos()
    {
        DrawGizmosRay(leftIRPoint, irDistance);
        DrawGizmosRay(rightIRPoint, irDistance);
        DrawGizmosRay(centerIRPoint, irDistance);
        DrawGizmosRay(gripperIRPoint, gripperDistance);
        DrawGizmosUltrasonic(ultrasonicPoint, ultrasonicDistance);
    }

    private void DrawGizmosRay(Transform point, float maxDistance)
    {
        if (point == null) return;

        Vector3 origin = point.position;
        Vector3 direction = point.forward;

        RaycastHit hit;
        bool isHit = Physics.Raycast(origin, direction, out hit, maxDistance, obstacleLayer);

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
            Quaternion rotation = Quaternion.AngleAxis(angle, up);
            Vector3 direction = rotation * forward;

            RaycastHit hit;
            bool isHit = Physics.Raycast(origin, direction, out hit, maxDistance, obstacleLayer);

            Gizmos.color = isHit ? Color.red : Color.white;
            Gizmos.DrawRay(origin, direction * maxDistance);

            if (isHit)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(hit.point, 0.01f);
            }
        }
    }

    // === Визуализация во время игры (опционально) ===
    void Update()
    {
        DrawDebugRay(leftIRPoint, irDistance);
        DrawDebugRay(rightIRPoint, irDistance);
        DrawDebugRay(centerIRPoint, irDistance);
        DrawDebugRay(gripperIRPoint, gripperDistance);
        DrawDebugUltrasonic(ultrasonicPoint, ultrasonicDistance);
        float testDist = GetUltrasonicDistance(ultrasonicPoint, ultrasonicDistance);
        Debug.Log($"Test ultrasonic: {testDist}");
    }

    private void DrawDebugRay(Transform point, float maxDistance)
    {
        if (point == null) return;

        RaycastHit hit;
        bool isHit = Physics.Raycast(point.position, point.forward, out hit, maxDistance, obstacleLayer);
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
            Quaternion rotation = Quaternion.AngleAxis(angle, up);
            Vector3 direction = rotation * forward;

            RaycastHit hit;
            bool isHit = Physics.Raycast(origin, direction, out hit, maxDistance, obstacleLayer);
            Color rayColor = isHit ? Color.red : Color.white;
            Debug.DrawRay(origin, direction * maxDistance, rayColor);
        }
    }

    // === Обязательные методы ===
    public override void OnEpisodeBegin() { }
    public override void OnActionReceived(ActionBuffers actionBuffers) { }
}
