using Google.Protobuf;
using System;
using System.Collections.Generic;
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

        // 玩家连接表（playerId -> endpoint）
        private Dictionary<string, IPEndPoint> _players;

        // 消息接收回调
        public Action<IPEndPoint, byte[], MsgID>? OnMessageReceived { get; set; }

        // 接收缓冲区大小
        private const int BUFFER_SIZE = 65535; // 64KB（UDP最大包）

        #endregion

        #region 构造函数

        public NetworkServer(int port)
        {
            _port = port;
            _players = new Dictionary<string, IPEndPoint>();
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
            _players.TryGetValue(playerId, out var endpoint);
            return endpoint;
        }

        /// <summary>
        /// 添加已认证的玩家（登录成功后调用）
        /// </summary>
        /// <param name="playerId">玩家唯一ID（UserId）</param>
        /// <param name="endpoint">玩家网络端点</param>
        public void AddPlayer(string playerId, IPEndPoint endpoint)
        {
            _players[playerId] = endpoint;
            Console.WriteLine($"[NetworkServer] ✅ 玩家上线: {playerId} ({endpoint})");
            Console.WriteLine($"[NetworkServer] 📊 当前在线: {_players.Count} 人");
        }

        /// <summary>
        /// 移除玩家（登出或断开连接时调用）
        /// </summary>
        /// <param name="playerId">玩家唯一ID</param>
        public void RemovePlayer(string playerId)
        {
            if (_players.Remove(playerId))
            {
                Console.WriteLine($"[NetworkServer] 👋 玩家离线: {playerId}");
                Console.WriteLine($"[NetworkServer] 📊 当前在线: {_players.Count} 人");
            }
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
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkServer] ⚠️ ReceiveCallback异常: {ex.Message}");
                
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

        #endregion
    }
}
