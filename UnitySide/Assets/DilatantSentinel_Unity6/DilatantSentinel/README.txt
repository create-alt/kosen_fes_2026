ダイラタント・センチネル / Dilatant Sentinel

コンセプト
強い衝撃で硬化し、ゆっくりした弱い打撃に弱いゲルを機械で保持する敵。
ダイラタンシーを着想にしたゲーム表現です。流体や粘度の物理シミュレーションは行いません。
青緑色のゲル、黄土色の機械装甲、単眼の頭、圧力タンク、左右に閉じる保護板で構成しています。
前回のPunchBotとは別アセットです。

Unity 6への取り込み
1. ZIPを展開する。
2. DilatantSentinelフォルダを、UnityプロジェクトのAssetsフォルダ内へコピーする。
3. Unityへ戻り、スクリプトの読み込みが終わるまで待つ。
4. Tools > Dilatant Sentinel > Create Enemy Prefab を実行する。
5. Assets/DilatantSentinelGenerated/DilatantSentinel.prefabをHierarchyへドラッグする。
6. Hierarchy上の敵を選択し、Scene欄にマウスを置いてFキーを押すとモデルが見える。
同名の生成先がある場合は、番号を付けた別フォルダへ作成します。

アニメーション
Idle：2.0秒。ゲルがゆっくり脈動する。繰り返し再生。
WeakHit：0.8秒。ゲルが横へ膨らみ縦に潰れ、頭と腕が揺れて元に戻る。
StrongBlock：0.65秒。左右の保護板が中央で閉じ、ゲルが締まり、機械の枠が細かく震えてから開く。
Death：1.3秒。ゲルが薄く潰れ、機械の枠と頭が前へ崩れる。その姿勢で停止。
プレビューGIFは見やすく繰り返しますが、WeakHit/StrongBlock/DeathはUnityではループしません。

操作テスト
1. HierarchyでCreate Emptyを選び、空のGameObjectを作る。
2. 空のGameObjectへDilatantEnemyDemo.csを付ける。
3. InspectorのTargetへ、Hierarchyに置いたDilatantSentinelをドラッグする。
4. Unityを再生し、Game画面をクリックする。
5. キーボード上段の1：弱攻撃、2：強攻撃、R：HPと姿勢をリセット。
左上にHPと操作方法を表示する。Show Controlsをオフにすると表示を消せる。
テスト用入力は敵Prefabに自動では付けません。

初期設定の挙動
最大HP：100
弱攻撃：基礎ダメージ10、衝撃値0.5。2倍の20ダメージを受け、5回で倒れる。
強攻撃：基礎ダメージ50、衝撃値2。硬化してダメージ0。
硬化の境界値：1。境界値以上の衝撃を強攻撃として判定する。
死亡後の攻撃は無視する。RまたはResetEnemy()で復帰できる。
強攻撃で閉じている最中でも、次の弱攻撃は有効。時間制の無敵状態は設けていない。
最大HP、硬化境界、弱攻撃倍率はDilatantEnemyコンポーネントのInspectorで再生前に調整可能。

既存の攻撃処理から呼ぶ
対象の型をDilatantEnemyにし、その敵に対して以下のメソッドを呼ぶ。

target.ReceiveWeakAttack();
target.ReceiveStrongAttack();
target.ResetEnemy();

独自の攻撃値を使う場合
target.ReceivePhysicalAttack(damage, impactStrength);
damageは基礎ダメージ、impactStrengthはゲーム内で決める衝撃の強さ。
衝撃値をRigidbodyなどから自動取得する処理は含まない。
CurrentHealth、MaxHealth、IsDeadをゲーム側から参照可能。
HPや死亡判定は敵自身が管理するため、以前のattackCountによる5回判定は併用しない。

モデル仕様
高さ約1.54m、正面+Z、上+Y、原点は足元。
50個のメッシュ部品、2204三角形、7色のマテリアル。
ゲルは不透明な色と光沢で表現。透明描画や追加シェーダーを要求しない。
メッシュ、マテリアル、4つの.anim、Animator Controller、Prefabはメニュー操作時に生成。
OBJとMTLは他の3Dソフト向けの静的モデル。アニメーションはJSONと生成スクリプトから作る。
当たり判定、Rigidbody、移動AI、敵からの攻撃、効果音、パーティクルは含まない。

確認範囲
Unity 6の参照アセンブリによるC#コンパイルと、ダメージ処理の独立した実行テストを実施。
同じモデル・アニメーションデータからプレビューを描画。
Unity Editor内でのPrefab生成・Play Mode・描画方式ごとの見た目は未確認。
テスト用入力の新Input System分岐はUnity内では未実行。
