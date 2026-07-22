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
/// Backend-independent robot command. Linear, angular and camera pan values are
/// normalized to [-1, 1]; camera pan is an absolute target, not a velocity.
/// </summary>
public readonly struct RobotCommand
{
    public RobotCommand(
        float linear,
        float angular,
        float cameraPanTarget,
        RobotGripperCommand gripper)
    {
        Linear = Mathf.Clamp(linear, -1f, 1f);
        Angular = Mathf.Clamp(angular, -1f, 1f);
        CameraPanTarget = Mathf.Clamp(cameraPanTarget, -1f, 1f);
        Gripper = gripper;
    }

    public float Linear { get; }
    public float Angular { get; }
    public float CameraPanTarget { get; }
    public RobotGripperCommand Gripper { get; }

    public static RobotCommand Stopped =>
        new RobotCommand(0f, 0f, 0f, RobotGripperCommand.None);
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
    bool TryGetCameraPanState(out float normalizedAngle);
}

public sealed class SimulationRobotCommandSink : IRobotCommandSink
{
    private readonly TrackController trackController;
    private readonly GripperController gripperController;
    private readonly CameraRotator cameraRotator;

    public SimulationRobotCommandSink(
        TrackController trackController,
        GripperController gripperController,
        CameraRotator cameraRotator)
    {
        this.trackController = trackController;
        this.gripperController = gripperController;
        this.cameraRotator = cameraRotator;
    }

    public RobotCommandResult ApplyCommand(RobotCommand command)
    {
        if (trackController != null)
        {
            trackController.Move(
                command.Linear * trackController.maxLinearCmd,
                command.Angular);
        }

        cameraRotator?.SetNormalizedTarget(command.CameraPanTarget);

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

    public bool TryGetCameraPanState(out float normalizedAngle)
    {
        normalizedAngle = cameraRotator != null
            ? cameraRotator.CurrentNormalized
            : 0f;
        return cameraRotator != null;
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
    public CameraRotator cameraRotator;
    public Transform gripperTransform;
    public Transform holdPoint;
    public Transform ball;

    [Header("Inference I/O source")]
    [Tooltip("Use the physical robot for both policy observations and policy commands.")]
    [FormerlySerializedAs("useRealRobotSensors")]
    [SerializeField] private bool useRealRobotIo;

    [Header("Policy observation normalization")]
    [Tooltip("Distance represented by ultrasonic observation value 1.0, in meters.")]
    [Min(0.01f)] public float ultrasonicObservationMaxMeters = 5f;

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

    [Header("Inference Debug")]
    [Tooltip("Логировать изменения команды захвата, Grip IR, триггера и состояния клешни.")]
    public bool logGripperInferenceDebug;

    [Tooltip("Опционально: если назначен, периодически генерирует новые стены/путь/препятствия и переставляет робота и мяч, вместо возврата на фиксированную стартовую позицию")]
    public EnvironmentManager environmentManager;

    [Tooltip("Через сколько эпизодов перегенерировать арену (новый путь/препятствия). 1 = каждый эпизод. Остальные эпизоды между перегенерациями просто возвращают робота и мяч на те же позиции, что и в последней сгенерированной конфигурации")]
    public int regenerateEveryEpisodes = 10;

    private int episodesUntilRegenerate = 0;

    // ==================== ОСНОВНЫЕ НАГРАДЫ И ШТРАФЫ ====================
    [Header("Основные награды и штрафы")]
    [Tooltip("Штраф за подтверждённый физическим движком контакт с препятствием")]
    public float wallPenalty = -0f;

    [Tooltip("Небольшой отдельный штраф за приближение к препятствию по ультразвуку. 0 отключает shaping близости.")]
    public float wallProximityPenalty = 0f;

    [Header("Регистрация столкновений")]
    [Tooltip("Слои объектов, контакт с которыми считается столкновением с препятствием")]
    public LayerMask collisionObstacleMask = 1 << 8;

    [Tooltip("Минимальный интервал между штрафами при продолжительном контакте с одним или несколькими препятствиями")]
    [Min(0f)] public float collisionPenaltyCooldown = 0.5f;

    [Tooltip("Множитель награды за сокращение дистанции до мяча (за каждый метр приближения)")]
    public float moveRewardScale = 0.01f;

    [Tooltip("Награда за успешный захват мяча (завершает эпизод)")]
    public float gripReward = 1f;

    [Tooltip("Штраф при выходе за пределы арены или падении")]
    public float outOfBoundsPenalty = -0.5f;

    // ==================== ГРАНИЦЫ СРЕДЫ ====================
    [Header("Границы среды")]
    [Tooltip("Порог падения по оси Y (ниже которого эпизод завершается)")]
    public float fallDistance = 3f;

    [Tooltip("Запас за физическими стенами для аварийного завершения при вылете из арены")]
    [Min(0f)] public float arenaEscapeMargin = 2f;

    // ==================== ОГРАНИЧЕНИЯ ЭПИЗОДА ====================
    [Header("Ограничения эпизода")]
    [Tooltip("Количество шагов без обнаружения, при котором соответствующее наблюдение достигает 1. Эпизод этим счётчиком не завершается.")]
    [Min(1f)]
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

    [FormerlySerializedAs("angleRewardScale")]
    [Tooltip("Масштаб потенциальной награды за улучшение положения цели в кадре")]
    [Min(0f)] public float cameraRewardScale = 0.02f;

    [Tooltip("Нормализованная мёртвая зона около центра кадра, в которой camera score равен 1")]
    [Range(0f, 0.99f)] public float cameraAngleDeadZone = 0.05f;

    [Tooltip("Максимальный модуль camera reward за одно новое измерение. 0 отключает ограничение.")]
    [Min(0f)] public float cameraRewardClamp = 0.02f;

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

    // ==================== ДОМЕННАЯ РАНДОМИЗАЦИЯ (ДИНАМИКА) ====================
    [Header("Domain Randomization")]
    [Tooltip("Master switch for camera, robot spawn and ball spawn randomization.")]
    public bool isTraining = true;

    [Tooltip("Случайная масса корпуса (1.0-4.0) в начале каждого эпизода — для устойчивости к неточной модели инерции")]
    public bool randomizeMass = true;

    [Tooltip("Случайные moveSpeed/turnSpeed привода в начале каждого эпизода")]
    public bool randomizeMotorParams = true;

    [Tooltip("Случайная скорость сервопривода поворота камеры (град/с) в начале каждого эпизода")]
    public bool randomizeCameraServoSpeed = true;

    [Tooltip("Шум ±5% на нормализованное показание ультразвука")]
    public bool addUltrasonicNoise = true;

    [Tooltip("Слепая зона камеры на несколько шагов при быстром вращении робота — имитирует смаз/потерю трекинга YOLO")]
    public bool enableBurstDropout = true;

    [Tooltip("Задержка применения команд газа/руля на несколько шагов — имитирует связь с реальным роботом")]
    public bool enableCommandLatency = true;

    // ==================== ОТКАЗЫ ДАТЧИКОВ (SENSOR FAULTS) ====================
    [Header("Sensor Faults")]
    [Tooltip("С вероятностью irFaultProbability инвертирует показание ИК-датчика за шаг")]
    public bool enableIRFaults = true;
    [Range(0f, 1f)] public float irFaultProbability = 0.1f;

    [Tooltip("С вероятностью ultrasonicFaultProbability подменяет показание ультразвука случайным (имитация полного отказа датчика)")]
    public bool enableUltrasonicFaults = true;
    [Range(0f, 1f)] public float ultrasonicFaultProbability = 0.15f;

    private Queue<float[]> actionLatencyBuffer = new Queue<float[]>();
    private int currentActionLatencySteps = 0;
    private int burstDropoutRemaining = 0;

    [Tooltip("Start each simulated training episode with the shared camera/ultrasonic pivot at a random safe angle.")]
    public bool randomizeCameraPanOnEpisodeBegin = true;

    // ==================== ПРИВАТНЫЕ ПОЛЯ ====================
    private Rigidbody rb;
    private Vector3 startPos;
    private Vector3 initialStartPos;
    private Quaternion startRot;
    private Vector3 ballStartPos;
    private Vector3 initialBallStartPos;
    private Quaternion ballStartRot;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float lastDistanceToBall;
    private bool targetVisible;
    private float stepsSinceLastDetection;
    private float lastKnownAngle;
    private float lastKnownAreaRatio;
    private float lastKnownAspectRatio;
    private float prevGas;
    private float prevSteer;
    private float previousCameraPanTarget;
    private float previousCameraScore;
    private bool previousCameraVisible;
    private bool cameraMeasurementPending;
    private bool hasSeenCameraMeasurement;
    private ulong lastCameraMeasurementVersion;
    private float cameraScoreSum;
    private int cameraMeasurementCount;
    private int targetAcquiredCount;
    private int targetLostCount;
    private float cameraPanCurrentSum;
    private float cameraPanTargetSum;
    private float cameraPanMovementSum;
    private float previousMeasuredCameraPan;
    private int cameraPanSampleCount;
    private int cameraPanAtLimitCount;
    private bool grabZoneRewardGranted;
    private int previousGripCommand = 0; // Предыдущая команда клешни (0 – ничего, 1 – захват, 2 – отпустить)
    private Vector2 arenaHalfExtents = new Vector2(15f, 30f);

    // Статистика эпизода для TensorBoard
    private float cameraRewardSum;
    private int episodeStepCounter;
    private bool statsSent;
    private RobotRosTeleop robotSensorReceiver;
    private RealVision realVision;
    private DiagnosticLogger diagLogger;
    private int arenaIndex;
    private SimulationRobotCommandSink simulationCommandSink;
    private int pendingCollisionPenalties;
    private float nextCollisionPenaltyTime;
    private bool hasGripperDebugSnapshot;
    private RobotGripperCommand lastDebugGripCommand;
    private RobotGripperCommand lastDebugAppliedGripCommand;
    private RobotGripAttemptResult lastDebugGripAttempt;
    private bool lastDebugTargetInTrigger;
    private bool lastDebugGripperIr;
    private bool lastDebugIsOpen;
    private bool lastDebugIsGrabbing;
    private RobotGripperCommand pendingHeuristicGripCommand;
    private RobotGripperCommand lastAppliedGripCommand;
    private float heuristicCameraPanTarget;

    // ==================== МЕТОДЫ ЖИЗНЕННОГО ЦИКЛА ====================

    protected override void Awake()
    {
        base.Awake();
        TrainingConfig.ApplyOverrides(this, "RobotBrain");
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
        if (cameraRotator == null) cameraRotator = GetComponentInChildren<CameraRotator>(true);
        if (collisionObstacleMask == 0)
            collisionObstacleMask = LayerMask.GetMask("Obstacle");
        simulationCommandSink = new SimulationRobotCommandSink(
            trackController,
            gripperTransform?.GetComponent<GripperController>(),
            cameraRotator);
    }

    public override void Initialize()
    {
        diagLogger = GetComponent<DiagnosticLogger>();
        arenaIndex = ResolveArenaIndex();
        diagLogger?.SetArenaIndex(arenaIndex);

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
            ballRb = ball.GetComponent<Rigidbody>();
            ballCollider = ball.GetComponent<Collider>();
            lastDistanceToBall = holdPoint != null
                ? Vector3.Distance(holdPoint.position, ball.position)
                : 0f;
        }

        cameraRewardSum = 0f;
        episodeStepCounter = 0;
        statsSent = false;
        cameraScoreSum = 0f;
        cameraMeasurementCount = 0;
        targetAcquiredCount = 0;
        targetLostCount = 0;
        cameraPanCurrentSum = 0f;
        cameraPanTargetSum = 0f;
        cameraPanMovementSum = 0f;
        previousMeasuredCameraPan = cameraRotator != null
            ? cameraRotator.CurrentNormalized
            : 0f;
        cameraPanSampleCount = 0;
        cameraPanAtLimitCount = 0;
        previousCameraPanTarget = previousMeasuredCameraPan;
        heuristicCameraPanTarget = previousCameraPanTarget;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        // Heuristic вызывается только на шагах принятия решения. Буфер сохраняет
        // короткое нажатие до ближайшего решения агента.
        if (keyboard.gKey.wasPressedThisFrame)
            pendingHeuristicGripCommand = RobotGripperCommand.Grab;
        else if (keyboard.rKey.wasPressedThisFrame)
            pendingHeuristicGripCommand = RobotGripperCommand.Release;

        if (keyboard.cKey.wasPressedThisFrame)
            heuristicCameraPanTarget = 0f;
    }

    private static bool IsLoadedSceneObject(Transform target)
    {
        return target != null && target.gameObject.scene.IsValid() && target.gameObject.scene.isLoaded;
    }

    // ArenaSpawner группирует каждую копию арены под корнем "Arena_{N}" и
    // парentит робота прямо под ним (см. ArenaSpawner.WireArena), поэтому
    // transform.parent.name у робота — это и есть его арена. Без ArenaSpawner
    // в сцене (одиночный тест) вернёт -1.
    private int ResolveArenaIndex()
    {
        const string prefix = "Arena_";
        Transform parent = transform.parent;
        if (parent != null && parent.name.StartsWith(prefix) &&
            int.TryParse(parent.name.Substring(prefix.Length), out int index))
        {
            return index;
        }

        return -1;
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

    private bool TryGetSelectedCameraPanState(out float normalizedAngle)
    {
        IRobotCommandSink commandSink = GetSelectedCommandSink();
        if (commandSink != null)
        {
            return commandSink.TryGetCameraPanState(out normalizedAngle);
        }

        normalizedAngle = 0f;
        return false;
    }

    public override void OnEpisodeBegin()
    {
        if (!useRealRobotIo)
        {
            if (isTraining)
            {
                yoloCamera?.RandomizeDomainParameters();
                sensors?.RandomizeSensorMounting();
            }
            else
            {
                yoloCamera?.ResetDomainParameters();
                sensors?.ResetSensorMounting();
            }
        }

        // Сброс статистики
        cameraRewardSum = 0f;
        episodeStepCounter = 0;
        statsSent = false;
        cameraScoreSum = 0f;
        cameraMeasurementCount = 0;
        targetAcquiredCount = 0;
        targetLostCount = 0;
        cameraPanCurrentSum = 0f;
        cameraPanTargetSum = 0f;
        cameraPanMovementSum = 0f;
        cameraPanSampleCount = 0;
        cameraPanAtLimitCount = 0;
        pendingCollisionPenalties = 0;
        nextCollisionPenaltyTime = Time.fixedTime;

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
            // Без EnvironmentManager возвращаем робота в сохранённую стартовую позу.
            rb.position = startPos;
            rb.rotation = startRot;
        }

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // Доменная рандомизация динамики — только в симуляции и только при обучении,
        // реальный робот не подчиняется этим полям.
        if (!useRealRobotIo && isTraining)
        {
            if (randomizeMass)
                rb.mass = UnityEngine.Random.Range(2.0f, 3.0f);

            if (randomizeMotorParams && trackController != null)
            {
                trackController.moveSpeed = UnityEngine.Random.Range(4.5f, 7.5f);
                trackController.turnSpeed = UnityEngine.Random.Range(100f, 140f);
            }

            if (randomizeCameraServoSpeed && cameraRotator != null)
                cameraRotator.SetServoSpeed(UnityEngine.Random.Range(80f, 200f));

            if (enableCommandLatency)
            {
                currentActionLatencySteps = UnityEngine.Random.Range(8, 14);
                actionLatencyBuffer.Clear();
                for (int i = 0; i < currentActionLatencySteps; i++)
                    actionLatencyBuffer.Enqueue(new float[] { 0f, 0f, 0f });
            }
            else
            {
                currentActionLatencySteps = 0;
                actionLatencyBuffer.Clear();
            }
        }
        else
        {
            currentActionLatencySteps = 0;
            actionLatencyBuffer.Clear();
        }

        burstDropoutRemaining = 0;

        targetVisible = false;
        stepsSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownAreaRatio = 0f;
        lastKnownAspectRatio = 0f;
        prevGas = 0f;
        prevSteer = 0f;
        previousCameraScore = 0f;
        previousCameraVisible = false;
        cameraMeasurementPending = false;
        hasSeenCameraMeasurement = false;
        lastCameraMeasurementVersion = 0;
        grabZoneRewardGranted = false;
        lastDistanceToBall = ball != null && holdPoint != null
            ? Vector3.Distance(holdPoint.position, ball.position)
            : 0f;
        previousGripCommand = 0;
        hasGripperDebugSnapshot = false;
        pendingHeuristicGripCommand = RobotGripperCommand.None;
        lastAppliedGripCommand = RobotGripperCommand.None;

        if (!useRealRobotIo && cameraRotator != null)
        {
            float episodeStartPan = isTraining && randomizeCameraPanOnEpisodeBegin
                ? Random.Range(-1f, 1f)
                : 0f;
            cameraRotator.ResetPan(episodeStartPan);
        }

        TryGetSelectedCameraPanState(out float currentCameraPan);
        previousCameraPanTarget = currentCameraPan;
        heuristicCameraPanTarget = currentCameraPan;
        previousMeasuredCameraPan = currentCameraPan;
    }

    private void ResetBall()
    {
        if (ball == null)
            return;

        // Родитель мяча = родитель робота (корень его собственной арены), а не
        // отдельно закэшированное значение — тот кэш (ballStartParent) на практике
        // оказывался равен Arena_0 у клонированных ArenaSpawner'ом арен, из-за
        // чего каждый ResetBall() утаскивал мяч в чужую арену. transform.parent
        // у робота никогда не перепривязывается после создания, так что он
        // надёжен как источник истины.
        ball.SetParent(transform.parent);
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
        out bool gripperMountedIrTriggered)
    {
        if (!useRealRobotIo)
        {
            if (sensors != null)
            {
                return sensors.TryReadSimulationSensors(
                    out ultrasonicMeters,
                    out leftIrTriggered,
                    out rightIrTriggered,
                    out gripperMountedIrTriggered);
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
                    out gripperMountedIrTriggered);
            }
        }

        ultrasonicMeters = 0f;
        leftIrTriggered = false;
        rightIrTriggered = false;
        gripperMountedIrTriggered = false;
        return false;
    }

    private SimulatedYoloCamera GetSelectedVisionSource()
    {
        if (useRealRobotIo)
        {
            if (realVision == null)
                realVision = FindAnyObjectByType<RealVision>();

            return realVision;
        }

        return yoloCamera;
    }

    public bool TryGetSelectedVision(
        out (float angle, float areaRatio, float aspectRatio, bool visible) targetInfo)
    {
        SimulatedYoloCamera selectedVision = GetSelectedVisionSource();

        if (selectedVision == null)
        {
            targetInfo = (0f, 0f, 0f, false);
            return false;
        }

        if (useRealRobotIo && selectedVision is RealVision selectedRealVision &&
            !selectedRealVision.HasFreshPacket)
        {
            targetInfo = (0f, 0f, 0f, false);
            return false;
        }

        targetInfo = selectedVision.GetTargetInfo();
        return true;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1. Ультразвук: физические метры преобразуются в 0..1; ИК — бинарные.
        TryGetSelectedRangeSensors(
            out float ultrasonicMeters,
            out bool leftIr,
            out bool rightIr,
            out bool gripperMountedIr);

        // Шум/отказы датчиков — только в симуляции при обучении, реальный робот
        // и так даёт зашумлённые/неидеальные показания сам по себе.
        bool simulateSensorNoise = !useRealRobotIo && isTraining;
        if (simulateSensorNoise)
        {
            float maxUltrasonicMeters = Mathf.Max(0.01f, ultrasonicObservationMaxMeters);
            if (addUltrasonicNoise)
                ultrasonicMeters += UnityEngine.Random.Range(-0.05f, 0.05f) * maxUltrasonicMeters;
            if (enableUltrasonicFaults && UnityEngine.Random.value < ultrasonicFaultProbability)
                ultrasonicMeters = UnityEngine.Random.Range(0f, maxUltrasonicMeters);
            ultrasonicMeters = Mathf.Clamp(ultrasonicMeters, 0f, maxUltrasonicMeters);

            if (enableIRFaults)
            {
                if (UnityEngine.Random.value < irFaultProbability) leftIr = !leftIr;
                if (UnityEngine.Random.value < irFaultProbability) rightIr = !rightIr;
                if (UnityEngine.Random.value < irFaultProbability) gripperMountedIr = !gripperMountedIr;
            }
        }

        float normalizedUltrasonic = Mathf.Clamp01(
            ultrasonicMeters / Mathf.Max(0.01f, ultrasonicObservationMaxMeters));
        sensor.AddObservation(normalizedUltrasonic);
        sensor.AddObservation(leftIr ? 1f : 0f);
        sensor.AddObservation(rightIr ? 1f : 0f);
        sensor.AddObservation(gripperMountedIr ? 1f : 0f);

        // 2. Информация о цели с YOLO-камеры (угол, площадь, видимость)
        SimulatedYoloCamera selectedVision = GetSelectedVisionSource();
        TryGetSelectedVision(out var targetInfo);

        if (selectedVision != null)
        {
            ulong measurementVersion = selectedVision.MeasurementVersion;
            bool sourceHasMeasurement = !(selectedVision is RealVision) || measurementVersion > 0;
            if (sourceHasMeasurement &&
                (!hasSeenCameraMeasurement || measurementVersion != lastCameraMeasurementVersion))
            {
                cameraMeasurementPending = true;
                hasSeenCameraMeasurement = true;
                lastCameraMeasurementVersion = measurementVersion;
            }
        }

        // Burst dropout: при резком вращении с шансом 15% на несколько шагов
        // "слепим" камеру — имитирует смаз кадра/потерю трекинга YOLO при повороте.
        if (burstDropoutRemaining > 0)
            burstDropoutRemaining--;
        if (simulateSensorNoise && enableBurstDropout && rb.angularVelocity.magnitude > 0.5f &&
            UnityEngine.Random.value < 0.15f)
        {
            burstDropoutRemaining = UnityEngine.Random.Range(5, 16);
        }
        bool effectiveVisible = targetInfo.visible && burstDropoutRemaining == 0;

        targetVisible = effectiveVisible;
        if (effectiveVisible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownAreaRatio = targetInfo.areaRatio;
            lastKnownAspectRatio = targetInfo.aspectRatio;
            stepsSinceLastDetection = 0f;
        }

        sensor.AddObservation(effectiveVisible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(effectiveVisible ? targetInfo.areaRatio : lastKnownAreaRatio);
        sensor.AddObservation(effectiveVisible ? targetInfo.aspectRatio : lastKnownAspectRatio);
        sensor.AddObservation(effectiveVisible ? 1f : 0f);

        // 3. Состояние клешни: закрыта = +1, открыта = -1, неизвестно = 0.
        bool hasGripperState = TryGetSelectedGripperState(out bool isGripperOpen);
        sensor.AddObservation(hasGripperState
            ? (isGripperOpen ? -1f : 1f)
            : 0f);

        // 4. Доля шагов без обнаружения цели
        sensor.AddObservation(Mathf.Clamp01(stepsSinceLastDetection / Mathf.Max(1f, noDetectionSteps)));

        // ----- 5. ПРЕДЫДУЩИЕ ДЕЙСТВИЯ (только один предыдущий шаг) -----
        // Предыдущие газ и руль в едином policy-диапазоне -1..1.
        float maxLinearCommand = trackController != null
            ? Mathf.Max(Mathf.Epsilon, trackController.maxLinearCmd)
            : 1f;
        sensor.AddObservation(Mathf.Clamp(prevGas / maxLinearCommand, -1f, 1f));
        sensor.AddObservation(prevSteer);
        // Команда клешни: Grab/закрыть = +1, Release/открыть = -1, None = 0.
        sensor.AddObservation(EncodeGripperCommandObservation(
            (RobotGripperCommand)previousGripCommand));

        // Active perception: append the current shared camera/ultrasonic pan
        // direction and previous absolute target after the legacy 13 values.
        bool hasCameraPanState = TryGetSelectedCameraPanState(out float currentCameraPan);
        sensor.AddObservation(hasCameraPanState ? currentCameraPan : 0f);
        sensor.AddObservation(previousCameraPanTarget);
    }

    private static float EncodeGripperCommandObservation(RobotGripperCommand command)
    {
        return command switch
        {
            RobotGripperCommand.Grab => 1f,
            RobotGripperCommand.Release => -1f,
            _ => 0f
        };
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        // Grab больше не выбор политики ни при каких условиях — захват происходит
        // только рефлекторно, по гриппер-ИК (см. OnActionReceived). Действие
        // остаётся в пространстве действий (не меняем размер branch), но агенту
        // недоступно постоянно.
        actionMask.SetActionEnabled(
            branch: 0,
            actionIndex: (int)RobotGripperCommand.Grab,
            isEnabled: false);

        bool hasGripperState = TryGetSelectedGripperState(out bool isGripperOpen);
        bool canRelease = hasGripperState && !isGripperOpen;

        actionMask.SetActionEnabled(
            branch: 0,
            actionIndex: (int)RobotGripperCommand.Release,
            isEnabled: canRelease);
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
        float cameraPanTarget = Mathf.Clamp(actions.ContinuousActions[2], -1f, 1f);
        RobotGripperCommand gripCommand = (RobotGripperCommand)Mathf.Clamp(
            actions.DiscreteActions[0],
            (int)RobotGripperCommand.None,
            (int)RobotGripperCommand.Release);

        // Grab у политики замаскирован в WriteDiscreteActionMask, но на случай
        // ручного/эвристического ввода (не проходит через маску) всё равно
        // трактуем его как None — единственный источник Grab ниже.
        if (gripCommand == RobotGripperCommand.Grab)
            gripCommand = RobotGripperCommand.None;

        // Рефлекторный захват по гриппер-ИК — единственный способ закрыть
        // клешню на мяче, политика в этом решении не участвует. Источник
        // датчика (симуляция/реальный робот) определяется тем же способом,
        // что и в WriteDiscreteActionMask. HasTargetInTrigger — тот же
        // резервный физический триггер, что раньше учитывался в маске
        // действий, на случай если IR-слой мяч не поймает.
        if (gripCommand == RobotGripperCommand.None &&
            TryGetSelectedGripperState(out bool isGripperOpenNow) && isGripperOpenNow)
        {
            bool gripperIrTriggeredNow = TryGetSelectedRangeSensors(
                out _, out _, out _, out bool irTriggered) && irTriggered;

            bool targetInTriggerNow = !useRealRobotIo &&
                gripperTransform != null &&
                gripperTransform.TryGetComponent(out GripperController simulationGripperNow) &&
                simulationGripperNow.HasTargetInTrigger;

            if (gripperIrTriggeredNow || targetInTriggerNow)
                gripCommand = RobotGripperCommand.Grab;
        }

        // DecisionRequester повторяет последнее действие между решениями. Команды
        // сервопривода трактуем как события перехода, а не как команду на каждом
        // физическом шаге — это совпадает с поведением реального ROS-драйвера.
        RobotGripperCommand appliedGripCommand = RobotGripperCommand.None;
        if (gripCommand == RobotGripperCommand.None)
        {
            lastAppliedGripCommand = RobotGripperCommand.None;
        }
        else if (gripCommand != lastAppliedGripCommand)
        {
            appliedGripCommand = gripCommand;
            lastAppliedGripCommand = gripCommand;
        }

        // Применяем клиппинг газа с учётом maxLinearCmd (как в TrackController)
        float maxLinearCommand = trackController != null
            ? Mathf.Max(0f, trackController.maxLinearCmd)
            : 0f;
        float clampedGas = Mathf.Clamp(gas, -maxLinearCommand, maxLinearCommand);
        float clampedSteer = Mathf.Clamp(steer, -1f, 1f);

        float normalizedLinear = maxLinearCommand > Mathf.Epsilon
            ? clampedGas / maxLinearCommand
            : 0f;

        bool targetInTrigger = false;
        bool gripperIrTriggered = false;
        bool gripperOpenBeforeCommand = false;
        if (logGripperInferenceDebug)
        {
            GripperController simulationGripper = gripperTransform?.GetComponent<GripperController>();
            targetInTrigger = !useRealRobotIo &&
                simulationGripper != null &&
                simulationGripper.HasTargetInTrigger;
            gripperIrTriggered = !useRealRobotIo &&
                sensors != null &&
                sensors.GetGripperIR() > 0.5f;
            TryGetSelectedGripperState(out gripperOpenBeforeCommand);
        }

        // Задержка команд: кладём свежее действие в очередь и берём то, что было
        // отправлено currentActionLatencySteps шагов назад — имитирует связь с
        // реальным роботом. Награды/наблюдения по-прежнему считаются от текущего
        // (незадержанного) действия политики, задерживается только его исполнение.
        // Поворот камеры задерживаем тем же способом, что и газ/руль — это тоже
        // реальный привод со своей связью, а не мгновенно отрабатывающая команда.
        float appliedLinear = normalizedLinear;
        float appliedSteer = clampedSteer;
        float appliedCameraPanTarget = cameraPanTarget;
        if (currentActionLatencySteps > 0)
        {
            actionLatencyBuffer.Enqueue(new float[] { normalizedLinear, clampedSteer, cameraPanTarget });
            float[] delayedAction = actionLatencyBuffer.Dequeue();
            appliedLinear = delayedAction[0];
            appliedSteer = delayedAction[1];
            appliedCameraPanTarget = delayedAction[2];
        }

        var robotCommand = new RobotCommand(
            appliedLinear,
            appliedSteer,
            appliedCameraPanTarget,
            appliedGripCommand);
        RobotCommandResult commandResult = GetSelectedCommandSink()?.ApplyCommand(robotCommand)
            ?? RobotCommandResult.Unavailable;

        bool hasCameraPanState = TryGetSelectedCameraPanState(out float currentCameraPan);
        if (hasCameraPanState)
        {
            cameraPanCurrentSum += currentCameraPan;
            cameraPanTargetSum += cameraPanTarget;
            cameraPanMovementSum += Mathf.Abs(currentCameraPan - previousMeasuredCameraPan);
            cameraPanSampleCount++;
            if (Mathf.Abs(currentCameraPan) >= 0.98f)
                cameraPanAtLimitCount++;
            previousMeasuredCameraPan = currentCameraPan;
        }

        if (logGripperInferenceDebug)
        {
            LogGripperInferenceState(
                gripCommand,
                appliedGripCommand,
                commandResult,
                gripperOpenBeforeCommand,
                targetInTrigger,
                gripperIrTriggered);
        }

        // ---------- НАГРАДЫ И ШТРАФЫ (каждый шаг) ----------

        // 1. Штраф за каждый шаг
        AddRewardWithStats("StepPenalty", stepPenalty);

        // Контакты приходят из физического шага, поэтому применяем накопленные события
        // здесь, чтобы награды и статистика оставались синхронизированы с шагом агента.
        while (pendingCollisionPenalties > 0)
        {
            AddRewardWithStats("ObstacleCollision", wallPenalty);
            pendingCollisionPenalties--;
        }

        // 2. Штраф за резкие изменения управления (используем предыдущие значения)
        float gasDelta = Mathf.Abs(clampedGas - prevGas);
        float steerDelta = Mathf.Abs(clampedSteer - prevSteer);
        float smoothnessPenalty = -smoothnessPenaltyScale * (gasDelta + steerDelta);
        AddRewardWithStats("SmoothnessPenalty", smoothnessPenalty);

        // 3. Потенциальная награда за положение цели в кадре. Рассчитывается только
        // один раз для каждого нового измерения камеры, а не для повторяемых действий.
        if (cameraMeasurementPending)
        {
            cameraMeasurementPending = false;

            float currentCameraScore = 0f;
            if (targetVisible)
            {
                float absoluteAngle = Mathf.Abs(Mathf.Clamp(lastKnownAngle, -1f, 1f));
                float normalizedError = Mathf.InverseLerp(cameraAngleDeadZone, 1f, absoluteAngle);
                currentCameraScore = 1f - normalizedError;
            }

            float cameraReward = cameraRewardScale * (currentCameraScore - previousCameraScore);
            if (cameraRewardClamp > 0f)
                cameraReward = Mathf.Clamp(cameraReward, -cameraRewardClamp, cameraRewardClamp);

            AddRewardWithStats("CameraReward", cameraReward);
            cameraRewardSum += cameraReward;
            cameraScoreSum += currentCameraScore;
            cameraMeasurementCount++;

            if (targetVisible && !previousCameraVisible)
                targetAcquiredCount++;
            else if (!targetVisible && previousCameraVisible)
                targetLostCount++;

            previousCameraScore = currentCameraScore;
            previousCameraVisible = targetVisible;
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

        // 6. Отдельный shaping-штраф за близость к стенам (ультразвук)
        if (!Mathf.Approximately(wallProximityPenalty, 0f) &&
            TryGetSelectedRangeSensors(
                out float rewardUltrasonicMeters,
                out _,
                out _,
                out _) &&
            rewardUltrasonicMeters < 0.5f)
            AddRewardWithStats("WallProximity", wallProximityPenalty);

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
        previousCameraPanTarget = cameraPanTarget;
        previousGripCommand = (int)gripCommand;

        // ---------- ДИАГНОСТИЧЕСКОЕ ЛОГИРОВАНИЕ (Sim-to-Real) ----------
        if (diagLogger != null)
        {
            TryGetSelectedRangeSensors(
                out float diagUltrasonic,
                out bool diagLeftIr,
                out bool diagRightIr,
                out _);
            float diagGripperIr = sensors != null ? sensors.GetGripperIR() : 0f;

            diagLogger.LogStep(
                StepCount, arenaIndex,
                targetVisible, lastKnownAngle, lastDistanceToBall,
                diagUltrasonic, diagLeftIr ? 1 : 0, diagRightIr ? 1 : 0, Mathf.RoundToInt(diagGripperIr),
                cameraPanTarget,
                clampedGas, clampedSteer, commandResult.IsGrabbing,
                rb.position.x - startPos.x, rb.position.z - startPos.z,
                transform.eulerAngles.y / 360f, rb.linearVelocity.magnitude);
        }

        // ---------- ПРОВЕРКИ ЗАВЕРШЕНИЯ ЭПИЗОДА ----------

        // 7. Успешный захват мяча – главная положительная цель
        if (!useRealRobotIo && commandResult.IsGrabbing)
        {
            AddRewardWithStats("GripReward", gripReward);
            SendStatsToTensorBoard(episodeSucceeded: true);
            EndEpisode();
            return;
        }

        // 8. Только аварийный вылет далеко за физические стены или падение.
        // Границы считаются от центра арены, а не от случайной стартовой позиции робота.
        Vector3 arenaCenter = environmentManager != null
            ? environmentManager.transform.position
            : initialStartPos;
        Vector3 displacementFromArenaCenter = rb.position - arenaCenter;
        bool escapedArena = Mathf.Abs(displacementFromArenaCenter.x) > arenaHalfExtents.x + arenaEscapeMargin
            || Mathf.Abs(displacementFromArenaCenter.z) > arenaHalfExtents.y + arenaEscapeMargin;
        bool fellFromFloor = rb.position.y < startPos.y - fallDistance;
        bool outsideArena = escapedArena || fellFromFloor;

        if (outsideArena)
        {
            AddRewardWithStats("OutOfBounds", outOfBoundsPenalty);
            SendStatsToTensorBoard(episodeSucceeded: false);
            EndEpisode();
            return;
        }

        // MaxStep завершается внутри ML-Agents сразу после OnActionReceived.
        // Отправляем пользовательскую статистику до автоматического сброса награды.
        if (MaxStep > 0 && StepCount >= MaxStep)
            SendStatsToTensorBoard(episodeSucceeded: false);
    }

    private void OnCollisionEnter(Collision collision)
    {
        RegisterObstacleContact(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        RegisterObstacleContact(collision);
    }

    private void RegisterObstacleContact(Collision collision)
    {
        if (useRealRobotIo || collision == null || collision.collider == null)
            return;

        int otherLayerBit = 1 << collision.collider.gameObject.layer;
        if ((collisionObstacleMask.value & otherLayerBit) == 0)
            return;

        if (Time.fixedTime < nextCollisionPenaltyTime)
            return;

        pendingCollisionPenalties++;
        nextCollisionPenaltyTime = Time.fixedTime + collisionPenaltyCooldown;
    }

    private void LogGripperInferenceState(
        RobotGripperCommand gripCommand,
        RobotGripperCommand appliedGripCommand,
        RobotCommandResult commandResult,
        bool gripperOpenBeforeCommand,
        bool targetInTrigger,
        bool gripperIrTriggered)
    {
        if (!logGripperInferenceDebug)
            return;

        bool stateChanged = !hasGripperDebugSnapshot ||
            gripCommand != lastDebugGripCommand ||
            appliedGripCommand != lastDebugAppliedGripCommand ||
            commandResult.GripAttempt != lastDebugGripAttempt ||
            targetInTrigger != lastDebugTargetInTrigger ||
            gripperIrTriggered != lastDebugGripperIr ||
            commandResult.IsGripperOpen != lastDebugIsOpen ||
            commandResult.IsGrabbing != lastDebugIsGrabbing;

        if (!stateChanged)
            return;

        float distanceToBall = ball != null && holdPoint != null
            ? Vector3.Distance(holdPoint.position, ball.position)
            : -1f;

        Debug.Log(
            $"[GripInference] agent={name}#{GetEntityId()} step={episodeStepCounter} " +
            $"action={gripCommand} applied={appliedGripCommand} " +
            $"trigger={(targetInTrigger ? 1 : 0)} " +
            $"ir={(gripperIrTriggered ? 1 : 0)} distance={distanceToBall:F3} " +
            $"openBefore={(gripperOpenBeforeCommand ? 1 : 0)} " +
            $"openAfter={(commandResult.IsGripperOpen ? 1 : 0)} " +
            $"grabbing={(commandResult.IsGrabbing ? 1 : 0)} " +
            $"attempt={commandResult.GripAttempt}",
            this);

        hasGripperDebugSnapshot = true;
        lastDebugGripCommand = gripCommand;
        lastDebugAppliedGripCommand = appliedGripCommand;
        lastDebugGripAttempt = commandResult.GripAttempt;
        lastDebugTargetInTrigger = targetInTrigger;
        lastDebugGripperIr = gripperIrTriggered;
        lastDebugIsOpen = commandResult.IsGripperOpen;
        lastDebugIsGrabbing = commandResult.IsGrabbing;
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

        const float heuristicPanStep = 0.1f;
        if (keyboard.qKey.isPressed) heuristicCameraPanTarget -= heuristicPanStep;
        if (keyboard.eKey.isPressed) heuristicCameraPanTarget += heuristicPanStep;
        heuristicCameraPanTarget = Mathf.Clamp(heuristicCameraPanTarget, -1f, 1f);

        // Клиппинг для соответствия с OnActionReceived
        float clampedGas = Mathf.Clamp(gas, -trackController.maxLinearCmd, trackController.maxLinearCmd);
        float clampedSteer = Mathf.Clamp(steer, -1f, 1f);

        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;
        continuousActions[0] = clampedGas;
        continuousActions[1] = clampedSteer;
        continuousActions[2] = heuristicCameraPanTarget;

        int gripCommand = (int)pendingHeuristicGripCommand;
        discreteActions[0] = gripCommand;
        pendingHeuristicGripCommand = RobotGripperCommand.None;

        // Обновляем историю для согласованности наблюдений в эвристическом режиме
        prevGas = clampedGas;
        prevSteer = clampedSteer;
        previousCameraPanTarget = heuristicCameraPanTarget;
        previousGripCommand = gripCommand;
    }

    // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ДЛЯ СТАТИСТИКИ ====================

    private void AddRewardWithStats(string type, float reward)
    {
        AddReward(reward);

        // Sum/Count теперь считаются напрямую по шагам средствами самого
        // ML-Agents (StatAggregationMethod.Sum суммирует все значения,
        // добавленные за окно summary_freq), а не по словарю, сбрасываемому
        // каждый эпизод — то есть без привязки к границам эпизодов. Avg
        // сознательно убран: он был средним ПО ЭПИЗОДАМ, что здесь больше
        // не имеет смысла.
        var statsRecorder = Academy.Instance.StatsRecorder;
        if (statsRecorder == null) return;

        statsRecorder.Add($"Rewards/{type}_Sum", reward, StatAggregationMethod.Sum);
        statsRecorder.Add($"Rewards/{type}_Count", 1f, StatAggregationMethod.Sum);
    }

    private void SendStatsToTensorBoard(bool episodeSucceeded)
    {
        if (statsSent) return;
        statsSent = true;

        var statsRecorder = Academy.Instance.StatsRecorder;
        if (statsRecorder == null) return;

        statsRecorder.Add("Rewards/TotalEpisodeReward", GetCumulativeReward());
        statsRecorder.Add("Rewards/EpisodeLength", episodeStepCounter);
        // Успех = эпизод завершился взятием мяча (см. вызовы SendStatsToTensorBoard).
        // 0/1 на эпизод, StatAggregationMethod.Average сам даёт долю успешных
        // эпизодов за окно summary_freq — то есть готовый процент в [0, 1].
        // Префикс "Environment/" — чтобы попасть в ту же карточку TensorBoard,
        // что и штатные Environment/Cumulative Reward и Environment/Episode Length.
        statsRecorder.Add("Environment/Success Rate", episodeSucceeded ? 1f : 0f, StatAggregationMethod.Average);
        statsRecorder.Add(
            "Camera/Score",
            cameraMeasurementCount > 0 ? cameraScoreSum / cameraMeasurementCount : 0f);
        statsRecorder.Add("Camera/Reward", cameraRewardSum);
        statsRecorder.Add("Camera/Measurements", cameraMeasurementCount);
        statsRecorder.Add("Camera/TargetAcquired", targetAcquiredCount);
        statsRecorder.Add("Camera/TargetLost", targetLostCount);
        statsRecorder.Add(
            "CameraPan/Current",
            cameraPanSampleCount > 0 ? cameraPanCurrentSum / cameraPanSampleCount : 0f);
        statsRecorder.Add(
            "CameraPan/Target",
            cameraPanSampleCount > 0 ? cameraPanTargetSum / cameraPanSampleCount : 0f);
        statsRecorder.Add("CameraPan/Movement", cameraPanMovementSum);
        statsRecorder.Add(
            "CameraPan/AtLimitFraction",
            cameraPanSampleCount > 0
                ? (float)cameraPanAtLimitCount / cameraPanSampleCount
                : 0f);
    }
}
