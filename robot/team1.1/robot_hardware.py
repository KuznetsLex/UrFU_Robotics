#!/usr/bin/env python3
"""Reusable hardware API for the team1.1 XiaoR robot.

This module has no ROS dependency. It can be imported from the Unity ROS bridge,
from a small manual program, or from an interactive Python session.
"""

import os
import sys
import time
from collections import deque
from dataclasses import dataclass

from hardware_pinout import (
    MOTOR_IN1_PIN,
    MOTOR_IN2_PIN,
    MOTOR_IN3_PIN,
    MOTOR_IN4_PIN,
    SENSOR_GRIPPER_IR_PIN,
    SENSOR_LEFT_IR_PIN,
    SENSOR_RIGHT_IR_PIN,
    SERVO_BASE_CHANNEL,
    SERVO_CAMERA_PAN_CHANNEL,
    SERVO_CLAW_CHANNEL,
    SERVO_ELBOW_CHANNEL,
    SERVO_SHOULDER_CHANNEL,
    ULTRASONIC_ECHO_PIN,
    ULTRASONIC_TRIGGER_PIN,
)


XIAOR_LIBRARY_PATH = "/root/XiaoRGeek"

# Calibrated persistent driving pose.
ANGLE_DRIVE_BASE = 40
ANGLE_DRIVE_SHOULDER = 166
ANGLE_DRIVE_ELBOW = 90
ANGLE_DRIVE_CAMERA = 90
ANGLE_CLAW_OPEN = 50
ANGLE_CLAW_CLOSED = 83

CAMERA_PAN_MIN = 20
CAMERA_PAN_MAX = 160
CAMERA_PAN_HALF_RANGE = 70


def _clamp(value, minimum, maximum):
    return max(minimum, min(maximum, value))


@dataclass(frozen=True)
class SensorReadings:
    """One filtered sensor snapshot with explicit physical units."""

    ultrasonic_meters: float
    left_ir_triggered: bool
    right_ir_triggered: bool
    gripper_ir_triggered: bool


class ActiveLowDigitalSensor:
    """A single active-low digital GPIO sensor."""

    def __init__(self, gpio_module, pin):
        self._gpio = gpio_module
        self.pin = pin
        self._gpio.setup(
            self.pin,
            self._gpio.IN,
            pull_up_down=self._gpio.PUD_UP,
        )

    def is_triggered(self):
        return self._gpio.input(self.pin) == self._gpio.LOW


class UltrasonicSensor:
    """Timeout-safe HC-SR04-compatible ultrasonic range finder."""

    ECHO_TIMEOUT_SECONDS = 0.03
    MAX_DISTANCE_CM = 500.0

    def __init__(
        self,
        gpio_module,
        trigger_pin=ULTRASONIC_TRIGGER_PIN,
        echo_pin=ULTRASONIC_ECHO_PIN,
    ):
        self._gpio = gpio_module
        self.trigger_pin = trigger_pin
        self.echo_pin = echo_pin
        self._gpio.setup(
            self.trigger_pin,
            self._gpio.OUT,
            initial=self._gpio.LOW,
        )
        self._gpio.setup(
            self.echo_pin,
            self._gpio.IN,
            pull_up_down=self._gpio.PUD_DOWN,
        )

    def read_centimeters(self):
        self._gpio.output(self.trigger_pin, self._gpio.LOW)
        time.sleep(0.000002)
        self._gpio.output(self.trigger_pin, self._gpio.HIGH)
        time.sleep(0.00001)
        self._gpio.output(self.trigger_pin, self._gpio.LOW)

        deadline = time.monotonic() + self.ECHO_TIMEOUT_SECONDS
        while self._gpio.input(self.echo_pin) == self._gpio.LOW:
            if time.monotonic() >= deadline:
                return self.MAX_DISTANCE_CM

        pulse_start = time.monotonic()
        deadline = pulse_start + self.ECHO_TIMEOUT_SECONDS
        while self._gpio.input(self.echo_pin) == self._gpio.HIGH:
            if time.monotonic() >= deadline:
                return self.MAX_DISTANCE_CM

        distance_cm = (time.monotonic() - pulse_start) * 17150.0
        if distance_cm <= 0.0 or distance_cm > self.MAX_DISTANCE_CM:
            return self.MAX_DISTANCE_CM
        return distance_cm


class RobotSensors:
    """Owns and filters all range and IR sensors."""

    def __init__(self, gpio_module):
        self.ultrasonic = UltrasonicSensor(gpio_module)
        self.left_ir = ActiveLowDigitalSensor(gpio_module, SENSOR_LEFT_IR_PIN)
        self.right_ir = ActiveLowDigitalSensor(gpio_module, SENSOR_RIGHT_IR_PIN)
        self.gripper_ir = ActiveLowDigitalSensor(
            gpio_module,
            SENSOR_GRIPPER_IR_PIN,
        )

        # Keep the legacy startup/filter behavior.
        self._ultrasonic_history = deque([100.0, 100.0, 100.0], maxlen=3)
        self._left_history = deque([False] * 5, maxlen=5)
        self._right_history = deque([False] * 5, maxlen=5)
        self._gripper_history = deque([False] * 5, maxlen=5)
        self.filtered_distance_cm = 500.0

    @staticmethod
    def _debounced(history):
        return sum(1 for value in list(history)[-3:] if value) >= 2

    def read(self):
        self._ultrasonic_history.append(self.ultrasonic.read_centimeters())
        self.filtered_distance_cm = sorted(self._ultrasonic_history)[1]

        self._left_history.append(self.left_ir.is_triggered())
        self._right_history.append(self.right_ir.is_triggered())
        self._gripper_history.append(self.gripper_ir.is_triggered())

        return SensorReadings(
            ultrasonic_meters=self.filtered_distance_cm / 100.0,
            left_ir_triggered=self._debounced(self._left_history),
            right_ir_triggered=self._debounced(self._right_history),
            gripper_ir_triggered=self._debounced(self._gripper_history),
        )


class DifferentialDrive:
    """Differential motor controller with calibration and soft start."""

    MAX_SPEED_M_S = 0.5
    MAX_LINEAR_M_S = 0.25
    TURN_GAIN = 0.25
    STEERING_EMA_ALPHA = 0.40
    MIN_MOTOR_PWM = 60.0
    MOTOR_DEAD_ZONE_PWM = 10.0
    MAX_PWM_STEP = 15.0

    def __init__(self, driver=None):
        self._driver = driver
        self.enabled = driver is not None
        self.left_pwm = 0.0
        self.right_pwm = 0.0
        self._filtered_angular = 0.0
        self.on_pwm_changed = None

    @staticmethod
    def _clamp_pwm(value):
        return _clamp(float(value), -100.0, 100.0)

    @classmethod
    def _effective_pwm(cls, requested_pwm):
        magnitude = abs(requested_pwm)
        if magnitude < cls.MOTOR_DEAD_ZONE_PWM:
            return 0.0
        return max(magnitude, cls.MIN_MOTOR_PWM)

    @classmethod
    def _ramp(cls, previous, target):
        delta = target - previous
        if abs(delta) <= cls.MAX_PWM_STEP:
            return target
        return previous + (cls.MAX_PWM_STEP if delta > 0.0 else -cls.MAX_PWM_STEP)

    def _publish_state(self, left, right):
        if self.on_pwm_changed is not None:
            self.on_pwm_changed(float(left), float(right))

    def _set_direction(self, in_a, in_b, requested_pwm):
        if requested_pwm > 0.0:
            self._driver.digital_write(in_a, 0)
            self._driver.digital_write(in_b, 1)
        elif requested_pwm < 0.0:
            self._driver.digital_write(in_a, 1)
            self._driver.digital_write(in_b, 0)
        else:
            self._driver.digital_write(in_a, 0)
            self._driver.digital_write(in_b, 0)

    def set_pwm(self, left_pwm, right_pwm):
        """Set logical track PWM in the -100..100 range."""

        left_target = self._clamp_pwm(left_pwm)
        right_target = self._clamp_pwm(right_pwm)

        # A zero command is an immediate hard stop, never a ramp.
        if abs(left_target) < 0.5 and abs(right_target) < 0.5:
            self.stop()
            return

        left_target = self._ramp(self.left_pwm, left_target)
        right_target = self._ramp(self.right_pwm, right_target)
        self.left_pwm = left_target
        self.right_pwm = right_target

        left_output = self._effective_pwm(left_target)
        right_output = self._effective_pwm(right_target)
        if left_output == 0.0 and right_output == 0.0:
            self.stop()
            return

        if not self.enabled:
            return

        self._driver.ena_pwm(int(left_output))
        self._driver.enb_pwm(int(right_output))
        self._set_direction(MOTOR_IN1_PIN, MOTOR_IN2_PIN, left_target)
        self._set_direction(MOTOR_IN3_PIN, MOTOR_IN4_PIN, right_target)
        self._publish_state(
            left_target if left_output > 0.0 else 0.0,
            right_target if right_output > 0.0 else 0.0,
        )

    def drive(self, linear_m_s, angular_rad_s):
        """Drive using standard ROS signs and physical SI units."""

        linear = _clamp(
            float(linear_m_s),
            -self.MAX_LINEAR_M_S,
            self.MAX_LINEAR_M_S,
        )
        self._filtered_angular = (
            self.STEERING_EMA_ALPHA * float(angular_rad_s)
            + (1.0 - self.STEERING_EMA_ALPHA) * self._filtered_angular
        )

        left_speed = linear + self._filtered_angular * self.TURN_GAIN
        right_speed = linear - self._filtered_angular * self.TURN_GAIN
        pwm_per_meter_second = 100.0 / self.MAX_SPEED_M_S
        self.set_pwm(
            left_speed * pwm_per_meter_second,
            right_speed * pwm_per_meter_second,
        )

    @property
    def is_moving(self):
        return abs(self.left_pwm) > 0.0 or abs(self.right_pwm) > 0.0

    def stop(self):
        self.left_pwm = 0.0
        self.right_pwm = 0.0
        self._filtered_angular = 0.0
        if self.enabled:
            self._driver.digital_write(MOTOR_IN1_PIN, 0)
            self._driver.digital_write(MOTOR_IN2_PIN, 0)
            self._driver.digital_write(MOTOR_IN3_PIN, 0)
            self._driver.digital_write(MOTOR_IN4_PIN, 0)
            self._driver.ena_pwm(0)
            self._driver.enb_pwm(0)
        self._publish_state(0.0, 0.0)


class RobotArm:
    """Calibrated arm, gripper, and camera servo controller."""

    MAX_CAMERA_STEP_DEGREES = 15.0

    def __init__(self, servo_driver=None):
        self._servo = servo_driver
        self.enabled = servo_driver is not None
        self.camera_angle = float(ANGLE_DRIVE_CAMERA)

    def _set(self, channel, angle):
        if self.enabled:
            self._servo.set(channel, int(round(angle)))

    def initialize_driving_pose(self, step_delay_seconds=0.3):
        if not self.enabled:
            return
        pose = (
            (SERVO_BASE_CHANNEL, ANGLE_DRIVE_BASE),
            (SERVO_SHOULDER_CHANNEL, ANGLE_DRIVE_SHOULDER),
            (SERVO_ELBOW_CHANNEL, ANGLE_DRIVE_ELBOW),
            (SERVO_CLAW_CHANNEL, ANGLE_CLAW_OPEN),
            (SERVO_CAMERA_PAN_CHANNEL, ANGLE_DRIVE_CAMERA),
        )
        for index, (channel, angle) in enumerate(pose):
            self._set(channel, angle)
            if index + 1 < len(pose) and step_delay_seconds > 0.0:
                time.sleep(step_delay_seconds)
        self.camera_angle = float(ANGLE_DRIVE_CAMERA)

    def open_gripper(self):
        self._set(SERVO_CLAW_CHANNEL, ANGLE_CLAW_OPEN)

    def close_gripper(self):
        self._set(SERVO_CLAW_CHANNEL, ANGLE_CLAW_CLOSED)

    def set_camera_pan(self, normalized_angle):
        normalized = _clamp(float(normalized_angle), -1.0, 1.0)
        target = ANGLE_DRIVE_CAMERA - normalized * CAMERA_PAN_HALF_RANGE
        target = _clamp(target, CAMERA_PAN_MIN, CAMERA_PAN_MAX)
        difference = target - self.camera_angle
        difference = _clamp(
            difference,
            -self.MAX_CAMERA_STEP_DEGREES,
            self.MAX_CAMERA_STEP_DEGREES,
        )
        self.camera_angle = _clamp(
            self.camera_angle + difference,
            CAMERA_PAN_MIN,
            CAMERA_PAN_MAX,
        )
        self._set(SERVO_CAMERA_PAN_CHANNEL, self.camera_angle)
        return self.camera_angle


class RobotHardware:
    """High-level owner of GPIO, motors, servos, and sensors."""

    def __init__(self, stealth=None):
        if stealth is None:
            stealth = os.environ.get("ROBOT_STEALTH", "").strip().lower() in (
                "1",
                "true",
                "yes",
                "on",
            )
        self.stealth = bool(stealth)
        self.gpio_module = None
        self.vendor_gpio = None
        self.drive = DifferentialDrive()
        self.arm = RobotArm()
        self.sensors = None

        self._load_gpio()
        self._load_sensors()
        self._load_actuators()

    def _load_gpio(self):
        if self.stealth:
            import RPi.GPIO as gpio_module

            gpio_module.setwarnings(False)
            gpio_module.setmode(gpio_module.BCM)
            self.gpio_module = gpio_module
            return

        if XIAOR_LIBRARY_PATH not in sys.path:
            sys.path.append(XIAOR_LIBRARY_PATH)
        import xr_gpio as vendor_gpio

        # Preserve the operational team2 direction ordering.
        vendor_gpio.IN1 = MOTOR_IN1_PIN
        vendor_gpio.IN2 = MOTOR_IN2_PIN
        vendor_gpio.IN3 = MOTOR_IN3_PIN
        vendor_gpio.IN4 = MOTOR_IN4_PIN
        self.vendor_gpio = vendor_gpio
        self.gpio_module = vendor_gpio.GPIO

    def _load_sensors(self):
        if self.gpio_module is not None:
            self.sensors = RobotSensors(self.gpio_module)

    def _load_actuators(self):
        if self.stealth:
            return
        self.drive = DifferentialDrive(self.vendor_gpio)
        try:
            from xr_servo import Servo

            self.arm = RobotArm(Servo())
        except Exception as error:
            # Driving and sensor diagnostics remain usable if the PCA9685 or
            # the vendor servo module is temporarily unavailable.
            print("WARNING: servo driver is unavailable: {}".format(error))

    @property
    def sensors_available(self):
        return self.sensors is not None

    @property
    def actuators_available(self):
        return self.drive.enabled and self.arm.enabled

    def read_sensors(self):
        if self.sensors is None:
            raise RuntimeError("robot sensors are unavailable")
        return self.sensors.read()

    def stop(self):
        self.drive.stop()

    def close(self):
        try:
            self.stop()
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, _exception_type, _exception, _traceback):
        self.close()
        return False
