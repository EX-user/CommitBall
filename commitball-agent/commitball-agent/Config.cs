using System;
using System.IO;
using System.Text.Json;

namespace CommitBallAgent
{
    static class Config
    {
        public static string ApiKey { get; set; } = "";
        public static string BaseUrl { get; set; } = "";
        public static string Model { get; set; } = "";
        public static bool IsConfigured { get; set; }

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string DataDir = Path.Combine(BaseDir, "data");
        public static readonly string MemoryDir = Path.Combine(DataDir, "agent-memory");

        private static readonly string ConfigPath = Path.Combine(DataDir, "agent-config.json");

        public static void Load()
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("api_key", out var ak)) ApiKey = ak.GetString() ?? "";
                if (root.TryGetProperty("base_url", out var bu)) BaseUrl = bu.GetString() ?? "";
                if (root.TryGetProperty("model", out var m)) Model = m.GetString() ?? "";
                IsConfigured = !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(Model);
            }
            catch { }
        }

        public static void Save(string baseUrl, string model, string apiKey)
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(new { base_url = baseUrl, model, api_key = apiKey }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            BaseUrl = baseUrl;
            Model = model;
            ApiKey = apiKey;
            IsConfigured = true;
        }
    }
}
