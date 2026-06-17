using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public class FundAccessService
{
    private readonly AppDbContext _db;
    private readonly CurrentUserService _currentUser;

    public FundAccessService(AppDbContext db, CurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public IQueryable<Fund> VisibleFunds()
    {
        var query = _db.Funds.Where(f => f.IsActive == 1);
        if (_currentUser.IsLeader)
            return query;

        if (!_currentUser.TeamId.HasValue)
            return query.Where(f => false);

        var teamId = _currentUser.TeamId.Value;
        return query.Where(f => f.TeamId == teamId);
    }

    public Task<Fund?> FindVisibleFundAsync(int fundId)
    {
        return VisibleFunds()
            .Include(f => f.Team)
            .FirstOrDefaultAsync(f => f.Id == fundId);
    }

    public Task<bool> CanAccessFundAsync(int fundId)
    {
        return VisibleFunds().AnyAsync(f => f.Id == fundId);
    }
}
