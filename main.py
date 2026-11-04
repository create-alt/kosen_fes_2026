"""動画ファイル用: 録画した動画でパンチ検出・ヒット判定・頭と体の動きを確認する。

    python main.py bag.mp4                       # GPU があれば自動で使う(--device cpu で CPU)
    python main.py bag.mp4 --save-video          # 検出結果を重ねた動画も保存する(bag_result.mp4)
    python main.py bag.mp4 --no-display --quiet  # 画面もコンソール出力もなし(速度の測定など)
    python main.py bag.mp4 --udp 127.0.0.1:5005  # 毎フレーム Unity へ UDP で送る

毎フレーム「頭の座標・体の座標・ヒット有無・パンチ有無」を出力する(--format json で JSON)。
動画のフレーム番号から時刻を求めるので、処理が遅くても判定結果は同じになる。
リアルタイム(カメラ)は realtime.py。
"""

import argparse
import time
from pathlib import Path

import cv2

from args import add_pipeline_args, check_args
from output import Reporter, draw_overlay
from pipeline import Pipeline

WINDOW = "Punching Bag Detector (press Q to quit)"


def parse_args():
    parser = argparse.ArgumentParser(description="Run the punch detection on a recorded video.")
    parser.add_argument("video", help="Path to the input video, e.g. bag.mp4")
    add_pipeline_args(parser)
    parser.add_argument("--no-display", action="store_true", help="Do not show the video window")
    parser.add_argument("--save-video", nargs="?", const="", default=None, metavar="PATH",
                        help="Also save the video with the overlay. No value = <input name>_result.mp4")
    args = parser.parse_args()
    return check_args(parser, args)


def open_writer(args, fps, frame):
    path = Path(args.save_video) if args.save_video else Path(args.video).with_name(Path(args.video).stem + "_result.mp4")
    path.parent.mkdir(parents=True, exist_ok=True)
    h, w = frame.shape[:2]
    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps if fps > 0 else 30.0, (w, h))
    if not writer.isOpened():
        raise SystemExit(f"Could not create the output video: {path}\n"
                         "Use an .mp4 path in a writable folder (ASCII-only paths are safest).")
    return writer, path


def main():
    args = parse_args()
    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        raise SystemExit(f"Could not open video: {args.video}")
    fps = cap.get(cv2.CAP_PROP_FPS)

    pipeline = Pipeline(args, motion_auto_frames=args.motion_calib_frames)
    reporter = Reporter(args.fmt, args.quiet, args.print_every, args.udp, args.udp_full, echo_sent=True)
    writer = path = last = None
    busy, frames, index, finished = 0.0, 0, 0, False
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                finished = True
                break
            now = index / fps if fps > 0 else time.monotonic()  # 動画の時刻(処理速度に依存しない)
            start = time.perf_counter()
            result = pipeline.process(frame, index, now)
            busy += time.perf_counter() - start
            frames += 1
            index += 1
            reporter.emit(result)

            if args.no_display and not args.save_video:
                continue
            draw_overlay(frame, pipeline.bag, pipeline.person, pipeline.judge, pipeline.motion, result)
            if args.save_video is not None:
                if writer is None:
                    writer, path = open_writer(args, fps, frame)
                writer.write(frame)
            if not args.no_display:
                last = frame.copy()
                cv2.imshow(WINDOW, frame)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    break

        if writer is not None:
            writer.release()
            print(f"Saved the result video: {path}")
        if finished and last is not None:  # 最後まで処理したら、最終フレームを表示したまま q を待つ
            cv2.imshow(WINDOW, last)
            print("Finished. Press q in the video window to close.")
            while cv2.waitKey(30) & 0xFF != ord("q"):
                pass
    finally:
        cap.release()
        reporter.close()
        pipeline.close()
        cv2.destroyAllWindows()

    if frames:
        ms = busy / frames * 1000
        print(f"Processing: {ms:.1f} ms/frame (~{1000 / ms:.0f} fps max, excluding display) on {pipeline.device.label}")
    print(f"Punches: {pipeline.judge.punch_count}   Hits (contacts): {pipeline.judge.contact_count}")


if __name__ == "__main__":
    main()
