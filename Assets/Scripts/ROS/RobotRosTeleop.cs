using RosMessageTypes.Geometry;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Team11.Ros
{
    /// <summary>
    /// Connects Unity to the ROS 1 TCP endpoint on the physical robot and publishes
    /// standard differential-drive commands. The component is installed automatically
    /// when Play Mode starts, so no scene or prefab changes are required.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class RobotRosTeleop : MonoBehaviour, IRobotCommandSink
    {
        private const string RobotIpAddress = "192.168.2.158";
        private const int RobotTcpPort = 10001;
        private const string CommandTopic = "/cmd_vel";
        private const string SensorDataTopic = "/sensor/data";

        private const float LinearSpeedMetersPerSecond = 0.15f;
        private const float AngularSpeedRadiansPerSecond = 0.8f;
        private const float PublishRateHz = 20f;
        private const float CommandTimeoutSeconds = 0.5f;
        private const float SensorTimeoutSeconds = 1f;

        private ROSConnection ros;
        private float nextPublishTime;
        private bool emergencyStop;
        private bool applicationHasFocus = true;
        private double ultrasonicMeters;
        private bool leftIrTriggered;
        private bool rightIrTriggered;
        private bool centerIrTriggered;
        private float lastSensorMessageTime = -1f;
        private RobotBrain robotBrain;
        private bool wasPublishingRealCommands;
        private RobotCommand latestCommand = RobotCommand.Stopped;
        private bool hasFreshCommand;
        private float lastCommandTime = -1f;
        private RobotGripperCommand lastGripperCommand = RobotGripperCommand.None;
        private RobotRosServoControl servoControl;

        // AfterSceneLoad (не BeforeSceneLoad) — чтобы к моменту проверки
        // RobotBrain.isTraining объекты сцены (и склонированные ArenaSpawner'ом
        // роботы) уже существовали и были доступны через FindAnyObjectByType.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotRosTeleop>() != null)
            {
                return;
            }

            // Во время обучения (сцена с RobotBrain.isTraining = true) не подключаемся
            // к реальному роботу вообще: иначе WASD, нажатый случайно во время
            // наблюдения за тренировкой, уйдёт как команда на физического робота.
            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
            {
                return;
            }

            var instance = new GameObject("Physical Robot ROS Teleop");
            DontDestroyOnLoad(instance);
            instance.AddComponent<RobotRosTeleop>();
        }

        private void Awake()
        {
            ros = ROSConnection.GetOrCreateInstance();
            EnsureCameraView(ros);
            ros.RosIPAddress = RobotIpAddress;
            ros.RosPort = RobotTcpPort;
            ros.ConnectOnStart = true;
            ros.ShowHud = true;
            ros.RegisterPublisher<TwistMsg>(CommandTopic, queue_size: 1);
            ros.Subscribe<QuaternionMsg>(SensorDataTopic, OnSensorData);
        }

        private static void EnsureCameraView(ROSConnection connection)
        {
            if (FindAnyObjectByType<RobotCameraView>() == null)
            {
                connection.gameObject.AddComponent<RobotCameraView>();
            }
        }

        private void Update()
        {
            bool useRealRobot = UsesRealRobotIo();
            if (!useRealRobot)
            {
                if (wasPublishingRealCommands)
                {
                    PublishStopNow();
                    wasPublishingRealCommands = false;
                }
                hasFreshCommand = false;
                lastGripperCommand = RobotGripperCommand.None;
                return;
            }

            wasPublishingRealCommands = true;
            var keyboard = Keyboard.current;

            if (keyboard != null)
            {
                // Escape, а не Space — Space занят захватом мяча в RobotBrain.Heuristic(),
                // при общей клавише ручной тест захвата в симуляции попутно
                // аварийно останавливал бы реального робота.
                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    emergencyStop = true;
                    PublishStopNow();
                }

                if (keyboard.enterKey.wasPressedThisFrame)
                {
                    emergencyStop = false;
                }
            }

            if (Time.unscaledTime < nextPublishTime)
            {
                return;
            }

            nextPublishTime = Time.unscaledTime + (1f / PublishRateHz);

            bool commandIsFresh = hasFreshCommand &&
                Time.unscaledTime - lastCommandTime <= CommandTimeoutSeconds;
            if (!applicationHasFocus || emergencyStop || !commandIsFresh)
            {
                PublishStopNow();
                return;
            }

            double linear = latestCommand.Linear * LinearSpeedMetersPerSecond;
            double angular = -latestCommand.Angular * AngularSpeedRadiansPerSecond;
            PublishCommand(linear, angular);
        }

        public RobotCommandResult ApplyCommand(RobotCommand command)
        {
            latestCommand = command;
            hasFreshCommand = true;
            lastCommandTime = Time.unscaledTime;

            if (!emergencyStop && applicationHasFocus &&
                command.Gripper != lastGripperCommand)
            {
                if (command.Gripper != RobotGripperCommand.None)
                {
                    ResolveServoControl()?.ApplyGripperCommand(command.Gripper);
                }

                lastGripperCommand = command.Gripper;
            }

            bool hasGripperState = TryGetGripperState(out bool isGripperOpen);
            return new RobotCommandResult(
                hasGripperState,
                isGripperOpen,
                false,
                RobotGripAttemptResult.None);
        }

        public void Stop()
        {
            latestCommand = RobotCommand.Stopped;
            hasFreshCommand = false;
            lastGripperCommand = RobotGripperCommand.None;
            if (wasPublishingRealCommands)
            {
                PublishStopNow();
            }
        }

        public bool TryGetGripperState(out bool isOpen)
        {
            RobotRosServoControl control = ResolveServoControl();
            if (control != null)
            {
                return control.TryGetCommandedClawState(out isOpen);
            }

            isOpen = false;
            return false;
        }

        private RobotRosServoControl ResolveServoControl()
        {
            if (servoControl == null)
            {
                servoControl = FindAnyObjectByType<RobotRosServoControl>();
            }

            return servoControl;
        }

        private void PublishCommand(double linear, double angular)
        {
            if (ros == null)
            {
                ros = ROSConnection.GetOrCreateInstance();
            }

            var topic = ros.GetTopic(CommandTopic);
            if (topic == null || !topic.IsPublisher)
            {
                ros.RegisterPublisher<TwistMsg>(CommandTopic, queue_size: 1);
            }

            var command = new TwistMsg(
                new Vector3Msg(linear, 0d, 0d),
                new Vector3Msg(0d, 0d, angular));

            ros.Publish(CommandTopic, command);
        }

        private void PublishStopNow()
        {
            PublishCommand(0d, 0d);
        }

        private void OnSensorData(QuaternionMsg message)
        {
            ultrasonicMeters = message.x;
            leftIrTriggered = message.y >= 0.5d;
            rightIrTriggered = message.z >= 0.5d;
            centerIrTriggered = message.w >= 0.5d;
            lastSensorMessageTime = Time.unscaledTime;
        }

        public bool TryGetFreshRobotSensors(
            out float freshUltrasonicMeters,
            out bool freshLeftIr,
            out bool freshRightIr,
            out bool freshCenterIr)
        {
            bool hasFreshData = lastSensorMessageTime >= 0f &&
                Time.unscaledTime - lastSensorMessageTime <= SensorTimeoutSeconds;

            freshUltrasonicMeters = hasFreshData ? (float)ultrasonicMeters : 0f;
            freshLeftIr = hasFreshData && leftIrTriggered;
            freshRightIr = hasFreshData && rightIrTriggered;
            freshCenterIr = hasFreshData && centerIrTriggered;
            return hasFreshData;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationHasFocus = hasFocus;

            if (!hasFocus && wasPublishingRealCommands)
            {
                PublishStopNow();
            }
        }

        private void OnDisable()
        {
            if (wasPublishingRealCommands)
            {
                PublishStopNow();
            }
        }

        private void OnApplicationQuit()
        {
            if (wasPublishingRealCommands)
            {
                PublishStopNow();
            }
        }

        private bool UsesRealRobotIo()
        {
            if (robotBrain == null)
            {
                robotBrain = FindAnyObjectByType<RobotBrain>();
            }

            return robotBrain != null && robotBrain.UseRealRobotIo;
        }

        private void OnGUI()
        {
            const int width = 440;
            const int height = 226;
            var panel = new Rect(Screen.width - width - 10, 10, width, height);

            if (robotBrain == null)
            {
                robotBrain = FindAnyObjectByType<RobotBrain>();
            }

            bool useRealSensors = robotBrain != null && robotBrain.UseRealRobotIo;
            string dataSource = useRealSensors ? "REAL ROBOT" : "SIMULATION";
            float displayUltrasonicMeters = 0f;
            bool displayLeftIr = false;
            bool displayRightIr = false;
            bool displayCenterIr = false;
            bool hasDisplayData = robotBrain != null && robotBrain.TryGetSelectedRangeSensors(
                out displayUltrasonicMeters,
                out displayLeftIr,
                out displayRightIr,
                out displayCenterIr);

            GUI.Box(panel, $"Sensors — {dataSource}");
            bool requestedRealSensors = GUI.Toggle(
                new Rect(panel.x + 12, panel.y + 22, width - 24, 20),
                useRealSensors,
                "Use real robot I/O for inference");
            if (robotBrain != null && requestedRealSensors != useRealSensors)
            {
                robotBrain.UseRealRobotIo = requestedRealSensors;
                useRealSensors = requestedRealSensors;
                dataSource = useRealSensors ? "REAL ROBOT" : "SIMULATION";
                hasDisplayData = robotBrain.TryGetSelectedRangeSensors(
                    out displayUltrasonicMeters,
                    out displayLeftIr,
                    out displayRightIr,
                    out displayCenterIr);
            }

            GUI.Label(new Rect(panel.x + 12, panel.y + 44, width - 24, 20),
                $"{RobotIpAddress}:{RobotTcpPort}  |  topic: {CommandTopic}");
            GUI.Label(new Rect(panel.x + 12, panel.y + 64, width - 24, 20),
                "Heuristic: WASD/arrows   Escape: E-STOP   Enter: reset E-STOP");

            string state = emergencyStop ? "E-STOP ACTIVE" : "ready";
            GUI.Label(new Rect(panel.x + 12, panel.y + 84, width - 24, 20), $"State: {state}");

            string sensorState = hasDisplayData
                ? $"Ultrasonic: {displayUltrasonicMeters:F2} m  |  " +
                  $"raw IR L:{(displayLeftIr ? 1 : 0)} " +
                  $"R:{(displayRightIr ? 1 : 0)} C:{(displayCenterIr ? 1 : 0)}"
                : $"{dataSource} sensors: no fresh data";
            GUI.Label(new Rect(panel.x + 12, panel.y + 106, width - 24, 20), sensorState);

            DrawUltrasonicBar(
                new Rect(panel.x + 12, panel.y + 128, width - 24, 10),
                hasDisplayData,
                displayUltrasonicMeters);

            const float indicatorWidth = 128f;
            const float indicatorGap = 12f;
            float indicatorsX = panel.x + 12;
            DrawIrIndicator(
                new Rect(indicatorsX, panel.y + 146, indicatorWidth, 28),
                "IR left",
                hasDisplayData,
                displayLeftIr);
            DrawIrIndicator(
                new Rect(indicatorsX + indicatorWidth + indicatorGap, panel.y + 146, indicatorWidth, 28),
                "IR right",
                hasDisplayData,
                displayRightIr);
            DrawIrIndicator(
                new Rect(indicatorsX + (indicatorWidth + indicatorGap) * 2f, panel.y + 146, indicatorWidth, 28),
                "IR center",
                hasDisplayData,
                displayCenterIr);

            (float angle, float areaRatio, float aspectRatio, bool visible) vision =
                (0f, 0f, 0f, false);
            bool hasVisionData = robotBrain != null && robotBrain.TryGetSelectedVision(out vision);
            string visionState = hasVisionData
                ? $"Vision: visible={(vision.visible ? 1 : 0)} | angle={vision.angle:+0.000;-0.000;0.000} | " +
                  $"area={vision.areaRatio:F4} | aspect={vision.aspectRatio:F3}"
                : $"Vision ({dataSource}): no fresh data";
            GUI.Label(new Rect(panel.x + 12, panel.y + 184, width - 24, 20), visionState);
        }

        private static void DrawUltrasonicBar(Rect rect, bool hasData, double distanceMeters)
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            if (hasData)
            {
                float normalizedDistance = Mathf.Clamp01((float)distanceMeters / 5f);
                GUI.color = Color.Lerp(
                    new Color(0.9f, 0.2f, 0.15f),
                    new Color(0.2f, 0.8f, 0.3f),
                    normalizedDistance);
                GUI.DrawTexture(
                    new Rect(rect.x, rect.y, rect.width * normalizedDistance, rect.height),
                    Texture2D.whiteTexture);
            }

            GUI.color = previousColor;
        }

        private static void DrawIrIndicator(Rect rect, string label, bool hasData, bool triggered)
        {
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = !hasData
                ? Color.gray
                : triggered
                    ? new Color(0.95f, 0.25f, 0.2f)
                    : new Color(0.2f, 0.75f, 0.3f);

            string value = !hasData ? "--" : triggered ? "BLOCKED" : "CLEAR";
            GUI.Box(rect, $"{label}: {value}");
            GUI.backgroundColor = previousBackground;
        }
    }
}
