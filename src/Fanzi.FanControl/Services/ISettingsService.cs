using Fanzi.FanControl.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
