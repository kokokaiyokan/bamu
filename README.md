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

利用前に、[利用上の注意](docs/usage-notes.md)を確認してください。非公式ツールであること、バックアップ、外部サービス・商品の利用条件、通信・保存内容、無保証・責任制限について説明しています。

## ライセンス

Copyright (c) 2026 kokokaiyokan。BAMUは[MITライセンス](LICENSE)で公開しています。コピーや再配布時には、著作権表示とライセンス本文を同梱してください。

BOOTHの商品・購入済みアセットや外部ツールには、それぞれの利用条件が適用されます。

