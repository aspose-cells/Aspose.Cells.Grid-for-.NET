# Aspose.Cells.GridJs 纯浏览器 WASM demo

基于 **Aspose.Cells** 与 **Aspose.Cells.GridJs** 的表格编辑器，**没有服务端进程**：Aspose.Cells
引擎、GridJs 服务与工作簿字节全部跑在一个 AOT 编译的 Blazor WebAssembly 应用里，网格客户端从
CDN 加载，文件存放在浏览器的 Origin Private File System（OPFS）。

English documentation: [README.md](README.md)

---

## 功能

- 加载 `.xlsx` 工作簿并渲染成可编辑网格
- 编辑单元格，公式由浏览器内的 Aspose.Cells 重算
- 图表与图片由链接进 wasm 镜像的 SkiaSharp 光栅化
- 上传本地工作簿，按 48 KB 分块写入 OPFS
- 通过 File 菜单导出回 `.xlsx`，直接由 OPFS 提供

页面加载完成后，不再有任何取数请求离开浏览器。

## 前置条件

| 要求 | 说明 |
| --- | --- |
| .NET 8 SDK | net8.0 是**最低**要求：SkiaSharp 的 wasm 资源对更早的目标框架直接报错。`global.json` 钉 `8.0.100` + `rollForward: latestMinor` |
| `wasm-tools` workload | `dotnet workload install wasm-tools`。Windows 上会写入 SDK 目录，需提权运行 |
| Python 3 | 运行 `tools/serve.py`。任何能发送 `Content-Type: application/wasm` 的静态服务器都可以 |
| 支持 OPFS 的浏览器 | Chrome/Edge 102+、Firefox 111+、Safari 15.2+ |
| 磁盘与时间 | 一次冷 AOT publish 约需 5 GB 空闲磁盘、20 分钟左右 |

## 快速开始

```bash
dotnet workload install wasm-tools
dotnet publish -c Release
python tools/serve.py --root bin/Release/net8.0/publish/wwwroot --port 5301
```

打开 <http://127.0.0.1:5301>，页面会自动加载 `SampleExcel.xlsx`。

- <http://127.0.0.1:5301/?file=chart.xlsx> 改为加载图表示例
- **upload** 按钮可加载任意本地工作簿；同名文件重新上传会替换 OPFS 里的那一份，不会累积副本

**不要**用 `dotnet run`。Blazor DevServer 托管的是 *build* 产物而非 publish 产物，结果会把整个
`_framework` 全部 404。把 `tools/serve.py` 指向 build 目录同样如此。

在浏览器控制台确认网格客户端带 wasm 缝：

```js
typeof window.x_spreadsheet?.setWasmMode   // 必须是 "function"
```

客户端从 unpkg 加载，**故意不带版本号**（`https://unpkg.com/gridjs-spreadsheet/xspreadsheet.js`
与 `.css`），所以 npm 上一次新发布在 CDN 刷新后对每位访客即时生效 —— 记下你实际解析到的版本。
`gridjs-spreadsheet@26.11.0` 带 wasm 缝，`Aspose.Cells` / `Aspose.Cells.GridJs` 26.11.0 已在
nuget.org 上。

## 部署前必读

**授权存放方式是 go/no-go 判断，不是细节。**

demo 开箱运行于 Aspose **评估模式**：`HttpProxy.InitializeAsync` 尝试取
`license/Aspose.Cells.lic`，取不到就静默回退，评估模式会插入提示工作表并施加功能限制。若有
自己的授权，把 `Aspose.Cells.lic` 放进 `wwwroot/license/` 即可；它是静态文件，无需 republish。
**绝不要提交它**：`.gitignore` 已屏蔽 `wwwroot/license/*.lic` 与 `*.lic.enc`。

引擎在浏览器里，授权就必须在浏览器里。`SetLicense` 必须与 `Workbook` 同进程，服务端无法代
客户端授权。AOT 编译的是**代码**（IL→wasm），license 是**数据**（字节），所以 AOT 对隐藏授权
毫无作用，它不是安全或 DRM 边界。浏览器运行时完全在终端用户掌控下 —— 网络抓包、JS 内存、
wasm 线性内存 dump、devtools 断点、interop hook —— 任何客户端秘密都可恢复：

| 授权存放方式 | 终端用户怎么拿 |
| --- | --- |
| wwwroot 静态文件 | 直接 `GET /license/Aspose.Cells.lic` |
| 从 JS 传进 `[JSInvokable]` 方法 | 调用点的 JS 内存里就有 |
| 嵌进程序集当资源 | 整个程序集都会下发，且不可裁剪 |
| 落地时加密、在 WASM 内解密 | 解密那一刻明文就在 wasm 线性内存里；外加密钥 —— 密钥要么在客户端（那就一起下发给攻击者），要么在服务端（那任何客户端都能调用它） |
| `SetLicense` 之后 | 在 wasm 线性内存里，dump 或下断点即得 |

把授权文件加密后再下发仍然值得做，但它是**抬高成本的卫生措施**，不是边界：发布的 `wwwroot`
里不再存在明文授权，表格第一行的拿法随即失效，爬虫、代理日志、误提交也都无法再从一个 README
自己写明的 URL 上取到一份有效的签名文件。`.enc` 同样不要入库。若把密钥放到服务上，一定要做
鉴权和限流 —— 每会话下发各自密钥能让泄露可归因、可吊销；而一个开放端点配上 metered license
会让陌生人替你刷计费。

这些都不改变那个已经在运行你页面的攻击者。按 Aspose EULA，一份能在浏览器里解密的授权就是
一份你已分发的授权。

**结论：这套架构只适用于内网、离线或受信任客户端场景。** 公网对外部署请用服务端版 ——
Aspose.Cells 与 GridJs 都留在服务端，浏览器只跑 JS 客户端，过线的只有 JSON 结果。

## 工作原理

```
浏览器页面（wwwroot/index.html + js/）
   │  DotNet.invokeMethodAsync('GridJsWasm', 'HttpProxyEntry', req)
   ▼
Proxy/HttpProxy.cs          解析逻辑上的 /GridJs2/* 路由、应用授权、维护 OPFS 状态
   ▼
Aspose.Cells.GridJs 的 GridJsService（IsWasm=true）
   ▼
Aspose.Cells                绘图走链接进 wasm 镜像的 SkiaSharp
```

- **没有 HTTP 层。** 上面的 `/GridJs2/*` 只是逻辑路由，由 `Proxy/HttpProxy.cs` 从 interop 传来
  的请求对象解析。
- **文件字节从不整体跨 interop。** `js/gridjs-opfs.js` 按 49,152 字节分块上传（3 的倍数，每块
  base64 可独立解码），走 `writeStart` / `writeChunk` / `writeEnd`，失败时 `writeAbort`，不留半
  个文件。
- **OPFS 取代磁盘缓存。** `OpfsCacheForStream` 实现 `GridJsOptions.CacheImp`，替换库自带、需要
  浏览器并不具有的文件系统的 `LocalFileCache`。
- **Blazor 不渲染任何东西。** `blazor.webassembly.js` 以 `autostart="false"` 加载，由
  `js/demo.js` 里 `await Blazor.start()`；页面本身是纯 HTML 与 JS。
- **`?file=` 使用前先校验**，正则 `^[A-Za-z0-9._-]+\.xlsx$`；这个值同时进入查询字符串和 OPFS
  键名，不匹配就回退到默认样例。

## 目录结构

```
GridJsWasm.csproj            依赖 + AOT。文件名即程序集名，js/ 里 DotNet.invokeMethodAsync('GridJsWasm', ...) 硬编码了它
global.json                  SDK 锁定：8.0.100，rollForward latestMinor
Program.cs                   Blazor 启动器；GridJsOptions/GridJsService 与 OPFS 注册
Proxy/HttpProxy.cs           路由处理、授权引导、上传/导出、运行时开关
Proxy/GridJsJsBridge.cs      供 DI 之外使用的静态 IJSRuntime 持有者
OpfsCacheForStream.cs        基于 OPFS 的 CacheImp 实现
OpfsJsonCache.cs             OPFS 中的整表 JSON 缓存（默认关闭）
wwwroot/index.html           唯一页面；CDN 客户端，autostart="false"
wwwroot/css/demo.css         顶栏与网格容器布局
wwwroot/js/demo.js           页面编排：启动 Blazor、加载样例、上传
wwwroot/js/gridjs-opfs.js    OPFS 原语与分块上传
wwwroot/js/gridjs-wasm-init.js  .NET 引导、setWasmMode、挂载 x-spreadsheet、导出
wwwroot/data/                SampleExcel.xlsx（默认）、chart.xlsx（图表示例）
wwwroot/license/README.txt   自有 .lic 的放置位置；该目录不含任何授权
tools/serve.py               静态服务器，MIME 正确且不缓存
```

样例工作簿不含个人数据：入库前把作者字段以及 Excel 写进 `xl/workbook.xml` 的保存位置属性都
清空了，其余部件未动，单元格、样式与公式都是普通工作簿内容。

## 构建说明

- **AOT 是取舍，不是前提。** `SkiaSharp.NativeAssets.WebAssembly` 4.154.0 自己会设置
  `WasmBuildNative` 并加入 `libSkiaSharp.a` 引用，所以开不开 AOT 都能链接进原生库 —— 两种配置
  都能加载并渲染工作簿。本项目保持 AOT 开启，因为这是本 demo 全程实测所在的配置，而且它把 IL
  编译成 wasm 而非解释执行；代价是 publish 时间与镜像大小（`dotnet.native.wasm` AOT 约 72 MiB，
  非 AOT 约 6.4 MiB）。要关掉它，设 `RunAOTCompilation=false`，并自行重测各交互路径。
- **不要在 `dotnet publish` 之前跑 `dotnet build`。** 它会重新生成每个 `.bc`，把增量 publish
  变成全量。
- **改 HTML/CSS/JS 不需要 republish。** `tools/serve.py` 发送 `Cache-Control: no-store`，刷新即
  生效。只有 C# 改动才需要 publish。
- **链接 `Aspose.Cells.dll.bc` 时进程被杀，直接重跑。** 该步骤会跳过已完成的产物，重跑通常就过。
  内存紧张时加 `-p:EmccExtraBitcodeLDFlags=-O1`。
- **仓库不提供预构建产物**，GitHub Pages 也不合适：Pages 和 `tools/serve.py` 都不会给已经预压缩的
  `.br` 资源加 `Content-Encoding: br`，每位访客都要下载未压缩的 wasm。要公开托管请自建于支持
  `br` 的服务器之后。
- **也不带 CI 工作流** —— 一次冷 AOT publish 约 20 分钟，超出托管 runner 的合理范围。请在自己
  估算过的机器上构建。

## 故障排查

| 现象 | 原因与处理 |
| --- | --- |
| restore 报 `NU1101` | 26.11.0 **确实**在 nuget.org 上 —— 检查你的 NuGet 源配置 |
| `NETSDK1147：必须安装 wasm-tools workload` | 安装它。磁盘上有 pack 不等于已安装 —— 用 `dotnet workload list` 确认 |
| `setWasmMode is not a function` | CDN 给的是不带 wasm 缝的 bundle —— 见**快速开始** |
| 启动时某个 `.wasm` 请求失败 | 服务器没有发送 `Content-Type: application/wasm`。用 `tools/serve.py` |
| 日志里 `GET /license/Aspose.Cells.lic → 404` | **预期。** 本仓库不含授权，应用捕获后以评估模式运行 |
| 日志里 `GET /content/img/updating.gif → 404` | **预期且无关功能。** npm bundle 内硬编码的路径，wasm 模式下用不到 |
| HTTP 日志里出现 `/GridJs2/*` 请求 | **不该出现** —— 一个都不应该走 HTTP。说明缝没接上：通常是 bundle 不带缝，或从未调用 `setWasmMode` |
| 单元格编辑报 `DllNotFoundException: libSkiaSharp` | Skia 归档根本没链接进来。确认 TFM 是 net8.0+ 且引用了 `SkiaSharp.NativeAssets.WebAssembly`。这与 AOT 开关无关 |

## 已知限制

- 大工作簿在浏览器内解析，存在内存溢出风险：wasm 内存区有限且与运行时共用。
- 首次加载与写入单元格比服务端版更慢，内存占用也更高。
- 缓存键取文件名的**前 48 个字符**，共享 48 字符前缀的两个文件名会共用同一个缓存槽。
- 裁剪开启且 `SuppressTrimAnalysisWarnings=true`，所以 interop POCO 与泛型实例的存活是**人工
  验证**的，不靠编译器警告。
- .NET 8 已于 2026 年 11 月停止支持。由于 SkiaSharp 的 wasm 资源，它仍是本栈的最低版本；迁移到
  受支持的 TFM 是独立的后续决定。

## 许可证

本仓库的 demo 代码为 MIT —— 见 [LICENSE](LICENSE)。第三方组件及其许可证列在
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

Aspose.Cells 与 Aspose.Cells.GridJs 是 Aspose 最终用户许可协议下的商业产品，构建时从 nuget.org
还原，本仓库不再分发。
