using GameLockstep;
using GameNetwork;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Server_MyDemo.Services
{
    /// <summary>
    /// 单个房间的 lockstep 中继状态。
    /// 服务器只做"封帧 + 广播"，不解读帧内容——位置/血量/伤害全部由各端确定性模拟，
    /// 服务器不持有任何游戏状态（阶段8 接入哈希校验后才会运行自己的影子世界）
    /// </summary>
    public sealed class LockstepRoomRelay
    {
        public readonly string RoomId;
        public readonly ulong Seed;
        public readonly long StartMs;

        /// playerId → 座位号（GameStart 时按加入顺序分配 0..N-1，全端一致）</summary>
        public readonly Dictionary<string, int> PlayerSeats = new();

        /// playerId → 昵称（重连端要靠它把名牌补回来）</summary>
        public readonly Dictionary<string, string> PlayerNames = new();

        /// 掉线/离开玩家的座位：封帧时对这些座位补空指令，不卡全场</summary>
        public readonly HashSet<int> DroppedSeats = new();

        /// 正在重连的座位：加载 Main / 灌历史期间保留中继，历史发完才恢复收指令
        public readonly HashSet<int> ResyncingSeats = new();
        public readonly Dictionary<int, long> ResyncSinceMs = new();

        /// 全员掉线的起始时刻（0 = 有人在场）；宽限期后才移除中继，留给重连
        public long AllDroppedAtMs;

        /// 下一个待封帧号</summary>
        public long NextSealFrame = 1;

        /// 未封帧的指令缓冲：frame → (seat → cmd)，封完即删</summary>
        public readonly Dictionary<long, Dictionary<int, PlayerCommand>> Pending = new();

        /// <summary>整局权威帧日志：重连端从头快进用（封帧即追加，封帧结果对象不再修改）</summary>
        public readonly List<LockstepFrameInputs> History = new();

        /// <summary>权威帧录像（写盘失败时为 null，不影响对局）</summary>
        public ReplayRecorder? Recorder;

        public LockstepRoomRelay(string roomId, ulong seed, long startMs, IReadOnlyList<(string Id, string Name)> players)
        {
            RoomId = roomId;
            Seed = seed;
            StartMs = startMs;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerSeats[players[i].Id] = i;
                PlayerNames[players[i].Id] = players[i].Name;
            }
        }

        /// <summary>帧到期判定：墙钟超过该帧的封帧期限（帧时隙 + 超时宽限）。
        /// 输入延迟 2 帧的设计下，正常指令在 帧-2 的墙钟就已到齐，
        /// 到期仍缺说明丢包/卡顿——补空封帧推进，绝不卡全场</summary>
        public bool IsDue(long frame, long nowMs)
        {
            return nowMs >= StartMs + (frame - 1) * LockstepConfig.TICK_MS + LockstepConfig.FRAME_TIMEOUT_MS;
        }

        /// <summary>是否所有在场座位都交了该帧指令（掉线座位视为已交空指令）</summary>
        public bool AllPresent(long frame)
        {
            if (!Pending.TryGetValue(frame, out var cmds)) return false;
            foreach (var kv in PlayerSeats)
            {
                if (DroppedSeats.Contains(kv.Value)) continue;
                if (!cmds.ContainsKey(kv.Value)) return false;
            }
            return true;
        }

        /// <summary>封走一帧：全员完整集合——齐了用真值，缺席座位显式补空条目。
        /// （显式补空而非省略条目：客户端 SameInputs 把"缺失"与"空指令"视为分歧，
        /// 省略会造成每帧一次无意义回滚）</summary>
        public LockstepFrameInputs Seal(long frame)
        {
            var fi = new LockstepFrameInputs { Frame = frame };
            var cmds = Pending.TryGetValue(frame, out var c) ? c : null;

            foreach (var kv in PlayerSeats)
            {
                var seat = kv.Value;
                var cmd = cmds != null && cmds.TryGetValue(seat, out var pc) ? pc : PlayerCommand.Empty;
                fi.Commands.Add(new LockstepPlayerCommand
                {
                    Seat = seat,
                    MoveX = cmd.MoveX,
                    MoveZ = cmd.MoveZ,
                    AimAngle = cmd.AimAngle,
                    Buttons = cmd.Buttons,
                });
            }

            if (cmds != null) Pending.Remove(frame);
            NextSealFrame = frame + 1;
            return fi;
        }
    }

    /// <summary>重连所需的开局信息快照（座位表含昵称）</summary>
    public sealed class ReconnectSnapshot
    {
        public string RoomId = "";
        public ulong Seed;
        public long StartMs;
        public long LatestFrame;
        public readonly List<(string Id, string Name, int Seat)> Seats = new();
    }

    /// <summary>
    /// lockstep 中继管理器：房间中继表 + 15Hz 封帧泵。
    /// 封帧规则（与客户端协议自测沙盒同一条规则）：
    ///   顺序封帧——某帧全员指令齐了立即封；帧到期（超时宽限后）仍缺则补空封。
    /// 线程模型：UDP 接收线程调 OnPlayerCommand，定时器线程调 Pump——内部统一加锁，
    /// 封帧结果通过 FramesSealed 事件抛出（锁外触发，广播由 GameServer 完成）
    /// </summary>
    public sealed class LockstepRelayManager : IDisposable
    {
        /// 允许客户端提前提交的最大帧窗口（防错位/刷包；2 帧输入延迟下正常余量）</summary>
        private const long MAX_FUTURE_WINDOW = 64;

        /// 重连端加载 Main / 建视图的等待上限；超时按掉线处理，避免中继永远留着
        private const int RECONNECT_READY_TIMEOUT_MS = 15000;

        /// 全员掉线后中继的保留时长：留出重连窗口，超时才回收
        private const int RECONNECT_GRACE_MS = 30000;

        private readonly Dictionary<string, LockstepRoomRelay> _relays = new();
        private readonly object _gate = new();
        private Timer? _sealTimer;
        private readonly Func<long> _clock;

        /// 新帧封好待广播（roomId, 合并了本批多帧的 notify）。可能在网络线程或定时器线程触发</summary>
        public event Action<string, LockstepInputsNotify>? FramesSealed;

        /// 时钟注入：默认取服务器墙钟，测试时给假时钟</summary>
        public LockstepRelayManager(Func<long>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// 开局：分配座位（按加入顺序）与种子。重复开始（重开局）会重置中继。
        /// startMs = 约定的开赛时刻（0 = 立即），封帧时钟从这一刻起算
        /// 同时按种子/座位建一份录像，边封帧边落盘
        /// </summary>
        public (ulong Seed, List<LockstepSeat> Seats) StartGame(string roomId, IReadOnlyList<(string Id, string Name)> players, long startMs = 0)
        {
            ulong seed = unchecked((ulong)(Environment.TickCount64 * 2654435761L) | 1UL);
            long startAt = startMs > 0 ? startMs : _clock();

            var relay = new LockstepRoomRelay(roomId, seed, startAt, players);
            try
            {
                relay.Recorder = new ReplayRecorder(roomId, seed, LockstepConfig.TICK_RATE, players);
                Console.WriteLine($"[Replay] 💾 开始录制: {relay.Recorder.FilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Replay] ⚠️ 录像创建失败（对局继续）: {ex.Message}");
            }

            var seats = new List<LockstepSeat>(players.Count);
            for (int i = 0; i < players.Count; i++)
                seats.Add(new LockstepSeat { PlayerId = players[i].Id, Seat = i });

            lock (_gate)
            {
                if (_relays.TryGetValue(roomId, out var old))
                {
                    old.Recorder?.Dispose();
                    old.Recorder = null;
                }
                _relays[roomId] = relay;
            }
            return (seed, seats);
        }

        /// <summary>当前正在进行 lockstep 中继的房间数（状态展示用）</summary>
        public int ActiveRoomCount
        {
            get
            {
                lock (_gate) return _relays.Count;
            }
        }

        /// 玩家离开/掉线：其座位改为补空（不卡帧，也不再有该座位的真值）</summary>
        public void RemovePlayer(string roomId, string playerId)
        {
            lock (_gate)
            {
                if (!_relays.TryGetValue(roomId, out var relay)) return;
                if (relay.PlayerSeats.TryGetValue(playerId, out var seat))
                    relay.DroppedSeats.Add(seat);
            }
        }

        /// <summary>
        /// 客户端登录后询问：我在哪个未结束的对局里？
        /// 找到就标记“重连中”——全员掉线时中继先别回收，等客户端回来。
        /// </summary>
        public bool TryBuildReconnectInfo(string playerId, out ReconnectSnapshot? snapshot)
        {
            snapshot = null;
            lock (_gate)
            {
                foreach (var relay in _relays.Values)
                {
                    if (!relay.PlayerSeats.TryGetValue(playerId, out var seat)) continue;

                    relay.ResyncingSeats.Add(seat);
                    relay.ResyncSinceMs[seat] = _clock();

                    var snap = new ReconnectSnapshot
                    {
                        RoomId = relay.RoomId,
                        Seed = relay.Seed,
                        StartMs = relay.StartMs,
                        LatestFrame = relay.NextSealFrame - 1,
                    };
                    foreach (var kv in relay.PlayerSeats)
                    {
                        string name = relay.PlayerNames.TryGetValue(kv.Key, out var n) ? n : kv.Key;
                        snap.Seats.Add((kv.Key, name, kv.Value));
                    }
                    snap.Seats.Sort((a, b) => a.Seat.CompareTo(b.Seat));

                    snapshot = snap;
                    return true;
                }
                return false;
            }
        }

        /// <summary>重连端 Main 就绪：持锁把整局历史交给调用方发出去，发完恢复座位收指令。
        /// 持锁是为了保证“历史快照之后不会再有帧被封进来”——之后封的帧会正常广播。</summary>
        public bool SendReconnectHistory(string roomId, string playerId,
                                         Action<IReadOnlyList<LockstepFrameInputs>> sendHistory)
        {
            lock (_gate)
            {
                if (!_relays.TryGetValue(roomId, out var relay)) return false;
                if (!relay.PlayerSeats.TryGetValue(playerId, out var seat)) return false;

                sendHistory(relay.History);

                relay.DroppedSeats.Remove(seat);
                relay.ResyncingSeats.Remove(seat);
                relay.ResyncSinceMs.Remove(seat);
                relay.AllDroppedAtMs = 0;
                return true;
            }
        }

        /// 启动封帧泵（15Hz）</summary>
        public void Start()
        {
            _sealTimer?.Dispose();
            _sealTimer = new Timer(_ => PumpAndBroadcast(), null,
                LockstepConfig.TICK_MS, LockstepConfig.TICK_MS);
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            _sealTimer?.Dispose();
            _sealTimer = null;
            lock (_gate)
            {
                foreach (var relay in _relays.Values)
                {
                    relay.Recorder?.Dispose();
                    relay.Recorder = null;
                }
                _relays.Clear();
            }
        }

        /// 收到某玩家的指令批量：登记后立即尝试封帧（齐了就封，不等定时器——降低封帧延迟）</summary>
        public void OnPlayerCommand(string roomId, string playerId, LockstepCommandReq req)
        {
            List<(string, LockstepInputsNotify)>? sealedBatches = null;
            lock (_gate)
            {
                if (!_relays.TryGetValue(roomId, out var relay)) return;
                if (!relay.PlayerSeats.TryGetValue(playerId, out var seat)) return;

                foreach (var d in req.Commands)
                {
                    if (d.Frame < relay.NextSealFrame)
                        continue; // 已封帧，迟到指令丢弃——客户端回滚机制会消化
                    if (d.Frame >= relay.NextSealFrame + MAX_FUTURE_WINDOW)
                        continue; // 过于超前的指令（异常/恶意），拒收

                    if (!relay.Pending.TryGetValue(d.Frame, out var cmds))
                    {
                        cmds = new Dictionary<int, PlayerCommand>();
                        relay.Pending[d.Frame] = cmds;
                    }
                    cmds[seat] = LockstepProtoConvert.ToCommand(d);
                }

                sealedBatches = PumpLocked(_clock());
            }
            Raise(sealedBatches);
        }

        /// <summary>手动触发一次封帧泵（定时器自动调；测试/工具可显式调用）</summary>
        public void PumpAndBroadcast()
        {
            List<(string, LockstepInputsNotify)>? sealedBatches;
            lock (_gate)
            {
                sealedBatches = PumpLocked(_clock());
            }
            Raise(sealedBatches);
        }

        /// <summary>封帧核心（须持锁调用）：顺序封帧——齐了就封 / 到期补空。
        /// 全员掉线的房间封完手头帧后自动移除中继（防空转刷空帧）。返回本批各房间的新封帧</summary>
        private List<(string, LockstepInputsNotify)>? PumpLocked(long nowMs)
        {
            List<(string, LockstepInputsNotify)>? result = null;
            List<string>? finished = null;

            foreach (var kv in _relays)
            {
                var relay = kv.Value;

                // 重连端迟迟不就绪（关客户端/卡加载）：放弃重连，按掉线处理
                if (relay.ResyncSinceMs.Count > 0)
                {
                    List<int>? expired = null;
                    foreach (var r in relay.ResyncSinceMs)
                        if (nowMs - r.Value > RECONNECT_READY_TIMEOUT_MS) (expired ??= new List<int>()).Add(r.Key);
                    if (expired != null)
                        foreach (int seat in expired)
                        {
                            relay.ResyncSinceMs.Remove(seat);
                            relay.ResyncingSeats.Remove(seat);
                            relay.DroppedSeats.Add(seat);
                        }
                }

                var notify = new LockstepInputsNotify();
                while (true)
                {
                    long f = relay.NextSealFrame;
                    if (relay.AllPresent(f) || relay.IsDue(f, nowMs))
                    {
                        var frame = relay.Seal(f);
                        relay.History.Add(frame);
                        notify.Frames.Add(frame);
                        RecordFrame(relay, frame);
                    }
                    else
                        break;
                }
                if (notify.Frames.Count > 0)
                    (result ??= new List<(string, LockstepInputsNotify)>()).Add((kv.Key, notify));

                // 全员掉线：宽限期后才移除中继（留出重连窗口）。重连中的座位不算“走光”
                bool allDropped = relay.PlayerSeats.Count > 0
                                  && relay.PlayerSeats.Count == relay.DroppedSeats.Count
                                  && relay.ResyncingSeats.Count == 0;
                if (allDropped)
                {
                    if (relay.AllDroppedAtMs == 0) relay.AllDroppedAtMs = nowMs;
                    else if (nowMs - relay.AllDroppedAtMs >= RECONNECT_GRACE_MS)
                        (finished ??= new List<string>()).Add(kv.Key);
                }
                else
                {
                    relay.AllDroppedAtMs = 0;
                }
            }
            if (finished != null)
                foreach (var roomId in finished)
                {
                    if (_relays.TryGetValue(roomId, out var relay))
                    {
                        relay.Recorder?.Dispose();
                        relay.Recorder = null;
                    }
                    _relays.Remove(roomId);
                }
            return result;
        }

        /// <summary>封帧即落盘；IO 出错就关掉该房间的录像，不拖累封帧</summary>
        private static void RecordFrame(LockstepRoomRelay relay, LockstepFrameInputs frame)
        {
            if (relay.Recorder == null) return;
            try
            {
                relay.Recorder.Append(frame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Replay] ⚠️ 写录像失败，停止录制: {ex.Message}");
                relay.Recorder.Dispose();
                relay.Recorder = null;
            }
        }

        private void Raise(List<(string, LockstepInputsNotify)>? batches)
        {
            if (batches == null) return;
            foreach (var (roomId, notify) in batches)
                FramesSealed?.Invoke(roomId, notify);
        }
    }
}
