# BOOTH Asset Management Utility

BOOTH・BOOTH Library Manager（BLM）・Unityをつなぐ、非公式のアセット管理ツール。for VRChat。

普段の名称は **BOOTH Asset Management Utility**、省略が必要な場所では **BAMU**、内部識別子は`bamu`です。BOOTH・pixiv・VRChatの公式ツールではありません。

## 主な機能

- 商品単位でBLM保存状態とProject内のAssetリンクを表示。
- インポート時の自動リンクと、導入済みアセットのGUID・内容・パス照合。
- タグ・採用辞書・商品画像のキャッシュ、検索・並べ替え。
- 代表prefabの選択とアバターへの配置。変換ツール連携は任意モジュールとして追加可能。
- Chrome拡張からBOOTHの商品一覧を取り込み、BLM未登録のVRChat対象商品を手動で一括取得。

更新の自動ダウンロードは行いません。Unityをすべて閉じている間は連携の受信・保存確認を休止します。

## 導入

Windows / Unity 2022.3 / BOOTH Library Manager 0.5.0（DBスキーマ20261005）向け。

1. UnityのPackage Managerで「Add package from disk...」から[package.json](Packages/io.github.kokokaiyokan.bamu/package.json)を指定します。配布用TGZも利用できます。
2. `Window > BOOTH Asset Management Utility`を開きます。
3. 上部の「設定」→「接続設定…」からBLM・Chrome連携を設定します。

詳細は[利用ガイド](Packages/io.github.kokokaiyokan.bamu/README.md)を参照してください。

## 0.1.0の名称変更

旧BOOTH Unity Bridgeから名称を変更しました。UnityパッケージIDは`io.github.kokokaiyokan.bamu`、名前空間は`bamu`、アセンブリは`bamu.Editor`です。旧データは新保存先にデータがない場合に引き継ぎ、元データを残します。

## 開発・検証

```powershell
python scripts/compile-unity.py
dotnet run --project tests/CoreTests -- "$PWD"
cd browser-extension
npm.cmd test
```

配布物の作成はリポジトリ直下で`python scripts/build-package.py`を実行します。

| 場所 | 内容 |
|---|---|
| `Packages/io.github.kokokaiyokan.bamu/` | Unity Editor拡張 |
| `browser-extension/` | Chrome拡張ソース |
| `scripts/` | ビルド・コンパイル・既存プロジェクト参照更新 |
| `tests/` | 分離した環境でのコア・通信・移行テスト |
| `docs/` | BOOTH連携手順・モジュールAPI |
| `companion/` | 配布対象外の旧Python実装・過去版検証用 |
| `artifacts/` | Git対象外の配布物・検証データ |

## 任意の変換ツール連携（0.1.0）

変換ツール固有の処理は本体から分離しました。本体は公開インターフェース`IPrefabConverter`と`PrefabConverterRegistry`を提供し、モジュールがなければ変換ボタンを表示しません。prefabの表示・選択・配置・リンクは本体だけで利用できます。

