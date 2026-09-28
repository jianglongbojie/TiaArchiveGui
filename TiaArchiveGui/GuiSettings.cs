using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaArchiveGui
{
    /// <summary>
    /// 记住上次用过的路径和选项，下次打开窗口时自动填回去。
    ///
    /// 存法刻意做得极简：%APPDATA%\TiaArchiveGui\settings.txt 里一行一个 key=value。
    /// 不引第三方配置库、不用注册表 —— 出问题时用户自己用记事本就能看到、能删掉重来。
    /// 类比 SCL：就像把掉电保持数据放在一个明文的保持性 DB 里，而不是藏进看不到的地方。
    /// </summary>
    internal sealed class GuiSettings
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly string _filePath;

        private GuiSettings(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>配置文件的绝对路径（界面上会显示给用户）。</summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>
        /// 从磁盘加载配置；文件不存在或损坏时返回一份空配置（不抛异常）。
        /// </summary>
        public static GuiSettings Load()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TiaArchiveGui");
            string filePath = Path.Combine(directory, "settings.txt");

            GuiSettings settings = new GuiSettings(filePath);

            try
            {
                if (!File.Exists(filePath))
                {
                    return settings;
                }

                foreach (string line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int index = line.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, index).Trim();
                    string value = line.Substring(index + 1).Trim();
                    settings._values[key] = value;
                }
            }
            catch (Exception)
            {
                // 读配置失败不影响使用，只是记不住上次的选择。
            }

            return settings;
        }

        /// <summary>取字符串配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="fallback">默认值。</param>
        /// <returns>取到的值或默认值。</returns>
        public string GetString(string key, string fallback)
        {
            string value;
            if (_values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
            return fallback;
        }

        /// <summary>取布尔配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="fallback">默认值。</param>
        /// <returns>取到的值或默认值。</returns>
        public bool GetBool(string key, bool fallback)
        {
            string value;
            if (_values.TryGetValue(key, out value))
            {
                bool parsed;
                if (bool.TryParse(value, out parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        /// <summary>取整数配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="fallback">默认值。</param>
        /// <returns>取到的值或默认值。</returns>
        public int GetInt(string key, int fallback)
        {
            string value;
            if (_values.TryGetValue(key, out value))
            {
                int parsed;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        /// <summary>写字符串配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="value">值；null 视为空串。</param>
        public void SetString(string key, string value)
        {
            _values[key] = value ?? string.Empty;
        }

        /// <summary>写布尔配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="value">值。</param>
        public void SetBool(string key, bool value)
        {
            _values[key] = value ? "true" : "false";
        }

        /// <summary>写整数配置。</summary>
        /// <param name="key">键。</param>
        /// <param name="value">值。</param>
        public void SetInt(string key, int value)
        {
            _values[key] = value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 保存到磁盘。写失败（权限、磁盘满）时静默忽略 —— 记不住偏好不是致命错误。
        /// </summary>
        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                List<string> lines = new List<string>();
                lines.Add("# TiaArchiveGui 上次使用的路径与选项（可直接删除，程序会重新生成）");
                foreach (KeyValuePair<string, string> pair in _values)
                {
                    lines.Add(pair.Key + "=" + pair.Value);
                }

                File.WriteAllLines(_filePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        /// <summary>
        /// 取一个路径所在的目录，用于把"浏览"对话框定位到上次的位置。
        /// 输入是文件时返回其目录；是目录时原样返回；都不存在时返回空串。
        /// </summary>
        /// <param name="path">文件或目录路径。</param>
        /// <returns>目录路径，取不到时为空串。</returns>
        public static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    return path;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch (Exception)
            {
                // 路径非法时忽略
            }

            return string.Empty;
        }
    }
}
