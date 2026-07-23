# team1.1 — ROS 1 bridge for Unity

This directory is deployed as `/home/pi/team1.1`. It is independent from `/home/pi/team1` and `/home/pi/team2`.

Start the robot ROS environment and Unity TCP endpoint:

```bash
cd ~/team1.1
./start_robot_team1.1.sh
```

Start in stealth mode (ROS-TCP and sensors enabled, motors and servos disabled):

```bash
./start_robot_team1.1.sh --stealth
```

Stealth mode does not import the XiaoR motor, servo, or combined ultrasonic/servo
drivers. `/cmd_vel`, `/cmd_gripper`, and `/cmd_camera_pan`
cannot activate physical actuators. IR sensors and ultrasonic ranging remain available.

Check nodes, topics, logs and the isolated TCP port 10001:

```bash
./status_robot_team1.1.sh
```

Stop the robot safely:

```bash
./stop_robot_team1.1.sh
```

Unity discovers robot `158` automatically and connects to its current DHCP address
on TCP port `10001`. The Raspberry Pi identity service listens on port `10003`.
`make robot-deploy` installs and enables that identity service automatically.

| Topic | ROS type | Direction |
| --- | --- | --- |
| `/cmd_vel` | `geometry_msgs/Twist` | Unity to robot |
| `/cmd_gripper` | `std_msgs/Int32` | Unity to robot (`1=grab`, `2=release`) |
| `/cmd_camera_pan` | `std_msgs/Float32` | Unity to robot (normalized `-1..1`) |
| `/sensor/data` | `geometry_msgs/Quaternion` | robot to Unity |
| `/sensor/pwm` | `geometry_msgs/Vector3` | robot diagnostics |

The Raspberry Pi owns the calibrated driving pose and applies it once during
normal-mode startup. Policy gripper commands change only the claw: 50 degrees
open and 89 degrees closed. Base, shoulder and elbow are not resent by Unity,
so their startup angles persist while driving and grabbing. Unity converts its
right-positive steering to ROS left-positive `angular.z`; the robot adapter
maps normalized camera pan to the physically reversed servo axis.

The container keeps ROS and its internal endpoint on a dedicated Docker bridge network
named `team11_ros_net`; only TCP port `10001` is published to the Raspberry Pi host.

The robot camera is available in Unity through the team1.1 read-only camera service:
`http://<discovered-robot-address>:10002/frame.jpg`. When team2 already owns `/dev/video0`, the
service relays its existing MJPEG stream from `127.0.0.1:8080` without opening the
camera a second time or modifying the team2 process.

Sensor data is published as `geometry_msgs/Quaternion` on `/sensor/data`:
`x` is ultrasonic distance in meters, `y` is the physical left IR,
`z` is the physical right IR, and `w` is the gripper IR.

The canonical pinout is kept in `hardware_pinout.py`. All GPIO numbers use
BCM numbering. The sensor mapping follows the physical connections:
physical left IR = GPIO 18, physical right IR = GPIO 25, and gripper IR =
`IR_M`/GPIO 22,
all active-low. Ultrasonic uses TRIG GPIO 17 and ECHO GPIO 4. Operational
motor direction pins follow team2: ENA/ENB = 13/20 and
IN1..IN4 = 16/19/26/21. Servo channels
are base/shoulder/elbow/claw/camera = 1/2/3/4/7.

The start script creates only the Docker container `xiao_ros_team11` and network
`team11_ros_net`. It does not remove or rewrite team1/team2 containers. ROS networking
and the Unity TCP endpoint are isolated from the other teams. Two normal-mode containers
still must not control the same GPIO/I2C hardware simultaneously; use `--stealth` for
parallel network and sensor diagnostics without activating team1.1 actuators.
