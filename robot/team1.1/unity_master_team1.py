#!/usr/bin/env python3
"""ROS 1 adapter between Unity topics and the reusable robot hardware API."""

import atexit
import os
import time
import traceback

import rospy
from geometry_msgs.msg import Quaternion, Twist, Vector3
from std_msgs.msg import Float32, Int32

from robot_hardware import RobotHardware


STEALTH_MODE = os.environ.get("ROBOT_STEALTH", "").strip().lower() in (
    "1",
    "true",
    "yes",
    "on",
)
WATCHDOG_TIMEOUT_SECONDS = 0.5


class UnityRobotNode:
    """Thin ROS transport adapter; hardware behavior lives in robot_hardware."""

    def __init__(self, hardware):
        self.hardware = hardware
        self.sensor_publisher = None
        self.pwm_publisher = None
        self.last_velocity_command_time = time.monotonic()
        self.hardware.drive.on_pwm_changed = self._publish_pwm

    def velocity_callback(self, message):
        self.last_velocity_command_time = time.monotonic()
        self.hardware.drive.drive(
            message.linear.x,
            message.angular.z,
        )

    def gripper_callback(self, message):
        if message.data == 1:
            self.hardware.arm.close_gripper()
        elif message.data == 2:
            self.hardware.arm.open_gripper()

    def camera_callback(self, message):
        self.hardware.arm.set_camera_pan(message.data)

    def sensor_timer_callback(self, _event):
        if self.sensor_publisher is None or not self.hardware.sensors_available:
            return
        try:
            readings = self.hardware.read_sensors()
            message = Quaternion()
            message.x = readings.ultrasonic_meters
            message.y = float(readings.left_ir_triggered)
            message.z = float(readings.right_ir_triggered)
            message.w = float(readings.gripper_ir_triggered)
            self.sensor_publisher.publish(message)
        except Exception as error:
            rospy.logerr_throttle(1.0, "Sensor read failed: %s", error)

    def watchdog_callback(self, _event):
        command_age = time.monotonic() - self.last_velocity_command_time
        if (
            command_age > WATCHDOG_TIMEOUT_SECONDS
            and self.hardware.drive.is_moving
        ):
            rospy.logwarn(
                "Velocity watchdog: no /cmd_vel for %.1f s; stopping",
                WATCHDOG_TIMEOUT_SECONDS,
            )
            self.hardware.stop()

    def _publish_pwm(self, left_pwm, right_pwm):
        if self.pwm_publisher is not None:
            self.pwm_publisher.publish(
                Vector3(float(left_pwm), float(right_pwm), 0.0)
            )

    def run(self):
        rospy.init_node("unity_robot_master", anonymous=True)
        rospy.on_shutdown(self.hardware.close)

        if self.hardware.stealth:
            rospy.logwarn(
                "STEALTH MODE: motor and servo commands are physically disabled"
            )

        self.pwm_publisher = rospy.Publisher(
            "/sensor/pwm",
            Vector3,
            queue_size=10,
        )
        if self.hardware.sensors_available:
            self.sensor_publisher = rospy.Publisher(
                "/sensor/data",
                Quaternion,
                queue_size=10,
            )

        rospy.Subscriber("/cmd_vel", Twist, self.velocity_callback)
        rospy.Subscriber("/cmd_gripper", Int32, self.gripper_callback)
        rospy.Subscriber("/cmd_camera_pan", Float32, self.camera_callback)

        if self.hardware.arm.enabled:
            rospy.logwarn(
                "Normal mode initializes and moves physical servos; "
                "keep the robot lifted and the area clear"
            )
            self.hardware.arm.initialize_driving_pose()

        if self.sensor_publisher is not None:
            rospy.Timer(rospy.Duration(0.1), self.sensor_timer_callback)
        rospy.Timer(rospy.Duration(0.2), self.watchdog_callback)
        rospy.spin()


def main():
    hardware = None
    try:
        hardware = RobotHardware(stealth=STEALTH_MODE)
        atexit.register(hardware.close)
        UnityRobotNode(hardware).run()
    except rospy.ROSInterruptException:
        pass
    except Exception:
        rospy.logfatal("Robot initialization failed:\n%s", traceback.format_exc())
        raise
    finally:
        if hardware is not None:
            hardware.close()


if __name__ == "__main__":
    main()
