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

HTTP 接收线程不会直接调用 Unity/KSP API；它把请求放进队列，主线程每帧最多处理 `maxRequestsPerFrame` 个请求。普通状态会缓存为约 10 Hz 的紧凑遥测，避免高频客户端反复遍历完整部件树。

编辑器紧凑遥测只读取部件数和异步任务状态，不会在每次高频轮询时遍历完整部件映射或连接关系。默认关闭逐零件调试日志；需要排障时可以把 PluginData/config.cfg 中的 verboseLogging 临时设为 true，正常使用应保持 false。

### 低延迟和实时状态

```http
GET http://127.0.0.1:8765/api/v1/telemetry?since=0&limit=64&include_events=true
```

遥测响应包含 `sequence`、`event_cursor`、场景、编辑器任务、紧凑飞行状态和增量 `events`。客户端保存 `event_cursor`，下一次请求把它作为 `since`，即可只读取新增的建造进度、命令完成、自动分级和指导器事件。

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

### 分帧建造

`editor.apply_craft` 可以传 `live=true` 和 `parts_per_frame`（1–16）。插件会返回 `job_id`，在后续 Unity 帧中逐个或按小批次生成部件；用下面的命令读取进度：

```json
{"command":"editor.job_status","args":{"job_id":"build-1"}}
```

这样 KSP 可以持续渲染，MCP 客户端也能在不读取屏幕的情况下知道已经生成了多少部件。`live=false` 仍保留旧的同步建造路径。

实时任务可以通过 editor.cancel_job 停止，已经生成的部件会保留，便于无视觉客户端先检查当前结构再决定清空或继续。flight.maneuver_burn_start 是节点执行层：它根据原生节点的 Δv、实时质量和估算可用推力计算有限燃烧窗口，在 OnFlyByWire 中依次执行对准、点火和燃烧阶段；调用方应继续读取 telemetry，完成后使用 flight.guidance_stop 释放控制。

### 游戏帧内指导器

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

### 星体、转移和原生节点

`flight.bodies` 读取 KSP 的星体参数和 patched-conic 轨道；MCP 端的 `ksp_flight_transfer_plan` 会用这些数据计算透明的圆共面 Hohmann 估算。`flight.maneuver_nodes` 读取原生节点，`flight.add_maneuver_node` 和 `flight.clear_maneuver_nodes` 都要求确认参数，避免把规划误变成执行。节点 Δv 使用 radial-plus、normal-plus、prograde 坐标，单位为 m/s。

`profile=orbit` 会在尚未入轨时使用上升制导，获得目标远点后在远点抬升近点；若近点高于目标，则在近点执行逆行降轨。`profile=landing` 使用相对地表速度对准反向速度，并根据地形高度和垂直速度调节油门。两者是可停止的基础闭环，不包含完整的 Duna 转移窗口搜索、再入气动模型或地形避障。

编辑器建造请求支持 `snap_to_node`。默认值为 `true`，会按父子 AttachNode 自动对齐位置和方向；若要保留传入的世界坐标和四元数，可以显式传 `false`。对称复制不依赖游戏当前的 UI 对称模式：MCP 文档中的每个零件都是显式实例，因此插件会在生成单个零件时暂时关闭 UI 对称，避免多出未登记的零件。
