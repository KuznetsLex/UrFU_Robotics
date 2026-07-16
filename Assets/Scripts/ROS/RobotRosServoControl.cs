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

        private static readonly string[] JointNames =
        {
            "base",
            "shoulder",
            "elbow",
            "claw",
            "camera_pan"
        };

        [Header("Servo target angles (degrees)")]
        [SerializeField, Range(0f, 180f)]
        private float baseAngle = 90f;

        [SerializeField, Range(20f, 160f)]
        private float shoulderAngle = 150f;

        [SerializeField, Range(0f, 180f)]
        private float elbowAngle = 90f;

        [SerializeField, Range(44f, 105f)]
        private float clawAngle = 50f;

        [SerializeField, Range(0f, 180f)]
        private float cameraAngle = 90f;

        private ROSConnection ros;
        private string status = "Set target angles in the Inspector, then press Apply to robot";

        public string Status => status;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotRosServoControl>() != null)
            {
                return;
            }

            var instance = new GameObject("Physical Robot ROS Servo Control");
            DontDestroyOnLoad(instance);
            instance.AddComponent<RobotRosServoControl>();
        }

        private void Awake()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<JointStateMsg>(JointCommandTopic, queue_size: 1);
        }

        [ContextMenu("Reset servo targets")]
        public void ResetTargets()
        {
            baseAngle = 90f;
            shoulderAngle = 150f;
            elbowAngle = 90f;
            clawAngle = 50f;
            cameraAngle = 90f;
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
    }
}
