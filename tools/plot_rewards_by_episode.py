"""
Перестраивает графики карточки "Rewards" из TensorBoard-логов ML-Agents
так, чтобы ось X была не числом шагов среды, а приблизительным числом
пройденных эпизодов. Rewards/EpisodeLength не трогается (остаётся по шагам,
для неё эпизоды не считаются).

Число эпизодов не логируется ML-Agents напрямую, поэтому оно оценивается:
на каждом интервале между двумя точками сводки берётся длина этого интервала
в шагах и делится на среднюю длину эпизода в этом интервале
(тег "Environment/Episode Length"), результат суммируется по всем интервалам.
Это оценка, а не точное число (параллельные агенты, усреднение), но форма
графика и его динамика будут корректными.

Использование:
    conda activate mlagents
    # PNG-картинки для одного эксперимента (по умолчанию):
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain

    # Отдельный "run" в самом TensorBoard (карточка "RewardsByEpisode"),
    # пишется в results/small_input_rand_ball_fix/by_episode — СОСЕДНЮЮ папку,
    # не в ту же, что и обычные Rewards/* (по шагам). Это обязательно: у
    # TensorBoard DirectoryWatcher в одной директории может быть только один
    # активно растущий event-файл одновременно — если в папке с настоящими
    # данными обучения появляется наш ВТОРОЙ растущий файл, DirectoryWatcher
    # считает файл обучения "завершённым" и перестаёт видеть его обновления
    # (см. EpisodeTensorBoardWriter и github.com/tensorflow/tensorboard/issues/349).
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain --mode tensorboard

    # То же самое, но в фоне, пока идёт обучение: раз в --interval секунд
    # дописывает новые точки в тот же файл (не пересоздаёт его), пока не
    # остановишь (Ctrl+C):
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain --mode tensorboard --watch --interval 30

    # Мониторить ВСЕ эксперименты разом: вместо конкретного <run-id>/<Brain>
    # указываете корень results/ и добавляете --all — скрипт сам находит все
    # папки с events.out.tfevents.* внутри и ведёт по отдельному writer'у на
    # каждую (см. discover_brain_dirs). Новые прогоны, появившиеся после
    # старта, тоже подхватятся на следующей итерации --watch.
    python tools/plot_rewards_by_episode.py results --all --mode tensorboard --watch --interval 30
"""

import argparse
import glob
import os
import time

import numpy as np
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

EPISODE_LENGTH_TAG = "Environment/Episode Length"
REWARDS_PREFIX = "Rewards/"
SKIP_TAGS = {"Rewards/EpisodeLength"}
TB_NEW_PREFIX = "RewardsByEpisode/"
# Суффикс наших собственных event-файлов, когда пишем в тот же run, что и
# обычные Rewards/* по шагам — по нему отличаем "свои" файлы от настоящих
# файлов обучения при очистке файлов от прошлых ЗАПУСКОВ этого скрипта.
BY_EPISODE_SUFFIX = ".by_episode"
# Папки, которые сам этот скрипт создаёт (производные данные) — при поиске
# экспериментов в них не спускаемся, чтобы не принять их за ещё один run.
OWN_OUTPUT_DIR_NAMES = {"by_episode", "episode_plots"}


def discover_brain_dirs(results_root):
    """Ищет все папки внутри results_root, где ML-Agents реально пишет
    events.out.tfevents.* (то есть папки конкретных Brain'ов внутри
    results/<run-id>/<Brain>), пропуская наши собственные производные
    поддиректории (by_episode, episode_plots)."""
    found = []
    for dirpath, dirnames, filenames in os.walk(results_root):
        dirnames[:] = [d for d in dirnames if d not in OWN_OUTPUT_DIR_NAMES]
        has_real_events = any(
            f.startswith("events.out.tfevents.") and BY_EPISODE_SUFFIX not in f
            for f in filenames
        )
        if has_real_events:
            found.append(dirpath.rstrip("/"))
    return sorted(found)


def load_scalars(run_dir):
    ea = EventAccumulator(run_dir, size_guidance={"scalars": 0})
    ea.Reload()
    tags = ea.Tags()["scalars"]
    return {tag: ea.Scalars(tag) for tag in tags}


def steps_to_episode_axis(episode_length_events):
    steps = np.array([e.step for e in episode_length_events], dtype=float)
    lengths = np.array([e.value for e in episode_length_events], dtype=float)

    prev_steps = np.concatenate([[0.0], steps[:-1]])
    interval = steps - prev_steps
    episodes_in_interval = interval / lengths
    cumulative_episodes = np.cumsum(episodes_in_interval)

    return steps, cumulative_episodes


def get_reward_tags(scalars):
    return sorted(
        tag for tag in scalars
        if tag.startswith(REWARDS_PREFIX) and tag not in SKIP_TAGS
    )


def write_png(run_dir, out_dir, scalars, ep_steps, cumulative_episodes, reward_tags):
    import matplotlib.pyplot as plt

    os.makedirs(out_dir, exist_ok=True)

    for tag in reward_tags:
        events = scalars[tag]
        steps = np.array([e.step for e in events], dtype=float)
        values = np.array([e.value for e in events], dtype=float)

        # На случай, если у тега точки записаны на чуть других шагах,
        # чем у Environment/Episode Length — интерполируем ось эпизодов.
        episodes = np.interp(steps, ep_steps, cumulative_episodes)

        plt.figure(figsize=(8, 4.5))
        plt.plot(episodes, values)
        plt.xlabel("Episode (оценка)")
        plt.ylabel(tag)
        plt.title(tag)
        plt.grid(True, alpha=0.3)
        plt.tight_layout()

        safe_name = tag.replace("/", "_") + ".png"
        out_path = os.path.join(out_dir, safe_name)
        plt.savefig(out_path, dpi=150)
        plt.close()
        print(f"saved {out_path}")


class EpisodeTensorBoardWriter:
    """Держит один открытый SummaryWriter на весь --watch-сеанс и только
    дописывает новые точки, а не пересоздаёт файл каждую итерацию — иначе
    TensorBoard видит, как файл в директории то исчезает, то появляется новый,
    и его фоновый reload может молча сломаться.

    ВАЖНО: writer всё равно должен указывать на ОТДЕЛЬНУЮ от настоящих данных
    обучения папку (см. resolve_tb_out_dir()). У TensorBoard DirectoryWatcher в
    одной директории может быть только один активно растущий event-файл — если
    туда же писать ещё один растущий файл (даже без удаления/пересоздания), при
    появлении нашего файла DirectoryWatcher посчитает файл обучения
    "завершённым" и перестанет видеть его дальнейшие обновления
    (см. github.com/tensorflow/tensorboard/issues/349 и связанные комментарии
    про directory_watcher.py). Дозапись в один файл решает только проблему
    исчезновения/пересоздания файла, но не проблему двух одновременно растущих
    файлов в одной папке — поэтому разные папки обязательны.
    """

    def __init__(self, out_dir):
        from torch.utils.tensorboard import SummaryWriter

        os.makedirs(out_dir, exist_ok=True)
        # Чистим только файлы от ПРОШЛЫХ ЗАПУСКОВ этого скрипта (по суффиксу) —
        # внутри одного сеанса файл после этого больше не трогаем.
        for old_file in glob.glob(os.path.join(out_dir, f"events.out.tfevents.*{BY_EPISODE_SUFFIX}")):
            os.remove(old_file)

        self.writer = SummaryWriter(log_dir=out_dir, filename_suffix=BY_EPISODE_SUFFIX)
        self.written_counts = {}  # tag -> сколько точек этого тега уже отправлено

    def update(self, scalars, ep_steps, cumulative_episodes, reward_tags):
        for tag in reward_tags:
            events = scalars[tag]
            already_written = self.written_counts.get(tag, 0)
            if already_written >= len(events):
                continue

            steps = np.array([e.step for e in events], dtype=float)
            episodes = np.interp(steps, ep_steps, cumulative_episodes)
            new_tag = TB_NEW_PREFIX + tag[len(REWARDS_PREFIX):]

            for i in range(already_written, len(events)):
                self.writer.add_scalar(new_tag, events[i].value, global_step=int(round(episodes[i])))
            self.written_counts[tag] = len(events)

        self.writer.flush()

    def close(self):
        self.writer.close()


def resolve_tb_out_dir(run_dir, out_dir_override):
    # ОБЯЗАТЕЛЬНО отдельная папка от run_dir — см. docstring EpisodeTensorBoardWriter.
    return out_dir_override if out_dir_override else os.path.join(os.path.dirname(run_dir), "by_episode")


def process_run(run_dir, mode, out_dir, tb_writers):
    """Обрабатывает один run_dir. tb_writers — общий на все runs словарь
    run_dir -> EpisodeTensorBoardWriter, чтобы каждый эксперимент держал свой
    собственный writer (а не создавался заново каждую итерацию)."""
    scalars = load_scalars(run_dir)

    if EPISODE_LENGTH_TAG not in scalars:
        raise RuntimeError(f"Тег '{EPISODE_LENGTH_TAG}' не найден в {run_dir}")

    ep_steps, cumulative_episodes = steps_to_episode_axis(scalars[EPISODE_LENGTH_TAG])

    reward_tags = get_reward_tags(scalars)
    if not reward_tags:
        raise RuntimeError(f"Не нашёл тегов '{REWARDS_PREFIX}*' в {run_dir}")

    if mode in ("png", "both"):
        png_out = out_dir if mode == "png" and out_dir else os.path.join(run_dir, "episode_plots")
        write_png(run_dir, png_out, scalars, ep_steps, cumulative_episodes, reward_tags)

    if mode in ("tensorboard", "both"):
        if run_dir not in tb_writers:
            tb_writers[run_dir] = EpisodeTensorBoardWriter(resolve_tb_out_dir(run_dir, out_dir))
        tb_writers[run_dir].update(scalars, ep_steps, cumulative_episodes, reward_tags)


def run_all_once(results_root, mode, out_dir, tb_writers, seen_runs):
    run_dirs = discover_brain_dirs(results_root)
    for run_dir in run_dirs:
        if run_dir not in seen_runs:
            seen_runs.add(run_dir)
            print(f"найден эксперимент: {run_dir}")
        try:
            process_run(run_dir, mode, out_dir, tb_writers)
        except Exception as exc:
            # Один сломанный/ещё не готовый run не должен останавливать
            # обработку остальных.
            print(f"[{run_dir}] пропускаю итерацию: {exc}")
    return run_dirs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "run_dir",
        help="Папка с events.out.tfevents.* (например results/.../GFSX_Brain). "
             "С флагом --all — корень для поиска всех экспериментов (например results)",
    )
    parser.add_argument(
        "--all", action="store_true",
        help="Трактовать run_dir как корень и обрабатывать ВСЕ найденные внутри "
             "эксперименты (папки с events.out.tfevents.*) за один запуск",
    )
    parser.add_argument(
        "--mode", choices=["png", "tensorboard", "both"], default="png",
        help="png — сохранить картинки; tensorboard — записать новый run для TensorBoard; both — оба варианта",
    )
    parser.add_argument(
        "-o", "--out-dir", default=None,
        help="Для --mode png: куда сохранять PNG (по умолчанию <run_dir>/episode_plots). "
             "Для --mode tensorboard: папка нового run'а (по умолчанию results/<run-id>/by_episode — "
             "ОБЯЗАТЕЛЬНО отдельная от run_dir, см. EpisodeTensorBoardWriter). "
             "Несовместимо с --all (у каждого эксперимента своя папка)",
    )
    parser.add_argument(
        "--watch", action="store_true",
        help="Не выходить после первого прохода, а повторять пересчёт каждые --interval секунд "
             "(пока обучение идёт), пока не остановишь Ctrl+C",
    )
    parser.add_argument(
        "--interval", type=float, default=30.0,
        help="Пауза между пересчётами в секундах при --watch (по умолчанию 30)",
    )
    args = parser.parse_args()

    if args.all and args.out_dir:
        raise SystemExit("--out-dir несовместим с --all: у каждого найденного эксперимента своя папка вывода")

    run_dir = args.run_dir.rstrip("/")
    tb_writers = {}

    try:
        if args.all:
            seen_runs = set()
            if not args.watch:
                run_dirs = run_all_once(run_dir, args.mode, args.out_dir, tb_writers, seen_runs)
                if not run_dirs:
                    print(f"Не нашёл ни одного эксперимента (events.out.tfevents.*) внутри {run_dir}")
                elif tb_writers:
                    for r, w in tb_writers.items():
                        print(f"[{r}] записано в {w.writer.log_dir}")
                return

            print(f"watch-режим (--all): пересчёт каждые {args.interval:.0f} сек, Ctrl+C для остановки")
            while True:
                try:
                    run_all_once(run_dir, args.mode, args.out_dir, tb_writers, seen_runs)
                except KeyboardInterrupt:
                    raise
                try:
                    time.sleep(args.interval)
                except KeyboardInterrupt:
                    print("остановлено")
                    break
            return

        # Режим одного эксперимента (как раньше)
        if args.mode in ("tensorboard", "both"):
            tb_writers[run_dir] = EpisodeTensorBoardWriter(resolve_tb_out_dir(run_dir, args.out_dir))

        if not args.watch:
            process_run(run_dir, args.mode, args.out_dir, tb_writers)
            if run_dir in tb_writers:
                print(f"Записано в {tb_writers[run_dir].writer.log_dir} (карточка '{TB_NEW_PREFIX.rstrip('/')}' в TensorBoard)")
            return

        print(f"watch-режим: пересчёт каждые {args.interval:.0f} сек, Ctrl+C для остановки")
        while True:
            try:
                process_run(run_dir, args.mode, args.out_dir, tb_writers)
                if run_dir in tb_writers:
                    print(f"обновлено ({sum(tb_writers[run_dir].written_counts.values())} точек всего по тегам)")
            except KeyboardInterrupt:
                raise
            except Exception as exc:
                # Событие могло попасть в event-файл наполовину записанным во время
                # флаша ML-Agents — не падаем, просто пробуем ещё раз через interval.
                print(f"пропускаю итерацию: {exc}")

            try:
                time.sleep(args.interval)
            except KeyboardInterrupt:
                print("остановлено")
                break
    finally:
        for writer in tb_writers.values():
            writer.close()


if __name__ == "__main__":
    main()
