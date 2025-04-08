using Discord.WebSocket;
using Discord;
using ModuleShared;
using System;
using System.Threading.Tasks;
using static DiscordBotPlugin.PluginMain;
using System.Text.RegularExpressions;
using LocalFileBackupPlugin;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Text;

namespace DiscordBotPlugin
{
    internal class Events
    {
        private readonly IApplicationWrapper application;
        private readonly Settings settings;
        private readonly ILogger log;
        private readonly IConfigSerializer config;
        private readonly Bot bot;
        private readonly Helpers helper;
        private BackupProvider? backupProvider;
        private readonly InfoPanel infoPanel;

        public IUser? currentUser;

        // Lock object for settings modification/save
        // Make internal so Helpers.cs can access the same lock instance
        internal static readonly object _settingsLock = new object();

        public Events(IApplicationWrapper application, Settings settings, ILogger log, IConfigSerializer config, Bot bot, Helpers helper, BackupProvider? backupProvider, InfoPanel infoPanel)
        {
            this.application = application;
            this.settings = settings;
            this.log = log;
            this.config = config;
            this.bot = bot;
            this.helper = helper;
            this.backupProvider = backupProvider;
            this.infoPanel = infoPanel;
        }

        public void SetCurrentUser(SocketGuildUser currentUser)
        {
            this.currentUser = currentUser;
        }

        public void SetBackupProvider(BackupProvider? backupProvider)
        {
            this.backupProvider = backupProvider;
        }

        /// <summary>
        /// Event handler for when a user joins the server.
        /// </summary>
        public async void UserJoins(object? sender, UserEventArgs args)
        {
            // Basic null check for args and User
            if (args?.User == null)
            {
                log.Error("User event argument or User property is null.");
                return;
            }

            try
            {
                string userName = args.User.Name;
                if (string.IsNullOrEmpty(userName))
                {
                    log.Warning("User name is null or empty in UserJoins event.");
                    userName = "Unknown User"; // Provide a default
                }

                // Add player to playtime dictionary using AddOrUpdate for thread safety
                var playerInfo = infoPanel.playerPlayTimes.AddOrUpdate(
                    userName,
                    new PlayerPlayTime { JoinTime = DateTime.Now }, // Add new player - REMOVED PlayerName assignment
                    (key, existingPlayer) => { // Update existing player (e.g., reconnect)
                        existingPlayer.JoinTime = DateTime.Now;
                        // We can rely on the key (userName) if needed, no need to store PlayerName redundantly inside value?
                        log.Info($"Updated JoinTime for returning player '{key}'."); // Log using key
                        return existingPlayer;
                    });

                log.Info($"Player '{userName}' added/updated in playtime dictionary.");

                // Delay presence update slightly after join to ensure count is accurate
                await helper.ExecuteWithDelay(2000, () => _ = bot.UpdatePresence(null, null));
            }
            catch (Exception ex)
            {
                log.Error($"Error processing player join: {ex.Message}");
            }

            // Check if posting player events is disabled
            if (!settings.MainSettings.PostPlayerEvents)
                return;

            // Use null-conditional and null-coalescing operators for safe guild access
            foreach (var (socketGuild, eventChannel) in from SocketGuild socketGuild in bot?.client?.Guilds ?? new List<SocketGuild>()
                                                        let eventChannel = bot?.GetEventChannel(socketGuild.Id, settings.MainSettings.PostPlayerEventsChannel)
                                                        select (socketGuild, eventChannel))
            {
                // Add bot null check
                var localBot = bot; // Use local variable
                // Check localBot AND localBot.client
                if (eventChannel == null || localBot == null || localBot.client == null || !localBot.CanBotSendMessageInChannel(localBot.client, eventChannel.Id))
                {
                    log.Error($"No permission to post join message to channel: {eventChannel?.Name ?? "Unknown"} ({eventChannel?.Id ?? 0L}) in guild {socketGuild.Name} or bot/client is null.");
                    return;
                }

                var joinColor = GetJoinColour(args);
                string userName = args.User.Name;
                var embed = new EmbedBuilder
                {
                    Title = "Server Event",
                    Description = string.IsNullOrEmpty(userName) ? $"A player joined the {application.ApplicationName} server." : $"{userName} joined the {application.ApplicationName} server.",
                    ThumbnailUrl = settings.MainSettings.GameImageURL ?? string.Empty,
                    Color = joinColor,
                    Footer = new EmbedFooterBuilder { Text = settings.MainSettings.BotTagline ?? string.Empty },
                    Timestamp = DateTimeOffset.Now
                };

                // Add null checks before sending message
                var targetGuild = bot?.client?.GetGuild(socketGuild.Id);
                var targetChannel = targetGuild?.GetTextChannel(eventChannel.Id);
                if (targetChannel != null)
                {
                    await targetChannel.SendMessageAsync(embed: embed.Build());
                }
                else
                {
                    log.Warning($"Could not find target channel ({eventChannel.Id}) in guild ({socketGuild.Id}) to send join message.");
                }
            }
        }

        private Color GetJoinColour(UserEventArgs args)
        {
            if (helper != null && settings?.ColourSettings != null && !string.IsNullOrEmpty(settings.ColourSettings.ServerPlayerJoinEventColour))
            {
                return helper.GetColour("PlayerJoin", settings.ColourSettings.ServerPlayerJoinEventColour);
            }
            return Color.Green; // Default
        }

        /// <summary>
        /// Event handler for when a user leaves the server.
        /// </summary>
        public async void UserLeaves(object? sender, UserEventArgs args)
        {
            if (args?.User == null)
            {
                log.Error("User event argument is null.");
                return;
            }

            try
            {
                 // Attempt to remove player using TryRemove for thread safety
                 if (infoPanel.playerPlayTimes.TryRemove(args.User.Name, out var player))
                 {
                    player.LeaveTime = DateTime.Now;
                    log.Info($"Player '{args.User.Name}' removed from playtime dictionary.");

                    // Lock settings access for modification and save
                     lock (_settingsLock)
                    {
                         log.Debug($"Acquired settings lock for UserLeaves: {args.User.Name}");
                         if (!string.IsNullOrEmpty(args.User.Name))
                         {
                             if (!settings.MainSettings.PlayTime.ContainsKey(args.User.Name))
                            {
                                 settings.MainSettings.PlayTime[args.User.Name] = TimeSpan.Zero;
                                 log.Warning($"PlayTime key for '{args.User.Name}' did not exist on leave, initialized to zero.");
                             }

                            var sessionPlayTime = player.LeaveTime - player.JoinTime;
                             // Basic sanity check for playtime (e.g., negative duration)
                             if (sessionPlayTime.TotalSeconds < 0) {
                                 log.Warning($"Calculated negative session playtime ({sessionPlayTime.TotalSeconds}s) for '{args.User.Name}'. Ignoring session.");
                                 sessionPlayTime = TimeSpan.Zero;
                             }

                            settings.MainSettings.PlayTime[args.User.Name] += sessionPlayTime;
                            settings.MainSettings.LastSeen[args.User.Name] = player.LeaveTime;
                             log.Info($"Updated PlayTime (+{sessionPlayTime}) and LastSeen for '{args.User.Name}'. Total: {settings.MainSettings.PlayTime[args.User.Name]}");
                         }
                         else
                         {
                            log.Warning("User name was null or empty in UserLeaves when trying to update playtime/lastseen.");
                         }

                        try {
                             config.Save(settings);
                             log.Debug("Saved settings after UserLeaves update.");
                         } catch (Exception ex) {
                             log.Error($"Error saving settings in UserLeaves for {args.User.Name}: {ex.Message}");
                         }
                         log.Debug($"Released settings lock for UserLeaves: {args.User.Name}");
                     }
                 }
                 else
                 {
                    log.Warning($"Player {args.User.Name} was not found in the playtime dictionary upon leave event.");
                    // Still update LastSeen if possible, even if session couldn't be recorded
                    lock(_settingsLock)
                    {
                        settings.MainSettings.LastSeen[args.User.Name] = DateTime.Now;
                         try {
                             config.Save(settings);
                             log.Info($"Updated LastSeen for '{args.User.Name}' even though playtime entry was missing.");
                         } catch (Exception ex) {
                             log.Error($"Error saving settings in UserLeaves (missing player) for {args.User.Name}: {ex.Message}");
                         }
                    }
                 }

                // Delay presence update slightly after leave to ensure count is accurate
                 await helper.ExecuteWithDelay(2000, () => _ = bot.UpdatePresence(null, null));
            }
            catch (Exception ex)
            {
                log.Error($"Error processing player leave: {ex.Message}");
            }

            // Check if posting player events is disabled
            if (!settings.MainSettings.PostPlayerEvents)
                return;

            // Use null-conditional and null-coalescing operators for safe guild access
            foreach (var (socketGuild, eventChannel) in from SocketGuild socketGuild in bot?.client?.Guilds ?? new List<SocketGuild>()
                                                        let eventChannel = bot?.GetEventChannel(socketGuild.Id, settings.MainSettings.PostPlayerEventsChannel)
                                                        select (socketGuild, eventChannel))
            {
                // Add bot null check
                var localBot = bot; // Use local variable
                // Check localBot AND localBot.client
                if (eventChannel == null || localBot == null || localBot.client == null || !localBot.CanBotSendMessageInChannel(localBot.client, eventChannel.Id))
                {
                    log.Error($"No permission to post leave message to channel: {eventChannel?.Name ?? "Unknown"} ({eventChannel?.Id ?? 0L}) in guild {socketGuild.Name} or bot/client is null.");
                    return;
                }

                var leaveColor = GetLeaveColour(args);
                string userName = args.User.Name;
                var embed = new EmbedBuilder
                {
                    Title = "Server Event",
                    Description = string.IsNullOrEmpty(userName) ? $"A player left the {application.ApplicationName} server." : $"{userName} left the {application.ApplicationName} server.",
                    ThumbnailUrl = settings.MainSettings.GameImageURL ?? string.Empty,
                    Color = leaveColor,
                    Footer = new EmbedFooterBuilder { Text = settings.MainSettings.BotTagline ?? string.Empty },
                    Timestamp = DateTimeOffset.Now
                };

                // Add null checks before sending message
                var targetGuild = bot?.client?.GetGuild(socketGuild.Id);
                var targetChannel = targetGuild?.GetTextChannel(eventChannel.Id);
                if (targetChannel != null)
                {
                    await targetChannel.SendMessageAsync(embed: embed.Build());
                }
                else
                {
                    log.Warning($"Could not find target channel ({eventChannel.Id}) in guild ({socketGuild.Id}) to send leave message.");
                }
            }
        }

        private Color GetLeaveColour(UserEventArgs args)
        {
            if (helper != null && settings?.ColourSettings != null && !string.IsNullOrEmpty(settings.ColourSettings.ServerPlayerLeaveEventColour))
            {
                return helper.GetColour("PlayerLeave", settings.ColourSettings.ServerPlayerLeaveEventColour);
            }
            return Color.Red; // Default
        }

        /// <summary>
        /// Logs a message with an information level.
        /// </summary>
        public Task Log(LogMessage msg)
        {
            log.Info(msg.ToString() ?? "Unknown log message");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs on SettingsModified event.
        /// </summary>
        public void Settings_SettingModified(object? sender, SettingModifiedEventArgs e)
        {
            log.Info($"Setting '{e.SettingName}' changed to '{e.NewValue}'");
            // Re-validate config if relevant settings changed

            if (settings.MainSettings.BotActive)
            {
                try
                {
                    if (bot.client == null || bot.client.ConnectionState == ConnectionState.Disconnected)
                    {
                        _ = bot.ConnectDiscordAsync(settings.MainSettings.BotToken);
                    }

                    // Run validation when settings change and bot is active
                    log.Info("Settings modified, running configuration validation...");
                    _ = Task.Run(() => ValidateConfigurationAsync("Settings Change"));
                }
                catch (Exception exception)
                {
                    log.Error($"Error with the Discord Bot: {exception.Message}");
                }
            }
            else
            {
                if (bot.client?.ConnectionState == ConnectionState.Connected)
                {
                    bot.client.ButtonExecuted -= infoPanel.OnButtonPress;
                    bot.client.Log -= Log;
                    bot.client.Ready -= bot.ClientReady;
                    bot.client.SlashCommandExecuted -= bot.SlashCommandHandler;
                    bot.client.MessageReceived -= bot.MessageHandler;

                    try
                    {
                        _ = bot.client.LogoutAsync();
                    }
                    catch (Exception exception)
                    {
                        log.Error($"Error logging out from Discord: {exception.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Runs on MessageLogged event.
        /// </summary>
        public void Log_MessageLogged(object? sender, LogEventArgs e)
        {
            if (e == null)
            {
                log.Error("LogEventArgs is null.");
                return;
            }

            string cleanMessage = e.Message.Replace("`", "'");

            if (e.Level == LogLevels.Chat.ToString() && settings.MainSettings.SendChatToDiscord && !string.IsNullOrEmpty(settings.MainSettings.ChatToDiscordChannel))
            {
                _ = bot.ChatMessageSend(cleanMessage);
            }

            if ((e.Level == LogLevels.Console.ToString() || e.Level == LogLevels.Chat.ToString()) && settings.MainSettings.SendConsoleToDiscord && !string.IsNullOrEmpty(settings.MainSettings.ConsoleToDiscordChannel))
            {
                // Use Enqueue for ConcurrentQueue
                bot.consoleOutput.Enqueue(cleanMessage);
            }

            if (e.Level == LogLevels.Console.ToString() && settings.GameSpecificSettings.ValheimJoinCode)
            {
                string pattern = @"join code (\d+)";
                Match match = Regex.Match(e.Message, pattern);

                if (match.Success)
                {
                    infoPanel.valheimJoinCode = match.Groups[1].Value;
                }
            }
        }

        /// <summary>
        /// Updates the bot's presence when the application state changes.
        /// </summary>
        public void ApplicationStateChange(object? sender, ApplicationStateChangeEventArgs args)
        {
            _ = bot.UpdatePresence(sender, args);
        }

        /// <summary>
        /// Handles backup completion event.
        /// </summary>
        public async void OnBackupComplete(object? sender, EventArgs e)
        {
            UnregisterBackupEvents();
            // Null check for currentUser
            if (currentUser != null)
            {
                await currentUser.SendMessageAsync("Backup completed successfully.");
            }
            else
            {
                log.Warning("Backup completed, but currentUser was null. Could not send DM.");
            }
        }

        /// <summary>
        /// Handles backup failure event.
        /// </summary>
        public async void OnBackupFailed(object? sender, EventArgs e)
        {
            UnregisterBackupEvents();
            // Null check for currentUser
            if (currentUser != null)
            {
                await currentUser.SendMessageAsync("Backup failed. Please check AMP logs for details.");
            }
            else
            {
                 log.Warning("Backup failed, but currentUser was null. Could not send DM.");
            }
        }

        /// <summary>
        /// Handles backup starting event.
        /// </summary>
        public void OnBackupStarting(object? sender, EventArgs e)
        {
            currentUser?.SendMessageAsync("Backup is starting.");
        }

        private void UnregisterBackupEvents()
        {
            if (backupProvider != null)
            {
                // Use correct event names from BackupProvider
                // backupProvider.BackupStatusChanged -= infoPanel.BackupStatusChangedHandler; // Assuming this handler exists/is needed
                 backupProvider.BackupActionComplete -= infoPanel.BackupCompleteHandler; // Corrected name
                 backupProvider.BackupActionFailed -= infoPanel.BackupFailedHandler;   // Corrected name
                 // backupProvider.BackupActionStarting -= infoPanel.BackupStartingHandler; // Assuming this handler exists/is needed
            }
        }

        /// <summary>
        /// Validates critical channel and role settings.
        /// </summary>
        /// <param name="triggerContext">String indicating what triggered the validation (e.g., "Initial Startup", "Settings Change").</param>
        /// <param name="requesterChannelId">Optional ID of the channel that requested the validation.</param>
        public async Task ValidateConfigurationAsync(string triggerContext, ulong? requesterChannelId = null)
        {
            log.Info("Validating configuration...");
            bool overallValid = true;
            StringBuilder issues = new StringBuilder();

            // Use a snapshot of the client guilds
            var guilds = bot?.client?.Guilds?.ToList() ?? new List<SocketGuild>();

            if (!guilds.Any())
            {
                log.Warning("Validation skipped: Bot is not connected to any guilds yet.");
                return; // Cannot validate without guilds
            }

            foreach(var guild in guilds)
            {
                issues.AppendLine($"\n--- Guild: {guild.Name} ({guild.Id}) ---");

                // Validate Channels
                issues.AppendLine("Channel Settings:");
                overallValid &= ValidateSingleChannel(guild, settings.MainSettings.ButtonResponseChannel, "Button/Command Log", issues, optional: true);
                overallValid &= ValidateSingleChannel(guild, settings.MainSettings.PostPlayerEventsChannel, "Player Events", issues, optional: !settings.MainSettings.PostPlayerEvents);
                overallValid &= ValidateSingleChannel(guild, settings.MainSettings.ChatToDiscordChannel, "Game Chat", issues, optional: !settings.MainSettings.SendChatToDiscord);
                overallValid &= ValidateSingleChannel(guild, settings.MainSettings.ConsoleToDiscordChannel, "Console Output", issues, optional: !settings.MainSettings.SendConsoleToDiscord);

                // Validate Roles
                if (settings.MainSettings.RestrictFunctions) {
                    // Call public method directly on bot
                    if (!bot.ValidateRoleSetting(settings.MainSettings.DiscordRole, "Discord Role Name(s)/ID(s)")) overallValid = false;
                }

                // Validate Info Panel Messages
                issues.AppendLine("\nInfo Panel Message Persistence:");
                int validMessages = 0;
                List<string> messagesToRemove = new List<string>();
                 // Iterate over a copy for safe removal, use ?? for null safety
                 foreach (string details in settings?.MainSettings?.InfoMessageDetails?.ToList() ?? new List<string>())
                {
                     try
                     {
                         string[] split = details.Split('-');
                         if (split.Length < 3 || !ulong.TryParse(split[0], out ulong msgGuildId) || !ulong.TryParse(split[1], out ulong msgChannelId) || !ulong.TryParse(split[2], out ulong msgId))
                         {
                            issues.AppendLine($"  - Invalid entry format: '{details}'. Removing.");
                            messagesToRemove.Add(details);
                             continue;
                         }

                        if (msgGuildId != guild.Id)
                        {
                            // This message belongs to a different guild being checked, skip for now
                            continue;
                        }

                        // Pass bot.client here
                        var msgChannel = helper.GetTextChannel(bot?.client, msgGuildId, msgChannelId.ToString());
                        if (msgChannel == null)
                        {
                            issues.AppendLine($"  - Channel {msgChannelId} for message {msgId} not found in guild {guild.Name}. Removing entry '{details}'.");
                            messagesToRemove.Add(details);
                            continue;
                        }

                        try
                        {
                            var message = await msgChannel.GetMessageAsync(msgId);
                            if (message == null)
                            {
                                issues.AppendLine($"  - Message {msgId} not found in channel {msgChannel.Name}. Removing entry '{details}'.");
                                messagesToRemove.Add(details);
                            }
                            else
                            {
                                validMessages++;
                            }
                        }
                        catch (Discord.Net.HttpException httpEx) when (httpEx.HttpCode == System.Net.HttpStatusCode.NotFound)
                        {
                            issues.AppendLine($"  - Message {msgId} not found (HTTP 404) in channel {msgChannel.Name}. Removing entry '{details}'.");
                            messagesToRemove.Add(details);
                        }
                        catch (Exception ex)
                        {
                            issues.AppendLine($"  - Error verifying message {msgId} in channel {msgChannel.Name}: {ex.Message}. Removing entry '{details}'.");
                            messagesToRemove.Add(details);
                        }
                    }
                    catch(Exception entryEx)
                    {
                        issues.AppendLine($"  - Error processing entry '{details}': {entryEx.Message}. Removing.");
                        messagesToRemove.Add(details);
                    }
                 }

                // Perform removal outside the loop
                if (messagesToRemove.Any())
                {
                    lock (_settingsLock)
                    {
                        messagesToRemove.ForEach(m => settings?.MainSettings?.InfoMessageDetails?.Remove(m));
                        config?.Save(settings);
                    }
                    issues.AppendLine($"  - Removed {messagesToRemove.Count} invalid/missing message entries from settings.");
                }
                 if (validMessages > 0)
                 {
                     issues.AppendLine($"  - Found {validMessages} valid persisted info panel message(s).");
                 }
                 else if (!(settings?.MainSettings?.InfoMessageDetails?.Any(d => d.StartsWith(guild.Id.ToString())) ?? false))
                 {
                      issues.AppendLine("  - No info panel messages configured/persisted for this guild.");
                 }

                issues.AppendLine("--- End Guild ---");
            }

            // Send results to Discord
            if (bot?.client?.ConnectionState == ConnectionState.Connected)
            {
                string summary = overallValid ? "Configuration validation passed." : "Configuration validation completed with issues.";
                log.Info(summary + " Check bot response for details.");

                var embed = new EmbedBuilder()
                    .WithTitle("Configuration Validation Result")
                    .WithDescription(summary)
                    .AddField("Details", $"```\n{issues.ToString().Trim()}\n```")
                    .WithColor(overallValid ? Color.Green : Color.Orange)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter($"Validation triggered by: {triggerContext}");

                // Try sending to the command channel if available, otherwise log channel, else nowhere
                ISocketMessageChannel? targetChannel = null;
                SocketGuild? firstGuild = guilds.FirstOrDefault(); // Get the first guild once

                // Check requester channel first
                if (requesterChannelId.HasValue && firstGuild != null) // Check firstGuild here
                {
                    targetChannel = firstGuild.GetTextChannel(requesterChannelId.Value);
                }

                // Fallback to ButtonResponseChannel if requester channel wasn't found or not provided
                if (targetChannel == null && !string.IsNullOrWhiteSpace(settings.MainSettings.ButtonResponseChannel))
                {
                     // Use helper to handle ID or Name for the log channel
                     // Check bot here too before accessing client
                     if (firstGuild != null && bot != null)
                     {
                         targetChannel = helper.GetEventChannel(bot.client!, firstGuild.Id, settings.MainSettings.ButtonResponseChannel);
                     }
                }

                if (targetChannel != null)
                {
                     try {
                        await targetChannel.SendMessageAsync(embed: embed.Build());
                     } catch (Exception sendEx) {
                         log.Error($"Failed to send validation results to channel {targetChannel.Name}: {sendEx.Message}");
                         // Log the full details to AMP console if Discord send fails
                         log.Warning("Validation Details:\n" + issues.ToString());
                     }
                }
                else
                {
                    log.Warning("Could not find a suitable channel to send validation results. Logging details here instead:");
                    log.Warning(issues.ToString());
                }
            }
            else
            {
                log.Warning("Bot is not connected, cannot send validation results to Discord. Logging details here:");
                log.Warning(issues.ToString());
            }
        }

        // Helper for ValidateConfigurationAsync
        private bool ValidateSingleChannel(SocketGuild guild, string channelNameOrId, string settingName, StringBuilder issues, bool optional)
        {
            if (string.IsNullOrWhiteSpace(channelNameOrId))
            {
                if (optional)
                {
                    issues.AppendLine($"  - {settingName}: OK (Optional, not configured)");
                    return true;
                }
                else
                {
                    issues.AppendLine($"  - {settingName}: FAIL (Required, but not configured)");
                    return false;
                }
            }

            // Use null-forgiving ! on client as bot is checked before calling this context
#pragma warning disable CS8602 // Suppress client warning
            var channel = helper.GetEventChannel(bot?.client!, guild.Id, channelNameOrId);
#pragma warning restore CS8602
            if (channel == null)
            {
                issues.AppendLine($"  - {settingName} ('{channelNameOrId}'): FAIL (Channel not found in guild '{guild.Name}')");
                return false;
            }
            // Use null-forgiving ! on client as bot is checked before calling this context
            var localBotForPerm = bot; // Use local variable
            // Check localBotForPerm AND localBotForPerm.client
#pragma warning disable CS8602 // Suppress client warning
            if (localBotForPerm == null || localBotForPerm.client == null || !localBotForPerm.CanBotSendMessageInChannel(localBotForPerm.client!, channel.Id))
#pragma warning restore CS8602
            {
                issues.AppendLine($"  - {settingName} ('{channel.Name}'): FAIL (Bot lacks permission to send messages or bot/client instance is null)");
                return false;
            }
            else
            {
                issues.AppendLine($"  - {settingName} ('{channel.Name}'): OK");
                return true;
            }
        }

        // Make synchronous (CS1998)
        private void SendLogEmbedAsync(string logEntry)
        {
            if (bot.client == null || !settings.MainSettings.SendConsoleToDiscord)
            {
                return;
            }

            var guilds = bot.client.Guilds;
            // Use default color as the setting was removed
            var logColor = Color.LightGrey;

            foreach (var guild in guilds)
            {
                // Implement the logic to send the log entry to the appropriate channel in the guild
                // This is a placeholder and should be replaced with the actual implementation
                // For example, you can use bot.client.GetGuild(guild.Id).GetTextChannel(channelId).SendMessageAsync(logEntry);
            }
        }

        // Async helper to safely send a message to a user
        private async Task SafeSendUserMessageAsync(IUser? user, string message)
        {
            if (user == null)
            {
                log.Warning("Attempted to send DM, but target user was null.");
                return;
            }
            try
            {
                await user.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                log.Error($"Failed to send DM to user {user?.Username ?? "Unknown"} ({user?.Id ?? 0}): {ex.Message}");
            }
        }
    }
}
