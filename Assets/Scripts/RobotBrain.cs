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
    public float fallDistance = 300f;

    // ==================== ОГРАНИЧЕНИЯ ЭПИЗОДА ====================
    [Header("Ограничения эпизода")]
    [Tooltip("Максимальное количество шагов без обнаружения цели, после которого эпизод прерывается")]
    public float noDetectionSteps = 100f;

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

    [Tooltip("Множитель штрафа за боковые ИК-датчики (применяется к wallPenalty)")]
    public float wallIRMultiplier = 1f;

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
    private float lastKnownDistance = 1f;
    private float prevGas;
    private float prevSteer;
    private float prevAbsAngle = 180f;
    private bool grabZoneRewardGranted;

    // Статистика эпизода для TensorBoard
    private Dictionary<string, float> rewardSumDict;
    private Dictionary<string, int> rewardCountDict;
    private int episodeStepCounter;
    private bool statsSent;

    // ==================== МЕТОДЫ ЖИЗНЕННОГО ЦИКЛА ====================

    /// <summary>
    /// Вызывается при пробуждении объекта. Получает ссылки на компоненты.
    /// </summary>
    protected override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
    }

    /// <summary>
    /// Инициализация агента: запоминает начальные позиции, находит мяч, инициализирует статистику.
    /// </summary>
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
    /// Вызывается в начале каждого нового эпизода. Сбрасывает состояние робота, мяча и статистику.
    /// </summary>
    public override void OnEpisodeBegin()
    {
        // Статистика уже отправлена перед EndEpisode, здесь только сбрасываем данные
        rewardSumDict.Clear();
        rewardCountDict.Clear();
        episodeStepCounter = 0;
        statsSent = false;

        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
            gripper.Release();

        trackController?.Stop();
        rb.position = startPos;
        rb.rotation = startRot;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        ResetBall();

        hasBall = false;
        targetVisible = false;
        stepsSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownDistance = 1f;
        prevGas = 0f;
        prevSteer = 0f;
        prevAbsAngle = 180f;
        grabZoneRewardGranted = false;
        lastDistanceToBall = ball != null && holdPoint != null
            ? Vector3.Distance(holdPoint.position, ball.position)
            : 0f;
    }

    /// <summary>
    /// Сбрасывает мяч в начальное положение и отключает физику.
    /// </summary>
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

    /// <summary>
    /// Заполняет вектор наблюдений данными с датчиков, камеры, позиции и состояния.
    /// </summary>
    public override void CollectObservations(VectorSensor sensor)
    {
        // Ультразвук и ИК-датчики
        sensor.AddObservation(sensors.GetUltrasonicNormalized());
        sensor.AddObservation(sensors.GetLeftIR());
        sensor.AddObservation(sensors.GetRightIR());
        sensor.AddObservation(sensors.GetGripperIR());

        // Информация о цели с YOLO-камеры
        var targetInfo = yoloCamera != null
            ? yoloCamera.GetTargetInfo()
            : (angle: 0f, distance: 1f, visible: false);

        targetVisible = targetInfo.visible;
        if (targetInfo.visible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownDistance = targetInfo.distance;
            stepsSinceLastDetection = 0f;
        }

        sensor.AddObservation(targetInfo.visible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(targetInfo.visible ? targetInfo.distance : lastKnownDistance);
        sensor.AddObservation(targetInfo.visible ? 1f : 0f);
        sensor.AddObservation(hasBall ? 1f : 0f);

        // Относительное положение относительно старта (нормировано)
        Vector3 deltaPos = rb.position - startPos;
        float arenaX = Mathf.Max(arenaHalfExtents.x, 0.01f);
        float arenaZ = Mathf.Max(arenaHalfExtents.y, 0.01f);
        sensor.AddObservation(Mathf.Clamp(deltaPos.x / arenaX, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(deltaPos.z / arenaZ, -1f, 1f));

        // Текущий курс (угол поворота) и скорость
        float signedHeading = Mathf.DeltaAngle(0f, rb.rotation.eulerAngles.y) / 180f;
        sensor.AddObservation(signedHeading);
        sensor.AddObservation(Mathf.Clamp01(rb.linearVelocity.magnitude / 2f));
        sensor.AddObservation(Mathf.Clamp01(stepsSinceLastDetection / noDetectionSteps));
    }

    // ==================== ПРИНЯТИЕ РЕШЕНИЙ ====================

    /// <summary>
    /// Основной метод, вызываемый каждый шаг симуляции. Принимает действия (газ, руль, команда захвата),
    /// вычисляет награды и проверяет условия завершения эпизода.
    /// </summary>
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

        trackController.Move(gas, steer);

        // ---------- НАГРАДЫ И ШТРАФЫ (каждый шаг) ----------

        // 1. Штраф за каждый шаг – стимулирует быстрое выполнение задачи
        AddRewardWithStats("StepPenalty", stepPenalty);

        // 2. Штраф за резкие изменения управления – поощряет плавность
        float gasDelta = Mathf.Abs(gas - prevGas);
        float steerDelta = Mathf.Abs(steer - prevSteer);
        float smoothnessPenalty = -smoothnessPenaltyScale * (gasDelta + steerDelta);
        AddRewardWithStats("SmoothnessPenalty", smoothnessPenalty);
        prevGas = gas;
        prevSteer = steer;

        // 3. Награда за уменьшение угла до мяча (если цель видна)
        //    Поощряет поворачиваться прямо на мяч.
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

            if (gripCommand == 1) gripper.Grab();
            else if (gripCommand == 2) gripper.Release();
        }

        // 5. Награда за приближение к мячу (разность расстояний * масштаб)
        if (ball != null)
        {
            float currentDistance = Vector3.Distance(holdPoint.position, ball.position);
            float distReward = (lastDistanceToBall - currentDistance) * moveRewardScale;
            AddRewardWithStats("DistanceReward", distReward);
            lastDistanceToBall = currentDistance;
        }

        // 6. Штрафы за близость к стенам (по разным датчикам)
        if (sensors.GetUltrasonicNormalized() < 0.01f)
            AddRewardWithStats("WallUltrasonic", wallPenalty * wallUltrasonicMultiplier);
        if (sensors.GetLeftIR() > 0.5f || sensors.GetRightIR() > 0.5f)
            AddRewardWithStats("WallIR", wallPenalty * wallIRMultiplier);

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

        // 8. Выход за границы арены или падение – штраф и завершение
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

        // 9. Долгая потеря цели – штраф и прерывание эпизода
        bool isInGrabZone = gripper != null && ball != null && holdPoint != null && 
                            Vector3.Distance(holdPoint.position, ball.position) < grabZoneRadius;
        if (!isInGrabZone && stepsSinceLastDetection > noDetectionSteps)
        {
            AddRewardWithStats("NoDetection", noDetectionPenalty);
            SendStatsToTensorBoard();
            EndEpisode();
        }
    }

    /// <summary>
    /// Эвристический режим (ручное управление) для тестирования.
    /// Клавиши: W/S – газ, A/D – руль, Пробел – захват, R – отпускание.
    /// </summary>
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

    // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ДЛЯ СТАТИСТИКИ ====================

    /// <summary>
    /// Добавляет награду и одновременно накапливает статистику по её типу.
    /// </summary>
    /// <param name="type">Уникальное имя типа награды (для логирования)</param>
    /// <param name="reward">Величина награды (может быть отрицательной)</param>
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

    /// <summary>
    /// Отправляет собранную за эпизод статистику в TensorBoard через StatsRecorder.
    /// Вызывается перед завершением эпизода.
    /// </summary>
    private void SendStatsToTensorBoard()
    {
        if (statsSent) return;
        statsSent = true;

        var statsRecorder = Academy.Instance.StatsRecorder;
        if (statsRecorder == null) return;

        // Общая награда эпизода и его длина
        statsRecorder.Add("Rewards/TotalEpisodeReward", GetCumulativeReward());
        statsRecorder.Add("Rewards/EpisodeLength", episodeStepCounter);

        // По каждому типу награды: сумма, количество срабатываний, среднее значение
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