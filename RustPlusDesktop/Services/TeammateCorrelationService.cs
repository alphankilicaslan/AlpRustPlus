using System;
using System.Collections.Generic;
using System.Linq;

namespace RustPlusDesk.Services
{
    public sealed class TeammateMatch
    {
        public string TargetBMId { get; set; } = string.Empty;
        public string TeammateName { get; set; } = string.Empty;
        public string TeammateBMId { get; set; } = string.Empty;
        public double OverlapHours { get; set; }
        public int OverlapPercent { get; set; }
        public int JointSessionsCount { get; set; }
        public bool IsSameGroup { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public static class TeammateCorrelationService
    {
        /// <summary>
        /// Analyzes session logs, groups, and joint activities to determine probable teammates for a player.
        /// </summary>
        public static List<TeammateMatch> CalculateCorrelationsForPlayer(string bmId)
        {
            var results = new List<TeammateMatch>();
            if (string.IsNullOrWhiteSpace(bmId)) return results;

            var target = TrackingService.GetTrackedPlayer(bmId);
            if (target == null) return results;

            var allTracked = TrackingService.GetTrackedPlayers();
            var now = DateTime.UtcNow;

            foreach (var other in allTracked)
            {
                if (other.BMId.Equals(target.BMId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(target.LastServerName) && 
                    !string.IsNullOrEmpty(other.LastServerName) && 
                    !target.LastServerName.Equals(other.LastServerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool sameGroup = !string.IsNullOrWhiteSpace(target.GroupName) && 
                                 target.GroupName.Equals(other.GroupName, StringComparison.OrdinalIgnoreCase);

                double totalOverlapMins = 0;
                int jointSessions = 0;

                // Compare sessions overlap
                if (target.Sessions.Count > 0 && other.Sessions.Count > 0)
                {
                    foreach (var sA in target.Sessions)
                    {
                        var endA = sA.DisconnectTime ?? now;
                        foreach (var sB in other.Sessions)
                        {
                            var endB = sB.DisconnectTime ?? now;

                            var overlapStart = sA.ConnectTime > sB.ConnectTime ? sA.ConnectTime : sB.ConnectTime;
                            var overlapEnd = endA < endB ? endA : endB;

                            if (overlapEnd > overlapStart)
                            {
                                totalOverlapMins += (overlapEnd - overlapStart).TotalMinutes;

                                // Simultaneous connect: within 15 minutes
                                if (Math.Abs((sA.ConnectTime - sB.ConnectTime).TotalMinutes) <= 15)
                                {
                                    jointSessions++;
                                }
                            }
                        }
                    }
                }

                double targetTotalMins = target.Sessions.Sum(s => ((s.DisconnectTime ?? now) - s.ConnectTime).TotalMinutes);
                if (targetTotalMins <= 0) targetTotalMins = 60; // baseline if no closed sessions yet

                int pct = (int)Math.Min(100, Math.Max(0, (totalOverlapMins / targetTotalMins) * 100));

                if (sameGroup)
                {
                    pct = Math.Max(pct, 85);
                }

                if (pct >= 25 || sameGroup || jointSessions >= 1)
                {
                    string details;
                    if (sameGroup)
                    {
                        details = $"Aynı grupta ('{target.GroupName}')" + 
                                  (totalOverlapMins > 0 ? $" • {Math.Round(totalOverlapMins / 60.0, 1)} sa birlikte oynandı" : "");
                    }
                    else
                    {
                        details = $"{Math.Round(totalOverlapMins / 60.0, 1)} sa örtüşme • {jointSessions} eşzamanlı giriş";
                    }

                    results.Add(new TeammateMatch
                    {
                        TargetBMId = target.BMId,
                        TeammateName = other.Name,
                        TeammateBMId = other.BMId,
                        OverlapHours = Math.Round(totalOverlapMins / 60.0, 1),
                        OverlapPercent = pct,
                        JointSessionsCount = jointSessions,
                        IsSameGroup = sameGroup,
                        GroupName = target.GroupName,
                        Details = details
                    });
                }
            }

            return results
                .OrderByDescending(r => r.IsSameGroup)
                .ThenByDescending(r => r.OverlapPercent)
                .ThenByDescending(r => r.OverlapHours)
                .ToList();
        }

        /// <summary>
        /// Returns a short UI summary of top teammates, e.g. "Aerofid (%85), LK (%85)"
        /// </summary>
        public static string GetSummary(string bmId)
        {
            var matches = CalculateCorrelationsForPlayer(bmId);
            if (matches.Count == 0) return string.Empty;

            var top = matches.Take(2).Select(m => $"{m.TeammateName} (%{m.OverlapPercent})");
            return "Olası Ekip: " + string.Join(", ", top);
        }
    }
}
