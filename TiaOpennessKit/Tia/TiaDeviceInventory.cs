using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// 读取 TIA 项目里全部网络设备与 IP。
    ///
    /// 只读实现：全程只用 GetXxx / 读属性，**不做任何 SetAttribute / 修改 / 保存** ——
    /// 这是本工具对项目的承诺（用户确认过：严格只读，绝不 Save）。
    ///
    /// 跨版本要点（V16~V21 已逐版本核对元数据，结构一致）：
    ///   - 类型都从"绑定 Openness 时加载的那个主程序集"里取，不额外依赖模块 DLL；
    ///   - `GetService&lt;NetworkInterface&gt;()` 只有泛型版本，靠 OpennessApi 的反射辅助调用；
    ///   - **IP / 掩码是动态属性**（Address / SubnetMask），任何版本都没有 IPAddress 类型；
    ///     属性名对个别设备族可能不同 → 读不到时把对象上真实存在的属性名记进诊断清单。
    /// </summary>
    public sealed class TiaDeviceInventory
    {
        // 动态属性名候选（实测 V16~V21 的 PROFINET 节点用前两个；其余为尽力而为）
        private const string AttributeIpAddress = "Address";
        private const string AttributeSubnetMask = "SubnetMask";
        private const string AttributeRouterAddress = "RouterAddress";
        private const string AttributeIpProtocolSelection = "IpProtocolSelection";
        private const string AttributeOrderNumber = "OrderNumber";
        private const string AttributeFirmwareVersion = "FirmwareVersion";

        // 设备项名里出现这些词，说明我们"以为它是网络接口" —— 若此时取不到
        // NetworkInterface 服务，就要当成异常情况记进诊断，而不是静默跳过。
        private static readonly string[] InterfaceNameHints = new string[]
        {
            "PROFINET", "PROFIBUS", "PN", "IE", "Ethernet", "以太网"
        };

        private const int MaxDepth = 20;

        // 注：IPv4 判定、订货号/固件版本解析、IP 冲突检测、设备去重键这些**纯规则**
        //     已迁到 DeviceInventoryModel.cs 的 DeviceInventoryRules（离线 CAx 工具也要用同一份）。

        private readonly OpennessApi _api;
        private readonly Logger _logger;

        /// <summary>
        /// 可疑路径（名字像网络接口却没取到服务）。遍历结束后再判定是否真的漏了 ——
        /// 见 <see cref="ReportSuspects"/>。每个实例一次 Collect 用，不并发。
        /// </summary>
        private readonly List<string[]> _suspects = new List<string[]>();

        /// <summary>构造函数。</summary>
        /// <param name="api">已绑定的 Openness API。</param>
        /// <param name="logger">日志器。</param>
        public TiaDeviceInventory(OpennessApi api, Logger logger)
        {
            if (api == null)
            {
                throw new ToolException(ExitCodes.Environment, "读取网络设备前必须先绑定 Openness API。");
            }

            _api = api;
            // 共享内核里没有"空日志器"，所以缺省给一个不啰嗦的（verbose=false）。
            // 界面版一定会传入自己的 sink，这里只是保证单独调用不炸。
            _logger = logger ?? new Logger(false);
        }

        /// <summary>
        /// 遍历项目，采集所有网络节点。
        ///
        /// 设备清单来自**三个来源**（见 <see cref="CollectAllDevices"/>）：项目根设备、
        /// 「未分组的设备」（分布式 IO 设备）、用户设备组 —— 只读一个来源会"少读设备"。
        /// </summary>
        /// <param name="project">已打开的 Project 对象（只读遍历，不会被修改）。</param>
        /// <returns>采集结果（含诊断与冲突标记）。</returns>
        public DeviceInventoryResult Collect(object project)
        {
            if (project == null)
            {
                throw new ToolException(ExitCodes.InputOutput, "项目对象为空，无法读取网络设备。");
            }

            DeviceInventoryResult result = new DeviceInventoryResult();
            _suspects.Clear();

            // 能力前置检查：缺少必需类型时直接给出可执行的错误，而不是遍历一半莫名其妙空表
            IList<string> problems = _api.SelfCheckDeviceRead();
            foreach (string problem in problems)
            {
                _logger.Warning("该版本 Openness 的读网络设备能力有缺口：" + problem);
            }

            if (_api.NetworkInterfaceType == null || _api.NodeType == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness（" + _api.AssemblyNameText + "）缺少读网络设备所需的类型："
                    + string.Join("；", ToArray(problems))
                    + "\r\n请用 probe 命令确认该版本的 API 面。");
            }

            // 子网清单（数量与 IO 系统名会用到；取不到不影响主流程）
            object subnets = _api.GetProjectSubnets(project);
            IList<object> subnetList = _api.Flatten(subnets);
            result.SubnetCount = subnetList.Count;

            _logger.Info("项目里的子网：" + (subnetList.Count == 0 ? "（无）" : DescribeSubnets(subnetList)));

            // 设备清单：**三个来源合并去重**（只读 Project.Devices 会漏掉全部分布式 IO 设备 —— 实测踩过）
            IList<object> deviceList = CollectAllDevices(project, result);
            result.DeviceCount = deviceList.Count;
            _logger.Info("项目里的设备数（三来源合并去重后）：" + deviceList.Count.ToString(CultureInfo.InvariantCulture));

            int deviceIndex = 0;
            foreach (object device in deviceList)
            {
                deviceIndex++;
                string deviceName = OpennessApi.GetPropertyTextOrEmpty(device, "Name");
                int before = result.Endpoints.Count;

                _logger.Debug("遍历设备 " + deviceIndex + "：" + deviceName);
                try
                {
                    VisitDevice(device, deviceIndex, deviceName, result);
                }
                catch (Exception ex)
                {
                    // 单台设备出错不能毁掉整份表：记诊断、继续下一台
                    result.Diagnostics.Add("设备 " + deviceName + " 遍历失败："
                        + ex.GetType().Name + "：" + ex.Message);
                    _logger.Warning("设备 " + deviceName + " 遍历失败：" + ex.Message);
                }

                if (result.Endpoints.Count == before)
                {
                    // 用户要求：没有 IP / 没有网络的设备也要列出来，并在备注里写清原因
                    NetworkEndpoint placeholder = new NetworkEndpoint();
                    placeholder.Index = result.Endpoints.Count + 1;
                    placeholder.DeviceIndex = deviceIndex;
                    placeholder.DeviceName = deviceName;
                    placeholder.DeviceTypeIdentifier = OpennessApi.GetPropertyTextOrEmpty(device, "TypeIdentifier");
                    placeholder.IsGsd = IsTrue(OpennessApi.GetPropertyTextOrEmpty(device, "IsGsd"));
                    placeholder.Remark = "未组态网络（该设备没有任何网络接口）";
                    result.Endpoints.Add(placeholder);
                }
            }

            // 去重统计 + 假问题过滤 + IP 冲突检测
            ReportSuspects(result);
            DeduplicateDiagnostics(result);
            DeviceInventoryRules.DetectIpConflicts(result);

            _logger.Ok("采集完成：" + result.BuildSummary());
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  设备清单的来源（三个来源合并去重）
        //
        //  ★ 为什么不能只读 Project.Devices（真实项目实测踩过，只读出 1 台主 PLC）：
        //    一个含 20 多台分布式 IO 的项目，Devices 只给根级设备（主控制 PLC / HMI / PC 站），
        //    分布式 IO 设备全部在「未分组的设备」里（西门子官方：所有分布式 I/O 设备都位于
        //    该文件夹中），而它对应 Openness 的 Project.UngroupedDevicesGroup。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 收集项目里的**全部**设备：三个来源各读一遍，按设备名去重后合并。
        ///
        ///   ① <c>Project.Devices</c>               → 根级设备（主控制 PLC、HMI、PC 站…）
        ///   ② <c>Project.UngroupedDevicesGroup</c> → 项目树里的「未分组的设备」，**分布式 IO 设备都在这里**
        ///   ③ <c>Project.DeviceGroups</c>[各组的 Devices] → 用户自建的设备组（**V17 及以上才有**，可递归子组）
        ///
        /// 三者可能重叠（系统组里也可能含根级设备），所以按设备名去重 —— 否则主 PLC 会被算两次。
        /// 顺序＝接入顺序＝项目树顺序（根级 → 未分组的设备 → 用户设备组），CSV 的"设备序号"就是这个顺序。
        /// 任一来源取不到都只记诊断 + 告警并继续，绝不让整次读取失败（V16 没有 DeviceGroups 也要能跑）。
        /// </summary>
        /// <param name="project">Project 对象。</param>
        /// <param name="result">采集结果（来源诊断写进 <c>Diagnostics</c>）。</param>
        /// <returns>去重后的设备列表。</returns>
        private IList<object> CollectAllDevices(object project, DeviceInventoryResult result)
        {
            DeviceListBuilder builder = new DeviceListBuilder();

            // ① 根级设备。取不到 Devices 属性时这里是**故意**让它抛的：那意味着这个版本的 Openness
            //    根本读不了设备，与其给一份空表不如明确报错（沿用改动前的行为）。
            IList<object> rootDevices = _api.Flatten(_api.GetProjectDevices(project));
            int rootAdded = builder.Append(rootDevices);
            _logger.Info("设备来源① 项目根（Project.Devices）：" + rootDevices.Count.ToString(CultureInfo.InvariantCulture)
                + " 台，新增 " + rootAdded.ToString(CultureInfo.InvariantCulture) + " 台");

            // ② 未分组的设备（"设备系统组"）—— 分布式 IO 设备都在这里，缺了它就会"只读到主 PLC"
            int systemGroupDevices = 0;
            string systemGroupName = string.Empty;
            bool systemGroupPresent = false;
            try
            {
                object systemGroup = _api.GetProjectUngroupedDevicesGroup(project);
                if (systemGroup == null)
                {
                    const string missing = "本机 Openness 的 Project 上取不到 UngroupedDevicesGroup 属性："
                        + "项目树里「未分组的设备」（全部分布式 IO 设备）会读不到，只会读到根级设备。";
                    result.Diagnostics.Add(missing);
                    _logger.Warning(missing);
                }
                else
                {
                    systemGroupPresent = true;
                    systemGroupName = OpennessApi.GetPropertyTextOrEmpty(systemGroup, "Name");
                    IList<object> groupDevices = _api.GetDeviceGroupDevices(systemGroup);
                    systemGroupDevices = groupDevices.Count;
                    builder.Append(groupDevices);
                }
            }
            catch (Exception ex)
            {
                // 单来源失败不能毁掉整次读取：记诊断 + 告警，继续读下面的来源
                result.Diagnostics.Add("读取「未分组的设备」失败：" + ex.GetType().Name + "：" + ex.Message);
                _logger.Warning("读取「未分组的设备」失败：" + ex.Message);
            }

            _logger.Info("设备来源② 未分组的设备（UngroupedDevicesGroup"
                + (systemGroupPresent ? "：" + (string.IsNullOrEmpty(systemGroupName) ? "(未命名)" : systemGroupName) : "")
                + "）：" + systemGroupDevices.ToString(CultureInfo.InvariantCulture) + " 台");

            // ③ 用户自建的设备组（V17+ 才有；取不到就当"该版本没有"，只记一条信息）
            List<string> groupTexts = new List<string>();
            object groupsComposition = null;
            try
            {
                groupsComposition = _api.GetProjectDeviceGroups(project);
                IList<object> groups = _api.Flatten(groupsComposition);
                foreach (object group in groups)
                {
                    AppendGroupTree(group, string.Empty, 0, builder, groupTexts);
                }

                if (groupsComposition == null)
                {
                    _logger.Info("本机 Openness 的 Project 上没有 DeviceGroups 属性（V16 即如此）："
                        + "跳过「用户设备组」这一来源。");
                }
                else
                {
                    foreach (string text in groupTexts)
                    {
                        result.Diagnostics.Add("设备组 " + text);
                    }

                    _logger.Info("设备来源③ 用户设备组（Project.DeviceGroups）：" + groups.Count.ToString(CultureInfo.InvariantCulture)
                        + " 个组");
                }
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add("读取用户设备组失败：" + ex.GetType().Name + "：" + ex.Message);
                _logger.Warning("读取用户设备组失败：" + ex.Message);
            }

            // 汇总诊断：一行说清"每个来源各多少台、去重跳过多少台、最终多少台"
            result.Diagnostics.Add("设备来源：项目根 " + rootDevices.Count.ToString(CultureInfo.InvariantCulture)
                + " 台；未分组的设备 " + systemGroupDevices.ToString(CultureInfo.InvariantCulture)
                + " 台；设备组 " + (groupTexts.Count == 0 ? "（无）" : string.Join("、", groupTexts.ToArray()))
                + "；去重后 " + builder.Devices.Count.ToString(CultureInfo.InvariantCulture)
                + " 台（跳过重复 " + builder.Duplicates.ToString(CultureInfo.InvariantCulture) + " 台）");

            return builder.Devices;
        }

        /// <summary>
        /// 把一个设备组（含其子组）里的设备并入清单，并把"组路径：台数"记进 <paramref name="groupTexts"/>。
        ///
        /// 子组遍历是**防御式**的：V16~V21 的官方元数据里 <c>HW.DeviceGroup</c> 只有
        /// Name / Devices / Parent（没有子组属性），所以正常情况下一层就到底；
        /// 将来版本若加了子组属性也能自动用上。深度上限用 <see cref="MaxDepth"/> 兜底。
        /// </summary>
        /// <param name="group">设备组对象。</param>
        /// <param name="parentPath">父组路径（顶层传空串）。</param>
        /// <param name="depth">递归深度。</param>
        /// <param name="builder">合并器。</param>
        /// <param name="groupTexts">组 → 台数 的可读文本（诊断用）。</param>
        private void AppendGroupTree(object group, string parentPath, int depth, DeviceListBuilder builder,
            List<string> groupTexts)
        {
            if (group == null || depth > MaxDepth)
            {
                return;
            }

            string name = OpennessApi.GetPropertyTextOrEmpty(group, "Name");
            string path = parentPath;
            if (!string.IsNullOrEmpty(name))
            {
                path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
            }

            if (string.IsNullOrEmpty(path))
            {
                path = "(未命名设备组)";
            }

            IList<object> groupDevices = _api.GetDeviceGroupDevices(group);
            int added = builder.Append(groupDevices);
            groupTexts.Add(path + "：" + added.ToString(CultureInfo.InvariantCulture)
                + " 台（组内共 " + groupDevices.Count.ToString(CultureInfo.InvariantCulture) + " 台）");

            foreach (object subGroup in _api.GetDeviceGroupSubGroups(group))
            {
                AppendGroupTree(subGroup, path, depth + 1, builder, groupTexts);
            }
        }

        /// <summary>
        /// 设备清单的"合并 + 按设备名去重"小工具（一次 Collect 用一个实例，不并发）。
        /// 单独成类是为了把"三个来源怎么合"写在一个地方，方法签名也不必满屏 ref/out。
        /// </summary>
        private sealed class DeviceListBuilder
        {
            private readonly List<object> _devices = new List<object>();
            private readonly Dictionary<string, object> _seen =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            private int _ordinal;

            /// <summary>已去重的设备清单（顺序＝接入顺序）。</summary>
            public IList<object> Devices
            {
                get { return _devices; }
            }

            /// <summary>被跳过的重复设备台数（诊断用：正常应为 0，不为 0 说明来源之间有重叠）。</summary>
            public int Duplicates { get; private set; }

            /// <summary>
            /// 并入一批设备；同名（大小写不敏感）视为同一台，重复的跳过。
            /// </summary>
            /// <param name="batch">设备列表（可为 null）。</param>
            /// <returns>真正新增的台数。</returns>
            public int Append(IList<object> batch)
            {
                if (batch == null)
                {
                    return 0;
                }

                int added = 0;
                foreach (object device in batch)
                {
                    if (device == null)
                    {
                        continue;
                    }

                    _ordinal++;
                    string key = DeviceInventoryRules.BuildDeviceKey(
                        OpennessApi.GetPropertyTextOrEmpty(device, "Name"),
                        OpennessApi.GetPropertyTextOrEmpty(device, "TypeIdentifier"), _ordinal);
                    if (_seen.ContainsKey(key))
                    {
                        Duplicates++;
                        continue;
                    }

                    _seen.Add(key, device);
                    _devices.Add(device);
                    added++;
                }

                return added;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  遍历
        // ══════════════════════════════════════════════════════════════════

        /// <summary>遍历一台设备：递归设备项树，遇到提供 NetworkInterface 服务的项就读节点。</summary>
        private void VisitDevice(object device, int deviceIndex, string deviceName, DeviceInventoryResult result)
        {
            string deviceTypeId = OpennessApi.GetPropertyTextOrEmpty(device, "TypeIdentifier");
            bool isGsd = IsTrue(OpennessApi.GetPropertyTextOrEmpty(device, "IsGsd"));
            string rootPath = "/" + deviceName;

            foreach (object item in GetChildItems(device))
            {
                VisitItem(item, deviceIndex, deviceName, deviceTypeId, isGsd, rootPath, result, 0);
            }
        }

        /// <summary>递归遍历一个设备项。</summary>
        private void VisitItem(object item, int deviceIndex, string deviceName, string deviceTypeId,
            bool isGsd, string parentPath, DeviceInventoryResult result, int depth)
        {
            if (item == null || depth > MaxDepth)
            {
                return;
            }

            string itemName = OpennessApi.GetPropertyTextOrEmpty(item, "Name");
            string path = parentPath + "/" + itemName;

            // ① 这个设备项是不是网络接口？（取不到服务是常态：机架、电源、IO 模块都没有）
            string serviceError;
            object networkInterface = _api.TryGetService(item, _api.NetworkInterfaceType, out serviceError);
            if (networkInterface != null)
            {
                result.InterfaceCount++;
                ReadInterface(networkInterface, item, deviceIndex, deviceName, deviceTypeId, isGsd,
                    itemName, path, result);
            }
            else if (LooksLikeInterface(itemName))
            {
                // 名字像网口却拿不到服务 —— 先记下来，遍历结束后再判定是不是真的漏了。
                // （实测：HMI 的 "HMI_1.IE_CP_1" 就是这种"名字像但其实不是接口"的父节点，
                //   它的子节点才是真接口。当场报警会制造假问题，所以延后判定。）
                _suspects.Add(new string[] { path, serviceError ?? string.Empty, itemName });
            }

            // ② 递归子项（CPU → 接口 → 端口这类层级都在子项里）
            foreach (object child in GetChildItems(item))
            {
                VisitItem(child, deviceIndex, deviceName, deviceTypeId, isGsd, path, result, depth + 1);
            }
        }

        /// <summary>读一个网络接口下的所有节点，每个节点产出一行。</summary>
        private void ReadInterface(object networkInterface, object ownerItem, int deviceIndex, string deviceName,
            string deviceTypeId, bool isGsd, string itemName, string path, DeviceInventoryResult result)
        {
            string interfaceName = OpennessApi.GetPropertyTextOrEmpty(networkInterface, "Name");
            if (string.IsNullOrEmpty(interfaceName))
            {
                // 实测：PROFINET 接口对象的 Name 常常是空的（名字在设备项上），
                // 空着会让"接口名"整列没意义，所以退回用设备项名。
                interfaceName = itemName;
            }

            string interfaceType = OpennessApi.GetPropertyTextOrEmpty(networkInterface, "InterfaceType");
            string operatingMode = OpennessApi.GetPropertyTextOrEmpty(networkInterface, "InterfaceOperatingMode");
            string ioController = ReadCompositionNames(networkInterface, "IoControllers");

            object nodes = OpennessApi.GetPropertyValue(networkInterface, "Nodes");
            IList<object> nodeList = _api.Flatten(nodes);

            if (nodeList.Count == 0)
            {
                NetworkEndpoint empty = NewEndpoint(result, ownerItem, deviceIndex, deviceName, deviceTypeId, isGsd,
                    path, interfaceName, interfaceType, operatingMode, ioController);
                empty.Remark = "接口已存在但没有任何节点（未组态网络）";
                result.Endpoints.Add(empty);
                return;
            }

            foreach (object node in nodeList)
            {
                NetworkEndpoint endpoint = NewEndpoint(result, ownerItem, deviceIndex, deviceName, deviceTypeId, isGsd,
                    path, interfaceName, interfaceType, operatingMode, ioController);
                ReadNode(node, ownerItem, endpoint, result);
                result.Endpoints.Add(endpoint);
            }
        }

        /// <summary>读一个节点上的标识与 IP 属性。</summary>
        private void ReadNode(object node, object ownerItem, NetworkEndpoint endpoint, DeviceInventoryResult result)
        {
            endpoint.NodeName = OpennessApi.GetPropertyTextOrEmpty(node, "Name");
            endpoint.PnDeviceName = OpennessApi.GetPropertyTextOrEmpty(node, "NodeId");
            endpoint.NodeType = OpennessApi.GetPropertyTextOrEmpty(node, "NodeType");

            // ★ IP 与"总线地址"必须分开：同一个 "Address" 动态属性，
            //   以太网节点上装的是 IP，MPI/PROFIBUS 节点上装的是站地址（实测 MPI 节点返回 1）。
            //   如果一律塞进 IP 列，做 IP 台账时会把站地址当成 IP 用，所以按"是不是 IPv4"分流。
            string rawAddress = _api.GetAttributeText(node, AttributeIpAddress);
            if (DeviceInventoryRules.IsIpv4Text(rawAddress))
            {
                endpoint.IpAddress = rawAddress;
                endpoint.SubnetMask = _api.GetAttributeText(node, AttributeSubnetMask);
            }
            else if (!string.IsNullOrEmpty(rawAddress))
            {
                endpoint.BusAddress = rawAddress;
            }

            endpoint.RouterAddress = _api.GetAttributeText(node, AttributeRouterAddress);
            endpoint.IpAssignment = _api.GetAttributeText(node, AttributeIpProtocolSelection);

            // 订货号 / 固件版本：先按属性读（含沿父级向上找），再退回从设备项类型标识里解析。
            // 类型标识长这样：OrderNumber:6ES7 515-2AM01-0AB0/V2.8 —— 两个信息都在里面。
            endpoint.OrderNumber = ReadAttributeUpwards(ownerItem, AttributeOrderNumber);
            if (string.IsNullOrEmpty(endpoint.OrderNumber))
            {
                endpoint.OrderNumber = DeviceInventoryRules.ExtractOrderNumber(endpoint.DeviceItemTypeIdentifier);
            }

            endpoint.FirmwareVersion = ReadAttributeUpwards(ownerItem, AttributeFirmwareVersion);
            if (string.IsNullOrEmpty(endpoint.FirmwareVersion))
            {
                endpoint.FirmwareVersion = DeviceInventoryRules.ExtractFirmwareVersion(endpoint.DeviceItemTypeIdentifier);
            }

            // 所属子网
            object subnet = OpennessApi.GetPropertyValue(node, "ConnectedSubnet");
            if (subnet != null)
            {
                endpoint.SubnetName = OpennessApi.GetPropertyTextOrEmpty(subnet, "Name");
                endpoint.SubnetType = OpennessApi.GetPropertyTextOrEmpty(subnet, "NetType");
                endpoint.IoSystem = ReadCompositionNames(subnet, "IoSystems");
            }

            result.NodeCount++;
            if (endpoint.HasIp)
            {
                result.IpCount++;
            }

            endpoint.Remark = BuildRemark(endpoint, node, result);
        }

        /// <summary>按"为什么没有 IP"给出备注（用户要求：无 IP 也要列出并写明原因）。</summary>
        private string BuildRemark(NetworkEndpoint endpoint, object node, DeviceInventoryResult result)
        {
            StringBuilder remark = new StringBuilder();

            return remark.ToString().TrimEnd('；', ';', ' ');
        }

        // ══════════════════════════════════════════════════════════════════
        //  辅助
        // ══════════════════════════════════════════════════════════════════

        /// <summary>取子项集合：优先 DeviceItems，退回 Items（两版本的属性名都考虑到了）。</summary>
        private IList<object> GetChildItems(object hardwareObject)
        {
            object items = OpennessApi.GetPropertyValue(hardwareObject, "DeviceItems");
            if (items == null)
            {
                items = OpennessApi.GetPropertyValue(hardwareObject, "Items");
            }

            return _api.Flatten(items);
        }

        /// <summary>读某个组合属性的元素名并拼成一行（如 IoControllers / IoSystems）；取不到返回空串。</summary>
        private string ReadCompositionNames(object owner, string propertyName)
        {
            object composition = OpennessApi.GetPropertyValue(owner, propertyName);
            List<string> names = new List<string>();
            foreach (object element in _api.Flatten(composition))
            {
                string name = OpennessApi.GetPropertyTextOrEmpty(element, "Name");
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? string.Empty : string.Join("; ", names.ToArray());
        }

        /// <summary>新建一行并填好"设备/接口"层面的公共字段。</summary>
        private NetworkEndpoint NewEndpoint(DeviceInventoryResult result, object ownerItem, int deviceIndex,
            string deviceName, string deviceTypeId, bool isGsd, string path, string interfaceName,
            string interfaceType, string operatingMode, string ioController)
        {
            NetworkEndpoint endpoint = new NetworkEndpoint();
            endpoint.Index = result.Endpoints.Count + 1;
            endpoint.DeviceIndex = deviceIndex;
            endpoint.DeviceName = deviceName;
            endpoint.DeviceTypeIdentifier = deviceTypeId;
            endpoint.IsGsd = isGsd;
            // 供应商：订货号/系统设备 → Siemens；GSD 设备从 GSDML 标识里解析（与离线 CAx 同一套规则）
            endpoint.Vendor = DeviceInventoryRules.ExtractVendor(deviceTypeId);
            endpoint.DeviceItemName = OpennessApi.GetPropertyTextOrEmpty(ownerItem, "Name");
            endpoint.DeviceItemTypeIdentifier = OpennessApi.GetPropertyTextOrEmpty(ownerItem, "TypeIdentifier");
            endpoint.Slot = NormalizeSlot(OpennessApi.GetPropertyTextOrEmpty(ownerItem, "PositionNumber"));
            endpoint.DeviceItemPath = path;
            endpoint.InterfaceName = interfaceName;
            endpoint.InterfaceType = interfaceType;
            endpoint.InterfaceOperatingMode = operatingMode;
            endpoint.IoController = ioController;
            return endpoint;
        }

        /// <summary>
        /// 槽号规范化：Openness 的 PositionNumber 是 int，取不到或不适用时是 0，
        /// 直接写 0 会在表里变成噪音，所以统一留空。
        /// </summary>
        private static string NormalizeSlot(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "0")
            {
                return string.Empty;
            }

            return text;
        }

        /// <summary>
        /// 把节点的真实属性名样本记进诊断（最多 3 条）。
        /// 用处：万一某个设备族不用 Address/SubnetMask 命名，日志里能直接看到它到底叫什么。
        /// </summary>
        private void SampleAttributeNames(object node, string path, DeviceInventoryResult result)
        {
            int samples = 0;
            foreach (string text in result.Diagnostics)
            {
                if (text.StartsWith("属性名样本", StringComparison.Ordinal))
                {
                    samples++;
                }
            }

            if (samples >= 3)
            {
                return;
            }

            IList<string> names = _api.GetAttributeNames(node);
            if (names.Count == 0)
            {
                result.Diagnostics.Add("属性名样本 " + path + "：（该节点没有可枚举的动态属性）");
                return;
            }

            result.Diagnostics.Add("属性名样本 " + path + "：共 " + names.Count + " 个 → "
                + string.Join(", ", ToArray(names)));
        }

        /// <summary>
        /// 汇报"名字像网络接口却没取到服务"的可疑路径，但**先排除假问题**：
        /// 如果它的子路径下已经成功读出了接口，那它只是个父节点
        /// （实测 HMI_1.IE_CP_1 就是这种：名字带 IE，真正的接口在它下面），不算漏。
        /// 只有"整棵子树都没读出接口"或"取服务时报了真错误"才值得写进诊断。
        /// </summary>
        private void ReportSuspects(DeviceInventoryResult result)
        {
            foreach (string[] suspect in _suspects)
            {
                string path = suspect[0];
                string error = suspect[1];

                bool covered = false;
                foreach (NetworkEndpoint endpoint in result.Endpoints)
                {
                    if (!string.IsNullOrEmpty(endpoint.DeviceItemPath)
                        && endpoint.DeviceItemPath.StartsWith(path + "/", StringComparison.Ordinal))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered && string.IsNullOrEmpty(error))
                {
                    continue;
                }

                result.Diagnostics.Add("设备项 " + path + " 名字像网络接口，但取不到 NetworkInterface 服务："
                    + (string.IsNullOrEmpty(error) ? "该设备项本身没有这个服务（其子项才是真接口）" : error));
            }
        }

        /// <summary>诊断信息去重（同一原因在不同设备上重复出现只留一条）。</summary>
        private static void DeduplicateDiagnostics(DeviceInventoryResult result)
        {
            List<string> unique = new List<string>();
            foreach (string text in result.Diagnostics)
            {
                if (!unique.Contains(text))
                {
                    unique.Add(text);
                }
            }

            result.Diagnostics.Clear();
            result.Diagnostics.AddRange(unique);
        }

        /// <summary>子网清单的可读描述。</summary>
        private string DescribeSubnets(IList<object> subnets)
        {
            List<string> texts = new List<string>();
            foreach (object subnet in subnets)
            {
                string name = OpennessApi.GetPropertyTextOrEmpty(subnet, "Name");
                string type = OpennessApi.GetPropertyTextOrEmpty(subnet, "NetType");
                texts.Add(name + "(" + type + ")");
            }

            return string.Join("、", texts.ToArray());
        }

        /// <summary>设备项名是否像网络接口。</summary>
        private static bool LooksLikeInterface(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
            {
                return false;
            }

            foreach (string hint in InterfaceNameHints)
            {
                if (itemName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>把 "True"/"true" 这类文本判断成布尔。</summary>
        private static bool IsTrue(string text)
        {
            return string.Equals(text, "True", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.Ordinal);
        }

        /// <summary>
        /// 沿 Parent 链向上找属性（最多 6 层）。
        /// 为什么要向上找：订货号 / 固件版本属于**模块**，而 IP 挂在模块的接口子项上，
        /// 直接问接口对象往往取不到；问它的父级（CPU 模块）才有。
        /// </summary>
        /// <param name="start">起点对象（一般是接口所属设备项）。</param>
        /// <param name="attributeName">属性名。</param>
        /// <returns>取到的文本；都没有则返回空串。</returns>
        private string ReadAttributeUpwards(object start, string attributeName)
        {
            object current = start;
            for (int depth = 0; depth < 6 && current != null; depth++)
            {
                string value = _api.GetAttributeText(current, attributeName);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }

                current = OpennessApi.GetPropertyValue(current, "Parent");
            }

            return string.Empty;
        }

        /// <summary>IList&lt;string&gt; → string[]（C# 7.3 下拼接用）。</summary>
        private static string[] ToArray(IList<string> list)
        {
            string[] array = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                array[i] = list[i];
            }

            return array;
        }
    }
}
