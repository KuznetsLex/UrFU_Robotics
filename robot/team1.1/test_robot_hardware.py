#!/usr/bin/env python3
"""Host-side unit tests for robot_hardware (no Raspberry Pi required)."""

import unittest

from hardware_pinout import (
    MOTOR_IN1_PIN,
    MOTOR_IN2_PIN,
    MOTOR_IN3_PIN,
    MOTOR_IN4_PIN,
    SENSOR_LEFT_IR_PIN,
)
from robot_hardware import DifferentialDrive, RobotArm, RobotSensors


class FakeGpioModule:
    IN = 0
    OUT = 1
    LOW = 0
    HIGH = 1
    PUD_UP = 2
    PUD_DOWN = 3

    def __init__(self):
        self.inputs = {}
        self.outputs = {}

    def setup(self, pin, _mode, **options):
        if "initial" in options:
            self.outputs[pin] = options["initial"]

    def input(self, pin):
        return self.inputs.get(pin, self.HIGH)

    def output(self, pin, value):
        self.outputs[pin] = value


class FakeMotorDriver:
    def __init__(self):
        self.digital_outputs = {}
        self.left_enable = None
        self.right_enable = None

    def digital_write(self, pin, value):
        self.digital_outputs[pin] = value

    def ena_pwm(self, value):
        self.left_enable = value

    def enb_pwm(self, value):
        self.right_enable = value


class FakeServoDriver:
    def __init__(self):
        self.commands = []

    def set(self, channel, angle):
        self.commands.append((channel, angle))


class DifferentialDriveTests(unittest.TestCase):
    def test_pwm_is_ramped_calibrated_and_hard_stopped(self):
        driver = FakeMotorDriver()
        drive = DifferentialDrive(driver)

        drive.set_pwm(50, -50)

        self.assertEqual(drive.left_pwm, 15)
        self.assertEqual(drive.right_pwm, -15)
        self.assertEqual(driver.left_enable, 60)
        self.assertEqual(driver.right_enable, 60)
        self.assertEqual(driver.digital_outputs[MOTOR_IN1_PIN], 0)
        self.assertEqual(driver.digital_outputs[MOTOR_IN2_PIN], 1)
        self.assertEqual(driver.digital_outputs[MOTOR_IN3_PIN], 1)
        self.assertEqual(driver.digital_outputs[MOTOR_IN4_PIN], 0)

        drive.stop()

        self.assertFalse(drive.is_moving)
        self.assertEqual(driver.left_enable, 0)
        self.assertEqual(driver.right_enable, 0)
        self.assertTrue(
            all(
                driver.digital_outputs[pin] == 0
                for pin in (
                    MOTOR_IN1_PIN,
                    MOTOR_IN2_PIN,
                    MOTOR_IN3_PIN,
                    MOTOR_IN4_PIN,
                )
            )
        )


class RobotArmTests(unittest.TestCase):
    def test_gripper_and_camera_commands_use_calibrated_channels(self):
        driver = FakeServoDriver()
        arm = RobotArm(driver)

        arm.open_gripper()
        arm.close_gripper()
        camera_angle = arm.set_camera_pan(0.5)

        self.assertEqual(driver.commands[0], (4, 50))
        self.assertEqual(driver.commands[1], (4, 83))
        self.assertEqual(camera_angle, 75)
        self.assertEqual(driver.commands[2], (7, 75))


class RobotSensorsTests(unittest.TestCase):
    def test_sensor_suite_filters_distance_and_debounces_ir(self):
        gpio = FakeGpioModule()
        sensors = RobotSensors(gpio)
        sensors.ultrasonic.read_centimeters = lambda: 25.0
        gpio.inputs[SENSOR_LEFT_IR_PIN] = gpio.LOW

        first = sensors.read()
        second = sensors.read()

        self.assertEqual(first.ultrasonic_meters, 1.0)
        self.assertFalse(first.left_ir_triggered)
        self.assertEqual(second.ultrasonic_meters, 0.25)
        self.assertTrue(second.left_ir_triggered)


if __name__ == "__main__":
    unittest.main()
