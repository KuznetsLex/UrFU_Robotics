#!/usr/bin/env bash
# Запускает mlagents-learn с DiagnosticLogger, сама подставляя путь для
# --env-args --diagLogDir по run-id — run-id указывается только один раз,
# вместо того чтобы дублировать его вручную в --run-id и в пути.
#
# При каждом старте или возобновлении (--resume) сохраняет снимок конфигов
# (config.yaml — гиперпараметры ML-Agents, training_config.yaml — наш
# runtime-конфиг Unity) и параметров запуска (--num-envs, --resume,
# --initialize-from) в results/<run-id>/config_snapshots — с таймстампом в
# имени файла, а не перезаписью, чтобы не терять историю, если конфиг
# правили между несколькими resume одного и того же run-id.
#
# Использование:
#   tools/run_training.sh test
#   tools/run_training.sh test --num-envs 4
#   tools/run_training.sh test --resume
#   tools/run_training.sh test --initialize-from previous_run_id
set -euo pipefail

if [ -z "${1:-}" ]; then
    echo "Использование: $0 <run-id> [--num-envs N] [--resume] [--initialize-from RUN_ID]" >&2
    exit 1
fi

RUN_ID="$1"
shift

NUM_ENVS=1
RESUME=0
INITIALIZE_FROM=""

while [ $# -gt 0 ]; do
    case "$1" in
        --num-envs)
            NUM_ENVS="$2"
            shift 2
            ;;
        --resume)
            RESUME=1
            shift
            ;;
        --initialize-from)
            INITIALIZE_FROM="$2"
            shift 2
            ;;
        *)
            echo "Неизвестный аргумент: $1" >&2
            exit 1
            ;;
    esac
done

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# ---------- Снимок конфигов и параметров запуска ----------
SNAPSHOT_DIR="$PROJECT_ROOT/results/$RUN_ID/config_snapshots"
mkdir -p "$SNAPSHOT_DIR"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"

cp "$PROJECT_ROOT/config.yaml" "$SNAPSHOT_DIR/${TIMESTAMP}_config.yaml"
cp "$PROJECT_ROOT/Assets/StreamingAssets/training_config.yaml" \
    "$SNAPSHOT_DIR/${TIMESTAMP}_training_config.yaml"

{
    echo "date: $(date -Iseconds)"
    echo "run_id: $RUN_ID"
    echo "num_envs: $NUM_ENVS"
    echo "resume: $([ "$RESUME" = 1 ] && echo true || echo false)"
    echo "initialize_from: ${INITIALIZE_FROM:-none}"
} > "$SNAPSHOT_DIR/${TIMESTAMP}_launch_params.txt"

# ---------- Запуск обучения ----------
ARGS=(--run-id="$RUN_ID" --env=Build/YandexCamp.app --num-envs="$NUM_ENVS" --no-graphics
    --env-args --diagLogDir "$PROJECT_ROOT/results/$RUN_ID")

if [ "$RESUME" = 1 ]; then
    ARGS+=(--resume)
else
    # --force и --resume взаимоисключающие: --force используем только на
    # свежий запуск (перезаписывает существующий run-id), при --resume не
    # добавляем, чтобы ML-Agents продолжил, а не начал заново.
    ARGS+=(--force)
fi

if [ -n "$INITIALIZE_FROM" ]; then
    ARGS+=(--initialize-from="$INITIALIZE_FROM")
fi

mlagents-learn "${ARGS[@]}"
