using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Мозг робота для обучения с подкреплением (ML-Agents).
/// Управляет движением, захватом мяча, получает сенсорные данные и формирует награды.
/// </summary>
[RequireComponent(typeof(TrackController), typeof(VirtualSensors), typeof(Rigidbody))]
public class RobotBrain : Agent
{
    // ==================== ССЫЛКИ НА КОМПОНЕНТЫ ====================
    [Header("Component references")]
    public TrackController trackController;
    public VirtualSensors sensors;
    public SimulatedYoloCamera yoloCamera;
    public Transform gripperTransform;
    public Transform holdPoint;
    public Transform ball;

    // ==================== ОСНОВНЫЕ НАГРАДЫ И ШТРАФЫ ====================
    [Header("Основные награды и штрафы")]
    [Tooltip("Штраф при столкновении со стеной (умножается на множители для разных датчиков)")]
    public float wallPenalty = -0f;

    [Tooltip("Множитель награды за сокращение дистанции до мяча (за каждый метр приближения)")]
    public float moveRewardScale = 0.01f;

    [Tooltip("Награда за успешный захват мяча (завершает эпизод)")]
    public float gripReward = 1f;

    [Tooltip("Штраф при выходе за пределы арены или падении")]
    public float outOfBoundsPenalty = -0.5f;

    // ==================== ГРАНИЦЫ СРЕДЫ ====================
    [Header("Границы среды")]
    [Tooltip("Половина размера арены по осям X и Z (прямоугольная область)")]
    public Vector2 arenaHalfExtents = new Vector2(300f, 300f);

    [Tooltip("Порог падения по оси Y (ниже которого эпизод завершается)")]
    public float fallDistance = 3f;

    // ==================== ОГРАНИЧЕНИЯ ЭПИЗОДА ====================
    [Header("Ограничения эпизода")]
    [Tooltip("Максимальное количество шагов без обнаружения цели, после которого эпизод прерывается")]
    public float noDetectionSteps = 1000f;

    [Tooltip("Штраф за каждый шаг (стимулирует быстрое выполнение задачи)")]
    public float stepPenalty = -0.0002f;

    [Tooltip("Бонус, когда робот находится в зоне, где возможен захват мяча (однократно за эпизод)")]
    public float grabAttemptReward = 0.1f;

    [Header("Ready-to-Grab zone")]
    [Tooltip("Радиус зоны вокруг мяча, в которой считается, что робот готов к захвату (используется для бонуса и для отключения штрафа за потерю цели)")]
    public float grabZoneRadius = 1f;

    // ==================== ДОПОЛНИТЕЛЬНЫЕ КОЭФФИЦИЕНТЫ ====================
    [Header("Коэффициенты наград (дополнительные)")]
    [Tooltip("Множитель штрафа за резкие изменения газа и руля")]
    public float smoothnessPenaltyScale = 0.002f;

    [Tooltip("Множитель награды за уменьшение угла до мяча")]
    public float angleRewardScale = 0.005f;

    [Tooltip("Штраф при превышении лимита шагов без детекции")]
    public float noDetectionPenalty = -0.2f;

    [Tooltip("Множитель штрафа за ультразвуковой датчик (применяется к wallPenalty)")]
    public float wallUltrasonicMultiplier = 0.5f;

    [Tooltip("Штраф за неудачную попытку захвата (клешня закрыта или мяч вне зоны)")]
    public float failedGrabPenalty = -0.1f;

    // ==================== ШТРАФ ЗА СКОРОСТЬ В ЗОНЕ ЗАХВАТА ====================
    [Header("Speed Penalty in Grab Zone")]
    [Tooltip("Порог линейной скорости (единицы/с, 1 = 1 дм), выше которого начисляется штраф")]
    public float speedPenaltyThresholdLinear = 0.2f;   // 2 дм/с, например

    [Tooltip("Порог угловой скорости (градусы/с), выше которого начисляется штраф")]
    public float speedPenaltyThresholdAngular = 30f;   // 30 градусов/с

    [Tooltip("Величина штрафа за превышение любого порога скорости (отрицательное число)")]
    public float speedPenaltyAmount = -0.05f;

    // ==================== РАНДОМИЗАЦИЯ СТАРТОВЫХ ПОЗИЦИЙ ====================
    [Header("Spawn Randomization")]
    [Tooltip("Половина размера спавна по осям X и Z (прямоугольная область)")]
    public Vector2 spawnHalfExtents = new Vector2(15f, 15f);

    [Tooltip("Включает случайную позицию робота при старте каждого эпизода")]
    public bool randomizeSpawn = true;

    [Tooltip("Включает случайную позицию мяча при старте каждого эпизода")]
    public bool randomizeBall = true;

    [Tooltip("Минимальное расстояние между роботом и мячом при рандомизации (чтобы не спавниться друг на друге)")]
    public float minSpawnDistance = 2f;

    [Tooltip("Флаг, указывающий, что сейчас идёт обучение (включайте в инспекторе для тренировки)")]
    public bool isTraining = true;

    // ==================== ПРИВАТНЫЕ ПОЛЯ ====================
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
    private float stepsSinceLastDetection;
    private float lastKnownAngle;
    private float lastKnownArea = 0f;
    private float prevGas;
    private float prevSteer;
    private float prevAbsAngle = 180f;
    private bool grabZoneRewardGranted;
    private int previousGripCommand = 0; // Предыдущая команда клешни (0 – ничего, 1 – захват, 2 – отпустить)

    // Статистика эпизода для TensorBoard
    private Dictionary<string, float> rewardSumDict;
    private Dictionary<string, int> rewardCountDict;
    private int episodeStepCounter;
    private bool statsSent;

    // ==================== МЕТОДЫ ЖИЗНЕННОГО ЦИКЛА ====================

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
            lastDistanceToBall = holdPoint != null
                ? Vector3.Distance(holdPoint.position, ball.position)
                : 0f;
        }

        rewardSumDict = new Dictionary<string, float>();
        rewardCountDict = new Dictionary<string, int>();
        episodeStepCounter = 0;
        statsSent = false;
    }

    /// <summary>
    /// Генерирует случайную позицию в пределах арены на заданной высоте Y.
    /// </summary>
    private Vector3 GetRandomPosition(float y)
    {
        float x = Random.Range(-spawnHalfExtents.x, spawnHalfExtents.x);
        float z = Random.Range(-spawnHalfExtents.y, spawnHalfExtents.y);
        return new Vector3(x, y, z);
    }

    public override void OnEpisodeBegin()
    {
        // ----- Рандомизация стартовых позиций (если включено) -----
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

        // Сброс статистики
        rewardSumDict.Clear();
        rewardCountDict.Clear();
        episodeStepCounter = 0;
        statsSent = false;

        // Открываем клешню
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
            gripper.Release();

        // Останавливаем движение
        trackController?.Stop();

        // Устанавливаем робота в начальную позицию
        rb.position = startPos;
        rb.rotation = startRot;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // Сбрасываем мяч (используется обновлённый ballStartPos)
        ResetBall();

        // Сброс внутренних флагов
        hasBall = false;
        targetVisible = false;
        stepsSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownArea = 0f;
        prevGas = 0f;
        prevSteer = 0f;
        prevAbsAngle = 180f;
        grabZoneRewardGranted = false;
        lastDistanceToBall = ball != null && holdPoint != null
            ? Vector3.Distance(holdPoint.position, ball.position)
            : 0f;
        previousGripCommand = 0;
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

    // ==================== СБОР НАБЛЮДЕНИЙ ====================

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1. Ультразвук (сырое расстояние)
        sensor.AddObservation(sensors.GetUltrasonicDistance());

        // 2. Информация о цели с YOLO-камеры (угол, площадь, видимость)
        var targetInfo = yoloCamera != null
            ? yoloCamera.GetTargetInfo()
            : (angle: 0f, area: 0f, visible: false);

        targetVisible = targetInfo.visible;
        if (targetInfo.visible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownArea = targetInfo.area;
            stepsSinceLastDetection = 0f;
        }

        sensor.AddObservation(targetInfo.visible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(targetInfo.visible ? targetInfo.area : lastKnownArea);
        sensor.AddObservation(targetInfo.visible ? 1f : 0f);

        // 3. Состояние клешни (открыта/закрыта)
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        sensor.AddObservation(gripper != null && gripper.IsOpen ? 1f : 0f);

        // 4. Доля шагов без обнаружения цели
        sensor.AddObservation(Mathf.Clamp01(stepsSinceLastDetection / noDetectionSteps));

        // ----- 5. ПРЕДЫДУЩИЕ ДЕЙСТВИЯ (только один предыдущий шаг) -----
        // Газ и руль с учётом клиппинга
        sensor.AddObservation(prevGas);
        sensor.AddObservation(prevSteer);
        // Команда клешни
        sensor.AddObservation(previousGripCommand);
    }

    // ==================== ПРИНЯТИЕ РЕШЕНИЙ ====================

    public override void OnActionReceived(ActionBuffers actions)
    {
        episodeStepCounter++;

        // Обновление счётчика шагов без детекции
        if (!targetVisible)
            stepsSinceLastDetection++;
        else
            stepsSinceLastDetection = 0;

        if (trackController == null)
            return;

        // Извлечение действий
        float gas = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        float steer = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        int gripCommand = actions.DiscreteActions[0];

        // Применяем клиппинг газа с учётом maxLinearCmd (как в TrackController)
        float clampedGas = Mathf.Clamp(gas, -trackController.maxLinearCmd, trackController.maxLinearCmd);
        float clampedSteer = Mathf.Clamp(steer, -1f, 1f);

        // Передаём команды в драйвер гусениц
        trackController.Move(clampedGas, clampedSteer);

        // ---------- НАГРАДЫ И ШТРАФЫ (каждый шаг) ----------

        // 1. Штраф за каждый шаг
        AddRewardWithStats("StepPenalty", stepPenalty);

        // 2. Штраф за резкие изменения управления (используем предыдущие значения)
        float gasDelta = Mathf.Abs(clampedGas - prevGas);
        float steerDelta = Mathf.Abs(clampedSteer - prevSteer);
        float smoothnessPenalty = -smoothnessPenaltyScale * (gasDelta + steerDelta);
        AddRewardWithStats("SmoothnessPenalty", smoothnessPenalty);

        // 3. Награда за уменьшение угла до мяча (если цель видна)
        if (targetVisible)
        {
            float currentAbsAngle = Mathf.Abs(lastKnownAngle);
            if (prevAbsAngle < 180f)
            {
                float angleImprovement = prevAbsAngle - currentAbsAngle;
                float reward = angleRewardScale * angleImprovement;
                AddRewardWithStats("AngleReward", reward);
            }
            prevAbsAngle = currentAbsAngle;
        }
        else
        {
            prevAbsAngle = 180f;
        }

        // 4. Бонус за нахождение в зоне захвата (однократно за эпизод)
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
        {
            if (!grabZoneRewardGranted && ball != null && holdPoint != null &&
                Vector3.Distance(holdPoint.position, ball.position) < grabZoneRadius)
            {
                AddRewardWithStats("GrabZoneReward", grabAttemptReward);
                grabZoneRewardGranted = true;
            }
        }

        // 5. Награда за приближение к мячу (реальное расстояние)
        if (ball != null)
        {
            float currentDistance = Vector3.Distance(holdPoint.position, ball.position);
            float distReward = (lastDistanceToBall - currentDistance) * moveRewardScale;
            AddRewardWithStats("DistanceReward", distReward);
            lastDistanceToBall = currentDistance;
        }

        // 6. Штрафы за близость к стенам (ультразвук)
        if (sensors.GetUltrasonicDistance() < 0.5f)
            AddRewardWithStats("WallUltrasonic", wallPenalty * wallUltrasonicMultiplier);

        // 7. Штраф за высокую скорость в зоне захвата
        // Проверяем, находимся ли в зоне захвата (используем расстояние от holdPoint до мяча)
        if (ball != null && holdPoint != null &&
            Vector3.Distance(holdPoint.position, ball.position) < grabZoneRadius)
        {
            // Получаем текущие скорости
            float linearSpeed = rb.linearVelocity.magnitude;          // в единицах/с
            float angularSpeed = rb.angularVelocity.magnitude * Mathf.Rad2Deg; // переводим в градусы/с

            bool speedExceeded = false;
            if (linearSpeed > speedPenaltyThresholdLinear)
                speedExceeded = true;
            if (angularSpeed > speedPenaltyThresholdAngular)
                speedExceeded = true;

            if (speedExceeded)
            {
                AddRewardWithStats("SpeedPenaltyInGrabZone", speedPenaltyAmount);
            }
        }

        // ---------- ОБРАБОТКА КОМАНД КЛЕШНИ ----------

        if (gripper != null)
        {
            if (gripCommand == 1) // Захват
            {
                if (gripper.IsGrabbing)
                {
                    // Уже держит мяч – ничего не делаем
                }
                else if (gripper.IsOpen)
                {
                    bool success = gripper.Grab();
                    if (!success)
                    {
                        AddRewardWithStats("FailedGrab", failedGrabPenalty);
                    }
                }
                else // Клешня закрыта (и не держит мяч)
                {
                    AddRewardWithStats("FailedGrab", failedGrabPenalty);
                }
            }
            else if (gripCommand == 2) // Отпускание
            {
                if (!gripper.IsOpen)
                {
                    gripper.Release();
                }
            }
        }

        // ---------- СОХРАНЯЕМ ТЕКУЩИЕ ДЕЙСТВИЯ КАК ПРЕДЫДУЩИЕ ДЛЯ СЛЕДУЮЩЕГО ШАГА ----------
        prevGas = clampedGas;
        prevSteer = clampedSteer;
        previousGripCommand = gripCommand;

        // ---------- ПРОВЕРКИ ЗАВЕРШЕНИЯ ЭПИЗОДА ----------

        // 7. Успешный захват мяча – главная положительная цель
        if (gripper != null && gripper.IsGrabbing)
        {
            hasBall = true;
            AddRewardWithStats("GripReward", gripReward);
            SendStatsToTensorBoard();
            EndEpisode();
            return;
        }

        // 8. Выход за границы арены или падение
        Vector3 displacement = rb.position - startPos;
        bool outsideArena = Mathf.Abs(displacement.x) > arenaHalfExtents.x
            || Mathf.Abs(displacement.z) > arenaHalfExtents.y
            || displacement.y < -fallDistance;

        if (outsideArena)
        {
            AddRewardWithStats("OutOfBounds", outOfBoundsPenalty);
            SendStatsToTensorBoard();
            EndEpisode();
            return;
        }

        // 9. Долгая потеря цели – штраф и прерывание
        bool isInGrabZone = gripper != null && ball != null && holdPoint != null && 
                            Vector3.Distance(holdPoint.position, ball.position) < grabZoneRadius;
        if (!isInGrabZone && stepsSinceLastDetection > noDetectionSteps)
        {
            AddRewardWithStats("NoDetection", noDetectionPenalty);
            SendStatsToTensorBoard();
            EndEpisode();
        }
    }

    // ==================== ЭВРИСТИЧЕСКИЙ РЕЖИМ ====================

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

        // Клиппинг для соответствия с OnActionReceived
        float clampedGas = Mathf.Clamp(gas, -trackController.maxLinearCmd, trackController.maxLinearCmd);
        float clampedSteer = Mathf.Clamp(steer, -1f, 1f);

        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;
        continuousActions[0] = clampedGas;
        continuousActions[1] = clampedSteer;

        int gripCommand = 0;
        if (keyboard.gKey.wasPressedThisFrame)
            gripCommand = 1;
        else if (keyboard.rKey.wasPressedThisFrame)
            gripCommand = 2;
        discreteActions[0] = gripCommand;

        // Обновляем историю для согласованности наблюдений в эвристическом режиме
        prevGas = clampedGas;
        prevSteer = clampedSteer;
        previousGripCommand = gripCommand;
    }

    // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ДЛЯ СТАТИСТИКИ ====================

    private void AddRewardWithStats(string type, float reward)
    {
        AddReward(reward);
        if (!rewardSumDict.ContainsKey(type))
        {
            rewardSumDict[type] = 0f;
            rewardCountDict[type] = 0;
        }
        rewardSumDict[type] += reward;
        rewardCountDict[type] += 1;
    }

    private void SendStatsToTensorBoard()
    {
        if (statsSent) return;
        statsSent = true;

        var statsRecorder = Academy.Instance.StatsRecorder;
        if (statsRecorder == null) return;

        statsRecorder.Add("Rewards/TotalEpisodeReward", GetCumulativeReward());
        statsRecorder.Add("Rewards/EpisodeLength", episodeStepCounter);

        foreach (var kvp in rewardSumDict)
        {
            string type = kvp.Key;
            float sum = kvp.Value;
            int count = rewardCountDict[type];
            float avg = count > 0 ? sum / count : 0f;

            statsRecorder.Add($"Rewards/{type}_Sum", sum);
            statsRecorder.Add($"Rewards/{type}_Count", count);
            statsRecorder.Add($"Rewards/{type}_Avg", avg);
        }
    }
}
