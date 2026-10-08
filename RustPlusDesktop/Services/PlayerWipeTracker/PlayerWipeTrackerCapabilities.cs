using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RustPlusDesk.Services.Data;

namespace RustPlusDesk.Services.PlayerWipeTracker;

public sealed record PlayerWipeTrackerCapabilities(
    string PlanCode,
    bool IsTrackerAvailable,
    bool CanTrackTeam,
    bool CanUseCloudSync,
    bool CanUseAdvancedViews,
    bool CanUseRouteReplay,
    bool CanExport,
    int MaxTrackedPlayers,
    int RetainedWipes,
    int CloudRetentionDays,
    DateTime FetchedUtc)
{
    public static PlayerWipeTrackerCapabilities Free(DateTime? fetchedUtc = null) => new(
        "pro", true, true, false, true, true, true, 50, 100, 30, fetchedUtc ?? DateTime.UtcNow);

    public bool CanTrackPlayer(ulong steamId, ulong ownSteamId)
        => steamId != 0 && (steamId == ownSteamId || CanTrackTeam) &&
           (steamId == ownSteamId || MaxTrackedPlayers > 1);
}

/// <summary>Fail-closed interpreter and 72-hour offline cache for backend limits.</summary>
public sealed class PlayerWipeTrackerCapabilityService
{
    public const string CacheKey = "player_wipe_tracker_capabilities";
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromHours(72);
    private PlayerWipeTrackerCapabilities _lastSuccessful = PlayerWipeTrackerCapabilities.Free();

    public PlayerWipeTrackerCapabilityService()
    {
        _lastSuccessful = PlayerWipeTrackerCapabilities.Free();
    }

    public PlayerWipeTrackerCapabilities Current => PlayerWipeTrackerCapabilities.Free();

    public PlayerWipeTrackerCapabilities Update(JsonElement bootstrap)
    {
        return Current;
    }

    public void Reset()
    {
        _lastSuccessful = PlayerWipeTrackerCapabilities.Free();
    }

    public PlayerWipeTrackerCapabilities Effective(DateTime nowUtc)
    {
        return PlayerWipeTrackerCapabilities.Free(nowUtc);
    }

    private static bool TryEnabled(JsonElement limits, string key)
    {
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("player_wipe_tracker", out var feature) ||
            feature.ValueKind != JsonValueKind.Object || !feature.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False)
            return false;
        return enabled.GetBoolean();
    }

    private static int TryValue(JsonElement limits, string key, int fallback)
    {
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("player_wipe_tracker", out var feature) ||
            feature.ValueKind != JsonValueKind.Object || !feature.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("value", out var number) ||
            number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var result))
            return fallback;
        return result;
    }
}
