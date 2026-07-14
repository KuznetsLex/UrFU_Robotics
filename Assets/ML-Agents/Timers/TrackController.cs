using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrackController : MonoBehaviour
{
    [Header("Калибровочные параметры физики")]
    public float moveSpeed = 0.57f;       // Базовая линейная скорость[cite: 4]
    public float turnSpeed = 120f;        // Базовая скорость поворота[cite: 4]
    public float turnK = 0.30f;           // Влияние руля на скорость гусениц[cite: 4]
    public float maxLinearCmd = 0.25f;    // Лимит поступательной скорости[cite: 4]

    [Header("Симуляция моторов (PWM)")]
    public float motorDeadzone = 10f;     // Порог мертвой зоны в %[cite: 4]
    public float minMotorPwm = 35f;       // Минимальный порог старта[cite: 4]
    public float pwmScale = 200f;         // Коэффициент перевода в ШИМ[cite: 4]

    private Rigidbody rb;
    private float currentGas = 0f;
    private float currentSteer = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        // Автоматическая настройка Rigidbody для повышения стабильности симуляции[cite: 4]
        rb.interpolation = RigidbodyInterpolation.Interpolate; // Сглаживание при движении[cite: 4]
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // Защита от прохождения сквозь стены[cite: 4]
        rb.linearDamping = 8f;  // Линейное гашение сил[cite: 4]
        rb.angularDamping = 10f; // Угловое гашение сил[cite: 4]
    }

    // Публичный метод, который будет вызывать RobotBrain.cs (ИИ-агент)
    public void Move(float gas, float steer)
    {
        // Ограничиваем базовый сигнал газа рамками maxLinearCmd[cite: 4]
        currentGas = Mathf.Clamp(gas, -maxLinearCmd, maxLinearCmd);
        currentSteer = Mathf.Clamp(steer, -1f, 1f);
    }

    // Программный ШИМ-контроллер
    private float ProcessPWM(float rawCommand)
    {
        // Перевод скорости в ШИМ-сигнал (0-100)
        float pwm = rawCommand * pwmScale;
        float absPwm = Mathf.Abs(pwm);

        // Отсечка мертвой зоны 
        if (absPwm < motorDeadzone)
        {
            return 0f;
        }

        // Обеспечение стартового тока (если сигнал прошел порог)
        if (absPwm < minMotorPwm)
        {
            return minMotorPwm * Mathf.Sign(pwm);
        }

        return pwm;
    }

    void FixedUpdate()
    {
        // 1. Смешивание скоростей бортов (дифференциальный привод)[cite: 4]
        float leftCmd = currentGas + (currentSteer * turnK);
        float rightCmd = currentGas - (currentSteer * turnK);

        // 2. Симуляция ШИМ-контроллера реальных моторов
        float leftPwm = ProcessPWM(leftCmd);
        float rightPwm = ProcessPWM(rightCmd);

        // 3. Обратный перевод эффективного сигнала PWM в физическую скорость[cite: 4]
        float effectiveLeft = leftPwm / pwmScale;
        float effectiveRight = rightPwm / pwmScale;

        // Расчет итоговых векторов движения базы
        float linearVelocity = (effectiveLeft + effectiveRight) / 2f;
        float angularVelocity = (effectiveLeft - effectiveRight) / 2f;

        // 4. Применение физических сил через Rigidbody[cite: 4]

        // Линейное перемещение
        Vector3 movement = transform.forward * linearVelocity * moveSpeed * Time.fixedDeltaTime;
        rb.MovePosition(rb.position + movement);

        // Вращение
        float turn = angularVelocity * turnSpeed * Time.fixedDeltaTime;
        Quaternion rotation = Quaternion.Euler(0f, turn, 0f);
        rb.MoveRotation(rb.rotation * rotation);
    }
}