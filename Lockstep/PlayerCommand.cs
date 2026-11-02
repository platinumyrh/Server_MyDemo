namespace GameLockstep
{
    /// <summary>
    /// 玩家单帧输入指令（量化、定长——阶段5 直接按字段映射 protobuf）
    ///
    /// 量化约定：
    /// - MoveX/MoveZ：世界空间移动轴，-127..127。客户端负责把"摄像机相对输入"换算成世界空间后量化，
    ///   逻辑层只认世界空间（表现层的摄像机朝向不进逻辑）
    /// - AimAngle：0..65535 环绕角（朝向/瞄准角）
    /// - Buttons：按键位域
    /// 超出域的值逻辑层按边界钳制处理，不抛异常——异常路径会破坏确定性
    /// </summary>
    public struct PlayerCommand
    {
        public int MoveX;
        public int MoveZ;
        public int AimAngle;
        public byte Buttons;

        public const byte BTN_FIRE = 1 << 0;
        public const byte BTN_SPRINT = 1 << 1;
        public const byte BTN_RELOAD = 1 << 2;
        public const byte BTN_JUMP = 1 << 3;

        public static PlayerCommand Empty => default;

        public bool Has(byte button) => (Buttons & button) != 0;

        /// <summary>仅表现层调用：float 轴（-1..1）→ 量化整数轴</summary>
        public static int QuantizeAxis(float axis)
        {
            if (axis > 1f) axis = 1f;
            if (axis < -1f) axis = -1f;
            return (int)(axis * 127f);
        }
    }
}
