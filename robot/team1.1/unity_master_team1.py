#!/usr/bin/env python3
import sys
import os
import rospy
import time
import traceback
import atexit
from geometry_msgs.msg import Twist, Vector3, Quaternion
from std_msgs.msg import Int32, Float32
from hardware_pinout import (
    SENSOR_LEFT_IR_PIN,
    SENSOR_RIGHT_IR_PIN,
    SENSOR_GRIPPER_IR_PIN,
    MOTOR_IN1_PIN,
    MOTOR_IN2_PIN,
    MOTOR_IN3_PIN,
    MOTOR_IN4_PIN,
    ULTRASONIC_TRIGGER_PIN,
    ULTRASONIC_ECHO_PIN,
    SERVO_BASE_CHANNEL,
    SERVO_SHOULDER_CHANNEL,
    SERVO_ELBOW_CHANNEL,
    SERVO_CLAW_CHANNEL,
    SERVO_CAMERA_PAN_CHANNEL,
)

# Добавляем путь к библиотекам XiaoR Geek
sys.path.append('/root/XiaoRGeek')

STEALTH_MODE = os.environ.get('ROBOT_STEALTH', '').strip().lower() in ('1', 'true', 'yes', 'on')

print("--- Инициализация XiaoR драйверов ---")
print("ROBOT MODE: %s" % ('STEALTH (actuators disabled)' if STEALTH_MODE else 'NORMAL'))

HAS_GPIO = False
try:
    if STEALTH_MODE:
        import RPi.GPIO as sensor_gpio
        sensor_gpio.setwarnings(False)
        sensor_gpio.setmode(sensor_gpio.BCM)

        class SensorOnlyGPIO:
            GPIO = sensor_gpio

            @staticmethod
            def digital_read(pin):
                return sensor_gpio.input(pin)

        gpio = SensorOnlyGPIO()
    else:
        import xr_gpio as gpio
        # Keep the operational team2 direction ordering. Swapping both IN pairs
        # reverses both differential-drive linear and angular velocity.
        gpio.IN1 = MOTOR_IN1_PIN
        gpio.IN2 = MOTOR_IN2_PIN
        gpio.IN3 = MOTOR_IN3_PIN
        gpio.IN4 = MOTOR_IN4_PIN
    HAS_GPIO = True
    if STEALTH_MODE:
        print("✅ Sensor-only GPIO loaded; motor driver was not imported")
    else:
        print("✅ Драйвер моторов (xr_gpio) загружен успешно!")
except Exception as e:
    print("❌ ОШИБКА загрузки GPIO:")
    print(traceback.format_exc())

if HAS_GPIO:
    gpio.GPIO.setup(SENSOR_LEFT_IR_PIN, gpio.GPIO.IN, pull_up_down=gpio.GPIO.PUD_UP)
    gpio.GPIO.setup(SENSOR_RIGHT_IR_PIN, gpio.GPIO.IN, pull_up_down=gpio.GPIO.PUD_UP)
    gpio.GPIO.setup(SENSOR_GRIPPER_IR_PIN, gpio.GPIO.IN, pull_up_down=gpio.GPIO.PUD_UP)

class SafeUltrasonic:
    TRIG_PIN = ULTRASONIC_TRIGGER_PIN
    ECHO_PIN = ULTRASONIC_ECHO_PIN
    ECHO_TIMEOUT_SECONDS = 0.03

    def __init__(self, gpio_module):
        self.GPIO = gpio_module
        self.GPIO.setup(self.TRIG_PIN, self.GPIO.OUT, initial=self.GPIO.LOW)
        self.GPIO.setup(self.ECHO_PIN, self.GPIO.IN, pull_up_down=self.GPIO.PUD_DOWN)

    def get_distance(self):
        self.GPIO.output(self.TRIG_PIN, self.GPIO.LOW)
        time.sleep(0.000002)
        self.GPIO.output(self.TRIG_PIN, self.GPIO.HIGH)
        time.sleep(0.00001)
        self.GPIO.output(self.TRIG_PIN, self.GPIO.LOW)

        deadline = time.monotonic() + self.ECHO_TIMEOUT_SECONDS
        while self.GPIO.input(self.ECHO_PIN) == self.GPIO.LOW:
            if time.monotonic() >= deadline:
                return 500.0

        pulse_start = time.monotonic()
        deadline = pulse_start + self.ECHO_TIMEOUT_SECONDS
        while self.GPIO.input(self.ECHO_PIN) == self.GPIO.HIGH:
            if time.monotonic() >= deadline:
                return 500.0

        return (time.monotonic() - pulse_start) * 17150.0


HAS_SENSORS = False
us = None
try:
    if STEALTH_MODE:
        if not HAS_GPIO:
            raise RuntimeError('sensor-only GPIO is unavailable')
        us = SafeUltrasonic(gpio.GPIO)
        print("✅ Direct ultrasonic GPIO loaded; servo driver was not imported")
    else:
        # The vendor Ultrasonic.get_distance() has no timeout while waiting for
        # ECHO to rise. A disconnected or brown-out-reset sensor therefore
        # spins forever and stops every /sensor/data publication. Use the same
        # bounded GPIO implementation as stealth mode in normal mode too.
        us = SafeUltrasonic(gpio.GPIO)
        print("✅ Безопасный драйвер Ultrasonic с таймаутом загружен успешно!")
    HAS_SENSORS = True
except Exception as e:
    print("❌ ОШИБКА загрузки ultrasonic:")
    print(traceback.format_exc())

HAS_SERVO = False
if STEALTH_MODE:
    print("✅ Servo drivers disabled by stealth mode")
else:
    try:
        # SafeUltrasonic is GPIO-only and does not instantiate a servo driver,
        # so normal mode owns exactly one explicit Servo instance.
        from xr_servo import Servo
        servo = Servo()
        HAS_SERVO = True
        print("✅ Драйвер сервомоторов (xr_servo) загружен успешно!")
    except Exception as e:
        print("❌ ОШИБКА загрузки xr_servo:")
        print(traceback.format_exc())


# ==========================================
# КОНФИГУРАЦИЯ И ЛОГИКА МОТОРОВ
# ==========================================
L = 0.15
MAX_SPEED_M_S = 0.5
PWM_CONVERSION_FACTOR = 100.0 / MAX_SPEED_M_S
# control6.py reliably starts both tracks at 60%. At the previous 35% one track
# remained in static friction, so a straight /cmd_vel command became a pivot.
MIN_MOTOR_PWM = 60

# --- SOFT-START: Защита от пускового тока и Back-EMF ---
# Максимальное изменение PWM за 1 тик. При 50Hz: разгон 0→100% за ~0.14 сек.
MAX_PWM_STEP = 15
prev_pwm_left = 0.0
prev_pwm_right = 0.0
pwm_pub = None
prev_ang_z = 0.0

def clamp_pwm(val):
    return max(min(val, 100.0), -100.0)

def set_motors_pwm(pwm_left, pwm_right):
    global prev_pwm_left, prev_pwm_right, pwm_pub
    if STEALTH_MODE or not HAS_GPIO: return

    # Полярность откалибрована относительно физического «вперёд» робота:
    # левый вперёд = IN2, правый вперёд = IN4.

    # v18 FIX: HARD STOP — если оба таргета нулевые, стопим мгновенно без рампы.
    # Без этого рампа давала 3-4 тика остаточного PWM, а MIN_MOTOR_PWM бустил его до 35.
    if abs(pwm_left) < 0.5 and abs(pwm_right) < 0.5:
        prev_pwm_left = 0.0
        prev_pwm_right = 0.0
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 0)
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 0)
        gpio.ena_pwm(0)
        gpio.enb_pwm(0)
        if pwm_pub is not None:
            try:
                pwm_pub.publish(Vector3(0.0, 0.0, 0.0))
            except:
                pass
        return

    # --- SOFT-START: Ограничиваем скорость нарастания ---
    delta_l = pwm_left - prev_pwm_left
    if abs(delta_l) > MAX_PWM_STEP:
        pwm_left = prev_pwm_left + (MAX_PWM_STEP if delta_l > 0 else -MAX_PWM_STEP)

    delta_r = pwm_right - prev_pwm_right
    if abs(delta_r) > MAX_PWM_STEP:
        pwm_right = prev_pwm_right + (MAX_PWM_STEP if delta_r > 0 else -MAX_PWM_STEP)

    prev_pwm_left = pwm_left
    prev_pwm_right = pwm_right

    abs_l = abs(pwm_left)
    # FIX: Если PWM ниже мёртвой зоны мотора — СТОП, а не буст до 35.
    # Без этого при steering>0.3 один мотор получал PWM=-2 → бустился до -35 → робот крутился.
    MOTOR_DEAD_ZONE = 10  # PWM ниже 10% — мотор всё равно не крутится, ставим 0
    if abs_l < MOTOR_DEAD_ZONE:
        abs_l = 0
    elif abs_l < MIN_MOTOR_PWM:
        abs_l = MIN_MOTOR_PWM
    abs_r = abs(pwm_right)
    if abs_r < MOTOR_DEAD_ZONE:
        abs_r = 0
    elif abs_r < MIN_MOTOR_PWM:
        abs_r = MIN_MOTOR_PWM

    if int(abs_l) == 0 and int(abs_r) == 0:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 0)
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 0)
        gpio.ena_pwm(0)
        gpio.enb_pwm(0)
        if pwm_pub is not None:
            try:
                pwm_pub.publish(Vector3(0.0, 0.0, 0.0))
            except:
                pass
        return

    gpio.ena_pwm(int(abs_l))
    gpio.enb_pwm(int(abs_r))

    if pwm_left > 0:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 1)
    elif pwm_left < 0:
        gpio.digital_write(gpio.IN1, 1)
        gpio.digital_write(gpio.IN2, 0)
    else:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 0)

    if pwm_right > 0:
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 1)
    elif pwm_right < 0:
        gpio.digital_write(gpio.IN3, 1)
        gpio.digital_write(gpio.IN4, 0)
    else:
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 0)

    if pwm_pub is not None:
        try:
            logical_left = pwm_left if abs_l > 0 else 0.0
            logical_right = pwm_right if abs_r > 0 else 0.0
            pwm_pub.publish(Vector3(float(logical_left), float(logical_right), 0.0))
        except:
            pass

SAFETY_STOP_CM = 50  # Локальный стоп если УЗ < 50см и робот едет ВПЕРЁД

def vel_callback(data):
    global filtered_cm, last_cmd_vel_time, prev_ang_z
    last_cmd_vel_time = time.time()

    # Дифференциальный привод: v = linear ± angular * TURN_K
    # v27: Снижен TURN_K до 0.25 для плавности поворота, MAX_LINEAR = 0.25 (без изменений)
    TURN_K = 0.25
    MAX_LINEAR = 0.25  # Ограничение: модель даёт gas=1.0, но робот едет max 50%

    # Стандарт ROS: положительный linear.x означает движение физически вперёд.
    lin_x = max(min(data.linear.x, MAX_LINEAR), -MAX_LINEAR)

    # EMA сглаживание для угловой скорости (steering)
    EMA_STEER = 0.40
    ang_z = EMA_STEER * data.angular.z + (1.0 - EMA_STEER) * prev_ang_z
    prev_ang_z = ang_z

    # Стандартный знак angular.z: положительный = поворот влево.
    # Каналы двигателя A/B физически стоят на противоположных сторонах шасси
    # относительно исходных имён, поэтому их угловые добавки зеркальны.
    v_left  = lin_x + (ang_z * TURN_K)
    v_right = lin_x - (ang_z * TURN_K)
    pwm_left = clamp_pwm(v_left * PWM_CONVERSION_FACTOR)
    pwm_right = clamp_pwm(v_right * PWM_CONVERSION_FACTOR)

    set_motors_pwm(pwm_left, pwm_right)

# --- WATCHDOG: Если Unity отключился, стопим моторы ---
last_cmd_vel_time = time.time()
WATCHDOG_TIMEOUT = 0.5  # секунд без команды → СТОП

def watchdog_callback(event):
    global prev_pwm_left, prev_pwm_right
    if time.time() - last_cmd_vel_time > WATCHDOG_TIMEOUT:
        if abs(prev_pwm_left) > 0 or abs(prev_pwm_right) > 0:
            print("⚠️ WATCHDOG: Нет /cmd_vel %.1f сек! АВАРИЙНАЯ ОСТАНОВКА!" % WATCHDOG_TIMEOUT)
            set_motors_pwm(0, 0)
            prev_pwm_left = 0.0
            prev_pwm_right = 0.0


# ==========================================
# КОНФИГУРАЦИЯ И ЛОГИКА СЕРВОМОТОРОВ
# ==========================================
SERVO_BASE = SERVO_BASE_CHANNEL
SERVO_SHOULDER = SERVO_SHOULDER_CHANNEL
SERVO_ELBOW = SERVO_ELBOW_CHANNEL
SERVO_CLAW = SERVO_CLAW_CHANNEL
SERVO_CAMERA_PAN = SERVO_CAMERA_PAN_CHANNEL

# Single robot-side source of truth for the persistent driving pose. Unity does
# not resend these calibrated angles while the robot drives.
ANGLE_DRIVE_BASE = 90
ANGLE_DRIVE_SHOULDER = 150
ANGLE_DRIVE_ELBOW = 90
ANGLE_DRIVE_CAMERA = 90
ANGLE_CLAW_OPEN = 50
ANGLE_CLAW_CLOSED = 89

CAMERA_PAN_MIN = 20
CAMERA_PAN_MAX = 160
CAMERA_PAN_HALF_RANGE = 70

def init_arm():
    global current_camera_angle
    if not HAS_SERVO: return
    print("Инициализация начальной позы манипулятора и камеры...")
    servo.set(SERVO_BASE, ANGLE_DRIVE_BASE)
    time.sleep(0.3)
    servo.set(SERVO_SHOULDER, ANGLE_DRIVE_SHOULDER)
    time.sleep(0.3)
    servo.set(SERVO_ELBOW, ANGLE_DRIVE_ELBOW)
    time.sleep(0.3)
    servo.set(SERVO_CLAW, ANGLE_CLAW_OPEN)
    time.sleep(0.3)
    servo.set(SERVO_CAMERA_PAN, ANGLE_DRIVE_CAMERA)
    current_camera_angle = ANGLE_DRIVE_CAMERA
    print("Рука поднята, камера отцентрирована. Робот готов!")

def gripper_callback(data):
    cmd = data.data
    if not HAS_SERVO: return
    if cmd == 1:
        # Grab: the driving pose remains unchanged; only the claw closes.
        servo.set(SERVO_CLAW, ANGLE_CLAW_CLOSED)
    elif cmd == 2:
        # Release: the driving pose remains unchanged; only the claw opens.
        servo.set(SERVO_CLAW, ANGLE_CLAW_OPEN)

# --- Плавное слежение камеры ---
current_camera_angle = ANGLE_DRIVE_CAMERA  # Текущий угол серво
MAX_CAMERA_STEP = 15       # Макс градусов за один тик (плавное движение)

def camera_callback(data):
    global current_camera_angle
    if not HAS_SERVO: return
    yaw = data.data
    # ИНВЕРСИЯ: Если в Unity камера в одну сторону, а в реальности в другую — меняем знак здесь
    target = ANGLE_DRIVE_CAMERA - (yaw * CAMERA_PAN_HALF_RANGE)
    target = max(CAMERA_PAN_MIN, min(CAMERA_PAN_MAX, target))

    # Плавное движение: не больше MAX_CAMERA_STEP градусов за шаг
    diff = target - current_camera_angle
    if abs(diff) > MAX_CAMERA_STEP:
        diff = MAX_CAMERA_STEP if diff > 0 else -MAX_CAMERA_STEP

    current_camera_angle += diff
    current_camera_angle = max(CAMERA_PAN_MIN, min(CAMERA_PAN_MAX, current_camera_angle))
    servo.set(SERVO_CAMERA_PAN, int(current_camera_angle))

# ==========================================
# ЧТЕНИЕ СЕНСОРОВ В ТАЙМЕРЕ
# ==========================================
# --- Фильтрация датчиков ---
us_history = [100.0, 100.0, 100.0]
filtered_cm = 500.0
ir_l_history = [0, 0, 0, 0, 0]
ir_r_history = [0, 0, 0, 0, 0]
ir_center_history = [0, 0, 0, 0, 0]  # Центральный ИК датчик (IO_1 / BCM GPIO 23)

def sensor_timer_callback(event):
    global us_history, ir_l_history, ir_r_history, ir_center_history, filtered_cm
    if not HAS_SENSORS or sensor_pub is None: return
    try:
        msg = Quaternion()
        # 1. Ультразвук с медианным фильтром (окно 3)
        dist_cm = us.get_distance()
        if dist_cm <= 0 or dist_cm > 500: dist_cm = 500.0

        us_history.pop(0)
        us_history.append(dist_cm)

        # Медиана убирает одиночные "вылеты" (0 или 500)
        sorted_us = sorted(us_history)
        filtered_cm = sorted_us[1]
        msg.x = filtered_cm / 100.0

        # 2. ИК сенсоры: ROS y = физический левый, ROS z = физический правый.
        # Фактическая разводка этой сборки определена по живым срабатываниям:
        # физический левый = GPIO 24, физический правый = GPIO 25.
        ir_l = 1 if gpio.digital_read(SENSOR_LEFT_IR_PIN) == 0 else 0
        ir_r = 1 if gpio.digital_read(SENSOR_RIGHT_IR_PIN) == 0 else 0

        ir_l_history.pop(0)
        ir_l_history.append(ir_l)
        ir_r_history.pop(0)
        ir_r_history.append(ir_r)

        # Только если последние 2 из 3 значений == 1, считаем препятствие (фильтр дребезга)
        msg.y = float(1 if sum(ir_l_history[-3:]) >= 2 else 0)
        msg.z = float(1 if sum(ir_r_history[-3:]) >= 2 else 0)

        # 3. Центральный ИК датчик: ROS w = физический разъём IO_1 (BCM GPIO 23).
        ir_center = 1 if gpio.digital_read(SENSOR_GRIPPER_IR_PIN) == 0 else 0
        ir_center_history.pop(0)
        ir_center_history.append(ir_center)
        msg.w = float(1 if sum(ir_center_history[-3:]) >= 2 else 0)

        sensor_pub.publish(msg)

    except Exception as e:
        print(f"❌ ОШИБКА В ТАЙМЕРЕ СЕНСОРОВ: {e}")

# ==========================================
# ГЛАВНЫЙ БЛОК ROS
# ==========================================
def listener():
    global sensor_pub, pwm_pub
    rospy.init_node('unity_robot_master', anonymous=True)

    if STEALTH_MODE:
        rospy.logwarn('STEALTH MODE: motor and servo commands are physically disabled')

    rospy.Subscriber('/cmd_vel', Twist, vel_callback)

    # Register command interfaces in both modes. The callbacks are no-ops when
    # HAS_SERVO is false, so stealth mode can validate the complete ROS contract
    # without importing or activating the actuator drivers.
    rospy.Subscriber('/cmd_gripper', Int32, gripper_callback)
    rospy.Subscriber('/cmd_camera_pan', Float32, camera_callback)

    if HAS_SERVO:
        init_arm()

    if HAS_SENSORS:
        sensor_pub = rospy.Publisher('/sensor/data', Quaternion, queue_size=10)
        rospy.Timer(rospy.Duration(0.1), sensor_timer_callback)

    pwm_pub = rospy.Publisher('/sensor/pwm', Vector3, queue_size=10)

    # Watchdog: каждые 0.2 сек проверяем, жив ли /cmd_vel
    rospy.Timer(rospy.Duration(0.2), watchdog_callback)

    rospy.spin()

# --- АВАРИЙНАЯ ОСТАНОВКА МОТОРОВ ПРИ ВЫХОДЕ ---
def emergency_stop():
    try:
        if HAS_GPIO and not STEALTH_MODE:
            gpio.digital_write(gpio.IN1, 0)
            gpio.digital_write(gpio.IN2, 0)
            gpio.digital_write(gpio.IN3, 0)
            gpio.digital_write(gpio.IN4, 0)
            gpio.ena_pwm(0)
            gpio.enb_pwm(0)
    except:
        pass

atexit.register(emergency_stop)

if __name__ == '__main__':
    try:
        listener()
    except rospy.ROSInterruptException:
        pass
    finally:
        emergency_stop()
