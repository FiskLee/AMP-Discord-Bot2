using Discord;
using Discord.WebSocket;
using ModuleShared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using static DiscordBotPlugin.PluginMain;
using System.Threading;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;

namespace DiscordBotPlugin
{
    internal class Helpers
    {
        // Make HttpClient static to reuse the handler
        private static readonly HttpClient httpClient = new HttpClient();

        private Settings settings;
        private ILogger log;
        private IApplicationWrapper application;
        private IConfigSerializer config;
        private IPlatformInfo platform;
        private InfoPanel? infoPanel;

        // Lock object for settings modification/save (use same as Events.cs)
        private static readonly object _settingsLock = Events._settingsLock; // Assuming Events._settingsLock is accessible or use a shared static lock object reference
        // NOTE: A better approach might be to pass the lock object via dependency injection or have a shared static class for locks.
        // For simplicity here, we'll assume we can access the one from Events or define a matching one.
        // If Events._settingsLock is not directly accessible, uncomment the next line and ensure consistency:
        // private static readonly object _settingsLock = new object();

        public Helpers(Settings settings, ILogger log, IApplicationWrapper application, IConfigSerializer config, IPlatformInfo platform, InfoPanel? infoPanel)
        {
            this.settings = settings;
            this.log = log;
            this.application = application;
            this.config = config;
            this.platform = platform;
            this.infoPanel = infoPanel;
        }

        public void SetInfoPanel(InfoPanel infoPanel)
        {
            this.infoPanel = infoPanel;
        }

        public string OnlineBotPresenceString(int onlinePlayers, int maximumPlayers)
        {
            if (settings?.MainSettings == null)
            {
                log.Error("Settings or MainSettings are null in OnlineBotPresenceString.");
                return "Online";
            }

            if (string.IsNullOrEmpty(settings.MainSettings.OnlineBotPresence) && settings.MainSettings.ValidPlayerCount)
            {
                return $"{onlinePlayers}/{maximumPlayers} players";
            }

            if (string.IsNullOrEmpty(settings.MainSettings.OnlineBotPresence))
            {
                return "Online";
            }

            string presence = settings.MainSettings.OnlineBotPresence;
            presence = presence.Replace("{OnlinePlayers}", onlinePlayers.ToString());
            presence = presence.Replace("{MaximumPlayers}", maximumPlayers.ToString());

            return presence;
        }

        public string GetPlayTimeLeaderBoard(int placesToShow, bool playerSpecific, string? playerName, bool fullList, bool webPanel)
        {
            if (settings?.MainSettings?.PlayTime == null || infoPanel?.playerPlayTimes == null)
            {
                log.Error("PlayTime or playerPlayTimes are null in GetPlayTimeLeaderBoard.");
                return "```No play time logged yet```";
            }

            // Create a snapshot of current playtime data for calculation
            // Initialize snapshot dictionary with case-insensitive comparer
            var currentPlaytimeSnapshot = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

            // Manually add entries from saved PlayTime, handling potential duplicates
            if (settings.MainSettings.PlayTime != null)
            {
                foreach (var kvp in settings.MainSettings.PlayTime)
                {
                    if (!currentPlaytimeSnapshot.TryAdd(kvp.Key, kvp.Value))
                    {
                        // Log if a duplicate key (case-insensitive) is found in the source data
                        log.Warning($"[PLAYTIME_LEADER] Duplicate key (case-insensitive) found in saved PlayTime data: '{kvp.Key}'. Skipping duplicate entry.");
                        // Optionally, decide how to merge if needed, e.g.:
                        // currentPlaytimeSnapshot[kvp.Key] += kvp.Value; // This would sum times, but might not be desired if data is corrupt
                    }
                }
            }

            // Iterate over a snapshot of the concurrent dictionary's values for current sessions
            var currentSessions = infoPanel.playerPlayTimes.Values.ToList();
            foreach (PlayerPlayTime player in currentSessions)
            {
                // Defensive check in case player was removed between snapshot and access
                if (player == null) continue;

                TimeSpan currentSessionDuration = DateTime.Now - player.JoinTime;
                // Add basic sanity check for session duration
                if (currentSessionDuration.TotalSeconds < 0) {
                    log.Warning($"[PLAYTIME_LEADER] Calculated negative current session duration ({currentSessionDuration.TotalSeconds}s) for '{player.PlayerName}'. Ignoring session part for leaderboard.");
                    currentSessionDuration = TimeSpan.Zero;
                }

                if (!currentPlaytimeSnapshot.ContainsKey(player.PlayerName))
                {
                    currentPlaytimeSnapshot[player.PlayerName] = TimeSpan.Zero;
                }
                // Add current session time to the stored time for leaderboard calculation
                currentPlaytimeSnapshot[player.PlayerName] += currentSessionDuration;
            }

            // Sort the combined snapshot
            var sortedList = currentPlaytimeSnapshot.OrderByDescending(kvp => kvp.Value).ToList();

            if (sortedList.Count == 0)
            {
                return "```No play time logged yet```";
            }

            if (playerSpecific)
            {
                var playerEntry = sortedList.Find(p => p.Key == playerName);
                if (playerEntry.Key != null)
                {
                    TimeSpan time = playerEntry.Value;
                    return $"`{time.Days}d {time.Hours}h {time.Minutes}m {time.Seconds}s, position {(sortedList.FindIndex(p => p.Key == playerName) + 1)}, last seen {GetLastSeen(playerName)}`";
                }
                else
                {
                    return "```No play time logged yet```";
                }
            }
            else
            {
                string leaderboard = "";
                if (!webPanel) leaderboard += "```";

                int position = 1;

                if (fullList)
                {
                    leaderboard += $"{string.Format("{0,-4}{1,-20}{2,-15}{3,-30}", "Pos", "Player Name", "Play Time", "Last Seen")}{Environment.NewLine}";
                }

                foreach (var player in sortedList)
                {
                    if (position > placesToShow) break;

                    if (fullList)
                    {
                        leaderboard += $"{string.Format("{0,-4}{1,-20}{2,-15}{3,-30}", position + ".", player.Key, $"{player.Value.Days}d {player.Value.Hours}h {player.Value.Minutes}m {player.Value.Seconds}s", GetLastSeen(player.Key))}{Environment.NewLine}";
                    }
                    else
                    {
                        if (webPanel)
                        {
                            leaderboard += $"{player.Key} - {player.Value.Days}d {player.Value.Hours}h {player.Value.Minutes}m {player.Value.Seconds}s{Environment.NewLine}";
                        }
                        else
                        {
                            leaderboard += $"{string.Format("{0,-4}{1,-20}{2,-15}", position + ".", player.Key, $"{player.Value.Days}d {player.Value.Hours}h {player.Value.Minutes}m {player.Value.Seconds}s")}{Environment.NewLine}";
                        }
                    }

                    position++;
                }

                if (!webPanel) leaderboard += "```";

                return leaderboard ?? string.Empty;
            }
        }

        public string GetLastSeen(string playerName)
        {
            if (application is not IHasSimpleUserList hasSimpleUserList)
            {
                log.Error("Application does not implement IHasSimpleUserList in GetLastSeen.");
                return "N/A";
            }

            bool playerOnline = hasSimpleUserList.Users?.Any(user => user?.Name == playerName) ?? false;
            string lastSeen;

            if (playerOnline)
            {
                lastSeen = DateTime.Now.ToString("dddd, dd MMMM yyyy HH:mm:ss");
            }
            else
            {
                try
                {
                    lastSeen = settings?.MainSettings?.LastSeen != null && settings.MainSettings.LastSeen.ContainsKey(playerName)
                        ? settings.MainSettings.LastSeen[playerName].ToString("dddd, dd MMMM yyyy HH:mm:ss")
                        : "N/A";
                }
                catch (Exception ex)
                {
                    log.Error($"Error retrieving last seen for {playerName}: {ex.Message}");
                    lastSeen = "N/A";
                }
            }

            return lastSeen;
        }

        public void ClearAllPlayTimes()
        {
            if (infoPanel?.playerPlayTimes == null || settings?.MainSettings?.PlayTime == null || config == null)
            {
                log.Error("InfoPanel, PlayTime, or Config is null in ClearAllPlayTimes.");
                return;
            }

            // Take snapshot of players currently in dictionary before clearing
            var playersToProcess = infoPanel.playerPlayTimes.Keys.ToList();
            log.Info($"ClearAllPlayTimes called. Attempting to process {playersToProcess.Count} players.");

            foreach (var playerName in playersToProcess)
            {
                // Try to remove the player and get their data atomically
                if (infoPanel.playerPlayTimes.TryRemove(playerName, out var playerPlayTime))
                {
                    log.Debug($"Processing playtime for {playerPlayTime.PlayerName} during ClearAllPlayTimes.");
                    playerPlayTime.LeaveTime = DateTime.Now; // Mark leave time as now

                    // Lock settings access for modification and save
                    lock (_settingsLock)
                    {
                        log.Debug($"Acquired settings lock for ClearAllPlayTimes: {playerPlayTime.PlayerName}");
                        if (!settings.MainSettings.PlayTime.ContainsKey(playerPlayTime.PlayerName))
                        {
                            settings.MainSettings.PlayTime.Add(playerPlayTime.PlayerName, TimeSpan.Zero);
                            log.Warning($"PlayTime key for '{playerPlayTime.PlayerName}' did not exist during ClearAllPlayTimes, initialized to zero.");
                        }

                        TimeSpan sessionPlayTime = playerPlayTime.LeaveTime - playerPlayTime.JoinTime;
                        // Basic sanity check for playtime
                        if (sessionPlayTime.TotalSeconds < 0) {
                            log.Warning($"Calculated negative session playtime ({sessionPlayTime.TotalSeconds}s) for '{playerPlayTime.PlayerName}' during ClearAllPlayTimes. Ignoring session.");
                            sessionPlayTime = TimeSpan.Zero;
                        }

                        settings.MainSettings.PlayTime[playerPlayTime.PlayerName] += sessionPlayTime;
                        settings.MainSettings.LastSeen[playerPlayTime.PlayerName] = playerPlayTime.LeaveTime;
                        log.Info($"Updated PlayTime (+{sessionPlayTime}) and LastSeen for '{playerPlayTime.PlayerName}' during ClearAllPlayTimes. Total: {settings.MainSettings.PlayTime[playerPlayTime.PlayerName]}");

                        try {
                            config.Save(settings);
                            log.Debug("Saved settings after ClearAllPlayTimes update.");
                        } catch (Exception ex) {
                            log.Error($"Error saving settings in ClearAllPlayTimes for {playerPlayTime.PlayerName}: {ex.Message}");
                        }
                        log.Debug($"Released settings lock for ClearAllPlayTimes: {playerPlayTime.PlayerName}");
                    }
                }
                else {
                    log.Warning($"Could not remove player '{playerName}' from dictionary during ClearAllPlayTimes, potentially already removed.");
                }
            }
            // Optional: Double-check if dictionary is empty after processing
            if (!infoPanel.playerPlayTimes.IsEmpty) {
                log.Warning($"Playtime dictionary was not empty after ClearAllPlayTimes finished. Count: {infoPanel.playerPlayTimes.Count}");
                // infoPanel.playerPlayTimes.Clear(); // Force clear if needed, though TryRemove loop should handle it.
            }
        }

        public string GetMemoryUsage()
        {
            if (application == null || platform == null || settings?.MainSettings == null)
            {
                log.Error("Application, Platform, or Settings is null in GetMemoryUsage.");
                return "Unknown";
            }

            // Use platform info for total memory
            double totalAvailableMB = platform.InstalledRAMMB;
            // Get current usage from application
            double usageMB = application.GetPhysicalRAMUsage(); // Assuming this returns MB

            // Determine if we should display in GB
            bool displayInGB = usageMB >= 1024 || (totalAvailableMB >= 1024 && settings.MainSettings.ShowMaximumRAM);
            string unit = displayInGB ? "GB" : "MB";

            if (displayInGB)
            {
                usageMB /= 1024.0;
                totalAvailableMB /= 1024.0;
            }

            // Check OS and attempt platform-specific reading only if needed?
            // The original GetPhysicalRAMUsage() might already be platform-specific from AMP Core.
            // Let's first trust application.GetPhysicalRAMUsage() and platform.InstalledRAMMB
            // and only fall back to manual OS check if those values seem wrong or unavailable.

            // Simplified logic: Trust AMP Core API values first.
            // Format based on settings
            if (settings.MainSettings.ShowMaximumRAM && totalAvailableMB > 0)
            {
                return $"{usageMB:F1} / {totalAvailableMB:F1} {unit}";
            }
            else
            {
                return $"{usageMB:F1} {unit}";
            }

            /* // Keep the Linux parsing code as a fallback or alternative if needed
            if (OperatingSystem.IsLinux())
            {
                return GetMemoryUsageLinux(displayInGB, unit) ?? "N/A"; // Pass display prefs
            }
            else
            {
                // If Windows/Other OS is needed, implement here
                // For now, rely on the AMP Core API values above.
                log.Warning("GetMemoryUsage relies on IApplicationWrapper.GetPhysicalRAMUsage() and IPlatformInfo.InstalledRAMMB. OS-specific parsing not currently used unless those fail.");
                return "OS N/I"; // Indicate OS-specific method not implemented/used
            }
            */
        }

        // Modified Linux parser to accept display preferences
        private string? GetMemoryUsageLinux(bool displayInGB, string targetUnit)
        {
            try
            {
                var memInfoLines = File.ReadAllLines("/proc/meminfo");
                long memTotal = 0, memAvailable = 0, memFree = 0, buffers = 0, cached = 0;

                foreach (var line in memInfoLines)
                {
                    var parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length < 2) continue;
                    var valueStr = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (!long.TryParse(valueStr, out long valueKb)) continue;

                    switch (parts[0])
                    {
                        case "MemTotal": memTotal = valueKb; break;
                        case "MemAvailable": memAvailable = valueKb; break;
                        case "MemFree": memFree = valueKb; break;
                        case "Buffers": buffers = valueKb; break;
                        case "Cached": cached = valueKb; break;
                    }
                }

                long memUsedKb = (memAvailable > 0) ? (memTotal - memAvailable) : (memTotal - memFree - buffers - cached);
                if (memTotal == 0) {
                     log?.Warning("Could not parse MemTotal from /proc/meminfo");
                     return null;
                }

                double usageConverted = memUsedKb / (displayInGB ? 1024.0 * 1024.0 : 1024.0);
                double totalConverted = memTotal / (displayInGB ? 1024.0 * 1024.0 : 1024.0);

                if (settings?.MainSettings?.ShowMaximumRAM == true && totalConverted > 0)
                {
                    return $"{usageConverted:F1} / {totalConverted:F1} {targetUnit}";
                }
                else
                {
                    return $"{usageConverted:F1} {targetUnit}";
                }
            }
            catch (Exception ex)
            {
                log?.Error($"Error reading /proc/meminfo: {ex.Message}");
                return null;
            }
        }

        public string GetApplicationStateString()
        {
            if (settings?.MainSettings == null)
            {
                log.Error("Settings or MainSettings are null in GetApplicationStateString.");
                return "Unknown";
            }

            return settings.MainSettings.ChangeStatus.TryGetValue(application.State.ToString(), out var stateString)
                ? stateString
                : application.State.ToString();
        }

        public async Task ExecuteWithDelay(int delay, Action action)
        {
            if (action == null)
            {
                log.Error("Action is null in ExecuteWithDelay.");
                return;
            }

            await Task.Delay(delay);
            action();
        }

        public Color GetColour(string command, string hexColour)
        {
            try
            {
                string cleanedHex = hexColour.Replace("#", "");
                uint colourCode = uint.Parse(cleanedHex, System.Globalization.NumberStyles.HexNumber);
                return new Color(colourCode);
            }
            catch
            {
                log.Info($"Invalid colour code for {command}, using default colour.");
                return command switch
                {
                    "Info" => Color.DarkGrey,
                    "Start" or "PlayerJoin" => Color.Green,
                    "Stop" or "Kill" or "PlayerLeave" => Color.Red,
                    "Restart" => Color.Orange,
                    "Update" or "Manage" => Color.Blue,
                    "Console" => Color.DarkGreen,
                    "Leaderboard" => Color.DarkGrey,
                    _ => Color.DarkerGrey
                };
            }
        }

        public List<string> SplitOutputIntoCodeBlocks(List<string> messages)
        {
            if (messages == null)
            {
                log.Error("Messages list is null in SplitOutputIntoCodeBlocks.");
                return new List<string> { "No messages to display" };
            }

            const int MaxCodeBlockLength = 2000;
            List<string> outputStrings = new List<string>();
            string currentString = "";

            foreach (string message in messages)
            {
                if (currentString.Length + message.Length + Environment.NewLine.Length + 6 > MaxCodeBlockLength)
                {
                    outputStrings.Add($"```{currentString}```");
                    currentString = "";
                }

                if (!string.IsNullOrEmpty(currentString))
                {
                    currentString += Environment.NewLine;
                }
                currentString += message;
            }

            if (!string.IsNullOrEmpty(currentString))
            {
                outputStrings.Add($"```{currentString}```");
            }

            return outputStrings;
        }

        public async Task<string> GetExternalIpAddressAsync()
        {
            // Use the static HttpClient instance
            try
            {
                // Use a service that returns the IP address in plain text
                // Ensure HttpResponseMessage is disposed
                 using (HttpResponseMessage response = await httpClient.GetAsync("https://api.ipify.org"))
                 {
                    response.EnsureSuccessStatusCode(); // Throws HttpRequestException on non-success codes

                    string ipAddress = await response.Content.ReadAsStringAsync();
                     log.Debug($"Successfully fetched external IP: {ipAddress}");
                    return ipAddress;
                }
            }
             catch (HttpRequestException httpEx)
            {
                // Log HTTP-specific errors
                 log.Error($"HTTP error fetching external IP: {httpEx.StatusCode} - {httpEx.Message}");
                 return null;
             }
            catch (Exception ex)
            {
                 // Log other potential errors (DNS issues, timeouts, etc.)
                log.Error("Error fetching external IP: " + ex.Message);
                return null;
            }
        }

        /// <param name="channelNameOrId">Channel Name or ID</param>
        /// <returns>SocketTextChannel or null if not found/accessible</returns>
        public SocketTextChannel? GetEventChannel(DiscordSocketClient? client, ulong guildId, string channelNameOrId)
        {
            // The 'as' cast correctly handles null if the channel is not found or not a text channel.
            // The method signature already returns SocketTextChannel?, reflecting the possibility of null.
            // Therefore, the suppression is unnecessary and the warning is likely a compiler artifact or misunderstanding.
            // We remove the suppression as the code is semantically correct.
            // #pragma warning disable CS8603 // Suppress warning, 'as' cast handles null correctly
            return GetChannel<SocketGuildChannel>(client, guildId, channelNameOrId) as SocketTextChannel;
            // #pragma warning restore CS8603
        }

        /// <summary>
        /// Helper to get a specific text channel by Name or ID within a guild.
        /// </summary>
        /// <param name="client">The Discord client instance.</param>
        /// <param name="guildId">Guild ID</param>
        /// <param name="channelNameOrId">Channel Name or ID</param>
        /// <returns>SocketTextChannel or null if not found/accessible</returns>
        public SocketTextChannel? GetTextChannel(DiscordSocketClient? client, ulong guildId, string channelNameOrId)
        {
             // The 'as' cast correctly handles null if the channel is not found or not a text channel.
             // The method signature already returns SocketTextChannel?, reflecting the possibility of null.
             // Therefore, the suppression is unnecessary and the warning is likely a compiler artifact or misunderstanding.
             // We remove the suppression as the code is semantically correct.
             // #pragma warning disable CS8603 // Suppress warning, 'as' cast handles null correctly
             return GetChannel<SocketGuildChannel>(client, guildId, channelNameOrId) as SocketTextChannel;
             // #pragma warning restore CS8603
        }

        // Generic private helper to find a channel - Return SocketGuildChannel?
        private SocketGuildChannel? GetChannel<T>(DiscordSocketClient? client, ulong guildId, string channelNameOrId) where T : SocketGuildChannel
        {
            // Use the passed client instance
            if (client == null || string.IsNullOrWhiteSpace(channelNameOrId))
            {
                // Safe access to log
                log?.Error("Discord client is null or channelNameOrId is null or empty in GetChannel.");
                return null;
            }

            var guild = client.GetGuild(guildId);
            if (guild == null)
            {
                 // Safe access to log
                 log?.Warning($"Guild with ID {guildId} not found by bot in GetChannel.");
                return null; // Explicitly return null if guild not found
            }

            SocketGuildChannel? channel = null;
            // Attempt to find by ID first
            if (ulong.TryParse(channelNameOrId, out ulong channelId))
            {
                channel = guild.GetChannel(channelId) as SocketGuildChannel;
            }

            // If not found by ID, try by name (case-insensitive)
            if (channel == null)
            {
                 // Find any SocketGuildChannel first, then check type later if needed by caller?
                 // Or keep T constraint? Keeping T is safer type-wise.
                 // Ensure we are looking for SocketGuildChannel which is the base type used in the 'as' cast later
                 channel = guild.Channels
                             // .OfType<T>() // REMOVED: Find any guild channel first
                             .FirstOrDefault(c => c.Name.Equals(channelNameOrId, StringComparison.OrdinalIgnoreCase));
            }

            // Add explicit check before returning
            if (channel == null)
            {
                 log?.Warning($"Channel '{channelNameOrId}' not found in guild '{guild.Name}' (ID: {guildId}). Returning null.");
                 return null;
            }

            return channel; // Returns the found SocketGuildChannel? or null
        }
    }
}
