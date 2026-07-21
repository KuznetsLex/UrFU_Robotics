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

    [Header("Track Points (local positions)")]
    public Vector3 leftTrackOffset = new Vector3(-1.5f, 0f, 0f);   // смещение левой гусеницы от центра
    public Vector3 rightTrackOffset = new Vector3(1.5f, 0f, 0f);   // смещение правой гусеницы от центра

    [Header("Randomization (для обучения)")]
    public bool randomizeFriction = false;
    public float frictionNoiseRange = 0.2f;

    private Rigidbody rb;
    private float currentGas;
    private float currentSteer;
    private float filteredSteer = 0f;
    private float currentFrictionCoeff;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogError("TrackController: Rigidbody missing!");
        ApplyFrictionRandomization();
    }

    private void Start()
    {
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        // Центр масс оставляем по умолчанию, но можно сместить для устойчивости
        rb.centerOfMass = new Vector3(0, -0.2f, 0);
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
        float cmdLeft  = lin_x + ang_z * turnK;
        float cmdRight = lin_x - ang_z * turnK;
        
        // 4. Желаемые силы для каждой гусеницы (без ограничения по трению, пока)
        float desiredForceLeft = cmdLeft * maxMotorForce;
        float desiredForceRight = cmdRight * maxMotorForce;

        // 5. Ограничение по трению (каждая гусеница независимо)
        float mass = rb.mass;
        float normalForce = mass * Physics.gravity.magnitude;
        float maxFrictionForce = normalForce * currentFrictionCoeff;

        float actualForceLeft = Mathf.Clamp(desiredForceLeft, -maxFrictionForce, maxFrictionForce);
        float actualForceRight = Mathf.Clamp(desiredForceRight, -maxFrictionForce, maxFrictionForce);
        Debug.Log($"ForceLeft={actualForceLeft}, ForceRight={actualForceRight}");

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
        
        // Отладка (опционально)
        Debug.DrawRay(leftWorld, transform.forward * (actualForceLeft / 10f), Color.cyan);
        Debug.DrawRay(rightWorld, transform.forward * (actualForceRight / 10f), Color.magenta);
        Debug.DrawRay(rb.position, rb.linearVelocity.normalized * 1f, Color.green);
    }
}