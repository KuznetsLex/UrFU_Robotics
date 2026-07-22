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
    # PNG-картинки (по умолчанию):
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain

    # Новая карточка "RewardsByEpisode" в TensorBoard — пишется прямо в run_dir,
    # в тот же run, что и обычные Rewards/* (по шагам):
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain --mode tensorboard

    # То же самое, но в фоне, пока идёт обучение: пересчитывает и перезаписывает
    # run каждые --interval секунд, пока не остановишь (Ctrl+C):
    python tools/plot_rewards_by_episode.py results/small_input_rand_ball_fix/GFSX_Brain --mode tensorboard --watch --interval 30
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
# файлов обучения при очистке перед перезаписью (см. write_tensorboard).
BY_EPISODE_SUFFIX = ".by_episode"


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


def write_tensorboard(out_dir, scalars, ep_steps, cumulative_episodes, reward_tags):
    from torch.utils.tensorboard import SummaryWriter

    os.makedirs(out_dir, exist_ok=True)

    # out_dir теперь по умолчанию — это тот же run_dir, где лежат настоящие
    # чекпоинты и tfevents самого обучения, поэтому rmtree() всей папки, как
    # раньше, недопустим — удалит чужие файлы. Чистим только файлы, которые
    # сами же создали на прошлых итерациях (отличаем по уникальному суффиксу),
    # иначе накопятся дублирующиеся точки из старых event-файлов.
    for old_file in glob.glob(os.path.join(out_dir, f"events.out.tfevents.*{BY_EPISODE_SUFFIX}")):
        os.remove(old_file)

    writer = SummaryWriter(log_dir=out_dir, filename_suffix=BY_EPISODE_SUFFIX)
    for tag in reward_tags:
        events = scalars[tag]
        steps = np.array([e.step for e in events], dtype=float)
        values = [e.value for e in events]
        episodes = np.interp(steps, ep_steps, cumulative_episodes)

        new_tag = TB_NEW_PREFIX + tag[len(REWARDS_PREFIX):]
        for ep, val in zip(episodes, values):
            writer.add_scalar(new_tag, val, global_step=int(round(ep)))
    writer.close()
    print(f"Записано в {out_dir} (карточка '{TB_NEW_PREFIX.rstrip('/')}' в TensorBoard)")


def run_once(run_dir, mode, out_dir):
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
        # По умолчанию пишем прямо в run_dir — та же папка и тот же run в
        # TensorBoard, что и обычные Rewards/* по шагам, просто другая карточка
        # (тег начинается с RewardsByEpisode/, а не Rewards/).
        tb_out = out_dir if mode == "tensorboard" and out_dir else run_dir
        write_tensorboard(tb_out, scalars, ep_steps, cumulative_episodes, reward_tags)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_dir", help="Папка с events.out.tfevents.* (например results/.../GFSX_Brain)")
    parser.add_argument(
        "--mode", choices=["png", "tensorboard", "both"], default="png",
        help="png — сохранить картинки; tensorboard — записать новый run для TensorBoard; both — оба варианта",
    )
    parser.add_argument(
        "-o", "--out-dir", default=None,
        help="Для --mode png: куда сохранять PNG (по умолчанию <run_dir>/episode_plots). "
             "Для --mode tensorboard: куда писать (по умолчанию run_dir — тот же run, "
             "что и Rewards/* по шагам)",
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

    run_dir = args.run_dir.rstrip("/")

    if not args.watch:
        run_once(run_dir, args.mode, args.out_dir)
        return

    print(f"watch-режим: пересчёт каждые {args.interval:.0f} сек, Ctrl+C для остановки")
    while True:
        try:
            run_once(run_dir, args.mode, args.out_dir)
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


if __name__ == "__main__":
    main()
