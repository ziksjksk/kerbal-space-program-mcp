# KSP 游戏侧桥接

把 `GameData/KspMcp` 复制到 KSP 根目录的 `GameData` 下。插件 DLL 位于 `Plugins/KspMcpBridge.dll`，配置位于 `PluginData/config.cfg`。

## 编译

在 PowerShell 中：

```powershell
$env:KSP_ROOT = 'D:\Games\Kerbal Space Program'
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本只引用当前 KSP 安装里的程序集，不会把 Unity 或 KSP DLL 复制进发布包。插件默认兼容 KSP 1.x；不同 KSP 小版本如果修改了私有 UI 或 TimeWarp 方法，代码中的反射兼容层会优先尝试多个入口。

## HTTP 协议

只建议从本机 MCP 服务端访问：

```http
GET http://127.0.0.1:8765/api/v1/status
POST http://127.0.0.1:8765/api/v1/command
Content-Type: application/json
X-KSP-MCP-Token: <可选>

{"command":"editor.validate","args":{}}
```

所有响应都是：

```json
{"ok":true,"result":{}}
```

或：

```json
{"ok":false,"error":{"code":"...","message":"...","details":null}}
```

HTTP 接收线程不会直接调用 Unity/KSP API；需要读取游戏对象的命令会放进队列，主线程每帧最多处理 `maxRequestsPerFrame` 个请求。普通状态会缓存为默认约 20 Hz 的紧凑遥测，避免高频客户端反复遍历完整部件树；`GET /api/v1/telemetry` 直接从线程安全缓存读取，因此不会被大型建造、完整快照或性能分析命令阻塞。

编辑器紧凑遥测只读取部件数和异步任务状态，不会在每次高频轮询时遍历完整部件映射或连接关系；异步建造期间也不会在每个零件前重复同步整个部件表。飞行侧的位置和阶段保持高频更新，资源/发动机摘要以较低频率缓存，避免大型飞船的实时状态反过来拖慢游戏。默认关闭逐零件调试日志；需要排障时可以把 PluginData/config.cfg 中的 verboseLogging 临时设为 true，正常使用应保持 false。

### 低延迟和实时状态

```http
GET http://127.0.0.1:8765/api/v1/telemetry?since=0&limit=256&include_events=true
```

遥测响应包含 `sequence`、`bridge_version`、`event_cursor`、`oldest_event_cursor`、`events_lost`、`events_returned`、`events_truncated`、`next_since`、场景、编辑器任务、紧凑飞行状态和增量 `events`。`event_cursor` 是生产者最新游标；如果 `events_truncated=true`，客户端应使用 `next_since` 而不是直接跳到 `event_cursor`，这样高速分帧建造时不会跳过中间零件；如果 `events_lost` 或 `resync_required` 大于零，则历史已经超出缓冲窗口，应以当前摘要和返回的 `next_since` 重新同步。

如果客户端不需要固定时长采样，可以在 MCP 层使用 `ksp_wait_for_event`：它把 `wait_ms` 交给桥的后台监听线程，检测到 `event_cursor` 推进后立即返回，不会在 Unity 主线程中等待，也不会让客户端以固定间隔重复发请求；`poll_interval` 仍保留为旧桥的兼容性退避参数。`ksp_realtime_state` 也支持可选的 `wait_ms`（0–1000）。这样无视觉模型可以按“发出命令 -> 等待事件 -> 读取摘要 -> 再决定下一步”的节奏控制建造、点火和分级。

多个安全命令可以通过一个请求批量提交：

```json
{
  "command": "batch",
  "args": {
    "commands": [
      {"command": "editor.set_stage", "args": {"id": "engine", "stage": 2}},
      {"command": "editor.validate", "args": {}}
    ]
  }
}
```

`editor.launch`、`flight.abort` 和 `flight.recover` 被明确禁止放入批处理，必须走各自的确认接口。

零件解析会优先使用 KSP 的运行时名称，并兼容配置文件常见的点号/下划线差异（例如 `liquidEngine2-2.v2` 与 `liquidEngine2-2_v2`）。仍然建议先调用 `parts.list`，把当前实例实际返回的名称和连接节点作为建造输入。

### 分帧建造

`editor.apply_craft` 可以传 `live=true` 和 `parts_per_frame`（1–16）。默认每个 Unity 帧生成 4 个部件；插件会返回 `job_id`，在后续 Unity 帧中逐个或按小批次生成部件，并为每个部件发出 `editor.build.part_added` 事件；用下面的命令读取进度：

```json
{"command":"editor.job_status","args":{"job_id":"build-1"}}
```

这样 KSP 可以持续渲染，MCP 客户端也能在不读取屏幕的情况下知道已经生成了多少部件。`live=false` 仍保留旧的同步建造路径。

实时任务可以通过 editor.cancel_job 停止，已经生成的部件会保留，便于无视觉客户端先检查当前结构再决定清空或继续。flight.maneuver_burn_start 是节点执行层：它根据原生节点的 Δv、实时质量和估算可用推力计算有限燃烧窗口，在 OnFlyByWire 中依次执行对准、点火和燃烧阶段；调用方应继续读取 telemetry，完成后使用 flight.guidance_stop 释放控制。

### 游戏帧内指导器（可由人接管）

```json
{
  "command": "flight.guidance_start",
  "args": {
    "profile": "ascent",
    "target_apoapsis": 80000,
    "auto_stage": true,
    "confirm": true
  }
}
```

指导器在 `OnFlyByWire` 回调中刷新杆量，`flight.guidance_status` 返回阶段和最后控制输出；`flight.guidance_stop` 会清除指导器和手动控制租约。它只使用 KSP 的状态和轨道数据，不需要截图。

启动指导器时会先返回并记录 `preflight`：当前飞船是否可控、质量、可用分级推力、局部重力、TWR、当前阶段、发动机数量和逐级发动机/分离器报告。`ascent`/`orbit` 在发射台或已着陆状态下如果估算 TWR 低于 1.02 会被拒绝，避免无视觉客户端把无法离地的飞船交给自动控制；上升段还会按约 1.35–1.75 的目标 TWR 调节油门，姿态回路会对角速度做阻尼。已经运行的指导计划可以通过 `flight.guidance_update`（MCP 工具名 `ksp_flight_guidance_update`）在线更新目标和安全选项，不必释放控制再重启。

### 星体、转移和原生节点

`flight.bodies` 读取 KSP 的星体参数和 patched-conic 轨道；MCP 端的 `ksp_flight_transfer_plan` 会用这些数据计算透明的圆共面 Hohmann 估算。`flight.maneuver_nodes` 读取原生节点，`flight.add_maneuver_node` 和 `flight.clear_maneuver_nodes` 都要求确认参数，避免把规划误变成执行。节点 Δv 使用 radial-plus、normal-plus、prograde 坐标，单位为 m/s。

`profile=orbit` 会在尚未入轨时使用上升制导，获得目标远点后在远点抬升近点；若近点高于目标，则在近点执行逆行降轨。`profile=landing` 会先执行简单脱轨决策，再使用相对地表速度、制动距离、局部重力、地形高度和垂直速度调节油门；进入低空后会自动发送齿轮下放命令，也可以传 `target_latitude`、`target_longitude` 让水平速度向指定地点收敛。两者是可停止、可在线调整的游戏内闭环，不会把近似轨道模型误报成完整的 Duna 任务保证；完整转移窗口搜索、再入气动模型和地形避障仍需模型逐段检查。指导器不是权限锁，用户可以随时在 KSP 中手动控制，停止指导后由原生界面继续飞行。

编辑器建造请求支持 `snap_to_node`。默认值为 `true`，会按父子 AttachNode 自动对齐位置和方向；若要保留传入的世界坐标和四元数，可以显式传 `false`。对称复制不依赖游戏当前的 UI 对称模式：MCP 文档中的每个零件都是显式实例，因此插件会在生成单个零件时暂时关闭 UI 对称，避免多出未登记的零件。

