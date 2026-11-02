using System;

namespace Server_MyDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "My Demo Game Server";
            Console.WriteLine("========================================");
            Console.WriteLine("       My Demo Game Server v1.0");
            Console.WriteLine("       UDP + Lockstep + ProtoBuf");
            Console.WriteLine("========================================");
            Console.WriteLine();

            GameServer server = new GameServer();
            server.Start();

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

                Thread.Sleep(100); // 避免CPU占用过高
            }

            Console.WriteLine("服务器已关闭。按任意键退出...");
            Console.ReadKey();
        }
    }
}
