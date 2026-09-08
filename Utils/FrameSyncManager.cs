using Server_MyDemo.Models;
using Server_MyDemo.Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameNetwork;  // 添加 GameNetwork 命名空间
using Google.Protobuf; // 添加 Google.Protobuf 命名空间

namespace Server_MyDemo.Utils
{
    /// <summary>
    /// 帧同步管理器
    /// 负责维护帧率、收集输入、广播状态
    /// </summary>
    public class FrameSyncManager
    {
        #region 私有字段

        private int _frameRate;                  // 帧率（fps）
        private int _frameIntervalMs;            // 帧间隔（毫秒）
        private uint _currentFrameId;            // 当前帧ID
        private bool _isRunning;                 // 是否运行中

        // 输入缓冲区（playerId -> 本帧输入）
        private Dictionary<string, PlayerInputReq> _inputBuffer;

        // 玩家位置状态（playerId -> 最近一次上报的位置）
        // 位置由各客户端自己物理模拟（含 CharacterController 碰撞结果）后随输入上报，
        // 服务端只持有和广播：hold-last 语义，未收到新状态时沿用旧值，
        // 保证帧广播里始终有房间内的最新位置。
        // 注意：这是"客户端上报 + 服务端中继"，并非服务端权威物理；
        // 服务端只做速度上限校验（见 ValidatePosition），能挡住瞬移但不做完整反作弊
        private Dictionary<string, PlayerState> _playerStates;

        // 位置校验用：记录每个玩家上一次被接受的位置与时间
        private Dictionary<string, (float X, float Y, float Z, DateTime Time)> _lastAcceptedPos;

        // 引用（不拥有）
        private RoomManager? _roomManager;
        private NetworkServer? _networkServer;

        // 定时器
        private System.Threading.Timer? _frameTimer;
        private Stopwatch _stopwatch;

        // 位置校验配置
        private const float MAX_MOVE_SPEED = 30f;      // 合理移动速度上限（单位/秒），正常跑动远低于此
        private const float TELEPORT_THRESHOLD = 50f; // 超过此距离判定为重生/传送，直接接受而不钳制

        private int _clampCount;                       // 钳制次数（用于节流日志）

        #endregion

        #region 构造函数

        public FrameSyncManager(int frameRate = 30)
        {
            _frameRate = frameRate;
            _frameIntervalMs = 1000 / frameRate;
            _currentFrameId = 0;
            _isRunning = false;
            _inputBuffer = new Dictionary<string, PlayerInputReq>();
            _playerStates = new Dictionary<string, PlayerState>();
            _lastAcceptedPos = new Dictionary<string, (float, float, float, DateTime)>();
            _stopwatch = new Stopwatch();

            Console.WriteLine($"[FrameSync] 初始化完成，帧率: {_frameRate}fps，间隔: {_frameIntervalMs}ms");
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 启动帧同步循环
        /// </summary>
        public void Start(RoomManager roomManager, NetworkServer networkServer)
        {
            if (_isRunning) return;

            _roomManager = roomManager;
            _networkServer = networkServer;
            _isRunning = true;

            _stopwatch.Restart();

            // 启动定时器
            _frameTimer = new System.Threading.Timer(FrameUpdate, null,
                _frameIntervalMs, _frameIntervalMs);

            Console.WriteLine($"[FrameSync] ✅ 帧同步已启动");
        }

        /// <summary>
        /// 停止帧同步
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _frameTimer?.Dispose();
            _stopwatch.Stop();

            Console.WriteLine("[FrameSync] 帧同步已停止");
        }

        /// <summary>
        /// 添加玩家输入（输入和该玩家的位置状态一起上报）
        /// </summary>
        public void AddPlayerInput(string playerId, PlayerInputReq input)
        {
            lock (_inputBuffer)
            {
                _inputBuffer[playerId] = input;
            }

            // 位置先过校验（防瞬移），再写入 hold-last 状态
            var validated = ValidatePosition(playerId, input.PosX, input.PosY, input.PosZ);

            lock (_playerStates)
            {
                _playerStates[playerId] = new PlayerState
                {
                    PosX = validated.X,
                    PosY = validated.Y,
                    PosZ = validated.Z,
                    RotY = input.RotY,
                    // 持续状态随快照走：远端玩家据此播放开火/换弹表现
                    IsFiring = input.Fire,
                    IsReloading = input.Reload
                };
            }
        }

        /// <summary>
        /// 清除某个玩家的全部帧同步状态（输入缓冲、位置状态、校验记录）
        /// 玩家离线、离开房间时调用，避免状态残留被持续广播
        /// </summary>
        public void RemovePlayerState(string playerId)
        {
            lock (_inputBuffer)
            {
                _inputBuffer.Remove(playerId);
            }

            lock (_playerStates)
            {
                _playerStates.Remove(playerId);
            }

            lock (_lastAcceptedPos)
            {
                _lastAcceptedPos.Remove(playerId);
            }
        }

        /// <summary>
        /// 获取当前帧ID
        /// </summary>
        public uint CurrentFrameId => _currentFrameId;

        #endregion

        #region 私有方法 - 帧更新

        /// <summary>
        /// 帧更新回调（每帧调用一次）
        /// </summary>
        private void FrameUpdate(object? state)
        {
            if (!_isRunning || _roomManager == null || _networkServer == null) return;

            try
            {
                // 递增帧ID
                _currentFrameId++;

                // 收集本帧所有输入
                Dictionary<string, PlayerInput> allInputs;
                lock (_inputBuffer)
                {
                    allInputs = new Dictionary<string, PlayerInput>();
                    foreach (var kvp in _inputBuffer)
                    {
                        allInputs[kvp.Key] = ConvertToPlayerInput(kvp.Key, kvp.Value);
                    }
                    _inputBuffer.Clear();
                }

                // 拷贝位置快照，避免广播期间长时间持锁
                Dictionary<string, PlayerState> statesSnapshot;
                lock (_playerStates)
                {
                    statesSnapshot = new Dictionary<string, PlayerState>(_playerStates);
                }

                // 广播（每个房间只带本房间成员的数据）
                BroadcastToAllRooms(allInputs, statesSnapshot);

                // 调试输出（每100帧输出一次）
                if (_currentFrameId % 100 == 0)
                {
                    Console.WriteLine($"[FrameSync] 帧 {_currentFrameId}, 在线输入: {allInputs.Count}, 位置状态: {statesSnapshot.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FrameSync] ❌ 帧更新异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 广播帧状态：为每个进行中的房间单独构建一个帧包，
        /// 只装入该房间成员的输入与位置，避免把 A 房间的坐标发给 B 房间
        /// </summary>
        private void BroadcastToAllRooms(
            Dictionary<string, PlayerInput> allInputs,
            Dictionary<string, PlayerState> allStates)
        {
            if (_roomManager == null || _networkServer == null) return;

            long serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rooms = _roomManager.GetAllRooms();

            foreach (var roomInfo in rooms)
            {
                if (!roomInfo.IsPlaying) continue;

                var room = _roomManager.GetRoom(roomInfo.RoomId);
                if (room == null) continue;

                // 按房间裁剪：只放本房间成员的数据
                var frameState = new FrameStateResp
                {
                    FrameId = _currentFrameId,
                    ServerTime = serverTime
                };

                foreach (var member in room.Players)
                {
                    if (allInputs.TryGetValue(member.PlayerId, out var input))
                    {
                        frameState.PlayerInputs[member.PlayerId] = input;
                    }

                    if (allStates.TryGetValue(member.PlayerId, out var playerState))
                    {
                        frameState.PlayerStates[member.PlayerId] = playerState;
                    }
                }

                // 发给房间内每个成员
                foreach (var member in room.Players)
                {
                    var endpoint = _networkServer.GetPlayerEndpoint(member.PlayerId);
                    if (endpoint != null)
                    {
                        _networkServer.Send(endpoint, MsgID.S2CFrameState, frameState);
                    }
                }
            }
        }

        /// <summary>
        /// 位置校验：按时间差算出合理位移上限，超出则钳制；
        /// 位移大到不可能（重生、传送）时直接接受，避免误伤正常传送逻辑
        /// </summary>
        private (float X, float Y, float Z) ValidatePosition(string playerId, float x, float y, float z)
        {
            var now = DateTime.Now;

            lock (_lastAcceptedPos)
            {
                if (_lastAcceptedPos.TryGetValue(playerId, out var last))
                {
                    float dt = (float)(now - last.Time).TotalSeconds;
                    if (dt > 0.001f)
                    {
                        float dx = x - last.X;
                        float dy = y - last.Y;
                        float dz = z - last.Z;
                        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        float maxDist = MAX_MOVE_SPEED * dt;

                        if (dist > TELEPORT_THRESHOLD)
                        {
                            // 远超合理范围：按重生/传送处理，接受新位置
                            Console.WriteLine($"[FrameSync] 🌀 [{playerId}] 位移 {dist:F1} 超过传送阈值，按重生处理");
                        }
                        else if (dist > maxDist && dist > 0.001f)
                        {
                            // 超出合理速度：钳制到边界（挡瞬移，同时保留正常移动）
                            float scale = maxDist / dist;
                            x = last.X + dx * scale;
                            y = last.Y + dy * scale;
                            z = last.Z + dz * scale;

                            int count = Interlocked.Increment(ref _clampCount);
                            if (count % 100 == 1)
                            {
                                Console.WriteLine($"[FrameSync] ⚠️ 位置超速已钳制 {count} 次，最近玩家 [{playerId}]，位移 {dist:F1} > 上限 {maxDist:F1}");
                            }
                        }
                    }
                }

                _lastAcceptedPos[playerId] = (x, y, z, now);
            }

            return (x, y, z);
        }

        /// <summary>
        /// 转换为PlayerInput格式
        /// </summary>
        private PlayerInput ConvertToPlayerInput(string playerId, PlayerInputReq req)
        {
            return new PlayerInput
            {
                PlayerId = playerId,
                MoveX = req.MoveX,
                MoveY = req.MoveY,
                IsSprinting = req.IsSprinting,
                IsAiming = req.IsAiming,
                Fire = req.Fire,
                Reload = req.Reload,
                Jump = req.Jump,
                Interact = req.Interact,
                Rotation = req.Rotation
            };
        }

        #endregion
    }
}
