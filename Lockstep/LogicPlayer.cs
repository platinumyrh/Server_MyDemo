using System;

namespace GameLockstep
{
    /// <summary>
    /// 逻辑玩家：纯数值状态，不含任何 Unity 引用（客户端/服务端共用）
    /// 阶段2 覆盖：转向/跳跃/水平移动/圆形碰撞参与；血量、弹匣、冷却字段已就位，结算逻辑阶段4 填充
    /// </summary>
    public class LogicPlayer
    {
        public readonly int PlayerId;

        // ===== 空间状态 =====
        public FP X;
        public FP Z;
        public FP Y;                // 垂直高度（0 = 地面）
        public FP VerticalVelocity; // 米/逻辑帧
        public int Yaw;             // 朝向角刻度 0..65535

        // ===== 战斗状态（阶段4 启用结算）=====
        public int MaxHp;
        public int Hp;
        public int Ammo;              // 弹匣余量
        public int FireCooldownTicks; // 距下次可开火
        public int ReloadTicksLeft;   // 换弹剩余
        public bool IsDead;

        public bool IsGrounded => Y.Raw == LockstepConfig.GROUND_Y && VerticalVelocity.Raw == 0;

        public LogicPlayer(int playerId, FP x, FP z, int yaw, int maxHp)
        {
            PlayerId = playerId;
            X = x;
            Z = z;
            Y = new FP(0);
            VerticalVelocity = new FP(0);
            Yaw = yaw & FPAngle.MASK;
            MaxHp = maxHp;
            Hp = maxHp;
            Ammo = LockstepConfig.MAGAZINE_SIZE;
        }

        /// <summary>单帧移动积分。死亡不响应任何指令（旧链路 IsLocalDead 行为的确定性版本）</summary>
        public void ApplyMovement(in PlayerCommand cmd)
        {
            if (IsDead) return;

            // 1. 转向：朝瞄准角限速旋转，接近后贴合不超调
            int delta = FPAngle.Delta(Yaw, cmd.AimAngle & FPAngle.MASK);
            int step = Math.Abs(delta) < LockstepConfig.TURN_SPEED_PER_TICK
                ? delta
                : Math.Sign(delta) * LockstepConfig.TURN_SPEED_PER_TICK;
            Yaw = (Yaw + step) & FPAngle.MASK;

            // 2. 垂直：起跳 + 重力，落地钳到地面
            if (cmd.Has(PlayerCommand.BTN_JUMP) && IsGrounded)
                VerticalVelocity = FP.FromRaw(LockstepConfig.JUMP_VELOCITY);
            VerticalVelocity -= FP.FromRaw(LockstepConfig.GRAVITY_PER_TICK_SQ);
            Y += VerticalVelocity;
            if (Y.Raw < LockstepConfig.GROUND_Y)
            {
                Y = FP.FromRaw(LockstepConfig.GROUND_Y);
                VerticalVelocity = new FP(0);
            }

            // 3. 水平：输入轴（-127..127 → 定点）超过单位圆时归一，模拟摇杆斜向不加速
            var input = new FPVector2(
                FP.FromRaw(ClampAxis(cmd.MoveX) * LockstepConfig.FP_ONE / 127),
                FP.FromRaw(ClampAxis(cmd.MoveZ) * LockstepConfig.FP_ONE / 127));
            if (input.SqrMagnitude > FP.FromRaw(LockstepConfig.FP_ONE))
                input = input.Normalized;

            var speed = FP.FromRaw(cmd.Has(PlayerCommand.BTN_SPRINT)
                ? LockstepConfig.SPRINT_SPEED_PER_TICK
                : LockstepConfig.WALK_SPEED_PER_TICK);
            X += input.X * speed;
            Z += input.Z * speed;
        }

        private static int ClampAxis(int v)
        {
            if (v > 127) return 127;
            if (v < -127) return -127;
            return v;
        }
    }
}
