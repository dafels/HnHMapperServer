using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Repositories;

public class TenantRepository : ITenantRepository
{
    private readonly ApplicationDbContext _context;

    public TenantRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<TenantEntity?> GetByIdAsync(string id)
    {
        return await _context.Tenants
            .IgnoreQueryFilters() // Bypass tenant filter for tenant management
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<List<TenantEntity>> GetAllAsync()
    {
        return await _context.Tenants
            .IgnoreQueryFilters() // Bypass tenant filter to see all tenants
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<TenantEntity> CreateAsync(TenantEntity tenant)
    {
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();
        return tenant;
    }

    public async Task UpdateAsync(TenantEntity tenant)
    {
        if (_context.Entry(tenant).State == EntityState.Detached)
        {
            _context.Tenants.Update(tenant);
        }

        // Counters owned by atomic UPDATEs elsewhere (StorageQuotaService, TenantActivityService):
        // never write back the values read when this entity was loaded — Update() marked every
        // column modified, so a superadmin rename overwrote the uploads' storage deltas and the
        // activity flush made since the load.
        var entry = _context.Entry(tenant);
        entry.Property(t => t.CurrentStorageMB).IsModified = false;
        entry.Property(t => t.LastActivityAt).IsModified = false;

        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var tenant = await GetByIdAsync(id);
        if (tenant != null)
        {
            _context.Tenants.Remove(tenant);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<bool> ExistsAsync(string id)
    {
        return await _context.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == id);
    }
}
