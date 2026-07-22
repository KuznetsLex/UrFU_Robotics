using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrackController : MonoBehaviour
{
    [Header("Drive Settings")]
    public float maxLinearCmd = 0.25f;       // ограничение газа
    public float turnK = 0.25f;              // коэффициент поворота (дифференциальный привод)
    public float emaSteer = 0.40f;           // сглаживание угловой команды (0..1)

    [Header("Physics")]
    public float maxMotorForce = 100f;       // максимальная сила на одну гусеницу (Н)
    public float frictionCoeff = 0.8f;       // сцепление с поверхностью
    public float dragCoeff = 0.5f;           // сопротивление движению
    public float angularDragCoeff = 1.0f;    // сопротивление вращению
    public float maxAngularSpeed = 180f;     // максимальная угловая скорость (градусы/с)

    [Header("Track Points (local positions)")]
    public Vector3 leftTrackOffset = new Vector3(-1.5f, 0f, 0f);
    public Vector3 rightTrackOffset = new Vector3(1.5f, 0f, 0f);

    [Header("Randomization (для обучения)")]
    public bool randomizeFriction = false;
    public float frictionNoiseRange = 0.2f;

    private Rigidbody rb;
    private float currentGas;
    private float currentSteer;
    private float filteredSteer = 0f;
    private float currentFrictionCoeff;
    private float logTimer = 0f;
    private float maxAngularRad; // кешированное значение в радианах

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogError("TrackController: Rigidbody missing!");
        ApplyFrictionRandomization();
        maxAngularRad = maxAngularSpeed * Mathf.Deg2Rad;
    }

    private void Start()
    {
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.centerOfMass = new Vector3(0, -0.2f, 0);
        // Если нужно запретить наклон – раскомментируйте:
        // rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    public void Move(float gas, float steer)
    {
        currentGas = Mathf.Clamp(gas, -1f, 1f);
        currentSteer = Mathf.Clamp(steer, -1f, 1f);
    }

    public void Stop()
    {
        currentGas = 0f;
        currentSteer = 0f;
    }

    public void RandomizeFriction()
    {
        if (randomizeFriction)
            currentFrictionCoeff = frictionCoeff * (1f + Random.Range(-frictionNoiseRange, frictionNoiseRange));
        else
            currentFrictionCoeff = frictionCoeff;
        currentFrictionCoeff = Mathf.Clamp(currentFrictionCoeff, 0.05f, 1.5f);
    }

    private void ApplyFrictionRandomization()
    {
        RandomizeFriction();
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        // 1. Ограничение газа
        float lin_x = Mathf.Clamp(currentGas, -maxLinearCmd, maxLinearCmd);

        // 2. EMA-фильтр для поворота
        filteredSteer = emaSteer * currentSteer + (1f - emaSteer) * filteredSteer;
        float ang_z = filteredSteer;

        // 3. Дифференциальный привод – команды для гусениц
        float cmdLeft = lin_x + ang_z * turnK;
        float cmdRight = lin_x - ang_z * turnK;

        // 4. Желаемые силы для каждой гусеницы
        float desiredForceLeft = cmdLeft * maxMotorForce;
        float desiredForceRight = cmdRight * maxMotorForce;

        // 5. Ограничение по трению (каждая гусеница независимо)
        float mass = rb.mass;
        float normalForce = mass * Physics.gravity.magnitude;
        float maxFrictionForce = normalForce * currentFrictionCoeff;

        float actualForceLeft = Mathf.Clamp(desiredForceLeft, -maxFrictionForce, maxFrictionForce);
        float actualForceRight = Mathf.Clamp(desiredForceRight, -maxFrictionForce, maxFrictionForce);

        // 6. Вычисляем мировые точки приложения сил
        Vector3 leftWorld = transform.TransformPoint(leftTrackOffset);
        Vector3 rightWorld = transform.TransformPoint(rightTrackOffset);

        // 7. Прикладываем силы к точкам гусениц
        rb.AddForceAtPosition(transform.forward * actualForceLeft, leftWorld);
        rb.AddForceAtPosition(transform.forward * actualForceRight, rightWorld);

        // 8. Сопротивление движению (прикладывается к центру масс)
        rb.AddForce(-rb.linearVelocity * dragCoeff);
        rb.AddTorque(-rb.angularVelocity * angularDragCoeff);

        // 9. Дополнительное торможение при отпущенном газе
        if (Mathf.Approximately(currentGas, 0f))
        {
            rb.AddForce(-rb.linearVelocity * (dragCoeff * 1.5f));
        }

        // ===== ОГРАНИЧЕНИЕ МАКСИМАЛЬНОЙ УГЛОВОЙ СКОРОСТИ =====
        Vector3 angularVel = rb.angularVelocity;
        angularVel.y = Mathf.Clamp(angularVel.y, -maxAngularRad, maxAngularRad);
        rb.angularVelocity = angularVel;

        // 10. Логирование скоростей (каждые 0.5 секунды)
        logTimer += Time.fixedDeltaTime;
        if (logTimer >= 0.5f)
        {
            logTimer = 0f;
            float linearSpeed = rb.linearVelocity.magnitude;
            float angularSpeed = rb.angularVelocity.magnitude * Mathf.Rad2Deg;
            Debug.Log($"Linear speed: {linearSpeed:F2} m/s, Angular speed: {angularSpeed:F2} deg/s");
        }
    }
}