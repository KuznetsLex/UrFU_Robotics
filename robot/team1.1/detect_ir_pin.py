#!/usr/bin/env python3
"""Find an active-low digital sensor by watching BCM GPIO transitions.

Run only while the robot container is stopped. This utility temporarily
configures the selected pins as pulled-up inputs, including motor pins when
--all-pins is used.
"""

import argparse
import time

import RPi.GPIO as GPIO


SAFE_PINS = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15, 18, 22, 23, 24, 25, 27]
ALL_PINS = list(range(1, 28))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--all-pins", action="store_true")
    parser.add_argument("--seconds", type=float, default=30.0)
    args = parser.parse_args()

    pins = ALL_PINS if args.all_pins else SAFE_PINS
    GPIO.setwarnings(False)
    GPIO.setmode(GPIO.BCM)

    states = {}
    for pin in pins:
        try:
            GPIO.setup(pin, GPIO.IN, pull_up_down=GPIO.PUD_UP)
            states[pin] = GPIO.input(pin)
        except Exception as error:
            print(f"skip GPIO{pin}: {error}", flush=True)

    print(f"watching BCM GPIO: {sorted(states)}", flush=True)
    print(
        "initial LOW GPIO: " +
        str([pin for pin, value in sorted(states.items()) if value == GPIO.LOW]),
        flush=True,
    )
    deadline = time.monotonic() + args.seconds
    try:
        while time.monotonic() < deadline:
            for pin, previous in list(states.items()):
                value = GPIO.input(pin)
                if value != previous:
                    print(f"GPIO{pin}: {previous} -> {value}", flush=True)
                    states[pin] = value
            time.sleep(0.02)
    finally:
        GPIO.cleanup(list(states))


if __name__ == "__main__":
    main()
