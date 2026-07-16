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

    public float GetUltrasonicNormalized()
    {
        float dist = GetUltrasonicMinDistance();
        return Mathf.Clamp01(dist / ultrasonicMaxDistance);
    }

    private float GetUltrasonicMinDistance()
    {
        if (ultrasonicPoint == null) return ultrasonicMaxDistance;
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
        return minDist;
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
