namespace GameLockstep
{
    /// <summary>
    /// 逻辑世界快照：帧号 + RNG 状态 + 全玩家数值状态的深拷贝。
    /// 客户端回滚重演的锚点——恢复到分歧帧的前一帧，再用修正后的输入重放到当前。
    /// 注意：LogicPlayer 新增状态字段时必须同步补进 PlayerSnap，否则回滚会丢状态
    /// </summary>
    public sealed class LogicWorldSnapshot
    {
        internal long Frame;
        internal ulong RngState;
        internal PlayerSnap[] Players;
    }

    /// <summary>单个玩家的快照切片（全部原始数值，无对象引用）</summary>
    internal struct PlayerSnap
    {
        internal int PlayerId;
        internal long X;
        internal long Z;
        internal long Y;
        internal long VerticalVelocity;
        internal int Yaw;
        internal int MaxHp;
        internal int Hp;
        internal int Ammo;
        internal int FireCooldownTicks;
        internal int ReloadTicksLeft;
        internal bool IsDead;
    }
}
