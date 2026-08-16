namespace Server_MyDemo.Models
{
    /// <summary>
    /// 输入状态（服务器内部使用，用于帧同步）
    /// </summary>
    public class InputState
    {
        // 移动输入
        public float MoveX { get; set; }  // 水平方向 (-1 到 1)
        public float MoveY { get; set; }  // 垂直方向 (-1 到 1)

        // 状态标志
        public bool IsSprinting { get; set; }  // 冲刺
        public bool IsAiming { get; set; }     // 瞄准

        // 动作按钮
        public bool Fire { get; set; }         // 开火
        public bool Reload { get; set; }       // 换弹
        public bool Jump { get; set; }         // 跳跃
        public bool Interact { get; set; }     // 交互

        // 视角
        public float Rotation { get; set; }    // 角度（弧度）

        /// <summary>
        /// 创建默认输入状态（无输入）
        /// </summary>
        public static InputState Default => new InputState();

        /// <summary>
        /// 检查是否有任何输入
        /// </summary>
        public bool HasInput()
        {
            return Math.Abs(MoveX) > 0.01f ||
                   Math.Abs(MoveY) > 0.01f ||
                   IsSprinting ||
                   IsAiming ||
                   Fire ||
                   Reload ||
                   Jump ||
                   Interact;
        }
    }
}
