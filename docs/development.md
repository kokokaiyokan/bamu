# 開発・検証

コアテストには.NET 8とPython 3を使います。Unity・BLMのインストールや購入済みアセットは不要です。毎回、合成のBLMデータベース・商品ファイル・台帳を生成し、`../bamu_development/artifacts/core-test-synthetic-<ID>/`内だけをアプリデータの保存先として使用します。Unity APIはテスト専用の代替実装で、EditorPrefsもメモリ内に保持し、実際のUnityプロジェクトへの切り替えは拒否します。

リポジトリ直下で実行します。

```powershell
dotnet run --project tests/CoreTests -- "$PWD"
cd browser-extension
npm.cmd test
```

Unityの実DLLを使ったコンパイル確認は別途`python scripts/compile-unity.py`で行います。この確認にはスクリプトで指定するUnity Editorのインストールが必要です。コアテストの代替APIでは、実Editorの画面操作やライフサイクルの動作までは確認できません。

配布物の作成はリポジトリ直下で`python scripts/build-package.py`を実行します。

| 場所 | 内容 |
|---|---|
| `Packages/io.github.kokokaiyokan.bamu/` | Unity Editor拡張 |
| `browser-extension/` | Chrome拡張ソース |
| `scripts/` | ビルド・コンパイル・既存プロジェクト参照更新 |
| `tests/` | 分離した環境でのコア・通信・移行テスト |
| `docs/` | BOOTH連携手順・モジュールAPI |
| `companion/` | 配布対象外の旧Python実装・過去版検証用 |

配布物・検証データ・テスト出力は、リポジトリの外にある`../bamu_development/artifacts/`へ保存します。各スクリプトは必要な出力フォルダを自動作成します。このフォルダにはローカルの検証データを含むため、公開対象に含めないでください。
