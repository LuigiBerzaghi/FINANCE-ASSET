using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;
using PUCFinance.AssetManagement.Models.DTOs;
using PUCFinance.AssetManagement.Services;

namespace PUCFinance.AssetManagement.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordService _passwords;
    private readonly AuthTokenService _tokens;
    private readonly CurrentUserService _currentUser;

    public AuthController(
        AppDbContext db,
        PasswordService passwords,
        AuthTokenService tokens,
        CurrentUserService currentUser)
    {
        _db = db;
        _passwords = passwords;
        _tokens = tokens;
        _currentUser = currentUser;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users
            .Include(u => u.Team)
            .FirstOrDefaultAsync(u => u.Email == email && u.IsActive == 1);

        if (user == null || !_passwords.VerifyPassword(request.Password, user.PasswordHash))
            return Unauthorized(new { error = "Email ou senha invalidos" });

        var token = _tokens.IssueToken(user);
        return Ok(new AuthResponse(
            Token: token.Token,
            ExpiresAt: token.ExpiresAt.UtcDateTime.ToString("O"),
            User: ToUserResponse(user)));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<AuthUserResponse>> Me()
    {
        if (!_currentUser.UserId.HasValue)
            return Unauthorized();

        var user = await _db.Users
            .Include(u => u.Team)
            .FirstOrDefaultAsync(u => u.Id == _currentUser.UserId.Value && u.IsActive == 1);

        if (user == null)
            return Unauthorized();

        return Ok(ToUserResponse(user));
    }

    private static AuthUserResponse ToUserResponse(AppUser user)
    {
        return new AuthUserResponse(
            Id: user.Id,
            Name: user.Name,
            Email: user.Email,
            Role: user.Role,
            TeamId: user.TeamId,
            TeamName: user.Team?.Name);
    }
}

[ApiController]
[Authorize(Roles = AppRoles.Leader)]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TeamsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<TeamResponse>>> GetAll()
    {
        var teams = await _db.Teams
            .OrderBy(t => t.Name)
            .Select(t => new TeamResponse(t.Id, t.Name))
            .ToListAsync();

        return Ok(teams);
    }
}
