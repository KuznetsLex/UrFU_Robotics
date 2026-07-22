SHELL := powershell.exe
.SHELLFLAGS := -NoProfile -ExecutionPolicy Bypass -Command

YOLO_DIR := .\tools\yolo
YOLO_RUNNER := $(YOLO_DIR)\run_yolo.ps1
YOLO_SCRIPT := $(YOLO_DIR)\yolo_vision_node.py
PYTHON := $(YOLO_DIR)\.venv\Scripts\python.exe
CAMERA_URL ?=
FALLBACK_CAMERA_URL ?=
MODEL ?=
SOURCE_MODE ?= auto

.DEFAULT_GOAL := help

.PHONY: help setup yolo yolo-headless check inference-check robot-deploy robot-start robot-restart robot-stealth robot-status robot-stop clean

help:
	@Write-Host "UrFU Robotics commands:"
	@Write-Host "  make setup          Prepare the Python environment"
	@Write-Host "  make yolo           Start camera + YOLO + Unity UDP output"
	@Write-Host "  make yolo-headless  Start YOLO without the OpenCV window"
	@Write-Host "  make check          Check Python and load the default model"
	@Write-Host "  make inference-check  Validate policy, Unity and robot interfaces"
	@Write-Host "  make robot-deploy     Deploy team1.1 files without restarting"
	@Write-Host "  make robot-start      Start normal mode and show status"
	@Write-Host "  make robot-restart    Deploy, restart normal mode and show status"
	@Write-Host "  make robot-stealth    Deploy and start safe no-actuator diagnostics"
	@Write-Host "  make robot-status     Show robot/ROS/camera status"
	@Write-Host "  make robot-stop       Stop team1.1 safely"
	@Write-Host "  make clean          Remove generated Python cache"
	@Write-Host ""
	@Write-Host "Optional overrides:"
	@Write-Host '  make yolo CAMERA_URL="http://robot/frame.jpg"'
	@Write-Host '  make yolo FALLBACK_CAMERA_URL="http://robot:8081/"'
	@Write-Host '  make yolo MODEL="data/model.onnx" SOURCE_MODE=stream'

setup:
	@& "$(YOLO_RUNNER)" -SetupOnly

yolo: setup
	@& "$(YOLO_RUNNER)" -SourceMode "$(SOURCE_MODE)" $(if $(CAMERA_URL),-StreamUrl "$(CAMERA_URL)",) $(if $(FALLBACK_CAMERA_URL),-FallbackStreamUrl "$(FALLBACK_CAMERA_URL)",) $(if $(MODEL),-Model "$(MODEL)",)

yolo-headless: setup
	@& "$(YOLO_RUNNER)" -NoDisplay -SourceMode "$(SOURCE_MODE)" $(if $(CAMERA_URL),-StreamUrl "$(CAMERA_URL)",) $(if $(FALLBACK_CAMERA_URL),-FallbackStreamUrl "$(FALLBACK_CAMERA_URL)",) $(if $(MODEL),-Model "$(MODEL)",)

check: setup
	@& "$(PYTHON)" -m py_compile "$(YOLO_SCRIPT)"
	@Push-Location "$(YOLO_DIR)"; try { & ".\.venv\Scripts\python.exe" -c "from pathlib import Path; from yolo_vision_node import DEFAULT_MODEL, OnnxYoloDetector; detector=OnnxYoloDetector(Path(DEFAULT_MODEL).resolve()); print('Model OK:', DEFAULT_MODEL); print('Classes:', detector.names)" } finally { Pop-Location }

inference-check:
	@& ".\.venv-mlagents\Scripts\python.exe" ".\tools\validate_inference_contract.py"

robot-deploy:
	@& ".\tools\robot\manage_robot.ps1" -Deploy

robot-start:
	@& ".\tools\robot\manage_robot.ps1" -StartNormal -Status

robot-restart:
	@& ".\tools\robot\manage_robot.ps1" -Deploy -StartNormal -Status

robot-stealth:
	@& ".\tools\robot\manage_robot.ps1" -Deploy -StartStealth -Status

robot-status:
	@& ".\tools\robot\manage_robot.ps1" -Status

robot-stop:
	@& ".\tools\robot\manage_robot.ps1" -Stop

clean:
	@if (Test-Path -LiteralPath "$(YOLO_DIR)\__pycache__") { Remove-Item -LiteralPath "$(YOLO_DIR)\__pycache__" -Recurse -Force }
	@Write-Host "Python cache cleaned. The reusable tools/yolo/.venv environment was kept."
