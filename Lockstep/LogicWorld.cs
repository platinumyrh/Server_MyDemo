using System.Collections.Generic;

namespace GameLockstep
{
    /// <summary>
    /// 逻辑世界：固定步长确定性模拟器（纯 C#，客户端/服务端共用同一份源码——阶段6 拷贝到服务端）
    ///
    /// Tick 内固定顺序（全端必须一致，改动顺序 = 改协议）：
    ///   1. 按 PlayerId 升序逐个移动积分（缺指令 = 空指令，站桩）
    ///   2. 战斗结算（冷却/换弹/开火 hitscan/伤害/死亡，基于移动后的位置）
    ///   3. 圆形碰撞互推（跑两轮缓解多人堆叠残留，配对顺序按 id 升序）→ 静态墙体推出
    ///   4. 世界边界钳制
    /// 输入延迟/本地预测/回滚重演（阶段3）在调用方完成——本类只消费"当前帧指令"，可重入重演
    /// </summary>
    public sealed class LogicWorld
    {
        private readonly List<LogicPlayer> _players = new List<LogicPlayer>(); // 始终按 PlayerId 升序
        private readonly Dictionary<int, LogicPlayer> _byId = new Dictionary<int, LogicPlayer>();
        private readonly List<LogicEvent> _tickEvents = new List<LogicEvent>();

        public long Frame { get; private set; }//帧号
        public DeterministicRNG Rng { get; }
        public IReadOnlyList<LogicPlayer> Players => _players;
        /// <summary>本帧产生的事件（Tick 开头清空）。回滚重演会重新生成，表现层需容忍重放</summary>
        public IReadOnlyList<LogicEvent> TickEvents => _tickEvents;

        public LogicWorld(ulong rngSeed)
        {
            Rng = new DeterministicRNG(rngSeed);
        }

        public LogicPlayer AddPlayer(int playerId, FP x, FP z, int yaw, int maxHp)
        {
            if (_byId.TryGetValue(playerId, out var existed))
                return existed; // 重复加入幂等（重连/快照恢复场景）

            var p = new LogicPlayer(playerId, x, z, yaw, maxHp);
            _byId[playerId] = p;
            _players.Add(p);
            _players.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId)); // 遍历顺序 = 确定性顺序
            return p;
        }

        /// <summary>断线移除（阶段6 中继侧调用）</summary>
        public bool RemovePlayer(int playerId)
        {
            if (!_byId.Remove(playerId)) return false;
            _players.RemoveAll(p => p.PlayerId == playerId);
            return true;
        }

        public LogicPlayer GetPlayer(int playerId) =>
            _byId.TryGetValue(playerId, out var p) ? p : null;

        /// <summary>推进一个逻辑帧</summary>
        public void Tick(IReadOnlyDictionary<int, PlayerCommand> commands)
        {
            Frame++;
            _tickEvents.Clear();

            // 0. 复活计时（在移动/战斗之前：复活当帧就能响应指令）
            ProcessRespawns();

            // 1. 移动积分（缺指令按空指令处理，不卡帧）
            for (int i = 0; i < _players.Count; i++)
            {
                commands.TryGetValue(_players[i].PlayerId, out var cmd);
                _players[i].ApplyMovement(cmd);
            }

            // 2. 战斗结算（基于移动后的位置；命中判定先于碰撞互推）
            for (int i = 0; i < _players.Count; i++)
            {
                commands.TryGetValue(_players[i].PlayerId, out var cmd);
                ProcessCombat(_players[i], cmd);
            }

            // 3. 圆形碰撞互推（两轮缓解多人堆叠残留），再处理静态墙体
            ResolveCollisions();
            ResolveCollisions();
            ResolveWalls();

            // 4. 世界边界
            var limit = FP.FromRaw(LockstepConfig.WORLD_LIMIT);
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p.X > limit) p.X = limit;
                if (p.X < -limit) p.X = -limit;
                if (p.Z > limit) p.Z = limit;
                if (p.Z < -limit) p.Z = -limit;
            }
        }

        /// <summary>
        /// 单玩家战斗结算：冷却/换弹推进 → 换弹判定 → 开火判定 → hitscan → 伤害/死亡。
        /// 死亡者只让冷却收敛，不再有任何主动行为
        /// </summary>
        private void ProcessCombat(LogicPlayer p, in PlayerCommand cmd)
        {
            if (p.FireCooldownTicks > 0) p.FireCooldownTicks--;
            if (p.IsDead) return;

            if (p.ReloadTicksLeft > 0)
            {
                p.ReloadTicksLeft--;
                if (p.ReloadTicksLeft == 0)
                    p.Ammo = LockstepConfig.MAGAZINE_SIZE;
                return; // 换弹中不能开火
            }

            if (cmd.Has(PlayerCommand.BTN_RELOAD) && p.Ammo < LockstepConfig.MAGAZINE_SIZE)
            {
                p.ReloadTicksLeft = LockstepConfig.RELOAD_TICKS;
                return;
            }

            if (p.FireCooldownTicks > 0) return; // 开火冷却未到（不拦换弹）

            if (!cmd.Has(PlayerCommand.BTN_FIRE)) return;

            if (p.Ammo <= 0)
            {
                p.ReloadTicksLeft = LockstepConfig.RELOAD_TICKS; // 空仓自动换弹
                return;
            }

            // 开火：定点数 hitscan——先查墙，再沿朝向对其他存活玩家做圆形相交，最近者受击
            p.Ammo--;
            p.FireCooldownTicks = LockstepConfig.FIRE_COOLDOWN_TICKS;

            var dir = FPTrig.Dir(p.Yaw);
            var target = Hitscan(p, dir, out var hitX, out var hitZ, out var hitWall, out var hitNX, out var hitNZ);

            var fired = new LogicEvent//添加一个开火事件到事件列表
            {
                Type = LogicEventType.Fired,
                Frame = (int)Frame,
                ShooterId = p.PlayerId,
                TargetId = target?.PlayerId ?? 0,
                OriginX = p.X,
                OriginZ = p.Z,
                DirX = dir.X,
                DirZ = dir.Z,
                HitX = hitX,
                HitZ = hitZ,
                HitWall = hitWall,
                HitNX = hitNX,
                HitNZ = hitNZ,
                Damage = target != null ? LockstepConfig.DAMAGE_PER_HIT : 0,
            };
            _tickEvents.Add(fired);

            if (target == null) return;

            target.Hp -= LockstepConfig.DAMAGE_PER_HIT;
            _tickEvents.Add(new LogicEvent//添加一个命中事件到事件列表
            {
                Type = LogicEventType.Hit,
                Frame = (int)Frame,
                ShooterId = p.PlayerId,
                TargetId = target.PlayerId,
                OriginX = p.X,
                OriginZ = p.Z,
                DirX = dir.X,
                DirZ = dir.Z,
                HitX = hitX,
                HitZ = hitZ,
                Damage = LockstepConfig.DAMAGE_PER_HIT,
            });

            if (target.Hp <= 0)
            {
                target.Hp = 0;
                target.IsDead = true;
                target.RespawnTicksLeft = LockstepConfig.RESPAWN_TICKS;
                target.Deaths++;
                p.Kills++;
                _tickEvents.Add(new LogicEvent//添加一个死亡事件到事件列表
                {
                    Type = LogicEventType.Died,
                    Frame = (int)Frame,
                    ShooterId = p.PlayerId,
                    TargetId = target.PlayerId,
                    Damage = LockstepConfig.DAMAGE_PER_HIT,
                });
            }
        }

        /// <summary>
        /// 定点 hitscan：射线（射程 MAX_RANGE）先与静态墙体相交，再检测其他存活玩家圆。
        /// 玩家命中点为圆心在射线上的最近投影点；距离相同取 PlayerId 靠前者（确定性平手规则）。
        /// 墙挡住时 hitWall=true，HitX/HitZ 为墙面交点、HitNX/HitNZ 为墙面法线
        /// </summary>
        private LogicPlayer Hitscan(LogicPlayer shooter, FPVector2 dir,
                                    out FP hitX, out FP hitZ,
                                    out bool hitWall, out FP hitNX, out FP hitNZ)
        {
            long rangeRaw = LockstepConfig.MAX_RANGE;
            var range = FP.FromRaw(rangeRaw);
            long radiusSqRaw = LockstepConfig.PLAYER_RADIUS * LockstepConfig.PLAYER_RADIUS; // 值×2^32 域

            // 墙先算：打不穿，墙后的玩家不吃伤害
            var wallT = LogicArena.RaycastWalls(shooter.X, shooter.Z, dir, range, out hitNX, out hitNZ);
            hitWall = wallT < range;

            // 默认终点：撞墙点（有墙）或射程尽头（没墙）
            hitX = shooter.X + dir.X * wallT;
            hitZ = shooter.Z + dir.Z * wallT;

            LogicPlayer best = null;
            long bestTRaw = hitWall ? wallT.Raw : rangeRaw;

            for (int i = 0; i < _players.Count; i++)
            {
                var t = _players[i];
                if (t == shooter || t.IsDead) continue; // 死者不挡弹（穿透）

                var proj = (t.X - shooter.X) * dir.X + (t.Z - shooter.Z) * dir.Z; // 沿射线距离
                if (proj.Raw < 0 || proj.Raw > bestTRaw) continue;                // 背后、超射程或被墙挡住

                var closestX = shooter.X + dir.X * proj;
                var closestZ = shooter.Z + dir.Z * proj;
                long distSq = FP.SqrDistance(closestX, closestZ, t.X, t.Z);
                if (distSq > radiusSqRaw) continue;

                if (proj.Raw < bestTRaw)
                {
                    bestTRaw = proj.Raw;
                    best = t;
                    hitX = closestX;
                    hitZ = closestZ;
                }
            }

            if (best != null) // 打中人：终点挪到人身上，墙的法线作废
            {
                hitWall = false;
                hitNX = new FP(0);
                hitNZ = new FP(0);
            }
            return best;
        }

        /// <summary>
        /// 死亡计时与复活：死亡只冻结该玩家的行为，计时照常推进。
        /// 归零后回到座位出生点（确定性），并立即做一次墙体推出——
        /// 座位 0 的出生点与地图斜墙相交，先推出再进入移动/战斗，避免复活瞬间卡在墙里。
        /// </summary>
        private void ProcessRespawns()
        {
            var radius = FP.FromRaw(LockstepConfig.PLAYER_RADIUS);
            int spawnStep = LockstepConfig.ANGLE_STEPS / 8;

            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (!p.IsDead) continue;

                if (p.RespawnTicksLeft > 0)
                {
                    p.RespawnTicksLeft--;
                    if (p.RespawnTicksLeft > 0) continue;
                }

                GetSpawnPoint(p.PlayerId, out var x, out var z);
                int yaw = (p.PlayerId * spawnStep + LockstepConfig.ANGLE_STEPS / 2) & FPAngle.MASK; // 面朝场地中心
                p.Respawn(x, z, yaw);

                for (int w = 0; w < LogicArena.Walls.Length; w++)
                    LogicArena.ResolveCircle(LogicArena.Walls[w], ref p.X, ref p.Z, radius);
            }
        }

        /// <summary>
        /// 静态墙体推出：放在互推之后，防止被同伴挤进墙里。
        /// 墙体是轴对齐盒子集合（LogicArena.Walls），全是定点数运算
        /// </summary>
        private void ResolveWalls()
        {
            var radius = FP.FromRaw(LockstepConfig.PLAYER_RADIUS);
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                for (int w = 0; w < LogicArena.Walls.Length; w++)
                    LogicArena.ResolveCircle(LogicArena.Walls[w], ref p.X, ref p.Z, radius);
            }
        }

        /// <summary>
        /// 圆形碰撞：等半径互推各让一半重叠量（纯位置修正，无速度冲量）
        /// </summary>
        private void ResolveCollisions()
        {
            // (2R)² 预先算好——与 SqrDistance 同在 值×2^32 域，比较免开方
            long minDistRaw = 2 * LockstepConfig.PLAYER_RADIUS;
            long minSqrRaw = minDistRaw * minDistRaw;

            for (int i = 0; i < _players.Count; i++)
            {
                for (int j = i + 1; j < _players.Count; j++)
                {
                    var a = _players[i];
                    var b = _players[j];

                    long sqrRaw = FP.SqrDistance(a.X, a.Z, b.X, b.Z);
                    if (sqrRaw >= minSqrRaw) continue;

                    var dist = FP.Sqrt(sqrRaw); // Q16.16
                    if (dist.IsZero)
                    {
                        // 完全重合的确定性兜底：固定沿 ±X 各推一个半径，恰好分开到 2R
                        var r = FP.FromRaw(LockstepConfig.PLAYER_RADIUS);
                        a.X += r;
                        b.X -= r;
                        continue;
                    }

                    var overlap = FP.FromRaw(minDistRaw) - dist;
                    var push = overlap / FP.FromInt(2);
                    var ux = (a.X - b.X) / dist; // 单位分离向量
                    var uz = (a.Z - b.Z) / dist;
                    a.X += ux * push;
                    a.Z += uz * push;
                    b.X -= ux * push;
                    b.Z -= uz * push;
                }
            }
        }

        /// <summary>出生点：8 个均匀分布在半径 8m 圆上，按加入序号取。
        /// 全端一致性依赖"加入顺序由服务端广播保证"（阶段6 落实）</summary>
        public static void GetSpawnPoint(int index, out FP x, out FP z)
        {
            int angle = (index % 8) * (LockstepConfig.ANGLE_STEPS / 8);
            var dir = FPTrig.Dir(angle);
            var radius = FP.FromInt(8);
            x = dir.X * radius;
            z = dir.Z * radius;
        }

        /// <summary>拍摄快照（当前帧结束态）。回滚重演（阶段3）/断线重连（阶段8）的基础</summary>
        public LogicWorldSnapshot CaptureSnapshot()
        {
            var players = new PlayerSnap[_players.Count];
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                players[i] = new PlayerSnap
                {
                    PlayerId = p.PlayerId,
                    X = p.X.Raw,
                    Z = p.Z.Raw,
                    Y = p.Y.Raw,
                    VerticalVelocity = p.VerticalVelocity.Raw,
                    Yaw = p.Yaw,
                    MaxHp = p.MaxHp,
                    Hp = p.Hp,
                    Ammo = p.Ammo,
                    FireCooldownTicks = p.FireCooldownTicks,
                    ReloadTicksLeft = p.ReloadTicksLeft,
                    IsDead = p.IsDead,
                    RespawnTicksLeft = p.RespawnTicksLeft,
                    Kills = p.Kills,
                    Deaths = p.Deaths,
                };
            }
            return new LogicWorldSnapshot { Frame = Frame, RngState = Rng.State, Players = players };
        }

        /// <summary>恢复快照。要求玩家集合与快照时一致——成员变更需重建世界（阶段8 重连流程）</summary>
        public void RestoreSnapshot(LogicWorldSnapshot snapshot)
        {
            if (snapshot.Players.Length != _players.Count)
                throw new System.InvalidOperationException(
                    $"快照玩家数({snapshot.Players.Length})与当前世界({_players.Count})不一致");
            for (int i = 0; i < snapshot.Players.Length; i++)
            {
                if (snapshot.Players[i].PlayerId != _players[i].PlayerId)
                    throw new System.InvalidOperationException("快照玩家集合与当前世界不一致");

                var p = _players[i];
                var s = snapshot.Players[i];
                p.X = FP.FromRaw(s.X);
                p.Z = FP.FromRaw(s.Z);
                p.Y = FP.FromRaw(s.Y);
                p.VerticalVelocity = FP.FromRaw(s.VerticalVelocity);
                p.Yaw = s.Yaw;
                p.MaxHp = s.MaxHp;
                p.Hp = s.Hp;
                p.Ammo = s.Ammo;
                p.FireCooldownTicks = s.FireCooldownTicks;
                p.ReloadTicksLeft = s.ReloadTicksLeft;
                p.IsDead = s.IsDead;
                p.RespawnTicksLeft = s.RespawnTicksLeft;
                p.Kills = s.Kills;
                p.Deaths = s.Deaths;
            }
            Frame = snapshot.Frame;
            Rng.Restore(snapshot.RngState);
        }

        /// <summary>状态哈希（阶段8 跨端漂移比对用）。纯整数混合，禁用运行时相关哈希（如字符串 GetHashCode）</summary>
        public long ComputeStateHash()
        {
            long h = unchecked((long)0x9E3779B97F4A7C15UL) ^ Frame;
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                h = Mix(h, p.PlayerId);
                h = Mix(h, p.X.Raw);
                h = Mix(h, p.Z.Raw);
                h = Mix(h, p.Y.Raw);
                h = Mix(h, p.Yaw);
                h = Mix(h, p.Hp);
                h = Mix(h, p.Ammo);
                h = Mix(h, p.IsDead ? 1 : 0);
                h = Mix(h, p.RespawnTicksLeft);
                h = Mix(h, p.Kills);
                h = Mix(h, p.Deaths);
            }
            return h;
        }

        private static long Mix(long h, long v)
        {
            h ^= v;
            h *= unchecked((long)0xFF51AFD7ED558CCDUL);
            h ^= h >> 33;
            return h;
        }
    }
}
