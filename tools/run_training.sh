#!/usr/bin/env bash
# Запускает mlagents-learn с DiagnosticLogger, сама подставляя путь для
# --env-args --diagLogDir по run-id — run-id указывается только один раз,
# вместо того чтобы дублировать его вручную в --run-id и в пути.
#
# Использование:
#   tools/run_training.sh test              # новый прогон (--force), CSV перезаписываются
#   tools/run_training.sh test --resume     # продолжить с чекпоинта; DiagnosticLogger
#                                            # дописывает CSV вместо перезаписи
set -euo pipefail

if [ -z "${1:-}" ]; then
    echo "Использование: $0 <run-id> [--resume]" >&2
    exit 1
fi

RUN_ID="$1"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Без массивов: пустой массив под "set -u" в bash 3.2 (дефолтный /bin/bash на
# macOS) даёт "unbound variable" при "${ARR[@]}", в отличие от bash 4+.
if [ "${2:-}" = "--resume" ]; then
    mlagents-learn --run-id="$RUN_ID" --env=Build/YandexCamp.app --num-envs=1 --no-graphics --resume \
        --env-args --diagLogDir "$PROJECT_ROOT/results/$RUN_ID" --resume
else
    mlagents-learn --run-id="$RUN_ID" --env=Build/YandexCamp.app --num-envs=1 --no-graphics --force \
        --env-args --diagLogDir "$PROJECT_ROOT/results/$RUN_ID"
fi
