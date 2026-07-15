using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;

[RequireComponent(typeof(TrackController), typeof(VirtualSensors), typeof(Rigidbody))]
public class RobotBrain : Agent
{
    [Header("Ссылки на компоненты")]
    public TrackController trackController;
    public VirtualSensors sensors;
    public SimulatedYoloCamera yoloCamera;
    public Transform gripperTransform;          // объект клешни (там висит GripperController)
    public Transform holdPoint;                 // точка удержания мяча (из GripperController)
    public Transform ball;                      // мяч (с тегом TargetBall)

    [Header("Параметры обучения")]
    public float wallPenalty = -0.01f;
    public float moveRewardScale = 0.01f;
    public float gripReward = 1f;
    public float outOfBoundsPenalty = -0.5f;

    private Rigidbody rb;
    private Vector3 startPos;
    private Quaternion startRot;
    private float lastDistanceToBall = 999f;
    private bool hasBall = false;
    private float timeSinceLastDetection = 0f;
    private float lastKnownAngle = 0f;
    private float lastKnownDistance = 1f;

    // Для штрафа за резкие действия
    private float prevGas = 0f;
    private float prevSteer = 0f;

    // ===== ИНИЦИАЛИЗАЦИЯ =====
    protected override void Awake()
    {
        base.Awake(); // обязательно вызываем базовый Awake для корректной работы ML-Agents
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
    }

    private void Start()
    {
        startPos = rb.position;
        startRot = rb.rotation;
        if (ball == null) ball = GameObject.FindWithTag("TargetBall")?.transform;
    }

    // ===== СБРОС ЭПИЗОДА =====
    public override void OnEpisodeBegin()
    {
        // Сброс робота на стартовую позицию
        rb.position = startPos;
        rb.rotation = startRot;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // Сброс клешни (если есть скрипт)
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null) gripper.Release();

        // Сброс состояния
        hasBall = false;
        lastDistanceToBall = 999f;
        timeSinceLastDetection = 0f;
        prevGas = 0f;
        prevSteer = 0f;
    }

    // ===== СБОР НАБЛЮДЕНИЙ =====
    public override void CollectObservations(VectorSensor sensor)
    {
        // 1. УЗ-дальномер (нормализованное расстояние)
        sensor.AddObservation(sensors.GetUltrasonicNormalized());

        // 2-4. ИК-датчики (бинарные)
        sensor.AddObservation(sensors.GetLeftIR());
        sensor.AddObservation(sensors.GetRightIR());
        sensor.AddObservation(sensors.GetGripperIR());

        // 5-6. Данные с камеры (если мяч виден, иначе – последние известные)
        var (angle, distance, visible) = yoloCamera.GetTargetInfo();
        if (visible)
        {
            sensor.AddObservation(angle);
            sensor.AddObservation(distance);
            lastKnownAngle = angle;
            lastKnownDistance = distance;
            timeSinceLastDetection = 0f;
        }
        else
        {
            sensor.AddObservation(lastKnownAngle);
            sensor.AddObservation(lastKnownDistance);
            timeSinceLastDetection += Time.fixedDeltaTime;
        }

        // 7. Флаг видимости мяча
        sensor.AddObservation(visible ? 1f : 0f);

        // 8. Статус захвата мяча
        sensor.AddObservation(hasBall ? 1f : 0f);

        // 9-10. Относительное смещение от старта (X, Z)
        Vector3 deltaPos = rb.position - startPos;
        sensor.AddObservation(deltaPos.x);
        sensor.AddObservation(deltaPos.z);

        // 11. Направление взгляда (Heading) – нормализованный угол
        float heading = rb.rotation.eulerAngles.y / 180f - 1f; // диапазон -1..1
        sensor.AddObservation(heading);

        // 12. Текущая линейная скорость (нормализованная, предполагаем макс. ~2 м/с)
        float speed = rb.linearVelocity.magnitude;
        sensor.AddObservation(Mathf.Clamp01(speed / 2f));

        // 13. Время с последней детекции (нормализованное до 10 секунд)
        sensor.AddObservation(Mathf.Clamp01(timeSinceLastDetection / 10f));
    }

    // ===== ПРИЁМ ДЕЙСТВИЙ =====
    public override void OnActionReceived(ActionBuffers actions)
    {
        if (trackController == null)
        {
            Debug.LogError("TrackController is null in RobotBrain!");
            return;
        }

        // Чтение непрерывных действий (2 штуки)
        float gas = actions.ContinuousActions[0];
        float steer = actions.ContinuousActions[1];

        // Дискретное действие для клешни (0 – ничего, 1 – закрыть, 2 – открыть)
        int gripCommand = actions.DiscreteActions[0];

        // --- Управление движением ---
        trackController.Move(gas, steer);

        // --- Штраф за резкие движения (плавность) ---
        float gasDelta = Mathf.Abs(gas - prevGas);
        float steerDelta = Mathf.Abs(steer - prevSteer);
        AddReward(-0.02f * (gasDelta + steerDelta));
        prevGas = gas;
        prevSteer = steer;

        // --- Управление клешней ---
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
        {
            switch (gripCommand)
            {
                case 1: gripper.Grab(); break;
                case 2: gripper.Release(); break;
                // case 0: ничего не делаем
            }
        }

        // --- Расчёт наград ---

        // 1. Награда за приближение к мячу (если мяч существует)
        if (ball != null)
        {
            float currentDist = Vector3.Distance(rb.position, ball.position);
            if (currentDist < lastDistanceToBall)
            {
                float reward = (lastDistanceToBall - currentDist) * moveRewardScale;
                // Усиливаем награду, если близко к мячу
                if (currentDist < 1.0f) reward *= 2f;
                AddReward(reward);
            }
            lastDistanceToBall = currentDist;
        }

        // 2. Бонус за центрирование (если мяч виден и робот смотрит на него)
        var (angle, _, visible) = yoloCamera.GetTargetInfo();
        if (visible)
        {
            float angleAbs = Mathf.Abs(angle);
            AddReward(Mathf.Max(0f, 1f - angleAbs) * 0.005f);
        }

        // 3. Штраф за близость к стенам (по УЗ и ИК)
        if (sensors.GetUltrasonicNormalized() < 0.2f)
            AddReward(wallPenalty * 0.5f);
        if (sensors.GetLeftIR() > 0.5f || sensors.GetRightIR() > 0.5f)
            AddReward(wallPenalty);

        // 4. Проверка захвата мяча
        if (gripper != null && gripper.IsGrabbing)
        {
            hasBall = true;
        }

        // --- Терминальные условия ---

        // Успех: мяч захвачен
        if (hasBall)
        {
            AddReward(gripReward);
            EndEpisode();
            return;
        }

        // Падение за пределы арены (если вышли за границы ±10)
        if (Mathf.Abs(rb.position.x) > 10f || Mathf.Abs(rb.position.z) > 10f)
        {
            AddReward(outOfBoundsPenalty);
            EndEpisode();
            return;
        }

        // Слишком долго без мяча (> 10 секунд)
        if (timeSinceLastDetection > 10f)
        {
            AddReward(-0.2f);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;

        // Газ: W = +1, S = -1
        float gas = 0f;
        if (keyboard.wKey.isPressed) gas = 1f;
        if (keyboard.sKey.isPressed) gas = -1f;
        // Если нажаты обе, газ = 0 (можно оставить так)

        // Руль: A = -1, D = +1
        float steer = 0f;
        if (keyboard.aKey.isPressed) steer = -1f;
        if (keyboard.dKey.isPressed) steer = 1f;

        continuousActions[0] = gas;
        continuousActions[1] = steer;

        // Клешня: пробел — захват, R — отпускание
        if (keyboard.spaceKey.wasPressedThisFrame)
            discreteActions[0] = 1;  // захват
        else if (keyboard.rKey.wasPressedThisFrame)
            discreteActions[0] = 2;  // отпустить
        else
            discreteActions[0] = 0;  // ничего
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.lKey.wasPressedThisFrame)
        {
            var (angle, distance, visible) = yoloCamera.GetTargetInfo();
            Debug.Log($"YOLO: visible={visible}, angle={angle:F2}, distance={distance:F2}");
        }
    }
}
