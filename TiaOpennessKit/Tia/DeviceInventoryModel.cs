using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// 一条网络节点记录（导出表的一行）。
    ///
    /// 行粒度说明（用户确认）：**一个网络节点 = 一行**。
    /// 一台 CPU 上如果有两个网口（X1 / X2），就是两行；这样能定位到具体接口，
    /// 做 IP 分配表、查线、查冲突都用得上。
    /// </summary>
    public sealed class NetworkEndpoint
    {
        /// <summary>行号（导出顺序）。</summary>
        public int Index { get; set; }

        /// <summary>该设备在项目里的序号（同一台设备的多个节点共用同一个号）。</summary>
        public int DeviceIndex { get; set; }

        /// <summary>设备名（Device.Name），如 PLC_1。</summary>
        public string DeviceName { get; set; }

        /// <summary>设备类型（DeviceItem.TypeIdentifier，形如 OrderNumber:6ES7 515-2AM01-0AB0/V2.8）。</summary>
        public string DeviceTypeIdentifier { get; set; }

        /// <summary>是否是 GSD（第三方）设备。</summary>
        public bool IsGsd { get; set; }

        /// <summary>
        /// 供应商：订货号/系统设备 → Siemens；GSD 设备从 GSDML 标识里解析（如 `GSDML-V2.31-ELCO-…` → ELCO）。
        /// （PRONETA 对第三方设备只显示"未知(0)"，这一列我们比它强。）
        /// </summary>
        public string Vendor { get; set; }

        /// <summary>
        /// 角色：Controller（IO 控制器）/ Device（IO 设备）。
        /// 离线 CAx 能判定（谁持有 PROFINET IO-System 谁就是控制器）；在线 Openness 读取时留空。
        /// </summary>
        public string Role { get; set; }

        /// <summary>接口所属设备项名（DeviceItem.Name），如 PROFINET 接口_1。</summary>
        public string DeviceItemName { get; set; }

        /// <summary>
        /// 设备项类型（DeviceItem.TypeIdentifier）。
        /// 形态通常是 OrderNumber:6ES7 515-2AM01-0AB0/V2.8 —— 订货号与固件版本就藏在里面，
        /// 所以读不到专门的属性时，这里可以直接兜底解析出来。
        /// </summary>
        public string DeviceItemTypeIdentifier { get; set; }

        /// <summary>设备项路径，如 /PLC_1/PROFINET 接口_1。</summary>
        public string DeviceItemPath { get; set; }

        /// <summary>
        /// 位置号（DeviceItem.PositionNumber）。
        ///
        /// ★ 实测提醒：对**机架上的模块**它就是槽号（1、2、3…）；
        ///   但对**接口这类子项**，TIA 给的是内部编号（实测 32768 / 33024 这种）。
        ///   所以列名用"位置号"而不是"槽号"，免得把内部编号当成槽位号去用。
        /// </summary>
        public string Slot { get; set; }

        /// <summary>网络接口名（NetworkInterface.Name）。</summary>
        public string InterfaceName { get; set; }

        /// <summary>接口类型（NetworkInterface.InterfaceType，V16~V21 都是 NetType 枚举）。</summary>
        public string InterfaceType { get; set; }

        /// <summary>接口运行模式（NetworkInterface.InterfaceOperatingMode）。</summary>
        public string InterfaceOperatingMode { get; set; }

        /// <summary>节点名（Node.Name）。</summary>
        public string NodeName { get; set; }

        /// <summary>节点标识（Node.NodeId）——PROFINET 下就是 PROFINET 设备名，PROFIBUS 下是站地址。</summary>
        public string PnDeviceName { get; set; }

        /// <summary>节点类型（Node.NodeType，NetType 枚举）。</summary>
        public string NodeType { get; set; }

        /// <summary>IP 地址（Node 的动态属性 Address，**只在它确实是 IPv4 时才有值**）。</summary>
        public string IpAddress { get; set; }

        /// <summary>
        /// 非 IP 的节点地址。
        /// 实测发现：MPI / PROFIBUS 节点的 Address 属性里装的是**总线站地址**（例如 1），
        /// 不是 IP。如果把它塞进 IP 列，做 IP 台账时会被误当成 IP，所以单独成一列。
        /// </summary>
        public string BusAddress { get; set; }

        /// <summary>子网掩码（Node 的动态属性 SubnetMask）。</summary>
        public string SubnetMask { get; set; }

        /// <summary>路由器地址（动态属性 RouterAddress，取不到留空）。</summary>
        public string RouterAddress { get; set; }

        /// <summary>IP 分配方式（动态属性 IpProtocolSelection，取不到留空）。</summary>
        public string IpAssignment { get; set; }

        /// <summary>所属子网名（Node.ConnectedSubnet.Name）。</summary>
        public string SubnetName { get; set; }

        /// <summary>所属子网类型（Subnet.NetType）。</summary>
        public string SubnetType { get; set; }

        /// <summary>订货号（动态属性 OrderNumber）。</summary>
        public string OrderNumber { get; set; }

        /// <summary>固件版本（动态属性 FirmwareVersion，取不到留空）。</summary>
        public string FirmwareVersion { get; set; }

        /// <summary>IO 控制器（NetworkInterface.IoControllers 里的名字，取不到留空）。</summary>
        public string IoController { get; set; }

        /// <summary>IO 系统（Subnet.IoSystems 里的名字，取不到留空）。</summary>
        public string IoSystem { get; set; }

        /// <summary>备注：无 IP 的原因、未连接子网、属性读取失败等。</summary>
        public string Remark { get; set; }

        /// <summary>告警：目前只有 IP 冲突。</summary>
        public string Alert { get; set; }

        /// <summary>是否读到了有效 IP。</summary>
        public bool HasIp
        {
            get { return !string.IsNullOrEmpty(IpAddress); }
        }
    }

    /// <summary>
    /// 一条连接关系（目前只有**离线 CAx（.aml）**读得出来；在线 Openness 读取时这张表为空）。
    ///
    /// 为什么单独成表：CAx 里的连线是"节点→子网"与"接口→IO 系统"两类，
    /// 不挂在某一行节点上（一台设备可能既有节点连接、又有接口到 IO 系统的连接）。
    /// </summary>
    public sealed class CaxTopologyLink
    {
        /// <summary>行号。</summary>
        public int Index { get; set; }

        /// <summary>连接类型：子网 / IO 系统。</summary>
        public string Kind { get; set; }

        /// <summary>A 端设备（站名，如 OP101RM111）。</summary>
        public string SideADevice { get; set; }

        /// <summary>A 端设备项路径（如 /OP101RM111/Interface）。</summary>
        public string SideAItem { get; set; }

        /// <summary>A 端端点名（节点名 IE1 / 接口名 Interface）。</summary>
        public string SideAEndPoint { get; set; }

        /// <summary>A 端端口标识（如 P1R；没有则空）。</summary>
        public string SideAPort { get; set; }

        /// <summary>B 端描述（子网名 / IO 系统名 + 控制器）。</summary>
        public string SideB { get; set; }

        /// <summary>备注。</summary>
        public string Remark { get; set; }
    }

    /// <summary>一个网络端口（离线 CAx 用：CAx 里只有端口清单，没有端口↔端口接线）。</summary>
    public sealed class CaxPortInfo
    {
        /// <summary>行号。</summary>
        public int Index { get; set; }

        /// <summary>设备（站名）。</summary>
        public string DeviceName { get; set; }

        /// <summary>所属设备项路径（如 /OP101RM111/Interface）。</summary>
        public string ItemPath { get; set; }

        /// <summary>端口名（如 Port 1）。</summary>
        public string PortName { get; set; }

        /// <summary>端口标识（Label，如 P1R / P2L）。</summary>
        public string Label { get; set; }

        /// <summary>位置号。</summary>
        public string PositionNumber { get; set; }
    }

    /// <summary>一次采集的结果。</summary>
    public sealed class DeviceInventoryResult
    {
        /// <summary>构造函数：初始化各集合，避免调用方到处判 null。</summary>
        public DeviceInventoryResult()
        {
            Endpoints = new List<NetworkEndpoint>();
            Diagnostics = new List<string>();
            Links = new List<CaxTopologyLink>();
            Ports = new List<CaxPortInfo>();
        }

        /// <summary>所有节点记录（导出的主体）。</summary>
        public List<NetworkEndpoint> Endpoints { get; private set; }

        /// <summary>诊断信息：属性读取失败、属性名兜底样本等（排错与跨版本取证用）。</summary>
        public List<string> Diagnostics { get; private set; }

        /// <summary>连接关系（离线 CAx 读得出；在线读取时为空）。</summary>
        public List<CaxTopologyLink> Links { get; private set; }

        /// <summary>端口清单（离线 CAx 读得出；在线读取时为空）。</summary>
        public List<CaxPortInfo> Ports { get; private set; }

        /// <summary>项目里的设备总数。</summary>
        public int DeviceCount { get; set; }

        /// <summary>识别到的网络接口总数。</summary>
        public int InterfaceCount { get; set; }

        /// <summary>节点总数。</summary>
        public int NodeCount { get; set; }

        /// <summary>读到 IP 的节点数。</summary>
        public int IpCount { get; set; }

        /// <summary>参与 IP 冲突的节点数（同一子网内 IP 重复）。</summary>
        public int ConflictCount { get; set; }

        /// <summary>子网总数。</summary>
        public int SubnetCount { get; set; }

        /// <summary>一句话摘要，用于日志与界面状态栏。</summary>
        /// <returns>形如 "设备 12 台 / 接口 15 个 / 节点 18 个 / 有 IP 16 个 / IP 冲突 2 个"。</returns>
        public string BuildSummary()
        {
            string text = string.Format(CultureInfo.InvariantCulture,
                "设备 {0} 台 / 接口 {1} 个 / 节点 {2} 个 / 有 IP {3} 个 / 子网 {4} 个",
                DeviceCount, InterfaceCount, NodeCount, IpCount, SubnetCount);
            if (ConflictCount > 0)
            {
                text += string.Format(CultureInfo.InvariantCulture, " / ⚠ IP 冲突 {0} 个节点", ConflictCount);
            }

            return text;
        }
    }

    /// <summary>
    /// 设备清单的**纯规则集**：IP 冲突判定、IPv4 识别、订货号/固件版本解析、设备去重键。
    ///
    /// 单独成类的理由（架构约定）：这些逻辑对"在线 Openness 读取"和"离线 CAx(.aml) 读取"
    /// 完全一样，而且**不依赖任何 Siemens 类型**，所以可以被两个工具共用、也能脱离 TIA 单测
    /// （两个工具的 --selftest 都是直接断言这些方法）。
    /// </summary>
    public static class DeviceInventoryRules
    {
        /// <summary>点分十进制 IPv4（用于把 IP 与总线站地址分开）。</summary>
        private static readonly Regex Ipv4Pattern = new Regex(
            @"^\s*\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\s*$", RegexOptions.Compiled);

        /// <summary>类型标识里的订货号。</summary>
        private static readonly Regex OrderNumberPattern = new Regex(
            @"OrderNumber\s*:\s*([^/]+)", RegexOptions.Compiled);

        /// <summary>类型标识末尾的固件版本。</summary>
        private static readonly Regex FirmwarePattern = new Regex(
            @"/(V[\d.]+)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// IP 冲突检测：**同一子网内**出现的相同非空 IP 视为冲突（跨子网不算冲突）。
        /// 这是工控现场最常见也最容易出事的配置错误，所以单独做成告警列。
        /// </summary>
        /// <param name="result">采集结果（就地写回 Alert 与 ConflictCount）。</param>
        public static void DetectIpConflicts(DeviceInventoryResult result)
        {
            if (result == null)
            {
                return;
            }
            Dictionary<string, Dictionary<string, List<NetworkEndpoint>>> bySubnet =
                new Dictionary<string, Dictionary<string, List<NetworkEndpoint>>>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkEndpoint endpoint in result.Endpoints)
            {
                if (string.IsNullOrEmpty(endpoint.IpAddress))
                {
                    continue;
                }

                string subnet = string.IsNullOrEmpty(endpoint.SubnetName) ? "(未指定子网)" : endpoint.SubnetName;
                Dictionary<string, List<NetworkEndpoint>> byIp;
                if (!bySubnet.TryGetValue(subnet, out byIp))
                {
                    byIp = new Dictionary<string, List<NetworkEndpoint>>(StringComparer.OrdinalIgnoreCase);
                    bySubnet[subnet] = byIp;
                }

                List<NetworkEndpoint> owners;
                if (!byIp.TryGetValue(endpoint.IpAddress, out owners))
                {
                    owners = new List<NetworkEndpoint>();
                    byIp[endpoint.IpAddress] = owners;
                }

                owners.Add(endpoint);
            }

            foreach (KeyValuePair<string, Dictionary<string, List<NetworkEndpoint>>> subnetEntry in bySubnet)
            {
                foreach (KeyValuePair<string, List<NetworkEndpoint>> ipEntry in subnetEntry.Value)
                {
                    if (ipEntry.Value.Count < 2)
                    {
                        continue;
                    }

                    string names = string.Join("、", NamesOf(ipEntry.Value));
                    string alert = "IP 冲突：子网 " + subnetEntry.Key + " 内共有 " + ipEntry.Value.Count
                        + " 个节点使用 " + ipEntry.Key + "（" + names + "）";
                    foreach (NetworkEndpoint endpoint in ipEntry.Value)
                    {
                        endpoint.Alert = alert;
                        result.ConflictCount++;
                    }
                }
            }
        }

        /// <summary>
        /// 是不是点分十进制的 IPv4。用来把 IP 与总线站地址分开。
        /// </summary>
        /// <param name="text">待判定文本。</param>
        /// <returns>是 IPv4 返回 true。</returns>
        public static bool IsIpv4Text(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return Ipv4Pattern.IsMatch(text);
        }

        /// <summary>节点类型里是否提到总线（PROFIBUS / MPI / DP）。</summary>
        /// <param name="nodeType">节点类型文本。</param>
        /// <returns>提到总线返回 true。</returns>
        public static bool MentionsBus(string nodeType)
        {
            if (string.IsNullOrEmpty(nodeType))
            {
                return false;
            }

            return nodeType.IndexOf("Profibus", StringComparison.OrdinalIgnoreCase) >= 0
                || nodeType.IndexOf("Mpi", StringComparison.OrdinalIgnoreCase) >= 0
                || nodeType.IndexOf("Dp", StringComparison.Ordinal) >= 0;
        }

        /// <summary>从类型标识里抠出订货号：`OrderNumber:6ES7 515-2AM01-0AB0/V2.8` → `6ES7 515-2AM01-0AB0`。</summary>
        /// <param name="typeIdentifier">类型标识。</param>
        /// <returns>订货号；没有则空串。</returns>
        public static string ExtractOrderNumber(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier))
            {
                return string.Empty;
            }

            Match match = OrderNumberPattern.Match(typeIdentifier);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        /// <summary>从类型标识里抠出固件版本：`.../V2.8` → `V2.8`。</summary>
        /// <param name="typeIdentifier">类型标识。</param>
        /// <returns>固件版本；没有则空串。</returns>
        public static string ExtractFirmwareVersion(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier))
            {
                return string.Empty;
            }

            Match match = FirmwarePattern.Match(typeIdentifier);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        /// <summary>是不是 GSD（第三方）设备的类型标识（形如 `GSD:GSDML-V2.31-ELCO-…/D`）。</summary>
        /// <param name="typeIdentifier">类型标识。</param>
        /// <returns>是 GSD 返回 true。</returns>
        public static bool IsGsdIdentifier(string typeIdentifier)
        {
            return !string.IsNullOrEmpty(typeIdentifier)
                && typeIdentifier.StartsWith("GSD", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从类型标识里解析**供应商**：
        ///   · 订货号 / 系统设备（`OrderNumber:…`、`System:Device.…`）→ `Siemens`
        ///   · GSD 设备 → 从 GSDML 标识里取厂商段：
        ///     `GSD:GSDML-V2.31-ELCO-IOLINKMASTER-20210517.XML/D` → `ELCO`
        ///   · 认不出来 → 空串（不猜）
        ///
        /// 解析规则（GSDML 命名约定）：`GSDML-V<版本>-<厂商>-<设备族>-<日期>.xml`，
        /// 版本段以 `V` 开头，紧随其后的第一段就是厂商。
        /// </summary>
        /// <param name="typeIdentifier">类型标识。</param>
        /// <returns>供应商标识；认不出返回空串。</returns>
        public static string ExtractVendor(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier))
            {
                return string.Empty;
            }

            string text = typeIdentifier.Trim();
            if (text.StartsWith("OrderNumber", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
            {
                return "Siemens";
            }

            int gsdIndex = text.IndexOf("GSDML-", StringComparison.OrdinalIgnoreCase);
            if (gsdIndex < 0)
            {
                return string.Empty;
            }

            string rest = text.Substring(gsdIndex + "GSDML-".Length);
            int cut = rest.IndexOf('/');
            if (cut >= 0)
            {
                rest = rest.Substring(0, cut);
            }

            string[] parts = rest.Split('-');
            // parts[0] = 版本（V2.31），parts[1] = 厂商
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                {
                    continue;
                }

                if (parts[i].StartsWith("V", StringComparison.OrdinalIgnoreCase))
                {
                    // 这是版本段 → 厂商在它后面
                    return i + 1 < parts.Length ? parts[i + 1] : string.Empty;
                }

                return parts[i];
            }

            return string.Empty;
        }

        /// <summary>
        /// 设备去重键：优先用设备名（项目内唯一），名为空时退回"类型标识 + 出现序号"。
        ///
        /// 为什么需要它：设备清单有三个来源（项目根 / 未分组的设备 / 用户设备组），
        /// 三处可能重叠，合并时必须按设备名去重，否则主 PLC 会被算两次。
        /// 调用方用**大小写不敏感**的字典承载。
        /// </summary>
        /// <param name="deviceName">设备名。</param>
        /// <param name="typeIdentifier">设备类型标识。</param>
        /// <param name="ordinal">该次出现的序号（1 起；仅当设备名为空时参与构键）。</param>
        /// <returns>去重键。</returns>
        public static string BuildDeviceKey(string deviceName, string typeIdentifier, int ordinal)
        {
            if (!string.IsNullOrEmpty(deviceName))
            {
                return "name:" + deviceName.Trim();
            }

            if (!string.IsNullOrEmpty(typeIdentifier))
            {
                return "type:" + typeIdentifier.Trim() + "#" + ordinal.ToString(CultureInfo.InvariantCulture);
            }

            return "anon:#" + ordinal.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>把记录列表转成"设备名/接口名"的可读名字数组。</summary>
        private static string[] NamesOf(List<NetworkEndpoint> endpoints)
        {
            List<string> names = new List<string>();
            foreach (NetworkEndpoint endpoint in endpoints)
            {
                names.Add(endpoint.DeviceName + "/" + endpoint.DeviceItemName);
            }

            return names.ToArray();
        }
    }
}
