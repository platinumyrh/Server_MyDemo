using Server_MyDemo.Models;
using Server_MyDemo.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using GameNetwork;  // 添加 Protobuf 命名空间
using Google.Protobuf; // 添加 Google.Protobuf 命名空间

namespace Server_MyDemo.Server
{
    /// <summary>
    /// 游戏服务器主类
    /// 管理网络通信、房间系统、帧同步
    /// </summary>
    public class GameServer
    {
        #region 私有字段

        private NetworkServer _networkServer;      // 网络层
        private RoomManager _roomManager;           // 房间管理器
        private FrameSyncManager _frameSyncManager; // 帧同步管理器
        private bool _isRunning;                    // 服务器运行状态

        // 配置参数
        private const int SERVER_PORT = 7777;       // 服务器端口
        private const int MAX_PLAYERS = 20;         // 最大在线玩家数
        private const int FRAME_RATE = 30;          // 帧率（30fps）
        private const int FRAME_INTERVAL_MS = 1000 / FRAME_RATE; // 帧间隔（33ms）

        #endregion

        #region 构造函数

        public GameServer()
        {
            _roomManager = new RoomManager();
            _frameSyncManager = new FrameSyncManager(FRAME_RATE);
            _isRunning = false;

            Console.WriteLine($"[GameServer] 初始化完成");
            Console.WriteLine($"[GameServer] 端口: {SERVER_PORT}, 帧率: {FRAME_RATE}fps, 最大玩家数: {MAX_PLAYERS}");
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 启动服务器
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                Console.WriteLine("[GameServer] 服务器已在运行中");
                return;
            }

            try
            {
                // 初始化网络层
                _networkServer = new NetworkServer(SERVER_PORT);
                _networkServer.OnMessageReceived += HandleMessage;
                _networkServer.Start();

                // 启动帧同步循环
                _frameSyncManager.Start(_roomManager, _networkServer);

                _isRunning = true;
                Console.WriteLine("[GameServer] ✅ 服务器启动成功！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] ❌ 启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            try
            {
                _frameSyncManager?.Stop();
                _networkServer?.Stop();
                Console.WriteLine("[GameServer] 服务器已停止");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] 停止时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 打印服务器状态
        /// </summary>
        public void PrintStatus()
        {
            Console.WriteLine("\n========== 服务器状态 ==========");
            Console.WriteLine($"运行状态: {(_isRunning ? "运行中" : "已停止")}");
            Console.WriteLine($"在线玩家: {_networkServer?.OnlineCount ?? 0}");
            Console.WriteLine($"房间数量: {_roomManager.GetRoomCount()}");
            Console.WriteLine($"当前帧ID: {_frameSyncManager?.CurrentFrameId ?? 0}");
            Console.WriteLine("==================================\n");
        }

        #endregion

        #region 私有方法 - 消息处理

        /// <summary>
        /// 处理收到的消息（分发到不同的处理器）
        /// </summary>
        private void HandleMessage(IPEndPoint clientEndpoint, byte[] data, MsgID msgId)
        {
            try
            {
                switch (msgId)
                {
                    // === 心跳消息 ===
                    case MsgID.C2SHeartBeat:
                        HandleHeartBeat(clientEndpoint, data);
                        break;

                    // === 房间管理消息 ===
                    case MsgID.C2SCreateRoom:
                        HandleCreateRoom(clientEndpoint, data);
                        break;
                    case MsgID.C2SJoinRoom:
                        HandleJoinRoom(clientEndpoint, data);
                        break;
                    case MsgID.C2SLeaveRoom:
                        HandleLeaveRoom(clientEndpoint, data);
                        break;
                    case MsgID.C2SGetRoomList:
                        HandleGetRoomList(clientEndpoint);
                        break;
                    case MsgID.C2SStartGame:
                        HandleStartGame(clientEndpoint);
                        break;

                    // === 帧同步消息 ===
                    case MsgID.C2SPlayerInput:
                        HandlePlayerInput(clientEndpoint, data);
                        break;

                    default:
                        Console.WriteLine($"[GameServer] ⚠️ 未处理的消息类型: {msgId}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] ❌ 消息处理异常 [{msgId}]: {ex.Message}");
            }
        }

        #endregion

        #region 消息处理器 - 心跳

        private void HandleHeartBeat(IPEndPoint client, byte[] data)
        {
            var req = HeartBeatReq.Parser.ParseFrom(data);
            
            var resp = new HeartBeatResp
            {
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClientTime = req.Timestamp
            };

            _networkServer.Send(client, MsgID.S2CHeartBeat, resp);
        }

        #endregion

        #region 消息处理器 - 房间管理

        private void HandleCreateRoom(IPEndPoint client, byte[] data)
        {
            var req = CreateRoomReq.Parser.ParseFrom(data);

            // 获取或创建玩家
            string playerId = GetOrCreatePlayer(client);
            
            var result = _roomManager.CreateRoom(playerId, req.RoomName, req.Password, req.RoomId);

            var resp = new CreateRoomResp
            {
                Success = result.Success,
                RoomId = result.RoomId ?? "",
                Message = result.Message ?? "",
                PlayerId = playerId
            };

            _networkServer.Send(client, MsgID.S2CCreateRoom, resp);

            if (result.Success)
            {
                Console.WriteLine($"[GameServer] 玩家 [{playerId}] 创建房间 [{result.RoomId}]");
            }
        }

        private void HandleJoinRoom(IPEndPoint client, byte[] data)
        {
            var req = JoinRoomReq.Parser.ParseFrom(data);
            string playerId = GetOrCreatePlayer(client);

            var result = _roomManager.JoinRoom(playerId, req.RoomId, req.Password);

            var resp = new JoinRoomResp
            {
                Success = result.Success,
                RoomId = req.RoomId,
                Message = result.Message ?? "",
                PlayerId = playerId
            };

            if (result.Success && result.RoomInfo != null)
            {
                resp.RoomInfo = result.RoomInfo;
            }

            _networkServer.Send(client, MsgID.S2CJoinRoom, resp);

            if (result.Success)
            {
                // 广播给房间内其他玩家
                BroadcastPlayerJoin(req.RoomId, playerId);
                Console.WriteLine($"[GameServer] 玩家 [{playerId}] 加入房间 [{req.RoomId}]");
            }
        }

        private void HandleLeaveRoom(IPEndPoint client, byte[] data)
        {
            var req = LeaveRoomReq.Parser.ParseFrom(data);
            string playerId = GetOrCreatePlayer(client);

            var result = _roomManager.LeaveRoom(playerId, req.RoomId);

            var resp = new LeaveRoomResp
            {
                Success = result.Success,
                Message = result.Message ?? ""
            };

            _networkServer.Send(client, MsgID.S2CLeaveRoom, resp);

            if (result.Success)
            {
                // 广播给房间内其他玩家
                BroadcastPlayerLeave(req.RoomId, playerId);
                Console.WriteLine($"[GameServer] 玩家 [{playerId}] 离开房间 [{req.RoomId}]");
            }
        }

        private void HandleGetRoomList(IPEndPoint client)
        {
            var rooms = _roomManager.GetAllRooms();

            var resp = new RoomListResp();
            resp.Rooms.AddRange(rooms);

            _networkServer.Send(client, MsgID.S2CRoomList, resp);
        }

        #endregion

        #region 消息处理器 - 帧同步

        private void HandlePlayerInput(IPEndPoint client, byte[] data)
        {
            var req = PlayerInputReq.Parser.ParseFrom(data);
            string playerId = GetOrCreatePlayer(client);

            // 将输入提交给帧同步管理器
            _frameSyncManager.AddPlayerInput(playerId, req);
        }

        private void HandleStartGame(IPEndPoint client)
        {
            string playerId = GetOrCreatePlayer(client);
            string? roomId = _roomManager.GetPlayerRoomId(playerId);

            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine($"[GameServer] ⚠️ 玩家 [{playerId}] 不在任何房间中，无法开始游戏");
                return;
            }

            var result = _roomManager.StartGame(roomId!, playerId);
            if (!result.Success)
            {
                Console.WriteLine($"[GameServer] ⚠️ 开始游戏失败: {result.Message}");
                return;
            }

            var room = _roomManager.GetRoom(roomId!);
            if (room == null) return;

            var notify = new GameStartNotify
            {
                RoomId = roomId!,
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            foreach (var player in room.Players)
            {
                var endpoint = _networkServer.GetPlayerEndpoint(player.PlayerId);
                if (endpoint != null)
                {
                    _networkServer.Send(endpoint, MsgID.S2CGameStart, notify);
                }
            }

            Console.WriteLine($"[GameServer] 玩家 [{playerId}] 开始游戏，房间 [{roomId}] 共 {room.Players.Count} 人");
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取或创建玩家（根据IP和端口识别）
        /// </summary>
        private string GetOrCreatePlayer(IPEndPoint client)
        {
            return $"{client.Address}:{client.Port}";
        }

        /// <summary>
        /// 广播玩家加入通知
        /// </summary>
        private void BroadcastPlayerJoin(string roomId, string playerId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var notify = new PlayerJoinNotify
            {
                Player = new PlayerInfo
                {
                    PlayerId = playerId,
                    PlayerName = $"Player_{playerId.GetHashCode() % 10000}",
                    IsReady = false
                }
            };

            foreach (var player in room.Players)
            {
                if (player.PlayerId != playerId)
                {
                    var endpoint = _networkServer.GetPlayerEndpoint(player.PlayerId);
                    if (endpoint != null)
                    {
                        _networkServer.Send(endpoint, MsgID.S2CPlayerJoin, notify);
                    }
                }
            }
        }

        /// <summary>
        /// 广播玩家离开通知
        /// </summary>
        private void BroadcastPlayerLeave(string roomId, string playerId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var notify = new PlayerLeaveNotify
            {
                PlayerId = playerId
            };

            foreach (var player in room.Players)
            {
                if (player.PlayerId != playerId)
                {
                    var endpoint = _networkServer.GetPlayerEndpoint(player.PlayerId);
                    if (endpoint != null)
                    {
                        _networkServer.Send(endpoint, MsgID.S2CPlayerLeave, notify);
                    }
                }
            }
        }

        #endregion
    }
}
