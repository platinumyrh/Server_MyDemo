using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// 🔧 修复 CS0103: 添加缺失的命名空间
using System.Text.Encodings.Web;  // JavaScriptEncoder 所在命名空间

namespace Server_MyDemo.Server
{
    /// <summary>
    /// 用户认证服务
    /// 负责用户登录验证、数据持久化（JSON文件存储）
    /// </summary>
    public class AuthService
    {
        #region 数据模型

        public class UserData
        {
            [JsonPropertyName("users")]
            public List<UserInfo> Users { get; set; } = new List<UserInfo>();

            [JsonPropertyName("version")]
            public string Version { get; set; } = "1.0";
        }

        public class UserInfo
        {
            [JsonPropertyName("username")]
            public string Username { get; set; } = "";

            [JsonPropertyName("password")]
            public string Password { get; set; } = "";           // 明文密码（运行时加载后会被哈希）

            [JsonPropertyName("user_id")]
            public string UserId { get; set; } = "";

            [JsonPropertyName("nickname")]
            public string Nickname { get; set; } = "";

            [JsonPropertyName("create_time")]
            public string CreateTime { get; set; } = "";

            [JsonPropertyName("last_login")]
            public string LastLogin { get; set; } = string.Empty;

            // 运行时字段（不序列化到JSON）
            [JsonIgnore]
            public string PasswordHash { get; set; } = "";       // MD5哈希值
        }

        #endregion

        #region 私有字段

        private readonly string _dataFilePath;
        private UserData _userData = new UserData();   // 永不为 null；Load() 失败时保持空库
        private readonly object _lock = new object();

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化认证服务
        /// </summary>
        /// <param name="dataFilePath">用户数据文件路径（默认: Data/users.json）</param>
        public AuthService(string? dataFilePath = null)
        {
            // 🔧 修复：如果未指定路径，自动获取相对于程序集位置的绝对路径
            if (string.IsNullOrEmpty(dataFilePath))
            {
                // 获取当前程序集所在目录（即 .exe 或 dotnet run 的目录）
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                _dataFilePath = Path.Combine(baseDirectory, "Data", "users.json");
                
                Console.WriteLine($"[AuthService] 数据文件路径: {_dataFilePath}");
            }
            else
            {
                // 如果是相对路径，转换为绝对路径
                if (!Path.IsPathRooted(dataFilePath))
                {
                    string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    _dataFilePath = Path.Combine(baseDirectory, dataFilePath);
                }
                else
                {
                    _dataFilePath = dataFilePath;
                }
            }
            
            Load();
        }

        #endregion

        #region 公开方法 - 数据管理

        /// <summary>
        /// 从JSON文件加载数据
        /// </summary>
        public void Load()
        {
            try
            {
                Console.WriteLine($"[AuthService] 🔍 尝试加载文件: {_dataFilePath}");
                Console.WriteLine($"[AuthService] 🔍 文件存在: {File.Exists(_dataFilePath)}");
                
                if (File.Exists(_dataFilePath))
                {
                    string json = File.ReadAllText(_dataFilePath, Encoding.UTF8);
                    Console.WriteLine($"[AuthService] 📄 JSON内容长度: {json.Length} 字符");
                    Console.WriteLine($"[AuthService] 📄 JSON前100字符: {json.Substring(0, Math.Min(100, json.Length))}...");
                    
                    var loaded = JsonSerializer.Deserialize<UserData>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded == null || loaded.Users == null || loaded.Users.Count == 0)
                    {
                        Console.WriteLine("[AuthService] ⚠️ 用户数据为空或Users集合为空，创建新的数据库");
                        _userData = new UserData();
                        Save();
                        return;
                    }

                    _userData = loaded;

                    // 对所有用户的密码进行MD5哈希（安全处理）
                    foreach (var user in _userData.Users)
                    {
                        if (user != null)
                        {
                            // 🔑 关键：如果 Password 已经是 32位十六进制（MD5格式），直接使用
                            // 否则认为是明文，计算MD5哈希
                            if (user.Password.Length == 32 && IsValidMD5(user.Password))
                            {
                                user.PasswordHash = user.Password;  // 已经是哈希值
                                Console.WriteLine($"[AuthService] 👤 用户 {user.Username}: 密码已是MD5哈希");
                            }
                            else
                            {
                                user.PasswordHash = ComputeMD5(user.Password);  // 明文转MD5
                                user.Password = user.PasswordHash;  // 🔑 存储哈希值回JSON，下次直接识别
                                Console.WriteLine($"[AuthService] 👤 用户 {user.Username}: 明文密码已转MD5哈希");
                            }
                        }
                    }

                    Console.WriteLine($"[AuthService] ✅ 成功加载 {_userData.Users.Count} 个用户");
                }
                else
                {
                    Console.WriteLine($"[AuthService] ❌ 文件不存在: {_dataFilePath}");
                    _userData = new UserData();
                    Save();
                    Console.WriteLine("[AuthService] 🆕 创建新的用户数据库");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AuthService] ❌ 加载用户数据失败: {ex.Message}");
                _userData = new UserData();  // 🔧 确保不会为 null
            }
        }

        /// <summary>
        /// 保存数据到JSON文件
        /// 🔑 关键：保存时恢复明文密码，否则下次加载会丢失密码
        /// </summary>
        public void Save()
        {
            try
            {
                lock (_lock)
                {
                    // 确保目录存在
                    string? dir = Path.GetDirectoryName(_dataFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // 🔑 关键：保存前恢复明文密码到 Password 字段
                    // 否则下次加载时 Password 为空，无法计算正确的 PasswordHash
                    foreach (var user in _userData.Users)
                    {
                        if (user != null && string.IsNullOrEmpty(user.Password) && !string.IsNullOrEmpty(user.PasswordHash))
                        {
                            // 如果 Password 为空但有 PasswordHash，说明是运行时状态
                            // 这里需要保留原始密码，但我们已经丢失了明文
                            // 解决方案：不要把 Password 清空，或者存储密码哈希而不是明文
                            // 
                            // 临时解决方案：存储 PasswordHash 到 Password 字段（作为持久化）
                            user.Password = user.PasswordHash;
                        }
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    string json = JsonSerializer.Serialize(_userData, options);
                    File.WriteAllText(_dataFilePath, json, Encoding.UTF8);

                    Console.WriteLine("[AuthService] 💾 用户数据已保存");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AuthService] ❌ 保存用户数据失败: {ex.Message}");
            }
        }

        #endregion

        #region 公开方法 - 认证功能

        /// <summary>
        /// 🔑 核心方法：验证用户登录
        /// </summary>
        /// <param name="username">用户名</param>
        /// <param name="password">密码（明文）</param>
        /// <returns>元组：(是否成功, 用户信息, 错误消息)</returns>
        public (bool Success, UserInfo? User, string Message) ValidateLogin(string username, string password)
        {
            lock (_lock)
            {
                // 1️⃣ 查找用户
                UserInfo? user = _userData.Users.FirstOrDefault(u => 
                    u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                
                if (user == null)
                {
                    Console.WriteLine($"[AuthService] ❌ 用户不存在: {username}");
                    return (false, null, "用户不存在");
                }

                // 2️⃣ 验证密码（调试日志）
                string inputHash = ComputeMD5(password);
                Console.WriteLine($"[AuthService] 🔍 验证用户: {username}");
                Console.WriteLine($"[AuthService] 🔍 输入密码: {password}");
                Console.WriteLine($"[AuthService] 🔍 输入Hash: {inputHash}");
                Console.WriteLine($"[AuthService] 🔍 存储Hash: {user.PasswordHash}");
                Console.WriteLine($"[AuthService] 🔍 Hash匹配: {string.Equals(user.PasswordHash, inputHash, StringComparison.OrdinalIgnoreCase)}");

                if (!string.Equals(user.PasswordHash, inputHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, "密码错误");
                }

                // 3️⃣ 更新最后登录时间
                user.LastLogin = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                // 异步保存（避免阻塞主线程）
                System.Threading.ThreadPool.QueueUserWorkItem(_ => Save());

                Console.WriteLine($"[AuthService] 玩家 [{username}] 登录成功，ID: {user.UserId}");

                return (true, user, "登录成功");
            }
        }

        /// <summary>
        /// 根据UserId获取用户信息
        /// </summary>
        /// <param name="userId">用户唯一ID</param>
        /// <returns>用户信息对象，如果不存在返回null</returns>
        public UserInfo? GetUserById(string userId)
        {
            lock (_lock)
            {
                return _userData.Users.FirstOrDefault(u => u.UserId == userId);
            }
        }

        /// <summary>
        /// 获取所有已加载的用户数量
        /// </summary>
        public int GetUserCount()
        {
            lock (_lock)
            {
                return _userData.Users.Count;
            }
        }

        /// <summary>
        /// 检查用户名是否已存在
        /// </summary>
        /// <param name="username">要检查的用户名</param>
        /// <returns>如果存在返回true</returns>
        public bool IsUsernameExists(string username)
        {
            lock (_lock)
            {
                return _userData.Users.Any(u => 
                    u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            }
        }

        #endregion

        #region 私有方法 - 工具函数

        /// <summary>
        /// 检查字符串是否是有效的MD5哈希格式（32位十六进制）
        /// </summary>
        /// <param name="input">输入字符串</param>
        /// <returns>是否是MD5格式</returns>
        private static bool IsValidMD5(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length != 32)
                return false;

            foreach (char c in input)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 计算字符串的MD5哈希值（32位小写十六进制）
        /// </summary>
        /// <param name="input">输入字符串</param>
        /// <returns>MD5哈希值</returns>
        private static string ComputeMD5(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));  // 转为小写十六进制
                }

                return sb.ToString();
            }
        }

        #endregion
    }
}
