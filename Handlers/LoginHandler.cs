using System;
using System.Net;
using System.Security.Cryptography;
using GameNetwork;
using Server_MyDemo.Network;

namespace Server_MyDemo.Handlers
{
    /// <summary>登录认证消息处理</summary>
    public sealed class LoginHandler
    {
        private readonly GameServer _server;

        public LoginHandler(GameServer server)
        {
            _server = server;
        }

        public void Register(MessageRouter router)
        {
            router.Register(MsgID.C2SLogin, LoginReq.Parser, HandleLogin);
        }

        private void HandleLogin(IPEndPoint endpoint, LoginReq req)
        {
            Console.WriteLine($"[Login] 收到登录请求: {req.Username}");

            var result = _server.Auth.ValidateLogin(req.Username, req.Password);
            if (!result.Success || result.User == null)
            {
                _server.Network.Send(endpoint, MsgID.S2CLogin, new LoginResp
                {
                    Success = false,
                    Message = result.Message ?? "登录失败"
                });
                Console.WriteLine($"[Login] ❌ [{req.Username}] 登录失败: {result.Message}");
                return;
            }

            var user = result.User;

            // 同账号已在别处在线：顶掉旧会话。
            // Unity 停止播放时客户端来不及发登出，旧会话会一直占着在线表导致再也登不上
            if (_server.Network.IsPlayerOnline(user.UserId))
            {
                Console.WriteLine($"[Login] ⚠️ [{req.Username}] 已在别处在线，踢掉旧会话");
                _server.DisconnectPlayer(user.UserId, "被新登录顶号");
            }

            _server.Network.AddPlayer(user.UserId, user.Nickname, endpoint);

            _server.Network.Send(endpoint, MsgID.S2CLogin, new LoginResp
            {
                Success = true,
                UserId = user.UserId,
                Username = user.Nickname,
                Token = GenerateSessionToken(),
                ExpireTime = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeMilliseconds(),
                Message = "登录成功"
            });

            Console.WriteLine($"[Login] ✅ [{req.Username}] 登录成功，ID: {user.UserId}");
        }

        private static string GenerateSessionToken()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }
    }
}
