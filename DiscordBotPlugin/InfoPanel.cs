using Discord.WebSocket;
using Discord;
using ModuleShared;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBotPlugin.PluginMain;
using System.Collections.Concurrent;
using System.Timers;
using System.Text;
using LocalFileBackupPlugin;

namespace DiscordBotPlugin
{
    internal class InfoPanel
    {
        private readonly IApplicationWrapper application;
        private readonly Settings settings;
        private readonly Helpers helper;
        private readonly IAMPInstanceInfo aMPInstanceInfo;
        private readonly ILogger log;
        private readonly IConfigSerializer config;
        private Bot? bot;
        private readonly Commands commands;
        internal string? valheimJoinCode;

        public InfoPanel(IApplicationWrapper application, Settings settings, Helpers helper, IAMPInstanceInfo aMPInstanceInfo, ILogger log, IConfigSerializer config, Bot? bot, Commands commands)
        {
            this.application = application;
            this.settings = settings;
            this.helper = helper;
            this.aMPInstanceInfo = aMPInstanceInfo;
            this.log = log;
            this.config = config;
            this.bot = bot;
            this.commands = commands;
        }

        public void SetBot(Bot bot)
        {
            this.bot = bot;
        }

        public ConcurrentDictionary<string, PlayerPlayTime> playerPlayTimes = new ConcurrentDictionary<string, PlayerPlayTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Task to get current server info and create or update an embedded message
        /// </summary>
        /// <param name="updateExisting">Embed already exists?</param>
        /// <param name="msg">Command from Discord</param>
        /// <param name="Buttonless">Should the embed be buttonless?</param>
        /// <returns></returns>
        public async Task GetServerInfo(bool updateExisting, SocketSlashCommand msg, bool Buttonless)
        {
            if (bot?.client?.ConnectionState != ConnectionState.Connected)
            {
                log.Warning("Client is not connected.");
                return;
            }

            if (application is not IHasSimpleUserList hasSimpleUserList)
            {
                log.Error("Application does not implement IHasSimpleUserList.");
                return;
            }

            var onlinePlayers = hasSimpleUserList.Users.Count;
            var maximumPlayers = hasSimpleUserList.MaxUsers;

            var embed = new EmbedBuilder
            {
                Title = "Server Info",
                ThumbnailUrl = settings?.MainSettings?.GameImageURL ?? string.Empty
            };

            // Check helper before use
            if (helper != null && settings?.ColourSettings != null && !string.IsNullOrEmpty(settings.ColourSettings.InfoPanelColour))
            {
                 Color panelColor = helper.GetColour("Info", settings.ColourSettings.InfoPanelColour);
                 // Explicitly use non-null color
                 embed.Color = (panelColor != default(Color)) ? panelColor : Color.DarkGrey;
            }
            else
            {
                 embed.Color = Color.DarkGrey; // Also assign default here
            }

            var appState = application?.State ?? ApplicationState.Stopped;
            switch (appState)
            {
                case ApplicationState.Ready:
                     // Use null forgiving ! only if helper is guaranteed non-null
                     // Prefer checking helper != null first
                     embed.AddField("Server Status", ":white_check_mark: " + (helper?.GetApplicationStateString() ?? "Ready"), false);
                    break;
                case ApplicationState.Failed:
                case ApplicationState.Stopped:
                     embed.AddField("Server Status", ":no_entry: " + (helper?.GetApplicationStateString() ?? "Stopped"), false);
                    break;
                default:
                     embed.AddField("Server Status", ":hourglass: " + (helper?.GetApplicationStateString() ?? "Pending"), false);
                    break;
            }

            embed.AddField("Server Name", "```" + (settings?.MainSettings?.ServerDisplayName ?? "N/A") + "```", false);
            string connectionURL = settings?.MainSettings?.ServerConnectionURL ?? string.Empty;
            if (connectionURL.ToLower().Contains("{publicip}"))
            {
                string ipAddress = await helper?.GetExternalIpAddressAsync() ?? string.Empty;
                if (!string.IsNullOrEmpty(ipAddress))
                {
                    connectionURL = connectionURL.ToLower().Replace("{publicip}", ipAddress);
                    log.Debug($"Replaced {{PublicIP}} in connection URL. Result: {connectionURL}");
                }
                else {
                    log.Warning("Failed to retrieve external IP address. Cannot replace {PublicIP} in connection URL.");
                }
            }
            embed.AddField("Server IP", "```" + connectionURL + "```", false);

            if (settings?.MainSettings != null && !string.IsNullOrEmpty(settings.MainSettings.ServerPassword))
            {
                embed.AddField("Server Password", "```" + settings.MainSettings.ServerPassword + "```", false);
            }

            // CPU Usage - More robust handling
            double? cpuUsage = application?.GetCPUUsage(); // Assuming GetCPUUsage returns double?
            string cpuUsageString = cpuUsage.HasValue ? $"{cpuUsage.Value:F1}%" : "N/A";
            embed.AddField("CPU Usage", cpuUsageString, true);

            // Memory Usage - Uses the refined GetMemoryUsage from Helpers
            // Add null check for helper
            string memoryUsageString = helper?.GetMemoryUsage() ?? "N/A"; // Default to N/A if helper is null
            embed.AddField("Memory Usage", memoryUsageString, true);

            if (appState == ApplicationState.Ready && application != null)
            {
                TimeSpan uptime = DateTime.Now.Subtract(application.StartTime.ToLocalTime());
                embed.AddField("Uptime", string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}", uptime.Days, uptime.Hours, uptime.Minutes, uptime.Seconds), true);
            }

            if (settings?.MainSettings?.ValidPlayerCount == true)
            {
                embed.AddField("Player Count", onlinePlayers + "/" + maximumPlayers, true);
            }

            if (settings?.MainSettings?.ShowOnlinePlayers == true && hasSimpleUserList != null)
            {
                List<string> onlinePlayerNames = hasSimpleUserList.Users?.Where(u => u != null && !string.IsNullOrEmpty(u.Name)).Select(u => u.Name!).ToList() ?? new List<string>();
                if (onlinePlayerNames.Any())
                {
                    embed.AddField("Online Players", string.Join(Environment.NewLine, onlinePlayerNames), false);
                }
            }

            if (settings?.MainSettings != null && !string.IsNullOrEmpty(settings.MainSettings.GameImageURL))
            {
                // This was wrongly ModpackURL before, assuming GameImageURL is correct here
                // embed.AddField("Server Mod Pack", settings.MainSettings.GameImageURL, false);
                // If it should be Modpack URL, change GameImageURL to ModpackURL
                 if (!string.IsNullOrEmpty(settings.MainSettings.ModpackURL)) {
                    embed.AddField("Server Mod Pack", settings.MainSettings.ModpackURL, false);
                 }
            }

            if (settings?.MainSettings?.ShowPlaytimeLeaderboard == true && helper != null)
            {
                int places = settings.MainSettings.PlaytimeLeaderboardPlaces > 0 ? settings.MainSettings.PlaytimeLeaderboardPlaces : 5;
                string? leaderboard = helper.GetPlayTimeLeaderBoard(places, false, null, false, false);
                if (!string.IsNullOrWhiteSpace(leaderboard))
                {
                     const int MaxLeaderboardLength = 1000; // Leave some buffer for ``` and potential newlines
                     if (leaderboard.Length > MaxLeaderboardLength)
                     {
                         leaderboard = leaderboard.Substring(0, MaxLeaderboardLength - 15) + "... (truncated)```"; // Ensure closing ``` is included after truncation
                     }
                     // Use null forgiving ! as leaderboard is checked and potentially modified
                     embed.AddField("Top " + places + " Players by Play Time", leaderboard!, false);
                }
            }

            if (settings?.GameSpecificSettings?.ValheimJoinCode == true && !string.IsNullOrEmpty(valheimJoinCode) && appState == ApplicationState.Ready)
            {
                embed.AddField("Server Join Code", "```" + valheimJoinCode + "```");
            }

            if (settings?.MainSettings != null && !string.IsNullOrEmpty(settings.MainSettings.AdditionalEmbedFieldTitle))
            {
                embed.AddField(settings.MainSettings.AdditionalEmbedFieldTitle, settings.MainSettings.AdditionalEmbedFieldText ?? string.Empty);
            }

            embed.WithFooter(settings?.MainSettings?.BotTagline ?? string.Empty).WithCurrentTimestamp();

            var builder = new ComponentBuilder();

            if (settings?.MainSettings?.ShowStartButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Start", "start-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Success, disabled: application?.State == ApplicationState.Ready || application?.State == ApplicationState.Starting || application?.State == ApplicationState.Installing);
            }

            if (settings?.MainSettings?.ShowStopButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Stop", "stop-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Danger, disabled: application?.State == ApplicationState.Stopped || application?.State == ApplicationState.Failed);
            }

            if (settings?.MainSettings?.ShowRestartButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Restart", "restart-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Danger, disabled: application?.State == ApplicationState.Stopped || application?.State == ApplicationState.Failed);
            }

            if (settings?.MainSettings?.ShowKillButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Kill", "kill-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Danger, disabled: application?.State == ApplicationState.Stopped || application?.State == ApplicationState.Failed);
            }

            if (settings?.MainSettings?.ShowUpdateButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Update", "update-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Primary, disabled: application?.State == ApplicationState.Installing);
            }

            if (settings?.MainSettings?.ShowManageButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Manage", "manage-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Primary);
            }

            if (settings?.MainSettings?.ShowBackupButton == true && aMPInstanceInfo != null)
            {
                builder.WithButton("Backup", "backup-server-" + aMPInstanceInfo.InstanceId, ButtonStyle.Secondary);
            }

            if (updateExisting)
            {
                foreach (string details in settings?.MainSettings?.InfoMessageDetails ?? new List<string>())
                {
                    try
                    {
                        string[] split = details.Split('-');
                        if (split.Length < 3) {
                            log.Warning($"Skipping invalid InfoMessageDetails entry: {details}");
                            settings?.MainSettings?.InfoMessageDetails?.Remove(details); // Clean up invalid entry
                            continue;
                        }

                        // Break down the calls and add null checks
                        SocketGuild? targetGuild = bot?.client?.GetGuild(Convert.ToUInt64(split[0]));
                        SocketTextChannel? targetChannel = targetGuild?.GetTextChannel(Convert.ToUInt64(split[1]));
                        IUserMessage? existingMsg = null;
                        if (targetChannel != null)
                        {
                             // Use Try/Catch for GetMessageAsync as it can throw if message is deleted
                             try {
                                existingMsg = await targetChannel.GetMessageAsync(Convert.ToUInt64(split[2])) as IUserMessage;
                             } catch (Discord.Net.HttpException httpEx) when (httpEx.HttpCode == System.Net.HttpStatusCode.NotFound) {
                                 log.Warning($"Message {split[2]} not found in channel {targetChannel.Id}. Removing from settings.");
                                 settings?.MainSettings?.InfoMessageDetails?.Remove(details); // Clean up missing message
                                 continue; // Skip to next detail
                             } catch (Exception getMsgEx) {
                                 log.Error($"Error retrieving message {split[2]} in channel {targetChannel.Id}: {getMsgEx.Message}");
                                 continue; // Skip to next detail
                             }
                        }

                        if (existingMsg != null)
                        {
                            await existingMsg.ModifyAsync(x =>
                            {
                                // Ensure embed and builder are not null
                                if(embed != null) x.Embed = embed.Build();
                                if (split.Length <= 3 || !split[3].ToString().Equals("True"))
                                {
                                    if(builder != null) x.Components = builder.Build();
                                }
                            });
                        }
                        else
                        {
                            // Log if guild/channel was found but message wasn't (and wasn't caught above)
                             if (targetChannel != null) {
                                 log.Warning($"Could not find existing message ({split[2]}) in channel {targetChannel.Name} ({targetChannel.Id}). Removing from settings.");
                             } else if (targetGuild != null) {
                                 log.Warning($"Could not find channel ({split[1]}) in guild {targetGuild.Name} ({targetGuild.Id}). Removing message detail {details}.");
                             } else {
                                 log.Warning($"Could not find guild ({split[0]}) for message detail {details}. Removing.");
                             }
                             settings?.MainSettings?.InfoMessageDetails?.Remove(details); // Clean up invalid entry
                        }
                    }
                    catch (FormatException formatEx) {
                        log.Error($"Error parsing InfoMessageDetails entry '{details}': {formatEx.Message}. Removing entry.");
                        settings?.MainSettings?.InfoMessageDetails?.Remove(details); // Clean up malformed entry
                    }
                    catch (Exception ex)
                    {
                        // General catch-all for other potential issues with this entry
                        log.Error($"Error processing InfoMessageDetails entry '{details}': {ex.Message}");
                        settings?.MainSettings?.InfoMessageDetails?.Remove(details); // Attempt removal on error
                    }
                }
            }
            else
            {
                if (msg == null) {
                    log.Error("Cannot create new info panel: Interaction context (msg) is null.");
                    // Maybe throw an exception or return early?
                    // For now, log the error and potentially return to avoid NullReferenceException
                    return; 
                }

                // Use FollowupAsync since the interaction was likely deferred in the command handler
                log.Debug($"Sending followup response for interaction {msg.Id}");
                IUserMessage responseMessage;
                if (Buttonless)
                {
                    responseMessage = await msg.FollowupAsync(embed: embed.Build(), ephemeral: false); // Send without buttons
                }
                else
                {
                    responseMessage = await msg.FollowupAsync(embed: embed.Build(), components: builder.Build(), ephemeral: false); // Send with buttons
                }

                // Save message details only if the followup was successful
                if (responseMessage != null)
                {
                    log.Debug($"Followup successful. Message ID: {responseMessage.Id}. Saving details.");
                    var guildId = msg.GuildId ?? 0;
                    var channelId = msg.ChannelId ?? 0;
                    // Ensure settings and list are not null before adding
                    if (settings?.MainSettings?.InfoMessageDetails != null)
                    {
                         settings.MainSettings.InfoMessageDetails.Add($"{guildId}-{channelId}-{responseMessage.Id}-{Buttonless}");
                         config?.Save(settings); // Save updated settings
                         log.Info($"Added new info message detail: {guildId}-{channelId}-{responseMessage.Id}-{Buttonless}");
                    }
                    else {
                        log.Error("Could not save new message details: Settings or InfoMessageDetails list is null.");
                    }
                }
                else {
                     log.Error($"FollowupAsync did not return a message. Could not save details for interaction {msg.Id}.");
                }
            }
        }

        public async Task UpdateWebPanel(string webPanelPath)
        {
            if (string.IsNullOrEmpty(webPanelPath))
            {
                log.Error("WebPanel path is null or empty.");
                return;
            }

            log.Info(webPanelPath);

            while (settings?.MainSettings?.EnableWebPanel == true)
            {
                try
                {
                    Directory.CreateDirectory(webPanelPath);

                    string scriptFilePath = Path.Combine(webPanelPath, "script.js");
                    string stylesFilePath = Path.Combine(webPanelPath, "styles.css");
                    string panelFilePath = Path.Combine(webPanelPath, "panel.html");
                    string jsonFilePath = Path.Combine(webPanelPath, "panel.json");

                    ResourceReader reader = new ResourceReader("DiscordBotPlugin.Resources.Strings", System.Reflection.Assembly.GetExecutingAssembly());

                    string scriptContent = reader.ReadResource("script.js") ?? string.Empty;
                    if (!File.Exists(scriptFilePath) && !string.IsNullOrEmpty(scriptContent))
                        await File.WriteAllTextAsync(scriptFilePath, scriptContent);

                    string stylesContent = reader.ReadResource("styles.css") ?? string.Empty;
                    if (!File.Exists(stylesFilePath) && !string.IsNullOrEmpty(stylesContent))
                        await File.WriteAllTextAsync(stylesFilePath, stylesContent);

                    string panelHtmlContent = reader.ReadResource("panel.html") ?? string.Empty;
                    if (!File.Exists(panelFilePath) && !string.IsNullOrEmpty(panelHtmlContent))
                        await File.WriteAllTextAsync(panelFilePath, panelHtmlContent);

                    var cpuUsage = (application?.GetCPUUsage() ?? 0) + "%";
                    var memoryUsage = helper?.GetMemoryUsage() ?? "N/A";

                    IHasSimpleUserList hasSimpleUserList = application as IHasSimpleUserList;
                    var onlinePlayerCount = hasSimpleUserList?.Users?.Count ?? 0;
                    var maximumPlayers = hasSimpleUserList?.MaxUsers ?? 0;

                    var serverStatus = (application?.State == ApplicationState.Ready ? "✅ " : (application?.State == ApplicationState.Failed || application?.State == ApplicationState.Stopped ? "⛔ " : "⏳ ")) + (helper?.GetApplicationStateString() ?? "Unknown");
                    var serverStatusClass = application?.State == ApplicationState.Ready ? "ready" : (application?.State == ApplicationState.Failed || application?.State == ApplicationState.Stopped ? "stopped" : "pending");

                    var uptime = (application?.State == ApplicationState.Ready && application != null) ? string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}", DateTime.Now.Subtract(application.StartTime.ToLocalTime()).Days, DateTime.Now.Subtract(application.StartTime.ToLocalTime()).Hours, DateTime.Now.Subtract(application.StartTime.ToLocalTime()).Minutes, DateTime.Now.Subtract(application.StartTime.ToLocalTime()).Seconds) : "00:00:00:00";

                    var onlinePlayers = (settings?.MainSettings?.ShowOnlinePlayers == true && hasSimpleUserList?.Users != null) ? hasSimpleUserList.Users.Where(u => u != null && !string.IsNullOrEmpty(u.Name)).Select(u => u.Name!).ToArray() : Array.Empty<string>();

                    var playerCount = settings?.MainSettings?.ValidPlayerCount == true ? $"{onlinePlayerCount}/{maximumPlayers}" : string.Empty;

                    var playtimeLeaderBoard = (settings?.MainSettings?.ShowPlaytimeLeaderboard == true && helper != null)
                        ? helper.GetPlayTimeLeaderBoard(5, false, null, false, true)?.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>()
                        : Array.Empty<string>();

                    ServerInfo serverInfo = new ServerInfo
                    {
                        ServerName = settings.MainSettings.ServerDisplayName ?? "Unknown Server",
                        ServerIP = settings.MainSettings.ServerConnectionURL ?? "N/A",
                        ServerStatus = serverStatus,
                        ServerStatusClass = serverStatusClass,
                        CPUUsage = cpuUsage,
                        MemoryUsage = memoryUsage,
                        Uptime = uptime,
                        OnlinePlayers = onlinePlayers,
                        PlayerCount = playerCount,
                        PlaytimeLeaderBoard = playtimeLeaderBoard! // Add ! as ?? Array.Empty ensures non-null
                    };

                    string? json = JsonConvert.SerializeObject(serverInfo, Formatting.Indented); // Result can be null

                    // Check if json is null before writing
                    if (json != null)
                    {
                         await File.WriteAllTextAsync(jsonFilePath, json);
                    }
                    else
                    {
                         log.Warning("[WEBPANEL] JSON serialization resulted in null, skipping file write.");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Error updating web panel: {ex.Message}");
                }

                try {
                    // Wait before next update
                    await Task.Delay(10000);
                } catch (TaskCanceledException) {
                    log.Info("[WEBPANEL] UpdateWebPanel task loop cancelled.");
                    break; // Exit loop if task is cancelled
                } catch (Exception delayEx) {
                    log.Error($"[WEBPANEL] Unexpected error during Task.Delay in UpdateWebPanel: {delayEx.Message}");
                    await Task.Delay(5000); // Wait before retrying loop
                }
            }
             log.Info("[WEBPANEL] UpdateWebPanel task exiting (Web panel disabled or task cancelled).");
        }

        public async Task OnButtonPress(SocketMessageComponent arg)
        {
            // Defer the interaction immediately to prevent timeout
            await arg.DeferAsync();

            string buttonId = "unknown";
            string action = "unknown";

            // Null check for data before accessing CustomId
            if (arg.Data?.CustomId != null)
            {
                buttonId = arg.Data.CustomId;
                action = buttonId.Split('-')[0]; // Get action part
            }
            else
            {
                log.Warning("Button interaction received with null Data or CustomId.");
                await arg.FollowupAsync("Failed to process button click: Invalid interaction data.", ephemeral: true);
                return; // Cannot proceed without CustomId
            }

            var startTime = DateTime.UtcNow;
            // Initial log with all relevant IDs
            log.Info($"[BTN][{startTime:O}] Received button interaction: User={arg.User!.Username}({arg.User.Id}), Guild={(arg.Channel as SocketGuildChannel)?.Guild.Id}, Channel={arg.ChannelId}, MessageId={arg.Message!.Id}, ButtonId={arg.Data!.CustomId}");

            try
            {
                if (arg.User is not SocketGuildUser user)
                {
                    log.Warning($"[BTN][{DateTime.UtcNow:O}] Button pressed by non-guild user {arg.User.Username} ({arg.User.Id}). Button ID: {arg.Data.CustomId}");
                    await arg.FollowupAsync("This button can only be used within a server.", ephemeral: true);
                    return;
                }

                // Permission Check
                log.Debug($"[BTN][{DateTime.UtcNow:O}] Performing permission check for user {user.Username} (ID: {user.Id}) for button {arg.Data.CustomId}. Required Role(s): '{settings.MainSettings.DiscordRole}', Restrict Functions: {settings.MainSettings.RestrictFunctions}");
                bool hasServerPermission = bot?.HasServerPermission(user) ?? false;
                log.Info($"[BTN][{DateTime.UtcNow:O}] Permission check result for user {user.Username}: {hasServerPermission}");

                if (!hasServerPermission)
                {
                    log.Warning($"[BTN][{DateTime.UtcNow:O}] User {user.Username} lacks permission for button {arg.Data.CustomId}. Responding with permission denied.");
                    // Respond ephemerally that they don't have permission
                    await arg.FollowupAsync("You do not have permission to use this button.", ephemeral: true);
                    log.Info($"[BTN][{DateTime.UtcNow:O}] Permission denied response sent for button {arg.Data.CustomId}. Total time: {(DateTime.UtcNow - startTime).TotalMilliseconds}ms.");
                    return;
                }

                // Execute Action based on Button ID
                var actionStartTime = DateTime.UtcNow;
                log.Debug($"[BTN][{actionStartTime:O}] Executing logic for button ID '{buttonId}'...");
                string actionCompleted = "Unknown action";

                switch (buttonId)
                {
                    case "start-server":
                        log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling Task.Run(() => application.Start())...");
                        try {
                             await Task.Run(() => application?.Start());
                            log.Info($"[BTN][{DateTime.UtcNow:O}] Task.Run(application.Start) completed.");
                        } catch (Exception taskEx) {
                            log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION during Task.Run(application.Start): {taskEx.Message}");
                            actionCompleted = "Start Server Failed";
                            await arg.FollowupAsync("An error occurred trying to start the application.", ephemeral: true);
                             // Optionally, still log the attempt via ButtonResponse if desired, but actionCompleted reflects failure
                             // await bot.ButtonResponse(actionCompleted, arg);
                             return; // Stop processing further for this button
                        }
                        actionCompleted = "Start Server";
                        break;
                    case "stop-server":
                         log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling Task.Run(() => application.Stop())...");
                        try {
                            await Task.Run(() => application?.Stop());
                            log.Info($"[BTN][{DateTime.UtcNow:O}] Task.Run(application.Stop) completed.");
                        } catch (Exception taskEx) {
                             log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION during Task.Run(application.Stop): {taskEx.Message}");
                            actionCompleted = "Stop Server Failed";
                            await arg.FollowupAsync("An error occurred trying to stop the application.", ephemeral: true);
                             return;
                         }
                        actionCompleted = "Stop Server";
                        break;
                    case "restart-server":
                         log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling Task.Run(() => application.Restart())...");
                         try {
                            await Task.Run(() => application?.Restart());
                            log.Info($"[BTN][{DateTime.UtcNow:O}] Task.Run(application.Restart) completed.");
                         } catch (Exception taskEx) {
                            log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION during Task.Run(application.Restart): {taskEx.Message}");
                            actionCompleted = "Restart Server Failed";
                            await arg.FollowupAsync("An error occurred trying to restart the application.", ephemeral: true);
                             return;
                        }
                        actionCompleted = "Restart Server";
                        break;
                    case "kill-server":
                        log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling Task.Run(() => application.Kill())...");
                         try {
                            await Task.Run(() => application?.Kill());
                            log.Info($"[BTN][{DateTime.UtcNow:O}] Task.Run(application.Kill) completed.");
                         } catch (Exception taskEx) {
                             log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION during Task.Run(application.Kill): {taskEx.Message}");
                            actionCompleted = "Kill Server Failed";
                            await arg.FollowupAsync("An error occurred trying to kill the application.", ephemeral: true);
                            return;
                         }
                        actionCompleted = "Kill Server";
                        break;
                    case "update-server":
                         log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling Task.Run(() => application.Update())...");
                        try {
                             await Task.Run(() => application?.Update());
                             log.Info($"[BTN][{DateTime.UtcNow:O}] Task.Run(application.Update) completed.");
                         } catch (Exception taskEx) {
                            log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION during Task.Run(application.Update): {taskEx.Message}");
                            actionCompleted = "Update Server Failed";
                             await arg.FollowupAsync("An error occurred trying to update the application.", ephemeral: true);
                            return;
                        }
                        actionCompleted = "Update Server";
                        break;
                    case "manage-server":
                        log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling commands.ManageServer...");
                         try {
                            await commands.ManageServer(arg); // This sends a DM
                            log.Info($"[BTN][{DateTime.UtcNow:O}] commands.ManageServer completed.");
                         } catch (Exception taskEx) {
                             log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION calling commands.ManageServer: {taskEx.Message}");
                             actionCompleted = "Manage Server Failed";
                             // Don't send followup here as ManageServer should have handled its own errors if possible
                             return;
                         }
                        actionCompleted = "Manage Server Link Sent";
                        break;
                    case "backup-server":
                         log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling commands.BackupServer...");
                         try {
                             // Check commands before calling
                             if (commands != null) {
                                // Pass non-null user
                                commands.BackupServer(user!);
                                log.Info($"[BTN][{DateTime.UtcNow:O}] commands.BackupServer call initiated.");
                             }
                         } catch (Exception taskEx) {
                             log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION calling commands.BackupServer: {taskEx.Message}");
                             actionCompleted = "Backup Server Failed";
                             await arg.FollowupAsync("An error occurred trying to initiate the backup.", ephemeral: true);
                             return;
                         }
                        actionCompleted = "Backup Server Requested";
                        break;
                    default:
                        log.Warning($"[BTN][{DateTime.UtcNow:O}] Unknown button ID pressed: {buttonId}");
                        await arg.FollowupAsync("Unknown button action.", ephemeral: true);
                        return;
                }

                // Send log response if enabled (already uses bot.ButtonResponse which now has logging)
                log.Debug($"[BTN][{DateTime.UtcNow:O}] Calling bot.ButtonResponse for action '{actionCompleted}'...");
                await bot!.LogButtonActionAsync(actionCompleted!, arg!);

                // Send a final confirmation to the user who pressed the button
                // (Except for Manage/Backup which send DMs)
                if (buttonId != "manage-server" && buttonId != "backup-server") {
                    log.Debug($"[BTN][{DateTime.UtcNow:O}] Sending final FollowupAsync confirmation for action '{actionCompleted}'...");
                    try {
                         await arg.FollowupAsync($"Action '{actionCompleted!}' initiated.", ephemeral: true);
                    } catch (Exception followupEx) {
                         log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION sending final confirmation followup for button {arg.Data.CustomId}: {followupEx.Message}");
                    }
                }

                var endTime = DateTime.UtcNow;
                log.Info($"[BTN][{endTime:O}] Successfully processed button {arg.Data.CustomId} for user {user.Username}. Action: {actionCompleted}. Total time: {(endTime - startTime).TotalMilliseconds}ms.");
            }
            catch (Exception ex)
            {
                log.Error($"[BTN][{DateTime.UtcNow:O}] EXCEPTION occurred while handling button interaction {arg?.Data?.CustomId} (User: {arg?.User?.Username}): {ex.Message}");
                log.Error($"[BTN][{DateTime.UtcNow:O}] Full exception details: {ex}");
                // Try to inform the user
                // Check if we can still respond to the interaction before trying FollowupAsync
                bool canRespond = false;
                try {
                    if (arg != null) {
                        var originalResponse = await arg.GetOriginalResponseAsync();
                        canRespond = originalResponse != null;
                    }
                } catch {
                    // Ignore exceptions trying to get the original response
                     log.Warning($"[BTN][{DateTime.UtcNow:O}] Could not get original response for button {arg?.Data?.CustomId}, cannot send error followup.");
                }

                if (canRespond)
                {
                    try
                    {
                         await arg.FollowupAsync("An error occurred while processing this button press. Please check the bot logs.", ephemeral: true);
                     }
                     catch (Exception followupEx)
                     {
                         log.Error($"[BTN][{DateTime.UtcNow:O}] Exception occurred while trying to send error followup for button {arg?.Data?.CustomId}: {followupEx.Message}");
                     }
                } else {
                    log.Warning($"[BTN][{DateTime.UtcNow:O}] Interaction {arg?.Data?.CustomId} already responded to or timed out, skipping error followup.");
                }
            }
        }

        private string GetPlayerDataJson()
        {
             // Add null check for playerPlayTimes
             var playersList = playerPlayTimes?.Values
                                             .Where(p => p != null)
                                             // Add null check for player name
                                             .Select(p => new { PlayerName = p!.PlayerName ?? "(Error)", JoinTime = p.JoinTime.ToString("o"), LeaveTime = (p.LeaveTime == DateTime.MinValue ? (string?)null : p.LeaveTime.ToString("o")) })
                                             .ToList();
            // Handle null playersList by returning empty JSON array
            return playersList != null ? JsonConvert.SerializeObject(playersList) : "[]";
        }

        // User/Status info not directly available in args, comment out DM logic for now
        public async Task BackupStatusChangedHandler(object? sender, LocalFileBackupPlugin.BackupStatusChangeEventArgs e)
        {
            log.Debug($"BackupStatusChangedHandler triggered. Action: {e?.Action}, Reason: {e?.Reason}");
            // Add await Task.CompletedTask to satisfy CS1998
            await Task.CompletedTask;
            // if (e == null || e.UserSnowflake == 0) return;
            // var user = await bot?.client?.GetUserAsync(e.UserSnowflake);
            // if (user != null)
            // {
            //     await user.SendMessageAsync(e.StatusMessage); // StatusMessage doesn't exist
            // }
        }

        // User/DownloadUrl info not directly available in args, comment out DM logic for now
        public async void BackupCompleteHandler(object? sender, LocalFileBackupPlugin.BackupStatusChangeEventArgs e)
        {
             log.Debug($"BackupCompleteHandler triggered. Action: {e?.Action}, Reason: {e?.Reason}");
             // Add await Task.CompletedTask to satisfy CS1998
             await Task.CompletedTask;
             // BackupManifest manifest = e?.Manifest; // Manifest IS available
             // log.Info($"Backup completed. Manifest ID: {manifest?.Id}, Filename: {manifest?.Filename}");
            // if (e == null || e.UserSnowflake == 0) return;
            // var user = await bot?.client?.GetUserAsync(e.UserSnowflake);
            //  if (user != null)
            // {
            //     await user.SendMessageAsync("Backup process completed.");
            //      if (!string.IsNullOrEmpty(e.DownloadUrl)) // DownloadUrl doesn't exist
            //      {
            //          await user.SendMessageAsync($"Download link (expires soon): {e.DownloadUrl}");
            //      }
            // }
        }

        // User info not directly available in args, comment out DM logic for now
        public async void BackupFailedHandler(object? sender, LocalFileBackupPlugin.BackupStatusChangeEventArgs e)
        {
             log.Debug($"BackupFailedHandler triggered. Action: {e?.Action}, Reason: {e?.Reason}");
            // Add await Task.CompletedTask to satisfy CS1998
            await Task.CompletedTask;
            // if (e == null || e.UserSnowflake == 0) return;
            //  var user = await bot?.client?.GetUserAsync(e.UserSnowflake);
            // if (user != null)
            // {
            //     // Use the Reason property from BackupStatusChangeEventArgs
            //     await user.SendMessageAsync($"Backup failed: {e.Reason ?? "Unknown reason"}");
            // }
        }
    }
}
