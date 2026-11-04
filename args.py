"""main.py と realtime.py で共通のコマンドライン引数。"""

import argparse
from pathlib import Path

from vision import DEFAULT_BAG_WEIGHTS


def add_pipeline_args(parser):
    g = parser.add_argument_group("計算機 (GPU)")
    g.add_argument("--device", "--yolo-device", dest="device", default="auto",
                   help='Where to run the models: "auto" (GPU if available, else CPU), "cpu", '
                        '"cuda" / "cuda:1" / "0" (NVIDIA GPU), "mps" (Apple). Default: auto')
    g.add_argument("--half", action=argparse.BooleanOptionalAction, default=None,
                   help="FP16 (half precision) inference. Default: on for NVIDIA GPUs, off otherwise")
    g.add_argument("--no-parallel", action="store_true",
                   help="Run the bag detection and the pose estimation one after the other (default: in parallel)")

    g = parser.add_argument_group("バッグの領域推定 (YOLO)")
    g.add_argument("--yolo-weights", type=Path, default=DEFAULT_BAG_WEIGHTS,
                   help=f"Trained bag segmentation weights (default: {DEFAULT_BAG_WEIGHTS.name})")
    g.add_argument("--yolo-imgsz", type=int, default=320, help="Bag inference image size (default: 320)")
    g.add_argument("--yolo-conf", type=float, default=0.4, help="Bag confidence threshold (default: 0.4)")

    g = parser.add_argument_group("骨格推定")
    g.add_argument("--no-pose", action="store_true",
                   help="No pose estimation: no hit test / head / body output (punches are judged by tilt only)")
    g.add_argument("--pose-weights", type=Path, default=None,
                   help="Pose model (default: pose_model.pt if it exists, else yolo11n-pose.pt)")
    g.add_argument("--pose-imgsz", type=int, default=640, help="Pose inference image size (default: 640)")
    g.add_argument("--pose-conf", type=float, default=0.25, help="Person detection confidence (default: 0.25)")
    g.add_argument("--kpt-conf", type=float, default=0.5,
                   help="A keypoint is used only if its confidence is at least this (default: 0.5)")

    g = parser.add_argument_group("ヒットとパンチの判定")
    g.add_argument("--hand-radius", type=float, default=None,
                   help="Radius (pixels) of the circle around each wrist; a hit = the bag's region "
                        "overlaps it. Default: bag width x --hand-radius-ratio")
    g.add_argument("--hand-radius-ratio", type=float, default=0.5,
                   help="Radius as a ratio of the bag width, used without --hand-radius (default: 0.5)")
    g.add_argument("--threshold", type=float, default=None,
                   help="Not given (default): a punch = a new hand contact only. With a number: a contact AND "
                        "a bag tilt change of at least this many degrees from rest (with --no-contact-gate / "
                        "--no-pose: tilt only, 8.0 if omitted)")
    g.add_argument("--cooldown", type=float, default=0.5, help="Minimum seconds between punches (default: 0.5)")
    g.add_argument("--calib-frames", type=int, default=30, help="Frames used to measure the resting tilt (default: 30)")
    g.add_argument("--contact-window", type=float, default=0.3,
                   help="With --threshold: the bag must sway within this many seconds after a hit (default: 0.3)")
    g.add_argument("--contact-pre", type=float, default=0.1,
                   help="Also count a sway that began up to this many seconds before the hit (default: 0.1)")
    g.add_argument("--contact-release", type=float, default=0.15,
                   help="A hand must be away this many seconds before a hit counts as a new contact (default: 0.15)")
    g.add_argument("--sway-max", type=float, default=None,
                   help="Upper limit (degrees) of the sway counted as a punch (default: none)")
    g.add_argument("--no-contact-gate", "--no-glove-gate", dest="no_contact_gate", action="store_true",
                   help="Judge punches by the bag's tilt only, even with pose estimation (old behavior)")

    g = parser.add_argument_group("頭と体の動き")
    g.add_argument("--no-motion", action="store_true", help="Do not compute the head / body movement")
    g.add_argument("--forward-direction", choices=["auto", "left", "right"], default="auto",
                   help="Which side of the image is 'forward' (auto = toward the bag at calibration)")
    g.add_argument("--motion-smooth", type=float, default=0.5,
                   help="Smoothing of the movement values; 1 = none (default: 0.5)")
    g.add_argument("--motion-calib-frames", type=int, default=30,
                   help="Video / --no-display: frames at the start used for the reference position (default: 30)")

    g = parser.add_argument_group("毎フレームの出力 (print / Unity)")
    g.add_argument("--quiet", action="store_true", help="Do not print the per-frame output (UDP still works)")
    g.add_argument("--format", dest="fmt", choices=["text", "json"], default="text",
                   help="Per-frame print format (default: text)")
    g.add_argument("--print-every", type=int, default=1, help="Print every N-th frame (punch frames always) (default: 1)")
    g.add_argument("--udp", metavar="HOST:PORT", default=None,
                   help="Also send every frame to Unity via UDP as JSON {forward, head, feet, punch}, e.g. 127.0.0.1:5005")
    g.add_argument("--udp-full", action="store_true",
                   help="Send all fields (hands, punch, punches, bag, motion...) over UDP instead of only forward/head/feet/punch")
    # 以前の版との互換(色追跡は廃止した)
    parser.add_argument("--tracker", choices=["yolo", "color"], default="yolo", help=argparse.SUPPRESS)
    return parser


def check_args(parser, args):
    if args.tracker == "color":
        parser.error("--tracker color was removed (YOLO is the only bag detector now). "
                     "Train the bag model: collect_dataset.py → train_yolo.py")
    return args
