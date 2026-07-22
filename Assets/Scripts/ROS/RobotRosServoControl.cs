using System;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Team11.Ros
{
    /// <summary>
    /// Publishes semantic servo commands. Physical angles and the driving pose are
    /// owned by the Raspberry Pi; Unity only asks the claw to grab/release and
    /// provides a normalized camera-pan target.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public sealed class RobotRosServoControl : MonoBehaviour
    {
        private const string GripperCommandTopic = "/cmd_gripper";
        private const string CameraCommandTopic = "/cmd_camera_pan";

        [Header("Policy camera pan")]
        [Tooltip("Estimated real actuator speed used when hardware feedback is unavailable.")]
        [SerializeField, Min(1f)]
        private float cameraEstimatedSpeedDegreesPerSecond = 140f;

        [Tooltip("Do not publish camera target changes smaller than this value.")]
        [SerializeField, Min(0f)]
        private float cameraCommandDeadbandDegrees = 0.5f;

        [Tooltip("Maximum ROS publish rate for policy-driven camera target updates.")]
        [SerializeField, Min(1f)]
        private float cameraCommandPublishRateHz = 20f;

        private ROSConnection ros;
        private string status = "Robot-side driving pose is active";
        private bool hasCommandedClawState = true;
        private bool commandedClawOpen = true;
        private float desiredCameraAngle = CameraRotator.CenterAngle;
        private float publishedCameraAngle = CameraRotator.CenterAngle;
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
                return;

            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
                return;

            ROSConnection.GetOrCreateInstance().gameObject.AddComponent<RobotRosServoControl>();
        }

        private void Awake()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<Int32Msg>(GripperCommandTopic, queue_size: 1);
            ros.RegisterPublisher<Float32Msg>(CameraCommandTopic, queue_size: 1);
            lastCameraEstimateTime = Time.unscaledTime;
        }

        public void ApplyGripperCommand(RobotGripperCommand command)
        {
            PublishGripperCommand(command);
        }

        public void ApplyPolicyCommand(
            float normalizedCameraTarget,
            RobotGripperCommand gripperCommand)
        {
            UpdateEstimatedCameraAngle();
            desiredCameraAngle = CameraRotator.DenormalizeAngle(normalizedCameraTarget);

            if (gripperCommand != RobotGripperCommand.None)
                PublishGripperCommand(gripperCommand);

            bool cameraChanged = Mathf.Abs(desiredCameraAngle - publishedCameraAngle) >=
                cameraCommandDeadbandDegrees;
            bool cameraPublishReady = Time.unscaledTime >= nextCameraPolicyPublishTime;
            if (!cameraChanged || !cameraPublishReady)
                return;

            float normalizedTarget = CameraRotator.NormalizeAngle(desiredCameraAngle);
            try
            {
                ros.Publish(CameraCommandTopic, new Float32Msg(normalizedTarget));
                publishedCameraAngle = desiredCameraAngle;
                nextCameraPolicyPublishTime = Time.unscaledTime +
                    1f / Mathf.Max(1f, cameraCommandPublishRateHz);
                status = $"Camera target {desiredCameraAngle:F1} degrees at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception exception)
            {
                status = $"Camera command failed: {exception.Message}";
            }
        }

        private void PublishGripperCommand(RobotGripperCommand command)
        {
            if (command == RobotGripperCommand.None)
                return;

            try
            {
                ros.Publish(GripperCommandTopic, new Int32Msg((int)command));
                commandedClawOpen = command == RobotGripperCommand.Release;
                hasCommandedClawState = true;
                status = $"Gripper {(commandedClawOpen ? "released" : "grabbed")} at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception exception)
            {
                status = $"Gripper command failed: {exception.Message}";
            }
        }

        private void UpdateEstimatedCameraAngle()
        {
            float now = Time.unscaledTime;
            float deltaTime = Mathf.Max(0f, now - lastCameraEstimateTime);
            lastCameraEstimateTime = now;
            estimatedCameraAngle = Mathf.MoveTowards(
                estimatedCameraAngle,
                desiredCameraAngle,
                Mathf.Max(1f, cameraEstimatedSpeedDegreesPerSecond) * deltaTime);
        }
    }
}
