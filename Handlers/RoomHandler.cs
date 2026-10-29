using System;
using System.Linq;
using System.Net;
using GameLockstep;
using GameNetwork;
using Server_MyDemo.Network;

namespace Server_MyDemo.Handlers
{
    /// <summary>房间管理消息处理：创建/加入/离开/列表/开始游戏</summary>
    public sealed class RoomHandler
    {
        private readonly GameServer _server;

        public RoomHandler(GameServer server)
        {
            _server = server;
        }

        public void Register(MessageRouter router)
        {
            router.Register(MsgID.C2SCreateRoom, CreateRoomReq.Parser, HandleCreateRoom);
            router.Register(MsgID.C2SJoinRoom, JoinRoomReq.Parser, HandleJoinRoom);
            router.Register(MsgID.C2SLeaveRoom, LeaveRoomReq.Parser, HandleLeaveRoom);
            router.Register(MsgID.C2SGetRoomList, GetRoomListReq.Parser, HandleGetRoomList);
            router.Register(MsgID.C2SStartGame, StartGameReq.Parser, HandleStartGame);
        }

        private void HandleCreateRoom(IPEndPoint endpoint, CreateRoomReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            var result = _server.Rooms.CreateRoom(
                playerId,
                _server.Network.GetPlayerName(playerId),
                req.RoomName,
                req.Password,
                req.RoomId);

            _server.Network.Send(endpoint, MsgID.S2CCreateRoom, new CreateRoomResp
            {
                Success = result.Success,
                RoomId = result.RoomId ?? "",
                Message = result.Message ?? "",
                PlayerId = playerId
            });

            if (result.Success)
            {
                Console.WriteLine($"[Room] 玩家 [{playerId}] 创建房间 [{result.RoomId}]");
            }
        }

        private void HandleJoinRoom(IPEndPoint endpoint, JoinRoomReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            var result = _server.Rooms.JoinRoom(
                playerId,
                _server.Network.GetPlayerName(playerId),
                req.RoomId,
                req.Password);

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

            _server.Network.Send(endpoint, MsgID.S2CJoinRoom, resp);

            if (result.Success)
            {
                BroadcastPlayerJoin(req.RoomId, playerId);
                Console.WriteLine($"[Room] 玩家 [{playerId}] 加入房间 [{req.RoomId}]");
            }
        }

        private void HandleLeaveRoom(IPEndPoint endpoint, LeaveRoomReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            var result = _server.Rooms.LeaveRoom(playerId, req.RoomId);

            _server.Network.Send(endpoint, MsgID.S2CLeaveRoom, new LeaveRoomResp
            {
                Success = result.Success,
                Message = result.Message ?? ""
            });

            if (result.Success)
            {
                _server.NotifyPlayerLeave(req.RoomId, playerId);
                _server.Lockstep.RemovePlayer(req.RoomId, playerId);
                Console.WriteLine($"[Room] 玩家 [{playerId}] 离开房间 [{req.RoomId}]");
            }
        }

        private void HandleGetRoomList(IPEndPoint endpoint, GetRoomListReq req)
        {
            var rooms = _server.Rooms.GetAllRooms();
            var resp = new RoomListResp();
            resp.Rooms.AddRange(rooms);

            _server.Network.Send(endpoint, MsgID.S2CRoomList, resp);
        }

        private void HandleStartGame(IPEndPoint endpoint, StartGameReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            string? roomId = _server.Rooms.GetPlayerRoomId(playerId);
            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine($"[Room] ⚠️ 玩家 [{playerId}] 不在任何房间中，无法开始游戏");
                return;
            }

            var result = _server.Rooms.StartGame(roomId!, playerId);
            if (!result.Success)
            {
                Console.WriteLine($"[Room] ⚠️ 开始游戏失败: {result.Message}");
                return;
            }

            var room = _server.Rooms.GetRoom(roomId!);
            if (room == null) return;

            // 座位按房间加入顺序分配；种子与座位表随 GameStart 下发，各端据此构造 LogicWorld。
            // 开赛时刻留出加载宽限：客户端要先进 Main 场景建好会话才能收发指令，
            // 封帧时钟与客户端帧号都从同一个 StartTime 起算
            var playerIds = room.Players.Select(p => p.PlayerId).ToList();
            long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + LockstepConfig.START_GRACE_MS;
            var (seed, seats) = _server.Lockstep.StartGame(roomId!, playerIds, startMs);

            var notify = new GameStartNotify
            {
                RoomId = roomId!,
                StartTime = startMs,
                LockstepSeed = seed,
                TickRate = LockstepConfig.TICK_RATE
            };
            notify.Seats.AddRange(seats);

            _server.BroadcastToRoom(roomId!, notify, MsgID.S2CGameStart);

            Console.WriteLine($"[Room] 玩家 [{playerId}] 开始游戏，房间 [{roomId}] 共 {room.Players.Count} 人");
        }

        /// <summary>通知房间内其他玩家：有人加入</summary>
        private void BroadcastPlayerJoin(string roomId, string playerId)
        {
            var room = _server.Rooms.GetRoom(roomId);
            if (room == null) return;

            var notify = new PlayerJoinNotify
            {
                Player = new PlayerInfo
                {
                    PlayerId = playerId,
                    PlayerName = _server.Network.GetPlayerName(playerId)
                }
            };

            foreach (var player in room.Players)
            {
                if (player.PlayerId == playerId) continue;

                var endpoint = _server.Network.GetPlayerEndpoint(player.PlayerId);
                if (endpoint != null)
                {
                    _server.Network.Send(endpoint, MsgID.S2CPlayerJoin, notify);
                }
            }
        }
    }
}
