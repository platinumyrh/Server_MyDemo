using System;
using System.Net;

namespace Server_MyDemo.Models
{
    /// <summary>
    /// 在线玩家会话数据模型（服务器内部使用）
    /// 持有网络端点与最后活跃时间，供网络层做端点反查与心跳超时检测
    /// </summary>
    public class Player
    {
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = "Player";

        public IPEndPoint? Endpoint { get; set; }

        public DateTime LastHeartbeatTime { get; set; } = DateTime.Now;

        /// <summary>刷新最后活跃时间（收到该玩家任意消息时调用）</summary>
        public void Touch() => LastHeartbeatTime = DateTime.Now;

        public bool IsTimedOut(TimeSpan timeout) => DateTime.Now - LastHeartbeatTime > timeout;
    }
}
