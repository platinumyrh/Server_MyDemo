using System.Collections.Generic;
using GameNetwork;

namespace Server_MyDemo.Models
{
    /// <summary>
    /// 房间数据模型（服务器内部使用）
    /// </summary>
    public class Room
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public int MaxPlayers { get; set; } = 4;
        public bool HasPassword { get; set; }
        public string Password { get; set; } = string.Empty;
        public bool IsPlaying { get; set; }
        
        public List<PlayerInfo> Players { get; set; } = new List<PlayerInfo>();
        
        // 扩展字段（预留）
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();
    }
}
