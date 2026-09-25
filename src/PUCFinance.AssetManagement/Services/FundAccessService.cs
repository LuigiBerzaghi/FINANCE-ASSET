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

        if (!_currentUser.UserId.HasValue)
            return query.Where(f => false);

        var userId = _currentUser.UserId.Value;
        return query.Where(f => _db.TeamMembers.Any(m => m.UserId == userId && m.TeamId == f.TeamId));
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
