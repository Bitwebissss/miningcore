using Miningcore.Api.Responses;

namespace Miningcore.Api.Requests;

public class UpdateMinerSettingsRequest
{
    public string Password { get; set; }
    public MinerSettings Settings { get; set; }
}
