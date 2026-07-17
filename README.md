# UrFU Robotics — запуск YOLO через Make

Проект получает кадры с камеры робота по HTTP, запускает YOLO на компьютере и
передаёт результаты детекции в Unity по UDP-порту `5005`.

## Требования

- Windows 10/11;
- Python 3.10 или новее: команда `python` либо `py` должна быть доступна в
  `PATH`;
- Unity Hub с версией Unity, указанной в `ProjectSettings/ProjectVersion.txt`;
- GNU Make, доступный как `make` или `mingw32-make`.

Проверьте доступные команды:

```powershell
python --version
py -3 --version
make --version
mingw32-make --version
```

Достаточно, чтобы работала одна команда Python и одна команда Make. Если Make
доступен только как `mingw32-make`, для текущего окна PowerShell можно создать
алиас:

```powershell
Set-Alias make mingw32-make.exe
```

Для постоянного алиаса выполните один раз:

```powershell
$aliasLine = 'Set-Alias make mingw32-make.exe'
if (-not (Test-Path -LiteralPath $PROFILE)) {
    New-Item -ItemType File -Path $PROFILE -Force | Out-Null
}
if (-not (Select-String -LiteralPath $PROFILE -SimpleMatch $aliasLine -Quiet)) {
    Add-Content -LiteralPath $PROFILE $aliasLine
}
. $PROFILE
```

Этот вариант требует, чтобы PowerShell разрешал загрузку профиля. Если профиль
заблокирован политикой выполнения, используйте временный `Set-Alias`, команду
`mingw32-make` напрямую или установите вариант GNU Make с именем `make.exe`.

После этого проверьте:

```powershell
make help
```

Если ни `make`, ни `mingw32-make` не найдены, установите GNU Make любым
подходящим менеджером пакетов и перезапустите PowerShell. Создавать алиас не
нужно, когда команда уже называется `make`.

## Быстрый запуск

Откройте PowerShell и перейдите в каталог клона, содержащий `Makefile` и
`README.md`:

```powershell
Set-Location "<путь-к-клону-репозитория>"
```

Замените значение в угловых скобках на реальный путь на своём компьютере. Все
последующие команды выполняются из корня репозитория.

Подготовьте Python-окружение:

```powershell
make setup
```

Получите файл весов `best_int8.onnx` отдельно и положите его по относительному
пути:

```text
data/onnx_vino/onnx26/best_int8.onnx
```

После этого проверьте окружение и модель:

```powershell
make check
```

Затем включите Play Mode в Unity и запустите YOLO:

```powershell
make yolo
```

При первом запуске `make setup` автоматически:

1. создаст `tools/yolo/.venv`;
2. установит `numpy`, `opencv-python` и `onnxruntime`;
3. сохранит окружение для последующих запусков.

Путь к окружению и модели вычисляется относительно расположения репозитория,
поэтому проект можно клонировать в любой каталог.

## Переносимость между компьютерами

В репозитории не хранятся абсолютные пути к проекту. Локальными для каждого
компьютера считаются:

- `tools/yolo/.venv` — создаётся командой `make setup`;
- файл весов в `data/onnx_vino/onnx26/` — копируется отдельно;
- URL камеры — задаётся через `CAMERA_URL` или блок `EDITABLE SETTINGS`;
- алиас `make`, если установленный GNU Make называется `mingw32-make`.

Эти данные не требуется переносить в Git. После клонирования достаточно
настроить Make, выполнить `make setup`, положить веса и запустить `make check`.

Остановить YOLO можно клавишей `Q` или `Esc` в окне камеры либо сочетанием
`Ctrl+C` в терминале.

## Команды Make

| Команда | Назначение |
|---|---|
| `make help` | Показать доступные команды |
| `make setup` | Создать окружение и установить зависимости |
| `make check` | Проверить Python и загрузить стандартную ONNX-модель |
| `make yolo` | Запустить камеру, YOLO и передачу детекций в Unity |
| `make yolo-headless` | Запустить YOLO без отдельного окна OpenCV |
| `make clean` | Удалить Python-кэш, сохранив `tools/yolo/.venv` |

## Настройка камеры

По умолчанию используется одиночный JPEG-кадр:

```text
http://192.168.2.158:10002/frame.jpg
```

Если основной адрес не отвечает, Python автоматически переключается на:

```text
http://192.168.2.158:8081/
```

Корень порта 8081 отдаёт непрерывный MJPEG-поток. В HUD активный источник
отмечается как `primary` или `MJPEG :8081`.

Запуск с другим адресом:

```powershell
make yolo CAMERA_URL="http://192.168.137.248:8081/"
```

Отдельный fallback можно переопределить так:

```powershell
make yolo FALLBACK_CAMERA_URL="http://robot:8081/"
```

Режим источника определяется автоматически:

- URL с окончанием `.jpg` или `.jpeg` считается последовательностью снимков;
- остальные URL считаются MJPEG или видеопотоком.

Из bounding box в политику передаются три признака:

- горизонтальный угол: `-1` слева, `0` по центру, `+1` справа;
- доля площади кадра: `(bbox_width * bbox_height) / (frame_width * frame_height)`;
- отношение сторон bounding box: `bbox_width / bbox_height`.

При необходимости режим можно указать явно:

```powershell
make yolo CAMERA_URL="http://robot/frame.jpg" SOURCE_MODE=snapshot
make yolo CAMERA_URL="http://robot:8081/" SOURCE_MODE=stream
```

## Настройка модели

Стандартная модель:

```text
data/onnx_vino/onnx26/best_int8.onnx
```

Веса не хранятся в Git. После клонирования положите файл
`best_int8.onnx` в подготовленную папку `data/onnx_vino/onnx26/`.
Пустая структура каталога сохраняется в репозитории через `.gitkeep`.

Она принимает изображение `512×512` и распознаёт классы:

```text
0 — ball
1 — cube
2 — robot-claw
```

По умолчанию детектор передаёт классы `ball` и `cube`.

Запуск другой модели:

```powershell
make yolo MODEL="data/another_model.onnx"
```

Основные значения по умолчанию находятся в блоке `EDITABLE SETTINGS` в начале
файла `tools/yolo/yolo_vision_node.py`. Аргументы Make и командной строки имеют
приоритет над этими значениями.

## Что отображается в Unity

После запуска Play Mode Unity автоматически создаёт:

- окно `Robot camera` в левом верхнем углу;
- UDP-приёмник `RealVision` на порту `5005`;
- зелёную рамку вокруг обнаруженного мяча;
- confidence и статус соединения с YOLO.
- долю площади кадра, занятую bounding box, и отношение его ширины к высоте;
- горизонтальное смещение цели (`-1` — слева, `0` — по центру, `+1` — справа).

Отдельное окно OpenCV можно отключить:

```powershell
make yolo-headless
```

Отображение рамки в Unity при этом продолжит работать.

## Проверка перед запуском

```powershell
make check
```

Ожидаемый результат:

```text
YOLO environment is ready: ...\tools\yolo\.venv\Scripts\python.exe
Model OK: ...\data\onnx_vino\onnx26\best_int8.onnx
Classes: {0: 'ball', 1: 'cube', 2: 'robot-claw'}
```

## Возможные проблемы

### Команда `make` не найдена

Проверьте оба возможных имени:

```powershell
make --version
mingw32-make --version
```

Если работает только `mingw32-make`, создайте временный алиас:

```powershell
Set-Alias make mingw32-make.exe
```

Либо используйте полное имя команды:

```powershell
mingw32-make yolo
```

### Скрипт пишет `Waiting for camera`

- проверьте питание и Wi-Fi робота;
- проверьте IP-адрес камеры;
- откройте URL камеры в браузере;
- для MJPEG принудительно задайте `SOURCE_MODE=stream`.

### Камера видна, но рамки в Unity нет

- убедитесь, что Unity находится в Play Mode;
- проверьте статус YOLO под окном камеры;
- убедитесь, что UDP-порт `5005` не занят другим процессом;
- проверьте, что Python и Unity используют изображение одной камеры.

### Модель не найдена

Проверьте наличие файла:

```powershell
Test-Path .\data\onnx_vino\onnx26\best_int8.onnx
```

## Полный запуск робота

Цель `make robot` пока не реализована. Для неё необходимо подключить policy
ONNX к `Behavior Parameters`, передать реальные сенсоры в политику и направить
её действия в ROS `/cmd_vel`.

До завершения этой интеграции рабочая команда запуска зрения:

```powershell
make yolo
```
