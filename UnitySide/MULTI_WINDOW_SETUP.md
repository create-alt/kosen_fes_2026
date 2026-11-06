# Windows版の3ウィンドウ表示

`StandaloneDisplayWindows` はWindows用exeで動作します。Unityエディター内では追加ウィンドウを開きません。

## Unityで行う設定

1. 戦闘シーンに空のGameObjectを作り、名前を `StandaloneDisplayWindows` にします。
2. 同名のスクリプトをAdd Componentで追加します。シーン内に1個だけ置いてください。
3. Main CameraのTarget Displayは `Display 1`、Sub Cameraは `Display 4` にします。既に設定されていれば変更不要です。
4. Build ProfilesでWindowsを選び、戦闘シーンをビルド対象に含めます。
5. Player SettingsでResizable Windowをオンにし、ウィンドウ表示でビルドしてください。初期解像度はスクリプト側でも指定します。
6. 作成したexeを起動します。配布する場合はexeだけでなく、同時に生成されたDataフォルダやDLLなども一緒に渡してください。

## 起動時の構成

| ウィンドウ | 内容 | 操作 |
| --- | --- | --- |
| ゲーム本体 | Display 1 | メニュー・キーボード操作を受け付ける |
| Display 1 (Mirror) | 本体と同じ映像。画面UIも含む | 表示専用 |
| Display 4 | Sub Cameraの映像 | 表示専用 |

ゲームは1つだけ実行します。UDP受信や戦闘処理も1つだけです。追加画面をクリックしてからキーボード操作する場合は、本体をクリックし直してください。UDP入力は本体が前面にない間も受け付けます。

追加ウィンドウは移動・サイズ変更できます。縦横比は保ち、余った部分は黒帯になります。追加ウィンドウの閉じるボタンはその画面だけを閉じます。再表示はexeを再起動してください。本体を終了すると追加画面も終了します。

## Inspector

- `Open Extra Windows`: 追加画面を有効にする。
- `Window Width / Window Height`: 本体の初期描画サイズと追加ウィンドウの初期サイズ。初期値960×540。Display 4の描画サイズもこの値で固定。
- `Mirror Frame Rate`: 追加画面の更新上限。初期値30。本体のフレームレートは制限しません。映像転送による遅延と処理負荷が生じます。
- `Flip Display 1 Vertically / Flip Display 4 Vertically`: GPU環境により映像が上下逆になった場合に、その画面だけオンにする。

Display 4のカメラは実行時だけRenderTextureへ出力先を変更します。既存のDisplay 4用Overlay Canvasは実行開始時にカメラ描画へ切り替え、終了時に元へ戻します。実行後に新しく生成するDisplay 4用Canvasや、シーン切り替えへの追従は対象外です。Left/Right Cameraの出力設定は変更しません。

この方式にはAsync GPU Readback対応GPUが必要です。初回はWindowsのGraphics APIをDirect3D11にして確認してください。モニターが1台でも3ウィンドウを表示できます。追加のネイティブプラグインや別プロセスは使いません。

コードのコンパイル確認と、exeでの画面表示確認は別です。実機でのウィンドウ表示・映像の向き・遅延は未確認です。
