using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrackController : MonoBehaviour
{
    [Header("Motion")]
    public float moveSpeed = 0.57f;
    public float turnSpeed = 120f;
    public float turnK = 0.30f;
    public float maxLinearCmd = 0.25f;

    [Header("Motor PWM simulation")]
    public float motorDeadzone = 10f;
    public float minMotorPwm = 35f;
    public float pwmScale = 200f;

    private Rigidbody rb;
    private float currentGas;
    private float currentSteer;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping = 8f;
        rb.angularDamping = 10f;
        rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    public void Move(float gas, float steer)
    {
        currentGas = Mathf.Clamp(gas, -maxLinearCmd, maxLinearCmd);
        currentSteer = Mathf.Clamp(steer, -1f, 1f);
    }

    public void Stop()
    {
        currentGas = 0f;
        currentSteer = 0f;
    }

    private float ProcessPwm(float rawCommand)
    {
        float pwm = rawCommand * pwmScale;
        float absPwm = Mathf.Abs(pwm);

        if (absPwm < motorDeadzone)
            return 0f;
        if (absPwm < minMotorPwm)
            return minMotorPwm * Mathf.Sign(pwm);

        return Mathf.Clamp(pwm, -100f, 100f);
    }

    private void FixedUpdate()
    {
        float leftCmd = currentGas + currentSteer * turnK;
        float rightCmd = currentGas - currentSteer * turnK;
        float effectiveLeft = ProcessPwm(leftCmd) / pwmScale;
        float effectiveRight = ProcessPwm(rightCmd) / pwmScale;

        float linearVelocity = (effectiveLeft + effectiveRight) * 0.5f;
        float angularVelocity = (effectiveLeft - effectiveRight) * 0.5f;

        Vector3 movement = transform.forward * linearVelocity * moveSpeed * Time.fixedDeltaTime;
        rb.MovePosition(rb.position + movement);

        float turn = angularVelocity * turnSpeed * Time.fixedDeltaTime;
        rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, turn, 0f));
    }
}
