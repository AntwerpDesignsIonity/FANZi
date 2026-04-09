using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

public sealed class AppSettings
{
    public List<FanProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }
}
