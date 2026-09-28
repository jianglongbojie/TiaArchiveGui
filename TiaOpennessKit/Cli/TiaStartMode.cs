namespace TiaOpennessKit.Cli
{
    /// <summary>
    /// TIA Portal 实例的获取方式。
    ///
    /// ★ 这个枚举是**共享内核**的一部分（不是"命令行工具专属"）：
    ///   TiaSession.Create() 的入参就是它，界面版与任何新工具都要用。
    ///   原先它放在命令行版的 Options.cs 里，命令行版归档后，
    ///   连同 <see cref="TiaOpennessKit.Cli.TiaStartMode"/> 一起精简到这里，
    ///   避免把整套命令行解析器（以及它的帮助文本）带到共享内核里当死代码。
    /// </summary>
    public enum TiaStartMode
    {
        /// <summary>启动新的无界面实例（默认，适合批处理/CI）。</summary>
        WithoutUserInterface = 0,

        /// <summary>启动带界面的实例（适合手动观察 TIA 在做什么）。</summary>
        WithUserInterface = 1,

        /// <summary>附加到已经在运行的 TIA Portal 实例。</summary>
        AttachExisting = 2
    }
}
