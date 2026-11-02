using System;
using System.Collections.Generic;
using System.Net;
using GameLockstep;
using GameNetwork;
using Server_MyDemo.Network;

namespace Server_MyDemo.Handlers
{
    /// <summary>
    /// 断线重连：服务器用整局权威帧日志帮客户端快进到当前帧。
    ///
    /// 流程：
    /// 1. 登录后 C2S_Reconnect —— 查座位，标记重连中，回种子/座位/开赛时刻
    /// 2. 客户端进 Main、重建玩家视图，发 C2S_ReconnectReady
    /// 3. 服务器持锁把 History 分块发完（期间不再封新帧），最后 done，
    ///    同时把该座位从“掉线”恢复为可收指令
    /// 之后新封的帧走正常的 S2C_LockstepInputs 广播，客户端无缝接回实时循环
    /// </summary>
    public sealed class ReconnectHandler
    {
        private const int HISTORY_CHUNK_FRAMES = 50; // 约 2KB/包，避免 UDP 大包分片

        private readonly GameServer _server;

        public ReconnectHandler(GameServer server)
        {
            _server = server;
        }

        public void Register(MessageRouter router)
        {
            router.Register(MsgID.C2SReconnect, ReconnectReq.Parser, HandleReconnect);
            router.Register(MsgID.C2SReconnectReady, ReconnectReady.Parser, HandleReady);
        }

        private void HandleReconnect(IPEndPoint endpoint, ReconnectReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            if (!_server.Lockstep.TryBuildReconnectInfo(playerId, out var snap) || snap == null)
            {
                Console.WriteLine($"[Reconnect] [{playerId}] 没有可恢复的对局");
                return;
            }

            // 重新挂回房间：BroadcastToRoom 按房间成员发帧，不恢复成员关系就收不到实时帧
            var room = _server.Rooms.RejoinPlayingRoom(
                playerId, _server.Network.GetPlayerName(playerId), snap.RoomId);
            if (room == null)
            {
                Console.WriteLine($"[Reconnect] ⚠️ 玩家 [{playerId}] 无法回到房间 [{snap.RoomId}]");
                return;
            }

            var info = new ReconnectInfoNotify
            {
                RoomId = snap.RoomId,
                LockstepSeed = snap.Seed,
                TickRate = LockstepConfig.TICK_RATE,
                StartTime = snap.StartMs,
                LatestFrame = snap.LatestFrame,
            };
            foreach (var s in snap.Seats)
                info.Seats.Add(new LockstepSeatInfo { PlayerId = s.Id, PlayerName = s.Name, Seat = s.Seat });

            _server.Network.Send(endpoint, MsgID.S2CReconnectInfo, info);
            Console.WriteLine($"[Reconnect] 🔌 [{playerId}] 申请重连 [{snap.RoomId}]，服务器已封到帧 {snap.LatestFrame}");
        }

        private void HandleReady(IPEndPoint endpoint, ReconnectReady req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            string? roomId = _server.Rooms.GetPlayerRoomId(playerId);
            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine($"[Reconnect] ⚠️ [{playerId}] 未准备就绪：找不到对局房间");
                return;
            }

            bool ok = _server.Lockstep.SendReconnectHistory(roomId!, playerId,
                history => SendHistory(endpoint, history));

            if (ok)
                // 其他玩家的计分板在掉线时被隐藏了，广播一次“回来了”把视图恢复
                _server.NotifyPlayerJoin(roomId!, playerId, _server.Network.GetPlayerName(playerId));

            Console.WriteLine(ok
                ? $"[Reconnect] ✅ [{playerId}] 历史帧发送完成，恢复实时收发"
                : $"[Reconnect] ⚠️ [{playerId}] 历史帧发送失败（中继已回收？）");
        }

        private void SendHistory(IPEndPoint endpoint, IReadOnlyList<LockstepFrameInputs> history)
        {
            if (history.Count == 0)
            {
                _server.Network.Send(endpoint, MsgID.S2CReconnectFrames, new ReconnectFramesNotify { Done = true });
                return;
            }

            for (int start = 0; start < history.Count; start += HISTORY_CHUNK_FRAMES)
            {
                int end = Math.Min(start + HISTORY_CHUNK_FRAMES, history.Count);
                var msg = new ReconnectFramesNotify { Done = end >= history.Count };
                for (int i = start; i < end; i++)
                    msg.Frames.Add(history[i]);
                _server.Network.Send(endpoint, MsgID.S2CReconnectFrames, msg);
            }
        }
    }
}
