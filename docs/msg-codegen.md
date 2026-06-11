# msg コード生成 (rosidl 相当)

rosettadds は ROS 2 の `.msg` から、CDR 互換な C# 型 (`struct`) と
シリアライザ (`ICdrSerializer<T>`) のペアを生成する仕組みを持つ。
IL2CPP / AOT 互換のため**すべてコンパイル時生成**で、ランタイム生成は行わない。

生成ロジックは依存ゼロの netstandard2.0 ライブラリ `ROSettaDDS.MsgGen` に集約され、
以下 2 つのフロントエンドから利用できる。

| フロントエンド | プロジェクト | 用途 |
| --- | --- | --- |
| CLI | `tools/rosettadds-genmsg` | 標準 msg の保守、Unity/任意プロジェクトへの生成 |
| Source Generator | `src/ROSettaDDS.SourceGenerator` | .NET プロジェクトでのコンパイル時透過生成 |

## 対応する .msg 文法

- コメント (`#` 以降)、空行
- プリミティブ: `bool` `byte` `char` `int8`〜`int64` `uint8`〜`uint64` `float32` `float64` `string`
  - C# 型対応は既存手書き型に一致 (`char`/`int8`→`sbyte`, `byte`/`uint8`→`byte`)
  - `wstring` は CDR 上 UTF-16 で string (UTF-8) と wire が異なるため**未対応** (解析時にエラー)
- bounded string: `string<=N` (wire は unbounded と同形、上限は注記のみ)
- ネスト型: `pkg/Type` または同一パッケージ相対 `Type`
- 配列: 固定長 `T[N]` / 可変長 `T[]` / 上限付き `T[<=N]`
- 定数: `TYPE NAME=value`
- デフォルト値: フィールド名の後ろに値 (文字列はクォート)

## 命名ポリシー (案 B / `.claude/CLAUDE.md`)

`ROSettaDDS.MsgGen.TypeMapping.TypeNameResolver` が決定する。

- BCL と衝突する std_msgs のスカラ型は `Message` サフィックス付き
  (`String`→`StringMessage`, `Int32`→`Int32Message`, `Empty`→`EmptyMessage` …)
- 衝突しない型はサフィックスなし (`Header`, `ColorRGBA`→`ColorRgba`, `MultiArrayLayout` …)
- 名前空間は `_msgs` を畳む: `std_msgs`→`ROSettaDDS.Msgs.Std`、
  `geometry_msgs`→`ROSettaDDS.Msgs.Geometry`、`builtin_interfaces`→`ROSettaDDS.Msgs.BuiltinInterfaces`
- フィールドは snake_case→PascalCase、コンストラクタ引数は camelCase
- 頭字語の連続は正規化 (`ColorRGBA`→`ColorRgba`)
- シリアライザは型名 + `Serializer`

## 1. CLI で生成する

`<input>/<package>/msg/<Name>.msg` レイアウトを走査し、
`<output>/<SubNamespace>/<TypeName>.cs` へ出力する。

```sh
# このリポジトリの標準 msg を再生成する
dotnet run --project tools/rosettadds-genmsg -- --input msgs --output src/rosettadds/Msgs

# 差分チェックのみ (CI 用、ドリフトがあれば非ゼロ終了)
dotnet run --project tools/rosettadds-genmsg -- --input msgs --output src/rosettadds/Msgs --check
```

`src/rosettadds/Msgs/` の標準型はこの CLI の生成物であり、入力は `msgs/` にある。
`.msg` を変更したら再生成してコミットする。生成物がコミット済みと一致することは
`GeneratedCodeDriftTests` (`dotnet test`) が保証する。

## 2. Source Generator で生成する (.NET プロジェクト)

`.msg` を `AdditionalFiles` に登録すると、コンパイル時に透過生成される
(生成物のコミットは不要)。実例は `samples/CustomMsgGen`。

```xml
<ItemGroup>
  <ProjectReference Include="path/to/src/rosettadds/rosettadds.csproj" />
  <ProjectReference Include="path/to/src/ROSettaDDS.SourceGenerator/ROSettaDDS.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>

<ItemGroup>
  <AdditionalFiles Include="msgs\**\*.msg" ROSettaDDSMsgPackage="sample_msgs" />
  <!-- パッケージ名メタデータを生成器へ渡すための宣言 -->
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="ROSettaDDSMsgPackage" />
</ItemGroup>
```

パッケージ名は `ROSettaDDSMsgPackage` メタデータを優先し、無ければパス
(`.../<package>/msg/<Name>.msg`) から推定する。

## 3. Unity プロジェクトで使う (CLI ワークフロー)

Unity はパッケージ外のファイルをコンパイルせず、ビルド時に MSBuild の
カスタムターゲットも実行しない。そのため Unity では **CLI で Assets 配下へ
生成 → Unity が通常どおりコンパイル**する方式を推奨する。

1. Unity プロジェクト側に `.msg` を置く (任意レイアウト)。例:
   `Assets/Msgs/my_robot_msgs/msg/Status.msg`
2. .NET SDK が入った環境で CLI を実行し、Assets 配下へ `.cs` を生成する。

   ```sh
   dotnet run --project /path/to/rosettadds/tools/rosettadds-genmsg -- \
     --input  Assets/Msgs \
     --output Assets/Scripts/GeneratedMsgs
   ```

3. Unity に戻ると生成 `.cs` がインポートされコンパイルされる。
   生成型は rosettadds パッケージの `ROSettaDDS.Cdr` / 既存 `ROSettaDDS.Msgs.*` を参照する。

生成 `.cs` は Assets 配下に置かれるため `.meta` は Unity が自動付与する。
標準 msg (`std_msgs` 等) は rosettadds パッケージに同梱済みなので、
ユーザーが生成するのは独自パッケージの型のみでよい。

> 補足: `.msg` 変更のたびに手動実行する代わりに、CI やコミットフックで
> `--check` を回してドリフトを検出できる。

## 仕組み (内部)

```
ROSettaDDS.MsgGen (netstandard2.0, 依存ゼロ)
  Parsing/MsgParser        .msg → MessageDefinition
  Model/                   型モデル (Field/Constant/FieldType …)
  TypeMapping/             ROS↔C#/CDR の対応表・命名・型名解決
  Emitting/CSharpEmitter   モデル → C# ソース文字列

tools/rosettadds-genmsg          Core を呼ぶ CLI (ファイル入出力)
src/ROSettaDDS.SourceGenerator   Core をソースリンクで取り込む IIncrementalGenerator
```

生成される `Serialize`/`Deserialize`/`GetSerializedSize` は既存手書き型と
CDR バイト列が一致する。回帰は `tests/rosettadds.Tests/Msgs/*`
(bit-exact + roundtrip) と `MsgGen/GeneratedCodeDriftTests` が担保する。
