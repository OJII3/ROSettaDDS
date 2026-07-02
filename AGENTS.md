# ROSettaDDS

## Git 戦略

- 変更は細かくコミットすること. コミットメッセージは日本語で conventional コミットに従う.
- `main` ブランチにコミットしない. コミットをする前にブランチを切り、すべてのタスクが終了したら PR を作成すること。

## Unity メタファイル

- Unity で取り込む `src/rosettadds` 配下のファイル・フォルダには `.meta` を必ずコミットすること。
- `.meta` の不足や orphan は `.github/scripts/check_unity_meta.sh` で確認すること。
