PunchBot用 小型ミサイルモデル

形状
・黄土色の機体、赤い先端、白い識別帯、4枚の尾翼、後部ノズル。
・全長0.634m、胴体の直径約0.14m。612三角形。
・+Zが先端方向、+Yが上。原点は胴体内の軸上。
・前回のロボットと同系色。必要ならUnityのScaleで調整可能。

Unity 6への取り込み
1. ZIPを展開し、PunchBotMissileフォルダをプロジェクトのAssets内へコピーする。
2. Unityへ戻り、スクリプトの読み込みが終わるまで待つ。
3. 上部メニュー Tools > PunchBot > Create Missile Prefab を押す。
4. Assets/PunchBotMissileGenerated/PunchBotMissile.prefabをHierarchyへドラッグする。
5. Hierarchy内のミサイルを選択し、Scene欄上でFキーを押すと見やすい位置へ移動する。

内容
・OBJとMTL：他の3Dソフトでも利用できる静的モデル。
・MissileModelData.jsonとEditor内のC#：Unity用のメッシュ、マテリアル、Prefabを生成。
・Missile_preview.png：同じ形状データから描画したプレビュー。

Prefab内の目印
・TipPoint：先端位置。
・ExhaustPoint：噴射口。ローカル+Zは後方へ向いている。
目印はTransformのみ。炎や煙のエフェクトは含まない。

モデルのみの配布です。飛行、追尾、発射、当たり判定、爆発は含みません。
既存のロボット、アニメーション、スクリプトは変更しません。

検証範囲
・頂点と三角形の参照、面積、寸法、プレビューを確認。
・Unity参照アセンブリを用いた生成スクリプトのコンパイルを確認。
・Unity EditorでのPrefab生成・表示は未確認。
