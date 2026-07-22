#!/usr/bin/env python3
"""Static contract check for the ML-Agents/Unity/ROS inference path."""

from __future__ import annotations

import re
import sys
from pathlib import Path

import onnx


ROOT = Path(__file__).resolve().parents[1]
MODEL = ROOT / "Assets/Models/GFSX_Brain.onnx"
PREFAB = ROOT / "Assets/Prefabs/Xiao-r GFS-X_1.prefab"
SCENE = ROOT / "Assets/Scenes/Inference.unity"
BRAIN = ROOT / "Assets/Scripts/RobotBrain.cs"
ARENA_SPAWNER = ROOT / "Assets/Scripts/ArenaSpawner.cs"
TELEOP = ROOT / "Assets/Scripts/ROS/RobotRosTeleop.cs"
SERVO = ROOT / "Assets/Scripts/ROS/RobotRosServoControl.cs"
CAMERA_VIEW = ROOT / "Assets/Scripts/ROS/RobotCameraView.cs"
VISION = ROOT / "Assets/Scripts/ROS/RealVision.cs"
ROBOT_MASTER = ROOT / "robot/team1.1/unity_master_team1.py"
ROBOT_PINOUT = ROOT / "robot/team1.1/hardware_pinout.py"
ROBOT_START = ROOT / "robot/team1.1/start_robot_team1.1.sh"

EXPECTED_MODEL_GUID = "700dcbe4dfe44e8b9fcd40c8a874588c"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    print(f"[OK] {message}")


def tensor_width(model: onnx.ModelProto, collection: str, name: str) -> int:
    values = getattr(model.graph, collection)
    value = next(item for item in values if item.name == name)
    dims = value.type.tensor_type.shape.dim
    return dims[-1].dim_value


def main() -> int:
    model = onnx.load(MODEL)
    observation_width = tensor_width(model, "input", "obs_0")
    mask_width = tensor_width(model, "input", "action_masks")
    continuous_width = tensor_width(model, "output", "continuous_actions")
    model_metadata = {item.key: item.value for item in model.metadata_props}

    require(observation_width == 75, "policy input is 75 = 15 observations x 5 stacks")
    require(continuous_width == 3, "policy has 3 continuous actions: linear/angular/camera")
    require(mask_width == 3, "policy discrete branch has none/grab/release")
    require(model_metadata.get("team11.observation_contract") ==
            "ultrasonic_0_1_prev_gas_minus1_1_v1",
            "deployed policy normalizer matches normalized ultrasonic/gas observations")

    prefab = read(PREFAB)
    scene = read(SCENE)
    brain = read(BRAIN)
    arena_spawner = read(ARENA_SPAWNER)
    teleop = read(TELEOP)
    servo = read(SERVO)
    camera_view = read(CAMERA_VIEW)
    vision = read(VISION)
    robot = read(ROBOT_MASTER)
    pinout = read(ROBOT_PINOUT)
    start = read(ROBOT_START)

    require("m_NumContinuousActions: 3" in prefab, "Unity BehaviorParameters exposes 3 continuous actions")
    require("BranchSizes: 03000000" in prefab, "Unity BehaviorParameters exposes one 3-way discrete branch")
    require("propertyPath: m_BrainParameters.VectorObservationSize\n      value: 15" in scene,
            "inference scene uses 15 vector observations")
    require("propertyPath: m_BrainParameters.NumStackedVectorObservations\n      value: 5" in scene,
            "inference scene uses 5 observation stacks")
    require("propertyPath: m_BehaviorType\n      value: 2" in scene,
            "inference scene forces InferenceOnly behavior")
    require(f"guid: {EXPECTED_MODEL_GUID}" in scene, "inference scene references the deployed policy model")
    require("propertyPath: useRealRobotIo\n      value: 1" in scene,
            "inference scene selects physical robot observations and commands")
    require("propertyPath: isTraining\n      value: 0" in scene,
            "inference scene disables training-only behavior")
    require("propertyPath: MaxStep\n      value: 0" in scene,
            "inference scene runs without an automatic episode timeout")
    require(re.search(r"^  arenaCount: 1$", scene, re.MULTILINE) is not None,
            "inference scene creates exactly one physical-robot agent")
    require("templateBrain.UseRealRobotIo && arenaCount != 1" in arena_spawner and
            "arenaCount = 1;" in arena_spawner,
            "ArenaSpawner prevents competing agents from controlling one physical robot")

    observation_calls = len(re.findall(r"sensor\.AddObservation\(", brain))
    require(observation_calls == 15, "RobotBrain emits exactly 15 observations per decision")
    require("ultrasonicMeters / Mathf.Max(0.01f, ultrasonicObservationMaxMeters)" in brain and
            "Mathf.Clamp01" in brain,
            "ultrasonic policy observation is normalized from 0..5 m to 0..1")
    require("prevGas / maxLinearCommand" in brain and
            "Mathf.Clamp(prevGas / maxLinearCommand, -1f, 1f)" in brain,
            "previous gas policy observation is normalized to -1..1")
    for index in range(3):
        require(f"actions.ContinuousActions[{index}]" in brain,
                f"RobotBrain consumes continuous action {index}")
    require("actions.DiscreteActions[0]" in brain, "RobotBrain consumes discrete gripper action 0")

    require('RobotIpAddress = "192.168.2.158"' in teleop and "RobotTcpPort = 10001" in teleop,
            "Unity ROS endpoint is 192.168.2.158:10001")
    require('CommandTopic = "/cmd_vel"' in teleop and "RegisterPublisher<TwistMsg>" in teleop,
            "Unity publishes geometry_msgs/Twist on /cmd_vel")
    require('SensorDataTopic = "/sensor/data"' in teleop and "Subscribe<QuaternionMsg>" in teleop,
            "Unity subscribes to geometry_msgs/Quaternion on /sensor/data")
    require("ros.Connect(RobotIpAddress, RobotTcpPort)" in teleop,
            "Unity explicitly starts the late-installed ROS connection")
    require('GripperCommandTopic = "/cmd_gripper"' in servo and
            "RegisterPublisher<Int32Msg>" in servo,
            "Unity publishes semantic Int32 gripper commands")
    require('CameraCommandTopic = "/cmd_camera_pan"' in servo and
            "RegisterPublisher<Float32Msg>" in servo,
            "Unity publishes normalized Float32 camera commands")
    require("JointCommandTopic" not in servo and "JointStateMsg" not in servo,
            "Unity does not resend a second set of absolute servo angles")
    require("angular = -latestCommand.Angular" in teleop,
            "policy right-positive steering is converted to ROS left-positive angular.z")
    require("ANGLE_DRIVE_CAMERA - (yaw * CAMERA_PAN_HALF_RANGE)" in robot,
            "physical camera axis is inverted at the robot adapter boundary")

    require("192.168.2.158:10002/frame.jpg" in camera_view,
            "Unity camera view reads the team1.1 frame endpoint")
    require("udpPort = 5005" in vision, "Unity receives YOLO detections on UDP 5005")

    expected_robot_interfaces = {
        "/cmd_vel": "Twist",
        "/cmd_gripper": "Int32",
        "/cmd_camera_pan": "Float32",
    }
    for topic, message_type in expected_robot_interfaces.items():
        pattern = rf"rospy\.Subscriber\('{re.escape(topic)}',\s*{message_type},"
        require(re.search(pattern, robot) is not None,
                f"robot subscribes to {topic} with {message_type}")
    require("rospy.Publisher('/sensor/data', Quaternion" in robot,
            "robot publishes geometry_msgs/Quaternion on /sensor/data")
    virtual_sensors = read(ROOT / "Assets/Scripts/VirtualSensors.cs")
    require("environment.globalScale" in virtual_sensors and
            "minDist / worldUnitsPerMeter" in virtual_sensors,
            "virtual ultrasonic distance is converted from Unity units to meters via globalScale")
    require("class SafeUltrasonic" in robot and
            "ECHO_TIMEOUT_SECONDS = 0.03" in robot and
            "us = SafeUltrasonic(gpio.GPIO)" in robot,
            "robot ultrasonic reads time out instead of blocking /sensor/data")
    expected_pinout = {
        "MOTOR_ENABLE_A_PIN": 13,
        "MOTOR_ENABLE_B_PIN": 20,
        "MOTOR_IN1_PIN": 16,
        "MOTOR_IN2_PIN": 19,
        "MOTOR_IN3_PIN": 26,
        "MOTOR_IN4_PIN": 21,
        "ULTRASONIC_ECHO_PIN": 4,
        "ULTRASONIC_TRIGGER_PIN": 17,
        "SERVO_BASE_CHANNEL": 1,
        "SERVO_SHOULDER_CHANNEL": 2,
        "SERVO_ELBOW_CHANNEL": 3,
        "SERVO_CLAW_CHANNEL": 4,
        "SERVO_CAMERA_PAN_CHANNEL": 7,
    }
    for name, value in expected_pinout.items():
        require(re.search(rf"^{name} = {value}$", pinout, re.MULTILINE) is not None,
                f"hardware pinout keeps {name}={value}")
    require("SENSOR_LEFT_IR_PIN = 18" in pinout and
            "SENSOR_RIGHT_IR_PIN = 25" in pinout and
            "SENSOR_GRIPPER_IR_PIN = 22" in pinout,
            "robot sensor semantics match left GPIO18/right GPIO25/IR_M wiring")
    require("gpio.IN1 = MOTOR_IN1_PIN" in robot and
            "gpio.IN2 = MOTOR_IN2_PIN" in robot and
            "gpio.IN3 = MOTOR_IN3_PIN" in robot and
            "gpio.IN4 = MOTOR_IN4_PIN" in robot,
            "normal mode applies the operational team2 motor direction ordering")
    require("HOST_TCP_PORT=10001" in start and "CAMERA_HTTP_PORT=10002" in start,
            "robot exposes ROS TCP on 10001 and camera HTTP on 10002")
    require("timeout 5 rostopic echo -n 1 /sensor/data" in start,
            "robot startup requires an actual /sensor/data message")

    expected_pose = {
        "ANGLE_DRIVE_BASE": 90,
        "ANGLE_DRIVE_SHOULDER": 150,
        "ANGLE_DRIVE_ELBOW": 90,
        "ANGLE_DRIVE_CAMERA": 90,
        "ANGLE_CLAW_OPEN": 50,
        "ANGLE_CLAW_CLOSED": 89,
    }
    for name, value in expected_pose.items():
        require(re.search(rf"^{name} = {value}$", robot, re.MULTILINE) is not None,
                f"robot owns calibrated servo target {name}={value}")
    gripper_body = re.search(
        r"def gripper_callback\(data\):(.*?)(?=\ndef camera_callback|\n# ---)",
        robot,
        re.DOTALL,
    )
    require(gripper_body is not None and
            "SERVO_SHOULDER" not in gripper_body.group(1) and
            "SERVO_ELBOW" not in gripper_body.group(1) and
            gripper_body.group(1).count("servo.set(SERVO_CLAW") == 2,
            "grab/release changes only the claw servo")

    print("\nInference contract is consistent.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, StopIteration) as error:
        print(f"[FAIL] {error}", file=sys.stderr)
        raise SystemExit(1)
