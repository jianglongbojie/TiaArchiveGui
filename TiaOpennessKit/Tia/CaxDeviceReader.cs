using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// 离线读 CAx（AutomationML / CAEX 2.15，`*.aml`）导出文件 —— **不装 TIA、不装 Openness 也能读**。
    ///
    /// 为什么要它（用户 2026-09-23 提出）：TIA 的「项目 → 导出 → CAx 数据」导出的 .aml
    /// 里已经带了设备清单的全部关键信息，读它比开 TIA 快两个数量级，而且能在没装 TIA 的电脑上跑。
    /// 对标 PRONETA 的离线设备表，并且给得更多（供应商、IO 控制器、模块/端口、连接关系、IP 冲突）。
    ///
    /// ★ 数据事实（实测 `OP10_V20_V21.aml`：899 KB / 664 个 InternalElement / 37 条连线）：
    ///   · 结构与 Openness 同构：项目 → 子网(`PN/IE_1`) +「未分组的设备」(`DeviceUserFolder`)
    ///     → 设备(`Device`) → 机架(`Rack`) → 站名(CPU/HeadModule，如 `OP10` / `OP101RM111`)
    ///     → 模块 + `Interface`(Label X1) → 节点(`IE1`，含 `NetworkAddress`/`SubnetMask`) → 端口(P1R/P2L)
    ///   · 连线只有两类：`节点→子网`（19 条）与 `接口→IO 系统`（18 条）；
    ///     **没有端口↔端口接线**（所以给不出「谁的口接到谁的口」，只能给端口清单 + 连接关系）
    ///   · **没有 MAC 地址**（PRONETA 那串 `00-00-00-00-00-xx` 是它自己生成的占位，不是工程数据）
    ///
    /// 本类不依赖任何 Siemens 类型、不启动 TIA，编译期与运行期都只需要 .NET 的 XML 库。
    /// </summary>
    public sealed class CaxDeviceReader
    {
        // AutomationML 的角色类短名（RefRoleClassPath 形如
        // AutomationProjectConfigurationRoleClassLib/Device、…EthernetRoleClassLib/NodeEthernet）
        private const string RoleDevice = "Device";
        private const string RoleDeviceItem = "DeviceItem";
        private const string RoleDeviceUserFolder = "DeviceUserFolder";
        private const string RoleSubnet = "Subnet";
        private const string RoleNode = "Node";
        private const string RoleNodeEthernet = "NodeEthernet";
        private const string RoleInterface = "CommunicationInterface";
        private const string RolePort = "CommunicationPort";
        private const string RoleIoSystem = "IoSystem";

        /// <summary>递归深度上限（防御 CAEX 里可能出现的怪结构）。</summary>
        private const int MaxDepth = 30;

        private readonly Logger _logger;

        // ── 一次 Read 用的临时索引（同一个实例不并发使用）────────────────────────
        private readonly Dictionary<string, XElement> _byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        private readonly Dictionary<XElement, NetworkEndpoint> _byNodeElement = new Dictionary<XElement, NetworkEndpoint>();
        private readonly Dictionary<XElement, string> _stationByDevice = new Dictionary<XElement, string>();
        private readonly Dictionary<XElement, string> _ioControllerByDevice = new Dictionary<XElement, string>();
        private readonly List<string> _deviceTexts = new List<string>();

        /// <summary>构造函数。</summary>
        /// <param name="logger">日志器（可为 null，内部退化为不啰嗦的默认日志）。</param>
        public CaxDeviceReader(Logger logger)
        {
            // 共享内核里没有"空日志器"，所以缺省给一个不啰嗦的（与 TiaDeviceInventory 一致）
            _logger = logger ?? new Logger(false);
        }

        /// <summary>
        /// 读一个 CAx（.aml）文件，产出与在线读取**同一套模型**（<see cref="DeviceInventoryResult"/>）。
        /// </summary>
        /// <param name="amlFilePath">.aml 文件路径。</param>
        /// <returns>采集结果（含端点、连接关系、端口清单与诊断）。</returns>
        /// <exception cref="ToolException">路径为空 / 文件不存在 / 不是 AutomationML 时抛出。</exception>
        public DeviceInventoryResult Read(string amlFilePath)
        {
            if (string.IsNullOrEmpty(amlFilePath))
            {
                throw new ToolException(ExitCodes.Usage, "还没有指定 CAx（.aml）文件。");
            }

            FileInfo file;
            try
            {
                file = new FileInfo(Path.GetFullPath(amlFilePath));
            }
            catch (Exception ex)
            {
                throw new ToolException(ExitCodes.Usage, "CAx 文件路径无效：" + ex.Message, ex);
            }

            if (!file.Exists)
            {
                throw new ToolException(ExitCodes.InputOutput, "CAx 文件不存在：" + file.FullName);
            }

            _logger.Section("读取 CAx 数据（离线，不需要 TIA）");
            _logger.Info("文件：" + file.FullName);
            _logger.Info("大小：" + SizeFormat.Format(file.Length));

            XDocument document;
            try
            {
                using (_logger.Measure("解析 XML"))
                {
                    document = XDocument.Load(file.FullName, LoadOptions.None);
                }
            }
            catch (Exception ex)
            {
                throw new ToolException(ExitCodes.InputOutput,
                    "CAx 文件解析失败（不是有效的 XML/AutomationML）：" + ex.Message, ex);
            }

            ResetIndexes();
            DeviceInventoryResult result = new DeviceInventoryResult();

            // ── ① 抓出全部 InternalElement / ExternalInterface / InternalLink，并建 ID → 元素索引
            List<XElement> internalElements = new List<XElement>();
            List<XElement> internalLinks = new List<XElement>();
            foreach (XElement element in document.Descendants())
            {
                if (IsElement(element, "InternalElement"))
                {
                    internalElements.Add(element);
                    RememberId(element);
                }
                else if (IsElement(element, "ExternalInterface"))
                {
                    RememberId(element);
                }
                else if (IsElement(element, "InternalLink"))
                {
                    internalLinks.Add(element);
                }
            }

            // ── ② 分类：子网 / IO 系统 / 设备
            List<XElement> subnets = new List<XElement>();
            List<XElement> ioSystems = new List<XElement>();
            List<XElement> devices = new List<XElement>();
            foreach (XElement element in internalElements)
            {
                if (HasRole(element, RoleSubnet))
                {
                    subnets.Add(element);
                }
                else if (HasRole(element, RoleIoSystem))
                {
                    ioSystems.Add(element);
                }
                else if (HasRole(element, RoleDevice))
                {
                    devices.Add(element);
                }
            }

            result.SubnetCount = subnets.Count;
            WriteWriterHeader(document);
            _logger.Info("CAx 里的子网：" + (subnets.Count == 0 ? "（无）" : DescribeSubnets(subnets)));

            if (devices.Count == 0)
            {
                throw new ToolException(ExitCodes.InputOutput,
                    "这个文件里没有找到任何 AutomationML 设备（Device 角色）："
                    + "请确认导出的是 TIA 的「项目 → 导出 → CAx 数据(.aml)」。");
            }

            // ── ③ 逐台设备：站名 → 接口 → 节点（一个节点一行）；顺带收集端口
            int deviceIndex = 0;
            foreach (XElement device in devices)
            {
                deviceIndex++;
                string container = FindContainerName(device);
                string station = ReadDeviceStation(device);
                _stationByDevice[device] = station;
                _deviceTexts.Add((string.IsNullOrEmpty(container) ? "(项目根)" : container) + "：" + station);

                int before = result.Endpoints.Count;
                try
                {
                    VisitDevice(device, deviceIndex, station, result);
                }
                catch (Exception ex)
                {
                    // 单台设备出错不能毁掉整份表（与在线读取同样的策略）
                    result.Diagnostics.Add("设备 " + station + " 解析失败：" + ex.GetType().Name + "：" + ex.Message);
                    _logger.Warning("设备 " + station + " 解析失败：" + ex.Message);
                }

                if (result.Endpoints.Count == before)
                {
                    NetworkEndpoint placeholder = NewEndpoint(result, deviceIndex, station, device, null, null);
                    placeholder.Role = "Device";
                    placeholder.Remark = "未组态网络（CAx 里该设备没有任何节点）";
                    result.Endpoints.Add(placeholder);
                }
            }

            result.DeviceCount = devices.Count;
            _logger.Debug("设备清单：" + string.Join("、", _deviceTexts.ToArray()));

            // ── ④ 连线：节点→子网、接口→IO 系统（子网名 / IO 控制器在这里解析）
            ResolveLinks(internalLinks, subnets, result);

            // ── ⑤ 收尾：角色、供应商、IO 控制器回填 + 诊断 + IP 冲突
            FinishEndpoints(result);
            WriteSourceDiagnostic(devices, result);
            WriteCaxFactsDiagnostic(ioSystems, result);
            DeviceInventoryRules.DetectIpConflicts(result);

            _logger.Ok("采集完成：" + result.BuildSummary());
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  设备 / 设备项遍历
        // ══════════════════════════════════════════════════════════════════

        /// <summary>遍历一台设备：接口 → 节点（每个节点一行），并把端口记进端口清单。</summary>
        private void VisitDevice(XElement device, int deviceIndex, string station, DeviceInventoryResult result)
        {
            XElement head = FindHeadItem(device);
            string headTypeId = AttributeValue(head, "TypeIdentifier");
            string headTypeName = AttributeValue(head, "TypeName");

            // 这台设备自己持有一个 IoSystem 元素 → 它就是 IO 控制器
            string role = FindOwned(device, RoleIoSystem) != null ? "Controller" : "Device";

            foreach (XElement item in AllInternalElements(device))
            {
                if (HasRole(item, RoleInterface))
                {
                    result.InterfaceCount++;
                }

                if (HasRole(item, RolePort))
                {
                    CaxPortInfo port = new CaxPortInfo();
                    port.Index = result.Ports.Count + 1;
                    port.DeviceName = station;
                    port.ItemPath = BuildPath(station, FindOwning(item, RoleInterface))
                        + "/" + Attr(item, "Name");
                    port.PortName = Attr(item, "Name");
                    port.Label = AttributeValue(item, "Label");
                    port.PositionNumber = AttributeValue(item, "PositionNumber");
                    result.Ports.Add(port);
                }

                if (!HasRole(item, RoleNode))
                {
                    continue;
                }

                XElement iface = FindOwning(item, RoleInterface);
                NetworkEndpoint endpoint = NewEndpoint(result, deviceIndex, station, device, iface, item);
                endpoint.NodeName = Attr(item, "Name");
                endpoint.Role = role;
                endpoint.DeviceTypeIdentifier = headTypeId;
                endpoint.IsGsd = DeviceInventoryRules.IsGsdIdentifier(headTypeId);
                endpoint.Vendor = DeviceInventoryRules.ExtractVendor(headTypeId);
                endpoint.OrderNumber = ReadAttrOrParse(head, "OrderNumber", headTypeId, true);
                endpoint.FirmwareVersion = ReadAttrOrParse(head, "FirmwareVersion", headTypeId, false);
                if (string.IsNullOrEmpty(endpoint.DeviceItemTypeIdentifier))
                {
                    endpoint.DeviceItemTypeIdentifier = headTypeName;
                }

                string networkAddress = AttributeValue(item, "NetworkAddress");
                if (DeviceInventoryRules.IsIpv4Text(networkAddress))
                {
                    endpoint.IpAddress = networkAddress.Trim();
                    endpoint.SubnetMask = AttributeValue(item, "SubnetMask");
                }
                else
                {
                    // 非以太网节点（PROFIBUS / MPI）：AML 里给的是总线站地址，单独一列，别混进 IP
                    string bus = networkAddress;
                    if (string.IsNullOrEmpty(bus))
                    {
                        bus = AttributeValue(item, "Address");
                    }

                    if (!string.IsNullOrEmpty(bus) && !DeviceInventoryRules.IsIpv4Text(bus))
                    {
                        endpoint.BusAddress = bus.Trim();
                    }
                }

                endpoint.IpAssignment = AttributeValue(item, "IpProtocolSelection");
                endpoint.RouterAddress = AttributeValue(item, "RouterAddress");
                endpoint.NodeType = ReadNodeType(item);
                endpoint.InterfaceType = ReadInterfaceType(iface, item);
                endpoint.Remark = BuildRemark(endpoint);

                _byNodeElement[item] = endpoint;

                result.NodeCount++;
                if (endpoint.HasIp)
                {
                    result.IpCount++;
                }

                result.Endpoints.Add(endpoint);
            }
        }

        /// <summary>新建一行（设备 / 接口 / 节点层面的公共字段）。</summary>
        private NetworkEndpoint NewEndpoint(DeviceInventoryResult result, int deviceIndex, string station,
            XElement device, XElement iface, XElement node)
        {
            NetworkEndpoint endpoint = new NetworkEndpoint();
            endpoint.Index = result.Endpoints.Count + 1;
            endpoint.DeviceIndex = deviceIndex;
            endpoint.DeviceName = station;
            endpoint.DeviceItemName = iface == null ? string.Empty : ReadInterfaceName(iface);
            endpoint.DeviceItemTypeIdentifier = iface == null ? string.Empty : AttributeValue(iface, "TypeIdentifier");
            endpoint.InterfaceName = iface == null ? string.Empty : ReadInterfaceName(iface);
            endpoint.Slot = NormalizeSlot(iface == null ? string.Empty : AttributeValue(iface, "PositionNumber"));
            endpoint.DeviceItemPath = node == null
                ? "/" + station
                : BuildPath(station, iface) + "/" + Attr(node, "Name");
            return endpoint;
        }

        // ══════════════════════════════════════════════════════════════════
        //  连线（节点→子网 / 接口→IO 系统）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 解析 InternalLink：CAx 里只有两类连线，正好覆盖「设备挂在哪个子网」与「谁是 IO 控制器」。
        /// 端口↔端口接线**不在** CAx 数据里（所以拓扑表只能给连接关系 + 端口清单）。
        /// </summary>
        private void ResolveLinks(List<XElement> internalLinks, List<XElement> subnets,
            DeviceInventoryResult result)
        {
            int subnetLinks = 0;
            int ioSystemLinks = 0;

            foreach (XElement link in internalLinks)
            {
                XElement ownerA;
                XElement ownerB;
                string endPointA;
                string endPointB;
                if (!ResolveLinkSides(link, out ownerA, out endPointA, out ownerB, out endPointB))
                {
                    continue;
                }

                if (IsSubnetEndPoint(endPointA) || IsSubnetEndPoint(endPointB))
                {
                    bool sideAIsSubnet = IsSubnetEndPoint(endPointA);
                    XElement subnet = sideAIsSubnet ? ownerA : ownerB;
                    XElement node = sideAIsSubnet ? ownerB : ownerA;
                    ApplySubnet(Attr(subnet, "Name"), AttributeValue(subnet, "Type"), node, result);
                    subnetLinks++;
                    continue;
                }

                if (IsIoSystemEndPoint(endPointA) || IsIoSystemEndPoint(endPointB))
                {
                    bool sideAIsIoSystem = IsIoSystemEndPoint(endPointA);
                    XElement ioSystem = sideAIsIoSystem ? ownerA : ownerB;
                    XElement ioDevicePart = sideAIsIoSystem ? ownerB : ownerA;
                    XElement ioDevice = FindOwning(ioDevicePart, RoleDevice);
                    XElement controller = FindOwning(ioSystem, RoleDevice);
                    string ioSystemName = Attr(ioSystem, "Name");
                    string controllerStation = controller == null ? string.Empty : StationOf(controller);

                    if (ioDevice != null)
                    {
                        // 控制器关系先记账，等所有连线解析完在 FinishEndpoints 里统一回填
                        // （这条连线的一端是「接口」而不是节点，当场回填在端点上找不到对应行）
                        _ioControllerByDevice[ioDevice] = controllerStation;

                        CaxTopologyLink ioLink = new CaxTopologyLink();
                        ioLink.Index = result.Links.Count + 1;
                        ioLink.Kind = "IO 系统";
                        ioLink.SideADevice = StationOf(ioDevice);
                        ioLink.SideAItem = BuildPath(ioLink.SideADevice, ioDevicePart);
                        ioLink.SideAEndPoint = Attr(ioDevicePart, "Name");
                        ioLink.SideAPort = AttributeValue(ioDevicePart, "Label");
                        ioLink.SideB = ioSystemName
                            + (string.IsNullOrEmpty(controllerStation)
                                ? string.Empty
                                : "（控制器：" + controllerStation + "）");
                        ioLink.Remark = "接口→IO 系统（由 CAx 连线推出）";
                        result.Links.Add(ioLink);
                    }

                    ioSystemLinks++;
                }
            }

            _logger.Info("CAx 连接关系：节点→子网 " + subnetLinks.ToString(CultureInfo.InvariantCulture)
                + " 条；接口→IO 系统 " + ioSystemLinks.ToString(CultureInfo.InvariantCulture) + " 条");
            _logger.Info("CAx 端口清单：" + result.Ports.Count.ToString(CultureInfo.InvariantCulture)
                + " 个（CAx 不含端口↔端口接线，给不出「谁的口接到谁的口」）");
            _logger.Info("CAx 子网：" + subnets.Count.ToString(CultureInfo.InvariantCulture) + " 个");
        }

        /// <summary>把「节点属于哪个子网」写回端点，并产出一条连接关系。</summary>
        private void ApplySubnet(string subnetName, string subnetType, XElement nodeElement,
            DeviceInventoryResult result)
        {
            NetworkEndpoint endpoint;
            if (nodeElement == null || !_byNodeElement.TryGetValue(nodeElement, out endpoint))
            {
                return;
            }

            endpoint.SubnetName = subnetName;
            endpoint.SubnetType = subnetType;

            CaxTopologyLink link = new CaxTopologyLink();
            link.Index = result.Links.Count + 1;
            link.Kind = "子网";
            link.SideADevice = endpoint.DeviceName;
            link.SideAItem = endpoint.DeviceItemPath;
            link.SideAEndPoint = endpoint.NodeName;
            link.SideB = subnetName + (string.IsNullOrEmpty(subnetType) ? string.Empty : "（" + subnetType + "）");
            link.Remark = endpoint.IpAddress;
            result.Links.Add(link);
        }

        /// <summary>收尾：把 IO 控制器回填到端点、补角色与供应商兜底。</summary>
        private void FinishEndpoints(DeviceInventoryResult result)
        {
            foreach (NetworkEndpoint endpoint in result.Endpoints)
            {
                string controller = ControllerFor(endpoint.DeviceName);
                if (!string.IsNullOrEmpty(controller))
                {
                    endpoint.IoController = controller;
                    endpoint.IoSystem = FindIoSystemName(result, controller);
                }

                if (string.IsNullOrEmpty(endpoint.Role))
                {
                    endpoint.Role = string.IsNullOrEmpty(controller) ? "Device" : "Device";
                }

                if (string.IsNullOrEmpty(endpoint.Vendor))
                {
                    endpoint.Vendor = DeviceInventoryRules.ExtractVendor(endpoint.DeviceTypeIdentifier);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  辅助
        // ══════════════════════════════════════════════════════════════════

        /// <summary>把设备名（站名）映射到 IO 控制器名（没有则空串）。</summary>
        private string ControllerFor(string station)
        {
            foreach (KeyValuePair<XElement, string> pair in _ioControllerByDevice)
            {
                string deviceStation;
                if (_stationByDevice.TryGetValue(pair.Key, out deviceStation)
                    && string.Equals(deviceStation, station, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return string.Empty;
        }

        /// <summary>按控制器名回查 IO 系统名（CAx 里 IO 系统挂在控制器设备下）。</summary>
        private static string FindIoSystemName(DeviceInventoryResult result, string controllerStation)
        {
            if (string.IsNullOrEmpty(controllerStation) || result == null)
            {
                return string.Empty;
            }

            foreach (CaxTopologyLink link in result.Links)
            {
                if (!string.Equals(link.Kind, "IO 系统", StringComparison.Ordinal) || link.SideB == null)
                {
                    continue;
                }

                if (link.SideB.IndexOf(controllerStation, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int cut = link.SideB.IndexOf('（');
                return cut > 0 ? link.SideB.Substring(0, cut) : link.SideB;
            }

            return string.Empty;
        }

        /// <summary>写入「设备来源」诊断（与在线读取同一套口径）。</summary>
        private void WriteSourceDiagnostic(List<XElement> devices, DeviceInventoryResult result)
        {
            int root = 0;
            int grouped = 0;
            foreach (XElement device in devices)
            {
                if (string.IsNullOrEmpty(FindContainerName(device)))
                {
                    root++;
                }
                else
                {
                    grouped++;
                }
            }

            _logger.Info("设备来源（CAx）：项目根 " + root.ToString(CultureInfo.InvariantCulture)
                + " 台；未分组的设备/设备组 " + grouped.ToString(CultureInfo.InvariantCulture)
                + " 台；共 " + devices.Count.ToString(CultureInfo.InvariantCulture) + " 台");
            result.Diagnostics.Add("设备来源：项目根 " + root.ToString(CultureInfo.InvariantCulture)
                + " 台；未分组的设备/设备组 " + grouped.ToString(CultureInfo.InvariantCulture)
                + " 台；共 " + devices.Count.ToString(CultureInfo.InvariantCulture) + " 台（离线 CAx）");
        }

        /// <summary>把 CAx 数据的「能读什么 / 读不到什么」写成诊断，免得被误当成数据缺了。</summary>
        private void WriteCaxFactsDiagnostic(List<XElement> ioSystems, DeviceInventoryResult result)
        {
            List<string> ioTexts = new List<string>();
            foreach (XElement ioSystem in ioSystems)
            {
                XElement controller = FindOwning(ioSystem, RoleDevice);
                ioTexts.Add(Attr(ioSystem, "Name")
                    + (controller == null ? string.Empty : "，控制器：" + StationOf(controller)));
            }

            result.Diagnostics.Add("CAx IO 系统："
                + (ioTexts.Count == 0 ? "（无）" : string.Join("、", ioTexts.ToArray()))
                + "；连接关系 " + result.Links.Count.ToString(CultureInfo.InvariantCulture)
                + " 条、端口 " + result.Ports.Count.ToString(CultureInfo.InvariantCulture) + " 个");
            result.Diagnostics.Add("CAx 数据里**没有端口↔端口接线**，也**没有 MAC 地址**"
                + "（PRONETA 显示的 MAC 是它自己生成的占位序列），故这两项不输出。");
        }

        /// <summary>写「是谁导出的」（排错时能看出 TIA 版本）。</summary>
        private void WriteWriterHeader(XDocument document)
        {
            string writer = string.Empty;
            string version = string.Empty;
            foreach (XElement element in document.Descendants())
            {
                if (IsElement(element, "WriterName"))
                {
                    writer = element.Value.Trim();
                }
                else if (IsElement(element, "WriterVersion"))
                {
                    version = element.Value.Trim();
                }
            }

            if (!string.IsNullOrEmpty(writer))
            {
                _logger.Info("导出者：" + writer
                    + (string.IsNullOrEmpty(version) ? string.Empty : "（" + version + "）"));
            }
        }

        /// <summary>子网清单的可读描述。</summary>
        private static string DescribeSubnets(List<XElement> subnets)
        {
            List<string> texts = new List<string>();
            foreach (XElement subnet in subnets)
            {
                texts.Add(Attr(subnet, "Name") + "（" + AttributeValue(subnet, "Type") + "）");
            }

            return string.Join("、", texts.ToArray());
        }

        /// <summary>读设备「站在哪个组」（最近的外层 DeviceUserFolder 名，如「未分组的设备」）。</summary>
        private static string FindContainerName(XElement device)
        {
            XElement container = FindOwning(device, RoleDeviceUserFolder);
            return container == null ? string.Empty : Attr(container, "Name");
        }

        /// <summary>设备的显示名：机架下的头模块（CPU / HeadModule）名 —— 就是 TIA 里看到的站名。</summary>
        private string ReadDeviceStation(XElement device)
        {
            XElement head = FindHeadItem(device);
            if (head != null)
            {
                string name = Attr(head, "Name");
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return Attr(device, "Name");
        }

        /// <summary>设备的站名（找不到时退回容器名，只用于诊断）。</summary>
        private string StationOf(XElement device)
        {
            string station;
            if (device != null && _stationByDevice.TryGetValue(device, out station))
            {
                return station;
            }

            return device == null ? string.Empty : Attr(device, "Name");
        }

        /// <summary>找设备的「机架 → 头模块」：机架是设备的第一个设备项，头模块是机架下第一个带 DeviceItemType 的子项。</summary>
        private static XElement FindHeadItem(XElement device)
        {
            XElement rack = null;
            foreach (XElement child in device.Elements())
            {
                if (IsElement(child, "InternalElement") && HasRole(child, RoleDeviceItem))
                {
                    rack = child;
                    break;
                }
            }

            if (rack == null)
            {
                return null;
            }

            XElement fallback = null;
            foreach (XElement child in rack.Elements())
            {
                if (!IsElement(child, "InternalElement") || !HasRole(child, RoleDeviceItem))
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = child;
                }

                if (!string.IsNullOrEmpty(AttributeValue(child, "DeviceItemType")))
                {
                    return child;
                }
            }

            return fallback;
        }

        /// <summary>接口名：优先 Label（X1 / X2），退回 Name（Interface / PROFINET 接口_1）。</summary>
        private static string ReadInterfaceName(XElement iface)
        {
            if (iface == null)
            {
                return string.Empty;
            }

            string label = AttributeValue(iface, "Label");
            return string.IsNullOrEmpty(label) ? Attr(iface, "Name") : label;
        }

        /// <summary>接口类型：接口上的 Type 属性，退回节点上的 Type 属性。</summary>
        private static string ReadInterfaceType(XElement iface, XElement node)
        {
            string type = AttributeValue(iface, "Type");
            if (string.IsNullOrEmpty(type))
            {
                type = AttributeValue(node, "Type");
            }

            return type;
        }

        /// <summary>节点类型：以太网节点按角色判定，其余用 Type 属性（Ethernet / Profibus 等）。</summary>
        private static string ReadNodeType(XElement node)
        {
            if (HasRole(node, RoleNodeEthernet))
            {
                return "Ethernet";
            }

            return AttributeValue(node, "Type");
        }

        /// <summary>订货号 / 固件版本：先读同名属性，读不到再从类型标识里解析（与在线读取同一套规则）。</summary>
        private static string ReadAttrOrParse(XElement owner, string attributeName, string typeIdentifier,
            bool orderNumber)
        {
            string value = AttributeValue(owner, attributeName);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            return orderNumber
                ? DeviceInventoryRules.ExtractOrderNumber(typeIdentifier)
                : DeviceInventoryRules.ExtractFirmwareVersion(typeIdentifier);
        }

        /// <summary>按「为什么没有 IP」给备注（与在线读取同一套口径）。</summary>
        private static string BuildRemark(NetworkEndpoint endpoint)
        {
            StringBuilder remark = new StringBuilder();
            if (string.IsNullOrEmpty(endpoint.SubnetName))
            {
                remark.Append("未连接到子网；");
            }

            if (!string.IsNullOrEmpty(endpoint.IpAddress))
            {
                return TrimEnd(remark.ToString());
            }

            if (!string.IsNullOrEmpty(endpoint.BusAddress))
            {
                remark.Append("非 IP 节点（" + endpoint.NodeType + "），Address 是总线地址 "
                    + endpoint.BusAddress + "；");
            }
            else if (DeviceInventoryRules.MentionsBus(endpoint.NodeType))
            {
                remark.Append("PROFIBUS 节点（本来就没有 IP）；");
            }
            else
            {
                remark.Append("未组态 IP（可能未分配子网、或该节点不通过 IP 寻址）；");
            }

            return TrimEnd(remark.ToString());
        }

        /// <summary>去掉结尾的分号与空白。</summary>
        private static string TrimEnd(string text)
        {
            return text == null ? string.Empty : text.TrimEnd('；', ';', ' ');
        }

        /// <summary>位置号：0 与空都不写（CAx 里机架是 0、接口常是 1）。</summary>
        private static string NormalizeSlot(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim() == "0")
            {
                return string.Empty;
            }

            return text.Trim();
        }

        /// <summary>拼设备项路径：/站名/接口名（接口为空时只到站名）。</summary>
        private static string BuildPath(string station, XElement item)
        {
            string path = "/" + station;
            if (item == null)
            {
                return path;
            }

            string name = ReadInterfaceName(item);
            if (!string.IsNullOrEmpty(name))
            {
                path = path + "/" + name;
            }

            return path;
        }

        /// <summary>端点名是不是「连到子网」。</summary>
        private static bool IsSubnetEndPoint(string endPointName)
        {
            return !string.IsNullOrEmpty(endPointName)
                && endPointName.EndsWith("Subnet", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>端点名是不是「连到 IO 系统」。</summary>
        private static bool IsIoSystemEndPoint(string endPointName)
        {
            return !string.IsNullOrEmpty(endPointName)
                && endPointName.EndsWith("IoSystem", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把一条连线的两端解析成「宿主元素 + 端点名」。</summary>
        private bool ResolveLinkSides(XElement link, out XElement ownerA, out string endPointA,
            out XElement ownerB, out string endPointB)
        {
            ownerA = null;
            ownerB = null;
            endPointA = string.Empty;
            endPointB = string.Empty;

            XElement elementA;
            XElement elementB;
            if (!ResolveSide(Attr(link, "RefPartnerSideA"), out elementA, out endPointA)
                || !ResolveSide(Attr(link, "RefPartnerSideB"), out elementB, out endPointB))
            {
                return false;
            }

            ownerA = OwnerOf(elementA);
            ownerB = OwnerOf(elementB);
            return ownerA != null || ownerB != null;
        }

        /// <summary>把 `GUID:LogicalEndPoint_Node` 解析成「端点元素 + 端点名」。</summary>
        private bool ResolveSide(string reference, out XElement element, out string endPointName)
        {
            element = null;
            endPointName = string.Empty;
            if (string.IsNullOrEmpty(reference))
            {
                return false;
            }

            int separator = reference.IndexOf(':');
            if (separator <= 0 || separator >= reference.Length - 1)
            {
                return false;
            }

            string id = reference.Substring(0, separator);
            endPointName = reference.Substring(separator + 1);
            return _byId.TryGetValue(id, out element);
        }

        /// <summary>端点元素所属的 InternalElement（端点本身是 ExternalInterface，父元素才是宿主）。</summary>
        private static XElement OwnerOf(XElement element)
        {
            if (element == null)
            {
                return null;
            }

            return IsElement(element, "ExternalInterface") ? element.Parent : element;
        }

        /// <summary>把 ID 记进索引（同 ID 只记一次）。</summary>
        private void RememberId(XElement element)
        {
            string id = Attr(element, "ID");
            if (!string.IsNullOrEmpty(id) && !_byId.ContainsKey(id))
            {
                _byId.Add(id, element);
            }
        }

        /// <summary>递归取一个元素下的全部 InternalElement（不含自身）。</summary>
        private static List<XElement> AllInternalElements(XElement root)
        {
            List<XElement> items = new List<XElement>();
            WalkItems(root, items, 0);
            return items;
        }

        private static void WalkItems(XElement parent, List<XElement> items, int depth)
        {
            if (parent == null || depth > MaxDepth)
            {
                return;
            }

            foreach (XElement child in parent.Elements())
            {
                if (!IsElement(child, "InternalElement"))
                {
                    continue;
                }

                items.Add(child);
                WalkItems(child, items, depth + 1);
            }
        }

        /// <summary>沿 Parent 向上找「带指定角色」的祖先（不含自身）。</summary>
        private static XElement FindOwning(XElement element, string roleSuffix)
        {
            XElement current = element == null ? null : element.Parent;
            int depth = 0;
            while (current != null && IsElement(current, "InternalElement") && depth < MaxDepth)
            {
                if (HasRole(current, roleSuffix))
                {
                    return current;
                }

                current = current.Parent;
                depth++;
            }

            return null;
        }

        /// <summary>在元素自己的子树里找「带指定角色」的元素（含自身）。</summary>
        private static XElement FindOwned(XElement element, string roleSuffix)
        {
            if (element == null)
            {
                return null;
            }

            if (HasRole(element, roleSuffix))
            {
                return element;
            }

            foreach (XElement child in element.Descendants())
            {
                if (IsElement(child, "InternalElement") && HasRole(child, roleSuffix))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>元素是否带某个角色（AutomationML 的 RefRoleClassPath 以 `/角色名` 结尾）。</summary>
        private static bool HasRole(XElement element, string roleSuffix)
        {
            if (element == null || string.IsNullOrEmpty(roleSuffix))
            {
                return false;
            }

            foreach (XElement child in element.Elements())
            {
                if (!IsElement(child, "SupportedRoleClass"))
                {
                    continue;
                }

                string path = Attr(child, "RefRoleClassPath");
                if (path.EndsWith("/" + roleSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>是不是某个名字的 CAEX 元素（**不区分命名空间**：CAEX 2.15 里常常没有默认命名空间）。</summary>
        private static bool IsElement(XElement element, string localName)
        {
            return element != null
                && string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);
        }

        /// <summary>读元素自身的 XML 属性（如 ID / Name / RefPartnerSideA）。</summary>
        private static string Attr(XElement element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            XAttribute attribute = element.Attribute(name);
            return attribute == null || attribute.Value == null ? string.Empty : attribute.Value.Trim();
        }

        /// <summary>
        /// 读 CAEX 的工程属性：`&lt;Attribute Name="X"&gt;&lt;Value&gt;y&lt;/Value&gt;&lt;/Attribute&gt;`。
        /// 只在直接子级里找（深度固定为 1），不会误取嵌套设备项的同名属性。
        /// </summary>
        private static string AttributeValue(XElement owner, string name)
        {
            if (owner == null || string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            foreach (XElement child in owner.Elements())
            {
                if (!IsElement(child, "Attribute")
                    || !string.Equals(Attr(child, "Name"), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (XElement value in child.Elements())
                {
                    if (IsElement(value, "Value"))
                    {
                        return value.Value == null ? string.Empty : value.Value.Trim();
                    }
                }

                return string.Empty;
            }

            return string.Empty;
        }

        /// <summary>清空一次 Read 用的临时索引（同一个实例被复用时不串味）。</summary>
        private void ResetIndexes()
        {
            _byId.Clear();
            _byNodeElement.Clear();
            _stationByDevice.Clear();
            _ioControllerByDevice.Clear();
            _deviceTexts.Clear();
        }
    }
}
