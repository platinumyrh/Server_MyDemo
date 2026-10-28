using System;
using System.Collections.Generic;
using System.Net;
using GameNetwork;
using Google.Protobuf;

namespace Server_MyDemo.Network
{
    /// <summary>
    /// 消息路由：MsgID → 处理函数。
    /// 与客户端 NetworkHandler 同思路——泛型注册时绑定 parser，业务 Handler 只处理强类型消息。
    /// 解析与业务异常统一在这里兜底，避免每个 Handler 重复写 try/catch。
    /// </summary>
    public sealed class MessageRouter
    {
        private readonly Dictionary<MsgID, Action<IPEndPoint, byte[]>> _handlers = new();

        public void Register<T>(MsgID msgId, MessageParser<T> parser, Action<IPEndPoint, T> handler)
            where T : IMessage<T>
        {
            _handlers[msgId] = (endpoint, data) =>
            {
                T message;
                try
                {
                    message = parser.ParseFrom(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Router] ❌ 解析 {typeof(T).Name} 失败: {ex.Message}");
                    return;
                }

                try
                {
                    handler(endpoint, message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Router] ❌ 处理 {typeof(T).Name} 异常: {ex.Message}");
                }
            };
        }

        public void Dispatch(IPEndPoint endpoint, MsgID msgId, byte[] data)
        {
            if (_handlers.TryGetValue(msgId, out var handler))
            {
                handler(endpoint, data);
            }
            else
            {
                Console.WriteLine($"[Router] ⚠️ 未处理的消息类型: {msgId}");
            }
        }
    }
}
