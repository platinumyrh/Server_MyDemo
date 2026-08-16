using Server_MyDemo.Models;
using Server_MyDemo.Server;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        
        // 引用（不拥有）
        private RoomManager? _roomManager;
        private NetworkServer? _networkServer;

        // 定时器
        private System.Threading.Timer? _frameTimer;
        private Stopwatch _stopwatch;

        #endregion

        #region 构造函数

        public FrameSyncManager(int frameRate = 30)
        {
            _frameRate = frameRate;
            _frameIntervalMs = 1000 / frameRate;
            _currentFrameId = 0;
            _isRunning = false;
            _inputBuffer = new Dictionary<string, PlayerInputReq>();
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
        /// 添加玩家输入
        /// </summary>
        public void AddPlayerInput(string playerId, PlayerInputReq input)
        {
            lock (_inputBuffer)
            {
                _inputBuffer[playerId] = input;
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

                // 构建帧状态消息
                var frameState = new FrameStateResp
                {
                    FrameId = _currentFrameId,
                    ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                // 添加所有玩家输入
                foreach (var input in allInputs)
                {
                    frameState.PlayerInputs[input.Key] = input.Value;
                }

                // 广播给所有在房间中的玩家
                BroadcastToAllRooms(frameState);

                // 调试输出（每100帧输出一次）
                if (_currentFrameId % 100 == 0)
                {
                    Console.WriteLine($"[FrameSync] 帧 {_currentFrameId}, 在线输入: {allInputs.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FrameSync] ❌ 帧更新异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 广播帧状态给所有房间的所有玩家
        /// </summary>
        private void BroadcastToAllRooms(FrameStateResp frameState)
        {
            if (_roomManager == null || _networkServer == null) return;

            var rooms = _roomManager.GetAllRooms();
            
            foreach (var roomInfo in rooms)
            {
                if (!roomInfo.IsPlaying) continue;

                var room = _roomManager.GetRoom(roomInfo.RoomId);
                if (room == null) continue;

                foreach (var player in room.Players)
                {
                    var endpoint = _networkServer.GetPlayerEndpoint(player.PlayerId);
                    if (endpoint != null)
                    {
                        _networkServer.Send(endpoint, MsgID.S2CFrameState, frameState);
                    }
                }
            }
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
