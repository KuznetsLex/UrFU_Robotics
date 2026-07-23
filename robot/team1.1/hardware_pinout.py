"""Physical XiaoR pinout selected for the team1.1 robot.

Sources:
  robot_hardware.py
  /home/pi/team2/xr_gpio.py
  /home/pi/team2/unity_master_team2.py

All GPIO numbers use Broadcom (BCM) numbering. IR sensors are active-low.
"""

# L298 motor driver.
MOTOR_ENABLE_A_PIN = 13
MOTOR_ENABLE_B_PIN = 20
MOTOR_IN1_PIN = 16
MOTOR_IN2_PIN = 19
MOTOR_IN3_PIN = 26
MOTOR_IN4_PIN = 21

BUZZER_PIN = 10

# Ultrasonic range finder.
ULTRASONIC_ECHO_PIN = 4
ULTRASONIC_TRIGGER_PIN = 17

# Physical sensor connections confirmed on the robot (BCM numbering).
SENSOR_LEFT_IR_PIN = 18
SENSOR_RIGHT_IR_PIN = 25
SENSOR_GRIPPER_IR_PIN = 22

# PCA9685 servo channels.
SERVO_BASE_CHANNEL = 1
SERVO_SHOULDER_CHANNEL = 2
SERVO_ELBOW_CHANNEL = 3
SERVO_CLAW_CHANNEL = 4
SERVO_CAMERA_PAN_CHANNEL = 7
