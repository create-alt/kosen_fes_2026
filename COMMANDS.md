# 実行ファイルの使い方(コマンドライン引数ガイド)

仕組みと Unity 連携は README.md。ここでは各コマンドの引数をまとめる。`--help` でも見られる。

```
データ作成・学習   collect_dataset.py → train_yolo.py   (バッグ)
                   annotation.py → train.py              (骨格・任意)
検証               validation.py                         (骨格推定)
実行               main.py (動画) / realtime.py (カメラ)
```

## 1. `collect_dataset.py` — バッグの教師データを作る

```bash
python collect_dataset.py bag1.mp4 bag2.mp4      # 動画ごとにバッグの四隅を4回クリック → Enter
python collect_dataset.py 0                       # カメラ0から(q で終了)
python collect_dataset.py bag1.mp4 --fresh        # 前のデータを消して作り直す
```
囲んだ範囲をバッグとして、テンプレートマッチングで追いかけ、YOLO 形式で `dataset/` に保存する(クラスは `punching_bag` のみ)。

| 引数 | 既定 | 意味 |
|---|---|---|
| `--out` | `dataset` | 出力フォルダ |
| `--fresh` | - | 既存のデータを消してから作る |
| `--start-frame` | 0 | バッグを囲むフレーム(= 収集開始位置) |
| `--every` | 3 | N フレームに 1 枚保存 |
| `--max-images` | 600 | 1動画あたりの最大枚数 |
| `--save-width` | 960 | 保存画像の幅(縮小) |
| `--max-tilt` / `--tilt-step` | 15 / 1.5 | 追跡で試す傾き(度)の範囲 / 刻み |
| `--context` | 0.15 | 照合に使う周囲の割合 |
| `--min-match` | 0.5 | これ未満の一致度のフレームは保存しない |
| `--search-margin` | 0.8 | 前の位置の周りを探す範囲(速いパンチなら上げる) |
| `--preview-frames` | 150 | 開始前に追跡を試して確認する枚数(0 で無効) |
| `--select` | `quad` | `quad`: 四隅クリック / `rect`: ドラッグ |
| `--no-light-normalize` | - | 影の補正を切る(非推奨) |
| `--no-display` | - | 画面を出さない |

コツ: バッグだけを境目ぎりぎりで囲む。照明・背景・パンチの強さが違う動画を複数使う(目安 数百枚以上)。

## 2. `train_yolo.py` — バッグの YOLO を学習する

```bash
python train_yolo.py                       # GPU があれば自動で使う → bag_yolo.pt
python train_yolo.py --device cpu --epochs 30
```
| 引数 | 既定 | 意味 |
|---|---|---|
| `--data` | `dataset/data.yaml` | データ |
| `--base` | `yolo11n-seg.pt` | 元のモデル(初回は自動ダウンロード) |
| `--epochs` / `--batch` / `--workers` | 60 / 16 / 2 | 学習設定 |
| `--imgsz` | 320 | 学習画像サイズ(実行時の `--yolo-imgsz` と揃える) |
| `--device` | `auto` | `auto` / `cpu` / `cuda` / `0` / `mps` |
| `--project` / `--name` | `runs` / `bag` | ログの場所 |
| `--out` | `bag_yolo.pt` | 出力 |

## 3. `validation.py` — 骨格推定を検証する(追加学習が要るかの判断)

```bash
python validation.py bag.mp4                                   # 動画: 頭・肩・腰・手首の検出率と、位置が飛ばないか
python validation.py bag.mp4 --save-video                      # 骨格を重ねた動画も保存(目で確認)
python validation.py --data pose_dataset                       # 正解付きデータ: 正解との誤差
python validation.py --data pose_dataset --weights yolo11n-pose.pt pose_model.pt   # 学習の前後を並べて比較
```
| 引数 | 既定 | 意味 |
|---|---|---|
| `source` | - | 動画、または画像フォルダ(正解は不要) |
| `--data` | - | `annotation.py` で作ったデータ(正解との誤差を測る) |
| `--split` | `val` | `val` / `train` / `all`(`--data` のとき。学習に使っていない `val` が公平) |
| `--weights` | 標準 + `pose_model.pt` | 検証するモデル(複数可) |
| `--imgsz` / `--conf` / `--kpt-conf` | 640 / 0.25 / 0.5 | 推論設定(実行時と揃える) |
| `--device` / `--half` / `--no-half` | `auto` | GPU 設定 |
| `--every` / `--max-frames` | 1 / - | 動画の間引き / 最大フレーム数 |
| `--save-video [PATH]` | - | 結果を重ねた動画を保存 |
| `--show` | - | 画面に表示(q で停止) |

結果の見方: 「人を検出できたフレーム」「頭/体の重心/手の検出率」「位置が飛んだ割合」。`--data` では体幹を 1 とした誤差も出る。
最後の「→ このモデルのままで使えそう / 追加学習を検討」は目安。

## 4. `annotation.py` — 骨格のアノテーション

```bash
python annotation.py bag.mp4                  # 60フレームごとに表示。ずれた点だけ直す
python annotation.py bag.mp4 --every 20       # もっと多く
python annotation.py images_folder/
```
現在のモデルの推定が下書きで出る。**左ドラッグ**: 点を動かす / 空きをクリック: 選択中の点を置く / **右クリック**: 点を消す。
キー: `Enter`/`Space` 保存して次へ、`s` スキップ、`b` 1つ戻る、`Tab`/`]` `[` 点の選択、`d` 点を「無し」、`r` 下書きに戻す、`x` 全消し、`p` 前回の点を貼る、`q`/`Esc` 終了。
左右は人の体の左右で付け、迷ったらカメラに近い側を left に統一する。

| 引数 | 既定 | 意味 |
|---|---|---|
| `source` | - | 動画または画像フォルダ |
| `--out` | `pose_dataset` | 出力フォルダ |
| `--every` / `--start` | 60 / 0 | フレームの間引き / 開始位置 |
| `--weights` | `pose_model.pt` or 標準 | 下書きに使うモデル |
| `--no-predict` | - | 下書きなし(空から) |
| `--imgsz` / `--device` | 640 / `auto` | 下書きモデルの設定 |
| `--redo` | - | 付け済みのフレームも再度出す |
| `--val-ratio` | 0.15 | 検証用に分ける割合 |
| `--save-width` | 1280 | 保存時の最大幅(0 で縮小しない) |

目安: 50 枚未満だと不安定。同じ人・同じカメラ位置の映像で数十〜数百枚。

## 5. `train.py` — 骨格推定を学習する

```bash
python train.py                               # pose_dataset/ → pose_model.pt(以降の実行で自動で使われる)
python train.py --base yolo11n-pose.pt        # 標準から学び直す
python train.py --epochs 80 --device cpu
```
| 引数 | 既定 | 意味 |
|---|---|---|
| `--data` | `pose_dataset/data.yaml` | データ |
| `--base` | `pose_model.pt` があればそれ、無ければ標準 | 元のモデル |
| `--out` | `pose_model.pt` | 出力(上書き前のモデルは `pose_model_prev.pt` に残る) |
| `--epochs` / `--imgsz` / `--batch` | 60 / 640 / 8 | 学習設定 |
| `--device` | `auto` | GPU 設定 |
| `--workers` | 2 | Windows で止まるなら 0 |
| `--freeze` | 10 | 前半(バックボーン)を固定。0 で全層学習 |
| `--patience` | 20 | 改善が無ければ打ち切る |
| `--no-cache` | - | 画像をメモリに載せない |
| `--project` / `--name` | `runs_pose` / `train` | ログ |

## 6. `main.py`(動画)/ `realtime.py`(カメラ)— 共通の引数

```bash
python main.py bag.mp4 [--save-video [PATH]] [--no-display]
python realtime.py [カメラ番号] [--no-display] [--stats] [--calib-key c]
```

### 計算機(GPU)
| 引数 | 既定 | 意味 |
|---|---|---|
| `--device` (`--yolo-device`) | `auto` | `auto`(CUDA → mps → CPU)/ `cpu` / `cuda` / `0` / `mps` |
| `--half` / `--no-half` | CUDA のときだけ FP16 | FP16 の切り替え |
| `--no-parallel` | - | バッグ推定と骨格推定を並列にしない |

### バッグ(領域・傾き)
| 引数 | 既定 | 意味 |
|---|---|---|
| `--yolo-weights` | `bag_yolo.pt` | バッグのモデル |
| `--yolo-imgsz` | 320 | 推論サイズ(学習時と揃える) |
| `--yolo-conf` | 0.4 | 信頼度のしきい値 |

### 骨格
| 引数 | 既定 | 意味 |
|---|---|---|
| `--no-pose` | - | 骨格推定を使わない(傾きだけの判定。ヒット・頭・体は出ない) |
| `--pose-weights` | `pose_model.pt` or 標準 | 骨格のモデル |
| `--pose-imgsz` | 640 | 推論サイズ(小さいほど速い) |
| `--pose-conf` | 0.25 | 人の検出の信頼度 |
| `--kpt-conf` | 0.5 | 点を「見えている」とみなす信頼度 |

### ヒットとパンチ
| 引数 | 既定 | 意味 |
|---|---|---|
| `--hand-radius` | - | 手首を中心とする円の半径(画素)。指定が優先 |
| `--hand-radius-ratio` | 0.6 | 未指定のとき、バッグ幅 × この値(0.5 だと fps が低いときに速いパンチを見落としやすい) |
| `--threshold` | なし | **なし = 接触のみでパンチ**。数値(例 8)= 接触 + 傾きのずれが度数以上でパンチ。`--no-contact-gate` では傾きのみ(省略時 8) |
| `--cooldown` | 0.5 | **同じ手**でパンチ判定してから、その手で次を判定するまでの最短間隔(秒)。**左右は別々に数える**ので、左→右→左…の連打は妨げられない |
| `--cooldown-left` / `--cooldown-right` | `--cooldown` と同じ | 左手 / 右手だけ別の間隔にしたいとき |
| `--cooldown-any` | 0(なし) | どちらの手でも、前のパンチから次までの最短間隔。両手が同時に触れて 2 回数えられるのを防ぎたいときだけ上げる |
| `--calib-frames` | 30 | バッグの静止時の傾きを測るフレーム数 |
| `--contact-window` | 0.3 | (`--threshold` 指定時)接触の後、この秒数以内の揺れをパンチとする |
| `--contact-pre` | 0.1 | (`--threshold` 指定時)接触の直前、この秒数以内に始まった揺れも含める |
| `--contact-release` | 0.15 | 手がこの秒数離れてから触れたら新しい接触 |
| `--sway-max` | - | (`--threshold` 指定時)これを超える大きな揺れは対象外(度) |
| `--no-contact-gate` | - | 接触を条件にせず、傾きだけで判定 |

### 頭と体の動き
| 引数 | 既定 | 意味 |
|---|---|---|
| `--no-motion` | - | 計算しない |
| `--forward-direction` | `auto` | 「前」の向き(`left`/`right`)。auto はバッグの位置から |
| `--motion-smooth` | 0.5 | 平滑化(1 でなし) |
| `--motion-calib-frames` | 30 | 自動キャリブレーションのフレーム数(動画・`--no-display`) |

### 毎フレームの出力
| 引数 | 既定 | 意味 |
|---|---|---|
| `--quiet` | - | コンソールに出さない(UDP は続く) |
| `--format` | `text` | `text`(1行)/ `json` |
| `--print-every` | 1 | N フレームに 1 回出す(パンチは必ず出る) |
| `--udp HOST:PORT` | - | 毎フレーム `{forward, head, feet, punch}` の JSON を Unity へ UDP で送る(例 `127.0.0.1:5005`) |
| `--udp-full` | - | UDP にも全項目(手・punch・punches・バッグ・動き量)を送る |

### `main.py` のみ
`--udp` を付けると、送るたびに送った JSON(正規化後の値)を `frame=46 sent: {"forward":0.428,"head":0.737,"feet":0.0,"punch":1}` の形で毎フレーム表示する(`--quiet` で止まる)。

| 引数 | 意味 |
|---|---|
| `video` | 入力動画 |
| `--no-display` | 画面を出さない(高速。最後に ms/frame とパンチ数を表示) |
| `--calib-key` / `--hold-timeout` | 画面のあるとき、動画の中で C キーを押している間、基準を測り直す(`realtime.py` と同じ) |
| `--save-video [PATH]` | 検出結果を重ねた動画を保存 |

### `realtime.py` のみ
| 引数 | 既定 | 意味 |
|---|---|---|
| `camera` | 0 | カメラ番号 |
| `--width` / `--height` / `--fps` | 640 / 480 / 60 | カメラ設定 |
| `--no-mjpg` | - | MJPG を要求しない(映像が乱れるとき) |
| `--no-display` | - | 画面なし(Ctrl+C で停止)。頭・体の基準は起動直後に自動で測る |
| `--stats` | - | 2秒ごとに fps と ms/frame を表示 |
| `--calib-key` | `c` | 押している間、バッグの傾き・頭・体・バッグの位置の基準を測るキー(いつでも何度でも。離した瞬間に新しい基準になる) |
| `--hold-timeout` | 0.7 | キーを離したとみなすまでの秒数(キーの自動リピートが止まってから) |

## 7. よく使う組み合わせ

```bash
# 動作確認(画面あり)
python main.py bag.mp4 --save-video

# 本番: Unity の裏で動かす(画面なし・print なし・UDP のみ・GPU 自動)
python realtime.py --no-display --quiet --udp 127.0.0.1:5005

# print で中身を確認しながら Unity にも送る(JSON、10 フレームに 1 回)
python realtime.py --no-display --format json --print-every 10 --udp 127.0.0.1:5005

# 速度を測る(CPU と GPU を比べる)
python realtime.py --stats --device cpu
python realtime.py --stats --device cuda
```

## 8. 揃えるべき設定

- `train_yolo.py --imgsz` と `main.py/realtime.py --yolo-imgsz`
- `validation.py` の `--imgsz/--conf/--kpt-conf` と、実行時の `--pose-imgsz/--pose-conf/--kpt-conf`
- 骨格の追加学習後は `pose_model.pt` が自動で使われる。使いたくないときは `--pose-weights yolo11n-pose.pt`
