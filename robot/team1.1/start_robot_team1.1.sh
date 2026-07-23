#!/usr/bin/env bash
set -Eeuo pipefail

CONTAINER_NAME="xiao_ros_team11"
IMAGE_NAME="ros_noetic_hardware_v2"
NETWORK_NAME="team11_ros_net"
HOST_TCP_PORT=10001
CONTAINER_TCP_PORT=10000
CAMERA_HTTP_PORT=10002
ROBOT_HOST="$(hostname -I | awk '{print $1}')"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CAMERA_PID_FILE="$SCRIPT_DIR/.camera_stream.pid"
CAMERA_LOG_FILE="$SCRIPT_DIR/camera_stream.log"

STEALTH_MODE=0
case "${1:-}" in
    "") ;;
    --stealth|stealth) STEALTH_MODE=1 ;;
    *)
        echo "Usage: $0 [--stealth]" >&2
        exit 2
        ;;
esac

if [[ "$STEALTH_MODE" -eq 1 ]]; then
    MODE_LABEL="stealth"
else
    MODE_LABEL="normal"
fi

# Keep SSH and the Unity TCP stream stable during long runs.
sudo iw dev wlan0 set power_save off >/dev/null 2>&1 || true

required_files=(
    unity_master_team1.py
    hardware_pinout.py
    camera_stream_team1.py
    smbus.py
    xr_car_light.py
    xr_music.py
)

for file in "${required_files[@]}"; do
    if [[ ! -f "$SCRIPT_DIR/$file" ]]; then
        echo "Required file is missing: $SCRIPT_DIR/$file" >&2
        exit 1
    fi
done

if docker ps --format '{{.Names}}' | grep -qx 'xiao_ros_brain'; then
    echo "The team1 container xiao_ros_brain is running." >&2
    echo "Stop it before starting team1.1 to avoid ROS and hardware conflicts." >&2
    exit 1
fi

echo "=== [1/6] Creating isolated container: $CONTAINER_NAME (mode: $MODE_LABEL) ==="
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || \
    docker network create --driver bridge "$NETWORK_NAME" >/dev/null
docker run -dt \
    --name "$CONTAINER_NAME" \
    --network "$NETWORK_NAME" \
    --publish "$HOST_TCP_PORT:$CONTAINER_TCP_PORT" \
    --privileged \
    --env "ROBOT_STEALTH=$STEALTH_MODE" \
    --label "team11.mode=$MODE_LABEL" \
    -v /dev:/dev \
    "$IMAGE_NAME" bash >/dev/null

echo "=== [2/6] Copying team1.1 nodes ==="
docker cp "$SCRIPT_DIR/unity_master_team1.py" "$CONTAINER_NAME:/root/unity_master_team1.py"
docker cp "$SCRIPT_DIR/hardware_pinout.py" "$CONTAINER_NAME:/root/hardware_pinout.py"
docker cp "$SCRIPT_DIR/smbus.py" "$CONTAINER_NAME:/root/XiaoRGeek/smbus.py"
docker cp "$SCRIPT_DIR/xr_car_light.py" "$CONTAINER_NAME:/root/XiaoRGeek/xr_car_light.py"
docker cp "$SCRIPT_DIR/xr_music.py" "$CONTAINER_NAME:/root/XiaoRGeek/xr_music.py"

if [[ -f "$SCRIPT_DIR/camera_stream_team1.py" ]]; then
    docker cp "$SCRIPT_DIR/camera_stream_team1.py" "$CONTAINER_NAME:/root/camera_stream_team1.py"
fi

echo "=== [3/6] Starting ROS 1 master ==="
docker exec -d "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && exec roscore >/tmp/roscore.log 2>&1'

ros_ready=0
for _ in $(seq 1 20); do
    if docker exec "$CONTAINER_NAME" bash -lc \
        'source /opt/ros/noetic/setup.bash && rosnode list >/dev/null 2>&1'; then
        ros_ready=1
        break
    fi
    sleep 0.5
done

if [[ "$ros_ready" -ne 1 ]]; then
    echo "ROS master did not become ready." >&2
    docker exec "$CONTAINER_NAME" cat /tmp/roscore.log 2>/dev/null || true
    exit 1
fi

echo "=== [4/6] Starting ROS-TCP-Endpoint on container port $CONTAINER_TCP_PORT ==="
docker exec -d "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && source /root/catkin_ws/devel/setup.bash && exec roslaunch ros_tcp_endpoint endpoint.launch tcp_ip:=0.0.0.0 tcp_port:=10000 >/tmp/endpoint.log 2>&1'

echo "=== [5/6] Starting robot hardware and sensor nodes ==="
docker exec -d "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && exec python3 -u /root/unity_master_team1.py >/tmp/master.log 2>&1'

echo "=== [6/6] Starting read-only camera service on port $CAMERA_HTTP_PORT ==="
if [[ -f "$CAMERA_PID_FILE" ]]; then
    old_camera_pid="$(cat "$CAMERA_PID_FILE" 2>/dev/null || true)"
    if [[ "$old_camera_pid" =~ ^[0-9]+$ ]] && kill -0 "$old_camera_pid" 2>/dev/null; then
        kill "$old_camera_pid" 2>/dev/null || true
        sleep 0.5
    fi
fi
nohup python3 -u "$SCRIPT_DIR/camera_stream_team1.py" \
    --port "$CAMERA_HTTP_PORT" \
    --upstream http://127.0.0.1:8080/ \
    >"$CAMERA_LOG_FILE" 2>&1 &
echo $! >"$CAMERA_PID_FILE"

sleep 3

if ! docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && rostopic info /cmd_vel' | grep -q '/unity_robot_master'; then
    echo "The robot node did not subscribe to /cmd_vel." >&2
    docker exec "$CONTAINER_NAME" cat /tmp/master.log 2>/dev/null || true
    exit 1
fi

for topic in /cmd_gripper /cmd_camera_pan; do
    if ! docker exec "$CONTAINER_NAME" bash -lc \
        "source /opt/ros/noetic/setup.bash && rostopic info $topic" | grep -q '/unity_robot_master'; then
        echo "The robot node did not subscribe to $topic." >&2
        docker exec "$CONTAINER_NAME" cat /tmp/master.log 2>/dev/null || true
        exit 1
    fi
done

if ! docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && timeout 5 rostopic echo -n 1 /sensor/data >/dev/null'; then
    echo "The robot node advertised /sensor/data but did not publish a message within 5 seconds." >&2
    docker exec "$CONTAINER_NAME" cat /tmp/master.log 2>/dev/null || true
    exit 1
fi

if ! docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && rosnode list' | grep -q '/unity_endpoint'; then
    echo "ROS-TCP-Endpoint did not start." >&2
    docker exec "$CONTAINER_NAME" cat /tmp/endpoint.log 2>/dev/null || true
    exit 1
fi

echo
echo "team1.1 is ready. Unity endpoint: $ROBOT_HOST:$HOST_TCP_PORT"
echo "Robot mode: $MODE_LABEL"
if [[ "$STEALTH_MODE" -eq 1 ]]; then
    echo "Actuators: DISABLED (motor/servo drivers were not initialized)"
fi
echo "Velocity topic: /cmd_vel (geometry_msgs/Twist)"
echo "Gripper topic: /cmd_gripper (std_msgs/Int32; 1=grab, 2=release)"
echo "Camera topic: /cmd_camera_pan (std_msgs/Float32; normalized -1..1)"
echo "Sensor topic: /sensor/data (geometry_msgs/Quaternion)"
echo "Camera frame: http://$ROBOT_HOST:$CAMERA_HTTP_PORT/frame.jpg"
echo "Stop safely with: $SCRIPT_DIR/stop_robot_team1.1.sh"
echo "Diagnostics:      $SCRIPT_DIR/status_robot_team1.1.sh"
