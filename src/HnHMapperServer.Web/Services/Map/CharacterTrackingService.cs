using HnHMapperServer.Web.Models;
using Microsoft.JSInterop;

namespace HnHMapperServer.Web.Services.Map;

/// <summary>
/// Service for managing character tracking state and follow mode
/// </summary>
public class CharacterTrackingService
{
    private readonly ILogger<CharacterTrackingService> _logger;

    /// <summary>
    /// List of all active characters across all maps
    /// </summary>
    private List<CharacterModel> _allCharacters = new();

    /// <summary>
    /// Whether follow mode is currently active
    /// </summary>
    private bool _isFollowing = false;

    /// <summary>
    /// ID of the character being followed (null if not following)
    /// </summary>
    private int? _followingCharacterId = null;

    /// <summary>
    /// Filter text for players list
    /// </summary>
    private string _playerFilter = "";

    /// <summary>
    /// Rate-limiting: last time we showed the "zero characters" hint
    /// </summary>
    private DateTime? _lastZeroCharactersHintTime = null;

    public CharacterTrackingService(ILogger<CharacterTrackingService> logger)
    {
        _logger = logger;
    }

    #region Public Properties

    /// <summary>
    /// All active characters
    /// </summary>
    public IReadOnlyList<CharacterModel> AllCharacters => _allCharacters.AsReadOnly();

    /// <summary>
    /// Whether follow mode is active
    /// </summary>
    public bool IsFollowing => _isFollowing;

    /// <summary>
    /// ID of the character being followed
    /// </summary>
    public int? FollowingCharacterId => _followingCharacterId;

    /// <summary>
    /// Player filter text
    /// </summary>
    public string PlayerFilter
    {
        get => _playerFilter;
        set => _playerFilter = value;
    }

    /// <summary>
    /// Filtered list of players based on current filter text
    /// </summary>
    public IEnumerable<CharacterModel> FilteredPlayers =>
        _allCharacters
            .Where(c => string.IsNullOrWhiteSpace(_playerFilter) ||
                       c.Name.Contains(_playerFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name);

    #endregion

    #region State Management

    /// <summary>
    /// Handle SSE characters snapshot (full state)
    /// </summary>
    public void HandleCharactersSnapshot(List<CharacterModel> characters)
    {
        _logger.LogDebug("CharactersSnapshot received with {Count} characters", characters.Count);

        if (characters.Count > 0)
        {
            _logger.LogDebug("First character: Id={Id}, Name={Name}, Map={Map}, Pos=({X},{Y}), Type={Type}",
                characters[0].Id, characters[0].Name, characters[0].Map,
                characters[0].Position.X, characters[0].Position.Y, characters[0].Type);
        }

        _allCharacters = characters;
    }

    /// <summary>
    /// Handle SSE character delta (incremental updates)
    /// </summary>
    public CharacterDeltaResult HandleCharacterDelta(CharacterDeltaModel delta)
    {
        var result = new CharacterDeltaResult();

        // Apply updates
        foreach (var character in delta.Updates)
        {
            var existingIndex = _allCharacters.FindIndex(c => c.Id == character.Id);
            if (existingIndex >= 0)
            {
                _allCharacters[existingIndex] = character;
            }
            else
            {
                _allCharacters.Add(character);
            }
            result.UpdatedCharacters.Add(character);
        }

        // Apply deletions
        foreach (var deletedId in delta.Deletions)
        {
            var toRemove = _allCharacters.FirstOrDefault(c => c.Id == deletedId);
            if (toRemove != null)
            {
                _allCharacters.Remove(toRemove);
                result.DeletedCharacterIds.Add(deletedId);

                // If we were following this character, stop following
                if (_followingCharacterId == deletedId)
                {
                    result.ShouldStopFollowing = true;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get character by ID
    /// </summary>
    public CharacterModel? GetCharacterById(int characterId)
    {
        return _allCharacters.FirstOrDefault(c => c.Id == characterId);
    }

    /// <summary>
    /// Get all characters for a specific map (returns snapshot to avoid collection modified exceptions)
    /// </summary>
    public IReadOnlyList<CharacterModel> GetCharactersForMap(int mapId)
    {
        return _allCharacters.Where(c => c.Map == mapId).ToList();
    }

    #endregion

    #region Follow Mode

    /// <summary>
    /// Start following a character
    /// </summary>
    public void StartFollowing(int characterId)
    {
        _isFollowing = true;
        _followingCharacterId = characterId;
        _logger.LogInformation("Started following character {CharacterId}", characterId);
    }

    /// <summary>
    /// Stop following the current character
    /// </summary>
    public void StopFollowing()
    {
        _isFollowing = false;
        _followingCharacterId = null;
        _logger.LogInformation("Stopped following character");
    }

    /// <summary>
    /// Get the currently followed character (if any)
    /// </summary>
    public CharacterModel? GetFollowedCharacter()
    {
        if (!_isFollowing || !_followingCharacterId.HasValue)
        {
            return null;
        }

        return GetCharacterById(_followingCharacterId.Value);
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Clear all characters
    /// </summary>
    public void Clear()
    {
        _allCharacters.Clear();
        StopFollowing();
    }

    /// <summary>
    /// Check if we should show the "zero characters" hint based on rate limiting
    /// </summary>
    public bool ShouldShowZeroCharactersHint()
    {
        var now = DateTime.UtcNow;
        if (!_lastZeroCharactersHintTime.HasValue ||
            (now - _lastZeroCharactersHintTime.Value).TotalMinutes >= 5)
        {
            _lastZeroCharactersHintTime = now;
            return true;
        }
        return false;
    }

    #endregion
}

/// <summary>
/// Result of applying a character delta
/// </summary>
public class CharacterDeltaResult
{
    public List<CharacterModel> UpdatedCharacters { get; } = new();
    public List<int> DeletedCharacterIds { get; } = new();
    public bool ShouldStopFollowing { get; set; }
}
