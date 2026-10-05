using System.Text.Json;
using UDRoute.Maui.Models;

namespace UDRoute.Maui.Services;

public class SceneTransferDto
{
    public string Name { get; set; } = "";
    public AppConfig Config { get; set; } = new();
}

public static class SceneTransferService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static string Export(Scene scene)
    {
        var dto = new SceneTransferDto
        {
            Name = scene.Name,
            Config = scene.Config
        };

        return JsonSerializer.Serialize(dto, _jsonOptions);
    }

    public static Scene? Import(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        try
        {
            string json = rawText.Trim();
            if (json.StartsWith("udroute://", StringComparison.OrdinalIgnoreCase))
            {
                // 支持 udroute:// 协议头格式
                var uri = new Uri(json);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                json = query["data"] ?? json;
            }

            var dto = JsonSerializer.Deserialize<SceneTransferDto>(json, _jsonOptions);
            if (dto == null) return null;

            return new Scene
            {
                Name = dto.Name,
                Config = dto.Config,
                IsSelected = false
            };
        }
        catch
        {
            return null;
        }
    }
}
