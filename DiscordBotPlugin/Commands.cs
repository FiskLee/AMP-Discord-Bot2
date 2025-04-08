using Discord;
using Discord.Commands;
using Discord.WebSocket;
using LocalFileBackupPlugin;
using ModuleShared;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace DiscordBotPlugin
{
    internal class Commands
    {
        private readonly IApplicationWrapper application;
        private readonly ILogger log;
        private BackupProvider? backupProvider;
        private readonly IAMPInstanceInfo aMPInstanceInfo;
        private readonly Settings settings;
        private readonly Helpers helper;
        private Bot? bot;

        private readonly ConcurrentDictionary<string, IUser> backupInitiatorUserMap = new ConcurrentDictionary<string, IUser>();

        private Events events = null!;

        public Commands(IApplicationWrapper application, ILogger log, BackupProvider? backupProvider, IAMPInstanceInfo aMPInstanceInfo, Settings settings, Helpers helper)
        {
            this.application = application;
            this.log = log;
            this.backupProvider = backupProvider;
            this.aMPInstanceInfo = aMPInstanceInfo;
            this.settings = settings;
            this.helper = helper;
        }

        public void SetEvents(Events events)
        {
            this.events = events;
        }

        public void SetBackupProvider(BackupProvider? backupProvider)
        {
            this.backupProvider = backupProvider;
        }

        public void SetBot(Bot botInstance)
        {
            this.bot = botInstance;
        }

        /// <summary>
        /// Send a command to the AMP instance
        /// </summary>
        /// <param name="msg">Command to send to the server</param>
        /// <returns>Task</returns>
        public Task SendConsoleCommand(SocketSlashCommand msg)
        {
            try
            {
                if (application is not IHasWriteableConsole writeableConsole)
                {
                    log.Error("Cannot send command: Application does not implement IHasWriteableConsole.");
                    return Task.CompletedTask;
                }

                // Check if message data options are valid before proceeding
                if (msg?.Data?.Options == null)
                {
                    log.Error("Cannot send command: Invalid message data.");
                    return Task.CompletedTask;
                }

                // Initialize the command string
                string command = "";

                // Get the command to be sent based on the bot name removal setting
                if (settings.MainSettings.RemoveBotName)
                {
                    // Explicitly check first option and its value
                    var firstOption = msg.Data.Options.FirstOrDefault();
                    command = firstOption?.Value?.ToString() ?? string.Empty;
                }
                else
                {
                    // Explicitly check first option, its options, and its value
                    var firstOption = msg.Data.Options.FirstOrDefault();
                    var subOption = firstOption?.Options?.FirstOrDefault();
                    command = subOption?.Value?.ToString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(command))
                {
                    log.Error("Cannot send command: Command is empty.");
                    return Task.CompletedTask;
                }

                // Send the command to the AMP instance
                writeableConsole?.WriteLine(command);

                // Return a completed task to fulfill the method signature
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                // Log any errors that occur during command sending
                log.Error("Cannot send command: " + exception.Message);

                // Return a completed task to fulfill the method signature
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Send a chat message to the AMP instance, only for Minecraft for now
        /// </summary>
        /// <param name="author">Discord name of the sender</param>
        /// <param name="msg">Message to send</param>
        /// <returns>Task</returns>
        public Task SendChatCommand(string author, string msg)
        {
            try
            {
                if (application is not IHasWriteableConsole writeableConsole)
                {
                    log.Error("Cannot send chat message: Application does not implement IHasWriteableConsole.");
                    return Task.CompletedTask;
                }

                // Ensure the message and author are not null or empty
                if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(msg))
                {
                    log.Error("Cannot send chat message: Author or message is empty.");
                    return Task.CompletedTask;
                }

                // Construct the command to send
                string command = $"say <{author}> {msg}";

                if (application.ApplicationName == "Seven Days To Die")
                    command = $"say \"<{author}> {msg}\"";

                if (application.ApplicationName == "Palworld")
                    command = $"broadcast <{author}>_{msg.Replace(" ","_")}";

                // Send the chat command to the AMP instance
                writeableConsole.WriteLine(command);

                // Return a completed task to fulfill the method signature
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                // Log any errors that occur during chat message sending
                log.Error("Cannot send chat message: " + exception.Message);

                // Return a completed task to fulfill the method signature
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Hook into LocalFileBackupPlugin events and request a backup
        /// </summary>
        /// <param name="user">The SocketGuildUser argument</param>
        public void BackupServer(SocketGuildUser user)
        {
            if (user == null)
            {
                log.Error("Cannot perform backup: User is null.");
                return;
            }

            BackupManifest manifest = new BackupManifest
            {
                ModuleName = aMPInstanceInfo.ModuleName,
                TakenBy = "DiscordBot",
                CreatedAutomatically = true,
                Name = "Backup Triggered by Discord Bot",
                Description = "Requested by " + user.Username
            };

            events?.SetCurrentUser(user);

            // Register event handlers
            //backupProvider.BackupActionComplete += events.OnBackupComplete; // Temporarily comment out
            //backupProvider.BackupActionFailed += events.OnBackupFailed; // Temporarily comment out
            //backupProvider.BackupActionStarting += events.OnBackupStarting; // Temporarily comment out

            // Initiate backup
            log.Info("Backup requested by " + user.Username + " - attempting to start");
            // backupProvider?.TakeBackup(manifest); // Temporarily comment out if TakeBackup API changed
            log.Warning("Backup initiation is temporarily commented out due to potential API changes."); // Add warning
        }

        /// <summary>
        /// Manages the server by sending a private message to the user with a link to the management panel.
        /// </summary>
        /// <param name="arg">The SocketMessageComponent argument.</param>
        public async Task ManageServer(SocketMessageComponent arg)
        {
            if (arg == null || arg.User == null)
            {
                log.Error("Cannot manage server: Argument or user is null.");
                return;
            }

            var builder = new ComponentBuilder();
            string managementProtocol = settings.MainSettings.ManagementURLSSL ? "https://" : "http://";

            // Build the button with the management panel link using the appropriate protocol and instance ID
            string managementPanelLink = $"{managementProtocol}{settings.MainSettings.ManagementURL}/?instance={aMPInstanceInfo.InstanceId}";
            builder.WithButton("Manage Server", style: ButtonStyle.Link, url: managementPanelLink);

            // Send a private message to the user with the link to the management panel
            await arg.User.SendMessageAsync("Link to management panel:", components: builder.Build());
        }

        /* // Ensure this entire block related to backup events is commented out
        // Consider making BackupStatusChangeEventArgs non-nullable if it's never null in practice
        // public void HandleBackupStatusChange(object? sender, BackupStatusChangeEventArgs e)
        // {
        //     log.Debug($"[BACKUP_EVT_CMD] Backup status changed: {e.NewState}");
        //     // Determine which original handler to call based on state
        //     switch (e.NewState)
        //     {
        //         case BackupState.Starting:
        //             OnBackupStarting(sender, e); // Pass through original args
        //             break;
        //         case BackupState.Complete:
        //             OnBackupComplete(sender, e); // Pass through original args
        //             break;
        //         case BackupState.Failed:
        //             OnBackupFailed(sender, e); // Pass through original args
        //             break;
        //             // Add other states if needed (e.g., Running, Aborted)
        //     }
        // }

        // Keep original handlers, but make sender nullable
        // private void OnBackupComplete(object? sender, EventArgs e)
        // {
        //     log.Debug("[BACKUP_EVT_CMD] OnBackupComplete triggered.");
        //     if (e is BackupStatusChangeEventArgs backupArgs)
        //     {
        //         var user = backupInitiatorUserMap.GetValueOrDefault(backupArgs.TaskReference);
        //         if (user != null)
        //         {
        //             _ = SendBackupDMAsync(user, "Backup Completed Successfully", "Your server backup has completed.", settings.ColourSettings.ServerStartColour); // Green for success
        //             backupInitiatorUserMap.TryRemove(backupArgs.TaskReference, out _);
        //         }
        //         else
        //         {
        //             log.Warning("[BACKUP_EVT_CMD] Could not find initiating user for completed backup task: " + backupArgs.TaskReference);
        //         }
        //     } else {
        //          log.Warning("[BACKUP_EVT_CMD] OnBackupComplete received unexpected EventArgs type: " + e?.GetType().Name);
        //     }
        // }

        // private void OnBackupFailed(object? sender, EventArgs e)
        // {
        //      log.Debug("[BACKUP_EVT_CMD] OnBackupFailed triggered.");
        //     if (e is BackupStatusChangeEventArgs backupArgs)
        //     {
        //         var user = backupInitiatorUserMap.GetValueOrDefault(backupArgs.TaskReference);
        //          if (user != null)
        //          {
        //              _ = SendBackupDMAsync(user, "Backup Failed", "Your server backup failed to complete. Check AMP logs for details.", settings.ColourSettings.ServerKillColour); // Red for failure
        //             backupInitiatorUserMap.TryRemove(backupArgs.TaskReference, out _);
        //          }
        //         else
        //         {
        //              log.Warning("[BACKUP_EVT_CMD] Could not find initiating user for failed backup task: " + backupArgs.TaskReference);
        //          }
        //     } else {
        //          log.Warning("[BACKUP_EVT_CMD] OnBackupFailed received unexpected EventArgs type: " + e?.GetType().Name);
        //      }
        //  }

        // private void OnBackupStarting(object? sender, EventArgs e)
        // {
        //     log.Debug("[BACKUP_EVT_CMD] OnBackupStarting triggered.");
        //     if (e is BackupStatusChangeEventArgs backupArgs)
        //     {
        //          var user = backupInitiatorUserMap.GetValueOrDefault(backupArgs.TaskReference);
        //          if (user != null)
        //          {
        //              _ = SendBackupDMAsync(user, "Backup Starting", "Your server backup is starting.", settings.ColourSettings.ServerRestartColour); // Orange/Yellow for starting
        //          }
        //          else
        //          {
        //              log.Warning("[BACKUP_EVT_CMD] Could not find initiating user for starting backup task: " + backupArgs.TaskReference);
        //          }
        //      } else {
        //          log.Warning("[BACKUP_EVT_CMD] OnBackupStarting received unexpected EventArgs type: " + e?.GetType().Name);
        //      }
        //  }
        */
        // Keep only THIS definition of SendBackupDMAsync
        private async Task SendBackupDMAsync(IUser user, string title, string message, string colourHex)
        {
            if (bot == null || bot.client == null || user == null) return; // Basic guard clauses
            log.Debug($"[BACKUP_DM] Attempting to send DM to {user.Username} ({user.Id}). Title: {title}");
            try
            {
                var embedBuilder = new EmbedBuilder()
                    .WithTitle(title)
                    .WithDescription(message ?? "No Message")
                    .WithColor(helper.GetColour(title ?? "Default Title", colourHex ?? "#FFFFFF") != default(Color) ? helper.GetColour(title ?? "Default Title", colourHex ?? "#FFFFFF") : Color.Default)
                    .WithCurrentTimestamp();

                await user.SendMessageAsync(embed: embedBuilder.Build());
                log.Info($"[BACKUP_DM] Sent backup status DM to {user.Username}.");
            }
            catch (Exception ex)
            {
                log.Error($"[BACKUP_DM] Failed to send backup DM to {user.Username}: {ex.Message}");
            }
        }
    }

    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commands;

        // Retrieve client and CommandService instance via constructor
        public CommandHandler(DiscordSocketClient client, CommandService commands)
        {
            _commands = commands;
            _client = client;
        }

        public async Task InstallCommandsAsync()
        {
            // Hook the MessageReceived event into our command handler
            _client.MessageReceived += HandleCommandAsync;

            // Discover all of the command modules in the entry 
            // assembly and load them
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), services: null);
        }

        private async Task HandleCommandAsync(SocketMessage messageParam)
        {
            // Don't process the command if it was a system message
            if (messageParam is not SocketUserMessage message) return;

            // Create a number to track where the prefix ends and the command begins
            int argPos = 0;

            // Determine if the message is a command based on the prefix and make sure no bots trigger commands
            if (!(message.HasCharPrefix('!', ref argPos) || 
                (_client?.CurrentUser != null && message.HasMentionPrefix(_client.CurrentUser, ref argPos))) ||
                message.Author.IsBot)
                return;

            // Create a WebSocket-based command context based on the message
            var context = new SocketCommandContext(_client, message);

            // Execute the command with the command context we just created
            await _commands.ExecuteAsync(context, argPos, services: null);
        }
    }
}
