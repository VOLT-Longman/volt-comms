using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoltComms.Core;

/// <summary>
/// exe 옆 config.json 으로 외부화되는 설정.
/// 파일이 없으면 기본값으로 새로 만들어 준다.
/// </summary>
public class AppConfig
{
    [JsonPropertyName("server_host")] public string ServerHost { get; set; } = "127.0.0.1";
    [JsonPropertyName("server_port")] public int ServerPort { get; set; } = 8443;

    /// <summary>서버가 TLS(wss)로 떠 있으면 true. 평문 테스트면 false.</summary>
    [JsonPropertyName("use_tls")] public bool UseTls { get; set; } = false;

    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";

    /// <summary>
    /// PTT 키. 키보드: "F13", "Scroll", "Pause" 등 (.NET Keys 이름).
    /// 마우스 사이드 버튼: "XButton1"(뒤로) / "XButton2"(앞으로), 별칭 "Mouse4"/"Mouse5".
    /// </summary>
    [JsonPropertyName("ptt_key")] public string PttKey { get; set; } = "XButton2";

    /// <summary>마이크 장치 이름 일부 (빈 문자열 = 기본 장치).</summary>
    [JsonPropertyName("input_device")] public string InputDevice { get; set; } = "";

    /// <summary>스피커/헤드셋 장치 이름 일부 (빈 문자열 = 기본 장치).</summary>
    [JsonPropertyName("output_device")] public string OutputDevice { get; set; } = "";

    /// <summary>발언권 획득 시 짧은 확인음 재생 여부.</summary>
    [JsonPropertyName("grant_beep")] public bool GrantBeep { get; set; } = true;

    [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; set; } = true;

    /// <summary>수신 음량 (0~100).</summary>
    [JsonPropertyName("output_volume")] public int OutputVolume { get; set; } = 100;

    /// <summary>실행 시 창을 띄우지 않고 트레이로 시작.</summary>
    [JsonPropertyName("start_minimized")] public bool StartMinimized { get; set; } = false;

    public static string BaseDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(BaseDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>설정을 읽는다. 파일이 없으면 기본값을 저장해 두고 created=true 를 돌려준다.</summary>
    public static AppConfig LoadOrCreate(out bool created)
    {
        created = false;
        if (File.Exists(ConfigPath))
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
            if (cfg != null) return cfg;
        }
        var def = new AppConfig();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(def, JsonOpts));
        created = true;
        return def;
    }

    public void Save() =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>실사용 가능한 설정인지 (토큰/닉네임을 채웠는지) 검사한다.</summary>
    public bool LooksConfigured =>
        !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(Nickname);
}
