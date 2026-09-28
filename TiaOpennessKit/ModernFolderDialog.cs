using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TiaOpennessKit
{
    /// <summary>
    /// 现代"选择文件夹"对话框：直接调 Windows Vista 起的 IFileOpenDialog（加 FOS_PICKFOLDERS）。
    ///
    /// 为什么不用 .NET Framework 自带的 <see cref="FolderBrowserDialog"/>：
    /// 它用的是几十年前那个树状老对话框 —— 左边一棵树、没有地址栏、不能粘贴路径、
    /// 上不去"此电脑/最近访问"。用户反馈它不如归档页的"浏览"方便，而归档页用的是
    /// OpenFileDialog，底层就是 IFileOpenDialog，观感现代、能直接粘贴路径。
    /// 这里调同一个 COM 接口、只把 FOS_PICKFOLDERS 打开，四个"选文件夹"的地方就统一了。
    ///
    /// 若机器上不可用（例如很老的系统，或 COM 调用失败），<see cref="TryShow"/> 返回 false，
    /// 调用方回退到原来的 FolderBrowserDialog，功能不受影响。
    /// </summary>
    internal static class ModernFolderDialog
    {
        /// <summary>用户点了取消（HRESULT 0x800704C7: ERROR_CANCELLED）。</summary>
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        /// <summary>
        /// 弹出文件夹选择框。
        /// </summary>
        /// <param name="owner">父窗口。</param>
        /// <param name="title">对话框标题。</param>
        /// <param name="initialDirectory">初始目录（不存在或为空则用系统的默认位置）。</param>
        /// <param name="selectedPath">选中的目录；取消则为 null。</param>
        /// <returns>
        /// true = 对话框正常用过（含用户取消，此时 selectedPath 为 null）；
        /// false = 本机不可用，调用方应回退到 FolderBrowserDialog。
        /// </returns>
        public static bool TryShow(
            IWin32Window owner, string title, string initialDirectory, out string selectedPath)
        {
            selectedPath = null;
            IFileOpenDialog dialog = null;

            try
            {
                dialog = new FileOpenDialogRCW() as IFileOpenDialog;
                if (dialog == null)
                {
                    return false;
                }

                FILEOPENDIALOGOPTIONS options;
                if (dialog.GetOptions(out options) != 0)
                {
                    return false;
                }

                // FOS_PICKFOLDERS：这是"选文件夹"而不是"选文件"的关键开关
                // FOS_FORCEFILESYSTEM：只返回文件系统路径（我们最终要的是路径字符串）
                // FOS_PATHMUSTEXIST：不允许选一个不存在的路径
                options |= FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS
                    | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                    | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST;

                if (dialog.SetOptions(options) != 0)
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(title))
                {
                    dialog.SetTitle(title);
                }

                SetInitialFolder(dialog, initialDirectory);

                int result = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);

                if (result == ErrorCancelled)
                {
                    return true;   // 用户取消：对话框本身是好的
                }
                if (result != 0)
                {
                    return false;
                }

                IShellItem item;
                if (dialog.GetResult(out item) != 0)
                {
                    return false;
                }

                try
                {
                    IntPtr buffer;
                    if (item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out buffer) != 0)
                    {
                        return false;
                    }

                    try
                    {
                        selectedPath = Marshal.PtrToStringUni(buffer);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }

                return true;
            }
            catch (Exception)
            {
                // 老系统没有这个接口，或 COM 不可用 —— 交给调用方回退
                return false;
            }
            finally
            {
                if (dialog != null)
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
        }

        /// <summary>
        /// 不弹窗的自检：把 COM 对话框真的建出来、设一遍选项与标题，再把选项读回来确认。
        ///
        /// 弹窗本身没法在自动自检里测（模态窗口会卡住），但"能不能建、选项能不能设上"
        /// 已经能覆盖绝大多数失败情形（老系统、COM 不可用、进程不是 STA 等）。
        /// </summary>
        /// <param name="detail">给人看的说明文字。</param>
        /// <returns>可用返回 true。</returns>
        internal static bool SmokeTest(out string detail)
        {
            detail = string.Empty;
            IFileOpenDialog dialog = null;

            try
            {
                dialog = new FileOpenDialogRCW() as IFileOpenDialog;
                if (dialog == null)
                {
                    detail = "无法创建 IFileOpenDialog（COM 不可用）";
                    return false;
                }

                FILEOPENDIALOGOPTIONS options;
                if (dialog.GetOptions(out options) != 0)
                {
                    detail = "GetOptions 失败";
                    return false;
                }

                FILEOPENDIALOGOPTIONS wanted = options
                    | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS
                    | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                    | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST;

                if (dialog.SetOptions(wanted) != 0)
                {
                    detail = "SetOptions 失败";
                    return false;
                }

                if (dialog.SetTitle("自检：选择文件夹（不会真的弹出来）") != 0)
                {
                    detail = "SetTitle 失败";
                    return false;
                }

                FILEOPENDIALOGOPTIONS actual;
                if (dialog.GetOptions(out actual) != 0)
                {
                    detail = "回读选项失败";
                    return false;
                }

                bool pickFolders = (actual & FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS) != 0;
                bool forceFileSystem = (actual & FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM) != 0;

                detail = "创建/设项/回读均正常，FOS_PICKFOLDERS="
                    + (pickFolders ? "已置上" : "没置上")
                    + "，FOS_FORCEFILESYSTEM=" + (forceFileSystem ? "已置上" : "没置上");

                return pickFolders && forceFileSystem;
            }
            catch (Exception ex)
            {
                detail = "异常：" + ex.GetType().Name + "：" + ex.Message;
                return false;
            }
            finally
            {
                if (dialog != null)
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
        }

        private static void SetInitialFolder(IFileOpenDialog dialog, string initialDirectory)
        {
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                return;
            }

            try
            {
                IShellItem folder;
                SHCreateItemFromParsingName(
                    initialDirectory, IntPtr.Zero, typeof(IShellItem).GUID, out folder);

                try
                {
                    dialog.SetFolder(folder);
                }
                finally
                {
                    Marshal.ReleaseComObject(folder);
                }
            }
            catch (Exception)
            {
                // 起始目录设不上不影响使用，忽略（对话框会用自己的默认位置）
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  COM 声明（顺序即 vtable 顺序，一个字都不能改）
        // ═══════════════════════════════════════════════════════════════════

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [Flags]
        private enum FILEOPENDIALOGOPTIONS : uint
        {
            FOS_OVERWRITEPROMPT = 0x00000002,
            FOS_STRICTFILETYPES = 0x00000004,
            FOS_NOCHANGEDIR = 0x00000008,
            FOS_PICKFOLDERS = 0x00000020,
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_ALLNONSTORAGEITEMS = 0x00000080,
            FOS_NOVALIDATE = 0x00000100,
            FOS_ALLOWMULTISELECT = 0x00000200,
            FOS_PATHMUSTEXIST = 0x00000800,
            FOS_FILEMUSTEXIST = 0x00001000,
            FOS_CREATEPROMPT = 0x00002000
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW
        {
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        /// <summary>
        /// IFileOpenDialog 的方法在这里**平铺**声明（不写接口继承）。
        /// COM 接口用继承声明时方法顺序容易错位，平铺最稳：
        /// 先 IModalWindow.Show，再 IFileDialog 的全部，最后 IFileOpenDialog 自己的两个。
        /// </summary>
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // ── IModalWindow
            [PreserveSig] int Show(IntPtr parent);

            // ── IFileDialog
            [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            [PreserveSig] int SetFileTypeIndex(uint iFileType);
            [PreserveSig] int GetFileTypeIndex(out uint piFileType);
            [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOptions(FILEOPENDIALOGOPTIONS fos);
            [PreserveSig] int GetOptions(out FILEOPENDIALOGOPTIONS pfos);
            [PreserveSig] int SetDefaultFolder(IShellItem psi);
            [PreserveSig] int SetFolder(IShellItem psi);
            [PreserveSig] int GetFolder(out IShellItem ppsi);
            [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            [PreserveSig] int AddPlace(IShellItem psi, int fdap);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            [PreserveSig] int Close(int hr);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int ClearClientData();
            [PreserveSig] int SetFilter(IntPtr pFilter);

            // ── IFileOpenDialog
            [PreserveSig] int GetResults(out IntPtr ppenum);
            [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
        }
    }
}
