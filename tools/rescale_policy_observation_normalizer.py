#!/usr/bin/env python3
"""Migrate the deployed ML-Agents ONNX normalizer to normalized observations."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import onnx
from onnx import numpy_helper


CONTRACT_KEY = "team11.observation_contract"
CONTRACT_VALUE = "ultrasonic_0_1_prev_gas_minus1_1_v1"
OBSERVATIONS_PER_STACK = 15
STACK_COUNT = 5
ULTRASONIC_INDEX = 0
PREVIOUS_GAS_INDEX = 10


def initializer_by_name(model: onnx.ModelProto, name: str):
    return next(value for value in model.graph.initializer if value.name == name)


def replace_initializer(model: onnx.ModelProto, name: str, values: np.ndarray) -> None:
    target = initializer_by_name(model, name)
    target.CopyFrom(numpy_helper.from_array(values.astype(np.float32), name=name))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("model", type=Path)
    parser.add_argument("--world-units-per-meter", type=float, default=10.0)
    parser.add_argument("--ultrasonic-max-meters", type=float, default=5.0)
    parser.add_argument("--max-linear-command", type=float, default=0.25)
    args = parser.parse_args()

    model = onnx.load(args.model)
    metadata = {item.key: item.value for item in model.metadata_props}
    if metadata.get(CONTRACT_KEY) == CONTRACT_VALUE:
        print(f"Normalizer already uses {CONTRACT_VALUE}")
        return 0

    sub = next(
        node for node in model.graph.node
        if node.op_type == "Sub" and "normalizer" in node.name
    )
    div = next(
        node for node in model.graph.node
        if node.op_type == "Div" and "normalizer" in node.name
    )
    mean_name = sub.input[1]
    scale_name = div.input[1]
    mean = numpy_helper.to_array(initializer_by_name(model, mean_name)).copy()
    scale = numpy_helper.to_array(initializer_by_name(model, scale_name)).copy()

    ultrasonic_factor = args.world_units_per_meter * args.ultrasonic_max_meters
    if ultrasonic_factor <= 0 or args.max_linear_command <= 0:
        raise ValueError("normalization factors must be positive")

    for stack in range(STACK_COUNT):
        base = stack * OBSERVATIONS_PER_STACK
        ultrasonic = base + ULTRASONIC_INDEX
        previous_gas = base + PREVIOUS_GAS_INDEX

        # new_ultrasonic = old_unity_distance / (units_per_meter * max_meters)
        mean[ultrasonic] /= ultrasonic_factor
        scale[ultrasonic] /= ultrasonic_factor

        # new_previous_gas = old_previous_gas / max_linear_command
        mean[previous_gas] /= args.max_linear_command
        scale[previous_gas] /= args.max_linear_command

    replace_initializer(model, mean_name, mean)
    replace_initializer(model, scale_name, scale)

    entry = next((item for item in model.metadata_props if item.key == CONTRACT_KEY), None)
    if entry is None:
        entry = model.metadata_props.add()
        entry.key = CONTRACT_KEY
    entry.value = CONTRACT_VALUE

    onnx.checker.check_model(model)
    onnx.save(model, args.model)
    print(f"Updated {args.model}: {CONTRACT_VALUE}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
