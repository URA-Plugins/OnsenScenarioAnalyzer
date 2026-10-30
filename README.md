# OnsenScenarioAnalyzer

解析温泉杯回合信息，并在插件 workspace 中显示训练分析。

## History

成功输出以 `(single_mode_chara_id, turn)` 为键保存在当前进程内；相同键会原位更新，不新增记录。进程退出后记录不会保留。

- `↑`：上一条（更旧）
- `↓`：下一条（较新）
- `←`：最旧一条
- `→`：最新一条

方向键用于 history 导航；正文可使用 `PageUp`、`PageDown`、`Home`、`End` 或鼠标滚轮滚动。

## 配置

配置保存在 `PluginData/OnsenScenarioAnalyzer/settings.json`：

```json
{
  "historyLimit": 100
}
```

`historyLimit` 的默认值为 `100`，有效范围为 `0` 到 `1000`。设为 `0` 时不保存 history，只显示最近一次成功输出；降低上限会立即删除最旧记录，提高上限不会恢复已删除的记录。

## 构建

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\OnsenScenarioAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。

## 本仓测试

`tests/OnsenScenarioAnalyzerSmoke` 默认运行本插件的生命周期、历史配置和显示检查，并检查发布边界。测试工具由本仓 `tests/Support` 持有。

```powershell
dotnet run --project tests/OnsenScenarioAnalyzerSmoke -p:UraTestHostRoot=<Host源码绝对路径> -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```
