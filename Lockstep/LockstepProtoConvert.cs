using System.Collections.Generic;
using GameNetwork;

namespace GameLockstep
{
    /// <summary>
    /// protobuf(GameNetwork) ↔ 逻辑层(GameLockstep) 的纯转换。
    /// 客户端/服务端共用（两端 GeneratedCodes/Messages.cs 同源生成），
    /// 不引用 UnityEngine——整个 Lockstep 目录保持可整体拷贝到服务端
    /// </summary>
    public static class LockstepProtoConvert
    {
        /// <summary>待发指令批量打包（LockstepClient.DrainPendingSends → C2S_LockstepCommand）</summary>
        public static LockstepCommandReq ToCommandReq(List<KeyValuePair<long, PlayerCommand>> commands)
        {
            var req = new LockstepCommandReq();
            foreach (var kv in commands)
            {
                var d = new LockstepCommandData { Frame = kv.Key };
                d.MoveX = kv.Value.MoveX;
                d.MoveZ = kv.Value.MoveZ;
                d.AimAngle = kv.Value.AimAngle;
                d.Buttons = kv.Value.Buttons;
                req.Commands.Add(d);
            }
            return req;
        }

        /// <summary>C2S 解包（服务端中继与测试回声用）</summary>
        public static PlayerCommand ToCommand(LockstepCommandData d)
        {
            return new PlayerCommand
            {
                MoveX = d.MoveX,
                MoveZ = d.MoveZ,
                AimAngle = d.AimAngle,
                Buttons = (byte)d.Buttons,
            };
        }

        /// <summary>封帧条目 → 逻辑指令</summary>
        public static PlayerCommand ToCommand(LockstepPlayerCommand c)
        {
            return new PlayerCommand
            {
                MoveX = c.MoveX,
                MoveZ = c.MoveZ,
                AimAngle = c.AimAngle,
                Buttons = (byte)c.Buttons,
            };
        }

        /// <summary>封帧帧 → 座位→指令表（重复座位后者覆盖，语义由 LockstepClient 决定）</summary>
        public static Dictionary<int, PlayerCommand> ToInputs(LockstepFrameInputs frame)
        {
            var inputs = new Dictionary<int, PlayerCommand>(frame.Commands.Count);
            foreach (var c in frame.Commands)
                inputs[c.Seat] = ToCommand(c);
            return inputs;
        }

        /// <summary>服务端封包（阶段6 中继用）：帧号→(座位→指令) → S2C_LockstepInputs。
        /// 包内条目顺序不影响语义（接收端按键重建），无需保证字典遍历序</summary>
        public static LockstepInputsNotify ToInputsNotify(Dictionary<long, Dictionary<int, PlayerCommand>> frames)
        {
            var notify = new LockstepInputsNotify();
            foreach (var f in frames)
            {
                var fi = new LockstepFrameInputs { Frame = f.Key };
                foreach (var kv in f.Value)
                {
                    fi.Commands.Add(new LockstepPlayerCommand
                    {
                        Seat = kv.Key,
                        MoveX = kv.Value.MoveX,
                        MoveZ = kv.Value.MoveZ,
                        AimAngle = kv.Value.AimAngle,
                        Buttons = kv.Value.Buttons,
                    });
                }
                notify.Frames.Add(fi);
            }
            return notify;
        }
    }
}
