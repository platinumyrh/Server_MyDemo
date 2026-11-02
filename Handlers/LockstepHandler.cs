using System.Net;
using GameNetwork;
using Server_MyDemo.Network;

namespace Server_MyDemo.Handlers
{
    /// <summary>
    /// lockstep 指令处理：服务器不解读指令内容，
    /// 交由中继按房间收集、封帧后广播（位置/血量/伤害由各端确定性模拟）
    /// </summary>
    public sealed class LockstepHandler
    {
        private readonly GameServer _server;

        public LockstepHandler(GameServer server)
        {
            _server = server;
        }

        public void Register(MessageRouter router)
        {
            router.Register(MsgID.C2SLockstepCommand, LockstepCommandReq.Parser, HandleCommand);
        }

        private void HandleCommand(IPEndPoint endpoint, LockstepCommandReq req)
        {
            string playerId = _server.PlayerIdOf(endpoint);
            string? roomId = _server.Rooms.GetPlayerRoomId(playerId);
            if (string.IsNullOrEmpty(roomId))
            {
                return; // 不在房间：静默丢弃（可能是离开瞬间在途的包）
            }

            _server.Lockstep.OnPlayerCommand(roomId!, playerId, req);
        }
    }
}
