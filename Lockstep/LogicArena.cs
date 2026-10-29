namespace GameLockstep
{
    /// <summary>
    /// 确定性静态几何：带朝向的盒子（OBB，只看 XZ 平面），负责遮挡弹道和阻挡走位。
    ///
    /// 为什么不用 Unity Physics：命中结果会参与伤害结算，必须跨端位一致，
    /// 所以墙的判定只能走定点数，不能碰引擎碰撞体/射线。
    /// 这张表就是"地图"——场景里改了墙，跑 Tools/Lockstep/生成墙体 OBB 代码 重新生成并粘贴。
    ///
    /// 朝向用 "中心 + 半尺寸 + 朝向角的 cos/sin" 表示：射线/圆心先转到盒子局部空间，
    /// 在那里盒子退化成轴对齐盒子，算完再转回世界——所以斜着摆的墙也是精确的，
    /// 而不是用外接大矩形近似。
    /// </summary>
    public static class LogicArena
    {
        public readonly struct Box2
        {
            public readonly FP CenterX;
            public readonly FP CenterZ;
            public readonly FP HalfX;
            public readonly FP HalfZ;

            /// <summary>朝向的余弦/正弦（Unity 的 yaw：+Z 转向 +X 为正）</summary>
            public readonly FP Cos;
            public readonly FP Sin;

            private Box2(FP centerX, FP centerZ, FP halfX, FP halfZ, FP cos, FP sin)
            {
                CenterX = centerX;
                CenterZ = centerZ;
                HalfX = halfX;
                HalfZ = halfZ;
                Cos = cos;
                Sin = sin;
            }

            /// <summary>
            /// 用世界坐标的中心、尺寸和绕 Y 的偏转角（度）构造。
            /// 只用 double 在静态初始化时算一次 cos/sin：查表粒度是 0.35°，
            /// 23m 长的墙端点会偏 7cm，弹孔就会浮在墙外，所以这里不用 FPTrig
            /// </summary>
            public static Box2 FromCenterSize(float centerX, float centerZ, float sizeX, float sizeZ, float yawDegrees = 0f)
            {
                double rad = yawDegrees * System.Math.PI / 180.0;
                return new Box2(
                    FP.FromFloat(centerX),
                    FP.FromFloat(centerZ),
                    FP.FromFloat(sizeX) / FP.FromInt(2),
                    FP.FromFloat(sizeZ) / FP.FromInt(2),
                    new FP((long)System.Math.Round(System.Math.Cos(rad) * FP.One)),
                    new FP((long)System.Math.Round(System.Math.Sin(rad) * FP.One)));
            }

            /// <summary>世界坐标 → 盒子局部坐标（局部原点在盒心，轴对齐）</summary>
            public void ToLocal(FP worldX, FP worldZ, out FP localX, out FP localZ)
            {
                var dx = worldX - CenterX;
                var dz = worldZ - CenterZ;
                localX = dx * Cos - dz * Sin;
                localZ = dx * Sin + dz * Cos;
            }

            /// <summary>点是否落在盒内（世界坐标，含朝向）</summary>
            public bool Contains(FP worldX, FP worldZ)
            {
                ToLocal(worldX, worldZ, out var lx, out var lz);
                return FP.Abs(lx) < HalfX && FP.Abs(lz) < HalfZ;
            }
        }

        /// <summary>Main 场景里的全部墙体（Y 方向忽略：子弹始终在墙体高度范围内）</summary>
        public static readonly Box2[] Walls =
        {
            // Wall：Enviroment(0,-0.5,0) 下，本地位置 (-6.83, 1.93, 2.05)，
            //       缩放 (23.451479, 3.1118, 1)，绕 Y 偏转 138.32°
            Box2.FromCenterSize(-6.83f, 2.05f, 23.451479f, 1f, 138.32f),
        };

        /// <summary>
        /// 射线 vs 全部墙体。返回最近命中距离（Q16.16），未命中返回 maxT。
        /// 法线以定点单位向量输出
        /// </summary>
        public static FP RaycastWalls(FP ox, FP oz, FPVector2 dir, FP maxT, out FP normalX, out FP normalZ)
        {
            var best = maxT;
            normalX = new FP(0);
            normalZ = new FP(0);

            for (int i = 0; i < Walls.Length; i++)
            {
                var t = RaycastBox(Walls[i], ox, oz, dir, best, out var nx, out var nz);
                if (t >= best) continue;
                best = t;
                normalX = nx;
                normalZ = nz;
            }

            return best;
        }

        /// <summary>
        /// 射线 vs 单个 OBB（2D slab 法）。返回最近命中距离，未命中返回 maxT。
        /// 起点落在盒子内部时不判定（避免贴脸 0 距离命中）
        /// </summary>
        public static FP RaycastBox(Box2 box, FP ox, FP oz, FPVector2 dir, FP maxT,
                                    out FP normalX, out FP normalZ)
        {
            normalX = new FP(0);
            normalZ = new FP(0);

            // 把射线转到盒子局部空间：盒子变成以原点为中心、轴对齐的 [-Half, +Half]
            box.ToLocal(ox, oz, out var lox, out var loz);
            var ldx = dir.X * box.Cos - dir.Z * box.Sin;
            var ldz = dir.X * box.Sin + dir.Z * box.Cos;

            // 两个轴各算一次进入/离开参数，取交集；哨兵值代表无穷
            var tNear = FP.FromInt(-1000000);
            var tFar = FP.FromInt(1000000);
            var nearNormX = new FP(0);
            var nearNormZ = new FP(0);

            // ---- X 轴 slab ----
            if (ldx.IsZero)
            {
                if (lox < -box.HalfX || lox > box.HalfX) return maxT; // 与该 slab 平行且在外侧
            }
            else
            {
                var t1 = (-box.HalfX - lox) / ldx;
                var t2 = (box.HalfX - lox) / ldx;
                if (t1 <= t2)
                {
                    if (t1 > tNear) { tNear = t1; nearNormX = FP.FromInt(-1); nearNormZ = new FP(0); }
                    if (t2 < tFar) tFar = t2;
                }
                else
                {
                    if (t2 > tNear) { tNear = t2; nearNormX = FP.FromInt(1); nearNormZ = new FP(0); }
                    if (t1 < tFar) tFar = t1;
                }
            }

            // ---- Z 轴 slab ----
            if (ldz.IsZero)
            {
                if (loz < -box.HalfZ || loz > box.HalfZ) return maxT;
            }
            else
            {
                var t1 = (-box.HalfZ - loz) / ldz;
                var t2 = (box.HalfZ - loz) / ldz;
                if (t1 <= t2)
                {
                    if (t1 > tNear) { tNear = t1; nearNormX = new FP(0); nearNormZ = FP.FromInt(-1); }
                    if (t2 < tFar) tFar = t2;
                }
                else
                {
                    if (t2 > tNear) { tNear = t2; nearNormX = new FP(0); nearNormZ = FP.FromInt(1); }
                    if (t1 < tFar) tFar = t1;
                }
            }

            if (tNear > tFar) return maxT; // 两段区间不相交
            if (tFar.Raw < 0) return maxT; // 盒子在身后
            if (tNear.Raw < 0) return maxT; // 起点在盒子内部
            if (tNear >= maxT) return maxT;

            // 局部法线转回世界
            normalX = nearNormX * box.Cos + nearNormZ * box.Sin;
            normalZ = -nearNormX * box.Sin + nearNormZ * box.Cos;

            return tNear;
        }

        /// <summary>
        /// 圆 vs 单个 OBB：圆心若与盒子重叠，推到盒外（只做位置修正，不改速度）。
        /// 贴墙走时会自然产生沿墙面的滑动。返回是否发生了修正
        /// </summary>
        public static bool ResolveCircle(Box2 box, ref FP x, ref FP z, FP radius)
        {
            // 转到局部空间，问题退化成"圆 vs 轴对齐盒子"
            box.ToLocal(x, z, out var lx, out var lz);

            var closestX = FP.Clamp(lx, -box.HalfX, box.HalfX);
            var closestZ = FP.Clamp(lz, -box.HalfZ, box.HalfZ);

            long distSqRaw = FP.SqrDistance(lx, lz, closestX, closestZ); // 值×2^32 域
            long radiusSqRaw = radius.Raw * radius.Raw;
            if (distSqRaw >= radiusSqRaw) return false;

            if (distSqRaw == 0)
            {
                // 圆心已经在盒内（走进墙里）：沿穿透最浅的一面推出去，
                // 平手按 X- → X+ → Z- → Z+ 固定顺序（确定性）
                var toMinX = lx + box.HalfX;
                var toMaxX = box.HalfX - lx;
                var toMinZ = lz + box.HalfZ;
                var toMaxZ = box.HalfZ - lz;

                var best = toMinX;
                int axis = 0;
                if (toMaxX < best) { best = toMaxX; axis = 1; }
                if (toMinZ < best) { best = toMinZ; axis = 2; }
                if (toMaxZ < best) { axis = 3; }

                switch (axis)
                {
                    case 0: lx = -box.HalfX - radius; break;
                    case 1: lx = box.HalfX + radius; break;
                    case 2: lz = -box.HalfZ - radius; break;
                    default: lz = box.HalfZ + radius; break;
                }
            }
            else
            {
                var dist = FP.Sqrt(distSqRaw); // Q16.16
                var push = radius - dist;
                lx += (lx - closestX) / dist * push; // 单位分离向量 × 穿透深度
                lz += (lz - closestZ) / dist * push;
            }

            // 局部 → 世界（旋转保持长度，圆还是圆）
            x = box.CenterX + lx * box.Cos + lz * box.Sin;
            z = box.CenterZ - lx * box.Sin + lz * box.Cos;
            return true;
        }
    }
}
