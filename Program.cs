using Server_MyDemo.Server;
using System;
using GameNetwork;

namespace Server_MyDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "My Demo Game Server";
            Console.WriteLine("========================================");
            Console.WriteLine("       My Demo Game Server v1.0");
            Console.WriteLine("       UDP + Frame Sync + ProtoBuf");
            Console.WriteLine("========================================");
            Console.WriteLine();

            // 创建并启动游戏服务器
            GameServer server = new GameServer();
            server.Start();

            // 主循环（保持控制台运行）
            Console.WriteLine("\n服务器已启动！按 'Q' 退出...");
            Console.WriteLine("可用命令：");
            Console.WriteLine("  Q - 退出服务器");
            Console.WriteLine("  S - 显示服务器状态");
            Console.WriteLine();

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Q)
                    {
                        Console.WriteLine("\n正在关闭服务器...");
                        server.Stop();
                        break;
                    }
                    else if (key == ConsoleKey.S)
                    {
                        server.PrintStatus();
                    }
                }

                // 避免CPU占用过高
                Thread.Sleep(100);
            }

            Console.WriteLine("服务器已关闭。按任意键退出...");
            Console.ReadKey();
        }
    }
}
