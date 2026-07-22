#!/usr/bin/env bash
set -u

CONTAINER_NAME="xiao_ros_team11"
HOST_TCP_PORT=10001
CONTAINER_TCP_PORT=10000
CAMERA_HTTP_PORT=10002

echo "=== container ==="
docker ps -a --filter "name=^/${CONTAINER_NAME}$" --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}'

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    exit 1
fi

echo
echo "=== mode ==="
docker inspect --format '{{index .Config.Labels "team11.mode"}}' "$CONTAINER_NAME" 2>/dev/null || true

echo
echo "=== ROS nodes ==="
docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && rosnode list' || true

echo
echo "=== command interfaces ==="
for topic in /cmd_vel /cmd_gripper /cmd_camera_pan; do
    echo "--- $topic ---"
    docker exec "$CONTAINER_NAME" bash -lc \
        "source /opt/ros/noetic/setup.bash && rostopic type $topic && rostopic info $topic" || true
done

echo
echo "=== sensor topics ==="
docker exec "$CONTAINER_NAME" bash -lc \
    "source /opt/ros/noetic/setup.bash && rostopic list | grep '^/sensor/'" || true
docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && rostopic type /sensor/data' || true
echo "--- latest /sensor/data ---"
docker exec "$CONTAINER_NAME" bash -lc \
    'source /opt/ros/noetic/setup.bash && timeout 3 rostopic echo -n 1 /sensor/data' || true

echo
echo "=== isolated TCP endpoint ==="
docker port "$CONTAINER_NAME" "$CONTAINER_TCP_PORT/tcp" 2>/dev/null || true
ss -ltnp 2>/dev/null | grep ":$HOST_TCP_PORT" || true

echo
echo "=== camera service ==="
ss -ltnp 2>/dev/null | grep ":$CAMERA_HTTP_PORT" || true
curl -fsS --max-time 2 "http://127.0.0.1:$CAMERA_HTTP_PORT/health" 2>/dev/null || true
echo
tail -n 8 "$HOME/team1.1/camera_stream.log" 2>/dev/null || true

echo
echo "=== recent logs ==="
for log in endpoint master; do
    echo "--- $log ---"
    docker exec "$CONTAINER_NAME" tail -n 12 "/tmp/$log.log" 2>/dev/null || true
done
