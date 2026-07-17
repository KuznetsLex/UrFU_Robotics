using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;
using System.Collections.Generic;

[RequireComponent(typeof(TrackController), typeof(VirtualSensors), typeof(Rigidbody))]
public class RobotBrain : Agent
{
    [Header("Component references")]
    public TrackController trackController;
    public VirtualSensors sensors;
    public SimulatedYoloCamera yoloCamera;
    public Transform gripperTransform;
    public Transform holdPoint;
    public Transform ball;

    [Header("Training")]
    public float wallPenalty = -0.01f;
    public float moveRewardScale = 0.01f;
    public float gripReward = 1f;
    public float outOfBoundsPenalty = -0.5f;
    public Vector2 arenaHalfExtents = new Vector2(30f, 30f);
    public float fallDistance = 3f;
    public float noDetectionTimeout = 10f;
    public float stepPenalty = -0.0002f;
    public float grabAttemptReward = 0.1f;        // бонус за попытку захвата рядом
    public float grabDistanceThreshold = 0.3f;    // дистанция до мяча для бонуса

    [Header("Spawn Randomization")]
    public bool randomizeSpawn = true;
    public bool randomizeBall = true;
    public float minSpawnDistance = 2f;
    public bool isTraining = true; // включайте в инспекторе для обучения

    [Header("Domain Randomization")]
    public bool randomizeMass = true;
    public bool randomizeMotorParams = true;
    public bool addUltrasonicNoise = true;
    public bool enableBurstDropout = true;
    public bool enableCommandLatency = true;
    [Header("Sensor Faults")]
    public bool enableIRFaults = true;
    public float irFaultProbability = 0.1f;

    public bool enableUltrasonicFaults = true;
    public float ultrasonicFaultProbability = 0.15f;

    public bool enableYoloDistanceNoise = true;
    public float yoloDistanceNoiseRange = 0.1f;
    private Queue<float[]> actionBuffer = new Queue<float[]>();
    private int currentActionLatency = 0;
    private Rigidbody rb;
    private Vector3 startPos;
    private Quaternion startRot;
    private Vector3 ballStartPos;
    private Quaternion ballStartRot;
    private Transform ballStartParent;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float lastDistanceToBall;
    private bool hasBall;
    private bool targetVisible;
    private float timeSinceLastDetection;
    private float lastKnownAngle;
    private float lastKnownDistance = 1f;
    private float prevGas;
    private float prevSteer;
    private int burstDropoutRemaining = 0;

    protected override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
    }

    public override void Initialize()
    {
        startPos = rb.position;
        startRot = rb.rotation;

        if (ball == null)
            ball = GameObject.FindWithTag("TargetBall")?.transform;

        if (ball != null)
        {
            ballStartPos = ball.position;
            ballStartRot = ball.rotation;
            ballStartParent = ball.parent;
            ballRb = ball.GetComponent<Rigidbody>();
            ballCollider = ball.GetComponent<Collider>();
            lastDistanceToBall = Vector3.Distance(rb.position, ball.position);
        }
    }
    private Vector3 GetRandomPosition(float y)
    {
        float x = Random.Range(-arenaHalfExtents.x, arenaHalfExtents.x);
        float z = Random.Range(-arenaHalfExtents.y, arenaHalfExtents.y);
        return new Vector3(x, y, z);
    }

    public override void OnEpisodeBegin()
    {
        if (randomizeSpawn)
        {
            startPos = GetRandomPosition(startPos.y);
        }

        if (randomizeBall && ball != null)
        {
            Vector3 newBallPos;
            int attempts = 0;
            do
            {
                newBallPos = GetRandomPosition(ballStartPos.y);
                attempts++;
            } while (Vector3.Distance(newBallPos, startPos) < minSpawnDistance && attempts < 50);
            ballStartPos = newBallPos;
        }

        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
            gripper.Release();

        trackController?.Stop();
        if (randomizeMotorParams && isTraining)
        {
            trackController.moveSpeed = UnityEngine.Random.Range(0.3f, 0.7f);
            trackController.turnSpeed = UnityEngine.Random.Range(80f, 160f);
            // если нужно smoothing:
            // trackController.smoothing = UnityEngine.Random.Range(0.01f, 0.25f);
        }
        rb.position = startPos;
        rb.rotation = startRot;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        if (randomizeMass && isTraining)
        {
            rb.mass = UnityEngine.Random.Range(1.0f, 4.0f);
        }

        ResetBall();
        if (enableCommandLatency && isTraining)
        {
            currentActionLatency = UnityEngine.Random.Range(8, 14); // рандомизируются шаги. Шаг = 20 мс. 160, 300
            actionBuffer.Clear();
            for (int i = 0; i < currentActionLatency; i++)
                actionBuffer.Enqueue(new float[] { 0f, 0f });
        }
        else
        {
            currentActionLatency = 0;
            actionBuffer.Clear();
        }

        hasBall = false;
        targetVisible = false;
        timeSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownDistance = 1f;
        prevGas = 0f;
        prevSteer = 0f;
        lastDistanceToBall = ball != null
            ? Vector3.Distance(rb.position, ball.position)
            : 0f;
    }

    private void ResetBall()
    {
        if (ball == null)
            return;

        ball.SetParent(ballStartParent);
        ball.SetPositionAndRotation(ballStartPos, ballStartRot);

        if (ballRb != null)
        {
            ballRb.isKinematic = false;
            ballRb.linearVelocity = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
        }

        if (ballCollider != null)
            ballCollider.enabled = true;
    }

    private void FixedUpdate()
    {
        if (!targetVisible)
            timeSinceLastDetection += Time.fixedDeltaTime;
    }

    public override void CollectObservations(VectorSensor sensor)
    {

        // 1. УЛЬТРАЗВУК С ШУМОМ (±5%)
        float ultrasonic = sensors.GetUltrasonicNormalized();

        // Шум ±5% (если включён через addUltrasonicNoise)
        if (addUltrasonicNoise && isTraining)
            ultrasonic += Random.Range(-0.05f, 0.05f);

        // Сбой с вероятностью 15%
        if (enableUltrasonicFaults && isTraining && Random.value < ultrasonicFaultProbability)
            ultrasonic = Random.value; // случайное значение от 0 до 1

        sensor.AddObservation(Mathf.Clamp01(ultrasonic));

        float leftIR = sensors.GetLeftIR();
        float rightIR = sensors.GetRightIR();
        float gripperIR = sensors.GetGripperIR();

        if (enableIRFaults && isTraining)
        {
            if (Random.value < irFaultProbability) leftIR = 1f - leftIR;
            if (Random.value < irFaultProbability) rightIR = 1f - rightIR;
            if (Random.value < irFaultProbability) gripperIR = 1f - gripperIR;
        }

        sensor.AddObservation(leftIR);
        sensor.AddObservation(rightIR);
        sensor.AddObservation(gripperIR);

        // 2. ЛОГИКА ПАЧЕЧНЫХ ПОТЕРЬ YOLO (Burst Dropout)
        // Уменьшаем счётчик, если активен
        if (burstDropoutRemaining > 0)
            burstDropoutRemaining--;

        // Если робот крутится (угловая скорость > 0.5 рад/с) и тренировка — с шансом 15% активируем слепую зону
        if (enableBurstDropout && isTraining && rb.angularVelocity.magnitude > 0.5f)
        {
            if (UnityEngine.Random.value < 0.15f)
                burstDropoutRemaining = UnityEngine.Random.Range(5, 16);
        }

        // Получаем информацию от YOLO
        var targetInfo = yoloCamera != null
            ? yoloCamera.GetTargetInfo()
            : (angle: 0f, distance: 1f, visible: false);

        float yoloDistance = targetInfo.distance;
        if (enableYoloDistanceNoise && isTraining)
        {
            yoloDistance += Random.Range(-yoloDistanceNoiseRange, yoloDistanceNoiseRange);
            yoloDistance = Mathf.Clamp01(yoloDistance);
        }

        // Применяем dropout к видимости
        bool ballVisible = targetInfo.visible && (burstDropoutRemaining == 0);

        // Обновляем таймер и запоминаем последние координаты
        if (ballVisible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownDistance = yoloDistance; // <-- зашумлённое значение
            timeSinceLastDetection = 0f;
        }
        else
        {
            timeSinceLastDetection += Time.fixedDeltaTime; // или Time.deltaTime – но CollectObservations вызывается не каждый кадр, а каждый шаг агента, поэтому используем фиксированный шаг
        }

        // Отправляем наблюдения (используем ballVisible вместо targetInfo.visible)
        sensor.AddObservation(ballVisible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(ballVisible ? yoloDistance : lastKnownDistance);
        sensor.AddObservation(ballVisible ? 1f : 0f);
        sensor.AddObservation(hasBall ? 1f : 0f);

        // Остальные наблюдения без изменений
        Vector3 deltaPos = rb.position - startPos;
        float arenaX = Mathf.Max(arenaHalfExtents.x, 0.01f);
        float arenaZ = Mathf.Max(arenaHalfExtents.y, 0.01f);
        sensor.AddObservation(Mathf.Clamp(deltaPos.x / arenaX, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(deltaPos.z / arenaZ, -1f, 1f));

        float signedHeading = Mathf.DeltaAngle(0f, rb.rotation.eulerAngles.y) / 180f;
        sensor.AddObservation(signedHeading);
        sensor.AddObservation(Mathf.Clamp01(rb.linearVelocity.magnitude / 2f));
        sensor.AddObservation(Mathf.Clamp01(timeSinceLastDetection / noDetectionTimeout));
    }
    public override void OnActionReceived(ActionBuffers actions)
    {
        if (trackController == null)
            return;

        // === ЗАДЕРЖКА КОМАНД (Latency) через очередь ===
        float gas, steer;
        if (enableCommandLatency && isTraining && currentActionLatency > 0)
        {
            // Кладём свежие действия в конец очереди
            float[] newActions = new float[] {
                Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f),
                Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f)
            };
            actionBuffer.Enqueue(newActions);

            // Извлекаем действие, которое было отправлено currentActionLatency шагов назад
            float[] delayed = actionBuffer.Dequeue();
            gas = delayed[0];
            steer = delayed[1];
        }
        else
        {
            // Без задержки (для ручного управления или инференса)
            gas = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
            steer = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        }

        int gripCommand = actions.DiscreteActions[0];

        // === Двигаем робота с задержанными командами ===
        trackController.Move(gas, steer);
        AddReward(stepPenalty);

        // Штраф за резкие движения (не меняем)
        float gasDelta = Mathf.Abs(gas - prevGas);
        float steerDelta = Mathf.Abs(steer - prevSteer);
        AddReward(-0.002f * (gasDelta + steerDelta));
        prevGas = gas;
        prevSteer = steer;

        // === Управление клешней (без задержки) ===
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
        {
            if (gripCommand == 1) gripper.Grab();
            else if (gripCommand == 2) gripper.Release();
        }

        // === Награды и завершение эпизода (без изменений) ===
        if (ball != null)
        {
            float currentDistance = Vector3.Distance(holdPoint.position, ball.position);
            AddReward((lastDistanceToBall - currentDistance) * moveRewardScale);
            lastDistanceToBall = currentDistance;
        }

        if (sensors.GetUltrasonicNormalized() < 0.2f)
            AddReward(wallPenalty * 0.5f);
        if (sensors.GetLeftIR() > 0.5f || sensors.GetRightIR() > 0.5f)
            AddReward(wallPenalty);

        if (gripper != null && gripper.IsGrabbing)
        {
            hasBall = true;
            AddReward(gripReward);
            EndEpisode();
            return;
        }

        // Награда за попытку захвата рядом с мячом
        if (gripCommand == 1 && ball != null && holdPoint != null)
        {
            float distToBall = Vector3.Distance(holdPoint.position, ball.position);
            if (distToBall < grabDistanceThreshold)
                AddReward(grabAttemptReward);
        }

        // Проверка выхода за арену
        Vector3 displacement = rb.position - startPos;
        bool outsideArena = Mathf.Abs(displacement.x) > arenaHalfExtents.x
            || Mathf.Abs(displacement.z) > arenaHalfExtents.y
            || displacement.y < -fallDistance;

        if (outsideArena)
        {
            AddReward(outOfBoundsPenalty);
            EndEpisode();
            return;
        }

        // Таймаут без обнаружения цели
        if (timeSinceLastDetection > noDetectionTimeout)
        {
            AddReward(-0.2f);
            EpisodeInterrupted();
        }
    }




    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        float gas = 0f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) gas += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) gas -= 1f;

        float steer = 0f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) steer -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) steer += 1f;

        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;
        continuousActions[0] = gas;
        continuousActions[1] = steer;

        if (keyboard.spaceKey.wasPressedThisFrame)
            discreteActions[0] = 1;
        else if (keyboard.rKey.wasPressedThisFrame)
            discreteActions[0] = 2;
        else
            discreteActions[0] = 0;
    }
}
