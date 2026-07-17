SHELL := powershell.exe
.SHELLFLAGS := -NoProfile -ExecutionPolicy Bypass -Command

YOLO_DIR := .\tools\yolo
YOLO_RUNNER := $(YOLO_DIR)\run_yolo.ps1
YOLO_SCRIPT := $(YOLO_DIR)\yolo_vision_node.py
PYTHON := $(YOLO_DIR)\.venv\Scripts\python.exe
CAMERA_URL ?=
MODEL ?=
SOURCE_MODE ?= auto

.DEFAULT_GOAL := help

.PHONY: help setup yolo yolo-headless check clean

help:
	@Write-Host "UrFU Robotics commands:"
	@Write-Host "  make setup          Prepare the Python environment"
	@Write-Host "  make yolo           Start camera + YOLO + Unity UDP output"
	@Write-Host "  make yolo-headless  Start YOLO without the OpenCV window"
	@Write-Host "  make check          Check Python and load the default model"
	@Write-Host "  make clean          Remove generated Python cache"
	@Write-Host ""
	@Write-Host "Optional overrides:"
	@Write-Host '  make yolo CAMERA_URL="http://robot/frame.jpg"'
	@Write-Host '  make yolo MODEL="data/model.onnx" SOURCE_MODE=stream'

setup:
	@& "$(YOLO_RUNNER)" -SetupOnly

yolo: setup
	@& "$(YOLO_RUNNER)" -SourceMode "$(SOURCE_MODE)" $(if $(CAMERA_URL),-StreamUrl "$(CAMERA_URL)",) $(if $(MODEL),-Model "$(MODEL)",)

yolo-headless: setup
	@& "$(YOLO_RUNNER)" -NoDisplay -SourceMode "$(SOURCE_MODE)" $(if $(CAMERA_URL),-StreamUrl "$(CAMERA_URL)",) $(if $(MODEL),-Model "$(MODEL)",)

check: setup
	@& "$(PYTHON)" -m py_compile "$(YOLO_SCRIPT)"
	@Push-Location "$(YOLO_DIR)"; try { & ".\.venv\Scripts\python.exe" -c "from pathlib import Path; from yolo_vision_node import DEFAULT_MODEL, OnnxYoloDetector; detector=OnnxYoloDetector(Path(DEFAULT_MODEL).resolve()); print('Model OK:', DEFAULT_MODEL); print('Classes:', detector.names)" } finally { Pop-Location }

clean:
	@if (Test-Path -LiteralPath "$(YOLO_DIR)\__pycache__") { Remove-Item -LiteralPath "$(YOLO_DIR)\__pycache__" -Recurse -Force }
	@Write-Host "Python cache cleaned. The reusable tools/yolo/.venv environment was kept."
