namespace GameLockstep
{
    /// <summary>
    /// 确定性随机数（xorshift64）
    /// 帧同步的 RNG 纪律：
    /// - 种子开局由服务端下发（GameStart 事件里带，全员一致）
    /// - 只在逻辑帧内消费，且消费顺序全端一致（按实体创建顺序/固定遍历顺序）
    /// - 表现层的随机（粒子、音效）严禁用本类——用 UnityEngine.Random
    /// </summary>
    public class DeterministicRNG
    {
        private ulong _state;

        public DeterministicRNG(ulong seed)
        {
            // 种子避免全 0（xorshift 死锁在 0）
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong State => _state;

        /// <summary>恢复内部状态（快照回滚用——只接受快照来源的值，不用于任意注入）</summary>
        public void Restore(ulong state) => _state = state;

        /// <summary>下一个 64 位随机数（xorshift64*，周期 2^64-1）</summary>
        public ulong NextULong()
        {
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>0..range-1 的随机整数（含头不含尾）</summary>
        public int NextInt(int range)
        {
            if (range <= 1) return 0;
            return (int)(NextULong() % (ulong)range);
        }

        /// <summary>0..65535 的随机角刻度（弹道散布用）</summary>
        public int NextAngle()
        {
            return (int)(NextULong() & (ulong)FPAngle.MASK);
        }
    }
}
