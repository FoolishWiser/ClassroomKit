using System.Drawing;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 自绘弹窗的控件底色工具。
/// WinForms 的 <see cref="Forms.Control"/> 默认没有声明
/// ControlStyles.SupportsTransparentBackColor，给它设 alpha&lt;255 的 BackColor 会抛
/// ArgumentException（"Control does not support transparent background colors."）；
/// 异常发生在构造函数里时，整个对话框都建不出来（点预览就像没反应）。
/// 这些弹窗的背景本来就是纯色，所以子控件直接用父级同色——视觉与透明等价，且任何控件都接受。
/// </summary>
internal static class DialogSurface
{
    /// <summary>父级是纯色时用同色代替透明：视觉等价，永远不会抛。</summary>
    public static void FillWith(Forms.Control control, Color parentBack)
    {
        // 兜底：即使父色本身带了透明度（主题改坏），也绝不让构造失败。
        try
        {
            control.BackColor = parentBack;
        }
        catch (ArgumentException)
        {
            control.BackColor = SystemColors.Control;
        }
    }

}
