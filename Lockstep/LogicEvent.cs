namespace GameLockstep
{
    public enum LogicEventType : byte
    {
        Fired = 0, // 开火（TargetId=0 表示未命中任何人）
        Hit = 1,   // 命中落点（受击方视角的受击方向由此推算）
        Died = 2,  // 死亡（ShooterId=击杀者，TargetId=阵亡者）
    }

    /// <summary>
    /// 逻辑帧事件：Tick 内产生、表现层逐帧消费的瞬时事件（枪口特效/弹道/受击动画/击杀播报）。
    /// 由确定性模拟在全端各自生成，不走网络——替代旧链路的 S2CFireEvent。
    /// 注意：回滚重演会丢弃并重新生成事件，表现层消费时需容忍重放（阶段7 做去重），
    /// 因此事件只携带表现所需数据，不参与状态哈希
    /// </summary>
    public struct LogicEvent
    {
        public LogicEventType Type;
        public int Frame;
        public int ShooterId;
        public int TargetId;
        public FP OriginX;
        public FP OriginZ;
        public FP DirX;
        public FP DirZ;
        public FP HitX;
        public FP HitZ;
        public int Damage;
    }
}
