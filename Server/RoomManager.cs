using Server_MyDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using GameNetwork;  // 添加 GameNetwork 命名空间

namespace Server_MyDemo.Server
{
    /// <summary>
    /// 房间管理器
    /// 负责创建、销毁、管理游戏房间
    /// </summary>
    public class RoomManager
    {
        #region 私有字段

        private Dictionary<string, Room> _rooms;       // 所有房间（roomId -> Room）
        private Dictionary<string, string> _playerRoom; // 玩家所在房间（playerId -> roomId）
        private object _lock;                           // 线程锁

        // 配置
        private const int MAX_ROOMS = 100;              // 最大房间数
        private const int MAX_PLAYERS_PER_ROOM = 4;     // 每个房间最大人数

        #endregion

        #region 构造函数

        public RoomManager()
        {
            _rooms = new Dictionary<string, Room>();
            _playerRoom = new Dictionary<string, string>();
            _lock = new object();

            Console.WriteLine($"[RoomManager] 初始化完成，最大房间数: {MAX_ROOMS}, 每房最大人数: {MAX_PLAYERS_PER_ROOM}");
        }

        #endregion

        #region 公开方法 - 房间操作

        /// <summary>
        /// 创建房间
        /// </summary>
        public (bool Success, string? RoomId, string? Message) CreateRoom(string hostId, string hostName, string roomName = "", string password = "", string roomId = "")
        {
            lock (_lock)
            {
                // 检查玩家是否已在其他房间
                if (_playerRoom.ContainsKey(hostId))
                {
                    return (false, null, "你已经在其他房间中");
                }

                // 检查房间数量限制
                if (_rooms.Count >= MAX_ROOMS)
                {
                    return (false, null, "房间数量已达上限");
                }

                // 创建新房间：优先使用客户端指定的固定ID，否则自动生成
                if (string.IsNullOrEmpty(roomId))
                {
                    roomId = GenerateRoomId();
                }
                else if (_rooms.ContainsKey(roomId))
                {
                    return (false, null, "房间ID已存在");
                }

                var room = new Room
                {
                    RoomId = roomId,
                    RoomName = string.IsNullOrEmpty(roomName) ? $"Room_{roomId.Substring(0, 6)}" : roomName,
                    HostId = hostId,
                    MaxPlayers = MAX_PLAYERS_PER_ROOM,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password,
                    IsPlaying = false
                };

                // 房主加入房间
                var playerInfo = CreatePlayerInfo(hostId, hostName);
                room.Players.Add(playerInfo);

                // 保存数据
                _rooms[roomId] = room;
                _playerRoom[hostId] = roomId;

                return (true, roomId, "创建成功");
            }
        }

        /// <summary>
        /// 开始游戏（仅房主可调用）
        /// </summary>
        public (bool Success, string? Message) StartGame(string roomId, string hostId)
        {
            lock (_lock)
            {
                if (!_rooms.TryGetValue(roomId, out var room))
                {
                    return (false, "房间不存在");
                }

                if (room.HostId != hostId)
                {
                    return (false, "只有房主能开始游戏");
                }

                room.IsPlaying = true;
                Console.WriteLine($"[RoomManager] 房间 [{roomId}] 游戏开始，玩家数: {room.Players.Count}");
                return (true, "游戏开始");
            }
        }

        /// <summary>
        /// 加入房间
        /// </summary>
        public (bool Success, RoomInfo? RoomInfo, string? Message) JoinRoom(string playerId, string playerName, string roomId, string password = "")
        {
            lock (_lock)
            {
                // 检查房间是否存在
                if (!_rooms.TryGetValue(roomId, out var room))
                {
                    return (false, null, "房间不存在");
                }

                // 检查是否已在其他房间
                if (_playerRoom.ContainsKey(playerId))
                {
                    return (false, null, "你已经在其他房间中");
                }

                // 检查密码
                if (room.HasPassword && room.Password != password)
                {
                    return (false, null, "密码错误");
                }

                // 检查房间人数
                if (room.Players.Count >= room.MaxPlayers)
                {
                    return (false, null, "房间已满");
                }

                // 检查游戏状态
                if (room.IsPlaying)
                {
                    return (false, null, "游戏中，无法加入");
                }

                // 加入房间
                var playerInfo = CreatePlayerInfo(playerId, playerName);
                room.Players.Add(playerInfo);
                _playerRoom[playerId] = roomId;

                Console.WriteLine($"[RoomManager] 玩家 [{playerId}] 加入房间 [{roomId}]，当前人数: {room.Players.Count}/{room.MaxPlayers}");

                return (true, ConvertToRoomInfo(room), "加入成功");
            }
        }

        /// <summary>
        /// 离开房间
        /// </summary>
        public (bool Success, string? Message) LeaveRoom(string playerId, string roomId)
        {
            lock (_lock)
            {
                // 检查房间是否存在
                if (!_rooms.TryGetValue(roomId, out var room))
                {
                    return (false, "房间不存在");
                }

                // 检查玩家是否在房间中
                var player = room.Players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player == null)
                {
                    return (false, "你不在此房间中");
                }

                // 从房间移除玩家
                room.Players.Remove(player);
                _playerRoom.Remove(playerId);

                Console.WriteLine($"[RoomManager] 玩家 [{playerId}] 离开房间 [{roomId}]，剩余人数: {room.Players.Count}");

                // 如果房间为空，则销毁
                if (room.Players.Count == 0)
                {
                    _rooms.Remove(roomId);
                    Console.WriteLine($"[RoomManager] 房间 [{roomId}] 已销毁（无玩家）");
                }
                else if (playerId == room.HostId)
                // 如果是房主离开，转移房主权限
                {
                    room.HostId = room.Players[0].PlayerId;
                    Console.WriteLine($"[RoomManager] 房主转移给: {room.HostId}");
                }

                return (true, "离开成功");
            }
        }

        /// <summary>
        /// 获取房间信息
        /// </summary>
        public Room? GetRoom(string roomId)
        {
            lock (_lock)
            {
                _rooms.TryGetValue(roomId, out var room);
                return room;
            }
        }

        /// <summary>
        /// 获取所有房间列表
        /// </summary>
        public List<RoomInfo> GetAllRooms()
        {
            lock (_lock)
            {
                return _rooms.Values.Select(r => ConvertToRoomInfo(r)).ToList();
            }
        }

        /// <summary>
        /// 获取房间数量
        /// </summary>
        public int GetRoomCount()
        {
            lock (_lock)
            {
                return _rooms.Count;
            }
        }

        /// <summary>
        /// 获取玩家所在的房间ID
        /// </summary>
        public string? GetPlayerRoomId(string playerId)
        {
            lock (_lock)
            {
                _playerRoom.TryGetValue(playerId, out var roomId);
                return roomId;
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 生成唯一房间ID
        /// </summary>
        private string GenerateRoomId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        }

        /// <summary>
        /// 创建玩家信息（playerName 为登录昵称，空则回退 ID）
        /// </summary>
        private PlayerInfo CreatePlayerInfo(string playerId, string playerName)
        {
            return new PlayerInfo
            {
                PlayerId = playerId,
                PlayerName = string.IsNullOrEmpty(playerName) ? $"Player_{playerId}" : playerName,
                IsReady = false
            };
        }

        /// <summary>
        /// 转换为Proto的RoomInfo
        /// </summary>
        private RoomInfo ConvertToRoomInfo(Room room)
        {
            var info = new RoomInfo
            {
                RoomId = room.RoomId,
                RoomName = room.RoomName,
                HostId = room.HostId,
                MaxPlayers = room.MaxPlayers,
                CurrentPlayers = room.Players.Count,
                HasPassword = room.HasPassword,
                IsPlaying = room.IsPlaying
            };

            info.Players.AddRange(room.Players);  // 修复：使用 Players 属性而不是 Rooms
            
            return info;
        }

        #endregion
    }
}
