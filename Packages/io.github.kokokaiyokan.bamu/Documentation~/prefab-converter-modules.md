# prefab変換ツールの任意モジュール

本体はツール固有の内部APIを参照せず、`bamu.IPrefabConverter`を実装するモジュールを表示します。モジュールなしでも本体のprefab表示・選択・アバターへの配置・手動リンクは動作します。

## モジュールの構成

`Packages/io.github.kokokaiyokan.bamu/LocalModules/<モジュール名>/Editor/`へコードとasmdefを置きます。asmdefはEditor限定とし、`bamu.Editor`を参照します。LocalModules全体とその.metaは.gitignore対象で、標準のZIP／TGZからも除外します。任意の別パッケージから同じ公開APIを使うことも可能です。

コードは`[InitializeOnLoad]`などで`PrefabConverterRegistry.Register(instance)`を呼びます。

| API | 内容 |
|---|---|
| `IPrefabConverter.Id` | 重複しない固定ID |
| `DisplayName` | 変換ボタンとリンク履歴の表示名 |
| `Available` | 対象ツールが利用できるか |
| `HasPending` | 変換結果の待機中か |
| `Notice` | 利用者へ表示する状態文 |
| `Open(Product, GameObject)` | 商品とProjectのprefabを受け取る |
| `Cancel()` | モジュール側の待機を解除する |
| `PrefabConverterRegistry.Unregister(instance)` | 同一インスタンスの登録を解除する |

同じIDで別インスタンスを登録すると置き換わります。古いインスタンスのUnregisterで新しいインスタンスを消すことはありません。登録・解除はUnityのメインスレッドで行ってください。引き渡し後の処理・監視・状態保存はモジュールが担当します。

## 変換結果のリンク

本体の`PrefabConversionLinks`はツールに依存しない補助APIです。

- `IsPrefab(path)`：ProjectのAssets内にある.prefabかを確認する。
- `IsNewResult(sourceGuid, inputGuid, previous, result)`：入力GUIDと出力パスを比較する。
- `LinkResult(ledger, product, sourceGuid, result, resultGuid, method)`：改変用コピーの正確なリンクと使用するprefabの選択へ追加する。

モジュールは処理の開始・完了と対応する入力を確認し、出力アセットが読み込めてから利用します。LinkResult自体は台帳を書き込みません。`BridgeStorage.Load()`で最新を読み、LinkResultの後に`BridgeStorage.Save(ledger)`で保存し、`BridgeWindow.RefreshOpenWindows()`で表示を更新します。

モジュールを外しても保存済みのリンクやprefab選択は削除しません。表示名が登録されていない過去のリンクはmethodの識別子を表示します。

## 検証・配布

`python scripts/compile-unity.py`は本体のみをコンパイルします。`--with-local-modules`を付けると、ローカルモジュールを本体とは別アセンブリとして追加コンパイルします。コアテストは特定ツールの連携コードを必要としません。

`.gitignore`は過去のコミットを書き換えません。履歴に含めたくない実装がある場合は、現在の追跡状態に加えて既存コミットも別途確認してください。
