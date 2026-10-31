using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GameNetwork;

namespace Server_MyDemo.Services
{
    /// <summary>
    /// 对局录像：把服务端封好的权威帧按行追加到共享目录。
    /// 纯文本、一行一帧：服务端中途退出最多丢最后半行，客户端解析时跳过即可。
    /// 目录两端约定一致（LocalApplicationData/LockstepDemo/Replays），演示环境客户端与服务端同机。
    /// </summary>
    public sealed class ReplayRecorder : IDisposable
    {
        private readonly StreamWriter _writer;

        public string FilePath { get; }

        public ReplayRecorder(string roomId, ulong seed, int tickRate, IReadOnlyList<(string Id, string Name)> players)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LockstepDemo", "Replays");
            Directory.CreateDirectory(dir);

            FilePath = Path.Combine(dir, $"match_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Sanitize(roomId)}.rpl");
            _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };

            _writer.WriteLine("LOCKSTEP_REPLAY 1");
            _writer.WriteLine("seed " + seed.ToString(CultureInfo.InvariantCulture));
            _writer.WriteLine("tick " + tickRate.ToString(CultureInfo.InvariantCulture));
            _writer.WriteLine("seats " + players.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < players.Count; i++)
                _writer.WriteLine($"seat {i} {B64(players[i].Id)} {B64(players[i].Name)}");
        }

        public void Append(LockstepFrameInputs frame)
        {
            var sb = new StringBuilder();
            sb.Append('f').Append(' ').Append(frame.Frame.ToString(CultureInfo.InvariantCulture));
            foreach (var c in frame.Commands)
            {
                sb.Append(' ').Append(c.Seat.ToString(CultureInfo.InvariantCulture))
                  .Append(' ').Append(c.MoveX.ToString(CultureInfo.InvariantCulture))
                  .Append(' ').Append(c.MoveZ.ToString(CultureInfo.InvariantCulture))
                  .Append(' ').Append(c.AimAngle.ToString(CultureInfo.InvariantCulture))
                  .Append(' ').Append(c.Buttons.ToString(CultureInfo.InvariantCulture));
            }
            _writer.WriteLine(sb.ToString());
        }

        public void Dispose()
        {
            try
            {
                _writer.WriteLine("end");
                _writer.Dispose();
            }
            catch
            {
                // 收尾失败不影响服务端退出
            }
        }

        private static string B64(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));

        private static string Sanitize(string roomId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = roomId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }
    }
}
