# サンドバッグ パンチ検出(YOLO セグメンテーション + 骨格推定)

サンドバッグの**領域・傾き**と、人の**骨格(頭・体・両手)**を同時に推定し、
「手(手首)を中心とした円がサンドバッグに触れたか(ヒット)」と「パンチしたか」を**毎フレーム**出力する。
出力は print と、裏で動かす Unity への UDP 送信(→ 6 章)の両方に対応。

```
カメラ/動画 ─┬─ サンドバッグの領域推定 (YOLO-seg) ─┐
             └─ 骨格推定 (YOLO-pose)  ← 並列 ──────┴─ ヒット判定・パンチ判定・頭/体の動き ─→ print / UDP(Unity)
```

## 1. 既にあった機能 / 今回追加した機能

| 依頼内容 | 状況 |
|---|---|
| サンドバッグの領域推定 | **既にあった**(`vision.BagTracker`。旧 `bag_model.py`) |
| 骨格推定 | **既にあった**(`vision.PoseEstimator`。旧 `pose_estimator.py`) |
| 手首を中心とした円とサンドバッグの接触判定 | **既にあった**(`logic.hit_test`。円の半径 = `--hand-radius` 画素、または バッグ幅 × `--hand-radius-ratio`) |
| 骨格の検証 | **既にあった**(`valid.py` → 今回 **`validation.py`** に改名) |
| 毎フレームの 頭・体の座標・ヒット有無の出力 | **新規**(`output.py`。print と JSON) |
| Unity との通信 | **新規**(`--udp HOST:PORT`。6 章) |
| GPU 切り替え | **新規**(`--device auto/cpu/cuda/0/mps`、`--half`。以前は `--yolo-device` を手で指定するだけ) |
| リファクタリング | **新規**(22 ファイル/5,206 行 → 13 ファイル/約 3,100 行。色追跡・グローブ検出などの不要部分を削除) |

## 2. 必要なもの

```
pip install ultralytics opencv-python numpy
```
GPU を使うには、NVIDIA GPU 用の PyTorch を入れる(https://pytorch.org の手順)。`python -c "import torch; print(torch.cuda.is_available())"` が `True` なら使える。

## 3. 使い方(最短手順)

```bash
# 1. バッグの教師データを作る(動画ごとにバッグの四隅をクリック)→ バッグの YOLO を学習(bag_yolo.pt ができる)
python collect_dataset.py bag1.mp4 bag2.mp4
python train_yolo.py

# 2.(任意)骨格推定の精度を確認 → 必要なら追加学習(pose_model.pt ができ、以降自動で使われる)
python validation.py bag.mp4
python annotation.py bag.mp4 && python train.py
python validation.py --data pose_dataset --weights yolo11n-pose.pt pose_model.pt

# 3. 実行
python realtime.py                       # カメラ(C キーを押している間に頭・体の基準位置を測る)
python main.py bag.mp4 --save-video      # 録画動画で確認
```

詳しい引数は **COMMANDS.md**。

## 4. ファイル構成

| ファイル | 役割 |
|---|---|
| `main.py` | 動画ファイルで実行(検証用) |
| `realtime.py` | カメラで実行(スレッドで最新フレームだけ処理) |
| `validation.py` | 骨格推定の検証(動画 / 正解付きデータ。複数モデルの比較) |
| `pipeline.py` | 1フレームの処理の流れ(`Pipeline.process` → `FrameResult`) |
| `vision.py` | `BagTracker`(領域・傾き)、`PoseEstimator`(骨格)、`Perception`(並列実行) |
| `logic.py` | `hit_test`、`PunchJudge`、`BodyMotion`(頭/体の動き)、`HoldKey` |
| `output.py` | `FrameResult`、`Reporter`(print / UDP)、画面への描画 |
| `args.py` | `main.py` / `realtime.py` 共通の引数 |
| `common.py` | キーポイント定義、画像/ラベル入出力、**`resolve_device`(GPU 切り替え)** |
| `collect_dataset.py` / `train_yolo.py` | バッグの教師データ作成 / 学習 |
| `annotation.py` / `train.py` | 骨格のアノテーション / 学習 |

## 5. 判定の仕組み

- **ヒット**: 各手首を中心とする半径 R の円がバッグの輪郭と接触・重なっていれば 1(`cv2.pointPolygonTest`)。
  R は `--hand-radius`(画素)か、未指定ならバッグ幅 × `--hand-radius-ratio`(0.5)。キャリブレーション中も毎フレーム出る。
- **パンチ**(`--threshold` で切り替え):
  - **指定なし(既定)= 接触のみ**: 手の円がバッグに新しく触れた瞬間に 1 回(`--cooldown` 0.5 秒の間隔つき)。傾きは見ない。
  - **`--threshold 8` のように数値を指定 = 接触 + 傾き**: 接触の後 0.3 秒以内(または直前 0.1 秒以内)にバッグの傾きが静止時から指定の度数以上ずれたとき 1 回。軽く触れただけの接触は数えない。
  - どちらも、1回の接触で1回だけ。`--no-contact-gate`(または `--no-pose`)は接触を使わない傾きだけの判定(`--threshold` 省略時は 8°)。
- **頭の座標** = 鼻・目・耳のうち見える点の平均。**体の座標** = 肩の中点と腰の中点の中央。見えなければ `none`/`null`。
- **頭と体の動き**: キャリブレーション時の位置からのずれ(頭の上下、体の前後)。横向きで奥側の点が隠れても飛ばないよう、点ごとに基準を持つ。
- **並列**: バッグ推定と骨格推定を別スレッドで同時に実行(ダミー 50ms の測定で 133ms → 90ms)。`--no-parallel` で無効。

### GPU の切り替え

```bash
python realtime.py                       # 既定 --device auto: CUDA があれば GPU(FP16)、Mac は mps、無ければ CPU
python realtime.py --device cpu          # 強制的に CPU
python realtime.py --device 0            # GPU 0 番(cuda も同じ)
python realtime.py --device cuda --no-half   # GPU で FP32(FP16 で結果がおかしい場合)
python realtime.py --stats               # 処理速度(fps, ms/frame)を2秒ごとに表示
```
- FP16 は NVIDIA GPU のみ既定で有効(`--half` / `--no-half` で上書き)。
- GPU が指定されたのに使えないときは、警告を出して CPU で続行する。
- `train_yolo.py` / `train.py` / `annotation.py` / `validation.py` も同じ `--device` を持つ。
- ※ この開発環境には GPU が無く、**GPU での動作は未確認**(CPU での動作と、FP16 引数が受理されることだけ確認)。

## 6. 毎フレームの出力と Unity との通信

### 6.1 print(既定)

```
frame=250 t=8.333 head=(317.0,161.8) body=(309.8,320.0) hit=1 punch=0
```
座標は画素(左上が原点)。`hit` は手の円にバッグが入っていれば 1、骨格が取れなければ `none`。`punch` はパンチと判定したフレームだけ 1。
`--format json` で JSON、`--print-every N` で間引き(パンチのフレームは必ず出る)、`--quiet` で非表示。

### 6.2 UDP で送る JSON(既定は 4 項目だけ)

`--udp` で送るのは、毎フレーム次の 4 項目だけ。

```json
{"forward": 0.25, "head": 1.0, "feet": 0.0, "punch": 0}
```
| キー | 意味 | 範囲 | null になる条件 |
|---|---|---|---|
| `forward` | **体の重心**の前後位置。キャリブレーション時のバッグの位置を **1**、プレイヤー側の画面の端を **0** とした割合 | 0〜1(範囲外は丸める) | キャリブレーション前、体(肩・腰)が見えないとき |
| `head` | **頭**の高さ。画面の下を **0**、上を **1** とした割合 | 0〜1 | 頭が見えないとき |
| `feet` | 予約。**常に 0.0** | 0.0 | なし |
| `punch` | **このフレームで**パンチと判定したか。パンチの瞬間の 1 フレームだけ 1 | 0 / 1 | なし |

- `forward` の「プレイヤー側の画面の端」は、キャリブレーション時のバッグと体の位置関係で自動的に決まる(プレイヤーが画面の左にいれば左端、右にいれば右端)。
  `forward = (体の x − 端の x) / (キャリブレーション時のバッグの x − 端の x)`。プレイヤーが右なら端は画面の右端(x = 幅)。
  体がバッグの位置まで来れば 1、端にいれば 0。例: 幅 960 でプレイヤーが左、バッグが x=700 のとき、体が x=300 なら 0.43。
- 体の重心は肩・腰の位置、頭は鼻・目・耳の位置から求める。横向きで一部の点が隠れても値が飛ばないよう、キャリブレーション時の位置からのずれに、その基準の位置を足して求めている。
- `forward` はキャリブレーション(`realtime.py` は **C キーを押している間**、`--no-display` や動画は最初の `--motion-calib-frames` フレーム)が終わるまで null。`head` は基準がなくても、見えていれば出る。
- 座標の基準は映像の**大きさ**なので、カメラの解像度を変えても同じ値になる。

`punch` は 1 フレームだけ 1 になる「出来事」なので、そのパケットが UDP で失われると、そのパンチは受信側に伝わらない。
`PunchReceiver.cs` は、受信のたびに(Update を待たず)`punch` を数えるので、受信側で見逃すことはない。通信路での取りこぼしが気になるときは、
`--udp-full` で `punches`(累計)も送り、増加で検知する。

全項目(上の4項目は `unity` に入る。ほかに頭・体の画素座標、手の座標、`hit`、`punches`、バッグの傾き、頭と体の動き量など)が要るときは `--udp-full`。`--format json` での print も全項目:

```json
{"frame":250,"t":8.333,"size":[960,540],"head":[317.0,161.8],"body":[309.8,320.0],
 "hands":[[608.9,264.0],[379.8,290.0]],"hit":1,"punch":0,"punches":4,
 "bag":{"angle":-7.23,"deviation":-7.19},
 "motion":{"vertical":-0.083,"forward":0.17,"vertical_px":-10.0,"forward_px":20.4}}
```

### 6.3 Unity へ送る(UDP)

Python を Unity の裏で動かし、**毎フレーム JSON を 1 パケットの UDP** で送る。画面は要らないので `--no-display --quiet` で動かす。

```bash
python realtime.py --no-display --quiet --udp 127.0.0.1:5005
```
別 PC の Unity に送るときは、その PC の IP を指定する(例 `--udp 192.168.1.20:5005`。ファイアウォールで UDP 5005 を許可)。

Unity (C#) の受信スクリプトは **`unity_test/PunchReceiver.cs`**(空の GameObject に付ける)。
JSON の解析に Newtonsoft.Json を使うので、Package Manager の「Add package by name」で `com.unity.nuget.newtonsoft-json` を追加する
(標準の `JsonUtility` は `null` を扱えないため)。テスト用の送信 `unity_test/send_dummy.py`、Unity なしの受信確認 `unity_test/recv_check.py` もある。
同じ PC なら `--udp 127.0.0.1:5005`、別 PC なら Unity 側の IP を指定(UDP 5005 をファイアウォールで許可)。

### 6.4 通信方式の選び方

- **UDP(現在の実装)**: 同一 PC / LAN なら遅延が最小で実装も簡単。パケットは失われうるので、`punch` は 1 回のパンチで 1 パケットだけ 1 になるので、落ちると数え損ねる(確実にしたいときは `--udp-full` の `punches`(累計)の増加で検知する)。
- 取りこぼしが許されない場合は TCP(接続を張って 1 行 1 JSON)や WebSocket にする。送信は `output.Reporter.emit` の 1 か所を変えるだけでよい。
- 画像を一緒に渡す必要がある場合は共有メモリなどが必要だが、座標だけなら UDP で十分。
- 受信側が居なくても Python は止まらない(送信エラーは無視)。

## 7. 精度を上げるコツ

- 手首の円が大きすぎるとヒットが多すぎ、小さすぎると見逃す。`--hand-radius-ratio` か `--hand-radius` を、`main.py --save-video` の動画の円を見て調整する。
- 横向きでは奥側の手が隠れやすい。`validation.py` で検出率を確認し、悪ければ `annotation.py` → `train.py` で追加学習。
- 接触だけだと、手が触れ続ける・かすっただけでも数えるので、多すぎるときは `--threshold 8` で傾きも条件にする、`--hand-radius-ratio` を小さくする、`--cooldown` を伸ばす。
- 速度が足りなければ `--yolo-imgsz 256`、`--pose-imgsz 320`、GPU を使う。

## 8. 速くするには(起動・1フレームの処理時間)

### 起動が遅いとき
起動時間の内訳(CPU・2コアの開発環境で実測): `import torch` 約 1.4 秒 + バッグのモデル読み込み/ウォームアップ 約 1.4 秒 + 骨格 約 1.4 秒。
- 2つのモデルは**同時に読み込む**ようにした(合計 2.8 秒 → 1.6 秒)。起動時に `Models ready in X s` と出る。
- 使うモデルが全部手元にあれば、ultralytics のネット接続確認を省く(`YOLO_OFFLINE`)。ネットが遅い・繋がらない環境で数秒止まるのを防ぐ。
  `yolo11n-pose.pt` が手元に無いと自動ダウンロードするので、**一度ダウンロードしてプロジェクトのフォルダに置いておく**(または `train.py` で `pose_model.pt` を作る)。
- GPU のときは、CUDA の初期化と最初の推論(ウォームアップ)に数秒かかる。これは1回だけで、実行中は速い。
- 起動のたびに待つのが嫌なら、`realtime.py` を終了させずに動かしっぱなしにする(Unity 側から再起動しない)。

### 1フレームの処理が速くならないとき
**バッグ推定と骨格推定は並列に動くので、全体の時間は遅い方で決まる**。片方だけ小さくしても全体はほとんど変わらない。実測(CPU・960x540):

| `--yolo-imgsz` | `--pose-imgsz` | 並列 ms/frame |
|---|---|---|
| 320 | 640 | 84(骨格が支配) |
| 160 | 640 | 84(バッグだけ半分にしても変わらない) |
| 320 | 320 | 39 |
| 160 | 320 | 32 |
| 224 | 256 | 31 |

- 画像サイズを半分にするのは、**カメラ/動画の解像度(`--width/--height`)ではなく、`--yolo-imgsz` と `--pose-imgsz`**。モデルは入力を内部でこのサイズに縮めるので、元の解像度を下げても推論時間はほぼ変わらない(デコードは 1ms 未満)。
- 骨格(既定 640)の方が重い。まず `--pose-imgsz 320` にする。手が小さく映る場合は精度が落ちるので、`validation.py --imgsz 320` で確認する。
- そこから先は下限がある(前処理・後処理などの固定費)。サイズを半分にしても時間は半分にならない。
- `--stats` で ms/frame を見ながら調整する。`--no-display` で描画も省ける。
- GPU(`--device auto`)が最も効く。CPU のままなら、ONNX に書き出すと約 20〜25% 速い(実測: バッグ 33→24 ms、骨格 90→74 ms @640、30→21 ms @320)。`yolo export model=bag_yolo.pt format=onnx imgsz=320` で作り、`--yolo-weights bag_yolo.onnx` のように指定する(※ 書き出し時の `imgsz` と実行時のサイズを揃える。本プロジェクトでの通しの動作は未確認)。Intel CPU なら OpenVINO 形式がさらに速いことが多い。
- CPU のコア数が少ないと、2つのモデルを並列に動かしても取り合いになる。その場合は `--no-parallel` と比べて速い方を選ぶ。

## 9. よくあるトラブル

| 症状 | 対処 |
|---|---|
| `Bag weights not found` | `collect_dataset.py` → `train_yolo.py` で `bag_yolo.pt` を作る |
| GPU を指定したのに CPU になる | 警告を確認。`torch.cuda.is_available()` が False なら CUDA 版 PyTorch を入れ直す |
| 速度が出ない | `--stats` で ms/frame を確認。GPU、`--pose-imgsz` を小さく、`--no-display` |
| `hit` がいつも none | 骨格が検出できていない。`validation.py` で確認 / `--pose-conf`、`--kpt-conf` を下げる |
| パンチが出ない | `hit` が 1 になるか見る(`--hand-radius-ratio` を調整)。`--threshold` 指定時は `deviation` がしきい値に届くかも見る |
| Unity に届かない | ポート/IP、ファイアウォールを確認。`python -c "import socket;s=socket.socket(2,2);s.bind(('127.0.0.1',5005));print(s.recv(999))"` で受信を確かめる |

## 10. 変更点と制限事項

- 色追跡(`--tracker color`)、グローブ・頭・体の YOLO クラスは削除した(バッグのモデルはバッグのみ。古い多クラスモデルもバッグ以外は無視して読める)。`--no-glove-gate` は `--no-contact-gate` の別名として残した。
- `valid.py` は `validation.py` に改名。
- 合成データでの確認(パンチ 4 回 → 4 回検出、UDP 420 フレーム → 420 パケット、頭/体の動きの誤差 1px 未満)まで。**実際の横向き映像での精度、GPU での動作は未確認**。
- 骨格は画面内で最も大きい 1 人だけを使う。
