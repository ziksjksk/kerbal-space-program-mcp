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

HTTP 接收线程不会直接调用 Unity/KSP API；它把请求放进队列，主线程每帧最多处理 `maxRequestsPerFrame` 个请求。

编辑器建造请求支持 `snap_to_node`。默认值为 `true`，会按父子 AttachNode 自动对齐位置和方向；若要保留传入的世界坐标和四元数，可以显式传 `false`。对称复制不依赖游戏当前的 UI 对称模式：MCP 文档中的每个零件都是显式实例，因此插件会在生成单个零件时暂时关闭 UI 对称，避免多出未登记的零件。
