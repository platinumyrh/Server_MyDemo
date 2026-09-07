using Google.Protobuf;
using Server_MyDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using GameNetwork;  // 添加 GameNetwork 命名空间

namespace Server_MyDemo.Server
{
    /// <summary>
    /// 网络服务器 - UDP通信层
    /// 负责收发消息、管理玩家连接
    /// </summary>
    public class NetworkServer
    {
        #region 私有字段

        private UdpClient? _udpClient;
        private bool _isRunning;
        private int _port;

        // 玩家会话表（playerId -> 会话，含端点与最后活跃时间）
        private Dictionary<string, Player> _players;

        // 反向索引（endpoint字符串 -> playerId）
        // 收包时要按端点反查玩家，走索引是 O(1)，避免每包线性扫描整张表
        private Dictionary<string, string> _endpointToPlayerId;

        // 消息接收回调
        public Action<IPEndPoint, byte[], MsgID>? OnMessageReceived { get; set; }

        // 接收缓冲区大小
        private const int BUFFER_SIZE = 65535; // 64KB（UDP最大包）

        // 禁用 UDP 端口不可达错误（WSAECONNRESET），避免客户端关闭后服务端刷屏
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        // 错误日志节流：相同消息在窗口内只打一次，防止意外刷屏
        private readonly Dictionary<string, DateTime> _lastErrorLog = new();
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(1);

        #endregion

        #region 构造函数

        public NetworkServer(int port)
        {
            _port = port;
            _players = new Dictionary<string, Player>();
            _endpointToPlayerId = new Dictionary<string, string>();
            _isRunning = false;

            Console.WriteLine($"[NetworkServer] 初始化，端口: {_port}");
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 启动网络服务
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _udpClient = new UdpClient(_port);

                // 关闭 WSAECONNRESET：客户端关闭后 OS 会回 ICMP port unreachable，
                // 该错误会缓存在 socket 上，导致后续 send/recv 抛 SocketException 刷屏。
                // 设为 false 让 socket 静默忽略，这是 Windows UDP 服务器的标准做法
                _udpClient.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);

                _isRunning = true;

                // 开始异步接收
                BeginReceive();

                Console.WriteLine($"[NetworkServer] ✅ UDP监听启动，端口: {_port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkServer] ❌ 启动失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 停止网络服务
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _udpClient?.Close();
            _players.Clear();
            _endpointToPlayerId.Clear();

            Console.WriteLine("[NetworkServer] UDP监听已停止");
        }

        /// <summary>
        /// 发送消息给指定客户端
        /// </summary>
        public void Send<T>(IPEndPoint endpoint, MsgID msgId, T message) where T : IMessage<T>
        {
            if (!_isRunning || _udpClient == null) return;

            try
            {
                // 序列化：MsgID(4字节) + Length(4字节) + ProtoData
                byte[] protoData = message.ToByteArray();
                byte[] msgIdBytes = BitConverter.GetBytes((int)msgId);
                byte[] lengthBytes = BitConverter.GetBytes(protoData.Length);

                byte[] sendData = new byte[8 + protoData.Length];
                Buffer.BlockCopy(msgIdBytes, 0, sendData, 0, 4);
                Buffer.BlockCopy(lengthBytes, 0, sendData, 4, 4);
                Buffer.BlockCopy(protoData, 0, sendData, 8, protoData.Length);

                _udpClient.Send(sendData, sendData.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkServer] ❌ 发送失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取在线玩家数量（仅统计已登录认证的玩家）
        /// </summary>
        public int OnlineCount => _players.Count;

        /// <summary>
        /// 获取玩家的Endpoint
        /// </summary>
        public IPEndPoint? GetPlayerEndpoint(string playerId)
        {
            return _players.TryGetValue(playerId, out var player) ? player.Endpoint : null;
        }

        /// <summary>
        /// 根据连接端点反查已认证的玩家ID（登录成功后由AddPlayer登记）
        /// 走反向索引，O(1)
        /// </summary>
        public bool TryGetPlayerId(IPEndPoint endpoint, out string playerId)
        {
            return _endpointToPlayerId.TryGetValue(endpoint.ToString(), out playerId!);
        }

        /// <summary>
        /// 刷新玩家最后活跃时间（收到该玩家任意消息时调用）
        /// </summary>
        public void TouchPlayer(string playerId)
        {
            if (_players.TryGetValue(playerId, out var player))
            {
                player.Touch();
            }
        }

        /// <summary>
        /// 取出所有超过指定时长没有任何活动的玩家（心跳超时检测用）
        /// </summary>
        public List<string> GetTimedOutPlayers(TimeSpan timeout)
        {
            return _players
                .Where(kvp => kvp.Value.IsTimedOut(timeout))
                .Select(kvp => kvp.Key)
                .ToList();
        }

        /// <summary>
        /// 添加已认证的玩家（登录成功后调用）
        /// </summary>
        /// <param name="playerId">玩家唯一ID（UserId）</param>
        /// <param name="endpoint">玩家网络端点</param>
        public void AddPlayer(string playerId, IPEndPoint endpoint)
        {
            var player = new Player
            {
                PlayerId = playerId,
                Endpoint = endpoint
            };
            player.Touch();

            _players[playerId] = player;
            _endpointToPlayerId[endpoint.ToString()] = playerId;

            Console.WriteLine($"[NetworkServer] ✅ 玩家上线: {playerId} ({endpoint})");
            Console.WriteLine($"[NetworkServer] 📊 当前在线: {_players.Count} 人");
        }

        /// <summary>
        /// 移除玩家（登出、超时或断开连接时调用）
        /// </summary>
        /// <param name="playerId">玩家唯一ID</param>
        public void RemovePlayer(string playerId)
        {
            if (_players.Remove(playerId, out var player))
            {
                if (player.Endpoint != null)
                {
                    _endpointToPlayerId.Remove(player.Endpoint.ToString());
                }

                Console.WriteLine($"[NetworkServer] 👋 玩家离线: {playerId}");
                Console.WriteLine($"[NetworkServer] 📊 当前在线: {_players.Count} 人");
            }
        }

        /// <summary>
        /// 检查玩家是否已在线
        /// </summary>
        /// <param name="playerId">玩家唯一ID</param>
        /// <returns>是否在线</returns>
        public bool IsPlayerOnline(string playerId)
        {
            return _players.ContainsKey(playerId);
        }

        #endregion

        #region 私有方法 - 接收数据

        /// <summary>
        /// 开始异步接收数据
        /// </summary>
        private void BeginReceive()
        {
            if (!_isRunning || _udpClient == null) return;

            try
            {
                _udpClient.BeginReceive(ReceiveCallback, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkServer] ⚠️ BeginReceive异常: {ex.Message}");

                // 尝试重新开始接收
                if (_isRunning)
                {
                    System.Threading.Thread.Sleep(100);
                    BeginReceive();
                }
            }
        }

        /// <summary>
        /// 异步接收回调
        /// </summary>
        private void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                IPEndPoint? remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] receivedData = _udpClient?.EndReceive(ar, ref remoteEndpoint) ?? Array.Empty<byte>();

                if (receivedData.Length > 0 && remoteEndpoint != null)
                {
                    ProcessReceivedData(remoteEndpoint, receivedData);
                }

                // 继续接收下一个包
                if (_isRunning)
                {
                    BeginReceive();
                }
            }
            catch (ObjectDisposedException)
            {
                // 正常关闭时忽略
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // 客户端关闭后 OS 缓存的 ICMP unreachable 被消费掉了，静默忽略
                // （SIO_UDP_CONNRESET 已禁掉源头，这里是兜底）
            }
            catch (Exception ex)
            {
                if (ShouldLogError("ReceiveCallback:" + ex.Message))
                {
                    Console.WriteLine($"[NetworkServer] ⚠️ ReceiveCallback异常: {ex.Message}");
                }

                if (_isRunning)
                {
                    BeginReceive();
                }
            }
        }

        /// <summary>
        /// 处理接收到的数据
        /// </summary>
        private void ProcessReceivedData(IPEndPoint endpoint, byte[] data)
        {
            try
            {
                // 解析协议头：MsgID(4字节) + Length(4字节) + Data
                if (data.Length < 8)
                {
                    Console.WriteLine($"[NetworkServer] ⚠️ 数据包太短: {data.Length} bytes");
                    return;
                }

                int msgId = BitConverter.ToInt32(data, 0);
                int length = BitConverter.ToInt32(data, 4);

                if (length < 0 || data.Length < 8 + length)
                {
                    Console.WriteLine($"[NetworkServer] ⚠️ 数据长度错误: expected={length}, actual={data.Length - 8}");
                    return;
                }

                byte[] payload = new byte[length];
                Buffer.BlockCopy(data, 8, payload, 0, length);

                // 触发消息回调（不再自动添加玩家，由GameServer在登录成功后添加）
                OnMessageReceived?.Invoke(endpoint, payload, (MsgID)msgId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkServer] ❌ 处理数据异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 错误日志节流：相同 key 的消息在窗口内只打印一次，防止意外刷屏
        /// </summary>
        private bool ShouldLogError(string key)
        {
            if (_lastErrorLog.TryGetValue(key, out var last) && DateTime.Now - last < ErrorLogInterval)
            {
                return false;
            }
            _lastErrorLog[key] = DateTime.Now;
            return true;
        }

        #endregion
    }
}
