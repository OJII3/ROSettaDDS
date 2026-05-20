# rclsharp

## Git 戦略

- 変更は細かくコミットすること. コミットメッセージは日本語で conventional コミットに従う.
- `main` ブランチにコミットしない. コミットをする前にブランチを切り、すべてのタスクが終了したら PR を作成すること。

## msg 型の命名方針 (案 B)

`Rclsharp.Msgs.*` 配下の msg 型は以下のルールに従う:

- **BCL 型と衝突する型のみ `Message` サフィックスを付ける**
  例: `StringMessage`, `BoolMessage`, `Int32Message`, `Float32Message`, `ByteMessage`, `CharMessage`, `EmptyMessage` など
- **衝突しない型はサフィックスなし** (ROS 2 名に近づける)
  例: `Header`, `ColorRgba`, `Time`, `Duration`, `MultiArrayLayout`, `MultiArrayDimension`, `ByteMultiArray`, `Float32MultiArray` など
- 今後追加する `geometry_msgs/Vector3, Point, Quaternion, Pose, Twist, Transform` などはサフィックスなし
- Serializer クラスは型名に `Serializer` を付ける (`HeaderSerializer`, `ColorRgbaSerializer`, `StringMessageSerializer`)
