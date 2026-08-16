namespace Server_MyDemo.Models
{
    /// <summary>
    /// 玩家数据模型（服务器内部使用）
    /// </summary>
    public class Player
    {
        public string PlayerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = "Player";
        public string RoomId { get; set; } = string.Empty;
        
        // 连接信息
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        
        // 游戏状态
        public bool IsReady { get; set; }
        public bool IsOnline { get; set; } = true;
        public DateTime LastHeartbeatTime { get; set; } = DateTime.Now;
        
        // 游戏数据（预留）
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
    }
}
