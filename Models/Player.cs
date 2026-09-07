using System;
using System.Net;

namespace Server_MyDemo.Models
{
    /// <summary>
    /// 玩家会话数据模型（服务器内部使用）
    /// 表示一条已认证的在线连接：持有网络端点与最后活跃时间，
    /// 供网络层做端点反查与心跳超时检测
    /// </summary>
    public class Player
    {
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = "Player";
        public string RoomId { get; set; } = string.Empty;

        // 连接信息
        public IPEndPoint? Endpoint { get; set; }
        public string IpAddress => Endpoint?.Address.ToString() ?? string.Empty;
        public int Port => Endpoint?.Port ?? 0;

        // 游戏状态
        public bool IsReady { get; set; }
        public bool IsOnline { get; set; } = true;
        public DateTime LastHeartbeatTime { get; set; } = DateTime.Now;

        // 游戏数据（预留：服务端权威模式下才会使用）
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float Rotation { get; set; }
        public int Health { get; set; } = 100;
        public int Score { get; set; }

        /// <summary>
        /// 获取唯一标识（IP:Port）
        /// </summary>
        public string GetUniqueKey() => $"{IpAddress}:{Port}";

        /// <summary>
        /// 刷新最后活跃时间（收到该玩家任意消息时调用）
        /// </summary>
        public void Touch()
        {
            LastHeartbeatTime = DateTime.Now;
            IsOnline = true;
        }

        /// <summary>
        /// 是否已超过指定时长没有任何活动
        /// </summary>
        public bool IsTimedOut(TimeSpan timeout) => DateTime.Now - LastHeartbeatTime > timeout;
    }
}
