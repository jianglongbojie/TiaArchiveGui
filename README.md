# TIA Portal 归档助手（TiaArchiveGui）

用鼠标点几下，完成 **TIA Portal（博途）项目的归档 / 恢复 / 批量备份**。
基于 Siemens **Openness API**，**一份 exe 通吃 V16~V21** —— 编译期不引用任何 Siemens 程序集，运行期按本机安装的 TIA 版本反射调用，不存在"一份 exe 只认一个版本"的问题。

![归档页](TiaArchiveGui/screenshots/gui-tab1.png)

## 能做什么

| 功能 | 说明 |
| --- | --- |
| **归档项目** | `.apXX` → `.zapXX`（压缩 / 保留可恢复数据等模式可选），可选"归档前先保存项目" |
| **恢复项目** | `.zapXX` → 项目并打开（支持旧版本项目的升级打开 `RetrieveWithUpgrade`） |
| **环境探测** | 不启动 TIA 也能验证本机 Openness 环境：版本、程序集、API 签名、用户组、自检 |
| **批量归档** | 扫描文件夹批量处理；按项目版本**自动选内核**（一个进程只绑一个版本，混版本自动走子进程） |
| **打包项目文件夹** | 整个项目目录压成 `.zip`，完全不经过 Openness —— 没装 Openness 的机器也能用 |
| **文件名规则** | 自定义后缀 + 日期时间戳，效果等同 TIA 归档对话框里的"将日期和时间添加到目标名称中" |

## 下载直接用

**[TiaArchiveGui/发布/TiaArchiveGui.exe](TiaArchiveGui/发布/TiaArchiveGui.exe)** —— 单文件（约 230 KB），无需安装。

目标机器需要：

- Windows + **.NET Framework 4.8**（Win10 1903 及以上 / Win11 自带）
- 归档 / 恢复需要本机装有 **TIA Portal** 且勾选了 Openness 组件（TIA 安装时选装，或在"修改安装"里补上）
- 只想"压个包备份"的话用 **打包项目文件夹（.zip）** 模式，连 Openness 都不需要

> 到新机器先跑一次体检（不开窗、不启动 TIA，退出码 0 = 全通过）：
> `TiaArchiveGui.exe --envcheck "%TEMP%\envcheck.log"`

## 自行编译

```bat
dotnet build TiaArchiveGui\TiaArchiveGui.csproj
```

编译**不需要**本机装有 TIA —— 工程里没有任何 Siemens 程序集引用（相关 DLL 由程序启动时从 TIA 安装目录按需加载）。

## 目录结构

```
├── TiaArchiveGui/     界面版工具（WinForms，net48）
└── TiaOpennessKit/    共享内核：归档 / 检索、环境探测、运行期绑定 Openness（唯一真源）
```

> `TiaArchiveGui` 通过工程文件里的 `<Compile Include="..\TiaOpennessKit\..." Link="..." />`
> 链接引用内核源码（不复制、不生成 DLL），所以两个目录必须保持平级。

## 实测过的一些"坑"（都已在代码里处理）

- **归档输出不能放在项目目录里（含子目录）**：TIA 会拒绝并报"项目目录已存在，无法保存。请选择一个不同的路径。"
  （V19 与 V21 实测均复现）；所以默认输出位置是**项目目录的上一级**，手动改回项目目录里会被工具在启动 TIA **之前**拦下。
- **TIA 的归档不会覆盖已存在的目标**（`Archive Operation is not possible as the target file/folder … is already exist.`）：
  界面上的"覆盖"是工具自己实现的 —— 旧文件先改名备份、归档成功后才删除备份，失败则恢复旧文件。
- **一个进程只能绑定一个版本的 Openness 程序集**：批量归档遇到混合版本的项目时，按版本分组交给子进程各自绑定，
  避免"新版主程序集 + 旧版依赖"的混合加载崩溃。
- 归档格式 = **运行内核的版本**，与项目本身版本无关（用 V21 内核归档 `.ap19` 项目，产物是 `.zap21`）。

## 详细文档

- 界面用法、每个选项的行为、排错指南：[TiaArchiveGui/README.md](TiaArchiveGui/README.md)
- 内核的跨版本事实、三条铁律、目录说明：[TiaOpennessKit/README.md](TiaOpennessKit/README.md)

## 反馈

遇到问题欢迎提 Issue，附上日志（界面上的"保存日志"按钮）和你的 TIA 版本。

## 许可

MIT License（见 [LICENSE](LICENSE)）。
