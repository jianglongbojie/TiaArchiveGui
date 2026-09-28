using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TiaOpennessKit.Tia;

namespace TiaOpennessKit.Services
{
    /// <summary>
    /// 网络设备清单的导出（CSV / JSON）。
    ///
    /// 纯 BCL 实现，**不引用任何 Siemens 类型**，所以可以脱离 TIA 单独测试与复用
    /// （自检里就是这么验证表头与行数一致、转义正确、冲突标记写进文件的）。
    ///
    /// CSV 约定：UTF-8 **带 BOM** —— 不带 BOM 时中文在 Excel 里会乱码，这是踩过的坑；
    /// 字段含逗号/引号/换行时按 RFC 4180 加引号并把引号翻倍。
    /// </summary>
    public static class DeviceInventoryExporter
    {
        /// <summary>CSV 表头（顺序即导出列顺序）。</summary>
        public static readonly string[] Columns = new string[]
        {
            "序号", "设备序号", "设备名", "设备类型", "供应商", "GSD设备", "角色", "设备项路径", "设备项名", "设备项类型", "位置号",
            "接口名", "接口类型", "接口运行模式",
            "节点名", "PROFINET设备名", "节点类型",
            "IP地址", "非IP节点地址", "子网掩码", "路由器地址", "IP分配方式",
            "子网名", "子网类型",
            "订货号", "固件版本", "IO控制器", "IO系统",
            "备注", "告警"
        };

        /// <summary>把一条记录按 <see cref="Columns"/> 的顺序转成字段数组。</summary>
        /// <param name="endpoint">记录。</param>
        /// <returns>字段数组（长度与表头一致）。</returns>
        public static string[] RowValues(NetworkEndpoint endpoint)
        {
            if (endpoint == null)
            {
                return new string[Columns.Length];
            }

            return new string[]
            {
                endpoint.Index.ToString(CultureInfo.InvariantCulture),
                endpoint.DeviceIndex.ToString(CultureInfo.InvariantCulture),
                endpoint.DeviceName,
                endpoint.DeviceTypeIdentifier,
                endpoint.Vendor,
                endpoint.IsGsd ? "是" : "否",
                endpoint.Role,
                endpoint.DeviceItemPath,
                endpoint.DeviceItemName,
                endpoint.DeviceItemTypeIdentifier,
                endpoint.Slot,
                endpoint.InterfaceName,
                endpoint.InterfaceType,
                endpoint.InterfaceOperatingMode,
                endpoint.NodeName,
                endpoint.PnDeviceName,
                endpoint.NodeType,
                endpoint.IpAddress,
                endpoint.BusAddress,
                endpoint.SubnetMask,
                endpoint.RouterAddress,
                endpoint.IpAssignment,
                endpoint.SubnetName,
                endpoint.SubnetType,
                endpoint.OrderNumber,
                endpoint.FirmwareVersion,
                endpoint.IoController,
                endpoint.IoSystem,
                endpoint.Remark,
                endpoint.Alert
            };
        }

        /// <summary>生成 CSV 文本（不含写盘）。</summary>
        /// <param name="result">采集结果。</param>
        /// <returns>CSV 文本。</returns>
        public static string BuildCsvText(DeviceInventoryResult result)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(JoinCsv(Columns));
            if (result != null)
            {
                foreach (NetworkEndpoint endpoint in result.Endpoints)
                {
                    builder.AppendLine(JoinCsv(RowValues(endpoint)));
                }
            }

            return builder.ToString();
        }

        /// <summary>写 CSV 文件（UTF-8 带 BOM）。</summary>
        /// <param name="result">采集结果。</param>
        /// <param name="path">输出文件路径。</param>
        public static void WriteCsv(DeviceInventoryResult result, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, BuildCsvText(result), new UTF8Encoding(true));
        }

        /// <summary>写 JSON 文件（UTF-8 带 BOM，便于记事本/浏览器直接看中文）。</summary>
        /// <param name="result">采集结果。</param>
        /// <param name="projectName">项目名。</param>
        /// <param name="projectPath">项目文件路径。</param>
        /// <param name="tiaVersion">读取所用的 TIA / Openness 版本文本。</param>
        /// <param name="path">输出文件路径。</param>
        public static void WriteJson(DeviceInventoryResult result, string projectName, string projectPath,
            string tiaVersion, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path,
                BuildJsonText(result, projectName, projectPath, tiaVersion, DateTime.Now),
                new UTF8Encoding(true));
        }

        /// <summary>生成 JSON 文本（不含写盘）。键名用英文，方便脚本消费。</summary>
        /// <param name="result">采集结果。</param>
        /// <param name="projectName">项目名。</param>
        /// <param name="projectPath">项目文件路径。</param>
        /// <param name="tiaVersion">版本文本。</param>
        /// <param name="now">生成时间。</param>
        /// <returns>JSON 文本。</returns>
        public static string BuildJsonText(DeviceInventoryResult result, string projectName, string projectPath,
            string tiaVersion, DateTime now)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("{");
            b.AppendLine("  \"schema\": \"tia-network-devices/1\",");
            b.AppendLine("  \"generatedAt\": " + Json(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + ",");
            b.AppendLine("  \"tool\": " + Json("TiaDeviceGui · TiaOpennessKit") + ",");
            b.AppendLine("  \"tiaVersion\": " + Json(tiaVersion) + ",");
            b.AppendLine("  \"projectName\": " + Json(projectName) + ",");
            b.AppendLine("  \"projectPath\": " + Json(projectPath) + ",");

            b.AppendLine("  \"summary\": {");
            if (result == null)
            {
                b.AppendLine("    \"devices\": 0, \"interfaces\": 0, \"nodes\": 0, \"nodesWithIp\": 0, \"subnets\": 0, \"ipConflicts\": 0");
            }
            else
            {
                b.AppendLine("    \"devices\": " + result.DeviceCount.ToString(CultureInfo.InvariantCulture) + ",");
                b.AppendLine("    \"interfaces\": " + result.InterfaceCount.ToString(CultureInfo.InvariantCulture) + ",");
                b.AppendLine("    \"nodes\": " + result.NodeCount.ToString(CultureInfo.InvariantCulture) + ",");
                b.AppendLine("    \"nodesWithIp\": " + result.IpCount.ToString(CultureInfo.InvariantCulture) + ",");
                b.AppendLine("    \"subnets\": " + result.SubnetCount.ToString(CultureInfo.InvariantCulture) + ",");
                b.AppendLine("    \"ipConflicts\": " + result.ConflictCount.ToString(CultureInfo.InvariantCulture));
            }

            b.AppendLine("  },");

            b.AppendLine("  \"diagnostics\": [");
            if (result != null)
            {
                for (int i = 0; i < result.Diagnostics.Count; i++)
                {
                    b.AppendLine("    " + Json(result.Diagnostics[i])
                        + (i == result.Diagnostics.Count - 1 ? string.Empty : ","));
                }
            }

            b.AppendLine("  ],");

            b.AppendLine("  \"endpoints\": [");
            if (result != null)
            {
                for (int i = 0; i < result.Endpoints.Count; i++)
                {
                    NetworkEndpoint e = result.Endpoints[i];
                    b.AppendLine("    {");
                    b.AppendLine("      \"index\": " + e.Index.ToString(CultureInfo.InvariantCulture) + ",");
                    b.AppendLine("      \"deviceIndex\": " + e.DeviceIndex.ToString(CultureInfo.InvariantCulture) + ",");
                    b.AppendLine("      \"deviceName\": " + Json(e.DeviceName) + ",");
                    b.AppendLine("      \"deviceType\": " + Json(e.DeviceTypeIdentifier) + ",");
                    b.AppendLine("      \"vendor\": " + Json(e.Vendor) + ",");
                    b.AppendLine("      \"isGsd\": " + (e.IsGsd ? "true" : "false") + ",");
                    b.AppendLine("      \"role\": " + Json(e.Role) + ",");
                    b.AppendLine("      \"deviceItemPath\": " + Json(e.DeviceItemPath) + ",");
                    b.AppendLine("      \"deviceItemName\": " + Json(e.DeviceItemName) + ",");
                    b.AppendLine("      \"deviceItemType\": " + Json(e.DeviceItemTypeIdentifier) + ",");
                    b.AppendLine("      \"slot\": " + Json(e.Slot) + ",");
                    b.AppendLine("      \"interfaceName\": " + Json(e.InterfaceName) + ",");
                    b.AppendLine("      \"interfaceType\": " + Json(e.InterfaceType) + ",");
                    b.AppendLine("      \"interfaceOperatingMode\": " + Json(e.InterfaceOperatingMode) + ",");
                    b.AppendLine("      \"nodeName\": " + Json(e.NodeName) + ",");
                    b.AppendLine("      \"pnDeviceName\": " + Json(e.PnDeviceName) + ",");
                    b.AppendLine("      \"nodeType\": " + Json(e.NodeType) + ",");
                    b.AppendLine("      \"ipAddress\": " + Json(e.IpAddress) + ",");
                    b.AppendLine("      \"busAddress\": " + Json(e.BusAddress) + ",");
                    b.AppendLine("      \"subnetMask\": " + Json(e.SubnetMask) + ",");
                    b.AppendLine("      \"routerAddress\": " + Json(e.RouterAddress) + ",");
                    b.AppendLine("      \"ipAssignment\": " + Json(e.IpAssignment) + ",");
                    b.AppendLine("      \"subnetName\": " + Json(e.SubnetName) + ",");
                    b.AppendLine("      \"subnetType\": " + Json(e.SubnetType) + ",");
                    b.AppendLine("      \"orderNumber\": " + Json(e.OrderNumber) + ",");
                    b.AppendLine("      \"firmwareVersion\": " + Json(e.FirmwareVersion) + ",");
                    b.AppendLine("      \"ioController\": " + Json(e.IoController) + ",");
                    b.AppendLine("      \"ioSystem\": " + Json(e.IoSystem) + ",");
                    b.AppendLine("      \"remark\": " + Json(e.Remark) + ",");
                    b.AppendLine("      \"alert\": " + Json(e.Alert));
                    b.Append("    }" + (i == result.Endpoints.Count - 1 ? string.Empty : ",") + Environment.NewLine);
                }
            }

            b.AppendLine("  ],");

            // 连接关系 / 端口清单：离线 CAx（.aml）读得出，在线 Openness 读取时是空数组
            b.AppendLine("  \"links\": [");
            if (result != null)
            {
                for (int i = 0; i < result.Links.Count; i++)
                {
                    CaxTopologyLink link = result.Links[i];
                    b.AppendLine("    {");
                    b.AppendLine("      \"index\": " + link.Index.ToString(CultureInfo.InvariantCulture) + ",");
                    b.AppendLine("      \"kind\": " + Json(link.Kind) + ",");
                    b.AppendLine("      \"sideADevice\": " + Json(link.SideADevice) + ",");
                    b.AppendLine("      \"sideAItem\": " + Json(link.SideAItem) + ",");
                    b.AppendLine("      \"sideAEndPoint\": " + Json(link.SideAEndPoint) + ",");
                    b.AppendLine("      \"sideAPort\": " + Json(link.SideAPort) + ",");
                    b.AppendLine("      \"sideB\": " + Json(link.SideB) + ",");
                    b.AppendLine("      \"remark\": " + Json(link.Remark));
                    b.Append("    }" + (i == result.Links.Count - 1 ? string.Empty : ",") + Environment.NewLine);
                }
            }

            b.AppendLine("  ],");

            b.AppendLine("  \"ports\": [");
            if (result != null)
            {
                for (int i = 0; i < result.Ports.Count; i++)
                {
                    CaxPortInfo port = result.Ports[i];
                    b.AppendLine("    {");
                    b.AppendLine("      \"index\": " + port.Index.ToString(CultureInfo.InvariantCulture) + ",");
                    b.AppendLine("      \"deviceName\": " + Json(port.DeviceName) + ",");
                    b.AppendLine("      \"itemPath\": " + Json(port.ItemPath) + ",");
                    b.AppendLine("      \"portName\": " + Json(port.PortName) + ",");
                    b.AppendLine("      \"label\": " + Json(port.Label) + ",");
                    b.AppendLine("      \"positionNumber\": " + Json(port.PositionNumber));
                    b.Append("    }" + (i == result.Ports.Count - 1 ? string.Empty : ",") + Environment.NewLine);
                }
            }

            b.AppendLine("  ]");

            b.AppendLine("}");
            return b.ToString();
        }

        /// <summary>
        /// 按共享的文件名规则算出导出文件路径。
        /// 规则**沿用归档工具那一套**（ArchiveNaming）：`项目名[_后缀][_时间戳].扩展名`，
        /// 这样两个工具产出的文件名风格一致，用户不用记两套。
        /// </summary>
        /// <param name="outputDirectory">输出目录。</param>
        /// <param name="projectName">项目名（不含扩展名）。</param>
        /// <param name="extension">扩展名，含点，如 .csv。</param>
        /// <param name="suffix">后缀（可为空，内部会规范化成 _xx）。</param>
        /// <param name="timestampFormat">时间戳格式（空串表示不加时间戳）。</param>
        /// <param name="now">时间基准。</param>
        /// <returns>完整的输出路径。</returns>
        public static string BuildOutputPath(string outputDirectory, string projectName, string extension,
            string suffix, string timestampFormat, DateTime now)
        {
            string name = ArchiveNaming.BuildFileName(projectName + extension, suffix, timestampFormat, now);
            return Path.Combine(outputDirectory, name);
        }

        /// <summary>按 RFC 4180 转义一个 CSV 字段。</summary>
        /// <param name="value">原始值。</param>
        /// <returns>可安全写入的字段。</returns>
        public static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool needQuote = value.IndexOf(',') >= 0
                || value.IndexOf('"') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('\r') >= 0;

            if (!needQuote)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>JSON 字符串转义。</summary>
        /// <param name="value">原始值。</param>
        /// <returns>带引号的 JSON 字符串。</returns>
        public static string Json(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            StringBuilder b = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            b.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            b.Append(c);
                        }

                        break;
                }
            }

            b.Append("\"");
            return b.ToString();
        }

        /// <summary>把字段数组拼成一行 CSV。</summary>
        private static string JoinCsv(string[] values)
        {
            string[] escaped = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                escaped[i] = CsvEscape(values[i]);
            }

            return string.Join(",", escaped);
        }

        /// <summary>确保输出目录存在。</summary>
        private static void EnsureDirectory(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
