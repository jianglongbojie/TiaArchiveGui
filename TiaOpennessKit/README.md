# TiaOpennessKit · TIA Portal Openness 共享内核

这个目录**不是**一个可执行工具，而是**一堆源文件的集合（唯一真源）**：
所有基于 Siemens TIA Portal Openness 的小工具都通过
`<Compile Include="..\TiaOpennessKit\X.cs" Link="Core\X.cs" />` 链接引用它，
而不是各自复制一份。这样改一处、所有工具同时生效，也不会出现"两份实现行为不一致"。

## 目录内容

| 文件 | 职责 | 用得到它的工具 |
|---|---|---|
| `ExitCodes.cs` | 退出码 + `ToolException` | 全部 |
| `Logging.cs` | 日志（`ILogSink` 接口 + 控制台实现 + `Logger`） | 全部 |
| `SizeFormat.cs` | 字节数可读化（"12.34 MB"）——全内核只有这一份实现 | 全部 |
| `ArchiveNaming.cs` | 文件名规则（后缀 + 时间戳 + 同名避让）、项目版本号解析 | 归档、网络设备导出 |
| `FolderPackager.cs` | 把项目文件夹压成 `.zip`（纯 BCL，不碰 Openness） | 归档 |
| `EnvironmentChecks.cs` | 前置条件体检（Openness 组件 / 用户组 / 权限 / TIA 本体 / .NET / 授权） | 全部 |
| `Cli/TiaStartMode.cs` | 实例获取方式枚举（`TiaSession.Create` 的入参） | 全部 |
| `Tia/TiaEnvironment.cs` | 多版本环境探测、程序集解析钩子、诊断报告 | 全部 |
| `Tia/OpennessApi.cs` | **运行期**绑定 Openness + 反射调用（编译期不引用任何 Siemens 程序集）；含 **CAx 导出**（`GetService<CaxProvider>().Export(...)`，并按类型名跨模块找 V21 的 Step7 模块） | 全部 |
| `Tia/TiaSession.cs` | 会话生命周期（启动 / 附加 / 成对释放；附加模式可"不关用户项目"）+ `ExportCaxData`（项目 → `.aml`） | 全部 |
| `Tia/TiaDeviceInventory.cs` | 读网络设备（**在线**，走 Openness）：**三处来源合并去重**取设备 → 设备项树 → 网络接口 → 节点 → IP/掩码/子网 | 网络设备导出 |
| `Tia/DeviceInventoryModel.cs` | **纯数据模型 + 纯规则**（`NetworkEndpoint` / `DeviceInventoryResult` / `CaxTopologyLink` / `CaxPortInfo`；IP 冲突、IPv4、订货号/固件、供应商、设备去重键）—— **零 Siemens 依赖**，在线与离线两个工具共用同一张表 | 网络设备导出、CAx 离线 |
| `Tia/CaxDeviceReader.cs` | 读网络设备（**离线**，读 CAx `.aml`）：CAEX/AML → 设备 → 机架 → 站名 → 接口 → 节点/端口 + 连线（子网 / IO 系统）—— **零 Openness/TIA 依赖**（只用 System.Xml.Linq） | CAx 离线 |
| `Services/CaxTopologyExporter.cs` | 连接关系 + 端口清单的 CSV 导出（纯 BCL） | CAx 离线 |
| `Tia/TiaRunningInstance.cs` | 探测正在运行的 TIA 及其主版本（附加时自动匹配 Openness 版本） | 网络设备导出、CAx 直连 |
| `Services/DeviceInventoryExporter.cs` | 网络设备清单导出 CSV / JSON（纯 BCL，可脱离 TIA 单测） | 网络设备导出 |
| `Services/ApiProbe.cs` | 归档/检索能力的元数据自检 | 归档 |
| `Services/ArchiveService.cs` | 归档 / 检索 | 归档 |

## 在新工具里怎么用

```xml
<ItemGroup>
  <!-- 只链接自己真正用得到的那几个文件，别整目录扫进来 -->
  <Compile Include="..\TiaOpennessKit\ExitCodes.cs"   Link="Core\ExitCodes.cs" />
  <Compile Include="..\TiaOpennessKit\Logging.cs"      Link="Core\Logging.cs" />
  <Compile Include="..\TiaOpennessKit\Tia\TiaEnvironment.cs" Link="Core\Tia\TiaEnvironment.cs" />
  <Compile Include="..\TiaOpennessKit\Tia\OpennessApi.cs"    Link="Core\Tia\OpennessApi.cs" />
  <Compile Include="..\TiaOpennessKit\Tia\TiaSession.cs"     Link="Core\Tia\TiaSession.cs" />
</ItemGroup>
```

命名空间约定：内核文件一律用 `TiaOpennessKit` / `TiaOpennessKit.Tia` / `TiaOpennessKit.Services` / `TiaOpennessKit.Cli`。
（`Cli` 这个名字是历史沿用：现在里面只有一个 `TiaStartMode` 枚举，命令行版已归档。）

**升级方式**：内核改了以后，各工具重新编译即可（源码是链接引用，不需要拷贝）；
只有"把整个工具拷到另一台机器"时才需要把对应文件一起带走 ——
但如果用同样方式编译，产物仍然只有一个 exe。

## 三条铁律（都是踩过的坑）

1. **编译期不引用任何 `Siemens.Engineering*` 类型。**
   Openness 的程序集**文件名**在版本之间都不一样：V16~V19 是 `Siemens.Engineering.dll`，
   V20/V21 是 `Siemens.Engineering.Base.dll`。代码里只要写下 `Project`、`TiaPortal`
   这类类型名，exe 的引用表就被钉死在一个版本上，换台机器必然加载失败
   （连绑定重定向都救不了，因为改不了程序集名）。
   所以一律"运行期按类型名找 + 反射调用"。

2. **先挂程序集解析钩子，再触碰 Openness。**
   顺序必须是 `TiaEnvironment.Detect` → `InstallAssemblyResolver` → `OpennessApi.Bind`。
   反了就会 `FileNotFoundException`。钩子里除了 `PublicAPI\Vxx\net48`，
   还必须包含 `Portal Vxx\Bin\PublicAPI`（运行时依赖在那里）。

3. **附加模式不关别人的项目。**
   附加到用户已打开的 TIA 时，`Project.Close()` 会把他正在编辑的项目关掉。
   用 `TiaSession.CloseProjectOnDispose = false`（只放弃引用，不 Save 也不 Close）。

## 跨版本事实（已逐版本核对，可直接引用）

| 能力 | V16 | V17 | V18 | V19 | V21 | 备注 |
|---|---|---|---|---|---|---|
| 读设备 / 设备项 / 网络接口 / 节点 / 子网 | ✓ | ✓ | ✓ | ✓ | ✓ | 成员结构完全一致 |
| 读 IP / 子网掩码 | ✓ | ✓ | ✓ | ✓ | ✓ | `Node` 的**动态属性** `Address` / `SubnetMask` |
| `Project.Devices`（只给**根级**设备） | ✓ | ✓ | ✓ | ✓ | ✓ | 主控制 PLC / HMI / PC 站 |
| `Project.UngroupedDevicesGroup`（**「未分组的设备」**） | ✓ | ✓ | ✓ | ✓ | ✓ | **全部分布式 IO 设备都在这里**；V16 声明在 `Project`、V17+ 在基类 `ProjectBase` |
| `Project.DeviceGroups`（用户自建的设备组） | ✗ | ✓ | ✓ | ✓ | ✓ | V16 没有这个属性 → 按"有则读、无则跳过" |
| 主程序集名 | `Siemens.Engineering.dll` | 同左 | 同左 | 同左 | `Siemens.Engineering.Base.dll` | 差异只在这里 |
| **CAx 导出**（`Cax.CaxProvider.Export`） | ✓ `(对象, 文件, 日志)→bool` | ✓ 同左 | ✓ 多一个 `ProjectBase` 重载 | ✓ 另有两参数 `→TransferResult` | ✓ 同左，但**类型在 `Siemens.Engineering.Step7.dll` 模块里** | 见 `OpennessApi.ExportCax`；V21 要跨模块按类型名找 |

- ★ **设备清单有三个来源，只读一个就会"少读设备"**（实测踩过：只读 `Project.Devices`，
  一个含 18 台分布式 IO 的项目只读出 1 台主 CPU）。三者可能重叠（系统组里也可能含根级设备），
  所以合并时**必须按设备名去重**。西门子官方对「未分组设备」的说明：
  「项目中的所有分布式 I/O 设备都位于"未分组设备"文件夹中」。
- `HW.DeviceGroup` 的公开属性只有 `Name` / `Devices` / `Parent`（V16~V21 一致，**没有子组属性**）
  → 组内子组遍历按"有则递归、无则忽略"的防御式实现（深度上限兜底）。
- **没有任何版本存在 `IPAddress` 类型** —— IP 只能按属性名读；
  官方写法见 `github.com/orgs/tia-portal-applications/discussions/2`
  （`node.SetAttribute("Address", "192.168.0.1")`，读就是同一个属性的 `GetAttribute`）。
- 因为属性名对个别设备族可能不同，`TiaDeviceInventory` 在**读不到 IP 时会把该节点
  真实存在的属性名记进诊断清单**，排错时直接看日志即可。
- ★ **CAx 导出不是 `Project` 上的方法**：它是服务式入口
  `project.GetService<CaxProvider>()` → `provider.Export(project, 导出文件[, 日志文件])`。
  （`Project` / `PlcBlock` 上的 `ExportAsDocuments` 家族导出的是"文档"，与 CAx 无关。）
  并且 `TransferResult.State = Error` **不等于导出失败**：实测一个含 HMI 的项目会报
  "device HMI_1 的执行将跳过。所需属性 TypeIdentifier 无效或缺失"、State=Error，
  但 `.aml` 照常生成、其余设备数据完整 —— 判据要用"文件是否真的生成"，
  把 State 与逐条消息当**告警**转达给用户。

### 怎么验证"某个版本的 Openness 够不够用"

不需要启动 TIA，也不需要装那个版本 —— 用网络设备工具的能力自检对着任意 API 目录跑：

```
TiaDeviceGui.exe --apicap out.log "E:\...\PublicAPI\V19"
```

输出 `APICAP_OK` 表示该版本具备读网络设备所需的全部类型与成员。
（归档能力另有 `ApiProbe` 提供同类自检。）
