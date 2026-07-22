#!/usr/bin/env bash
# Запускает mlagents-learn с DiagnosticLogger, сама подставляя путь для
# --env-args --diagLogDir по run-id — run-id указывается только один раз,
# вместо того чтобы дублировать его вручную в --run-id и в пути.
#
# Использование:
#   tools/run_training.sh test
#   tools/run_training.sh small_input_rand_ball_fix2
set -euo pipefail

if [ -z "${1:-}" ]; then
    echo "Использование: $0 <run-id>" >&2
    exit 1
fi

RUN_ID="$1"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

mlagents-learn --run-id="$RUN_ID" --env=Build/YandexCamp.app --num-envs=1 --no-graphics --force \
    --env-args --diagLogDir "$PROJECT_ROOT/results/$RUN_ID"
