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
    public sealed class RobotRosTeleop : MonoBehaviour
    {
        private const string RobotIpAddress = "192.168.2.158";
        private const int RobotTcpPort = 10000;
        private const string CommandTopic = "/cmd_vel";
        private const string SensorDataTopic = "/sensor/data";

        private const float LinearSpeedMetersPerSecond = 0.15f;
        private const float AngularSpeedRadiansPerSecond = 0.8f;
        private const float PublishRateHz = 20f;
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
        private VirtualSensors simulationSensors;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotRosTeleop>() != null)
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
            ros.RosIPAddress = RobotIpAddress;
            ros.RosPort = RobotTcpPort;
            ros.ConnectOnStart = true;
            ros.ShowHud = true;
            ros.RegisterPublisher<TwistMsg>(CommandTopic, queue_size: 1);
            ros.Subscribe<QuaternionMsg>(SensorDataTopic, OnSensorData);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;

            if (keyboard != null)
            {
                if (keyboard.spaceKey.wasPressedThisFrame)
                {
                    emergencyStop = true;
                    PublishStop();
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

            if (!applicationHasFocus || emergencyStop || keyboard == null)
            {
                PublishStop();
                return;
            }

            double linear = ReadLinearInput(keyboard) * LinearSpeedMetersPerSecond;
            double angular = ReadAngularInput(keyboard) * AngularSpeedRadiansPerSecond;
            PublishCommand(linear, angular);
        }

        private static float ReadLinearInput(Keyboard keyboard)
        {
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            {
                return 1f;
            }

            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            {
                return -1f;
            }

            return 0f;
        }

        private static float ReadAngularInput(Keyboard keyboard)
        {
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                return 1f;
            }

            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                return -1f;
            }

            return 0f;
        }

        private void PublishCommand(double linear, double angular)
        {
            if (ros == null)
            {
                return;
            }

            var command = new TwistMsg(
                new Vector3Msg(linear, 0d, 0d),
                new Vector3Msg(0d, 0d, angular));

            ros.Publish(CommandTopic, command);
        }

        private void PublishStop()
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

        private void OnApplicationFocus(bool hasFocus)
        {
            applicationHasFocus = hasFocus;

            if (!hasFocus)
            {
                PublishStop();
            }
        }

        private void OnDisable()
        {
            PublishStop();
        }

        private void OnApplicationQuit()
        {
            PublishStop();
        }

        private void OnGUI()
        {
            const int width = 440;
            const int height = 174;
            var panel = new Rect(Screen.width - width - 10, 10, width, height);
            bool hasRobotSensorData = lastSensorMessageTime >= 0f &&
                Time.unscaledTime - lastSensorMessageTime <= SensorTimeoutSeconds;

            bool hasDisplayData = hasRobotSensorData;
            double displayUltrasonicMeters = ultrasonicMeters;
            bool displayLeftIr = leftIrTriggered;
            bool displayRightIr = rightIrTriggered;
            bool displayCenterIr = centerIrTriggered;
            string dataSource = hasRobotSensorData ? "ROBOT" : "NO DATA";

            if (!hasRobotSensorData && TryReadSimulationSensors(
                    out float simulationUltrasonicMeters,
                    out bool simulationLeftIr,
                    out bool simulationRightIr,
                    out bool simulationCenterIr))
            {
                hasDisplayData = true;
                displayUltrasonicMeters = simulationUltrasonicMeters;
                displayLeftIr = simulationLeftIr;
                displayRightIr = simulationRightIr;
                displayCenterIr = simulationCenterIr;
                dataSource = "SIMULATION";
            }

            GUI.Box(panel, $"Sensors — {dataSource}");
            GUI.Label(new Rect(panel.x + 12, panel.y + 24, width - 24, 20),
                $"{RobotIpAddress}:{RobotTcpPort}  |  topic: {CommandTopic}");
            GUI.Label(new Rect(panel.x + 12, panel.y + 44, width - 24, 20),
                "WASD/arrows: drive   Space: E-STOP   Enter: reset E-STOP");

            string state = emergencyStop ? "E-STOP ACTIVE" : "ready";
            GUI.Label(new Rect(panel.x + 12, panel.y + 64, width - 24, 20), $"State: {state}");

            string sensorState = hasDisplayData
                ? $"Ultrasonic: {displayUltrasonicMeters:F2} m  |  " +
                  $"raw IR L:{(displayLeftIr ? 1 : 0)} " +
                  $"R:{(displayRightIr ? 1 : 0)} C:{(displayCenterIr ? 1 : 0)}"
                : "Sensors: no robot connection or simulation provider";
            GUI.Label(new Rect(panel.x + 12, panel.y + 86, width - 24, 20), sensorState);

            DrawUltrasonicBar(
                new Rect(panel.x + 12, panel.y + 108, width - 24, 10),
                hasDisplayData,
                displayUltrasonicMeters);

            const float indicatorWidth = 128f;
            const float indicatorGap = 12f;
            float indicatorsX = panel.x + 12;
            DrawIrIndicator(
                new Rect(indicatorsX, panel.y + 130, indicatorWidth, 28),
                "IR left",
                hasDisplayData,
                displayLeftIr);
            DrawIrIndicator(
                new Rect(indicatorsX + indicatorWidth + indicatorGap, panel.y + 130, indicatorWidth, 28),
                "IR right",
                hasDisplayData,
                displayRightIr);
            DrawIrIndicator(
                new Rect(indicatorsX + (indicatorWidth + indicatorGap) * 2f, panel.y + 130, indicatorWidth, 28),
                "IR center",
                hasDisplayData,
                displayCenterIr);
        }

        private bool TryReadSimulationSensors(
            out float simulationUltrasonicMeters,
            out bool simulationLeftIr,
            out bool simulationRightIr,
            out bool simulationCenterIr)
        {
            if (simulationSensors == null)
            {
                simulationSensors = FindAnyObjectByType<VirtualSensors>();
            }

            if (simulationSensors != null)
            {
                return simulationSensors.TryReadSimulationSensors(
                    out simulationUltrasonicMeters,
                    out simulationLeftIr,
                    out simulationRightIr,
                    out simulationCenterIr);
            }

            simulationUltrasonicMeters = 0f;
            simulationLeftIr = false;
            simulationRightIr = false;
            simulationCenterIr = false;
            return false;
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
