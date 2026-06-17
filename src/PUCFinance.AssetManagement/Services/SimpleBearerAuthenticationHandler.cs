using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PUCFinance.AssetManagement.Data;

namespace PUCFinance.AssetManagement.Services;

public class SimpleBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SimpleBearer";

    private readonly AppDbContext _db;
    private readonly AuthTokenService _tokens;

    public SimpleBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext db,
        AuthTokenService tokens)
        : base(options, logger, encoder)
    {
        _db = db;
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return AuthenticateResult.NoResult();

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization[prefix.Length..].Trim();
        var validatedToken = _tokens.ValidateToken(token);
        if (validatedToken == null)
            return AuthenticateResult.Fail("Invalid or expired token.");

        var user = await _db.Users
            .Include(u => u.Team)
            .FirstOrDefaultAsync(u => u.Id == validatedToken.UserId && u.IsActive == 1);

        if (user == null)
            return AuthenticateResult.Fail("User not found or inactive.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role)
        };

        if (user.TeamId.HasValue)
            claims.Add(new Claim("team_id", user.TeamId.Value.ToString()));

        if (!string.IsNullOrWhiteSpace(user.Team?.Name))
            claims.Add(new Claim("team_name", user.Team.Name));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
