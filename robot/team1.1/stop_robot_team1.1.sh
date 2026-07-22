#!/usr/bin/env bash
set -u

CONTAINER_NAME="xiao_ros_team11"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CAMERA_PID_FILE="$SCRIPT_DIR/.camera_stream.pid"

if [[ -f "$CAMERA_PID_FILE" ]]; then
    camera_pid="$(cat "$CAMERA_PID_FILE" 2>/dev/null || true)"
    if [[ "$camera_pid" =~ ^[0-9]+$ ]] && kill -0 "$camera_pid" 2>/dev/null; then
        kill "$camera_pid" 2>/dev/null || true
    fi
    rm -f "$CAMERA_PID_FILE"
fi

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    echo "team1.1 container and camera service are not running."
    exit 0
fi

echo "Publishing a zero velocity command before shutdown..."
docker exec "$CONTAINER_NAME" bash -lc \
    "source /opt/ros/noetic/setup.bash && rostopic pub -1 /cmd_vel geometry_msgs/Twist '{linear: {x: 0.0, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}'" \
    >/dev/null 2>&1 || true
sleep 1

docker rm -f "$CONTAINER_NAME" >/dev/null
echo "team1.1 stopped."
