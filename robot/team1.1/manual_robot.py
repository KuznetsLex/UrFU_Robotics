#!/usr/bin/env python3
"""Small safe examples for direct, non-ROS robot control.

Stop the Unity ROS node before running this file. Only one process may own the
GPIO and I2C hardware at a time.
"""

import argparse
import time

from robot_hardware import RobotHardware


def show_sensors(robot):
    readings = robot.read_sensors()
    print(
        "ultrasonic={:.3f} m, left_ir={}, right_ir={}, gripper_ir={}".format(
            readings.ultrasonic_meters,
            readings.left_ir_triggered,
            readings.right_ir_triggered,
            readings.gripper_ir_triggered,
        )
    )


def drive_for(robot, linear_m_s, angular_rad_s, duration_seconds):
    """Example helper: drive for a bounded time and always stop afterward."""

    try:
        deadline = time.monotonic() + max(0.0, duration_seconds)
        while time.monotonic() < deadline:
            # Repeating at 20 Hz lets the same soft-start ramp used by ROS
            # reach its target instead of applying only the first PWM step.
            robot.drive.drive(linear_m_s, angular_rad_s)
            time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))
    finally:
        robot.stop()


def pwm_for(robot, left_pwm, right_pwm, duration_seconds):
    try:
        deadline = time.monotonic() + max(0.0, duration_seconds)
        while time.monotonic() < deadline:
            robot.drive.set_pwm(left_pwm, right_pwm)
            time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))
    finally:
        robot.stop()


def interactive_shell(robot):
    print(
        "Commands: sensors | drive LINEAR ANGULAR SECONDS | "
        "pwm LEFT RIGHT SECONDS | grip open|close | camera -1..1 | "
        "pose | stop | quit"
    )
    while True:
        try:
            parts = input("robot> ").strip().split()
        except (EOFError, KeyboardInterrupt):
            print()
            break
        if not parts:
            continue

        command = parts[0].lower()
        try:
            if command in ("quit", "exit", "q"):
                break
            if command == "sensors":
                show_sensors(robot)
            elif command == "drive" and len(parts) == 4:
                drive_for(robot, float(parts[1]), float(parts[2]), float(parts[3]))
            elif command == "pwm" and len(parts) == 4:
                pwm_for(robot, float(parts[1]), float(parts[2]), float(parts[3]))
            elif command == "grip" and len(parts) == 2:
                if parts[1].lower() == "open":
                    robot.arm.open_gripper()
                elif parts[1].lower() == "close":
                    robot.arm.close_gripper()
                else:
                    raise ValueError("grip expects open or close")
            elif command == "camera" and len(parts) == 2:
                angle = robot.arm.set_camera_pan(float(parts[1]))
                print("camera servo angle: {:.1f} degrees".format(angle))
            elif command == "pose":
                robot.arm.initialize_driving_pose()
            elif command == "stop":
                robot.stop()
            else:
                print("Unknown command or wrong argument count")
        except Exception as error:
            robot.stop()
            print("error: {}".format(error))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--stealth",
        action="store_true",
        help="read sensors without importing motor or servo drivers",
    )
    arguments = parser.parse_args()

    with RobotHardware(stealth=arguments.stealth) as robot:
        interactive_shell(robot)


if __name__ == "__main__":
    main()
