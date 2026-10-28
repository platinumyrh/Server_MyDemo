using System;
using System.Net;
using GameNetwork;
using Server_MyDemo.Network;

namespace Server_MyDemo.Handlers
{
    /// <summary>心跳消息处理（响应里回显客户端时间戳，客户端据此算延迟）</summary>
    public sealed class HeartbeatHandler
    {
        private readonly GameServer _server;

        public HeartbeatHandler(GameServer server)
        {
            _server = server;
        }

        public void Register(MessageRouter router)
        {
            router.Register(MsgID.C2SHeartBeat, HeartBeatReq.Parser, HandleHeartBeat);
        }

        private void HandleHeartBeat(IPEndPoint endpoint, HeartBeatReq req)
        {
            _server.Network.Send(endpoint, MsgID.S2CHeartBeat, new HeartBeatResp
            {
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClientTime = req.Timestamp
            });
        }
    }
}
