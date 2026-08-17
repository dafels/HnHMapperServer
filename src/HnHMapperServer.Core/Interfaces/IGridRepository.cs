using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Core.Interfaces;

public interface IGridRepository
{
    Task<GridData?> GetGridAsync(string gridId);
    Task SaveGridAsync(GridData gridData);
    Task DeleteGridAsync(string gridId);
    Task<List<GridData>> GetAllGridsAsync();
    Task<List<GridData>> GetGridsByMapAsync(int mapId);

    /// <summary>All grids of a map whose coordinates fall inside the inclusive bounding box.</summary>
    Task<List<GridData>> GetGridsByMapInAreaAsync(int mapId, int minX, int minY, int maxX, int maxY);
    Task DeleteGridsByMapAsync(int mapId);
    Task<bool> AnyGridsExistAsync();

    // Batch operations for optimized import
    Task<HashSet<string>> GetExistingGridIdsAsync(IEnumerable<string> gridIds);
    Task SaveGridsBatchAsync(IEnumerable<GridData> grids, bool skipExistenceCheck = false);
}
