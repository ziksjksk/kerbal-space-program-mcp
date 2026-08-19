# Kerbal Space Program MCP

这是一个面向 Kerbal Space Program 1.x 的完整 MCP 接入工程。它由两部分组成：

1. `ksp-plugin/`：安装到 KSP `GameData` 后运行在游戏内部的 C# 插件。插件在 Unity 主线程执行编辑器和飞行操作，并在本机回环地址提供 HTTP 桥接。
2. `server/`：标准输入输出（stdio）MCP 服务端。MCP 客户端只需要启动它，它会把工具调用转发给游戏插件。

建造链路支持从空白编辑器开始创建火箭，也支持一次性提交完整的部件树；粒度较细的工具可以继续增删零件、移动/旋转、重新连接、设置阶段和动作组。飞行链路支持发射后的状态读取、油门和姿态控制、SAS/RCS、分级、时间加速、单部件动作、紧急中止和回收。

当前目标平台是 KSP 1.12.x（KSP 1.x 的 `Assembly-CSharp.dll` API）。KSP 2 使用另一套 API，不能直接使用这个插件。

## 目录

```text
server/                 Python MCP stdio 服务端（仅使用标准库）
tests/                  不需要启动 KSP 的协议和数据模型测试
ksp-plugin/src/         KSP 游戏侧 C# 插件源码
ksp-plugin/GameData/    可直接复制到 KSP 根目录的插件目录和配置
examples/               MCP 客户端配置示例
```

## 安装

### 1. 安装游戏插件

在 KSP 根目录执行：

```powershell
Copy-Item -Recurse -Force .\ksp-plugin\GameData\KspMcp .\GameData\KspMcp
```

如果还没有 DLL，需要先设置 KSP 根目录并编译：

```powershell
$env:KSP_ROOT = 'D:\Games\Kerbal Space Program'
powershell -ExecutionPolicy Bypass -File .\ksp-plugin\build.ps1
```

构建脚本会引用游戏自己的 `KSP_x64_Data\Managed\Assembly-CSharp.dll` 和 Unity 程序集，把 DLL 输出到 `ksp-plugin\GameData\KspMcp\Plugins\KspMcpBridge.dll`。如果 KSP 使用的是 32 位目录，脚本会自动寻找 `KSP_Data\Managed`。

启动游戏后，插件会在 `127.0.0.1:8765` 监听。可以先用下面的命令检查桥接是否起来：

```powershell
Invoke-RestMethod http://127.0.0.1:8765/api/v1/status | ConvertTo-Json -Depth 8
```

如果要改端口或加令牌，编辑：

```text
GameData/KspMcp/PluginData/config.cfg
```

然后让 MCP 服务端使用同一个 `KSP_MCP_URL` 和 `KSP_MCP_TOKEN`。

### 2. 启动 MCP 服务端

服务端只依赖 Python 3.10+ 标准库：

```powershell
$env:KSP_MCP_URL = 'http://127.0.0.1:8765'
python -m server
```

如果 MCP 客户端从其他工作目录启动，请把项目绝对路径放入 `PYTHONPATH`，或者在配置里把 `cwd` 设置为本项目根目录。示例配置见 `examples/mcp.json`。

## 推荐的建造流程

对模型来说，最稳定的操作顺序是：

1. 调用 `ksp_status` 确认已经进入 VAB/SPH。
2. 调用 `ksp_parts_list` 查看当前游戏实例实际加载的零件名称和连接节点。
3. 调用 `ksp_editor_new` 清空编辑器。
4. 用 `ksp_editor_apply_craft` 一次提交完整的部件树，或者反复调用 `ksp_editor_add_part` 增量建造。
5. 用 `ksp_editor_validate` 检查控制核心、发动机、连接关系、阶段和成本。
6. 用 `ksp_editor_save` 保存 `.craft` 文件。
7. 用户明确允许后，才调用 `ksp_editor_launch`。

VAB 中插件会把生成的根零件自动放到安全高度（默认 `y=50`），避免高大的火箭穿过编辑器地板。`ksp_editor_load` 是异步的；插件会等待 KSP 的部件树稳定后再恢复保存的阶段号并重新检查根部位置。调用方仍应在加载后再次调用 `ksp_editor_get_craft` 和 `ksp_editor_validate`，确认部件数量、发动机和连接关系。

### 分级和游戏规则检查

`ksp_editor_validate` 不只检查“有没有一个控制舱”，还会返回 `summary.stage_summary`，逐级列出部件数、发动机数、控制模块数和分离器数。当前规则是：

- 有效飞行器至少要有一个可控制的 `ModuleCommand`（例如 Mk1 指令舱）；每个油箱、适配器和发动机不需要各自安装控制舱。
- 每个在分离后仍要继续工作的级，必须在同一分级链中有发动机和相容的推进剂；否则验证器会报错。
- `stage 0` 可以作为最终载荷/分离动作，因此允许没有发动机；但 `stage > 0` 如果含有分离器却没有后续发动机，会被拒绝。
- KSP 原生对舱体、适配器和油箱等被动零件使用 `inverseStage=-1` 是正常状态，不会被误判成非法分级；真正带发动机或分离动作的零件仍必须有有效阶段号。

一个经过真实 KSP 1.12.x 烟测的两级大型火箭采用：Mk1 指令舱、上级 Mainsail、下级 Mammoth、两组燃料箱和两个分离器。加载保存的 `.craft` 后，KSP 会在发射台确认控制核心，MCP 可读到 `commandable=true`；实测下级点火、级间分离和上级 Mainsail 接管均成功。

完整部件格式如下：

```json
{
  "name": "MCP Test Rocket",
  "description": "Built by MCP",
  "editor_mode": "VAB",
  "parts": [
    {
      "id": "pod",
      "part": "mk1pod.v2",
      "position": [0, 0, 0],
      "rotation": [0, 0, 0, 1],
      "stage": 0
    },
    {
      "id": "tank",
      "part": "fuelTankSmall",
      "parent_id": "pod",
      "parent_attach_node": "bottom",
      "attach_node": "top",
      "snap_to_node": true,
      "stage": 1
    }
  ]
}
```

`position` 是 KSP 世界坐标，`rotation` 是 Unity 四元数 `[x, y, z, w]`。部件树中的 `parent_attach_node` 和 `attach_node` 必须是实际零件配置里的节点名称；先调用 `ksp_parts_list` 可以直接查看它们。默认 `snap_to_node=true`，插件会把子零件的节点对齐到父零件节点；要保留自定义的世界坐标/姿态时才设为 `false`。对于表面连接，仍然使用同一字段，只需选择对应的 `srfAttach` 节点。一个可直接提交的三件套火箭见 `examples/minimal_rocket.json`。

## 重要边界

- 游戏侧操作严格在 KSP 主线程执行，HTTP 线程只负责接收请求，避免从后台线程触碰 Unity 对象。
- 默认只监听回环地址，不把游戏控制端口暴露到局域网。
- `ksp_editor_launch` 要求 `confirm: true`，防止模型在校验前误发射。
- 工具不会替模型猜测不存在的零件名称；零件名、节点名和已解锁状态来自当前运行的游戏实例。
- 本地没有 KSP 安装时，可以运行全部 Python 测试，但无法在这里替你启动真实游戏验证 Unity 行为；编译和游戏内烟测需要在安装了 KSP 的机器上完成。

## 验证

```powershell
python -m unittest discover -s tests -v
python -m server --self-test
```

`--self-test` 只检查 MCP 初始化、工具列表和参数路由，不要求 KSP 在线。

## API 依据

插件使用 KSP 1.x 的 `EditorLogic`、`ShipConstruct`、`Part`、`AttachNode`、`Vessel` 和 `FlightCtrlState` 接口。KSP API 的公开文档可参考 [KSPDocsSite](https://kspmoddinglibs.github.io/KSPDocsSite/) 以及 [XML Documentation for the KSP API](https://anatid.github.io/XML-Documentation-for-the-KSP-API/)。

