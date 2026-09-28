using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TiaOpennessKit.Tia;

namespace TiaOpennessKit.Services
{
    /// <summary>
    /// 连接关系（谁挂在哪个子网 / 谁归哪个 IO 控制器）与端口清单的 CSV 导出。
    ///
    /// 为什么单独成文件：设备清单走 <see cref="DeviceInventoryExporter"/>（在线/离线共用同一套列），
    /// 而"连接关系 + 端口"是离线 CAx 才有的数据（在线读取时是空表），所以各自成表更清楚。
    ///
    /// ★ 口径说明（别误当成数据缺了）：CAx 数据里**没有端口↔端口接线**，
    ///   所以这里给的是「节点→子网」「接口→IO 系统」两类连接关系 + 每个设备的端口清单。
    ///
    /// 纯 BCL，UTF-8 **带 BOM**（不带 BOM 时中文在 Excel 里会乱码），RFC 4180 转义。
    /// </summary>
    public static class CaxTopologyExporter
    {
        /// <summary>连接关系表的表头。</summary>
        public static readonly string[] LinkColumns = new string[]
        {
            "序号", "连接类型", "A端设备", "A端设备项路径", "A端端点", "A端端口", "B端", "备注"
        };

        /// <summary>端口清单表的表头。</summary>
        public static readonly string[] PortColumns = new string[]
        {
            "序号", "设备", "设备项路径", "端口名", "端口标识", "位置号"
        };

        /// <summary>生成连接关系 CSV 文本。</summary>
        /// <param name="result">采集结果。</param>
        /// <returns>CSV 文本。</returns>
        public static string BuildLinksCsvText(DeviceInventoryResult result)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(JoinCsv(LinkColumns));
            if (result != null)
            {
                foreach (CaxTopologyLink link in result.Links)
                {
                    builder.AppendLine(JoinCsv(new string[]
                    {
                        link.Index.ToString(CultureInfo.InvariantCulture),
                        link.Kind,
                        link.SideADevice,
                        link.SideAItem,
                        link.SideAEndPoint,
                        link.SideAPort,
                        link.SideB,
                        link.Remark
                    }));
                }
            }

            return builder.ToString();
        }

        /// <summary>生成端口清单 CSV 文本。</summary>
        /// <param name="result">采集结果。</param>
        /// <returns>CSV 文本。</returns>
        public static string BuildPortsCsvText(DeviceInventoryResult result)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(JoinCsv(PortColumns));
            if (result != null)
            {
                foreach (CaxPortInfo port in result.Ports)
                {
                    builder.AppendLine(JoinCsv(new string[]
                    {
                        port.Index.ToString(CultureInfo.InvariantCulture),
                        port.DeviceName,
                        port.ItemPath,
                        port.PortName,
                        port.Label,
                        port.PositionNumber
                    }));
                }
            }

            return builder.ToString();
        }

        /// <summary>写连接关系 CSV（UTF-8 带 BOM）。</summary>
        /// <param name="result">采集结果。</param>
        /// <param name="path">目标文件。</param>
        public static void WriteLinksCsv(DeviceInventoryResult result, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, BuildLinksCsvText(result), new UTF8Encoding(true));
        }

        /// <summary>写端口清单 CSV（UTF-8 带 BOM）。</summary>
        /// <param name="result">采集结果。</param>
        /// <param name="path">目标文件。</param>
        public static void WritePortsCsv(DeviceInventoryResult result, string path)
        {
            EnsureDirectory(path);
            File.WriteAllText(path, BuildPortsCsvText(result), new UTF8Encoding(true));
        }

        /// <summary>把字段数组拼成一行 CSV（转义复用设备清单那一套，只有一份实现）。</summary>
        private static string JoinCsv(string[] values)
        {
            List<string> escaped = new List<string>();
            foreach (string value in values)
            {
                escaped.Add(DeviceInventoryExporter.CsvEscape(value));
            }

            return string.Join(",", escaped.ToArray());
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
