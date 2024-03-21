using Newtonsoft.Json;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using System.Linq;
using System;

namespace Oxide.Plugins
{
    [Info("Instant Craft", "Vlad-0003 / Orange / rostov114", "2.2.7")]
    [Description("Allows players to instantly craft items with features")]
    public class InstantCraft : RustPlugin
    {
        #region Vars
        private const string permUse = "instantcraft.use";
        private const string permNormal = "instantcraft.normal";
        private readonly object True = true;
        private readonly object False = false;
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permNormal, this);

            _config.Init(this);

            if (!_config.HasAnyBlockedItems)
            {
                Unsubscribe(nameof(CanCraft));
            }
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (_config.IsBlocked(blueprint.targetItem))
            {
                Message(itemCrafter.baseEntity, "Blocked");
                return False;
            }

            return null;
        }

        private object OnItemCraft(ItemCraftTask task, BasePlayer owner)
        {
            if (task.cancelled)
            {
                return null;
            }

            if (permission.UserHasPermission(owner.UserIDString, permNormal) || !permission.UserHasPermission(owner.UserIDString, permUse))
            {
                return null;
            }

            List<int> stacks = GetStacks(task.blueprint.targetItem, task.amount * task.blueprint.amountToCreate);
            int slots = FreeSlots(owner);
            if (!HasPlace(slots, stacks))
            {
                CancelTask(task, owner, "Slots", stacks.Count, slots);
                return False;
            }

            if (_config.IsNormal(task.blueprint.targetItem))
            {
                Message(owner, "Normal");
                return null;
            }

            if (!GiveItem(task, owner, stacks))
            {
                return null;
            }

            return True;
        }
        #endregion

        #region API

        [HookMethod(nameof(API_IsItemBlocked))]
        public bool API_IsItemBlocked(ItemDefinition itemDefinition)
        {
            return _config.IsBlocked(itemDefinition);
        }

        [HookMethod(nameof(API_GetIsItemBlockedCallback))]
        public Func<ItemDefinition, bool> API_GetIsItemBlockedCallback()
        {
            return itemDefinition => _config.IsBlocked(itemDefinition);
        }

        #endregion

        #region Helpers
        public void CancelTask(ItemCraftTask task, BasePlayer owner, string reason, params object[] args)
        {
            task.cancelled = true;
            Message(owner, reason, args);
            GiveRefund(task, owner);
            Interface.CallHook("OnItemCraftCancelled", task, owner.inventory.crafting);
        }

        public void GiveRefund(ItemCraftTask task, BasePlayer owner)
        {
            if (task.takenItems != null && task.takenItems.Count > 0)
            {
                foreach (var item in task.takenItems)
                {
                    owner.inventory.GiveItem(item, null);
                }
            }
        }

        public bool GiveItem(ItemCraftTask task, BasePlayer owner, List<int> stacks)
        {
            ulong skin = ItemDefinition.FindSkin(task.blueprint.targetItem.itemid, task.skinID);
            int iteration = 0;

            if (_config.split)
            {
                foreach (var stack in stacks)
                {
                    if (!Give(task, owner, stack, skin) && iteration <= 0)
                    {
                        return false;
                    }

                    iteration++;
                }
            }
            else
            {
                int final = 0;
                foreach (var stack in stacks)
                {
                    final += stack;
                }

                if (!Give(task, owner, final, skin))
                {
                    return false;
                }
            }

            task.cancelled = true;
            return true;
        }

        public bool Give(ItemCraftTask task, BasePlayer owner, int amount, ulong skin)
        {
            Item item = null;
            try
            {
                item = ItemManager.CreateByItemID(task.blueprint.targetItem.itemid, amount, skin);
            }
            catch (Exception e)
            {
                PrintError($"Exception creating item! targetItem: {task.blueprint.targetItem}-{amount}-{skin}; Exception: {e}");
            }

            if (item == null)
            {
                return false;
            }

            if (item.hasCondition && task.conditionScale != 1f)
            {
                item.maxCondition *= task.conditionScale;
                item.condition = item.maxCondition;
            }

            item.OnVirginSpawn();

            if (task.instanceData != null)
            {
                item.instanceData = task.instanceData;
            }

            Interface.CallHook("OnItemCraftFinished", task, item, owner.inventory.crafting);

            if (owner.inventory.GiveItem(item))
            {
                owner.Command("note.inv", new object[]{item.info.itemid, amount});
                return true;
            }

            ItemContainer itemContainer = owner.inventory.crafting.containers.First<ItemContainer>();
            owner.Command("note.inv", new object[]{item.info.itemid, item.amount});
            owner.Command("note.inv", new object[]{item.info.itemid, -item.amount});
            item.Drop(itemContainer.dropPosition, itemContainer.dropVelocity, default(Quaternion));

            return true;
        }

        public int FreeSlots(BasePlayer player)
        {
            var slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            var taken = player.inventory.containerMain.itemList.Count + player.inventory.containerBelt.itemList.Count;
            return slots - taken;
        }

        public List<int> GetStacks(ItemDefinition item, int amount)
        {
            var list = new List<int>();
            var maxStack = item.stackable;

            if (maxStack == 0)
            {
                maxStack = 1;
            }

            while (amount > maxStack)
            {
                amount -= maxStack;
                list.Add(maxStack);
            }

            list.Add(amount);

            return list; 
        }

        public bool HasPlace(int slots, List<int> stacks)
        {
            if (!_config.checkPlace)
            {
                return true;
            }

            if (_config.split && slots - stacks.Count < 0)
            {
                return false;
            }

            return slots > 0;
        }
        #endregion

        #region Localization 1.1.1
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Blocked", "Crafting of that item is blocked!"},
                {"Slots", "You don't have enough place to craft! Need {0}, have {1}!"},
                {"Normal", "Item will be crafted with normal speed."}
            }, this, "en");
        }

        public void Message(BasePlayer player, string messageKey, params object[] args)
        {
            if (player == null)
            {
                return;
            }

            var message = GetMessage(messageKey, player.UserIDString, args);
            player.ChatMessage(message);
        }

        public string GetMessage(string messageKey, string playerID, params object[] args)
        {
            return string.Format(lang.GetMessage(messageKey, this, playerID), args);
        }
        #endregion

        #region Configuration 1.1.0
        private Configuration _config;
        private class Configuration
        {
            [JsonProperty(PropertyName = "Check for free place")]
            public bool checkPlace = true;

            [JsonProperty(PropertyName = "Split crafted stacks")]
            public bool split = true;

            [JsonProperty(PropertyName = "Normal Speed")]
            private string[] normal =
            {
                "hammer",
                "put item shortname here"
            };

            [JsonProperty(PropertyName = "Blacklist")]
            private string[] blocked =
            {
                "rock",
                "put item shortname here"
            };

            private HashSet<int> _normalCraftItemIds = new HashSet<int>();
            private HashSet<int> _blockedItemIds = new HashSet<int>();

            [JsonIgnore]
            public bool HasAnyBlockedItems => _blockedItemIds.Count > 0;

            public void Init(InstantCraft plugin)
            {
                ValidateItemShortNames(plugin, normal, _normalCraftItemIds);
                ValidateItemShortNames(plugin, blocked, _blockedItemIds);
            }

            public bool IsNormal(ItemDefinition itemDefinition) => _normalCraftItemIds.Contains(itemDefinition.itemid);
            public bool IsBlocked(ItemDefinition itemDefinition) => _blockedItemIds.Contains(itemDefinition.itemid);

            private void ValidateItemShortNames(InstantCraft plugin, string[] itemShortNameList, HashSet<int> validItemIdList)
            {
                foreach (var itemShortName in itemShortNameList)
                {
                    if (itemShortName == "put item shortname here")
                        return;

                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition == null)
                    {
                        plugin.PrintError($"Invalid item short name in config: {itemShortName}");
                        continue;
                    }

                    validItemIdList.Add(itemDefinition.itemid);
                }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>();
                SaveConfig();
            }
            catch
            {
                PrintError("Error reading config, please check!");

                Unsubscribe(nameof(OnItemCraft));
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);
        #endregion
    }
}