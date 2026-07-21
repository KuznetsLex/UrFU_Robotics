using System;
using RosMessageTypes.Sensor;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Team11.Ros
{
    [DefaultExecutionOrder(-900)]
    public sealed class RobotRosServoControl : MonoBehaviour
    {
        private const string JointCommandTopic = "/cmd_servo_angles";
        private const float BaseMinAngle = 0f;
        private const float BaseMaxAngle = 180f;
        private const float ShoulderMinAngle = 20f;
        private const float ShoulderMaxAngle = 160f;
        private const float ElbowMinAngle = 0f;
        private const float ElbowMaxAngle = 180f;
        private const float ClawMinAngle = 44f;
        private const float ClawMaxAngle = 105f;
        private const float CameraMinAngle = CameraRotator.MinAngle;
        private const float CameraMaxAngle = CameraRotator.MaxAngle;

        private static readonly string[] JointNames =
        {
            "base",
            "shoulder",
            "elbow",
            "claw",
            "camera_pan"
        };

        [Header("Servo target angles (degrees)")]
        [SerializeField, Range(BaseMinAngle, BaseMaxAngle)]
        private float baseAngle = 90f;

        [SerializeField, Range(ShoulderMinAngle, ShoulderMaxAngle)]
        private float shoulderAngle = 150f;

        [SerializeField, Range(ElbowMinAngle, ElbowMaxAngle)]
        private float elbowAngle = 90f;

        [SerializeField, Range(ClawMinAngle, ClawMaxAngle)]
        private float clawAngle = 50f;

        [SerializeField, Range(ClawMinAngle, ClawMaxAngle)]
        private float clawOpenAngle = 50f;

        [SerializeField, Range(ClawMinAngle, ClawMaxAngle)]
        private float clawClosedAngle = 105f;

        [SerializeField, Range(CameraMinAngle, CameraMaxAngle)]
        private float cameraAngle = 90f;

        [Header("Policy camera pan")]
        [Tooltip("Estimated real actuator speed used when hardware angle feedback is unavailable.")]
        [SerializeField, Min(1f)]
        private float cameraEstimatedSpeedDegreesPerSecond = 140f;

        [Tooltip("Do not publish camera target changes smaller than this value.")]
        [SerializeField, Min(0f)]
        private float cameraCommandDeadbandDegrees = 0.5f;

        [Tooltip("Maximum ROS publish rate for policy-driven camera target updates.")]
        [SerializeField, Min(1f)]
        private float cameraCommandPublishRateHz = 20f;

        private ROSConnection ros;
        private string status = "Set target angles in the Inspector, then press Apply to robot";
        private bool hasCommandedClawState = true;
        private bool commandedClawOpen = true;
        private float desiredCameraAngle = CameraRotator.CenterAngle;
        private float estimatedCameraAngle = CameraRotator.CenterAngle;
        private float lastCameraEstimateTime;
        private float nextCameraPolicyPublishTime;

        public string Status => status;

        public bool TryGetCommandedClawState(out bool isOpen)
        {
            isOpen = commandedClawOpen;
            return hasCommandedClawState;
        }

        public bool TryGetCameraPanState(out float normalizedAngle)
        {
            UpdateEstimatedCameraAngle();
            normalizedAngle = CameraRotator.NormalizeAngle(estimatedCameraAngle);
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotRosServoControl>() != null)
            {
                return;
            }

            // Не подключаемся к реальному роботу во время обучения — см. тот же
            // комментарий в RobotRosTeleop.Install().
            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
            {
                return;
            }

            ROSConnection.GetOrCreateInstance().gameObject.AddComponent<RobotRosServoControl>();
        }

        private void Awake()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<JointStateMsg>(JointCommandTopic, queue_size: 1);
            cameraAngle = CameraRotator.ClampAngle(cameraAngle);
            desiredCameraAngle = cameraAngle;
            estimatedCameraAngle = cameraAngle;
            lastCameraEstimateTime = Time.unscaledTime;
        }

        [ContextMenu("Reset servo targets")]
        public void ResetTargets()
        {
            baseAngle = 90f;
            shoulderAngle = 150f;
            elbowAngle = 90f;
            clawAngle = 50f;
            cameraAngle = CameraRotator.CenterAngle;
            desiredCameraAngle = cameraAngle;
            estimatedCameraAngle = cameraAngle;
            lastCameraEstimateTime = Time.unscaledTime;
            commandedClawOpen = true;
            hasCommandedClawState = true;
            status = "Home targets loaded; press Apply to robot to move";
        }

        [ContextMenu("Apply servo angles to robot")]
        public void PublishTargetAngles()
        {
            if (ros == null)
            {
                status = "ROS connection is unavailable";
                return;
            }

            cameraAngle = CameraRotator.ClampAngle(cameraAngle);
            desiredCameraAngle = cameraAngle;

            commandedClawOpen = Mathf.Abs(clawAngle - clawOpenAngle) <=
                Mathf.Abs(clawAngle - clawClosedAngle);
            hasCommandedClawState = true;

            var positionsRadians = new double[]
            {
                baseAngle * Mathf.Deg2Rad,
                shoulderAngle * Mathf.Deg2Rad,
                elbowAngle * Mathf.Deg2Rad,
                clawAngle * Mathf.Deg2Rad,
                cameraAngle * Mathf.Deg2Rad
            };

            var message = new JointStateMsg
            {
                name = (string[])JointNames.Clone(),
                position = positionsRadians,
                velocity = Array.Empty<double>(),
                effort = Array.Empty<double>()
            };

            try
            {
                ros.Publish(JointCommandTopic, message);
                status = $"Applied at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception exception)
            {
                status = $"Send failed: {exception.Message}";
            }
        }

        public void ApplyGripperCommand(RobotGripperCommand command)
        {
            ApplyPolicyCommand(
                CameraRotator.NormalizeAngle(desiredCameraAngle),
                command);
        }

        public void ApplyPolicyCommand(
            float normalizedCameraTarget,
            RobotGripperCommand gripperCommand)
        {
            UpdateEstimatedCameraAngle();
            desiredCameraAngle = CameraRotator.DenormalizeAngle(normalizedCameraTarget);

            bool gripperChanged = gripperCommand != RobotGripperCommand.None;
            if (gripperChanged)
            {
                commandedClawOpen = gripperCommand == RobotGripperCommand.Release;
                hasCommandedClawState = true;
                clawAngle = commandedClawOpen ? clawOpenAngle : clawClosedAngle;
            }

            bool cameraChanged = Mathf.Abs(desiredCameraAngle - cameraAngle) >=
                cameraCommandDeadbandDegrees;
            bool cameraPublishReady = Time.unscaledTime >= nextCameraPolicyPublishTime;
            if (!gripperChanged && (!cameraChanged || !cameraPublishReady))
                return;

            cameraAngle = desiredCameraAngle;
            nextCameraPolicyPublishTime = Time.unscaledTime +
                1f / Mathf.Max(1f, cameraCommandPublishRateHz);
            PublishTargetAngles();
        }

        private void UpdateEstimatedCameraAngle()
        {
            float now = Time.unscaledTime;
            float deltaTime = Mathf.Max(0f, now - lastCameraEstimateTime);
            lastCameraEstimateTime = now;
            estimatedCameraAngle = Mathf.MoveTowards(
                estimatedCameraAngle,
                cameraAngle,
                Mathf.Max(1f, cameraEstimatedSpeedDegreesPerSecond) * deltaTime);
        }
    }
}
