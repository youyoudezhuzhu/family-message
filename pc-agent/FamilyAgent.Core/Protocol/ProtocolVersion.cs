namespace FamilyAgent.Core.Protocol;

/// <summary>
/// 协议 / 客户端版本号（docs/CORE-REFACTOR-PLAN.md §3.2：取代原来硬编码在
/// <c>AgentClient.cs:23</c> 里的 <c>AgentVersion</c>）。
///
/// 为什么值得单独一个文件：这个字符串有三个对外含义，以前散落在连接的查询串、
/// 启动日志、单实例仲裁文件和窗口标题里，改名时漏一处就会「两版并存」的提示错乱。
/// <list type="bullet">
///   <item>连接查询串 <c>agent_version=</c>（服务端 <c>devices.py</c> 侧可见）；</item>
///   <item>启动日志 <c>agent=</c> 与宿主窗口标题（用户报版本时看的就是它）；</item>
///   <item>单实例仲裁文件里的版本比对（两个实例版本不一致时当面告知）。</item>
/// </list>
///
/// ⚠ 改这个值 = 一次客户端发版：单实例仲裁会拿它跟运行中的实例比，
///   不一致会弹「屏幕上跑的不是你刚双击的那份」。
/// </summary>
public static class ProtocolVersion
{
    /// <summary>对外暴露的版本号（原 <c>AgentClient.ReportedVersion</c>）。</summary>
    public const string AgentVersion = "cs-0.20.0";
}
