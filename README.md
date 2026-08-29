# OnsenScenarioAnalyzer

解析温泉杯回合信息，并在插件 workspace 中显示训练分析。

## History

成功输出以 `(single_mode_chara_id, turn)` 为键保存在当前进程内；相同键会原位更新，不新增记录。插件重载或进程退出后记录不会保留。

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
