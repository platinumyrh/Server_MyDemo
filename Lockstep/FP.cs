using System;

namespace GameLockstep
{
    /// <summary>
    /// Q16.16 定点数（long 底座）
    /// 帧同步确定性数学的核心：跨机器位一致（整数运算无浮点漂移）
    ///
    /// 表示范围：±32768，精度 1/65536 米——游戏尺度足够
    /// 使用约束（违反会破坏确定性）：
    /// - 逻辑代码禁止出现 float/double（本类内部除查表初始化外全部整数运算）
    /// - 除法有截断：a/b 与 b/a 顺序敏感，交换律不成立——写逻辑时注意顺序固定
    /// </summary>
    public readonly struct FP : IEquatable<FP>, IComparable<FP>
    {
        /// <summary>原始定点值（Q16.16）</summary>
        public readonly long Raw;

        public const long One = LockstepConfig.FP_ONE;
        public const long Zero = 0;

        public FP(long raw) => Raw = raw;

        // ===== 基础 =====

        public static FP FromRaw(long raw) => new FP(raw);
        public static explicit operator long(FP v) => v.Raw >> LockstepConfig.FP_FRACTIONAL_BITS; // 截断取整
        public static explicit operator int(FP v) => (int)(v.Raw >> LockstepConfig.FP_FRACTIONAL_BITS);

        /// <summary>从整数构造</summary>
        public static FP FromInt(long v) => new FP(v << LockstepConfig.FP_FRACTIONAL_BITS);

        /// <summary>仅限表现层/初始化使用！逻辑代码禁止用浮点构造</summary>
        public static FP FromFloat(float f) => new FP((long)(f * One));

        /// <summary>仅限表现层使用</summary>
        public float ToFloat() => Raw / (float)One;

        // ===== 运算（全部整数实现，确定性） =====

        public static FP operator +(FP a, FP b) => new FP(a.Raw + b.Raw);
        public static FP operator -(FP a, FP b) => new FP(a.Raw - b.Raw);
        public static FP operator -(FP a) => new FP(-a.Raw);
        public static FP operator *(FP a, FP b) => new FP(a.Raw * b.Raw >> LockstepConfig.FP_FRACTIONAL_BITS);
        public static FP operator /(FP a, FP b) => new FP((a.Raw << LockstepConfig.FP_FRACTIONAL_BITS) / b.Raw);

        public static FP operator *(FP a, long b) => new FP(a.Raw * b);
        public static FP operator /(FP a, long b) => new FP(a.Raw / b);

        public bool IsZero => Raw == 0;

        // ===== 比较 =====

        public static bool operator ==(FP a, FP b) => a.Raw == b.Raw;
        public static bool operator !=(FP a, FP b) => a.Raw != b.Raw;
        public static bool operator >(FP a, FP b) => a.Raw > b.Raw;
        public static bool operator <(FP a, FP b) => a.Raw < b.Raw;
        public static bool operator >=(FP a, FP b) => a.Raw >= b.Raw;
        public static bool operator <=(FP a, FP b) => a.Raw <= b.Raw;

        public bool Equals(FP other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is FP other && Raw == other.Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(FP other) => Raw.CompareTo(other.Raw);

        // ===== 常用函数（整数实现） =====

        public static FP Min(FP a, FP b) => a.Raw < b.Raw ? a : b;
        public static FP Max(FP a, FP b) => a.Raw > b.Raw ? a : b;
        public static FP Abs(FP a) => a.Raw < 0 ? -a : a;
        public static FP Clamp(FP v, FP min, FP max) => v.Raw < min.Raw ? min : (v.Raw > max.Raw ? max : v);

        /// <summary>平方距离（long 可能溢出的场景用）——两个 ±300 内的数平方在 Q16.16 下安全</summary>
        public static long SqrDistance(FP ax, FP az, FP bx, FP bz)
        {
            long dx = ax.Raw - bx.Raw;
            long dz = az.Raw - bz.Raw;
            return dx * dx + dz * dz;
        }

        /// <summary>平方根（牛顿迭代整数实现，确定性）。
        /// 输入约定：SqrDistance 的返回值（差值平方和 = 值×2^32 域）——
        /// isqrt(值×2^32) 恰好 = sqrt(值)×2^16，结果天然就是 Q16.16，无需任何移位</summary>
        public static FP Sqrt(long raw)
        {
            if (raw <= 0) return new FP(0);

            long x = raw;
            long y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + raw / x) / 2;
            }
            return new FP(x);
        }

        public override string ToString() => $"{Raw / (double)One:F4}";
    }

    /// <summary>
    /// 定点向量2（XZ 平面，帧同步世界坐标）
    /// </summary>
    public struct FPVector2
    {
        public FP X, Z;

        public FPVector2(FP x, FP z)
        {
            X = x;
            Z = z;
        }

        public static FPVector2 Zero => new FPVector2(new FP(0), new FP(0));

        public static FPVector2 operator +(FPVector2 a, FPVector2 b) => new FPVector2(a.X + b.X, a.Z + b.Z);
        public static FPVector2 operator -(FPVector2 a, FPVector2 b) => new FPVector2(a.X - b.X, a.Z - b.Z);
        public static FPVector2 operator *(FPVector2 a, FP b) => new FPVector2(a.X * b, a.Z * b);

        public FP SqrMagnitude => new FP(FP.SqrDistance(X, Z, FPVector2.Zero.X, FPVector2.Zero.Z) >> LockstepConfig.FP_FRACTIONAL_BITS); // SqrDistance 是值×2^32 域，右移 16 位才是 Q16.16

        public FPVector2 Normalized
        {
            get
            {
                long sqr = FP.SqrDistance(X, Z, Zero.X, Zero.Z);
                if (sqr == 0) return Zero;
                var len = FP.Sqrt(sqr);
                return new FPVector2(X / len, Z / len);
            }
        }
    }

    /// <summary>
    /// 环绕角度：0..65535 映射 0..360°（uint16 环绕）
    /// 用 int 存储但参与运算时 mask 到 16 位，保证跨机器一致
    /// </summary>
    public static class FPAngle
    {
        public const int MASK = LockstepConfig.ANGLE_STEPS - 1;

        /// <summary>角度差（考虑环绕，结果在 -32768..32767，正值=顺时针）</summary>
        public static int Delta(int from, int to)
        {
            // C# 的 & 按二进制补码取低 16 位：负差值的结果同样落在 0..65535，天然完成环绕取模
            int d = (to - from) & MASK;
            if (d >= LockstepConfig.ANGLE_STEPS / 2) d -= LockstepConfig.ANGLE_STEPS;
            return d;
        }

        /// <summary>从角度得到单位方向向量（查表）</summary>
        public static FPVector2 ToDirection(int angle)
        {
            return FPTrig.Dir(angle);
        }

        /// <summary>仅表现层：float 角度（度）→ 量化角</summary>
        public static int FromDegrees(float degrees)
        {
            long a = (long)(degrees / 360f * LockstepConfig.ANGLE_STEPS);
            return (int)(a & MASK);
        }

        /// <summary>仅表现层：量化角 → float 角度（度）</summary>
        public static float ToDegrees(int angle)
        {
            return angle * 360f / LockstepConfig.ANGLE_STEPS;
        }
    }

    /// <summary>
    /// 确定性三角函数：静态构造时用 double 生成查表（一次性），
    /// 逻辑帧内查表取值——整数索引，跨机器位一致
    /// </summary>
    public static class FPTrig
    {
        private static readonly FP[] _cosTable;
        private static readonly FP[] _dirX;
        private static readonly FP[] _dirZ;

        // 方向查表精度：每 64 个角刻度一个条目（65536/64 = 1024 条），误差 < 0.006°，足够
        private const int TABLE_DIV = 64;
        private const int TABLE_SIZE = LockstepConfig.ANGLE_STEPS / TABLE_DIV;

        static FPTrig()
        {
            _cosTable = new FP[TABLE_SIZE + 1];
            _dirX = new FP[TABLE_SIZE];
            _dirZ = new FP[TABLE_SIZE];

            for (int i = 0; i < TABLE_SIZE; i++)
            {
                // 量化角 → 弧度（只在初始化时用 double）
                double rad = i * TABLE_DIV * LockstepConfig.TWO_PI / LockstepConfig.ANGLE_STEPS;
                double cx = Math.Cos(rad);
                double cz = Math.Sin(rad);

                // 直接建方向表（Unity 约定：0° 朝 +Z，角度绕 Y 顺时针→x=sin, z=cos）
                _dirX[i] = new FP((long)Math.Round(Math.Sin(rad) * FP.One));
                _dirZ[i] = new FP((long)Math.Round(Math.Cos(rad) * FP.One));
                _cosTable[i] = new FP((long)Math.Round(cx * FP.One));
            }
            _cosTable[TABLE_SIZE] = _cosTable[0];
        }

        /// <summary>角刻度 → 单位方向（XZ 平面，0° 朝 +Z）</summary>
        public static FPVector2 Dir(int angle)
        {
            int idx = (angle & FPAngle.MASK) / TABLE_DIV;
            return new FPVector2(_dirX[idx], _dirZ[idx]);
        }

        /// <summary>角刻度 → cos（-1..1）</summary>
        public static FP Cos(int angle)
        {
            int idx = (angle & FPAngle.MASK) / TABLE_DIV;
            return _cosTable[idx];
        }

        /// <summary>角刻度 → sin（-1..1）</summary>
        public static FP Sin(int angle)
        {
            // sin(a) = cos(a - 90°)
            return Cos((angle - LockstepConfig.ANGLE_STEPS / 4) & FPAngle.MASK);
        }
    }
}
