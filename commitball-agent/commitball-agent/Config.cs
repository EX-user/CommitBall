using System;
using System.IO;

namespace CommitBallAgent
{
    static class Config
    {
        public const string ApiKey = "sk-f7639376d1164f1cba09bbb2c320242f";
        public const string BaseUrl = "https://api.deepseek.com";
        public const string Model = "deepseek-chat";

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

        public static readonly string DataDir = Path.Combine(BaseDir, "data");
        public static readonly string MemoryDir = Path.Combine(DataDir, "agent-memory");
    }
}
