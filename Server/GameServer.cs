using Server_MyDemo.Models;
using Server_MyDemo.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;  // RandomNumberGenerator 所在命名空间
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

        private NetworkServer _networkServer;       // 网络层（构造函数中创建，Start() 中开始监听）
        private RoomManager _roomManager;           // 房间管理器
        private FrameSyncManager _frameSyncManager; // 帧同步管理器
        private AuthService _authService;           // 认证服务（用户登录验证）
        private bool _isRunning;                    // 服务器运行状态

        // 心跳超时检测定时器（UDP 无连接，客户端强退时只能靠超时发现）
        private System.Threading.Timer? _timeoutTimer;

        // 配置参数
        private const int SERVER_PORT = 7777;       // 服务器端口
        private const int MAX_PLAYERS = 20;         // 最大在线玩家数
        private const int FRAME_RATE = 60;          // 帧率（30fps）
        private const int FRAME_INTERVAL_MS = 1000 / FRAME_RATE; // 帧间隔（33ms）

        private const int HEARTBEAT_TIMEOUT_SEC = 15;      // 超过该时长无任何消息则判定掉线
        private const int TIMEOUT_CHECK_INTERVAL_MS = 5000; // 超时扫描间隔

        #endregion

        #region 构造函数

        public GameServer()
        {
            _roomManager = new RoomManager();
            _frameSyncManager = new FrameSyncManager(FRAME_RATE);

            // 🔧 修复：使用项目根目录（.csproj 所在目录）作为数据目录
            // 避免 bin/Debug/net8.0/ 和项目根目录之间的路径混乱
            string projectDirectory = FindProjectDirectory();
            string dataPath = Path.Combine(projectDirectory, "Data", "users.json");

            Console.WriteLine($"[GameServer] 📂 项目根目录: {projectDirectory}");
            Console.WriteLine($"[GameServer] 📂 用户数据文件路径: {dataPath}");
            Console.WriteLine($"[GameServer] 📂 文件是否存在: {File.Exists(dataPath)}");

            _authService = new AuthService(dataPath);   // 使用项目根目录的绝对路径

            _networkServer = new NetworkServer(SERVER_PORT);
            _isRunning = false;

            Console.WriteLine($"[GameServer] 初始化完成");
            Console.WriteLine($"[GameServer] 端口: {SERVER_PORT}, 帧率: {FRAME_RATE}fps, 最大玩家数: {MAX_PLAYERS}");
            Console.WriteLine($"[GameServer] 心跳超时: {HEARTBEAT_TIMEOUT_SEC}s, 扫描间隔: {TIMEOUT_CHECK_INTERVAL_MS}ms");
            Console.WriteLine($"[GameServer] 已加载 {_authService.GetUserCount()} 个用户账号");
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
                // 启动网络层
                _networkServer.OnMessageReceived += HandleMessage;
                _networkServer.Start();

                // 启动帧同步循环
                _frameSyncManager.Start(_roomManager, _networkServer);

                // 启动心跳超时扫描
                _timeoutTimer = new System.Threading.Timer(
                    _ => CheckTimeouts(),
                    null,
                    TIMEOUT_CHECK_INTERVAL_MS,
                    TIMEOUT_CHECK_INTERVAL_MS);

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
                _timeoutTimer?.Dispose();
                _timeoutTimer = null;

                _frameSyncManager.Stop();
                _networkServer.Stop();
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
            Console.WriteLine($"在线玩家: {_networkServer.OnlineCount}");
            Console.WriteLine($"房间数量: {_roomManager.GetRoomCount()}");
            Console.WriteLine($"当前帧ID: {_frameSyncManager.CurrentFrameId}");
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
                // 任意消息都算活跃：刷新心跳时间
                // 这样帧同步期间即使不发心跳包也不会被误判掉线
                _networkServer.TouchPlayer(GetOrCreatePlayer(clientEndpoint));

                switch (msgId)
                {
                    // === 登录认证消息 ===
                    case MsgID.C2SLogin:
                        HandleLogin(clientEndpoint, data);
                        break;

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

                    // === 战斗事件 ===
                    case MsgID.C2SFireEvent:
                        HandleFireEvent(clientEndpoint, data);
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

        #region 消息处理器 - 登录认证

        /// <summary>
        /// 处理登录请求
        /// </summary>
        private void HandleLogin(IPEndPoint client, byte[] data)
        {
            try
            {
                var req = LoginReq.Parser.ParseFrom(data);

                Console.WriteLine($"[GameServer] 收到登录请求: {req.Username}");

                // 调用认证服务验证用户
                var result = _authService.ValidateLogin(req.Username, req.Password);

                if (result.Success && result.User != null)
                {
                    // 🔑 同账号已在别处在线：顶掉旧会话
                    // 场景：Unity 停止播放 / 直接关掉打包程序，客户端来不及发登出，
                    // 旧会话会一直占着在线表导致再也登不上。这里直接踢掉旧的放行新的
                    if (_networkServer.IsPlayerOnline(result.User.UserId))
                    {
                        Console.WriteLine($"[GameServer] ⚠️ [{req.Username}] 已在别处在线，踢掉旧会话");
                        HandlePlayerDisconnect(result.User.UserId, "被新登录顶号");
                    }

                    // 🔑 生成会话Token
                    string token = GenerateSessionToken(result.User.UserId);

                    // 🔑 登录成功后才添加到在线玩家列表
                    _networkServer.AddPlayer(result.User.UserId, client);

                    // 构建成功响应
                    var resp = new LoginResp
                    {
                        Success = true,
                        UserId = result.User.UserId,          // 👈 关键！返回给客户端的用户唯一ID
                        Username = result.User.Nickname,       // 返回昵称
                        Token = token,
                        ExpireTime = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeMilliseconds(),
                        Message = "登录成功"
                    };

                    _networkServer.Send(client, MsgID.S2CLogin, resp);

                    Console.WriteLine($"[GameServer] ✅ [{req.Username}] 登录成功，ID: {result.User.UserId}");
                }
                else
                {
                    // 构建失败响应
                    var resp = new LoginResp
                    {
                        Success = false,
                        Message = result.Message ?? "登录失败"
                    };

                    _networkServer.Send(client, MsgID.S2CLogin, resp);

                    Console.WriteLine($"[GameServer] ❌ [{req.Username}] 登录失败: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] ❌ 登录处理异常: {ex.Message}");

                var errorResp = new LoginResp
                {
                    Success = false,
                    Message = "服务器内部错误"
                };
                _networkServer.Send(client, MsgID.S2CLogin, errorResp);
            }
        }

        /// <summary>
        /// 生成会话令牌（随机32字节Base64）
        /// </summary>
        private static string GenerateSessionToken(string userId)
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] bytes = new byte[32];
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes);
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
                _frameSyncManager.RemovePlayerState(playerId);
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

            // 将输入提交给帧同步管理器（位置会在内部做速度校验）
            _frameSyncManager.AddPlayerInput(playerId, req);
        }

        /// <summary>
        /// 处理开火事件：demo阶段服务端只做中继——
        /// 把射击方上报的弹道/命中信息转发给房间内其他玩家，供其重播视觉表现。
        /// 伤害结算接入后，这里改为先验证命中合法性再转发/结算
        /// </summary>
        private void HandleFireEvent(IPEndPoint client, byte[] data)
        {
            var req = FireEventReq.Parser.ParseFrom(data);
            string playerId = GetOrCreatePlayer(client);

            // 探针：确认事件到达服务端
            Console.WriteLine($"[GameServer] 🔫 收到开火事件 [{playerId}] hit={req.HitSomething}");

            string? roomId = _roomManager.GetPlayerRoomId(playerId);
            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine("[GameServer] ⚠️ 开火事件被丢弃：射手不在任何房间");
                return;
            }

            var room = _roomManager.GetRoom(roomId!);
            if (room == null) return;

            var notify = new FireEventNotify
            {
                PlayerId = playerId,
                OriginX = req.OriginX,
                OriginY = req.OriginY,
                OriginZ = req.OriginZ,
                DirX = req.DirX,
                DirY = req.DirY,
                DirZ = req.DirZ,
                EndX = req.EndX,
                EndY = req.EndY,
                EndZ = req.EndZ,
                HitSomething = req.HitSomething,
                HitPlayerId = req.HitPlayerId,
                HitNormalX = req.HitNormalX,
                HitNormalY = req.HitNormalY,
                HitNormalZ = req.HitNormalZ
            };

            // 转发给房间内除射手以外的玩家（射手本地已即时播放，不发回去避免双重特效）
            foreach (var player in room.Players)
            {
                if (player.PlayerId == playerId) continue;

                var endpoint = _networkServer.GetPlayerEndpoint(player.PlayerId);
                if (endpoint != null)
                {
                    _networkServer.Send(endpoint, MsgID.S2CFireEvent, notify);
                }
            }
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

        #region 私有方法 - 连接维护

        /// <summary>
        /// 扫描并清理心跳超时的玩家
        /// UDP 是无连接的，客户端崩溃/强退不会通知服务端，只能靠超时发现
        /// </summary>
        private void CheckTimeouts()
        {
            if (!_isRunning) return;

            try
            {
                var timedOut = _networkServer.GetTimedOutPlayers(TimeSpan.FromSeconds(HEARTBEAT_TIMEOUT_SEC));
                foreach (var playerId in timedOut)
                {
                    HandlePlayerDisconnect(playerId, $"心跳超时 {HEARTBEAT_TIMEOUT_SEC}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] ❌ 超时检测异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 统一处理玩家断开：退出房间 → 广播离开 → 清理帧同步状态 → 移除在线记录
        /// </summary>
        private void HandlePlayerDisconnect(string playerId, string reason)
        {
            try
            {
                // 1. 退出所在房间，并通知房间内其他人
                string? roomId = _roomManager.GetPlayerRoomId(playerId);
                if (!string.IsNullOrEmpty(roomId))
                {
                    _roomManager.LeaveRoom(playerId, roomId);
                    BroadcastPlayerLeave(roomId, playerId);
                }

                // 2. 清理帧同步残留（输入缓冲、位置状态、校验记录）
                _frameSyncManager.RemovePlayerState(playerId);

                // 3. 从在线表移除（含端点反向索引）
                _networkServer.RemovePlayer(playerId);

                Console.WriteLine($"[GameServer] 👋 玩家 [{playerId}] 已断开：{reason}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] ❌ 断开处理异常 [{playerId}]: {ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取或创建玩家标识
        /// 优先使用登录认证时分配的 UserId（如 user_001，已由 AddPlayer 登记端点映射），
        /// 未登录的连接才退回用 IP:端口 字符串标识。
        /// 注意：房间成员、帧同步广播全部以此为键，必须与 NetworkServer 在线表的键一致，
        /// 否则 GetPlayerEndpoint 反查不到连接，广播会静默丢失
        /// </summary>
        private string GetOrCreatePlayer(IPEndPoint client)
        {
            if (_networkServer.TryGetPlayerId(client, out var userId))
            {
                return userId;
            }
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

        #region 私有方法 - 路径工具

        /// <summary>
        /// 查找项目根目录（包含 .csproj 文件的目录）
        /// 从当前运行目录向上回溯，直到找到 .csproj 文件
        /// </summary>
        /// <returns>项目根目录绝对路径</returns>
        private static string FindProjectDirectory()
        {
            // 从当前运行目录开始（通常是 bin/Debug/net8.0/）
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;

            // 向上回溯最多5层，查找包含 .csproj 文件的目录
            for (int i = 0; i < 5; i++)
            {
                // 检查当前目录是否有 .csproj 文件
                string[] csprojFiles = Directory.GetFiles(currentDir, "*.csproj");
                if (csprojFiles.Length > 0)
                {
                    return currentDir;
                }

                // 检查父目录
                string? parentDir = Directory.GetParent(currentDir)?.FullName;
                if (string.IsNullOrEmpty(parentDir))
                {
                    break;
                }
                currentDir = parentDir;
            }

            // 如果找不到项目目录，回退到运行目录
            Console.WriteLine("[GameServer] ⚠️ 无法找到项目根目录（.csproj），使用运行目录");
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        #endregion
    }
}
