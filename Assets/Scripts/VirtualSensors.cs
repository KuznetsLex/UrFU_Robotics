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
    public float ultrasonicMaxDistance = 50f;
    public int ultrasonicRayCount = 10;
    public float ultrasonicAngle = 45f;
    public LayerMask obstacleLayer;

    [Header("Domain Randomization (крепление датчиков)")]
    [Tooltip("Рандомизировать положение/угол точек датчиков каждый эпизод — имитирует разброс при сборке/креплении на реальном роботе")]
    public bool randomizeSensorMounting = true;
    [Tooltip("Максимальное случайное смещение точки датчика по каждой оси, единицы сцены (1 unit = 1 дм)")]
    [Min(0f)] public float sensorPositionJitter = 0.05f;
    [Tooltip("Максимальный случайный угол наклона точки датчика по каждой оси, градусы")]
    [Min(0f)] public float sensorAngleJitter = 5f;

    private RobotBrain robotBrain;

    private bool baseSensorTransformsCaptured;
    private Vector3 baseLeftIRLocalPos, baseRightIRLocalPos, baseGripperIRLocalPos, baseUltrasonicLocalPos;
    private Quaternion baseLeftIRLocalRot, baseRightIRLocalRot, baseGripperIRLocalRot, baseUltrasonicLocalRot;

    private void Awake()
    {
        TrainingConfig.ApplyOverrides(this, "VirtualSensors");
        if (obstacleLayer == 0)
            obstacleLayer = LayerMask.GetMask("Obstacle");
        robotBrain = GetComponent<RobotBrain>();
    }

    // === Доменная рандомизация крепления датчиков ===
    // Вызывается из RobotBrain.OnEpisodeBegin() тем же образом, что и
    // SimulatedYoloCamera.RandomizeDomainParameters()/ResetDomainParameters().

    private void CaptureBaseSensorTransforms()
    {
        if (baseSensorTransformsCaptured)
            return;

        if (leftIRPoint != null) { baseLeftIRLocalPos = leftIRPoint.localPosition; baseLeftIRLocalRot = leftIRPoint.localRotation; }
        if (rightIRPoint != null) { baseRightIRLocalPos = rightIRPoint.localPosition; baseRightIRLocalRot = rightIRPoint.localRotation; }
        if (gripperIRPoint != null) { baseGripperIRLocalPos = gripperIRPoint.localPosition; baseGripperIRLocalRot = gripperIRPoint.localRotation; }
        if (ultrasonicPoint != null) { baseUltrasonicLocalPos = ultrasonicPoint.localPosition; baseUltrasonicLocalRot = ultrasonicPoint.localRotation; }
        baseSensorTransformsCaptured = true;
    }

    public void RandomizeSensorMounting()
    {
        CaptureBaseSensorTransforms();

        if (!randomizeSensorMounting)
        {
            ResetSensorMounting();
            return;
        }

        JitterTransform(leftIRPoint, baseLeftIRLocalPos, baseLeftIRLocalRot);
        JitterTransform(rightIRPoint, baseRightIRLocalPos, baseRightIRLocalRot);
        JitterTransform(gripperIRPoint, baseGripperIRLocalPos, baseGripperIRLocalRot);
        JitterTransform(ultrasonicPoint, baseUltrasonicLocalPos, baseUltrasonicLocalRot);
    }

    public void ResetSensorMounting()
    {
        CaptureBaseSensorTransforms();

        if (leftIRPoint != null) leftIRPoint.SetLocalPositionAndRotation(baseLeftIRLocalPos, baseLeftIRLocalRot);
        if (rightIRPoint != null) rightIRPoint.SetLocalPositionAndRotation(baseRightIRLocalPos, baseRightIRLocalRot);
        if (gripperIRPoint != null) gripperIRPoint.SetLocalPositionAndRotation(baseGripperIRLocalPos, baseGripperIRLocalRot);
        if (ultrasonicPoint != null) ultrasonicPoint.SetLocalPositionAndRotation(baseUltrasonicLocalPos, baseUltrasonicLocalRot);
    }

    private void JitterTransform(Transform point, Vector3 basePos, Quaternion baseRot)
    {
        if (point == null)
            return;

        Vector3 posOffset = new Vector3(
            UnityEngine.Random.Range(-sensorPositionJitter, sensorPositionJitter),
            UnityEngine.Random.Range(-sensorPositionJitter, sensorPositionJitter),
            UnityEngine.Random.Range(-sensorPositionJitter, sensorPositionJitter));

        Vector3 angleOffset = new Vector3(
            UnityEngine.Random.Range(-sensorAngleJitter, sensorAngleJitter),
            UnityEngine.Random.Range(-sensorAngleJitter, sensorAngleJitter),
            UnityEngine.Random.Range(-sensorAngleJitter, sensorAngleJitter));

        point.SetLocalPositionAndRotation(basePos + posOffset, baseRot * Quaternion.Euler(angleOffset));
    }

    // === Методы для получения показаний датчиков ===

    public float GetLeftIR() => CastRay(leftIRPoint, irDistance) ? 1f : 0f;
    public float GetRightIR() => CastRay(rightIRPoint, irDistance) ? 1f : 0f;
    public float GetCenterIR() => GetGripperIR();
    public float GetGripperIR() => ReadGripperIR() ? 1f : 0f;

    // Гриппер-ИК должен реагировать не только на препятствия (obstacleLayer),
    // но и на сам мяч — у реального сенсора расстояния присутствие любого
    // объекта в зоне действия и есть срабатывание, без понятия "луча"/конуса.
    // Мяч намеренно не размечен как Obstacle (чтобы не путать его с
    // препятствиями арены в остальной наблюдательной/физической логике),
    // поэтому проверяем его отдельно — простой проверкой расстояния до точки
    // датчика.
    private bool ReadGripperIR()
    {
        if (CastRay(gripperIRPoint, gripperDistance))
            return true;

        Transform ball = robotBrain != null ? robotBrain.ball : null;
        return IsBallNearGripper(ball);
    }

    private bool IsBallNearGripper(Transform ball)
    {
        if (gripperIRPoint == null || ball == null)
            return false;

        return Vector3.Distance(gripperIRPoint.position, ball.position) <= gripperDistance;
    }

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

        ultrasonicMeters = GetUltrasonicMinDistance();
        leftIrTriggered = CastRay(leftIRPoint, irDistance);
        gripperMountedIrTriggered = ReadGripperIR();
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
    // Пропускается при Is Training = true на RobotBrain — эти лучи только для
    // визуальной отладки, а лишние Physics.Raycast и Debug.DrawRay каждый кадр
    // на десятках арен заметно грузят обучение.

    private void Update()
    {
        if (robotBrain != null && robotBrain.isTraining)
            return;

        DrawDebugRay(leftIRPoint, irDistance);
        DrawDebugRay(rightIRPoint, irDistance);
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
