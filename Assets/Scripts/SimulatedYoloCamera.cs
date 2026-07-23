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

    [Header("Sensor randomization")]
    [Tooltip("Randomize angle noise, detection dropout and measurement latency per training episode.")]
    public bool randomizeSensorEffects = true;
    [Tooltip("Range for normalized angle noise standard deviation.")]
    public Vector2 angleNoiseStdDevRange = new Vector2(0f, 0.03f);
    [Tooltip("Range for the probability of dropping an otherwise valid detection.")]
    public Vector2 detectionDropoutRange = new Vector2(0f, 0.1f);
    [Tooltip("Range for camera latency measured in agent camera observations.")]
    public Vector2Int latencyMeasurementsRange = new Vector2Int(0, 2);
    [Tooltip("Suppress simulated detections while the target moves too quickly across the camera view.")]
    public bool simulateMotionBlurDropout = true;
    [Tooltip("Per-episode threshold range for target angular speed relative to the camera, in radians per second.")]
    public Vector2 motionBlurAngularSpeedThresholdRange = new Vector2(0.6f, 0.85f);
    [Tooltip("Keep detections suppressed for this long after target angular speed falls below the threshold.")]
    [Min(0f)] public float motionBlurRecoverySeconds = 0.2f;

    [Header("Current sensor effects")]
    [SerializeField, Min(0f)] private float angleNoiseStdDev;
    [SerializeField, Range(0f, 1f)] private float detectionDropoutProbability;
    [SerializeField, Min(0)] private int latencyMeasurements;
    [SerializeField, Min(0f)] private float motionBlurAngularSpeedThreshold = 0.725f;
    [SerializeField, Min(0f)] private float targetAngularSpeedRadiansPerSecond;
    [SerializeField, Min(0f)] private float motionBlurRecoveryRemaining;

    private bool baseParametersCaptured;
    private float baseHorizontalFOV;
    private Quaternion baseLocalRotation;
    private Vector3 previousTargetDirectionInCameraSpace;
    private bool hasPreviousTargetDirection;
    private ulong measurementVersion;
    private Transform sensorOwnerRoot;
    private readonly Queue<(float angle, float areaRatio, float aspectRatio, bool visible)>
        delayedMeasurements = new Queue<(float, float, float, bool)>();

    /// <summary>
    /// Changes whenever this source produces a new camera measurement. RobotBrain
    /// uses it to apply camera shaping only once per measurement.
    /// </summary>
    public virtual ulong MeasurementVersion => measurementVersion;
    public float TargetAngularSpeedRadiansPerSecond => targetAngularSpeedRadiansPerSecond;
    public float MotionBlurAngularSpeedThreshold => motionBlurAngularSpeedThreshold;
    public bool MotionBlurDropoutActive =>
        simulateMotionBlurDropout &&
        (targetAngularSpeedRadiansPerSecond > motionBlurAngularSpeedThreshold ||
         motionBlurRecoveryRemaining > 0f);

    [Header("References")]
    public Transform targetBall;
    public LayerMask obstacleLayer;

    private void Awake()
    {
        // До CaptureBaseParameters() (вызывается позже, из RobotBrain.OnEpisodeBegin) —
        // иначе за "базовое" значение для рандомизации/сброса возьмётся не то, что
        // задано в конфиге, а старое значение из инспектора.
        TrainingConfig.ApplyOverrides(this, "SimulatedYoloCamera");

        RobotBrain owner = GetComponentInParent<RobotBrain>();
        sensorOwnerRoot = owner != null ? owner.transform : transform;
        ResetMotionBlurState();
    }

    private void OnEnable()
    {
        ResetMotionBlurState();
    }

    private void FixedUpdate()
    {
        if (targetBall == null)
        {
            hasPreviousTargetDirection = false;
            targetAngularSpeedRadiansPerSecond = 0f;
            return;
        }

        Vector3 directionToTarget = targetBall.position - transform.position;
        if (directionToTarget.sqrMagnitude <= Mathf.Epsilon)
        {
            hasPreviousTargetDirection = false;
            targetAngularSpeedRadiansPerSecond = 0f;
            return;
        }

        Vector3 currentTargetDirectionInCameraSpace =
            transform.InverseTransformDirection(directionToTarget.normalized);
        if (!hasPreviousTargetDirection)
        {
            previousTargetDirectionInCameraSpace = currentTargetDirectionInCameraSpace;
            hasPreviousTargetDirection = true;
            targetAngularSpeedRadiansPerSecond = 0f;
            return;
        }

        float deltaTime = Mathf.Max(Time.fixedDeltaTime, Mathf.Epsilon);
        targetAngularSpeedRadiansPerSecond =
            Vector3.Angle(
                previousTargetDirectionInCameraSpace,
                currentTargetDirectionInCameraSpace) *
            Mathf.Deg2Rad /
            deltaTime;
        previousTargetDirectionInCameraSpace = currentTargetDirectionInCameraSpace;

        if (!simulateMotionBlurDropout)
        {
            motionBlurRecoveryRemaining = 0f;
            return;
        }

        if (targetAngularSpeedRadiansPerSecond > motionBlurAngularSpeedThreshold)
        {
            motionBlurRecoveryRemaining = Mathf.Max(0f, motionBlurRecoverySeconds);
        }
        else
        {
            motionBlurRecoveryRemaining = Mathf.Max(
                0f,
                motionBlurRecoveryRemaining - deltaTime);
        }
    }

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

        if (randomizeHorizontalFOV)
        {
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
        else
        {
            horizontalFOV = baseHorizontalFOV;
            transform.localRotation = baseLocalRotation;
        }

        if (randomizeSensorEffects)
        {
            angleNoiseStdDev = SampleRange(angleNoiseStdDevRange, 0f, float.PositiveInfinity);
            detectionDropoutProbability = SampleRange(detectionDropoutRange, 0f, 1f);
            motionBlurAngularSpeedThreshold = SampleRange(
                motionBlurAngularSpeedThresholdRange,
                0f,
                float.PositiveInfinity);

            int minimumLatency = Mathf.Max(0, Mathf.Min(
                latencyMeasurementsRange.x,
                latencyMeasurementsRange.y));
            int maximumLatency = Mathf.Max(minimumLatency, Mathf.Max(
                latencyMeasurementsRange.x,
                latencyMeasurementsRange.y));
            latencyMeasurements = UnityEngine.Random.Range(minimumLatency, maximumLatency + 1);
        }
        else
        {
            angleNoiseStdDev = 0f;
            detectionDropoutProbability = 0f;
            latencyMeasurements = 0;
            motionBlurAngularSpeedThreshold = MidpointOfRange(
                motionBlurAngularSpeedThresholdRange,
                0f,
                float.PositiveInfinity);
        }

        delayedMeasurements.Clear();
        ResetMotionBlurState();
    }

    public virtual void ResetDomainParameters()
    {
        CaptureBaseParameters();
        horizontalFOV = baseHorizontalFOV;
        transform.localRotation = baseLocalRotation;
        angleNoiseStdDev = 0f;
        detectionDropoutProbability = 0f;
        latencyMeasurements = 0;
        motionBlurAngularSpeedThreshold = MidpointOfRange(
            motionBlurAngularSpeedThresholdRange,
            0f,
            float.PositiveInfinity);
        delayedMeasurements.Clear();
        ResetMotionBlurState();
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
        measurementVersion++;
        ResolveSceneTarget();

        if (targetBall == null ||
            maxVisibleDistance <= 0f ||
            horizontalFOV <= 0f ||
            cameraAspectRatio <= 0f)
        {
            return ApplySensorEffects((0f, 0f, 0f, false));
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
            return ApplySensorEffects((0f, 0f, 0f, false));
        }

        var rawMeasurement = TryProjectBounds(bounds, out float angle, out float areaRatio, out float aspectRatio)
            ? (angle, areaRatio, aspectRatio, true)
            : (0f, 0f, 0f, false);
        return ApplySensorEffects(rawMeasurement);
    }

    private (float angle, float areaRatio, float aspectRatio, bool visible) ApplySensorEffects(
        (float angle, float areaRatio, float aspectRatio, bool visible) measurement)
    {
        if (measurement.visible)
        {
            if (detectionDropoutProbability > 0f &&
                UnityEngine.Random.value < detectionDropoutProbability)
            {
                measurement = (0f, 0f, 0f, false);
            }
            else if (angleNoiseStdDev > 0f)
            {
                measurement.angle = Mathf.Clamp(
                    measurement.angle + SampleStandardNormal() * angleNoiseStdDev,
                    -1f,
                    1f);
            }
        }

        delayedMeasurements.Enqueue(measurement);
        if (delayedMeasurements.Count <= latencyMeasurements)
            return (0f, 0f, 0f, false);

        var delayedMeasurement = delayedMeasurements.Dequeue();
        return MotionBlurDropoutActive
            ? (0f, 0f, 0f, false)
            : delayedMeasurement;
    }

    private static float SampleRange(Vector2 range, float lowerBound, float upperBound)
    {
        float minimum = Mathf.Clamp(Mathf.Min(range.x, range.y), lowerBound, upperBound);
        float maximum = Mathf.Clamp(Mathf.Max(range.x, range.y), minimum, upperBound);
        return UnityEngine.Random.Range(minimum, maximum);
    }

    private static float MidpointOfRange(Vector2 range, float lowerBound, float upperBound)
    {
        float minimum = Mathf.Clamp(Mathf.Min(range.x, range.y), lowerBound, upperBound);
        float maximum = Mathf.Clamp(Mathf.Max(range.x, range.y), minimum, upperBound);
        return (minimum + maximum) * 0.5f;
    }

    private void ResetMotionBlurState()
    {
        previousTargetDirectionInCameraSpace = Vector3.zero;
        hasPreviousTargetDirection = false;
        targetAngularSpeedRadiansPerSecond = 0f;
        motionBlurRecoveryRemaining = 0f;
    }

    private static float SampleStandardNormal()
    {
        float uniformA = Mathf.Max(UnityEngine.Random.value, 0.000001f);
        float uniformB = UnityEngine.Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(uniformA)) * Mathf.Cos(2f * Mathf.PI * uniformB);
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
            // ArenaSpawner parents the robot, walls and generated boxes under the
            // same Arena_N root. Comparing transform.root therefore classified every
            // arena obstacle as part of the robot and made occluded balls visible.
            // Ignore only colliders that actually belong to this robot hierarchy.
            if (hit.transform == sensorOwnerRoot || hit.transform.IsChildOf(sensorOwnerRoot))
                continue;
            if (hit.transform == targetBall || hit.transform.IsChildOf(targetBall))
                return true;
            return false;
        }

        return true;
    }
}
