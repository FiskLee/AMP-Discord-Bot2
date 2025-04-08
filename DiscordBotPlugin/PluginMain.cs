using FileManagerPlugin;
using LocalFileBackupPlugin;
using ModuleShared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace DiscordBotPlugin
{
    public class PluginMain : AMPPlugin
    {
        private readonly Settings _settings;
        private readonly ILogger log;
        private readonly IRunningTasksManager _tasks;
        public readonly IApplicationWrapper application;
        private readonly Bot bot;
        private BackupProvider? _backupProvider;
        private readonly IFeatureManager _features = null!;
        private readonly Commands commands;
        private readonly InfoPanel infoPanel;
        private readonly Events events;
        private readonly Helpers helper;
        private readonly IConfigSerializer config;
        private readonly IAMPInstanceInfo instanceInfo;
        private readonly IPlatformInfo platformInfo;
        private readonly IRunningTasksManager taskManager;
        private readonly IFeatureManager featureManager;

        public PluginMain(ILogger log, IConfigSerializer config, IPlatformInfo platform,
            IRunningTasksManager taskManager, IApplicationWrapper application, IAMPInstanceInfo AMPInstanceInfo, IFeatureManager Features)
        {
            this.log = log;
            _settings = config.Load<Settings>(AutoSave: true);
            _tasks = taskManager;
            this.application = application;
            _features = Features;
            this.config = config;
            this.instanceInfo = AMPInstanceInfo;
            this.platformInfo = platform;
            this.taskManager = taskManager;
            this.featureManager = Features;

            _features.PostLoadPlugin(application, "LocalFileBackupPlugin");

            config.SaveMethod = PluginSaveMethod.KVP;
            config.KVPSeparator = "=";

            // Initialize some dependencies first
            helper = new Helpers(_settings, this.log, this.application, config, platform, null); // Temporary null for infoPanel
            commands = new Commands(this.application, this.log, null, this.instanceInfo, _settings, helper);
            infoPanel = new InfoPanel(this.application, _settings, helper, this.instanceInfo, this.log, config, null, commands); // Temporary null for bot
            bot = new Bot(_settings, this.instanceInfo, this.application, this.log, null, helper, infoPanel, commands); // Temporary null for events

            // Pass the dependencies with fully initialized objects, except backupprovider (post-init)
            events = new Events(this.application, _settings, this.log, config, bot, helper, null, infoPanel);

            // Complete the object initialization
            helper.SetInfoPanel(infoPanel); // Set the previously null dependency
            commands.SetEvents(events);     // Restore event injection
            infoPanel.SetBot(bot);          // Inject bot
            bot.SetEvents(events);          // Inject events

            _settings.SettingModified += events.Settings_SettingModified;
            log.MessageLogged += events.Log_MessageLogged;
            application.StateChanged += events.ApplicationStateChange;
            if (application is IHasSimpleUserList hasSimpleUserList)
            {
                hasSimpleUserList.UserJoins += events.UserJoins;
                hasSimpleUserList.UserLeaves += events.UserLeaves;
            }

            /* // Temporarily comment out Backup Provider retrieval due to API changes (IApplicationWrapper.Components missing)
            // Attempt to get Backup Provider - Ensure using original code
            try
            {
                _backupProvider = application.Components.OfType<BackupProvider>().FirstOrDefault(); // Fix CS1061: Use original code
                if (_backupProvider != null) {
                    log.Info("LocalFileBackupPlugin detected.");
                } else {
                    log.Info("LocalFileBackupPlugin not found in this instance.");
                }
            }
            catch (Exception ex)
            {
                log.Warning("Failed to check for LocalFileBackupPlugin: " + ex.Message);
                _backupProvider = null;
            }
            */

            // Re-assign events/infopanel now that they are created
            bot.SetEvents(events);
            infoPanel.SetBot(bot); // Assuming InfoPanel has a SetBot method
            commands.SetBot(bot);

            log.Info("Discord Bot Plugin Initialized.");

            // Optional: Subscribe to backup events if provider exists
            if (_backupProvider != null)
            {
                // Comment out subscription due to API changes
                // _backupProvider.BackupStatusChanged += commands.HandleBackupStatusChange; 
                log.Warning("Backup event subscription is temporarily commented out due to potential API changes."); 
            }

            // Defer Discord connection until PostInit
        }

        /// <summary>
        /// Initializes the bot and assigns an instance of WebMethods to APIMethods.
        /// </summary>
        /// <param name="APIMethods">An output parameter to hold the instance of WebMethods.</param>
        public override void Init(out WebMethodsBase APIMethods)
        {
            // Create a new instance of WebMethods and assign it to APIMethods
            APIMethods = new WebMethods(_tasks);
        }

        public override bool HasFrontendContent => false;

        /// <summary>
        /// Performs post-initialization actions for the bot.
        /// </summary>
        public override void PostInit()
        {
            _backupProvider = (BackupProvider?)_features.RequestFeature<BackupProvider>();
            commands.SetBackupProvider(_backupProvider);  // Inject backupprovider
            events.SetBackupProvider(_backupProvider);    // Inject backupprovider

            // Check if the bot is turned on
            if (_settings.MainSettings.BotActive)
            {
                log.Info("Discord Bot Activated");

                // Check if we have a bot token and attempt to connect
                if (!string.IsNullOrEmpty(_settings.MainSettings.BotToken))
                {
                    Task connectionTask = null;
                    try
                    {
                        log.Info("Attempting Discord connection...");
                        // Ensure bot is not null before calling connect
                        if (bot != null) {
                            // Ensure token is non-null HERE
                            string token = _settings.MainSettings.BotToken ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(token)) {
                                log.Error("Bot Token is empty or whitespace, cannot connect.");
                            } else {
                                // Pass token directly, ConnectDiscordAsync handles null check internally
                                // Add ! to assure compiler token is non-null after check.
                                connectionTask = bot.ConnectDiscordAsync(token!); 
                                // Don't block PostInit, let connection happen in background
                                // Pass null args to UpdatePresence, force=true
                                // Add null check for bot before calling UpdatePresence
                                if (bot != null) {
                                     _ = bot.UpdatePresence(null, null, true);
                                }
                            }
                        } else {
                            log.Error("Bot instance is null, cannot connect to Discord.");
                        }
                    }
                    catch (Exception exception)
                    {
                        // Log any errors that occur during initial connection attempt
                        log.Error("Error initiating Discord Bot connection: " + exception.Message);
                    }

                    // Schedule validation to run after a short delay to allow connection attempt
                    // Run validation even if ConnectDiscordAsync throws an immediate error, as the client might still exist.
                     _ = Task.Run(async () => {
                         await Task.Delay(10000); // Wait 10 seconds for connection attempt
                         // Check events as well
                         if (bot?.client != null && events != null) { // Check bot AND client AND events
                            log.Info("Running initial configuration validation...");
                            // Call the renamed async method
                            await bot.PerformInitialConfigurationValidationAsync();
                         } else {
                            log.Warning("Skipping initial configuration validation as Discord client was not initialized or connected.");
                         }
                     });

                }
                else {
                     log.Error("Bot is active but no Bot Token is configured. Cannot connect.");
                }
            }
             else {
                 log.Info("Discord Bot is not active in settings.");
             }
        }

        public override IEnumerable<SettingStore> SettingStores => Utilities.EnumerableFrom(_settings);

        /// <summary>
        /// Represents player playtime information.
        /// </summary>
        public class PlayerPlayTime
        {
            public string PlayerName { get; set; } = string.Empty;
            public DateTime JoinTime { get; set; }
            public DateTime LeaveTime { get; set; }
        }

        public class ServerInfo
        {
            public string ServerName { get; set; } = string.Empty;
            public string ServerIP { get; set; } = string.Empty;
            public string ServerStatus { get; set; } = string.Empty;
            public string ServerStatusClass { get; set; } = string.Empty;
            public string CPUUsage { get; set; } = string.Empty;
            public string MemoryUsage { get; set; } = string.Empty;
            public string Uptime { get; set; } = string.Empty;
            public string[] OnlinePlayers { get; set; } = Array.Empty<string>();
            public string PlayerCount { get; set; } = string.Empty;
            public string[] PlaytimeLeaderBoard { get; set; } = Array.Empty<string>();
        }

        // Class to hold data for the web panel
    public class WebPanelData
    {
        public string PlayerName { get; set; } = string.Empty; // Add = string.Empty
        public string PlayerUUID { get; set; } = string.Empty; // Add = string.Empty
        public string ServerName { get; set; } = string.Empty; // Add = string.Empty
        public string ServerIP { get; set; } = string.Empty; // Add = string.Empty
        public string ServerStatus { get; set; } = string.Empty; // Add = string.Empty
        public string ServerStatusClass { get; set; } = string.Empty; // Add = string.Empty
        public string CPUUsage { get; set; } = string.Empty; // Add = string.Empty
        public string MemoryUsage { get; set; } = string.Empty; // Add = string.Empty
        public string Uptime { get; set; } = string.Empty; // Add = string.Empty
        public List<string> OnlinePlayers { get; set; } = new List<string>(); // Add = new List<string>()
        public string PlayerCount { get; set; } = string.Empty; // Add = string.Empty
        public List<KeyValuePair<string, TimeSpan>> PlaytimeLeaderBoard { get; set; } = new List<KeyValuePair<string, TimeSpan>>(); // Add = new List<...>()
    }
    }
}
