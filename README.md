# Kerbal Space Program MCP

这是一个面向 Kerbal Space Program 1.x 的完整 MCP 接入工程。它由两部分组成：

1. `ksp-plugin/`：安装到 KSP `GameData` 后运行在游戏内部的 C# 插件。插件在 Unity 主线程执行编辑器和飞行操作，并在本机回环地址提供 HTTP 桥接。
2. `server/`：标准输入输出（stdio）MCP 服务端。MCP 客户端只需要启动它，它会把工具调用转发给游戏插件。

建造链路支持从空白编辑器开始创建火箭，也支持一次性提交完整的部件树；粒度较细的工具可以继续增删零件、移动/旋转、重新连接、设置阶段和动作组。飞行链路支持发射后的状态读取、油门和姿态控制、SAS/RCS、分级、时间加速、单部件动作、紧急中止和回收。

0.2.6 在 0.2.5 的低延迟 HTTP 复用、紧凑遥测和事件游标、单往返批处理、分帧异步建造、真实零件性能分析、星体模型、原生轨道节点和有限燃烧控制之上，进一步提高无视觉实时性：建造期间跳过重复部件表扫描、资源/发动机摘要低频缓存、`ksp_watch` 有界采样，并保留默认 20 Hz 的位置和事件遥测；同时扩大事件缓冲、报告丢失游标、提高 watch 的事件窗口，新增发动机状态/点火、升空、着陆和失去控制能力事件，并在发射前强制执行 TWR/质心/推力几何预检。无视觉模型可以通过事件游标观察“哪个零件刚生成、何时点火、何时分级、何时升空、当前是否进入燃烧/着陆阶段”，用户仍能在 KSP 中看到部件逐帧出现。

当前目标平台是 KSP 1.12.x（KSP 1.x 的 `Assembly-CSharp.dll` API）。KSP 2 使用另一套 API，不能直接使用这个插件。

## 目录

```text
server/                 Python MCP stdio 服务端（仅使用标准库）
tests/                  不需要启动 KSP 的协议和数据模型测试
ksp-plugin/src/         KSP 游戏侧 C# 插件源码
ksp-plugin/GameData/    可直接复制到 KSP 根目录的插件目录和配置
examples/               MCP 客户端配置示例
releases/               可直接下载的完整安装包
```

## 安装

### 1. 安装游戏插件

在 KSP 根目录执行：

```powershell
Copy-Item -Recurse -Force .\ksp-plugin\GameData\KspMcp .\GameData\KspMcp
```

如果要从发布包安装，可以先正常退出 KSP，然后运行包根目录的安装脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -KspRoot 'D:\Games\Kerbal Space Program'
```

脚本会拒绝在 KSP 仍运行时覆盖 DLL，避免游戏继续使用旧版本插件。开发者如果还没有 DLL，需要先设置 KSP 根目录并编译：

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
4. 用 `ksp_editor_apply_craft` 提交完整部件树。MCP 默认使用分帧 live 模式，立即返回 `job_id`，默认每个 Unity 帧生成 8 个部件；需要更明显的逐件展示时传 `parts_per_frame=1`，需要更快完成时可以提高到 12 或 16。每个部件会通过 `editor.build.part_added` 事件进入实时事件游标。
5. 用 `ksp_editor_job_status` 和 `ksp_realtime_state` 读取 `completed/total`、事件和当前部件数，直到任务进入 `completed`。
6. 调用 `ksp_editor_analyze` 读取真实零件质量、推力、TWR、近似 Δv、质心/推力中心和分级风险。
7. 用 `ksp_editor_validate` 检查控制核心、发动机、连接关系、阶段和成本，再用 `ksp_editor_save` 保存 `.craft` 文件。
8. 用户明确允许后，才调用 `ksp_editor_launch`。

如果必须兼容旧的同步调用方，可以传 `wait_for_completion=true`；对于模型调用，推荐保留默认 live 模式，并通过事件游标观察过程。

VAB 中插件会把生成的根零件自动放到安全高度（默认 `y=50`），避免高大的火箭穿过编辑器地板。`ksp_editor_load` 是异步的；插件会等待 KSP 的部件树稳定后再恢复保存的阶段号并重新检查根部位置。调用方仍应在加载后再次调用 `ksp_editor_get_craft` 和 `ksp_editor_validate`，确认部件数量、发动机和连接关系。

### 无视觉实时状态

`ksp_realtime_state` 返回缓存的紧凑状态，避免每次读取都序列化完整部件树。它包含场景、建造任务、当前飞船、位置/速度、海拔、地形高度、垂直速度、质量、级号、姿态、轨道根数和 MCP 控制租约；`events` 使用单调递增的 `event_cursor`，模型可以把上次游标传入 `since`，只接收增量事件。

`ksp_watch` 会在一个有界时间段内连续采样这些状态，适合无视觉模型观察“部件逐个出现、加载稳定、发射、点火、分级、远点/近点变化和着陆”。为避免一次 MCP 响应积累几千个样本拖慢模型，`ksp_watch` 默认最多返回 120 个样本，也可传 `max_samples`（1–240）调整；`event_limit` 默认 256，用于覆盖高速分帧建造的一整个轮询窗口。遥测还会返回 `oldest_event_cursor` 和 `events_lost`，如果客户端轮询太慢，模型可以发现事件游标已经落后，而不是把不完整历史误认为完整过程。`ksp_batch` 把多个安全命令放在一次 HTTP 往返里；发射、Abort 和回收仍必须使用各自的确认工具，不能隐藏在批处理中。

`ksp_realtime_state` 的摘要是轻量的：编辑器只返回当前部件数、任务进度和事件；飞行侧返回位置/速度、轨道根数、阶段、指导阶段、发动机点火/故障汇总和资源总量，不遍历完整部件树。默认遥测间隔为 50 ms，可在 `GameData/KspMcp/PluginData/config.cfg` 用 `telemetryIntervalMs` 调整（25–1000）；要检查连接节点、模块、资源和详细验证结果时再调用 `ksp_editor_get_craft`、`ksp_editor_validate` 或 `ksp_editor_analyze`。如果需要排查游戏侧问题，可以临时设置 `verboseLogging = true`，正常使用应保持 `false`。

示例观察循环：

```text
ksp_editor_apply_craft(...)
  -> ksp_editor_job_status(job_id)
  -> ksp_realtime_state(since=event_cursor)
  -> ksp_editor_analyze()
  -> ksp_editor_validate()
```

### 星际转移与原生轨道节点

飞行工具现在可以直接读取 KSP 当前星体和 patched-conic 数据：

- `ksp_flight_bodies` 返回半径、引力参数、大气高度、影响球和星体轨道；
- `ksp_flight_transfer_plan` 使用当前飞船所在星体和目标星体，计算透明的圆轨道、共面 Hohmann 转移估算，返回出发相位角、转移时间、出发/捕获 Δv 和警告；
- `ksp_flight_maneuver_nodes` 读取游戏原生节点；
- `ksp_flight_add_maneuver_node` 在明确 `confirm=true` 后创建节点；
- `ksp_flight_clear_maneuver_nodes` 在明确 `confirm=true` 后清除节点。
- `ksp_flight_maneuver_burn_start` 在明确 `confirm=true` 后按原生节点执行一个有限燃烧控制计划，实时报告 `coast_to_node_burn`、`aligning_for_node_burn`、`burning_node` 和 `burn_complete` 阶段。

例如，模型可以先调用 `ksp_flight_transfer_plan(destination_body="Duna")`，核对相位角和推进剂余量，再根据用户确认调用节点工具，最后调用 `ksp_flight_maneuver_burn_start`。节点的 Δv 坐标使用 KSP 原生约定：`radial` 为径向外侧正、`normal` 为法向正、`prograde` 为顺行正，单位是 m/s。燃烧控制会根据节点 Δv、实时质量和可用推力估算对称点火时刻，并在游戏帧内对准燃烧向量；它仍然需要实时监控，不会把近似的有限燃烧误报成任务保证。规划器明确忽略了非共面修正、发射/转向损失、大气阻力、真实相位误差和目标星体地形。

`ksp_flight_guidance_start(profile="orbit")` 会先完成上升，在远点等待并抬升近点，或在近点执行降轨修正，直到目标远点/近点容差内；`profile="landing"` 会先尝试把正近点降到与星体相交，再使用相对地表速度、制动距离和局部重力控制下降。两个 profile 仍属于可停止的基础闭环，用户应持续读取遥测，在进入 Duna 转移、捕获、再入和着陆前逐段确认。

### 分级和游戏规则检查

`ksp_editor_validate` 不只检查“有没有一个控制舱”，还会返回 `summary.stage_summary`，逐级列出部件数、发动机数、控制模块数和分离器数。当前规则是：

- 有效飞行器至少要有一个可控制的 `ModuleCommand`（例如 Mk1 指令舱）；每个油箱、适配器和发动机不需要各自安装控制舱。
- 每个在分离后仍要继续工作的级，必须在同一分级链中有发动机和相容的推进剂；否则验证器会报错。
- `stage 0` 可以作为最终载荷/分离动作，因此允许没有发动机；但 `stage > 0` 如果含有分离器却没有后续发动机，会被拒绝。
- KSP 原生对舱体、适配器和油箱等被动零件使用 `inverseStage=-1` 是正常状态，不会被误判成非法分级；真正带发动机或分离动作的零件仍必须有有效阶段号。

推荐的两级大型火箭参考结构采用：Mk1 指令舱、上级 Mainsail、下级 Mammoth、两组燃料箱和两个分离器。重新安装 0.2.6 后应先通过 `ksp_editor_validate`、`ksp_editor_analyze` 和 `ksp_realtime_state` 做游戏内烟测；当前工作区的运行实例仍是旧版桥，因此这里不把尚未重新验证的游戏内点火/分离结果写成已实测事实。

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

### 结构和性能分析

`ksp_editor_analyze` 不猜测零件名称，而是读取当前 KSP 实例的实际 `Part`、资源密度、发动机大气曲线和分级。它会返回：

- 总质量、推进剂质量、发动机总推力、海平面/真空 TWR、加权 Isp；
- 按 `inverseStage` 分组的发动机、质量、剩余质量、TWR 和近似 Δv；
- 质心、推力中心和“质心是否位于推力中心上方”的几何检查；
- 不能起飞、可能严重下沉或容易翻滚等错误/警告。

Δv 是工程估算，不是完整的飞行仿真：它明确排除了阻力、转向损失、节流曲线、跨级供料变化和大气变化。真正发射前仍要同时通过 `ksp_editor_analyze` 和 `ksp_editor_validate`，并用遥测观察实际 TWR、垂直速度、燃料和级号。

### 实时飞行指导

新增的 `ksp_flight_guidance_start` 在游戏帧内运行闭环指导，支持 `ascent`、`orbit`、`landing` 和 `node_burn` 四种 profile；`ksp_flight_guidance_stop` 立即释放控制，`ksp_flight_guidance_status` 返回当前阶段、目标、控制输出和剩余时间。启动必须传 `confirm=true`，默认允许自动分级，但不会绕过发射工具的确认门槛。

当前指导器的职责是提供可观测、可停止的基础闭环：上升阶段按海拔执行重力转弯并以目标远点收油，轨道阶段按远点/近点和原生轨道遥测执行圆化修正，节点燃烧阶段根据原生节点向量进行对准和有限燃烧，着陆阶段先把正近点降到与星体相交，再按相对地表反向速度、制动距离和垂直速度控制下降。它不是全任务级别的“保证成功”黑盒；去 Duna 的转移窗口精确求解、跨影响球捕获、再入热/气动控制和地形避障仍需要模型逐段规划。模型应持续调用 `ksp_realtime_state`，发现燃料、姿态或垂直速度异常时先停止指导或 Abort。

## 重要边界

- 游戏侧操作严格在 KSP 主线程执行，HTTP 线程只负责接收请求，避免从后台线程触碰 Unity 对象。
- 默认只监听回环地址，不把游戏控制端口暴露到局域网。
- `ksp_editor_launch` 要求 `confirm: true`，并且会同时通过游戏规则验证和估算的起飞 TWR/质心-推力几何预检，防止模型把无控制舱、无推进剂、无法离地或容易翻滚的火箭送入飞行。
- `ksp_flight_guidance_start` 要求 `confirm: true`；`ksp_flight_set_controls` 与指导器互斥，避免两个控制回路互相抢杆。
- 工具不会替模型猜测不存在的零件名称；零件名、节点名和已解锁状态来自当前运行的游戏实例。
- 更新 `GameData/KspMcp/Plugins/KspMcpBridge.dll` 后必须重启 KSP，已经运行的 Unity 进程不会热加载新 DLL。
- 本地没有 KSP 安装时，可以运行全部 Python 测试，但无法在这里替你启动真实游戏验证 Unity 行为；编译和游戏内烟测需要在安装了 KSP 的机器上完成。

## 验证

```powershell
python -m unittest discover -s tests -v
python -m server --self-test
```

`--self-test` 只检查 MCP 初始化、工具列表和参数路由，不要求 KSP 在线。

## API 依据

插件使用 KSP 1.x 的 `EditorLogic`、`ShipConstruct`、`Part`、`AttachNode`、`Vessel` 和 `FlightCtrlState` 接口。火箭设计和轨道流程参考 KSP 官方 [KSPedia 手册](https://www.kerbalspaceprogram.com/files/KSPedia-XB1.pdf)；控制器的“导航/制导/控制分层”和上升/着陆状态机参考 NASA 的 [Guidance, Navigation & Control](https://www.nasa.gov/reference/jsc-guidance-navigation-control-subsystems/) 以及 [Rocket Control](https://www1.grc.nasa.gov/beginners-guide-to-aeronautics/rocket-control/)。KSP API 的公开文档可参考 [KSPDocsSite](https://kspmoddinglibs.github.io/KSPDocsSite/) 以及 [XML Documentation for the KSP API](https://anatid.github.io/XML-Documentation-for-the-KSP-API/)。
