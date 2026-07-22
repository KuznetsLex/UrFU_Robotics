using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Controls the shared camera/ultrasonic pan pivot. The policy provides an
/// absolute normalized target, while the simulated servo approaches it at a
/// finite rate to match the physical actuator.
/// </summary>
public class CameraRotator : MonoBehaviour
{
    public const float CenterAngle = 90f;
    public const float PanHalfRange = 70f;
    public const float MinAngle = CenterAngle - PanHalfRange;
    public const float MaxAngle = CenterAngle + PanHalfRange;

    [Header("Camera pan servo")]
    [Tooltip("Absolute servo target in degrees. The safe physical range is 20..160 degrees.")]
    [FormerlySerializedAs("angle")]
    [SerializeField, Range(MinAngle, MaxAngle)]
    private float targetAngle = CenterAngle;

    [Tooltip("Simulated actuator speed. 140 deg/s gives a one-second full safe sweep; calibrate against the real servo.")]
    [SerializeField, Min(1f)]
    private float maxSpeedDegreesPerSecond = 140f;

    [Tooltip("Ignore smaller target changes so simulation matches ROS command deadband.")]
    [SerializeField, Min(0f)]
    private float commandDeadbandDegrees = 0.5f;

    private float currentAngle = CenterAngle;

    public float CurrentAngle => currentAngle;
    public float TargetAngle => targetAngle;
    public float CurrentNormalized => NormalizeAngle(currentAngle);
    public float TargetNormalized => NormalizeAngle(targetAngle);

    private void Awake()
    {
        targetAngle = ClampAngle(targetAngle);
        currentAngle = targetAngle;
        ApplyRotation();
    }

    private void FixedUpdate()
    {
        float nextAngle = Mathf.MoveTowards(
            currentAngle,
            targetAngle,
            maxSpeedDegreesPerSecond * Time.fixedDeltaTime);

        if (Mathf.Approximately(nextAngle, currentAngle))
            return;

        currentAngle = nextAngle;
        ApplyRotation();
    }

    private void OnValidate()
    {
        targetAngle = ClampAngle(targetAngle);
        maxSpeedDegreesPerSecond = Mathf.Max(1f, maxSpeedDegreesPerSecond);
        commandDeadbandDegrees = Mathf.Max(0f, commandDeadbandDegrees);

        if (!Application.isPlaying)
        {
            currentAngle = targetAngle;
            ApplyRotation();
        }
    }

    public void SetNormalizedTarget(float normalizedTarget)
    {
        SetTargetAngle(DenormalizeAngle(normalizedTarget));
    }

    public void SetTargetAngle(float requestedAngle)
    {
        float safeTarget = ClampAngle(requestedAngle);
        if (Mathf.Abs(safeTarget - targetAngle) < commandDeadbandDegrees)
            return;

        targetAngle = safeTarget;
    }

    /// <summary>
    /// Used for domain randomization — real servos vary in max slew rate between
    /// units/batches, so this is randomized per episode rather than fixed.
    /// </summary>
    public void SetServoSpeed(float degreesPerSecond)
    {
        maxSpeedDegreesPerSecond = Mathf.Max(1f, degreesPerSecond);
    }

    public void ResetPan(float normalizedAngle)
    {
        float safeAngle = DenormalizeAngle(normalizedAngle);
        targetAngle = safeAngle;
        currentAngle = safeAngle;
        ApplyRotation();
    }

    public static float NormalizeAngle(float angleDegrees)
    {
        return Mathf.Clamp((ClampAngle(angleDegrees) - CenterAngle) / PanHalfRange, -1f, 1f);
    }

    public static float DenormalizeAngle(float normalizedAngle)
    {
        return CenterAngle + Mathf.Clamp(normalizedAngle, -1f, 1f) * PanHalfRange;
    }

    public static float ClampAngle(float angleDegrees)
    {
        return Mathf.Clamp(angleDegrees, MinAngle, MaxAngle);
    }

    private void ApplyRotation()
    {
        transform.localRotation = Quaternion.Euler(0f, currentAngle, 0f);
    }
}
