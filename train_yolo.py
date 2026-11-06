"""collect_dataset.py で作ったデータで YOLO セグメンテーションモデルを学習する。

学習するのは、サンドバッグ(bag)。人の頭・体・手は、骨格推定(pose_estimator.py)で求める
ので、ここでは学習しない。(以前のデータに glove / head / body のラベルがあっても、学習はできるが、
今のプログラムはそれらを使わない。)

    python train_yolo.py
    python train_yolo.py --epochs 80 --imgsz 320 --device 0

学習が終わると、最良のモデルを bag_yolo.pt として保存する
(main.py / realtime.py の --tracker yolo が既定で読み込むファイル)。

--base の yolo11n-seg.pt は、初回に自動でダウンロードされる(インターネット接続が必要)。
GPU が無くても学習できるが時間がかかる。imgsz を小さくする・epochs を減らすと速い。
"""

import argparse
import shutil
from pathlib import Path

from classes import CLASSES
from yolo_pose import DEFAULT_YOLO_WEIGHTS

HERE = Path(__file__).parent


def parse_args():
    parser = argparse.ArgumentParser(description="Train a YOLO segmentation model for the punching bag.")
    parser.add_argument(
        "--data", type=Path, default=HERE / "dataset" / "data.yaml", help="Dataset config"
    )
    parser.add_argument(
        "--base",
        default="yolo11n-seg.pt",
        help="Base model to fine-tune (default: yolo11n-seg.pt, the smallest one)",
    )
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--imgsz", type=int, default=320, help="Training image size")
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--device", default=None, help='"cpu", "0" (GPU), ... (default: auto)')
    parser.add_argument("--project", type=Path, default=HERE / "runs", help="Training output folder")
    parser.add_argument("--name", default="bag", help="Run name")
    parser.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_YOLO_WEIGHTS,
        help=f"Where to save the best weights (default: {DEFAULT_YOLO_WEIGHTS.name})",
    )
    return parser.parse_args()


def check_dataset(data_yaml):
    """学習の前にデータの枚数を確認する。少なすぎるときは止め、足りないときは警告する。"""
    root = data_yaml.parent
    n_train = len(list((root / "images" / "train").glob("*.jpg")))
    n_val = len(list((root / "images" / "val").glob("*.jpg")))
    n_labels = len(list((root / "labels" / "train").glob("*.txt")))
    print(f"Dataset: {n_train} train / {n_val} val images ({n_labels} train labels)")

    if n_train < 20 or n_labels < 20:
        raise SystemExit(
            "Too few training images. Run collect_dataset.py on more video "
            "(at least a few hundred images are recommended)."
        )
    # クラスごとの物体の数。少ないクラスは、検出が不安定になる
    by_id = {number: key for key, number, _ in CLASSES}
    counts = {key: 0 for key in by_id.values()}
    for label in (root / "labels" / "train").glob("*.txt"):
        for line in label.read_text(encoding="utf-8").splitlines():
            head = line.split(" ", 1)[0]
            if head.isdigit() and int(head) in by_id:
                counts[by_id[int(head)]] += 1
    print("Objects in the training labels: " + ", ".join(f"{k} {n}" for k, n in counts.items()))
    if counts.get("bag", 0) == 0:
        raise SystemExit("No 'bag' labels in the training data. Run collect_dataset.py first.")
    if counts["bag"] < 100:
        print(f"Warning: only {counts['bag']} 'bag' labels. Collect more for stable detection.")
    if any(counts[k] for k in counts if k != "bag"):
        print("Note: glove / head / body labels are not used by the current program "
              "(pose estimation is used for the person). They are trained but ignored.")

    if n_train < 200:
        print(
            "Warning: fewer than 200 training images. It will train, but the model may "
            "not work well on different lighting or backgrounds. Add more videos."
        )


def main():
    args = parse_args()
    if not args.data.exists():
        raise SystemExit(
            f"Dataset config not found: {args.data}\nRun collect_dataset.py first."
        )
    check_dataset(args.data)
    try:
        from ultralytics import YOLO
    except ImportError:
        raise SystemExit("ultralytics is not installed. Run: pip install ultralytics")

    model = YOLO(args.base)
    model.train(
        data=str(args.data),
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        workers=args.workers,
        device=args.device,
        project=str(args.project),
        name=args.name,
        exist_ok=True,
    )

    best = Path(model.trainer.best)
    shutil.copy2(best, args.out)
    print(f"Saved the trained model: {args.out}")
    print("Use it with: python realtime.py --tracker yolo")


if __name__ == "__main__":
    main()
