using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Instant Untie", "MJSU", "1.0.13")]
[Description("Instantly untie underwater boxes")]
internal class InstantUntie : RustPlugin
{
    #region Class Fields
    private PluginConfig _pluginConfig; //Plugin Config

    private const string UsePermission = "instantuntie.use";

    private static InstantUntie _ins;
        
    private const string AccentColor = "#de8732";
        
    #endregion

    #region Setup & Loading
    private void Init()
    {
        _ins = this;
        permission.RegisterPermission(UsePermission, this);
    }

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Chat] = $"<color=#bebebe>[<color={AccentColor}>{Title}</color>] {{0}}</color>",
            [LangKeys.Untie] = "The box will untie in {0} seconds. Please hold the use key down until this is completed.",
            [LangKeys.Canceled] = "You have canceled untying the box. Please hold the use key down to untie."
        }, this);
    }
        
    protected override void LoadDefaultConfig()
    {
        PrintWarning("Loading Default Config");
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
        _pluginConfig = AdditionalConfig(Config.ReadObject<PluginConfig>());
        Config.WriteObject(_pluginConfig);
    }

    private PluginConfig AdditionalConfig(PluginConfig config)
    {
        return config;
    }

    private void OnServerInitialized()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            AddBehavior(player);
        }
    }

    private void OnPlayerConnected(BasePlayer player)
    {
        AddBehavior(player);
    }

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
        DestroyBehavior(player);
    }

    private void Unload()
    {
        foreach (UnderwaterBehavior water in GameObject.FindObjectsOfType<UnderwaterBehavior>())
        {
            water.DoDestroy();
        }

        _ins = null;
    }
    #endregion

    #region uMod Hooks
    private void OnUserPermissionGranted(string playerId, string permName)
    {
        if (permName != UsePermission)
        {
            return;
        }
            
        HandleUserChanges(playerId);
    }
        
    private void OnUserPermissionRevoked(string playerId, string permName)
    {
        if (permName != UsePermission)
        {
            return;
        }
            
        HandleUserChanges(playerId);
    }
        
    private void OnUserGroupAdded(string playerId, string groupName)
    {
        HandleUserChanges(playerId);
    }
        
    private void OnUserGroupRemoved(string playerId, string groupName)
    {
        HandleUserChanges(playerId);
    }

    private void OnGroupPermissionGranted(string groupName, string permName)
    {
        if (permName != UsePermission)
        {
            return;
        }

        NextTick(() =>
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                HandleUserChanges(player);
            }
        });
    }
        
    private void OnGroupPermissionRevoked(string groupName, string permName)
    {
        if (permName != UsePermission)
        {
            return;
        }
            
        NextTick(() =>
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                HandleUserChanges(player);
            }
        });
    }

    private void HandleUserChanges(string id)
    {
        NextTick(() =>
        {
            BasePlayer player = BasePlayer.Find(id);
            if (player == null)
            {
                return;
            }

            HandleUserChanges(player);
        });
    }

    private void HandleUserChanges(BasePlayer player)
    {
        bool hasPerm = HasPermission(player, UsePermission);
        bool hasBehavior = player.GetComponent<UnderwaterBehavior>() != null;
        if (hasPerm == hasBehavior)
        {
            return;
        }

        if (hasBehavior)
        {
            DestroyBehavior(player);
        }
        else
        {
            AddBehavior(player);
        }
    }
    #endregion

    #region Helper Methods
    private T Raycast<T>(Ray ray, float distance) where T : BaseEntity
    {
        RaycastHit hit;
        if (!Physics.Raycast(ray, out hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return null;
        }

        return hit.GetEntity() as T;
    }

    private void AddBehavior(BasePlayer player)
    {
        if (!HasPermission(player, UsePermission))
        {
            return;
        }

        if (player.GetComponent<UnderwaterBehavior>() == null)
        {
            player.gameObject.AddComponent<UnderwaterBehavior>();
        }
    }

    private void DestroyBehavior(BasePlayer player)
    {
        player.GetComponent<UnderwaterBehavior>()?.DoDestroy();
    }

    private void Chat(BasePlayer player, string format) => PrintToChat(player, Lang(LangKeys.Chat, player, format));
        
    private bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

    private string Lang(string key, BasePlayer player = null)
    {
        return lang.GetMessage(key, this, player ? player.UserIDString : null);
    }
        
    private string Lang(string key, BasePlayer player = null, params object[] args)
    {
        try
        {
            return string.Format(Lang(key, player), args);
        }
        catch(Exception ex)
        {
            PrintError($"Lang Key '{key}' threw exception\n:{ex}");
            throw;
        }
    }
    #endregion

    #region Behavior
    private class UnderwaterBehavior : FacepunchBehaviour
    {
        private BasePlayer _player;
        private FreeableLootContainer _box;
        private float _nextRaycastTime;

        private void Awake()
        {
            enabled = false;
            _player = GetComponent<BasePlayer>();
            InvokeRandomized(UpdateUnderwater, 0f, _ins._pluginConfig.UnderWaterUpdateRate, 0.1f);
        }

        private void UpdateUnderwater()
        {
            bool isUnderwater = _player.WaterFactor() == 1f;
            if (isUnderwater && !enabled)
            {
                enabled = true;
            }
            else if (!isUnderwater && enabled)
            {
                enabled = false;
            }
        }

        private void FixedUpdate()
        {
            if (!_box)
            {
                if (_nextRaycastTime < Time.realtimeSinceStartup && _player.serverInput.IsDown(BUTTON.USE))
                {
                    _box = _ins.Raycast<FreeableLootContainer>(_player.eyes.HeadRay(), 3f);
                    if (!_box || !_box.IsTiedDown())
                    {
                        return;
                    }

                    _nextRaycastTime = Time.realtimeSinceStartup + _ins._pluginConfig.HeldKeyUpdateRate;
                    CancelInvoke(Untie);
                    Invoke(Untie, _ins._pluginConfig.UntieDuration);
                    if (_ins._pluginConfig.ShowUntieMessage)
                    {
                        _ins.Chat(_player, _ins.Lang(LangKeys.Untie, _player, _ins._pluginConfig.UntieDuration));
                    }
                }
            }
            else if (!_player.serverInput.IsDown(BUTTON.USE))
            {
                _box = null;
                CancelInvoke(Untie);

                if (_ins._pluginConfig.ShowCanceledMessage)
                {
                    _ins.Chat(_player, _ins.Lang(LangKeys.Canceled));
                }
            }
        }

        private void Untie()
        {
            if (!_box || !_box.IsTiedDown())
            {
                return;
            }

            _box.buoyancy.buoyancyScale = _ins._pluginConfig.BuoyancyScale;
            _box.GetRB().isKinematic = false;
            _box.buoyancy.enabled = true;
            _box.SetFlag(BaseEntity.Flags.Reserved8, false);
            _box.SendNetworkUpdate();
            if (_ins._pluginConfig.SetOwnerId)
            {
                _box.OwnerID = _player.userID;
            }
            if (_box.freedEffect.isValid)
            {
                Effect.server.Run(_box.freedEffect.resourcePath, _box.transform.position, Vector3.up);
            }

            _player.ProcessMissionEvent(BaseMission.MissionEventType.FREE_CRATE, _box.net.ID, 1f);
                
            _box = null;
        }

        public void DoDestroy()
        {
            Destroy(this);
        }
    }
    #endregion

    #region Classes

    private class LangKeys
    {
        public const string Chat = "Chat";
        public const string Untie = "Untie";
        public const string Canceled = "UntieCanceled";
    }

    private class PluginConfig
    {
        [DefaultValue(0f)]
        [JsonProperty(PropertyName = "Untie Duration (Seconds)")]
        public float UntieDuration { get; set; }

        [DefaultValue(5f)]
        [JsonProperty(PropertyName = "How often to check if player is underwater (Seconds)")]
        public float UnderWaterUpdateRate { get; set; }
            
        [DefaultValue(1f)]
        [JsonProperty(PropertyName = "How often to check if a player is holding the use button (Seconds)")]
        public float HeldKeyUpdateRate { get; set; }
            
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Show Untie Message")]
        public bool ShowUntieMessage { get; set; }
            
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Show canceled message")]
        public bool ShowCanceledMessage { get; set; }
            
        [DefaultValue(1)]
        [JsonProperty(PropertyName = "Buoyancy Scale")]
        public float BuoyancyScale { get; set; }
            
        [DefaultValue(false)]
        [JsonProperty(PropertyName = "Set box owner as untie player")]
        public bool SetOwnerId { get; set; }
    }
    #endregion
}