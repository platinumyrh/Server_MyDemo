namespace GameLockstep
{
    /// <summary>
    /// 帧同步全局常量（阶段0决策的落地）
    ///
    /// 决策记录：
    /// - 逻辑帧率 15Hz：操作手感与网络压力的平衡点（66.67ms/帧）
    /// - 输入延迟 2 帧：lockstep 经典取值——本地指令在第 N 帧产生，第 N+2 帧生效，
    ///   给所有人的指令留出到达时间（15Hz 下约 133ms）
    /// - 定点数 Q16.16：1 单位 = 1 米（精度约 1/65536 米，游戏尺度足够）
    /// - 角度量化：0..65535 表示 0..360°（uint16 环绕，无浮点）
    /// - 移动方向：由角度表示（瞄准角即移动基准，摇杆偏移在表现层换算）
    /// </summary>
    public static class LockstepConfig
    {
        // ===== 逻辑帧 =====

        /// <summary>逻辑帧率（帧/秒）。渲染层按任意帧率插值显示</summary>
        public const int TICK_RATE = 15;

        /// <summary>逻辑帧时长（毫秒）</summary>
        public const int TICK_MS = 1000 / TICK_RATE;

        /// <summary>输入延迟帧数：本地第 N 帧产生的指令在第 N+2 帧生效</summary>
        public const int INPUT_DELAY_FRAMES = 2;

        /// <summary>服务端等齐一帧指令的超时（毫秒）：超时用空指令补齐推进（不卡全场）</summary>
        public const int FRAME_TIMEOUT_MS = 200;

        /// <summary>状态哈希校验间隔（逻辑帧）：每 N 帧全量哈希一次用于漂移比对</summary>
        public const int HASH_INTERVAL = 45; // 15Hz 下每 3 秒

        /// <summary>客户端预测超前上限（帧）：最多模拟到 已确认帧+此值。
        /// 覆盖典型 RTT+输入延迟的抖动余量；服务器断流时预测自然停在此处不空转</summary>
        public const int MAX_PREDICTION_FRAMES = 8;

        // ===== 定点数（与 FP 类强耦合的常量） =====

        /// <summary>Q16.16 定点数：小数位数</summary>
        public const int FP_FRACTIONAL_BITS = 16;

        /// <summary>1.0 的定点表示</summary>
        public const long FP_ONE = 1L << FP_FRACTIONAL_BITS;

        // ===== 角度（uint16 环绕角） =====

        /// <summary>角度刻度数：0..65535 映射 0..360°</summary>
        public const int ANGLE_STEPS = 65536;

        /// <summary>角度转弧度的换算常数（double，仅在查表初始化时使用一次）</summary>
        public const double TWO_PI = 6.283185307179586476925286766559;

        // ===== 世界尺度（单位：定点数·米） =====

        /// <summary>玩家碰撞半径（米）</summary>
        public const long PLAYER_RADIUS = FP.One / 2;

        /// <summary>世界边界（±米，玩家活动范围硬限制，防逃逸）</summary>
        public const long WORLD_LIMIT = 500 * FP.One;

        // ===== 移动/战斗（从 PlayerProfileSO 的 Walker 数值迁移，逻辑层写死起步；
        //         后续可由 vehicles.json 按载具下发，各端一致即可） =====

        /// <summary>走路速度（米/逻辑帧）</summary>
        public const long WALK_SPEED_PER_TICK = 5 * FP.One / TICK_RATE;

        /// <summary>冲刺速度（米/逻辑帧）</summary>
        public const long SPRINT_SPEED_PER_TICK = 8 * FP.One / TICK_RATE;

        /// <summary>转向速度（角刻度/逻辑帧）：65536/15 ≈ 一秒转一圈</summary>
        public const int TURN_SPEED_PER_TICK = ANGLE_STEPS / TICK_RATE;

        /// <summary>跳跃初速度（米/逻辑帧，垂直）</summary>
        public const long JUMP_VELOCITY = 5 * FP.One / TICK_RATE;

        /// <summary>重力加速度（米/逻辑帧²）</summary>
        public const long GRAVITY_PER_TICK_SQ = (long)(9.81 * FP_ONE / (TICK_RATE * (double)TICK_RATE));

        /// <summary>地面高度（米）</summary>
        public const long GROUND_Y = 0;

        /// <summary>开火冷却（逻辑帧）：8发/秒 ≈ 每 2 帧一发</summary>
        public const int FIRE_COOLDOWN_TICKS = 2;

        /// <summary>弹匣容量</summary>
        public const int MAGAZINE_SIZE = 30;

        /// <summary>换弹耗时（逻辑帧）</summary>
        public const int RELOAD_TICKS = 23; // ≈1.5s

        /// <summary>最大射程（米）</summary>
        public const long MAX_RANGE = 50 * FP.One;

        /// <summary>单发伤害</summary>
        public const int DAMAGE_PER_HIT = 10;

        /// <summary>血量上限</summary>
        public const int MAX_HP = 100;
    }
}
