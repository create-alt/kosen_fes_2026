"""骨格推定モデルの検証。**追加学習が必要かどうか**を判断するためのスクリプト。

標準の学習済みモデル(yolo11n-pose.pt)と、train.py で学習したモデル(pose_model.pt)の
どちらでも検証できる。複数を並べて比べることもできる。

1) 動画で検証する(正解のラベルは要らない)
    python validation.py bag.mp4
    python validation.py bag.mp4 --weights yolo11n-pose.pt pose_model.pt     # 学習の前後を比べる
    python validation.py bag.mp4 --save-video                                # 骨格を重ねた動画も保存する
    python validation.py bag.mp4 --show                                      # 画面に表示しながら

  頭・肩・腰・手首が、どれだけのフレームで検出できたか(検出率)と、フレーム間で位置が
  飛んだ回数(ちらつき・誤検出の目安)を調べ、追加学習が必要かどうかの目安を出す。

2) アノテーションしたデータで検証する(正解との誤差が分かる。より確実)
    python validation.py --data pose_dataset
    python validation.py --data pose_dataset --weights yolo11n-pose.pt pose_model.pt

  annotation.py で付けた正解と比べて、キーポイントごとの誤差と、頭の位置・体の重心・両手の
  位置(実際に使う値)の誤差を出す。誤差は、肩〜腰の長さ(体幹、約50cm)を1とした値でも出す。
  学習に使っていない画像(val)で調べるのが基本。--split all で全部を調べることもできる。
"""

import argparse
import statistics
import sys
from pathlib import Path

import cv2
import numpy as np

from common import (
    ANNOTATED,
    IMAGE_SUFFIXES,
    KEYPOINT_NAMES,
    list_samples,
    read_image,
    read_label,
    resolve_device,
)
from output import draw_person
from vision import BASE_POSE_WEIGHTS, DEFAULT_POSE_WEIGHTS, PersonPose, PoseEstimator

# 追加学習が必要かどうかの目安(動画での検出率 / データでの誤差)
RATE_HEAD = 0.95  # 頭を検出できたフレームの割合
RATE_BODY = 0.90  # 体の重心(肩と腰)を検出できたフレームの割合
RATE_HAND = 0.85  # どちらかの手を検出できたフレームの割合
MAX_JUMP_RATE = 0.03  # 位置が飛んだ(体幹の半分以上動いた)フレームの割合の上限
MAX_ERR_HEAD_BODY = 0.10  # 頭・体の重心の平均誤差(体幹を1として)の上限
MAX_ERR_HAND = 0.20  # 手の平均誤差(体幹を1として)の上限


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="Validate a pose model on a video or on an annotated dataset."
    )
    parser.add_argument("source", nargs="?", help="Video file or a folder of images (no labels needed)")
    parser.add_argument(
        "--data", type=Path, default=None,
        help="Annotated dataset folder made by annotation.py (compare with the ground truth)",
    )
    parser.add_argument("--split", choices=["val", "train", "all"], default="val", help="With --data (default: val)")
    parser.add_argument(
        "--weights", nargs="+", default=None,
        help=(
            "Pose model(s) to check; several are compared side by side. Default: the standard "
            f"{BASE_POSE_WEIGHTS}, plus pose_model.pt if it exists"
        ),
    )
    parser.add_argument("--imgsz", type=int, default=640, help="Inference image size (default: 640)")
    parser.add_argument("--conf", type=float, default=0.25, help="Person detection confidence (default: 0.25)")
    parser.add_argument("--kpt-conf", type=float, default=0.5, help="Keypoint confidence threshold (default: 0.5)")
    parser.add_argument("--device", default="auto", help='"auto" (GPU if available), "cpu", "cuda", "0", "mps" (default: auto)')
    parser.add_argument("--half", action=argparse.BooleanOptionalAction, default=None, help="FP16 inference (default: on for NVIDIA GPUs)")
    parser.add_argument("--every", type=int, default=1, help="Video: use every N-th frame (default: 1)")
    parser.add_argument("--max-frames", type=int, default=0, help="Video: stop after N frames; 0 = all")
    parser.add_argument(
        "--save-video", nargs="?", const="", default=None, metavar="PATH",
        help="Video: save the video with the skeleton drawn. No value = <input>_pose_<model>.mp4",
    )
    parser.add_argument("--show", action="store_true", help="Video: show the result in a window (q to stop)")
    args = parser.parse_args(argv)
    if (args.source is None) == (args.data is None):
        parser.error("Give either a video / image folder, or --data <dataset folder>.")
    return args


def default_weights():
    weights = [BASE_POSE_WEIGHTS]
    if DEFAULT_POSE_WEIGHTS.exists():
        weights.append(str(DEFAULT_POSE_WEIGHTS))
    return weights


# ------------------------------------------------------------------ 動画での検証


def iter_frames(source, every, max_frames):
    """(フレーム番号, 画像) を順に返す。動画または画像フォルダ。"""
    path = Path(source)
    if path.is_dir():
        files = sorted(f for f in path.iterdir() if f.suffix.lower() in IMAGE_SUFFIXES)
        for i, file in enumerate(files):
            if i % every:
                continue
            image = read_image(file)
            if image is not None:
                yield i, image
            if max_frames and i // every + 1 >= max_frames:
                return
        return
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise SystemExit(f"Could not open: {source}")
    try:
        i = used = 0
        while True:
            ok, image = cap.read()
            if not ok:
                break
            if i % every == 0:
                yield i, image
                used += 1
                if max_frames and used >= max_frames:
                    break
            i += 1
    finally:
        cap.release()


def video_output_path(args, weights):
    if args.save_video is None:
        return None
    stem = Path(weights).stem
    if args.save_video:
        base = Path(args.save_video)
        return base.with_name(f"{base.stem}_{stem}{base.suffix or '.mp4'}") if len(args.weights_list) > 1 else base
    source = Path(args.source)
    return source.with_name(f"{source.stem}_pose_{stem}.mp4")


def evaluate_video(args, weights):
    """動画での検出率・ちらつきを調べる。結果の dict を返す。"""
    estimator = PoseEstimator(weights, args.device_obj, args.imgsz, args.conf, args.kpt_conf)
    names = ["head", "body", "hand_any", "hand_both", "left_wrist", "right_wrist"]
    seen = {n: 0 for n in names}
    frames = person_frames = 0
    kpt_seen = {i: 0 for i in ANNOTATED}
    previous = {}
    jumps = {"head": 0, "body": 0, "wrist": 0}
    pairs = {"head": 0, "body": 0, "wrist": 0}
    steps = {"head": [], "body": [], "wrist": []}
    writer = None
    out_path = video_output_path(args, estimator.weights)

    try:
        for index, image in iter_frames(args.source, max(1, args.every), args.max_frames):
            frames += 1
            person = estimator.estimate(image)
            current = {}
            torso = None
            if person is not None:
                person_frames += 1
                for i in ANNOTATED:
                    kpt_seen[i] += int(person.keypoints[i, 2] >= args.kpt_conf)
                head, body = person.head, person.body_center
                lw, rw = person.point(9), person.point(10)
                seen["head"] += head is not None
                seen["body"] += body is not None
                seen["hand_any"] += (lw is not None) or (rw is not None)
                seen["hand_both"] += (lw is not None) and (rw is not None)
                seen["left_wrist"] += lw is not None
                seen["right_wrist"] += rw is not None
                torso = person.torso_length
                current = {"head": head, "body": body, "lw": lw, "rw": rw}

            # フレーム間で位置が大きく飛んだ回数(体幹の半分以上。体幹が分からないときは人の枠の高さの1/4)
            if person is not None:
                scale = torso if torso else (person.box[3] - person.box[1]) / 2.0
                for kind, keys in (("head", ["head"]), ("body", ["body"]), ("wrist", ["lw", "rw"])):
                    for key in keys:
                        a, b = previous.get(key), current.get(key)
                        if a is not None and b is not None and scale > 0:
                            distance = float(np.hypot(a[0] - b[0], a[1] - b[1]))
                            pairs[kind] += 1
                            steps[kind].append(distance / scale)
                            if distance > 0.5 * scale:
                                jumps[kind] += 1
            previous = current

            if args.show or out_path is not None:
                shown = image.copy()
                draw_person(shown, person)
                cv2.putText(shown, f"{Path(str(estimator.weights)).name}  frame {index}", (10, 28),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
                if out_path is not None:
                    if writer is None:
                        out_path.parent.mkdir(parents=True, exist_ok=True)
                        h, w = shown.shape[:2]
                        writer = cv2.VideoWriter(str(out_path), cv2.VideoWriter_fourcc(*"mp4v"), 30.0, (w, h))
                    writer.write(shown)
                if args.show:
                    cv2.imshow("validation.py (q to stop)", shown)
                    if cv2.waitKey(1) & 0xFF == ord("q"):
                        break
    finally:
        if writer is not None:
            writer.release()
            print(f"Saved: {out_path}")
        if args.show:
            cv2.destroyAllWindows()

    if frames == 0:
        raise SystemExit("No frames were read.")
    rate = {n: seen[n] / frames for n in names}
    return {
        "weights": estimator.weights,
        "frames": frames,
        "person_rate": person_frames / frames,
        "rate": rate,
        "kpt_rate": {i: kpt_seen[i] / frames for i in ANNOTATED},
        "jump_rate": {k: (jumps[k] / pairs[k] if pairs[k] else 0.0) for k in jumps},
        "step_median": {k: (statistics.median(v) if v else 0.0) for k, v in steps.items()},
    }


def verdict_video(result):
    """追加学習が必要な理由のリスト(空なら、このままで足りる目安)。"""
    reasons = []
    r = result["rate"]
    if r["head"] < RATE_HEAD:
        reasons.append(f"頭の検出率が低い({r['head']:.0%} < {RATE_HEAD:.0%})")
    if r["body"] < RATE_BODY:
        reasons.append(f"体の重心(肩と腰)の検出率が低い({r['body']:.0%} < {RATE_BODY:.0%})")
    if r["hand_any"] < RATE_HAND:
        reasons.append(f"手の検出率が低い({r['hand_any']:.0%} < {RATE_HAND:.0%})")
    for kind, label in (("head", "頭"), ("body", "体の重心"), ("wrist", "手首")):
        if result["jump_rate"][kind] > MAX_JUMP_RATE:
            reasons.append(f"{label}の位置が飛ぶ({result['jump_rate'][kind]:.1%} > {MAX_JUMP_RATE:.0%})")
    return reasons


def print_video_report(result):
    r = result["rate"]
    print(f"\n=== {result['weights']}  ({result['frames']} frames) ===")
    print(f"  人を検出できたフレーム : {result['person_rate']:.1%}")
    print(f"  頭               : {r['head']:.1%}   体の重心(肩+腰): {r['body']:.1%}")
    print(
        f"  手(どちらか)       : {r['hand_any']:.1%}   両手: {r['hand_both']:.1%}   "
        f"左手首: {r['left_wrist']:.1%}  右手首: {r['right_wrist']:.1%}"
    )
    print("  キーポイントごとの検出率 : " + ", ".join(
        f"{KEYPOINT_NAMES[i]} {result['kpt_rate'][i]:.0%}" for i in ANNOTATED))
    jr, sm = result["jump_rate"], result["step_median"]
    print(
        f"  位置が飛んだ割合   : 頭 {jr['head']:.1%}  体 {jr['body']:.1%}  手首 {jr['wrist']:.1%}"
        f"   (1フレームの動きの中央値[体幹=1]: 頭 {sm['head']:.3f} 体 {sm['body']:.3f} 手首 {sm['wrist']:.3f})"
    )
    reasons = verdict_video(result)
    if reasons:
        print("  → 追加学習を推奨: " + " / ".join(reasons))
    else:
        print("  → このモデルのままでも使えそうです(目安)。")


# ------------------------------------------------------------------ アノテーションとの比較


def _ground_truth_person(keypoints, kpt_conf):
    """正解のキーポイントを、PersonPose と同じ形にする(v>0 の点を信頼度 1 とする)。"""
    kps = keypoints.copy()
    kps[:, 2] = (kps[:, 2] > 0).astype(float)
    return PersonPose(kps, (0, 0, 0, 0), 1.0, 0.5)


def _iou(a, b):
    ix0, iy0, ix1, iy1 = max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, ix1 - ix0) * max(0.0, iy1 - iy0)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def _distance(a, b):
    return float(np.hypot(a[0] - b[0], a[1] - b[1]))


def evaluate_dataset(args, weights):
    """正解との誤差を調べる。結果の dict を返す。"""
    samples = list_samples(args.data, args.split)
    if not samples and args.split == "val":
        print("(No validation images. Using all images; they may have been used for training.)")
        samples = list_samples(args.data, "all")
    if not samples:
        raise SystemExit(f"No annotated images found in {args.data} (split: {args.split}).")

    estimator = PoseEstimator(weights, args.device_obj, args.imgsz, args.conf, args.kpt_conf)
    detected = 0
    kpt_errors = {i: [] for i in ANNOTATED}  # 体幹を1とした誤差
    kpt_px = {i: [] for i in ANNOTATED}
    kpt_found = {i: 0 for i in ANNOTATED}
    kpt_total = {i: 0 for i in ANNOTATED}
    derived = {"head": [], "body": [], "hand": []}
    derived_px = {"head": [], "body": [], "hand": []}
    derived_missed = {"head": 0, "body": 0, "hand": 0}
    derived_total = {"head": 0, "body": 0, "hand": 0}

    for image_path, label_path in samples:
        image = read_image(image_path)
        if image is None:
            continue
        height, width = image.shape[:2]
        truth = read_label(label_path, width, height)
        if not truth:
            continue
        gt = truth[0]
        gt_person = _ground_truth_person(gt["keypoints"], args.kpt_conf)
        scale = gt_person.torso_length or max(gt["box"][3] - gt["box"][1], 1.0) / 2.0

        people = estimator.people(image)
        match = max(people, key=lambda p: _iou(p.box, gt["box"]), default=None)
        if match is not None and _iou(match.box, gt["box"]) <= 0.05:
            match = None
        detected += match is not None

        for i in ANNOTATED:
            if gt["keypoints"][i, 2] <= 0:
                continue
            kpt_total[i] += 1
            if match is None:
                continue
            error = _distance(match.keypoints[i], gt["keypoints"][i])
            kpt_px[i].append(error)
            kpt_errors[i].append(error / scale)
            kpt_found[i] += int(match.keypoints[i, 2] >= args.kpt_conf)

        # 実際に使う値: 頭の位置・体の重心・両手
        truth_values = {"head": gt_person.head, "body": gt_person.body_center}
        pred_values = {
            "head": match.head if match else None,
            "body": match.body_center if match else None,
        }
        for kind in ("head", "body"):
            if truth_values[kind] is None:
                continue
            derived_total[kind] += 1
            if pred_values[kind] is None:
                derived_missed[kind] += 1
            else:
                d = _distance(truth_values[kind], pred_values[kind])
                derived_px[kind].append(d)
                derived[kind].append(d / scale)
        for wrist in (9, 10):
            if gt["keypoints"][wrist, 2] <= 0:
                continue
            derived_total["hand"] += 1
            if match is None or match.point(wrist) is None:
                derived_missed["hand"] += 1
            else:
                d = _distance(match.point(wrist), gt["keypoints"][wrist])
                derived_px["hand"].append(d)
                derived["hand"].append(d / scale)

    count = len(samples)

    def mean(values):
        return float(np.mean(values)) if values else float("nan")

    return {
        "weights": estimator.weights,
        "images": count,
        "detect_rate": detected / count,
        "kpt": {
            i: {
                "found": kpt_found[i] / kpt_total[i] if kpt_total[i] else float("nan"),
                "px": mean(kpt_px[i]),
                "torso": mean(kpt_errors[i]),
                "pck": (float(np.mean(np.array(kpt_errors[i]) <= 0.2)) * len(kpt_errors[i]) / kpt_total[i])
                if kpt_total[i] else float("nan"),
                "n": kpt_total[i],
            }
            for i in ANNOTATED
        },
        "derived": {
            k: {
                "px": mean(derived_px[k]),
                "torso": mean(derived[k]),
                "missed": derived_missed[k] / derived_total[k] if derived_total[k] else float("nan"),
                "n": derived_total[k],
            }
            for k in derived
        },
    }


def verdict_dataset(result):
    reasons = []
    d = result["derived"]
    for kind, label, limit in (("head", "頭", MAX_ERR_HEAD_BODY), ("body", "体の重心", MAX_ERR_HEAD_BODY), ("hand", "手", MAX_ERR_HAND)):
        info = d[kind]
        if info["n"] == 0:
            continue
        if not np.isnan(info["missed"]) and info["missed"] > 0.15:
            reasons.append(f"{label}を検出できない割合が高い({info['missed']:.0%})")
        if not np.isnan(info["torso"]) and info["torso"] > limit:
            reasons.append(f"{label}の誤差が大きい(体幹の{info['torso']:.2f}倍 > {limit:.2f})")
    if result["detect_rate"] < 0.9:
        reasons.append(f"人を検出できない画像が多い({result['detect_rate']:.0%})")
    return reasons


def print_dataset_report(result):
    print(f"\n=== {result['weights']}  ({result['images']} images) ===")
    print(f"  人を検出できた画像 : {result['detect_rate']:.1%}")
    print("  キーポイント          検出率   誤差[px]  誤差[体幹=1]  PCK@0.2   (件数)")
    for i in ANNOTATED:
        k = result["kpt"][i]
        print(
            f"  {KEYPOINT_NAMES[i]:<15}  {k['found']:>7.0%}  {k['px']:>8.1f}  {k['torso']:>11.3f}"
            f"  {k['pck']:>8.0%}   ({k['n']})"
        )
    print("  実際に使う値(正解との平均誤差):")
    for kind, label in (("head", "頭の位置"), ("body", "体の重心"), ("hand", "手(手首)")):
        d = result["derived"][kind]
        print(
            f"    {label:<8}: {d['px']:.1f}px = 体幹の {d['torso']:.3f} 倍   "
            f"検出できない {d['missed']:.0%}   (件数 {d['n']})"
        )
    reasons = verdict_dataset(result)
    if reasons:
        print("  → 追加学習を推奨: " + " / ".join(reasons))
    else:
        print("  → このモデルのままでも使えそうです(目安)。")


def print_comparison(results, kind):
    if len(results) < 2:
        return
    print("\n=== 比較 ===")
    for r in results:
        if kind == "video":
            rate = r["rate"]
            print(f"  {Path(str(r['weights'])).name:<22} 頭 {rate['head']:.0%}  体 {rate['body']:.0%}  "
                  f"手 {rate['hand_any']:.0%}  飛び(手首) {r['jump_rate']['wrist']:.1%}")
        else:
            d = r["derived"]
            print(f"  {Path(str(r['weights'])).name:<22} 頭 {d['head']['torso']:.3f}  体 {d['body']['torso']:.3f}  "
                  f"手 {d['hand']['torso']:.3f}  (誤差、体幹=1。小さいほど良い)")


def main(argv=None):
    args = parse_args(argv)
    args.device_obj = resolve_device(args.device, args.half)
    print(f"Device: {args.device_obj.label}")
    args.weights_list = args.weights or default_weights()
    kind = "data" if args.data is not None else "video"
    print(f"Models to check: {', '.join(map(str, args.weights_list))}")
    if kind == "video":
        print("Tip: with --data (annotated images) you also get the error against the ground truth.")

    results = []
    for weights in args.weights_list:
        if kind == "data":
            result = evaluate_dataset(args, weights)
            print_dataset_report(result)
        else:
            result = evaluate_video(args, weights)
            print_video_report(result)
        results.append(result)
    print_comparison(results, "data" if kind == "data" else "video")

    if kind == "video":
        print(
            "\n動画だけでは、位置が正しいかまでは分かりません(検出できたかどうかだけ)。"
            "\n--save-video で骨格を重ねた動画を作って目で確認するか、annotation.py で数十枚付けて "
            "--data で誤差を測ってください。"
        )


if __name__ == "__main__":
    main()
