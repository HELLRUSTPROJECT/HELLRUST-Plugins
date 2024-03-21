using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Auto Commands", "Wulf", "2.0.1")]
    [Description("Automatically runs configured commands on player and server events")]
    public class AutoCommands : CovalencePlugin
    {
        #region Configuration

        private Configuration config;

        public class Configuration
        {
            [JsonProperty("Run commands on player connect")]
            public bool RunCommandsOnConnect = false;

            [JsonProperty("Commands on connect", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ConnectCommands = new List<string> { "examplecmd $player.id", "example.cmd" };

            [JsonProperty("Run commands on player disconnect")]
            public bool RunCommandsOnDisconnect = false;

            [JsonProperty("Commands on disconnect", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> DisconnectCommands = new List<string> { "examplecmd", "example.cmd \"text example\"" };

            [JsonProperty("Run commands on server startup")]
            public bool RunCommandsOnStartup = false;

            [JsonProperty("Commands on server startup", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> StartupCommands = new List<string> { "examplecmd $server.name", "example.cmd" };

#if HURTWORLD || RUST

            [JsonProperty("Run commands on server wipe")]
            public bool RunCommandsOnWipe = false;

            [JsonProperty("Commands on server wipe", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> WipeCommands = new List<string> { "examplecmd arg", "example.cmd \"text example\"" };

#endif

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion Configuration

        #region Initialization

        private void Init()
        {
            if (!config.RunCommandsOnConnect)
            {
                Unsubscribe(nameof(OnUserConnected));
            }

            if (!config.RunCommandsOnDisconnect)
            {
                Unsubscribe(nameof(OnUserDisconnected));
            }

            if (!config.RunCommandsOnStartup)
            {
                Unsubscribe(nameof(OnServerInitialized));
            }

#if HURTWORLD || RUST
            if (!config.RunCommandsOnWipe)
            {
                Unsubscribe(nameof(OnNewSave));
            }
#endif
        }

        #endregion Initialization

        #region Player Commands

        private void OnUserConnected(IPlayer player)
        {
            foreach (string command in config.ConnectCommands)
            {
                ProcessCommand(command, player);
            }
        }

        private void OnUserDisconnected(IPlayer player)
        {
            foreach (string command in config.DisconnectCommands)
            {
                ProcessCommand(command, player);
            }
        }

        #endregion Player Commands

        #region Server Commands

        private void OnServerInitialized()
        {
            foreach (string command in config.StartupCommands)
            {
                ProcessCommand(command);
            }
        }

#if HURTWORLD || RUST

        private void OnNewSave()
        {
            foreach (string command in config.WipeCommands)
            {
                ProcessCommand(command);
            }
        }

#endif

        #endregion Server Commands

        #region Helpers

        private void ProcessCommand(string command, IPlayer player = null)
        {
            if (!command.StartsWith("examplecmd") && !command.StartsWith("example.cmd"))
            {
                server.Command(ReplacePlaceholders(command, player));
            }
        }

        private string ReplacePlaceholders(string command, IPlayer player = null)
        {
            if (player != null)
            {
                command = command
                .Replace("$player.id", player.Id)
                .Replace("$player.name", player.Name)
                .Replace("$player.ip", player.Address)
                .Replace("$player.language", player.Language.TwoLetterISOLanguageName)
                .Replace("$player.ping", player.Ping.ToString())
                .Replace("$player.position", player.Position().ToString());
            }

            return command
                .Replace("$server.name", server.Name)
                .Replace("$server.ip", server.Address.ToString())
                .Replace("$server.port", server.Port.ToString())
                .Replace("$server.players", server.Players.ToString())
                .Replace("$server.language", server.Language.TwoLetterISOLanguageName)
                .Replace("$server.maxplayers", server.MaxPlayers.ToString())
                .Replace("$server.protocol", server.Protocol)
                .Replace("$server.version", server.Version);
        }

        #endregion Helpers
    }
}
