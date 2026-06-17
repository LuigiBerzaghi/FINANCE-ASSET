using System.Security.Claims;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public class CurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public int? UserId => GetIntClaim(ClaimTypes.NameIdentifier);
    public int? TeamId => GetIntClaim("team_id");
    public string? Name => User?.FindFirstValue(ClaimTypes.Name);
    public string? Email => User?.FindFirstValue(ClaimTypes.Email);
    public string? Role => User?.FindFirstValue(ClaimTypes.Role);
    public string? TeamName => User?.FindFirstValue("team_name");
    public bool IsLeader => string.Equals(Role, AppRoles.Leader, StringComparison.OrdinalIgnoreCase);

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    private int? GetIntClaim(string claimType)
    {
        var value = User?.FindFirstValue(claimType);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
