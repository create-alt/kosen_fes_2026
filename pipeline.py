"""1フレームの処理の流れを1か所にまとめる(main.py / realtime.py 共通)。

    pipeline = Pipeline(args)
    result = pipeline.process(frame, frame_index, now)   # FrameResult(頭・体・ヒット・パンチ・動き量)
    pipeline.bag / pipeline.person                       # 直近の推定(描画用)
"""

import time
from concurrent.futures import ThreadPoolExecutor

from common import resolve_device
from logic import BodyMotion, PunchJudge
from output import FrameResult
from vision import BagTracker, Perception, PoseEstimator, prefer_offline, resolve_pose_weights


class Pipeline:
    def __init__(self, args, motion_auto_frames=None):
        """motion_auto_frames: 最初のこのフレーム数で頭・体の基準を自動で測る。None なら set_calibrating で手動。"""
        self.device = resolve_device(args.device, args.half)
        print(f"Device: {self.device.label}")
        # 2つのモデルの読み込みとウォームアップ(初回推論)は独立なので、同時に行って起動を短くする
        prefer_offline(args.yolo_weights, None if args.no_pose else resolve_pose_weights(args.pose_weights))
        started = time.perf_counter()
        with ThreadPoolExecutor(max_workers=2) as pool:
            bag_job = pool.submit(BagTracker, args.yolo_weights, self.device, args.yolo_imgsz, args.yolo_conf)
            pose_job = None if args.no_pose else pool.submit(
                PoseEstimator, args.pose_weights, self.device, args.pose_imgsz, args.pose_conf, args.kpt_conf)
            bag = bag_job.result()
            pose = pose_job.result() if pose_job else None
        if pose is not None:
            print(f"Pose: {pose.weights} (imgsz={args.pose_imgsz}, "
                  f"{'sequential' if args.no_parallel else 'parallel with the bag detection'})")
        else:
            print("Pose: off (--no-pose)")
        print(f"Models ready in {time.perf_counter() - started:.1f} s")
        self.perception = Perception(bag, pose, parallel=not args.no_parallel)

        use_hands = pose is not None and not args.no_contact_gate
        mode = ("hand hit only" if args.threshold is None else f"hand hit + bag sway >= {args.threshold}deg") if use_hands \
            else f"bag sway only (>= {args.threshold if args.threshold is not None else 8.0}deg)"
        print("Punch judgment: " + mode)
        if use_hands:
            left = args.cooldown if args.cooldown_left is None else args.cooldown_left
            right = args.cooldown if args.cooldown_right is None else args.cooldown_right
            print(f"Interval per hand: left {left}s / right {right}s"
                  + (f" (either hand: {args.cooldown_any}s)" if args.cooldown_any else ""))
        self.judge = PunchJudge(
            use_hands=use_hands, threshold=args.threshold, cooldown=args.cooldown,
            cooldown_left=args.cooldown_left, cooldown_right=args.cooldown_right, cooldown_any=args.cooldown_any,
            calib_frames=args.calib_frames, hand_radius=args.hand_radius,
            hand_radius_ratio=args.hand_radius_ratio, window=args.contact_window,
            pre_window=args.contact_pre, release=args.contact_release, sway_max=args.sway_max,
        )
        self.motion = None
        if pose is not None and not args.no_motion:
            self.motion = BodyMotion(motion_auto_frames, args.motion_smooth, args.forward_direction, depth_range=args.depth_range)
        self.bag = self.person = None

    @property
    def calibrating(self):
        """いま基準(バッグの傾き・頭・体)を測っている間か。"""
        return self.judge.calibrating or bool(self.motion and self.motion.calibrating)

    def set_calibrating(self, active):
        """
        active=True の間、バッグの傾き・頭・体の位置・バッグの位置を集め、False にしたとき、その値を新しい基準にする。
        (起動直後に限らず、いつでも測り直せる。realtime.py / main.py では C キーを押している間。)
        """
        self.judge.set_calibrating(active)
        if self.motion is not None:
            self.motion.set_calibrating(active)

    def process(self, frame, index, now):
        self.bag, self.person = self.perception.process(frame)
        sides = self.person.hands_by_side if self.person else {}
        punch = self.judge.update(self.bag, sides, now)
        reading = self.motion.update(self.person, self.bag.center[0] if self.bag else None) if self.motion else None

        h, w = frame.shape[:2]
        forward = head = None
        if self.motion is not None and reading is not None:
            forward, head = self.motion.normalized(reading, w, h)
        if head is None and self.person is not None and self.person.head is not None:
            head = max(0.0, min(1.0, 1.0 - self.person.head[1] / h))  # 基準がまだ無いときは、見えている頭の位置そのまま
        j = self.judge
        return FrameResult(
            frame=index, time=now, size=(w, h),
            head=self.person.head if self.person else None,
            body=self.person.body_center if self.person else None,
            hands=list(sides.values()), hands_lr=dict(sides), hit=j.hit, hit_sides=j.hit_sides if j.use_hands else {},
            punch=punch, punch_sides=list(j.punch_sides), punches=j.punch_count,
            bag_angle=self.bag.angle_deg if self.bag else None,
            bag_deviation=j.deviation if (self.bag and j.calibrated) else None,
            vertical=reading.vertical if reading else None, forward=reading.forward if reading else None,
            vertical_px=reading.vertical_px if reading else None, forward_px=reading.forward_px if reading else None,
            game_forward=forward, game_head=head, game_depth=reading.depth if reading else None,
            size_ratio=reading.size_ratio if reading else None,
        )

    def close(self):
        self.perception.close()
