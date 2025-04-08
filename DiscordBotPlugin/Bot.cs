using Discord.WebSocket;
using Discord;
using ModuleShared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Net.Http;
using static DiscordBotPlugin.PluginMain;
using System.Collections.Concurrent;

namespace DiscordBotPlugin
{
    internal class Bot
    {
        public DiscordSocketClient? client;
        private readonly Settings settings;
        private readonly IAMPInstanceInfo aMPInstanceInfo;
        private readonly IApplicationWrapper application;
        private readonly ILogger log;
        private Events? events;
        private readonly Helpers helper;
        private readonly InfoPanel infoPanel;
        private readonly Commands commands;

        // Dictionary for command handlers
        private Dictionary<string, Func<SocketSlashCommand, Task>> _commandHandlers = null!;

        public Bot(Settings settings, IAMPInstanceInfo aMPInstanceInfo, IApplicationWrapper application, ILogger log, Events? events, Helpers helper, InfoPanel infoPanel, Commands commands)
        {
            this.settings = settings;
            this.aMPInstanceInfo = aMPInstanceInfo;
            this.application = application;
            this.log = log;
            this.events = events;
            this.helper = helper;
            this.infoPanel = infoPanel;
            this.commands = commands;

            InitializeCommandHandlers();
        }

        // Method to initialize the command handler dictionary
        private void InitializeCommandHandlers()
        {
            _commandHandlers = new Dictionary<string, Func<SocketSlashCommand, Task>>(StringComparer.OrdinalIgnoreCase)
            {
                { "info", HandleInfoCommandAsync },
                { "start-server", HandleStartServerCommandAsync },
                { "stop-server", HandleStopServerCommandAsync },
                { "restart-server", HandleRestartServerCommandAsync },
                { "kill-server", HandleKillServerCommandAsync },
                { "update-server", HandleUpdateServerCommandAsync },
                { "console", HandleConsoleCommandAsync },
                { "show-playtime", HandleShowPlaytimeCommandAsync },
                { "full-playtime-list", HandleFullPlaytimeListCommandAsync },
                { "take-backup", HandleTakeBackupCommandAsync }
                // Add other commands here if needed
            };
             log.Info($"Initialized {_commandHandlers.Count} command handlers.");
        }

        public void SetEvents(Events events)
        {
            this.events = events;
        }

        // Use ConcurrentQueue for thread-safe console output buffering
        public ConcurrentQueue<string> consoleOutput = new ConcurrentQueue<string>();

        /// <summary>
        /// Async task to handle the Discord connection and call the status check
        /// </summary>
        /// <param name="BotToken">Discord Bot Token</param>
        /// <returns>Task</returns>
        public async Task ConnectDiscordAsync(string BotToken)
        {
            if (string.IsNullOrEmpty(BotToken))
            {
                log.Error("Bot token is not provided.");
                return;
            }

            DiscordSocketConfig config;

            if (client == null)
            {
                // Initialize DiscordSocketClient with the necessary config
                config = settings.MainSettings.SendChatToDiscord || settings.MainSettings.SendDiscordChatToServer
                    ? new DiscordSocketConfig { GatewayIntents = GatewayIntents.DirectMessages | GatewayIntents.GuildMessages | GatewayIntents.Guilds | GatewayIntents.MessageContent }
                    : new DiscordSocketConfig { GatewayIntents = GatewayIntents.DirectMessages | GatewayIntents.GuildMessages | GatewayIntents.Guilds };

                // Handle mismatch timezones
                config.UseInteractionSnowflakeDate = false;

                if (settings.MainSettings.DiscordDebugMode)
                    config.LogLevel = LogSeverity.Debug;

                client = new DiscordSocketClient(config);

                // Attach event handlers for logs and events
                if (events != null) { // Check if events is not null
                client.Log += events.Log;
                }
                client.ButtonExecuted += infoPanel.OnButtonPress;
                client.Ready += ClientReady;
                client.SlashCommandExecuted += SlashCommandHandler;
                if (settings.MainSettings.SendChatToDiscord || settings.MainSettings.SendDiscordChatToServer)
                    client.MessageReceived += MessageHandler;
            }

            try
            {
                await client.LoginAsync(TokenType.Bot, BotToken);
                await client.StartAsync();
                log.Info("Bot successfully connected.");

                _ = SetStatus();
                _ = ConsoleOutputSend();
                _ = infoPanel.UpdateWebPanel(Path.Combine(Environment.CurrentDirectory, "WebPanel-" + aMPInstanceInfo.InstanceName));

                await Task.Delay(-1); // Blocks task until stopped
            }
            catch (Exception ex)
            {
                log.Error("Error during bot connection: " + ex.Message);
            }
        }

        public async Task UpdatePresence(object? sender, ApplicationStateChangeEventArgs? args, bool force = false)
        {
            if (client == null)
            {
                log.Warning("Bot client is not connected. Cannot update presence.");
                return;
            }

            if (settings.MainSettings.BotActive && (args == null || args.PreviousState != args.NextState || force) && client?.ConnectionState == ConnectionState.Connected)
            {
                try
                {

                    string currentActivity = client?.Activity?.Name ?? "";
                    if (currentActivity != "")
                    {
                        var customStatus = client?.Activity as CustomStatusGame;
                        if (customStatus != null)
                        {
                            currentActivity = customStatus.State;
                        }
                    }
                    UserStatus currentStatus = client?.Status ?? UserStatus.Offline;
                    UserStatus status;

                    // Get the current user and max user count
                    IHasSimpleUserList? hasSimpleUserList = application as IHasSimpleUserList;
                    var onlinePlayers = hasSimpleUserList?.Users?.Count ?? 0;
                    var maximumPlayers = hasSimpleUserList?.MaxUsers ?? 0;

                    // If the server is stopped or in a failed state, set the presence to DoNotDisturb
                    if (application.State == ApplicationState.Stopped || application.State == ApplicationState.Failed)
                    {
                        status = UserStatus.DoNotDisturb;

                        // If there are still players listed in the timer, remove them
                        if (infoPanel.playerPlayTimes.Count != 0)
                            helper.ClearAllPlayTimes();
                    }
                    // If the server is running, set presence to Online
                    else if (application.State == ApplicationState.Ready)
                    {
                        status = UserStatus.Online;
                    }
                    // For everything else, set to Idle
                    else
                    {
                        status = UserStatus.Idle;

                        // If there are still players listed in the timer, remove them
                        if (infoPanel.playerPlayTimes.Count != 0)
                            helper.ClearAllPlayTimes();
                    }

                    if (status != currentStatus)
                    {
                        await client?.SetStatusAsync(status);
                    }

                    string presenceString = helper.OnlineBotPresenceString(onlinePlayers, maximumPlayers) ?? string.Empty;

                    // Set the presence/activity based on the server state
                    if (application.State == ApplicationState.Ready)
                    {
                        string stateString = application.State.ToString() ?? "Unknown";
                        if (currentActivity != stateString)
                        {
                            await client?.SetActivityAsync(new CustomStatusGame(stateString));
                        }
                    }
                    else
                    {
                        if (currentActivity != presenceString)
                        {
                            await client?.SetActivityAsync(new CustomStatusGame(presenceString));
                        }
                    }
                }
                catch (System.Net.WebException exception)
                {
                    await client?.SetGameAsync("Server Offline", null, ActivityType.Watching);
                    await client?.SetStatusAsync(UserStatus.DoNotDisturb);
                    log.Error("Exception: " + exception.Message);
                }
            }
        }

        /// <summary>
        /// Looping task to update bot status/presence
        /// </summary>
        /// <returns></returns>
        public async Task SetStatus()
        {
            // While the bot is active, update its status
            while (settings?.MainSettings?.BotActive == true && client != null)
            {
                try
                {

                    // Get the current user and max user count
                    IHasSimpleUserList? hasSimpleUserList = application as IHasSimpleUserList;
                    var onlinePlayers = hasSimpleUserList?.Users?.Count ?? 0;
                    var maximumPlayers = hasSimpleUserList?.MaxUsers ?? 0;

                    var clientConnectionState = client?.ConnectionState.ToString() ?? "Disconnected";
                    log.Debug("Server Status: " + application.State + " || Players: " + onlinePlayers + "/" + maximumPlayers + " || CPU: " + application.GetCPUUsage() + "% || Memory: " + helper.GetMemoryUsage() + ", Bot Connection Status: " + clientConnectionState);

                    // Update the embed if it exists
                    if (settings.MainSettings.InfoMessageDetails != null && settings.MainSettings.InfoMessageDetails.Count > 0)
                    {
                        _ = infoPanel.GetServerInfo(true, null, false);
                    }

                    //change presence if required
                    if (client?.ConnectionState == ConnectionState.Connected)
                    {
                    _ = UpdatePresence(null, null, true);
                    }
                }
                catch (System.Net.WebException exception)
                {
                    await client?.SetGameAsync("Server Offline", null, ActivityType.Watching);
                    await client?.SetStatusAsync(UserStatus.DoNotDisturb);
                    log.Error("Exception: " + exception.Message);
                }

                // Loop the task according to the bot refresh interval setting
                try {
                await Task.Delay(settings.MainSettings.BotRefreshInterval * 1000);
                 } catch (TaskCanceledException) {
                     log.Info("[STATUS] SetStatus task loop cancelled.");
                     break; // Exit loop if task is cancelled
                 } catch (Exception delayEx) {
                     log.Error($"[STATUS] Unexpected error during Task.Delay in SetStatus: {delayEx.Message}");
                     // Optional: Break or continue depending on desired behavior after delay error
                     await Task.Delay(5000); // Wait a bit before retrying loop
                 }
            }
             log.Info("[STATUS] SetStatus task exiting.");
        }

        /// <summary>
        /// Sets up and registers application commands for the client.
        /// </summary>
        public async Task ClientReady()
        {
            // Create lists to store command properties and command builders
            List<ApplicationCommandProperties> applicationCommandProperties = new List<ApplicationCommandProperties>();
            List<SlashCommandBuilder> commandList = new List<SlashCommandBuilder>();

            if (settings.MainSettings.RemoveBotName)
            {
                // Add individual commands to the command list
                commandList.Add(new SlashCommandBuilder()
                    .WithName("info")
                    .WithDescription("Create the Server Info Panel")
                    .AddOption("nobuttons", ApplicationCommandOptionType.Boolean, "Hide buttons for this panel?", isRequired: false));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("start-server")
                    .WithDescription("Start the Server"));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("stop-server")
                    .WithDescription("Stop the Server"));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("restart-server")
                    .WithDescription("Restart the Server"));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("kill-server")
                    .WithDescription("Kill the Server"));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("update-server")
                    .WithDescription("Update the Server"));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("show-playtime")
                    .WithDescription("Show the Playtime Leaderboard")
                    .AddOption("playername", ApplicationCommandOptionType.String, "Get playtime for a specific player", isRequired: false));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("console")
                    .WithDescription("Send a Console Command to the Application")
                    .AddOption("value", ApplicationCommandOptionType.String, "Command text", isRequired: true));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("full-playtime-list")
                    .WithDescription("Full Playtime List")
                    .AddOption("playername", ApplicationCommandOptionType.String, "Get info for a specific player", isRequired: false));

                commandList.Add(new SlashCommandBuilder()
                    .WithName("take-backup")
                    .WithDescription("Take a backup of the instance"));
            }
            else
            {
                if (client != null && client.CurrentUser != null)
                {
                    string botName = client.CurrentUser.Username?.ToLower() ?? "ampbot";

                    // Replace any spaces with '-'
                    botName = Regex.Replace(botName, "[^a-zA-Z0-9]", String.Empty);
                    if (string.IsNullOrWhiteSpace(botName)) { botName = "ampbot"; }

                    log.Info("Base command for bot: " + botName);

                    // Create the base bot command with subcommands
                    SlashCommandBuilder baseCommand = new SlashCommandBuilder()
                        .WithName(botName)
                        .WithDescription("Base bot command");

                    // Add subcommands to the base command
                    baseCommand.AddOption(new SlashCommandOptionBuilder()
                        .WithName("info")
                        .WithDescription("Create the Server Info Panel")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("nobuttons", ApplicationCommandOptionType.Boolean, "Hide buttons for this panel?", isRequired: false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("start-server")
                        .WithDescription("Start the Server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("stop-server")
                        .WithDescription("Stop the Server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("restart-server")
                        .WithDescription("Restart the Server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("kill-server")
                        .WithDescription("Kill the Server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("update-server")
                        .WithDescription("Update the Server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("show-playtime")
                        .WithDescription("Show the Playtime Leaderboard")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("playername", ApplicationCommandOptionType.String, "Get playtime for a specific player", isRequired: false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("console")
                        .WithDescription("Send a Console Command to the Application")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("value", ApplicationCommandOptionType.String, "Command text", isRequired: true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("full-playtime-list")
                        .WithDescription("Full Playtime List")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("playername", ApplicationCommandOptionType.String, "Get info for a specific player", isRequired: false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("take-backup")
                        .WithDescription("Take a backup of the instance")
                        .WithType(ApplicationCommandOptionType.SubCommand));

                    // Add the base command to the command list
                    commandList.Add(baseCommand);
                }
                else
                {
                    log.Error("Client or CurrentUser is null in ClientReady method.");
                    return;
                }
            }

            try
            {
                // Build the application command properties from the command builders
                foreach (SlashCommandBuilder command in commandList)
                {
                    applicationCommandProperties.Add(command.Build());
                }

                // Bulk overwrite the global application commands with the built command properties
                if (client != null && applicationCommandProperties.Any()) // Add null check for client
                {
                await client.BulkOverwriteGlobalApplicationCommandsAsync(applicationCommandProperties.ToArray());
                }
                else if (!applicationCommandProperties.Any())
                {
                     log.Warning("No application commands were built to register.");
                }
                else { // client must be null
                    log.Error("Cannot register application commands - Discord client is null.");
                }
            }
            catch (Exception exception)
            {
                log.Error(exception.Message);
            }

            // Perform initial validation of settings now that the client is ready and guilds are available
            _ = Task.Run(PerformInitialConfigurationValidationAsync);
        }

        /// <summary>
        /// Handles incoming socket messages.
        /// </summary>
        /// <param name="message">The incoming socket message.</param>
        public async Task MessageHandler(SocketMessage message)
        {
            // If sending Discord chat to server is disabled or the message is from a bot, return and do nothing further
            if (!settings.MainSettings.SendDiscordChatToServer || message.Author.IsBot)
                return;

            // Check if the message is in the specified chat-to-Discord channel
            if (message.Channel?.Name?.Equals(settings.MainSettings.ChatToDiscordChannel) == true || message.Channel?.Id.ToString() == settings.MainSettings.ChatToDiscordChannel) // Add ?. checks
            {
                // Send the chat command to the server
                await commands.SendChatCommand(message.Author.Username ?? "UnknownUser", message.CleanContent ?? string.Empty); // Add defaults
            }
        }

        /// <summary>
        /// Handles incoming socket slash commands.
        /// </summary>
        /// <param name="command">The incoming socket slash command.</param>
        public async Task SlashCommandHandler(SocketSlashCommand command)
        {
            var startTime = DateTime.UtcNow;
            log.Info($"[CMD][{startTime:O}] Received slash command interaction: User={command.User.Username}({command.User.Id}), Guild={command.GuildId}, Channel={command.ChannelId}, CommandId={command.CommandId}, CommandName={command.Data.Name}, Options={JsonConvert.SerializeObject(command.Data.Options)}");

            try
            {
                log.Debug($"[CMD][{startTime:O}] Attempting to defer command {command.CommandId} (Ephemeral=True)...");
                await command.DeferAsync(ephemeral: true);
                var deferTime = DateTime.UtcNow;
                log.Info($"[CMD][{deferTime:O}] Successfully deferred command {command.CommandId}. Time taken: {(deferTime - startTime).TotalMilliseconds}ms.");

                string commandName = command.Data.Name;
                string baseCommandName = command.Data.Name; // Keep original base command name
                object optionsSource = command.Data; // Top-level options for direct commands

                // Determine the actual command name and options source if using subcommands
                if (!settings.MainSettings.RemoveBotName && command.Data.Options?.Count > 0 && command.Data.Options.First().Type == ApplicationCommandOptionType.SubCommand)
                {
                    commandName = command.Data.Options.First().Name;
                    optionsSource = command.Data.Options.First(); // Use subcommand's options
                    log.Debug($"[CMD][{deferTime:O}] Using subcommand: Name={commandName}, OptionsSource=Subcommand");
                        }
                        else
                        {
                     log.Debug($"[CMD][{deferTime:O}] Using base command: Name={commandName}, OptionsSource=BaseCommand");
                }

                log.Debug($"[CMD][{deferTime:O}] Processing command '{commandName}' (Base: '{baseCommandName}') for user {command.User.Username} (ID: {command.User.Id})");

                // Permission check logic needs to be applied consistently
                bool needsPermissionCheck = commandName switch
                {
                    "info" => false, // Info panel creation is often public
                    "show-playtime" => false, // Playtime lookup is public
                    "full-playtime-list" => false, // Full playtime list lookup is public (but might be restricted based on needs)
                    _ => true // All other commands (start, stop, console, etc.) require permission
                };

                bool hasServerPermission = !needsPermissionCheck; // Assume permission if no check needed

                if (needsPermissionCheck)
                {
                    log.Debug($"[CMD][{DateTime.UtcNow:O}] Performing permission check for user {command.User.Username} (ID: {command.User.Id}) for command '{commandName}'. Required Role(s): '{settings.MainSettings.DiscordRole}', Restrict Functions: {settings.MainSettings.RestrictFunctions}");
                    if (command.User is SocketGuildUser user)
                    {
                        hasServerPermission = HasServerPermission(user);
                        log.Info($"[CMD][{DateTime.UtcNow:O}] Permission check result for user {command.User.Username}: {hasServerPermission}");
                            }
                            else
                            {
                        log.Warning($"[CMD][{DateTime.UtcNow:O}] Could not perform permission check for user {command.User.Username} as they are not a SocketGuildUser (likely a DM?). Assuming no permission.");
                        hasServerPermission = false; // Cannot check permissions in DM
                    }

                    if (!hasServerPermission)
                    {
                        log.Warning($"[CMD][{DateTime.UtcNow:O}] User {command.User.Username} lacks permission for command '{commandName}'. Responding with permission denied.");
                        await command.FollowupAsync("You do not have permission to use this command!", ephemeral: true);
                        log.Info($"[CMD][{DateTime.UtcNow:O}] Permission denied response sent for command {command.CommandId}. Total time: {(DateTime.UtcNow - startTime).TotalMilliseconds}ms.");
                        return; // Stop processing
                    }
                }
                else {
                    log.Debug($"[CMD][{DateTime.UtcNow:O}] Command '{commandName}' does not require a permission check.");
                }

                // --- Command Dispatch using Dictionary --- 
                var commandStartTime = DateTime.UtcNow;
                log.Debug($"[CMD][{commandStartTime:O}] Dispatching command '{commandName}'...");

                if (_commandHandlers.TryGetValue(commandName, out var handler))
                {
                     log.Debug($"[CMD][{DateTime.UtcNow:O}] Found handler for '{commandName}'. Invoking...");
                     try
                     {
                         // Pass the original command object to the handler
                         await handler(command);
                     }
                     catch (Exception handlerEx)
                     {
                         // Log exceptions thrown by the specific handler
                         log.Error($"[CMD][{DateTime.UtcNow:O}] EXCEPTION occurred within handler for command '{commandName}': {handlerEx.Message}");
                         log.Error($"[CMD][{DateTime.UtcNow:O}] Handler Exception Details: {handlerEx}");
                         // Attempt to notify user (handler should ideally do this, but catch here as fallback)
                        try {
                             if (command != null && (await command.GetOriginalResponseAsync()) != null) {
                                 await command.FollowupAsync($"An internal error occurred executing the '{commandName}' command. Please check logs.", ephemeral: true);
                             }
                        } catch {}
                     }
                     log.Info($"[CMD][{DateTime.UtcNow:O}] Handler for command '{commandName}' completed execution.");
                }
                else
                {
                     log.Warning($"[CMD][{DateTime.UtcNow:O}] No handler found for command name: '{commandName}'");
                     try {
                         await command.FollowupAsync($"Unknown command '{commandName}'.", ephemeral: true);
                     } catch {}
                }

                var endTime = DateTime.UtcNow;
                log.Info($"[CMD][{endTime:O}] Completed processing interaction for command '{commandName}' (Base: '{baseCommandName}'). Total time: {(endTime - startTime).TotalMilliseconds}ms.");

            }
            catch (Exception ex)
            {
                log.Error($"[CMD][{DateTime.UtcNow:O}] EXCEPTION occurred while handling slash command {command?.CommandId} (User: {command?.User?.Username}, Command: {command?.Data?.Name}): {ex.Message}");
                log.Error($"[CMD][{DateTime.UtcNow:O}] Full exception details: {ex}");
                // Try to inform the user, but this might fail if the interaction is already dead
                 // Check if we can still respond to the interaction before trying FollowupAsync
                bool canRespond = false;
                try {
                    if (command != null) {
                        var originalResponse = await command.GetOriginalResponseAsync();
                        canRespond = originalResponse != null;
                    }
                } catch {
                    // Ignore exceptions trying to get the original response, just means we likely can't respond.
                    log.Warning($"[CMD][{DateTime.UtcNow:O}] Could not get original response for command {command?.CommandId}, cannot send error followup.");
                }

                if (canRespond)
                {
                    try
                    {
                        await command.FollowupAsync("An error occurred while processing your command. Please check the bot logs.", ephemeral: true);
                    }
                    catch (Exception followupEx)
                    {
                        log.Error($"[CMD][{DateTime.UtcNow:O}] Exception occurred while trying to send error followup for command {command?.CommandId}: {followupEx.Message}");
                    }
                } else {
                     log.Warning($"[CMD][{DateTime.UtcNow:O}] Interaction {command?.CommandId} already responded to or timed out, skipping error followup.");
                }
            }
        }

        /// <summary>
        /// Retrieves the event channel from the specified guild by ID or name.
        /// </summary>
        /// <param name="guildID">The ID of the guild.</param>
        /// <param name="channel">The ID or name of the channel.</param>
        /// <returns>The event channel if found; otherwise, null.</returns>
        public SocketGuildChannel? GetEventChannel(ulong guildID, string channel)
        {
            if (client == null)
            {
                log.Error("Client is null in GetEventChannel.");
                return null;
            }

            var guild = client.GetGuild(guildID);

            if (guild == null)
            {
                log.Error($"Guild with ID {guildID} not found.");
                return null;
            }

            SocketGuildChannel? eventChannel;

            // Try by ID first
            try
            {
                if (ulong.TryParse(channel, out ulong channelId))
                {
                    eventChannel = client.GetGuild(guildID).Channels.FirstOrDefault(x => x.Id == channelId);
                }
                else
                {
                    eventChannel = null;
                }
            }
            catch
            {
                eventChannel = null; // Failed ID parse or GetGuild failed
            }

            // If not found by ID, try by name
            if (eventChannel == null && guild != null) // Add null check for guild
            {
                try
                {
                eventChannel = client.GetGuild(guildID).Channels.FirstOrDefault(x => x.Name == channel);
                }
                catch
                {
                    eventChannel = null; // GetGuild failed
                }
            }

            return eventChannel;
        }

        public bool HasServerPermission(SocketGuildUser user)
        {
            if (client != null)
            {
                // client.PurgeUserCache(); // REMOVED: Assuming Discord.Net cache is sufficient.
            }
            else
            {
                log.Warning("Client is null in HasServerPermission.");
            }

            if (settings != null)
            {
                // The user has the permission if either RestrictFunctions is turned off, or if they are part of the appropriate role.
                string[] roles = settings.MainSettings.DiscordRole.Split(',');
                return !settings.MainSettings.RestrictFunctions || user.Roles.Any(r => roles.Contains(r.Name)) || user.Roles.Any(r => roles.Contains(r.Id.ToString()));
            }
            else
            {
                log.Warning("Settings is null in HasServerPermission.");
                return false;
            }
        }

        public bool CanBotSendMessageInChannel(DiscordSocketClient client, ulong channelId)
        {
            if (client == null)
            {
                log.Error("Client is null in CanBotSendMessageInChannel");
                return false;
            }
            // Get the channel object from the channel ID
            var channel = client.GetChannel(channelId) as SocketTextChannel;

            if (channel == null)
            {
                Console.WriteLine("Channel not found or is not a text channel.");
                return false;
            }

            // Get the current user (the bot) as a user object within the context of the guild
            var botUser = channel.Guild.GetUser(client.CurrentUser.Id);

            // Get the bot's permissions in the channel
            var permissions = botUser.GetPermissions(channel);

            // Check if the bot has SendMessage permission in the channel
            return permissions.Has(ChannelPermission.SendMessages);
        }

        /// <summary>
        /// Handles button response and logs the command if enabled in settings.
        /// </summary>
        /// <param name="Command">Command received from the button.</param>
        /// <param name="arg">SocketMessageComponent object containing information about the button click.</param>
        public async Task ButtonResponse(string Command, SocketMessageComponent arg)
        {
            // Note: This method is primarily for LOGGING the button press if enabled.
            // The actual interaction deferral and followup happens in InfoPanel.OnButtonPress

            if (!settings.MainSettings.LogButtonsAndCommands)
                return;

             var startTime = DateTime.UtcNow;
             // Use the consistent logging prefix [BTN_LOG]
            log.Debug($"[BTN_LOG][{startTime:O}] Preparing button log response for action '{Command}' by user {arg.User.Username} (ButtonId: {arg.Data.CustomId})");

            SocketGuildChannel targetChannel;
            ulong guildId = (arg.Channel as SocketGuildChannel)?.Guild.Id ?? 0;

            if (guildId == 0) {
                log.Warning($"[BTN_LOG][{DateTime.UtcNow:O}] Cannot determine guild ID for button log (likely DM?). Button: {arg.Data.CustomId}");
                return;
            }

            // If a specific log channel is set, find it
            if (!string.IsNullOrEmpty(settings.MainSettings.ButtonResponseChannel))
            {
                targetChannel = GetEventChannel(guildId, settings.MainSettings.ButtonResponseChannel);
                 if (targetChannel == null)
                {
                    log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] ButtonResponseChannel '{settings.MainSettings.ButtonResponseChannel}' not found in guild {guildId}. Cannot log button press.");
                    return; // Can't find log channel
                }
                log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Found log channel: {targetChannel.Name} ({targetChannel.Id})");
            }
            else // Otherwise, use the channel where the button was pressed
            {
                targetChannel = arg.Channel as SocketGuildChannel;
                 if (targetChannel == null) { // Should already be caught by guildId check, but belt-and-suspenders
                     log.Warning($"[BTN_LOG][{DateTime.UtcNow:O}] Cannot log button response in non-guild channel for button {arg.Data.CustomId}.");
                     return;
                 }
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Using invoking channel for log: {targetChannel.Name} ({targetChannel.Id})");
            }

             // Determine colour based on action
            string colourHex = "";
            Color embedColor;
             switch (Command.ToLower().Split(' ')[0]) // Use first word for switch simplicity
            {
                 case "start": colourHex = settings.ColourSettings.ServerStartColour; break;
                 case "stop": colourHex = settings.ColourSettings.ServerStopColour; break;
                 case "restart": colourHex = settings.ColourSettings.ServerRestartColour; break;
                 case "kill": colourHex = settings.ColourSettings.ServerKillColour; break;
                 case "update": colourHex = settings.ColourSettings.ServerUpdateColour; break;
                 case "manage": colourHex = settings.ColourSettings.ManageLinkColour; break;
                 case "backup": colourHex = ""; break; // Backup has its own DM notifications, skip logging embed
                 default: colourHex = ""; break;
             }

             // Skip logging embed if backup (it has DM notifications)
             if (Command.ToLower().Contains("backup")) {
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Skipping log embed for backup action.");
                 return;
             }

            embedColor = !string.IsNullOrEmpty(colourHex) ? helper.GetColour(Command.Split(' ')[0], colourHex) : Color.Default;

            var builder = new EmbedBuilder();

            // Set the title and description of the embed based on the command
            if (Command == "Manage Server Link Sent") // Use the action string passed from OnButtonPress
            {
                builder.Title = "Manage Request Logged";
                builder.Description = $"`{arg.User.Username}` requested the manage server link.";
            }
            else
            {
                builder.Title = "Button Action Logged";
                builder.Description = $"`{arg.User.Username}` pressed the `{Command}` button.";
            }

            builder.Color = embedColor;
            builder.ThumbnailUrl = settings.MainSettings.GameImageURL;
            builder.AddField("Requested by", arg.User.Mention ?? "Unknown User", true);
            builder.WithFooter(settings.MainSettings.BotTagline ?? string.Empty);
            builder.WithCurrentTimestamp();

            try {
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Checking permissions to send log to Channel {targetChannel.Id}...");
                 if (client != null && CanBotSendMessageInChannel(client, targetChannel.Id))
                 {
                    await client.GetGuild(guildId).GetTextChannel(targetChannel.Id).SendMessageAsync(embed: builder.Build());
                    var endTime = DateTime.UtcNow;
                    log.Info($"[BTN_LOG][{endTime:O}] Button log embed sent for action '{Command}'. Total time: {(endTime - startTime).TotalMilliseconds}ms.");
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] EXCEPTION sending button log embed for action '{Command}' to channel {targetChannel?.Id}: {ex.Message}");
                log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] Full exception details: {ex}");
            }
        }

        /// <summary>
        /// Sends a chat message to the specified text channel in each guild the bot is a member of.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task ChatMessageSend(string Message)
        {
            if (client == null)
            {
                log.Error("Client is null in ChatMessageSend");
                return;
            }

            // Get all guilds the bot is a member of
            var guilds = client.Guilds;
            foreach (var (guildID, eventChannel) in
            // Iterate over each guild
            from SocketGuild guild in guilds// Find the text channel with the specified name
            let guildID = guild.Id
            let eventChannel = GetEventChannel(guildID, settings.MainSettings.ChatToDiscordChannel)
            where eventChannel != null
            select (guildID, eventChannel))
            {
                // Send the message to the channel
                await client.GetGuild(guildID).GetTextChannel(eventChannel.Id).SendMessageAsync("`" + Message + "`");
            }
        }

        /// <summary>
        /// Looping task to send batched console output
        /// </summary>
        /// <returns></returns>
        public async Task ConsoleOutputSend()
        {
            if (!settings.MainSettings.SendConsoleToDiscord || string.IsNullOrEmpty(settings.MainSettings.ConsoleToDiscordChannel)) {
                 log.Info("[CONSOLE_SEND] Console output sending is disabled or channel not set. Task exiting.");
                return;
            }

            while (client?.ConnectionState == ConnectionState.Connected && settings.MainSettings.SendConsoleToDiscord)
            {
                // Wait for a period before checking the queue
                 try {
                    await Task.Delay(5000); // Check every 5 seconds
                 } catch (TaskCanceledException) {
                     log.Info("[CONSOLE_SEND] ConsoleOutputSend task loop cancelled.");
                     break; // Exit loop if task is cancelled
                 } catch (Exception delayEx) {
                     log.Error($"[CONSOLE_SEND] Unexpected error during Task.Delay: {delayEx.Message}");
                     await Task.Delay(5000); // Wait before retrying loop
                     continue; // Skip rest of loop iteration on delay error
                 }

                if (!consoleOutput.IsEmpty) {
                    log.Debug($"[CONSOLE_SEND] Found {consoleOutput.Count} messages in console output queue.");
                    List<string> messagesToSend = new List<string>();
                    // Dequeue messages safely
                    while(consoleOutput.TryDequeue(out string message))
                    {
                        messagesToSend.Add(message);
                         // Avoid making the batch too large in one go
                         if (messagesToSend.Count >= 50) break;
                    }

                    if (messagesToSend.Any()) {
                         log.Debug($"[CONSOLE_SEND] Processing batch of {messagesToSend.Count} console messages.");
                         // Use helper to split potentially long output into multiple messages
                         List<string> outputBlocks = helper.SplitOutputIntoCodeBlocks(messagesToSend);
                         log.Debug($"[CONSOLE_SEND] Split into {outputBlocks.Count} message blocks.");

                        foreach (var guild in client.Guilds)
                        {
                             var consoleChannel = GetEventChannel(guild.Id, settings.MainSettings.ConsoleToDiscordChannel);
                             if (consoleChannel != null)
                             {
                                 bool canSend = client != null && CanBotSendMessageInChannel(client, consoleChannel.Id); // Add null check for client
                                 if (canSend)
                                 {
                                     log.Debug($"[CONSOLE_SEND] Sending {outputBlocks.Count} blocks to channel {consoleChannel.Name} ({consoleChannel.Id}) in guild {guild.Name} ({guild.Id})...");
                                     foreach (string block in outputBlocks)
                                    {
                                        try {
                                            await client.GetGuild(guild.Id).GetTextChannel(consoleChannel.Id).SendMessageAsync(block);
                                            // Optional: Short delay between sending blocks if rate limiting becomes an issue
                                            // await Task.Delay(500);
                                        } catch (Exception ex) {
                                            log.Error($"[CONSOLE_SEND] FAILED to send console output block to {consoleChannel.Name}: {ex.Message}");
                                            // Stop sending to this channel for this batch if an error occurs
                                            break;
                                        }
                                    }
                                    log.Info($"[CONSOLE_SEND] Finished sending console blocks to channel {consoleChannel.Name} ({consoleChannel.Id}).");
                                    // Assuming we only want to send to the first valid channel found across guilds per batch
                                    break; 
                                 }
                                 else if (consoleChannel != null) {
                                     log.Warning($"[CONSOLE_SEND] Found console channel '{consoleChannel.Name}' but cannot send messages to it (Permissions?). Guild: {guild.Name}");
                                 }
                            }
                        }
                    }
                }
            }
            log.Info("[CONSOLE_SEND] Console output sending task is exiting (Client disconnected or setting disabled).");
        }


        /// <summary>
        /// Show play time on the server
        /// </summary>
        /// <param name="msg">Command from Discord</param>
        /// <returns></returns>
        private async Task ShowPlayerPlayTime(SocketSlashCommand msg)
        {
            if (client == null || settings == null || helper == null)
            {
                log.Error("Cannot show player playtime: Client, settings, or helper is null.");
                if (msg != null) await msg.FollowupAsync("An internal error occurred.", ephemeral: true);
                return;
            }

            var startTime = DateTime.UtcNow;
            log.Debug($"[PLAYTIME][{startTime:O}] Generating playtime leaderboard for command {msg.CommandId} (User: {msg.User.Username})...");

            var builder = new EmbedBuilder();
            string leaderboard = helper.GetPlayTimeLeaderBoard(settings.MainSettings.PlaytimeLeaderboardPlaces, false, null, false, false);
            log.Debug($"[PLAYTIME][{DateTime.UtcNow:O}] Playtime data generated. Length: {leaderboard?.Length ?? 0}");


            string colourHex = settings?.ColourSettings?.PlaytimeLeaderboardColour;
            Color embedColor = !string.IsNullOrEmpty(colourHex) ? helper.GetColour("Leaderboard", colourHex) : Color.DarkGrey;

            builder.Title = "Play Time Leaderboard";
            builder.Description = leaderboard ?? "No playtime data available.";
            builder.Color = embedColor;
            builder.Footer = new EmbedFooterBuilder() { Text = settings.MainSettings.BotTagline ?? string.Empty };
            builder.Timestamp = DateTimeOffset.Now;

            log.Debug($"[PLAYTIME][{DateTime.UtcNow:O}] Sending playtime leaderboard embed for command {msg.CommandId}...");
             try {
                await msg.FollowupAsync(embed: builder.Build(), ephemeral: true);
             } catch (Exception followupEx) {
                 log.Error($"[PLAYTIME][{DateTime.UtcNow:O}] EXCEPTION sending playtime followup for command {msg.CommandId}: {followupEx.Message}");
                 // Don't try to send another followup here if this one failed.
             }
            var endTime = DateTime.UtcNow;
            log.Info($"[PLAYTIME][{endTime:O}] Playtime leaderboard sent for command {msg.CommandId}. Total time: {(endTime - startTime).TotalMilliseconds}ms.");
        }

        /// <summary>
        /// Logs the execution of a slash command to the configured log channel (if enabled).
        /// </summary>
        /// <param name="actionDescription">A description of the action performed (e.g., "Start Server", "`help` console").</param>
        /// <param name="command">The original SocketSlashCommand context.</param>
        public async Task LogCommandActionAsync(string actionDescription, SocketSlashCommand command)
        {
            if (!settings.MainSettings.LogButtonsAndCommands)
                return;

            if (command == null || command.Channel == null || command.User == null)
            {
                log.Error("Invalid arguments in LogCommandActionAsync.");
                return;
            }

            var startTime = DateTime.UtcNow;
            log.Debug($"[CMD_LOG][{startTime:O}] Preparing command log response for action '{actionDescription}' by user {command.User.Username} (CommandId: {command.CommandId})");

            var embed = new EmbedBuilder();

            // Set the title and description of the embed based on the command
            if (actionDescription.Equals("Manage Server", StringComparison.OrdinalIgnoreCase))
            {
                embed.Title = "Manage Request Logged";
                embed.Description = $"`{command.User.Username}` requested the manage server link.";
            }
            else if (actionDescription.Contains(" console", StringComparison.OrdinalIgnoreCase)) {
                 embed.Title = "Console Command Logged";
                 embed.Description = $"`{command.User.Username}` used the console command: {actionDescription.Split(new[] { " console" }, StringSplitOptions.None)[0]}"; // Extract command part
            }
             else if (actionDescription.Equals("Backup Server", StringComparison.OrdinalIgnoreCase)) {
                  embed.Title = "Backup Command Logged";
                  embed.Description = $"`{command.User.Username}` requested a server backup.";
            }
            else
            {
                embed.Title = "Server Command Logged";
                embed.Description = $"`{command.User.Username}` used the `{actionDescription}` command.";
            }

            // Set the embed color based on the command
             string colourHex = "";
             Color embedColor;
             string commandActionWord = actionDescription.Split(' ')[0].ToLower(); // Get first word for simpler switch
             if (commandActionWord == "`") { // Handle console command case specifically
                 commandActionWord = "console";
             }

             switch(commandActionWord)
             {
                case "start": colourHex = settings.ColourSettings.ServerStartColour; break;
                case "stop": colourHex = settings.ColourSettings.ServerStopColour; break;
                case "restart": colourHex = settings.ColourSettings.ServerRestartColour; break;
                case "kill": colourHex = settings.ColourSettings.ServerKillColour; break;
                case "update": colourHex = settings.ColourSettings.ServerUpdateColour; break;
                case "manage": colourHex = settings.ColourSettings.ManageLinkColour; break;
                case "console": colourHex = settings.ColourSettings.ConsoleCommandColour; break;
                case "backup": colourHex = ""; break; // Skip embed for backup
                default: colourHex = ""; break;
             }

            // Skip logging embed if backup (it has DM notifications)
            if (commandActionWord == "backup") {
                log.Debug($"[CMD_LOG][{DateTime.UtcNow:O}] Skipping log embed for backup action.");
                return;
            }

            embedColor = !string.IsNullOrEmpty(colourHex) ? helper.GetColour(commandActionWord, colourHex) : Color.Default;
            embed.Color = embedColor;

            embed.ThumbnailUrl = settings.MainSettings.GameImageURL;
            embed.AddField("Requested by", command.User.Mention ?? "Unknown User", true);
            embed.WithFooter(settings.MainSettings.BotTagline ?? string.Empty);
            embed.WithCurrentTimestamp();

            SocketGuildChannel targetChannel;
            ulong guildId = command.GuildId.GetValueOrDefault();
            if (guildId == 0) {
                log.Warning($"[CMD_LOG][{DateTime.UtcNow:O}] Cannot determine guild ID for command log (likely DM?). Command: {command.CommandId}");
                return;
            }

            // If a specific log channel is set, find it
            if (!string.IsNullOrEmpty(settings.MainSettings.ButtonResponseChannel))
            {
                targetChannel = GetEventChannel(guildId, settings.MainSettings.ButtonResponseChannel); // Removed bot?.
                 if (targetChannel == null)
                {
                    log.Error($"[CMD_LOG][{DateTime.UtcNow:O}] ButtonResponseChannel '{settings.MainSettings.ButtonResponseChannel}' not found in guild {guildId}. Cannot log command.");
                    return; // Can't find log channel
                }
                 log.Debug($"[CMD_LOG][{DateTime.UtcNow:O}] Found log channel: {targetChannel.Name} ({targetChannel.Id})");
            }
            else // Otherwise, use the channel where the command was invoked
            {
                targetChannel = command.Channel as SocketGuildChannel;
                 if (targetChannel == null) { // Should already be caught by guildId check
                     log.Warning($"[CMD_LOG][{DateTime.UtcNow:O}] Cannot log command response in non-guild channel for command {command.CommandId}.");
                     return;
                 }
                 log.Debug($"[CMD_LOG][{DateTime.UtcNow:O}] Using invoking channel for log: {targetChannel.Name} ({targetChannel.Id})");
            }

            try {
                 log.Debug($"[CMD_LOG][{DateTime.UtcNow:O}] Checking permissions to send log to Channel {targetChannel.Id}...");
                 if (client == null || !CanBotSendMessageInChannel(client, targetChannel.Id))
                 {
                    log.Error($"[CMD_LOG][{DateTime.UtcNow:O}] No permission to post command log embed to channel: {targetChannel.Name} ({targetChannel.Id})");
                    return;
                 }

                log.Debug($"[CMD_LOG][{DateTime.UtcNow:O}] Sending command log embed to channel {targetChannel.Id}...");
                await client.GetGuild(guildId).GetTextChannel(targetChannel.Id).SendMessageAsync(embed: embed.Build());
                var endTime = DateTime.UtcNow;
                log.Info($"[CMD_LOG][{endTime:O}] Command log embed sent for action '{actionDescription}'. Total time: {(endTime - startTime).TotalMilliseconds}ms.");
            }
             catch (Exception ex)
            {
                log.Error($"[CMD_LOG][{DateTime.UtcNow:O}] EXCEPTION sending command log embed for action '{actionDescription}' to channel {targetChannel?.Id}: {ex.Message}");
                log.Error($"[CMD_LOG][{DateTime.UtcNow:O}] Full exception details: {ex}");
            }
        }

        /// <summary>
        /// Logs the pressing of a button to the configured log channel (if enabled).
        /// </summary>
        /// <param name="actionDescription">The action performed (e.g., "Start Server", "Manage Server Link Sent").</param>
        /// <param name="arg">The SocketMessageComponent argument.</param>
        public async Task LogButtonActionAsync(string actionDescription, SocketMessageComponent arg)
        {
            // Note: This method is primarily for LOGGING the button press if enabled.
            // The actual interaction deferral and followup happens in InfoPanel.OnButtonPress

            if (!settings.MainSettings.LogButtonsAndCommands)
                return;

             var startTime = DateTime.UtcNow;
             // Use the consistent logging prefix [BTN_LOG]
            log.Debug($"[BTN_LOG][{startTime:O}] Preparing button log response for action '{actionDescription}' by user {arg.User.Username} (ButtonId: {arg.Data.CustomId})");

            SocketGuildChannel targetChannel;
            ulong guildId = (arg.Channel as SocketGuildChannel)?.Guild.Id ?? 0;

            if (guildId == 0) {
                log.Warning($"[BTN_LOG][{DateTime.UtcNow:O}] Cannot determine guild ID for button log (likely DM?). Button: {arg.Data.CustomId}");
                return;
            }

            // If a specific log channel is set, find it
            if (!string.IsNullOrEmpty(settings.MainSettings.ButtonResponseChannel))
            {
                targetChannel = GetEventChannel(guildId, settings.MainSettings.ButtonResponseChannel);
                 if (targetChannel == null)
                {
                    log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] ButtonResponseChannel '{settings.MainSettings.ButtonResponseChannel}' not found in guild {guildId}. Cannot log button press.");
                    return; // Can't find log channel
                }
                log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Found log channel: {targetChannel.Name} ({targetChannel.Id})");
            }
            else // Otherwise, use the channel where the button was pressed
            {
                targetChannel = arg.Channel as SocketGuildChannel;
                 if (targetChannel == null) { // Should already be caught by guildId check, but belt-and-suspenders
                     log.Warning($"[BTN_LOG][{DateTime.UtcNow:O}] Cannot log button response in non-guild channel for button {arg.Data.CustomId}.");
                     return;
                 }
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Using invoking channel for log: {targetChannel.Name} ({targetChannel.Id})");
            }

             // Determine colour based on action
            string colourHex = "";
            Color embedColor;
             string actionWord = actionDescription.Split(' ')[0].ToLower(); // Use first word for switch simplicity
             switch (actionWord)
            {
                 case "start": colourHex = settings.ColourSettings.ServerStartColour; break;
                 case "stop": colourHex = settings.ColourSettings.ServerStopColour; break;
                 case "restart": colourHex = settings.ColourSettings.ServerRestartColour; break;
                 case "kill": colourHex = settings.ColourSettings.ServerKillColour; break;
                 case "update": colourHex = settings.ColourSettings.ServerUpdateColour; break;
                 case "manage": colourHex = settings.ColourSettings.ManageLinkColour; break;
                 case "backup": colourHex = ""; break; // Backup has its own DM notifications, skip logging embed
                 default: colourHex = ""; break;
             }

             // Skip logging embed if backup (it has DM notifications)
             if (actionWord == "backup") {
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Skipping log embed for backup action.");
                 return;
             }

            embedColor = !string.IsNullOrEmpty(colourHex) ? helper.GetColour(actionWord, colourHex) : Color.Default;

            var builder = new EmbedBuilder();

            // Set the title and description of the embed based on the command
            if (actionDescription == "Manage Server Link Sent") // Use the action string passed from OnButtonPress
            {
                builder.Title = "Manage Request Logged";
                builder.Description = $"`{arg.User.Username}` requested the manage server link.";
            }
            else
            {
                builder.Title = "Button Action Logged";
                builder.Description = $"`{arg.User.Username}` pressed the `{actionDescription}` button.";
            }

            builder.Color = embedColor;
            builder.ThumbnailUrl = settings.MainSettings.GameImageURL;
            builder.AddField("Requested by", arg.User.Mention ?? "Unknown User", true);
            builder.WithFooter(settings.MainSettings.BotTagline ?? string.Empty);
            builder.WithCurrentTimestamp();

            try {
                 log.Debug($"[BTN_LOG][{DateTime.UtcNow:O}] Checking permissions to send log to Channel {targetChannel.Id}...");
                 if (client != null && CanBotSendMessageInChannel(client, targetChannel.Id))
                 {
                    await client.GetGuild(guildId).GetTextChannel(targetChannel.Id).SendMessageAsync(embed: builder.Build());
                    var endTime = DateTime.UtcNow;
                    log.Info($"[BTN_LOG][{endTime:O}] Button log embed sent for action '{actionDescription}'. Total time: {(endTime - startTime).TotalMilliseconds}ms.");
                }
            }
            catch (Exception ex)
            {
                log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] EXCEPTION sending button log embed for action '{actionDescription}' to channel {targetChannel?.Id}: {ex.Message}");
                log.Error($"[BTN_LOG][{DateTime.UtcNow:O}] Full exception details: {ex}");
            }
        }

        /// <summary>
        /// Validates if a configured channel identifier (name or ID) exists in at least one connected guild.
        /// </summary>
        /// <param name="channelIdentifier">The channel name or ID string from settings.</param>
        /// <param name="settingName">The name of the setting being validated (for logging).</param>
        /// <returns>True if the channel is found in at least one guild, false otherwise.</returns>
        public async Task<bool> ValidateChannelSettingAsync(string channelIdentifier, string settingName)
        {
            if (string.IsNullOrWhiteSpace(channelIdentifier)) {
                 log.Debug($"[VALIDATE] Skipping validation for empty setting '{settingName}'.");
                 return true; // Not configured, so technically valid in the sense of not being *invalidly* configured
            }

            if (client == null || client.ConnectionState != ConnectionState.Connected)
            {
                log.Warning($"[VALIDATE] Cannot validate setting '{settingName}' ('{channelIdentifier}') - Discord client not connected.");
                return false; // Cannot validate
            }

            log.Info($"[VALIDATE] Validating channel setting '{settingName}' ('{channelIdentifier}')...");
            bool found = false;
            // Use a temporary list to avoid issues if Guilds collection changes during iteration (unlikely but safer)
            var guilds = client?.Guilds.ToList() ?? new List<SocketGuild>(); // Add null check
            foreach (var guild in guilds)
            {
                SocketGuildChannel? channel = null;
                // Try parsing as ID first
                if (ulong.TryParse(channelIdentifier, out ulong channelId))
                {
                    try {
                        channel = guild.GetChannel(channelId);
                    } catch (Exception ex) {
                        log.Debug($"[VALIDATE] Exception checking channel ID {channelId} in guild {guild.Name}: {ex.Message}");
                        // Could be permissions issue or invalid ID, continue checking by name / other guilds
                    }
                }

                // If not found by ID, try by name
                if (channel == null)
                {
                    try {
                         channel = guild.Channels.FirstOrDefault(c => c.Name.Equals(channelIdentifier, StringComparison.OrdinalIgnoreCase));
                    } catch (Exception ex) {
                        log.Debug($"[VALIDATE] Exception checking channel name '{channelIdentifier}' in guild {guild.Name}: {ex.Message}");
                        // Continue checking other guilds
                    }
                }

                if (channel != null)
                {
                    log.Info($"[VALIDATE] Found channel '{channel.Name}' ({channel.Id}) for setting '{settingName}' in Guild '{guild.Name}' ({guild.Id}). Validation successful.");
                    found = true;
                    break; // Found in at least one guild, that's enough
                }
            }

            if (!found)
            {
                log.Error($"[VALIDATE] FAILED: Could not find channel '{channelIdentifier}' (for setting '{settingName}') in ANY connected guilds. Please check configuration.");
            }
            return found;
        }

        /// <summary>
        /// Validates if configured role identifiers (names or IDs) exist in at least one connected guild.
        /// Each specified role must exist in at least one guild (not necessarily the same one).
        /// </summary>
        /// <param name="roleIdentifiers">Comma-separated string of role names or IDs from settings.</param>
        /// <param name="settingName">The name of the setting being validated (for logging).</param>
        /// <returns>True if all specified roles are found in at least one guild each, false otherwise.</returns>
        public async Task<bool> ValidateRoleSettingAsync(string roleIdentifiers, string settingName)
        {
            if (string.IsNullOrWhiteSpace(roleIdentifiers)) {
                log.Debug($"[VALIDATE] Skipping validation for empty setting '{settingName}'.");
                 return true; // Not configured
            }

            if (client == null || client.ConnectionState != ConnectionState.Connected)
            {
                 log.Warning($"[VALIDATE] Cannot validate setting '{settingName}' ('{roleIdentifiers}') - Discord client not connected.");
                 return false; // Cannot validate
            }

            log.Info($"[VALIDATE] Validating role setting '{settingName}' ('{roleIdentifiers}')...");
            var rolesToFind = roleIdentifiers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).Distinct().ToList();
            if (!rolesToFind.Any()) {
                log.Debug($"[VALIDATE] No roles specified for setting '{settingName}'. Skipping detailed validation.");
                return true; // No roles listed is valid
            }

            List<string> rolesNotFound = new List<string>(rolesToFind);
            var guilds = client?.Guilds.ToList() ?? new List<SocketGuild>(); // Add null check

            foreach (var roleIdentifier in rolesToFind)
            {
                bool foundCurrentRole = false;
                 foreach (var guild in guilds)
                 {
                    SocketRole? role = null;
                     // Try parsing as ID first
                    if (ulong.TryParse(roleIdentifier, out ulong roleId))
                    {
                        try {
                            role = guild.GetRole(roleId);
                        } catch (Exception ex) {
                            log.Debug($"[VALIDATE] Exception checking role ID {roleId} in guild {guild.Name}: {ex.Message}");
                        }
                     }

                    // If not found by ID, try by name
                     if (role == null)
                     {
                        try {
                            role = guild.Roles.FirstOrDefault(r => r.Name.Equals(roleIdentifier, StringComparison.OrdinalIgnoreCase));
                        } catch (Exception ex) {
                            log.Debug($"[VALIDATE] Exception checking role name '{roleIdentifier}' in guild {guild.Name}: {ex.Message}");
                        }
                     }

                    if (role != null)
                    {
                         log.Debug($"[VALIDATE] Found role '{role.Name}' ({role.Id}) matching identifier '{roleIdentifier}' in Guild '{guild.Name}' ({guild.Id}).");
                         foundCurrentRole = true;
                         rolesNotFound.Remove(roleIdentifier); // Found it, remove from missing list
                         break; // Stop checking guilds for *this* role identifier
                     }
                 }
                 if (!foundCurrentRole) {
                     log.Warning($"[VALIDATE] Could not find role matching identifier '{roleIdentifier}' in ANY connected guilds.");
                 }
            }

            if (rolesNotFound.Any())
            {
                 log.Error($"[VALIDATE] FAILED: Could not find the following role(s) (for setting '{settingName}') in ANY connected guilds: {string.Join(", ", rolesNotFound)}. Please check configuration.");
                 return false;
            }
            else
            {
                 log.Info($"[VALIDATE] All specified roles for setting '{settingName}' were found in at least one guild. Validation successful.");
                return true;
            }
        }

        /// <summary>
        /// Performs validation of critical configuration settings after the bot is ready.
        /// </summary>
        public async Task PerformInitialConfigurationValidationAsync()
        {
             if (client?.ConnectionState != ConnectionState.Connected) {
                 log.Warning("[VALIDATE] Cannot perform configuration validation - client not connected.");
                 return;
             }
             if (!settings.MainSettings.BotActive) {
                 log.Info("[VALIDATE] Skipping configuration validation - Bot is not set to Active.");
                 return;
             }

            log.Info("[VALIDATE] Performing initial configuration validation...");
            bool allValid = true;

            // Validate Log Channel
            if (settings.MainSettings.LogButtonsAndCommands) {
                 if (!await ValidateChannelSettingAsync(settings.MainSettings.ButtonResponseChannel, "Button/Command Log Channel")) allValid = false;
            }
            // Validate Player Event Channel
             if (settings.MainSettings.PostPlayerEvents) {
                 if (!await ValidateChannelSettingAsync(settings.MainSettings.PostPlayerEventsChannel, "Player Events Channel")) allValid = false;
             }
            // Validate Chat Channel
            if (settings.MainSettings.SendChatToDiscord || settings.MainSettings.SendDiscordChatToServer) { // If either chat feature is enabled, channel must be valid
                 if (!await ValidateChannelSettingAsync(settings.MainSettings.ChatToDiscordChannel, "Chat Discord Channel")) allValid = false;
             }
            // Validate Console Channel
            if (settings.MainSettings.SendConsoleToDiscord) {
                 if (!await ValidateChannelSettingAsync(settings.MainSettings.ConsoleToDiscordChannel, "Console Discord Channel")) allValid = false;
             }
             // Validate Roles
             if (settings.MainSettings.RestrictFunctions) {
                 if (!await ValidateRoleSettingAsync(settings.MainSettings.DiscordRole, "Discord Role Name(s)/ID(s)")) allValid = false;
             }

            if (allValid)
            {
                 log.Info("[VALIDATE] Initial configuration validation complete. All checked settings appear valid.");
            }
            else
            {
                log.Error("[VALIDATE] Initial configuration validation FAILED. One or more critical settings (Channels/Roles) could not be validated. Please review settings and previous log messages.");
                 // Consider adding a notification mechanism here if desired (e.g., DM to owner, post in a default channel)
            }
        }

        // --- Individual Command Handler Methods --- 

        private async Task HandleInfoCommandAsync(SocketSlashCommand command)
        {
            bool buttonless = false;
            try {
                // Determine buttonless state based on structure (Subcommand or Direct)
                if (!settings.MainSettings.RemoveBotName) { // Subcommand structure
                    buttonless = command.Data.Options?.FirstOrDefault()?.Options?.FirstOrDefault(o => o.Name == "nobuttons")?.Value as bool? ?? false;
                } else { // Direct command structure
                     buttonless = command.Data.Options?.FirstOrDefault(o => o.Name == "nobuttons")?.Value as bool? ?? false;
                }

                log.Debug($"[CMD][INFO] Calling infoPanel.GetServerInfo (UpdateExisting=False, Buttonless={buttonless})...");
                // Add specific try/catch around GetServerInfo as it sends its own followup
                try {
                     await infoPanel.GetServerInfo(false, command, buttonless);
                     log.Info($"[CMD][INFO] infoPanel.GetServerInfo completed.");
                } catch (Exception infoEx) {
                     log.Error($"[CMD][INFO] EXCEPTION during infoPanel.GetServerInfo: {infoEx.Message}");
                     // Attempt to send an error followup if GetServerInfo failed before sending its own response
                     try {
                        if ((await command.GetOriginalResponseAsync()) != null) {
                            await command.FollowupAsync("An error occurred while generating the server info panel.", ephemeral: true);
                        }
                     } catch {}
                 }
                 // GetServerInfo handles its own followup on success, so no followup here usually needed
            }
            catch (Exception ex)
            {
                // Catch errors determining options etc.
                log.Error($"[CMD][INFO] EXCEPTION in HandleInfoCommandAsync (outer): {ex.Message}");
                 try {
                    if ((await command.GetOriginalResponseAsync()) != null) {
                         await command.FollowupAsync("An unexpected error occurred processing the info command.", ephemeral: true);
                    }
                 } catch {}
            }
        }

        private async Task HandleStartServerCommandAsync(SocketSlashCommand command)
        {
            log.Debug($"[CMD][START] Calling Task.Run(() => application.Start())...");
            try {
                await Task.Run(() => application.Start());
                log.Info($"[CMD][START] Task.Run(application.Start) completed.");
            } catch (Exception taskEx) {
                 log.Error($"[CMD][START] EXCEPTION during Task.Run(application.Start): {taskEx.Message}");
                 await command.FollowupAsync("An error occurred trying to start the application.", ephemeral: true);
                 return;
            }
            await LogCommandActionAsync("Start Server", command); // Log action
            try {
                await command.FollowupAsync("Start command sent to the application.", ephemeral: true);
            } catch (Exception followupEx) {
                log.Error($"[CMD][START] EXCEPTION sending followup: {followupEx.Message}");
            }
        }

        private async Task HandleStopServerCommandAsync(SocketSlashCommand command)
        {
            log.Debug($"[CMD][STOP] Calling Task.Run(() => application.Stop())...");
             try {
                await Task.Run(() => application.Stop());
                log.Info($"[CMD][STOP] Task.Run(application.Stop) completed.");
            } catch (Exception taskEx) {
                 log.Error($"[CMD][STOP] EXCEPTION during Task.Run(application.Stop): {taskEx.Message}");
                 await command.FollowupAsync("An error occurred trying to stop the application.", ephemeral: true);
                 return;
            }
             try {
                 await LogCommandActionAsync("Stop Server", command);
             } catch (Exception logEx) {
                 log.Error($"[CMD][STOP] EXCEPTION during CommandResponse: {logEx.Message}");
             }
            try {
                await command.FollowupAsync("Stop command sent to the application.", ephemeral: true);
            } catch (Exception followupEx) {
                 log.Error($"[CMD][STOP] EXCEPTION sending followup: {followupEx.Message}");
             }
        }

        private async Task HandleRestartServerCommandAsync(SocketSlashCommand command)
        {
             log.Debug($"[CMD][RESTART] Calling Task.Run(() => application.Restart())...");
             try {
                await Task.Run(() => application.Restart());
                log.Info($"[CMD][RESTART] Task.Run(application.Restart) completed.");
            } catch (Exception taskEx) {
                 log.Error($"[CMD][RESTART] EXCEPTION during Task.Run(application.Restart): {taskEx.Message}");
                 await command.FollowupAsync("An error occurred trying to restart the application.", ephemeral: true);
                 return;
            }
             try {
                await LogCommandActionAsync("Restart Server", command);
             } catch (Exception logEx) {
                 log.Error($"[CMD][RESTART] EXCEPTION during CommandResponse: {logEx.Message}");
             }
            try {
                await command.FollowupAsync("Restart command sent to the application.", ephemeral: true);
             } catch (Exception followupEx) {
                 log.Error($"[CMD][RESTART] EXCEPTION sending followup: {followupEx.Message}");
             }
        }

         private async Task HandleKillServerCommandAsync(SocketSlashCommand command)
        {
             log.Debug($"[CMD][KILL] Calling Task.Run(() => application.Kill())...");
             try {
                await Task.Run(() => application.Kill());
                log.Info($"[CMD][KILL] Task.Run(application.Kill) completed.");
             } catch (Exception taskEx) {
                 log.Error($"[CMD][KILL] EXCEPTION during Task.Run(application.Kill): {taskEx.Message}");
                 await command.FollowupAsync("An error occurred trying to kill the application.", ephemeral: true);
                 return;
            }
             try {
                await LogCommandActionAsync("Kill Server", command);
             } catch (Exception logEx) {
                 log.Error($"[CMD][KILL] EXCEPTION during CommandResponse: {logEx.Message}");
             }
            try {
                await command.FollowupAsync("Kill command sent to the application.", ephemeral: true);
            } catch (Exception followupEx) {
                 log.Error($"[CMD][KILL] EXCEPTION sending followup: {followupEx.Message}");
             }
        }

        private async Task HandleUpdateServerCommandAsync(SocketSlashCommand command)
        {
             log.Debug($"[CMD][UPDATE] Calling Task.Run(() => application.Update())...");
             try {
                await Task.Run(() => application.Update());
                 log.Info($"[CMD][UPDATE] Task.Run(application.Update) completed.");
            } catch (Exception taskEx) {
                 log.Error($"[CMD][UPDATE] EXCEPTION during Task.Run(application.Update): {taskEx.Message}");
                 await command.FollowupAsync("An error occurred trying to update the application.", ephemeral: true);
                 return;
             }
            try {
                await LogCommandActionAsync("Update Server", command);
            } catch (Exception logEx) {
                 log.Error($"[CMD][UPDATE] EXCEPTION during CommandResponse: {logEx.Message}");
             }
            try {
                await command.FollowupAsync("Update command sent to the application.", ephemeral: true);
            } catch (Exception followupEx) {
                 log.Error($"[CMD][UPDATE] EXCEPTION sending followup: {followupEx.Message}");
             }
        }

        private async Task HandleConsoleCommandAsync(SocketSlashCommand command)
        {
             string consoleCommandText = "";
             // Extract console command text based on structure
             if (!settings.MainSettings.RemoveBotName) { // Subcommand structure
                consoleCommandText = command.Data.Options?.FirstOrDefault()?.Options?.FirstOrDefault(o => o.Name == "value")?.Value?.ToString() ?? string.Empty;
            } else { // Direct command structure
                consoleCommandText = command.Data.Options?.FirstOrDefault(o => o.Name == "value")?.Value?.ToString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(consoleCommandText)) {
                log.Warning("[CMD][CONSOLE] Console command text is empty.");
                 try { await command.FollowupAsync("Cannot send an empty console command.", ephemeral: true); } catch {} 
                return;
            }

            log.Debug($"[CMD][CONSOLE] Calling commands.SendConsoleCommand with text: '{consoleCommandText}'...");
             try {
                await commands.SendConsoleCommand(command); // Passes original command for context if needed by SendConsoleCommand
                log.Info($"[CMD][CONSOLE] commands.SendConsoleCommand completed for text: '{consoleCommandText}'.");
            } catch (Exception taskEx) {
                 log.Error($"[CMD][CONSOLE] EXCEPTION during SendConsoleCommand: {taskEx.Message}");
                 try { await command.FollowupAsync("An error occurred trying to send the console command.", ephemeral: true); } catch {}
                 return;
             }
            try {
                await LogCommandActionAsync($"`{consoleCommandText}` console", command);
             } catch (Exception logEx) {
                 log.Error($"[CMD][CONSOLE] EXCEPTION during CommandResponse: {logEx.Message}");
             }
             try {
                 await command.FollowupAsync($"Command sent to the server: `{consoleCommandText}`", ephemeral: true);
            } catch (Exception followupEx) {
                 log.Error($"[CMD][CONSOLE] EXCEPTION sending followup: {followupEx.Message}");
             }
        }

        private async Task HandleShowPlaytimeCommandAsync(SocketSlashCommand command)
        {
             string playerName = null;
             // Extract playername option correctly based on structure
             if (!settings.MainSettings.RemoveBotName) { // Subcommand structure
                 playerName = command.Data.Options?.FirstOrDefault()?.Options?.FirstOrDefault(o => o.Name == "playername")?.Value?.ToString();
             } else { // Direct command structure
                 playerName = command.Data.Options?.FirstOrDefault(o => o.Name == "playername")?.Value?.ToString();
             }

            if (!string.IsNullOrEmpty(playerName))
            {
                 log.Debug($"[CMD][PLAYTIME] Calling helper.GetPlayTimeLeaderBoard (SpecificPlayer={playerName})...");
                string playTime = helper.GetPlayTimeLeaderBoard(1, true, playerName, false, false);
                log.Info($"[CMD][PLAYTIME] helper.GetPlayTimeLeaderBoard completed. Result: {playTime}");
                 try { await command.FollowupAsync($"Playtime for {playerName}: {playTime}", ephemeral: true); } catch {} 
            }
            else
            {
                 log.Debug($"[CMD][PLAYTIME] Calling ShowPlayerPlayTime (Leaderboard)...");
                 // ShowPlayerPlayTime now handles its own followup
                 await ShowPlayerPlayTime(command);
                 log.Info($"[CMD][PLAYTIME] ShowPlayerPlayTime completed.");
             }
        }

        private async Task HandleFullPlaytimeListCommandAsync(SocketSlashCommand command)
        {
             string playerName = null;
             // Extract playername option correctly
             if (!settings.MainSettings.RemoveBotName) { // Subcommand structure
                 playerName = command.Data.Options?.FirstOrDefault()?.Options?.FirstOrDefault(o => o.Name == "playername")?.Value?.ToString();
            } else { // Direct command structure
                 playerName = command.Data.Options?.FirstOrDefault(o => o.Name == "playername")?.Value?.ToString();
             }

            if (!string.IsNullOrEmpty(playerName))
            {
                 log.Debug($"[CMD][FULLPLAY] Calling helper.GetPlayTimeLeaderBoard (SpecificPlayer={playerName}, FullList=True)...");
                string playTime = helper.GetPlayTimeLeaderBoard(1, true, playerName, true, false);
                 log.Info($"[CMD][FULLPLAY] helper.GetPlayTimeLeaderBoard completed. Result length: {playTime?.Length ?? 0}");
                 try { await command.FollowupAsync($"Playtime for {playerName}: {playTime}", ephemeral: true); } catch {}
            }
            else
            {
                 log.Debug($"[CMD][FULLPLAY] Calling helper.GetPlayTimeLeaderBoard (FullList=True)...");
                string playTime = helper.GetPlayTimeLeaderBoard(1000, false, null, true, false);
                log.Info($"[CMD][FULLPLAY] helper.GetPlayTimeLeaderBoard completed. Result length: {playTime?.Length ?? 0}");
                if (playTime.Length > 1900) // Slightly less than 2000 for safety margin
                {
                    string path = Path.Combine(application.BaseDirectory, $"full-playtime-list-{command.User.Id}.txt");
                    log.Debug($"[CMD][FULLPLAY] Playtime list too long ({playTime.Length} chars). Creating file: {path}");
                    try
                    {
                        playTime = playTime.Replace("```", ""); // Remove markdown
                        using (FileStream fileStream = File.Create(path))
                        {
                            byte[] text = new UTF8Encoding(true).GetBytes(playTime);
                            await fileStream.WriteAsync(text, 0, text.Length);
                        }
                        log.Debug($"[CMD][FULLPLAY] File created. Sending FollowupWithFileAsync...");
                        await command.FollowupWithFileAsync(path, ephemeral: true);
                        log.Info($"[CMD][FULLPLAY] File sent. Deleting file...");
                        File.Delete(path); // Clean up
                        log.Debug($"[CMD][FULLPLAY] File deleted.");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[CMD][FULLPLAY] Error creating/sending playtime file: {ex.Message}");
                        try { await command.FollowupAsync("There was an error generating the full playtime list file.", ephemeral: true); } catch {}
                    }
                }
                else
                {
                     log.Debug($"[CMD][FULLPLAY] Sending playtime list as text...");
                    try { await command.FollowupAsync(playTime, ephemeral: true); } catch {}
                     log.Info($"[CMD][FULLPLAY] Playtime list sent.");
                }
            }
        }

        private async Task HandleTakeBackupCommandAsync(SocketSlashCommand command)
        {
             log.Debug($"[CMD][BACKUP] Initiating backup command...");
            if (command.User is SocketGuildUser user)
            {
                try {
                    commands.BackupServer(user);
                    log.Info($"[CMD][BACKUP] commands.BackupServer call initiated for user {user.Username}.");
                } catch (Exception taskEx) {
                    log.Error($"[CMD][BACKUP] EXCEPTION calling commands.BackupServer: {taskEx.Message}");
                    try { await command.FollowupAsync("An error occurred trying to initiate the backup.", ephemeral: true); } catch {}
                    return;
                }
                // Log the action first
                try { await LogCommandActionAsync("Backup Server", command); } catch {} 
                 try {
                    await command.FollowupAsync("Backup command sent to the panel. You will receive DM notifications on progress.", ephemeral: true);
                 } catch (Exception followupEx) {
                     log.Error($"[CMD][BACKUP] EXCEPTION sending followup: {followupEx.Message}");
                 }
            }
            else
            {
                 log.Warning($"[CMD][BACKUP] Could not execute backup command for user {command.User.Username} as they are not a SocketGuildUser.");
                 try { await command.FollowupAsync("Backup command can only be run from within a server.", ephemeral: true); } catch {}
            }
        }

        // --- End of Individual Command Handler Methods ---

        /// <param name="ex">The exception that occurred.</param>
        /// <param name="source">Source of the exception</param>
        public static void HandleLogException(Exception ex, string source)
        {
            // Log the exception details to the console error stream
            Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Error] [{source}] Exception during logging: {ex.GetType().Name} - {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            // Removed reference to non-accessible instance member 'bot'
        }

        /// <summary>
        /// Validates a channel ID or name setting.
        /// </summary>
        /// <returns>True if valid, False otherwise.</returns>
        // Ensure this is public
        public bool ValidateChannelSetting(string? channelNameOrId, string settingName)
        {
             if (string.IsNullOrWhiteSpace(channelNameOrId)) {
                 log.Error($"[VALIDATE] {settingName} is not configured.");
                 return false;
             }

            if (client == null || client.ConnectionState != ConnectionState.Connected)
            {
                log.Warning($"[VALIDATE] Cannot validate setting '{settingName}' ('{channelNameOrId}') - Discord client not connected.");
                return false; // Cannot validate
            }

            log.Debug($"[VALIDATE] Validating channel setting '{settingName}' ('{channelNameOrId}')...");
            bool found = false;
            // Use a temporary list to avoid issues if Guilds collection changes during iteration
            var guilds = client?.Guilds.ToList() ?? new List<SocketGuild>();
            foreach (var guild in guilds)
            {
                SocketGuildChannel? channel = null;
                // Try parsing as ID first
                if (ulong.TryParse(channelNameOrId, out ulong channelId))
                {
                    try {
                        channel = guild.GetChannel(channelId);
                    } catch (Exception ex) {
                        log.Debug($"[VALIDATE] Exception checking channel ID {channelId} in guild {guild.Name}: {ex.Message}");
                    }
                }

                // If not found by ID, try by name
                if (channel == null)
                {
                    try {
                         channel = guild.Channels.FirstOrDefault(c => c.Name.Equals(channelNameOrId, StringComparison.OrdinalIgnoreCase));
                    } catch (Exception ex) {
                        log.Debug($"[VALIDATE] Exception checking channel name '{channelNameOrId}' in guild {guild.Name}: {ex.Message}");
                    }
                }

                if (channel != null)
                {
                    log.Info($"[VALIDATE] Found channel '{channel.Name}' ({channel.Id}) for setting '{settingName}' in Guild '{guild.Name}' ({guild.Id}). Validation successful for this setting.");
                    found = true;
                    break; // Found in at least one guild, that's enough
                }
            }

            if (!found)
            {
                log.Error($"[VALIDATE] FAILED: Could not find channel '{channelNameOrId}' (for setting '{settingName}') in ANY connected guilds. Please check configuration.");
            }
            return found;
        }

        /// <summary>
        /// Validates a role name or ID setting.
        /// </summary>
        /// <returns>True if valid, False otherwise.</returns>
        // Ensure this is public
        public bool ValidateRoleSetting(string? roleNameOrId, string settingName)
        {
            if (string.IsNullOrWhiteSpace(roleNameOrId)) {
                log.Error($"[VALIDATE] {settingName} is not configured (required because Restrict Functions is enabled).");
                return false;
            }

            if (client == null || client.ConnectionState != ConnectionState.Connected)
            {
                 log.Warning($"[VALIDATE] Cannot validate setting '{settingName}' ('{roleNameOrId}') - Discord client not connected.");
                 return false; // Cannot validate
            }

            log.Debug($"[VALIDATE] Validating role setting '{settingName}' ('{roleNameOrId}')...");
            var rolesToFind = roleNameOrId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).Distinct().ToList();
            if (!rolesToFind.Any()) {
                log.Debug($"[VALIDATE] No roles specified for setting '{settingName}'. Skipping detailed validation as empty is allowed.");
                return true; // No roles listed is valid
            }

            List<string> rolesNotFound = new List<string>(rolesToFind);
            var guilds = client?.Guilds.ToList() ?? new List<SocketGuild>();

            foreach (var roleIdentifier in rolesToFind)
            {
                bool foundCurrentRole = false;
                 foreach (var guild in guilds)
                 {
                    SocketRole? role = null;
                     // Try parsing as ID first
                    if (ulong.TryParse(roleIdentifier, out ulong roleId))
                    {
                        try {
                            role = guild.GetRole(roleId);
                        } catch (Exception ex) {
                            log.Debug($"[VALIDATE] Exception checking role ID {roleId} in guild {guild.Name}: {ex.Message}");
                        }
                     }

                    // If not found by ID, try by name
                     if (role == null)
                     {
                        try {
                            role = guild.Roles.FirstOrDefault(r => r.Name.Equals(roleIdentifier, StringComparison.OrdinalIgnoreCase));
                        } catch (Exception ex) {
                            log.Debug($"[VALIDATE] Exception checking role name '{roleIdentifier}' in guild {guild.Name}: {ex.Message}");
                        }
                     }

                    if (role != null)
                    {
                         log.Debug($"[VALIDATE] Found role '{role.Name}' ({role.Id}) matching identifier '{roleIdentifier}' in Guild '{guild.Name}' ({guild.Id}).");
                         foundCurrentRole = true;
                         rolesNotFound.Remove(roleIdentifier); // Found it, remove from missing list
                         break; // Stop checking guilds for *this* role identifier
                     }
                 }
                 if (!foundCurrentRole) {
                     log.Warning($"[VALIDATE] Could not find role matching identifier '{roleIdentifier}' in ANY connected guilds.");
                 }
            }

            if (rolesNotFound.Any())
            {
                 log.Error($"[VALIDATE] FAILED: Could not find the following role(s) (for setting '{settingName}') in ANY connected guilds: {string.Join(", ", rolesNotFound)}. Please check configuration.");
                 return false;
            }
            else
            {
                 log.Info($"[VALIDATE] All specified roles for setting '{settingName}' were found in at least one guild. Validation successful for this setting.");
                return true;
            }
        }

    }
}

