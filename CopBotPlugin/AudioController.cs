using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace CopBotPlugin;

[ApiController]
public partial class AudioController : ControllerBase
{
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SafeName();

    // The voice lines the HUD plays
    [HttpGet("/copbot/audio/{name}.mp3")]
    public IActionResult Get(string name)
    {
        if (!SafeName().IsMatch(name)) return NotFound();

        var path = Path.GetFullPath(Path.Join("plugins", "CopBotPlugin", "audio", name + ".mp3"));
        return System.IO.File.Exists(path) ? PhysicalFile(path, "audio/mpeg") : NotFound();
    }
}
