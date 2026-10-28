using System;
using System.IO;
using System.Net;
using GameNetwork;
using Google.Protobuf;
using Server_MyDemo.Handlers;
using Server_MyDemo.Network;
using Server_MyDemo.Services;

namespace Server_MyDemo
{
    /// <summary>
    /// 服务器组装与生命周期：创建各模块、注册消息路由、维护在线超时。
    /// 网络收发在 Network/，具体消息在 Handlers/，房间/认证/lockstep 中继在 Services/。
    /// </summary>
    public class GameServer
    {
        private const int SERVER_PORT = 7777;
        private const int HEARTBEAT_TIMEOUT_SEC = 15;       // 超过该时长无任何消息判定掉线
        private const int TIMEOUT_CHECK_INTERVAL_MS = 5000; // 超时扫描间隔

        private readonly NetworkServer _network;
        private readonly RoomManager _rooms;
        private readonly AuthService _auth;
        private readonly LockstepRelayManager _lockstep;
        private readonly MessageRouter _router = new();

        private System.Threading.Timer? _timeoutTimer;
        private bool _isRunning;

        internal NetworkServer Network => _network;
        internal RoomManager Rooms => _rooms;
        internal AuthService Auth => _auth;
        internal LockstepRelayManager Lockstep => _lockstep;

        public GameServer()
        {
            _rooms = new RoomManager();
            _lockstep = new LockstepRelayManager();

            // 封帧结果 → 房间广播（可能在网络线程/定时器线程触发，UDP Send 线程安全）
            _lockstep.FramesSealed += (roomId, notify) =>
                BroadcastToRoom(roomId, notify, MsgID.S2CLockstepInputs);

            // 用项目根目录（.csproj 所在目录）定位数据文件，避免 bin/Debug 与根目录路径混乱
            string dataPath = Path.Combine(FindProjectDirectory(), "Data", "users.json");
            _auth = new AuthService(dataPath);

            _network = new NetworkServer(SERVER_PORT);
            _network.OnMessageReceived = HandleMessage;

            new LoginHandler(this).Register(_router);
            new HeartbeatHandler(this).Register(_router);
            new RoomHandler(this).Register(_router);
            new LockstepHandler(this).Register(_router);

            Console.WriteLine("[Server] 初始化完成");
            Console.WriteLine($"[Server] 端口: {SERVER_PORT}, 心跳超时: {HEARTBEAT_TIMEOUT_SEC}s");
            Console.WriteLine($"[Server] 已加载 {_auth.GetUserCount()} 个用户账号");
        }

        #region 生命周期

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _network.Start();
                _lockstep.Start();

                _timeoutTimer = new System.Threading.Timer(
                    _ => CheckTimeouts(),
                    null,
                    TIMEOUT_CHECK_INTERVAL_MS,
                    TIMEOUT_CHECK_INTERVAL_MS);

                _isRunning = true;
                Console.WriteLine("[Server] ✅ 服务器启动成功！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] ❌ 启动失败: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            try
            {
                _timeoutTimer?.Dispose();
                _timeoutTimer = null;

                _lockstep.Stop();
                _network.Stop();
                Console.WriteLine("[Server] 服务器已停止");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] 停止时出错: {ex.Message}");
            }
        }

        public void PrintStatus()
        {
            Console.WriteLine("\n========== 服务器状态 ==========");
            Console.WriteLine($"运行状态: {(_isRunning ? "运行中" : "已停止")}");
            Console.WriteLine($"在线玩家: {_network.OnlineCount}");
            Console.WriteLine($"房间数量: {_rooms.GetRoomCount()}");
            Console.WriteLine($"进行中的对局: {_lockstep.ActiveRoomCount}");
            Console.WriteLine("==================================\n");
        }

        #endregion

        #region 消息分发

        private void HandleMessage(IPEndPoint endpoint, byte[] data, MsgID msgId)
        {
            // 任意消息都算活跃：帧同步期间即使不发心跳包也不会被误判掉线
            _network.TouchPlayer(PlayerIdOf(endpoint));
            _router.Dispatch(endpoint, msgId, data);
        }

        /// <summary>
        /// 解析发送方标识：已登录用 UserId（与在线表键一致），
        /// 未登录连接退回 IP:端口（房间与 lockstep 广播都依赖该键，必须保持一致）
        /// </summary>
        internal string PlayerIdOf(IPEndPoint endpoint)
        {
            return _network.TryGetPlayerId(endpoint, out var playerId)
                ? playerId
                : $"{endpoint.Address}:{endpoint.Port}";
        }

        #endregion

        #region 房间广播与断线

        /// <summary>向房间内所有玩家广播一条消息</summary>
        internal void BroadcastToRoom<T>(string roomId, T message, MsgID msgId) where T : IMessage<T>
        {
            var room = _rooms.GetRoom(roomId);
            if (room == null) return;

            foreach (var player in room.Players)
            {
                var endpoint = _network.GetPlayerEndpoint(player.PlayerId);
                if (endpoint != null)
                {
                    _network.Send(endpoint, msgId, message);
                }
            }
        }

        /// <summary>通知房间内其他玩家：有人离开</summary>
        internal void NotifyPlayerLeave(string roomId, string playerId)
        {
            var room = _rooms.GetRoom(roomId);
            if (room == null) return;

            var notify = new PlayerLeaveNotify { PlayerId = playerId };

            foreach (var player in room.Players)
            {
                if (player.PlayerId == playerId) continue;

                var endpoint = _network.GetPlayerEndpoint(player.PlayerId);
                if (endpoint != null)
                {
                    _network.Send(endpoint, MsgID.S2CPlayerLeave, notify);
                }
            }
        }

        /// <summary>
        /// 统一处理玩家断开：退出房间 → 广播离开 → 清理中继座位 → 移除在线记录
        /// </summary>
        internal void DisconnectPlayer(string playerId, string reason)
        {
            try
            {
                string? roomId = _rooms.GetPlayerRoomId(playerId);
                if (!string.IsNullOrEmpty(roomId))
                {
                    _rooms.LeaveRoom(playerId, roomId!);
                    NotifyPlayerLeave(roomId!, playerId);
                    _lockstep.RemovePlayer(roomId!, playerId);
                }

                _network.RemovePlayer(playerId);
                Console.WriteLine($"[Server] 👋 玩家 [{playerId}] 已断开：{reason}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] ❌ 断开处理异常 [{playerId}]: {ex.Message}");
            }
        }

        /// <summary>
        /// 扫描心跳超时的玩家。UDP 无连接，客户端崩溃/强退不会通知服务端，只能靠超时发现
        /// </summary>
        private void CheckTimeouts()
        {
            if (!_isRunning) return;

            try
            {
                var timedOut = _network.GetTimedOutPlayers(TimeSpan.FromSeconds(HEARTBEAT_TIMEOUT_SEC));
                foreach (var playerId in timedOut)
                {
                    DisconnectPlayer(playerId, $"心跳超时 {HEARTBEAT_TIMEOUT_SEC}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] ❌ 超时检测异常: {ex.Message}");
            }
        }

        #endregion

        #region 路径工具

        /// <summary>从运行目录向上查找包含 .csproj 的项目根目录</summary>
        private static string FindProjectDirectory()
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;

            for (int i = 0; i < 5; i++)
            {
                if (Directory.GetFiles(currentDir, "*.csproj").Length > 0)
                {
                    return currentDir;
                }

                string? parentDir = Directory.GetParent(currentDir)?.FullName;
                if (string.IsNullOrEmpty(parentDir))
                {
                    break;
                }
                currentDir = parentDir;
            }

            Console.WriteLine("[Server] ⚠️ 无法找到项目根目录（.csproj），使用运行目录");
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        #endregion
    }
}
