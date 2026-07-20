using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using System.Collections.Generic;
using Team11.Ros;

public enum RobotGripperCommand
{
    None = 0,
    Grab = 1,
    Release = 2
}

public enum RobotGripAttemptResult
{
    None,
    Succeeded,
    Failed
}

/// <summary>
/// Backend-independent robot command. Linear and angular values are normalized
/// to [-1, 1]; positive angular means a right turn in the policy coordinate system.
/// </summary>
public readonly struct RobotCommand
{
    public RobotCommand(float linear, float angular, RobotGripperCommand gripper)
    {
        Linear = Mathf.Clamp(linear, -1f, 1f);
        Angular = Mathf.Clamp(angular, -1f, 1f);
        Gripper = gripper;
    }

    public float Linear { get; }
    public float Angular { get; }
    public RobotGripperCommand Gripper { get; }

    public static RobotCommand Stopped => new RobotCommand(0f, 0f, RobotGripperCommand.None);
}

public readonly struct RobotCommandResult
{
    public RobotCommandResult(
        bool hasGripperState,
        bool isGripperOpen,
        bool isGrabbing,
        RobotGripAttemptResult gripAttempt)
    {
        HasGripperState = hasGripperState;
        IsGripperOpen = isGripperOpen;
        IsGrabbing = isGrabbing;
        GripAttempt = gripAttempt;
    }

    public bool HasGripperState { get; }
    public bool IsGripperOpen { get; }
    public bool IsGrabbing { get; }
    public RobotGripAttemptResult GripAttempt { get; }

    public static RobotCommandResult Unavailable =>
        new RobotCommandResult(false, false, false, RobotGripAttemptResult.None);
}

public interface IRobotCommandSink
{
    RobotCommandResult ApplyCommand(RobotCommand command);
    void Stop();
    bool TryGetGripperState(out bool isOpen);
}

public sealed class SimulationRobotCommandSink : IRobotCommandSink
{
    private readonly TrackController trackController;
    private readonly GripperController gripperController;

    public SimulationRobotCommandSink(
        TrackController trackController,
        GripperController gripperController)
    {
        this.trackController = trackController;
        this.gripperController = gripperController;
    }

    public RobotCommandResult ApplyCommand(RobotCommand command)
    {
        if (trackController != null)
        {
            trackController.Move(
                command.Linear * trackController.maxLinearCmd,
                command.Angular);
        }

        RobotGripAttemptResult gripAttempt = RobotGripAttemptResult.None;
        if (gripperController != null)
        {
            if (command.Gripper == RobotGripperCommand.Grab)
            {
                if (!gripperController.IsGrabbing)
                {
                    gripAttempt = gripperController.IsOpen && gripperController.Grab()
                        ? RobotGripAttemptResult.Succeeded
                        : RobotGripAttemptResult.Failed;
                }
            }
            else if (command.Gripper == RobotGripperCommand.Release &&
                     !gripperController.IsOpen)
            {
                gripperController.Release();
            }
        }

        return gripperController != null
            ? new RobotCommandResult(
                true,
                gripperController.IsOpen,
                gripperController.IsGrabbing,
                gripAttempt)
            : RobotCommandResult.Unavailable;
    }

    public void Stop()
    {
        trackController?.Stop();
    }

    public bool TryGetGripperState(out bool isOpen)
    {
        isOpen = gripperController != null && gripperController.IsOpen;
        return gripperController != null;
    }
}

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

    [Header("Inference I/O source")]
    [Tooltip("Use the physical robot for both policy observations and policy commands.")]
    [FormerlySerializedAs("useRealRobotSensors")]
    [SerializeField] private bool useRealRobotIo;

    public bool UseRealRobotIo
    {
        get => useRealRobotIo;
        set
        {
            if (useRealRobotIo == value)
            {
                return;
            }

            GetSelectedCommandSink()?.Stop();
            useRealRobotIo = value;
        }
    }

    public bool UseRealRobotSensors
    {
        get => UseRealRobotIo;
        set => UseRealRobotIo = value;
    }

    [Tooltip("Опционально: если назначен, периодически генерирует новые стены/путь/препятствия и переставляет робота и мяч, вместо возврата на фиксированную стартовую позицию")]
    public EnvironmentManager environmentManager;

    [Tooltip("Через сколько эпизодов перегенерировать арену (новый путь/препятствия). 1 = каждый эпизод. Остальные эпизоды между перегенерациями просто возвращают робота и мяч на те же позиции, что и в последней сгенерированной конфигурации")]
    public int regenerateEveryEpisodes = 10;

    private int episodesUntilRegenerate = 0;

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

    [Tooltip("Master switch for camera, robot spawn and ball spawn randomization.")]
    public bool isTraining = true;

    // ==================== ПРИВАТНЫЕ ПОЛЯ ====================
    private Rigidbody rb;
    private Vector3 startPos;
    private Vector3 initialStartPos;
    private Quaternion startRot;
    private Vector3 ballStartPos;
    private Vector3 initialBallStartPos;
    private Quaternion ballStartRot;
    private Transform ballStartParent;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float lastDistanceToBall;
    private bool hasBall;
    private bool targetVisible;
    private float stepsSinceLastDetection;
    private float lastKnownAngle;
    private float lastKnownAreaRatio;
    private float lastKnownAspectRatio;
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
    private RobotRosTeleop robotSensorReceiver;
    private RealVision realVision;
    private SimulationRobotCommandSink simulationCommandSink;

    // ==================== МЕТОДЫ ЖИЗНЕННОГО ЦИКЛА ====================

    protected override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
        simulationCommandSink = new SimulationRobotCommandSink(
            trackController,
            gripperTransform?.GetComponent<GripperController>());
    }

    public override void Initialize()
    {
        startPos = rb.position;
        initialStartPos = startPos;
        startRot = rb.rotation;

        if (!IsLoadedSceneObject(ball))
            ball = GameObject.FindWithTag("TargetBall")?.transform;

        if (yoloCamera != null && !IsLoadedSceneObject(yoloCamera.targetBall))
            yoloCamera.targetBall = ball;

        if (ball != null)
        {
            ballStartPos = ball.position;
            initialBallStartPos = ballStartPos;
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

    private static bool IsLoadedSceneObject(Transform target)
    {
        return target != null && target.gameObject.scene.IsValid() && target.gameObject.scene.isLoaded;
    }

    private IRobotCommandSink GetSelectedCommandSink()
    {
        if (!useRealRobotIo)
        {
            return simulationCommandSink;
        }

        if (robotSensorReceiver == null)
        {
            robotSensorReceiver = FindAnyObjectByType<RobotRosTeleop>();
        }

        return robotSensorReceiver;
    }

    private bool TryGetSelectedGripperState(out bool isOpen)
    {
        IRobotCommandSink commandSink = GetSelectedCommandSink();
        if (commandSink != null)
        {
            return commandSink.TryGetGripperState(out isOpen);
        }

        isOpen = false;
        return false;
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
        if (!useRealRobotIo)
        {
            if (isTraining)
                yoloCamera?.RandomizeDomainParameters();
            else
                yoloCamera?.ResetDomainParameters();
        }

        // ----- Рандомизация стартовых позиций (если включено, нет EnvironmentManager
        // и идёт обучение) -----
        // Когда назначен EnvironmentManager, он сам расставляет робота и мяч (с учётом
        // стен/пути/препятствий) ниже, используя последний сгенерированный startPos/
        // ballStartPos между перегенерациями — простой сброс к initialStartPos и
        // рандомизация по прямоугольнику здесь их бы сразу перезаписали.
        if (environmentManager == null)
        {
            startPos = initialStartPos;
            if (ball != null)
                ballStartPos = initialBallStartPos;

            if (isTraining && randomizeSpawn)
            {
                startPos = GetRandomPosition(startPos.y);
            }

            if (isTraining && randomizeBall && ball != null)
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
        }

        // Сброс статистики
        rewardSumDict.Clear();
        rewardCountDict.Clear();
        episodeStepCounter = 0;
        statsSent = false;

        // Открываем клешню
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (!useRealRobotIo && gripper != null)
            gripper.Release();

        // Останавливаем движение
        simulationCommandSink?.Stop();
        if (useRealRobotIo)
            GetSelectedCommandSink()?.Stop();

        // Сначала освобождаем мяч, если он был захвачен клешнёй в прошлом эпизоде —
        // иначе генератор арены (или простой рестарт позиции) переставит мяч,
        // всё ещё "прилипший" к руке робота.
        ResetBall();

        if (environmentManager != null)
        {
            // Перегенерируем конфигурацию (путь/препятствия) не каждый эпизод, а раз
            // в regenerateEveryEpisodes эпизодов — иначе арена меняется каждые
            // несколько секунд, и агент не успевает потренироваться на одной раскладке.
            if (episodesUntilRegenerate <= 0)
            {
                // Генератор сам строит новые стены, путь и препятствия и переставляет
                // робота и мяч на новые случайные позиции внутри арены (высота
                // сохраняется — см. EnvironmentManager.RandomizeStartAndTarget()).
                environmentManager.GenerateArena();

                // Границы для наблюдений/выхода-за-пределы должны совпадать с реальным
                // размером сгенерированной арены, а не с дефолтным значением поля.
                arenaHalfExtents = environmentManager.baseArenaSize * environmentManager.globalScale * 0.5f;
                startPos = rb.position;
                startRot = rb.rotation;
                if (ball != null)
                {
                    ballStartPos = ball.position;
                    ballStartRot = ball.rotation;
                    ballStartParent = ball.parent;
                }
                episodesUntilRegenerate = Mathf.Max(1, regenerateEveryEpisodes);
            }
            else
            {
                // Та же конфигурация — просто возвращаем робота на позицию,
                // зафиксированную при последней генерации (мяч уже вернул ResetBall()).
                rb.position = startPos;
                rb.rotation = startRot;
            }
            episodesUntilRegenerate--;
        }
        else
        {
            // Без EnvironmentManager — startPos/ballStartPos уже посчитаны выше
            // через randomizeSpawn/randomizeBall (или остались от прошлого эпизода).
            rb.position = startPos;
            rb.rotation = startRot;
        }

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        hasBall = false;
        targetVisible = false;
        stepsSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownAreaRatio = 0f;
        lastKnownAspectRatio = 0f;
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

    public bool TryGetSelectedRangeSensors(
        out float ultrasonicMeters,
        out bool leftIrTriggered,
        out bool rightIrTriggered,
        out bool centerIrTriggered)
    {
        if (!useRealRobotIo)
        {
            if (sensors != null)
            {
                return sensors.TryReadSimulationSensors(
                    out ultrasonicMeters,
                    out leftIrTriggered,
                    out rightIrTriggered,
                    out centerIrTriggered);
            }
        }
        else
        {
            if (robotSensorReceiver == null)
            {
                robotSensorReceiver = FindAnyObjectByType<RobotRosTeleop>();
            }

            if (robotSensorReceiver != null)
            {
                return robotSensorReceiver.TryGetFreshRobotSensors(
                    out ultrasonicMeters,
                    out leftIrTriggered,
                    out rightIrTriggered,
                    out centerIrTriggered);
            }
        }

        ultrasonicMeters = 0f;
        leftIrTriggered = false;
        rightIrTriggered = false;
        centerIrTriggered = false;
        return false;
    }

    public bool TryGetSelectedVision(
        out (float angle, float areaRatio, float aspectRatio, bool visible) targetInfo)
    {
        SimulatedYoloCamera selectedVision = yoloCamera;
        if (useRealRobotIo)
        {
            if (realVision == null)
            {
                realVision = FindAnyObjectByType<RealVision>();
            }

            if (realVision == null || !realVision.HasFreshPacket)
            {
                targetInfo = (0f, 0f, 0f, false);
                return false;
            }

            selectedVision = realVision;
        }

        if (selectedVision == null)
        {
            targetInfo = (0f, 0f, 0f, false);
            return false;
        }

        targetInfo = selectedVision.GetTargetInfo();
        return true;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1. Ультразвук (сырое расстояние) и ИК-датчики
        TryGetSelectedRangeSensors(
            out float ultrasonicMeters,
            out bool leftIr,
            out bool rightIr,
            out bool _);

        sensor.AddObservation(ultrasonicMeters);
        sensor.AddObservation(leftIr ? 1f : 0f);
        sensor.AddObservation(rightIr ? 1f : 0f);

        float gripperIrValue = 0f;
        if (sensors != null)
        {
            gripperIrValue = sensors.GetGripperIR(); // 0 или 1
        }
        sensor.AddObservation(gripperIrValue);

        // 2. Информация о цели с YOLO-камеры (угол, площадь, видимость)
        TryGetSelectedVision(out var targetInfo);

        targetVisible = targetInfo.visible;
        if (targetInfo.visible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownAreaRatio = targetInfo.areaRatio;
            lastKnownAspectRatio = targetInfo.aspectRatio;
            stepsSinceLastDetection = 0f;
        }

        sensor.AddObservation(targetInfo.visible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(targetInfo.visible ? targetInfo.areaRatio : lastKnownAreaRatio);
        sensor.AddObservation(targetInfo.visible ? targetInfo.aspectRatio : lastKnownAspectRatio);
        sensor.AddObservation(targetInfo.visible ? 1f : 0f);

        // 3. Состояние клешни (открыта/закрыта)
        TryGetSelectedGripperState(out bool isGripperOpen);
        sensor.AddObservation(isGripperOpen ? 1f : 0f);

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

        // Извлечение действий
        float gas = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        float steer = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        RobotGripperCommand gripCommand = (RobotGripperCommand)Mathf.Clamp(
            actions.DiscreteActions[0],
            (int)RobotGripperCommand.None,
            (int)RobotGripperCommand.Release);

        // Применяем клиппинг газа с учётом maxLinearCmd (как в TrackController)
        float maxLinearCommand = trackController != null
            ? Mathf.Max(0f, trackController.maxLinearCmd)
            : 0f;
        float clampedGas = Mathf.Clamp(gas, -maxLinearCommand, maxLinearCommand);
        float clampedSteer = Mathf.Clamp(steer, -1f, 1f);

        float normalizedLinear = maxLinearCommand > Mathf.Epsilon
            ? clampedGas / maxLinearCommand
            : 0f;
        var robotCommand = new RobotCommand(normalizedLinear, clampedSteer, gripCommand);
        RobotCommandResult commandResult = GetSelectedCommandSink()?.ApplyCommand(robotCommand)
            ?? RobotCommandResult.Unavailable;

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
        if (TryGetSelectedRangeSensors(
                out float rewardUltrasonicMeters,
                out _,
                out _,
                out _) &&
            rewardUltrasonicMeters < 0.5f)
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

        // Результат попытки захвата доступен только у backend с обратной связью.
        if (commandResult.GripAttempt == RobotGripAttemptResult.Failed)
        {
            AddRewardWithStats("FailedGrab", failedGrabPenalty);
        }

        // ---------- СОХРАНЯЕМ ТЕКУЩИЕ ДЕЙСТВИЯ КАК ПРЕДЫДУЩИЕ ДЛЯ СЛЕДУЮЩЕГО ШАГА ----------
        prevGas = clampedGas;
        prevSteer = clampedSteer;
        previousGripCommand = (int)gripCommand;

        // ---------- ПРОВЕРКИ ЗАВЕРШЕНИЯ ЭПИЗОДА ----------

        // 7. Успешный захват мяча – главная положительная цель
        if (!useRealRobotIo && commandResult.IsGrabbing)
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
