# オフラインPCでのセットアップ

ClosedXML は単体では動作しません。推移的依存を含めて **8パッケージ / 8DLL** が必要です。
このフォルダにはその 8 個の `.nupkg`（計約16MB）が入っています。

| パッケージ | 出力されるDLL | サイズ |
|---|---|---:|
| ClosedXML 0.105.1 | ClosedXML.dll | 1.69 MB |
| ClosedXML.Parser 2.0.0 | ClosedXML.Parser.dll | 192 KB |
| DocumentFormat.OpenXml 3.1.1 | DocumentFormat.OpenXml.dll | 6.35 MB |
| DocumentFormat.OpenXml.Framework 3.1.1 | DocumentFormat.OpenXml.Framework.dll | 471 KB |
| SixLabors.Fonts 1.0.0 | SixLabors.Fonts.dll | 1.14 MB |
| System.IO.Packaging 8.0.1 | System.IO.Packaging.dll | 142 KB |
| **RBush.Signed** 4.0.0 | **RBush.dll** | 34 KB |
| ExcelNumberFormat 1.1.0 | ExcelNumberFormat.dll | 30 KB |

注意点:

- **ClosedXML の nupkg には `ClosedXML.dll` しか入っていません。** `ClosedXML.Parser.dll` は別パッケージです。
- **RBush.Signed パッケージが出力するDLL名は `RBush.dll`** でパッケージ名と一致しません。

## 目的別の手順

### A. オフラインPCで「実行するだけ」— NuGet不要

オンラインPCで publish し、出力フォルダごとコピーするだけです。

```
dotnet publish -c Release -o publish-out
```

13ファイル / 約9.8MB。配布先に .NET 8 Desktop Runtime が必要です。
ランタイムも入れられない場合は自己完結publish（約164MB）にします。

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-out
```

`.deps.json` と `.runtimeconfig.json` も必須です。DLLだけ手で集めると起動に失敗します。

### B. オフラインPCで「ビルドもする」

以下のどちらか。両方とも nuget.org を遮断した状態で復元・ビルドが通ることを確認済みです。

**B-1. ローカルフィードとして使う**（このフォルダをプロジェクト直下に置く）

プロジェクトルートに `nuget.config` を作成:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="offline-packages" />
  </packageSources>
</configuration>
```

以後 `dotnet build` がこのフォルダだけを見て解決します。

**B-2. グローバルキャッシュに配置する**（nuget.config 不要）

オンラインPCの `%USERPROFILE%\.nuget\packages` から下記8フォルダを、
オフラインPCの同じ場所へコピーします。

```
closedxml\  closedxml.parser\  documentformat.openxml\
documentformat.openxml.framework\  sixlabors.fonts\
system.io.packaging\  rbush.signed\  excelnumberformat\
```

フィード設定が一切無くても復元・ビルドが通ります。恒久的に使うならこちらが手軽です。

## nupkgの入手元

すべて nuget.org（既定フィード）です。オンラインPCで復元済みなら、
`%USERPROFILE%\.nuget\packages\<パッケージ名>\<バージョン>\*.nupkg` からコピーできます。
