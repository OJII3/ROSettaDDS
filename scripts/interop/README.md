# interop scripts

ROS 2 実装との相互運用を確認するための手動検証スクリプトです。
通常の `dotnet test` には混ぜず、ROS 2 と RMW 実装が入った環境で明示的に実行します。

## Fast DDS

```sh
scripts/interop/fastdds_talker_listener.sh
```

既定では以下を使います。

- `RMW_IMPLEMENTATION=rmw_fastrtps_cpp`
- `ROS_LOCALHOST_ONLY=1`
- `ROS_DOMAIN_ID`: 未指定ならスクリプト内で衝突しにくい値を選択

ログは `artifacts/interop/` に出力します。

Nix devShell では `ros2` CLI が Python extension の動的ライブラリ解決に失敗する環境があるため、
`$AMENT_PREFIX_PATH/lib/demo_nodes_cpp/{talker,listener}` が存在する場合は `ros2 run` を経由せず直接実行します。
