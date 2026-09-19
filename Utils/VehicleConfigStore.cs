using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Server_MyDemo.Utils
{
    /// <summary>
    /// 载具配置（与 Unity 客户端的 PlayerProfileSO 同源：
    /// 由编辑器菜单 Tools/导出载具配置到服务端 生成 Data/vehicles.json）
    /// </summary>
    public class VehicleConfig
    {
        public string Profile { get; set; } = "";
        public int MaxHp { get; set; } = 100;
        public float WalkSpeed { get; set; } = 5f;
        public float SprintSpeed { get; set; } = 8f;
        public int WeaponDamage { get; set; } = 10;
        public float WeaponFireRate { get; set; } = 8f;
        public int WeaponMagazineSize { get; set; } = 30;
        public float WeaponReloadTime { get; set; } = 1.5f;
        public float WeaponMaxRange { get; set; } = 50f;
    }

    /// <summary>
    /// 载具配置仓库：启动后首次访问时加载 Data/vehicles.json（懒加载 + 缓存）
    /// 按 profile 名（不区分大小写）查询；文件缺失/解析失败时回退默认值（100 血）
    /// </summary>
    public static class VehicleConfigStore
    {
        private const string ConfigPath = "Data/vehicles.json";

        private static Dictionary<string, VehicleConfig>? _vehicles;
        private static readonly object _lock = new();

        /// <summary>按 profile 名查询载具配置；未找到返回 null</summary>
        public static VehicleConfig? Get(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return null;

            EnsureLoaded();
            lock (_lock)
            {
                foreach (var v in _vehicles!.Values)
                {
                    if (string.Equals(v.Profile, profile, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }
            }
            return null;
        }

        /// <summary>按 profile 名查询血量上限；未知载具返回 100</summary>
        public static int GetMaxHp(string profile)
        {
            return Get(profile)?.MaxHp ?? 100;
        }

        private static void EnsureLoaded()
        {
            if (_vehicles != null) return;

            lock (_lock)
            {
                if (_vehicles != null) return;

                _vehicles = new Dictionary<string, VehicleConfig>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(ConfigPath))
                    {
                        Console.WriteLine($"[VehicleConfig] ⚠️ 配置文件不存在: {ConfigPath}，使用默认血量 100");
                        return;
                    }

                    var json = File.ReadAllText(ConfigPath);
                    var list = JsonSerializer.Deserialize<VehicleList>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (list?.Vehicles == null) return;

                    foreach (var entry in list.Vehicles)
                    {
                        if (string.IsNullOrEmpty(entry.Profile)) continue;

                        _vehicles[entry.Profile] = new VehicleConfig
                        {
                            Profile = entry.Profile,
                            MaxHp = entry.MaxHp,
                            WalkSpeed = entry.WalkSpeed,
                            SprintSpeed = entry.SprintSpeed,
                            WeaponDamage = entry.WeaponDamage,
                            WeaponFireRate = entry.WeaponFireRate,
                            WeaponMagazineSize = entry.WeaponMagazineSize,
                            WeaponReloadTime = entry.WeaponReloadTime,
                            WeaponMaxRange = entry.WeaponMaxRange
                        };
                    }

                    Console.WriteLine($"[VehicleConfig] ✅ 已加载 {list.Vehicles.Count} 个载具配置 ({ConfigPath})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VehicleConfig] ❌ 加载配置失败: {ex.Message}（使用默认血量 100）");
                }
            }
        }

        private class VehicleList
        {
            public List<VehicleEntry>? Vehicles { get; set; }
        }

        private class VehicleEntry
        {
            public string Profile { get; set; } = "";
            public int MaxHp { get; set; } = 100;
            public float WalkSpeed { get; set; } = 5f;
            public float SprintSpeed { get; set; } = 8f;
            public int WeaponDamage { get; set; } = 10;
            public float WeaponFireRate { get; set; } = 8f;
            public int WeaponMagazineSize { get; set; } = 30;
            public float WeaponReloadTime { get; set; } = 1.5f;
            public float WeaponMaxRange { get; set; } = 50f;
        }
    }
}
