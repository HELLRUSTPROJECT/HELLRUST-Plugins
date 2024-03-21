using Oxide.Plugins.BiplaneExtensionMethods;
using System.Collections.Generic;
using Oxide.Game.Rust.Cui;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using Rust.Modular;
using UnityEngine;
using Oxide.Core;
using System;

namespace Oxide.Plugins
{
    [Info("Biplane", "Adem", "1.0.8")]
    class Biplane : RustPlugin
    {
        #region Variables
        const bool en = true;
        bool unload = false;
        private static Biplane ins;
        HashSet<BiplaneComponent> biplanes = new HashSet<BiplaneComponent>();
        List<SellerData> sellers = new List<SellerData>();
        HashSet<MonumentInfo> monuments = new HashSet<MonumentInfo>();
        Coroutine spawnCorountine;
        Coroutine saveCoroutine;
        #endregion Variables

        #region Hooks
        void OnServerInitialized()
        {
            ins = this;
            UpdateConfig();
            foreach (BiplaneSetting biplaneSetting in _config.biplanePresets.Values) { if (biplaneSetting.permission != "") permission.RegisterPermission(biplaneSetting.permission, this); }
            if (!_config.samSite && !_config.samSiteMonuments) Unsubscribe("OnSamSiteTargetScan");
            LoadDefaultMessages();
            permission.RegisterPermission(_config.itemPermission, this);
            foreach (MonumentInfo monumentInfo in TerrainMeta.Path.Monuments.Where(x => _config.spawnSetting.monumentSpawnSettings.ContainsKey(x.name))) monuments.Add(monumentInfo);
            if (_config.spawnSetting.monumentSpawnSettings.Any(x => x.Value.enableSpawn) || _config.spawnSetting.customSpawnPointsConfig.enableSpawn) spawnCorountine = ServerMgr.Instance.StartCoroutine(SpawnCorountine());
            SpawnSellersAtMonuments();

            LoadData();
            foreach (ModularCar modularCar in ModularCar.allCarsList)
            {
                if (!modularCar.IsExists() || modularCar.NumAttachedModules > 1) continue;

                SaveBiplaneData saveBiplaneData = saveBiplanes.FirstOrDefault(x => x.biplaneId == modularCar.net.ID.Value);
                if (saveBiplaneData == null) continue;

                BaseVehicleModule baseVehicleModule;
                modularCar.TryGetModuleAt(0, out baseVehicleModule);
                if (modularCar.NumAttachedModules == 0 || (modularCar.NumAttachedModules == 1 && baseVehicleModule != null && baseVehicleModule.name == "assets/content/vehicles/modularcar/module_entities/1module_engine.prefab")) 
                    LoadBiplane(modularCar, saveBiplaneData);
            }

            timer.In(1f, () => saveCoroutine = ServerMgr.Instance.StartCoroutine(SaveCorountine()));
        }

        void Unload()
        {
            if (saveCoroutine != null) ServerMgr.Instance.StopCoroutine(saveCoroutine);
            unload = true;
            if (spawnCorountine != null) ServerMgr.Instance.StopCoroutine(spawnCorountine);
            foreach (SellerData sellerData in sellers) sellerData.Destroy();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, "BiplaneSellerPanel");
                CuiHelper.DestroyUi(player, "FuelText");
            }
            saveBiplanes.Clear();
            foreach (BiplaneComponent biplane in biplanes)
            {
                SaveBiplane(biplane);
                UnityEngine.Object.DestroyImmediate(biplane, true);
            }
            SaveData();
            ins = null;
        }

        object OnEntityTakeDamage(DecorWing entity, HitInfo info)
        {
            if (entity == null || info == null) return null;
            if (entity.ShortPrefabName.Contains("kayak") || entity.ShortPrefabName.Contains("boogieboard"))
            {
                BiplaneComponent biplaneComponent = biplanes.FirstOrDefault(x => x.subEntities.Contains(entity));
                if (biplaneComponent == null) return null;
                biplaneComponent.modularCar.Hurt(info);
                return true;
            }
            return null;
        }

        object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (player == null || entity == null || !player.userID.IsSteamId()) return null;
            BiplaneComponent biplaneComponent = biplanes.FirstOrDefault(x => x != null && (x.fakeSeat != null && x.fakeSeat == entity) || (x.pilotSeat != null && x.pilotSeat == entity));
            if (biplaneComponent != null)
            {
                if (biplaneComponent.biplaneSetting.permission != "" && !permission.UserHasPermission(player.UserIDString, biplaneComponent.biplaneSetting.permission))
                {
                    PrintToChat(player, GetMessage("NoPermission", player.UserIDString, ins._config.prefics));
                    return true;
                }
                else if (biplaneComponent.modularCar.CarLock != null && !biplaneComponent.modularCar.CarLock.PlayerCanUseThis(player, ModularCarCodeLock.LockType.Door))
                {
                    PrintToChat(player, GetMessage("Lock", player.UserIDString, ins._config.prefics));
                    return true;
                }
                else if (biplaneComponent.fakeSeat == entity)
                {
                    biplaneComponent.MountPlayer(player);
                    return true;
                }
            }
            return null;
        }

        void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (plan == null || go == null) return;
            Item item = plan.GetItem();
            if (item == null) return;

            var pair = _config.itemsPresets.FirstOrDefault(x => x.Value.shortname == item.info.shortname && x.Value.skin == item.skin);
            if (pair.Value == null || pair.Key == null) return;

            BaseEntity entity = go.GetComponent<BaseEntity>();
            if (entity == null) return;

            CreateBiplane(entity.transform.position, entity.transform.rotation, _config.biplanePresets.FirstOrDefault(x => x.Value.customItemShortname == pair.Key).Key, true, false);
            NextTick(() => entity.Kill());
        }

        object OnNpcConversationStart(VehicleVendor vehicleVendor, BasePlayer player, ConversationData conversationData)
        {
            if (vehicleVendor != null)
            {
                SellerData sellerData = sellers.FirstOrDefault(x => x.seller == vehicleVendor);
                if (sellerData != null)
                {
                    CreateGui(player, sellerData.sellerPrefab, sellers.IndexOf(sellerData));
                    return true;
                }
            }
            return null;
        }

        void OnEntityMounted(BaseMountable entity, BasePlayer player)
        {
            if (entity == null) return;
            BiplaneComponent biplaneComponent = biplanes.FirstOrDefault(x => x != null && x.pilotSeat != null && x.pilotSeat.net.ID == entity.net.ID);
            if (biplaneComponent != null) biplaneComponent.UnSleep();
        }

        void OnEntityDismounted(BaseMountable entity, BasePlayer player)
        {
            if (entity == null) return;
            BiplaneComponent biplaneComponent = biplanes.FirstOrDefault(x => x != null && x.pilotSeat != null && x.pilotSeat == entity);
            if (biplaneComponent != null) CuiHelper.DestroyUi(player, "FuelText");
        }

        object OnEngineStart(BaseVehicle vehicle, BasePlayer driver)
        {
            if (vehicle == null || driver == null) return null;
            BiplaneComponent biplaneComponent = biplanes.FirstOrDefault(x => x.modularCar.net.ID == vehicle.net.ID);
            if (biplaneComponent == null) return null;
            if (biplaneComponent.modularCar.CarLock != null && !biplaneComponent.modularCar.CarLock.PlayerCanUseThis(driver, ModularCarCodeLock.LockType.Door))
            {
                PrintToChat(driver, GetMessage("Lock", driver.UserIDString, ins._config.prefics));
                return true;
            }
            return null;
        }

        object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (player == null || container == null) return null;
            BiplaneComponent biplaneComponent = null;
            if (container.ShortPrefabName == "rhib_storage") biplaneComponent = biplanes.FirstOrDefault(x => x.container.net.ID == container.net.ID);

            else if (container.ShortPrefabName == "modular_car_v8_engine_storage" || container.ShortPrefabName == "modular_car_fuel_storage") biplaneComponent = container.GetComponentInParent<BiplaneComponent>();

            if (biplaneComponent == null) return null;
            if (biplaneComponent.biplaneSetting.permission != "" && !permission.UserHasPermission(player.UserIDString, biplaneComponent.biplaneSetting.permission))
            {
                PrintToChat(player, GetMessage("NoPermission", player.UserIDString, ins._config.prefics));
                return true;
            }
            if (biplaneComponent.modularCar.CarLock != null && !biplaneComponent.modularCar.CarLock.PlayerCanUseThis(player, ModularCarCodeLock.LockType.Door))
            {
                PrintToChat(player, GetMessage("Lock", player.UserIDString, ins._config.prefics));
                return true;
            }

            return null;
        }

        object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity.PrefabName.Contains("kayak") && entity is Kayak == false)
            {
                BiplaneComponent biplaneComponent = entity.GetComponentInParent<BiplaneComponent>();
                if (biplaneComponent == null) return null;
                if (biplaneComponent.biplaneSetting.customItemShortname != "")
                {
                    if (!ins._config.allowIpckUp) return false;
                    if (biplaneComponent.biplaneSetting.permission != "" && !permission.UserHasPermission(player.UserIDString, biplaneComponent.biplaneSetting.permission))
                    {
                        PrintToChat(player, GetMessage("NoPermission", player.UserIDString, ins._config.prefics));
                        return false;
                    }
                    if (biplaneComponent.modularCar.CarLock != null && !biplaneComponent.modularCar.CarLock.PlayerCanUseThis(player, ModularCarCodeLock.LockType.Door))
                    {
                        PrintToChat(player, GetMessage("Lock", player.UserIDString, ins._config.prefics));
                        return false;
                    }
                    else if (biplaneComponent.modularCar.GetFuelSystem().GetFuelAmount() > 0 || biplaneComponent.container.inventory.itemList.Count > 0)
                    {
                        PrintToChat(player, GetMessage("EmptyСontainers", player.UserIDString, ins._config.prefics));
                        return false;
                    }
                    foreach (BaseVehicleModule module in biplaneComponent.modularCar.AttachedModuleEntities)
                    {
                        VehicleModuleEngine engineModule = module as VehicleModuleEngine;
                        if (engineModule == null) continue;
                        Rust.Modular.EngineStorage engineStorage = engineModule.GetContainer() as Rust.Modular.EngineStorage;
                        if (engineStorage == null) continue;

                        if (engineStorage.inventory.itemList.Count > 0)
                        {
                            PrintToChat(player, GetMessage("EmptyСontainers", player.UserIDString, ins._config.prefics));
                            return false;
                        }
                    }
                    MoveItem(player, CreateItem(_config.itemsPresets[biplaneComponent.biplaneSetting.customItemShortname]));
                    biplaneComponent.modularCar.Kill();
                }
                return false;
            }
            return null;
        }

        void OnLicensedVehicleSpawned(BaseEntity entity, BasePlayer player, string vehicleType)
        {
            if (vehicleType != null && _config.biplanePresets.ContainsKey(vehicleType) && entity != null && entity.ShortPrefabName.Contains("3module.entity"))
            {
                BiplaneComponent biplaneComponent = entity.gameObject.AddComponent<BiplaneComponent>();
                biplaneComponent.Init(vehicleType, false);
                biplanes.Add(biplaneComponent);
            }
        }
        #endregion Hooks

        #region Commands
        [ChatCommand("biplanemonument")]
        private void ChatNewBiplaneMonument(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            MonumentInfo monumentInfo = TerrainMeta.Path.Monuments.FirstOrDefault(x => Vector3.Distance(player.transform.position, x.transform.position) < x.Bounds.size.x);
            if (monumentInfo == null)
            {
                PrintToChat(player, "Monument not found!");
                return;
            }
            if (_config.spawnSetting.monumentSpawnSettings.ContainsKey(monumentInfo.name))
            {
                PrintToChat(player, "The monument has already been added");
                return;
            }
            _config.spawnSetting.monumentSpawnSettings.Add(monumentInfo.name, new MonumentConfig());
            SaveConfig();
        }

        [ChatCommand("biplanemonumentpoint")]
        private void ChatNewBiplanePoint(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            MonumentConfig monumentConfig;
            LocationConfig locationConfig;
            DefineMonumentAndPlayerLocation(player, out monumentConfig, out locationConfig);
            if (monumentConfig == null || locationConfig == null) return;
            monumentConfig.points.Add(locationConfig);
            PrintToChat(player, "The biplane spawn point has been successfully added to the location");
            SaveConfig();
        }

        [ChatCommand("biplaneseller")]
        private void ChatNewBiplaneSeller(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            MonumentConfig monumentConfig;
            LocationConfig locationConfig;
            DefineMonumentAndPlayerLocation(player, out monumentConfig, out locationConfig);
            if (monumentConfig == null || locationConfig == null) return;
            monumentConfig.sellerLocation = locationConfig;
            PrintToChat(player, "The seller's spawn point has been successfully added to the location");
            SaveConfig();
        }

        [ChatCommand("biplanesellerpoint")]
        private void ChatNewBiplaneSellerPoint(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            MonumentConfig monumentConfig;
            LocationConfig locationConfig;
            DefineMonumentAndPlayerLocation(player, out monumentConfig, out locationConfig);
            if (monumentConfig == null || locationConfig == null) return;
            monumentConfig.sellerBiplaneLocation = locationConfig;
            PrintToChat(player, "The seller's biplane spawn point has been successfully added to the location");
            SaveConfig();
        }

        [ChatCommand("givebiplane")]
        void BiplaneGiveCommandk(BasePlayer player, string command, string[] arg)
        {
            if (player == null || arg == null || arg.Length == 0) return;
            if (!_config.itemsPresets.ContainsKey(arg[0])) return;

            if (!permission.UserHasPermission(player.UserIDString, _config.itemPermission))
            {
                PrintToChat(player, "You do not have permission to use this command!");
                return;
            }

            MoveItem(player, CreateItem(_config.itemsPresets[arg[0]]));
            PrintToChat(player, GetMessage("GetBiplane", player.UserIDString, ins._config.prefics));
        }

        [ConsoleCommand("givebiplane")]
        void GiveCustomItemCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            BasePlayer target = null;
            bool rcon = player == null;
            if (arg.Args == null || arg.Args.Length == 0) return;

            if (player != null)
            {
                if (!permission.UserHasPermission(player.UserIDString, _config.itemPermission) && !rcon)
                {
                    PrintToConsole(player, "You do not have permission to use this command!");
                    return;
                }
                if (arg.Args.Length == 1 && !rcon) target = player;
                else target = BasePlayer.FindByID(Convert.ToUInt64(arg.Args[1]));
            }

            else if (arg.Args.Length >= 2) target = BasePlayer.FindByID(Convert.ToUInt64(arg.Args[1]));

            if (target == null)
            {
                if (rcon) Puts("Player not found");
                else PrintToConsole(player, "Player not found");
                return;
            }
            MoveItem(target, CreateItem(_config.itemsPresets[arg.Args[0]]));
            PrintToChat(target, GetMessage("GetBiplane", target.UserIDString, ins._config.prefics));
            if (rcon) Puts($"A biplane was given to {target.displayName}");
            else PrintToConsole(player, $"A biplane was given to {target.displayName}");
        }

        [ChatCommand("biplanecustomspawnpoint")]
        private void ChatNewCustomSpawnPoint(BasePlayer player)
        {
            if (!player.IsAdmin) return;
            _config.spawnSetting.customSpawnPointsConfig.points.Add(new LocationConfig { position = player.transform.position.ToString(), rotation = player.viewAngles.y.ToString() });
            PrintToChat(player, "Custom spawn point successfully added");
            SaveConfig();
        }
        #endregion Commands

        #region Methods
        void UpdateConfig()
        {
            if (_config.version != Version.ToString())
            {
                VersionNumber versionNumber;
                var versionArray = _config.version.Split('.');
                versionNumber.Major = Convert.ToInt32(versionArray[0]);
                versionNumber.Minor = Convert.ToInt32(versionArray[1]);
                versionNumber.Patch = Convert.ToInt32(versionArray[2]);

                if (versionNumber.Patch == 0)
                {
                    PrintError("Delete the configuration file!");
                    NextTick(() => Server.Command($"o.unload {Name}"));
                    return;
                }
                if (versionNumber.Patch == 1)
                {
                    if (!_config.itemsPresets.ContainsKey("biplaneitem_airfield")) _config.itemsPresets.Add("biplaneitem_airfield",
                        new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561072,
                            name = "Biplane Airfield"
                        }
                    );
                    if (!_config.itemsPresets.ContainsKey("biplaneitem_bomber")) _config.itemsPresets.Add("biplaneitem_bomber",
                        new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561506,
                            name = "Biplane Airfield"
                        }
                    );
                    if (!_config.itemsPresets.ContainsKey("biplaneitem_stormtrooper")) _config.itemsPresets.Add("biplaneitem_stormtrooper",
                        new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561787,
                            name = "Biplane Airfield"
                        }
                    );

                    foreach (var pair in _config.biplanePresets)
                    {
                        if (pair.Key == "biplane_airfield")
                        {
                            pair.Value.customItemShortname = "biplaneitem_airfield";
                        }
                        else if (pair.Key == "biplane_bomber")
                        {
                            pair.Value.customItemShortname = "biplaneitem_bomber";
                        }
                        else if (pair.Key == "biplane_stormtrooper")
                        {
                            pair.Value.customItemShortname = "biplaneitem_stormtrooper";
                        }
                        else if (pair.Key == "biplane_default")
                        {
                            pair.Value.bombs = false;
                            pair.Value.rockets = false;
                        }
                        pair.Value.permission = "";
                    }
                }
                if (versionNumber.Patch <= 2)
                {
                    _config.marker = new MarkerConfig
                    {
                        enable = true,
                        text = "Biplane seller"
                    };
                }
                if (versionNumber.Patch <= 3)
                {
                    _config.allowIpckUp = true;
                    _config.spawnSetting.customSpawnPointsConfig = new CustomSpawnPointsConfig
                    {
                        enableSpawn = false,
                        presets = new Dictionary<string, float>
                        {
                            ["biplane_airfield"] = 100
                        },
                        points = new List<LocationConfig>()
                    };
                }
                if (versionNumber.Patch <= 4)
                {
                    foreach (string key in _config.biplanePresets.Keys)
                    {
                        BiplaneSetting biplaneSetting = _config.biplanePresets[key];
                        biplaneSetting.biplaneControlSetting = new BiplaneControlSetting
                        {
                            turningADSpeed = 1,
                            turningMouseSpeed = 1,
                        };
                    }
                }

                _config.version = Version.ToString();
                SaveConfig();
            }
        }

        IEnumerator SpawnCorountine()
        {
            while (true)
            {
                yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(_config.spawnSetting.minRespawnTime, _config.spawnSetting.maxRespawnTime));
                SpawnBipaneAtMonuments();
                SpawnBipaneAtCustomPosition();
            }
        }

        IEnumerator SaveCorountine()
        {
            while (true)
            {
                saveBiplanes.Clear();
                foreach (BiplaneComponent biplane in biplanes) SaveBiplane(biplane);
                SaveData();
                yield return CoroutineEx.waitForSeconds(3600);
            }
        }

        void DefineMonumentAndPlayerLocation(BasePlayer player, out MonumentConfig monumentConfig, out LocationConfig locationConfig)
        {
            MonumentInfo monumentInfo = TerrainMeta.Path.Monuments.FirstOrDefault(x => _config.spawnSetting.monumentSpawnSettings.ContainsKey(x.name) && Vector3.Distance(player.transform.position, x.transform.position) < x.Bounds.size.x);
            if (monumentInfo == null)
            {
                PrintToChat(player, "The monument was not found in the config. To add it, use the <addbiplanemonument> command ");
                monumentConfig = null;
                locationConfig = null;
                return;
            }
            MonumentConfig newMonumentConfig = _config.spawnSetting.monumentSpawnSettings[monumentInfo.name];
            LocationConfig newLocationConfig = new LocationConfig
            {
                position = monumentInfo.transform.InverseTransformPoint(player.transform.position).ToString(),
                rotation = (player.viewAngles - monumentInfo.transform.rotation.eulerAngles).y.ToString()
            };
            monumentConfig = newMonumentConfig;
            locationConfig = newLocationConfig;
        }

        void SpawnSellersAtMonuments()
        {
            foreach (MonumentInfo monumentInfo in monuments)
            {
                MonumentConfig monumentConfig = _config.spawnSetting.monumentSpawnSettings[monumentInfo.name];
                if (!monumentConfig.enableSeller || monumentConfig.sellerLocation == null) continue;
                Vector3 position = monumentInfo.transform.TransformPoint(monumentConfig.sellerLocation.position.ToVector3());
                VehicleVendor seller = GameManager.server.CreateEntity("assets/prefabs/npc/bandit/shopkeepers/bandit_conversationalist.prefab", position, monumentInfo.transform.rotation * Quaternion.Euler(new Vector3(0, float.Parse(monumentConfig.sellerLocation.rotation), 0))) as VehicleVendor;
                seller.enableSaving = false;
                seller.Spawn();
                if (!_config.marker.enable) sellers.Add(new SellerData(seller, monumentConfig.sellerName, monumentInfo.transform, monumentInfo.name, null));
                else sellers.Add(new SellerData(seller, monumentConfig.sellerName, monumentInfo.transform, monumentInfo.name, CreateMapMarker(position)));
            }
        }

        void SpawnBipaneAtMonuments()
        {
            foreach (MonumentInfo monumentInfo in monuments)
            {
                MonumentConfig monumentConfig = _config.spawnSetting.monumentSpawnSettings[monumentInfo.name];
                if (monumentConfig.points.Count == 0 || monumentConfig.presets.Count == 0 || !monumentConfig.presets.Values.Any(x => x > 0)) continue;
                LocationConfig locationConfig = monumentConfig.points.GetRandom();

                string biplanePreset = "";

                while (String.IsNullOrEmpty(biplanePreset))
                {
                    foreach (var pair in monumentConfig.presets)
                    {
                        if (UnityEngine.Random.Range(0f, 100f) <= pair.Value)
                        {
                            biplanePreset = pair.Key;
                            break;
                        }
                    }
                }

                Vector3 position = monumentInfo.transform.TransformPoint(locationConfig.position.ToVector3());
                if (biplanes.Any(x => Vector3.Distance(x.transform.position, position) < 10f)) continue;
                CreateBiplane(position, monumentInfo.transform.rotation * Quaternion.Euler(new Vector3(0, float.Parse(locationConfig.rotation), 0)), biplanePreset);
            }
        }

        void SpawnBipaneAtCustomPosition()
        {
            if (!_config.spawnSetting.customSpawnPointsConfig.enableSpawn) return;
            foreach (LocationConfig locationConfig in _config.spawnSetting.customSpawnPointsConfig.points)
            {
                string biplanePreset = "";
                while (String.IsNullOrEmpty(biplanePreset))
                {
                    foreach (var pair in _config.spawnSetting.customSpawnPointsConfig.presets)
                    {
                        if (UnityEngine.Random.Range(0f, 100f) <= pair.Value)
                        {
                            biplanePreset = pair.Key;
                            break;
                        }
                    }
                }
                if (biplanes.Any(x => Vector3.Distance(x.transform.position, locationConfig.position.ToVector3()) < 10f)) continue;
                CreateBiplane(locationConfig.position.ToVector3(), Quaternion.Euler(0, Convert.ToSingle(locationConfig.rotation), 0), biplanePreset);
            }
        }

        void CreateBiplane(Vector3 position, Quaternion rotation, string presetName, bool firstSpawn = true, bool addModule = true)
        {
            ModularCar car = GameManager.server.CreateEntity("assets/content/vehicles/modularcar/3module_car_spawned.entity.prefab", position, rotation) as ModularCar;
            BiplaneComponent biplaneComponent = car.gameObject.AddComponent<BiplaneComponent>();
            car.spawnSettings.useSpawnSettings = false;
            car.Spawn();
            biplaneComponent.Init(presetName, firstSpawn, addModule);
            biplanes.Add(biplaneComponent);
        }

        void SaveBiplane(BiplaneComponent biplaneComponent)
        {
            if (biplaneComponent == null || !biplaneComponent.modularCar.IsExists()) return;
            SaveBiplaneData saveBiplaneData = new SaveBiplaneData(biplaneComponent.modularCar.net.ID.Value, biplaneComponent.presetName, 0);
            foreach (Item item in biplaneComponent.container.inventory.itemList)
            {
                if (item == null) continue;
                SaveItemData saveItemData = new SaveItemData(item.info.shortname, item.name, item.amount, item.skin, item.condition);
                BaseProjectile projectile = item.GetHeldEntity() as BaseProjectile;
                if (projectile != null)
                {
                    saveItemData.projectileData = new ProjectileData(projectile.primaryMagazine.ammoType.shortname, projectile.primaryMagazine.contents);
                    foreach (Item modItem in item.contents.itemList) saveItemData.projectileData.mods.Add(modItem.info.shortname);
                }
                saveBiplaneData.items.Add(saveItemData);
            }
            saveBiplanes.Add(saveBiplaneData);
        }

        void LoadBiplane(ModularCar modularCar, SaveBiplaneData saveBiplaneData)
        {
            BiplaneComponent biplaneComponent = modularCar.gameObject.AddComponent<BiplaneComponent>();
            if (saveBiplaneData.keyIndex != 0)
            {
                //modularCar.carLock.AddALock();
                //modularCar.carLock.LockID = saveBiplaneData.keyIndex;
            }
            biplaneComponent.Init(saveBiplaneData.biplanePrefab, false);
            biplanes.Add(biplaneComponent);
            if (saveBiplaneData.items.Count > 0) timer.In(1f, () =>
            {
                foreach (SaveItemData saveItemData in saveBiplaneData.items)
                {
                    Item newItem = ItemManager.CreateByName(saveItemData.shortname, saveItemData.amount, saveItemData.skin);
                    newItem.condition = saveItemData.condition;
                    if (saveItemData.customName != null) newItem.name = saveItemData.customName;
                    if (!newItem.MoveToContainer(biplaneComponent.container.inventory))
                    {
                        newItem.Remove();
                        continue;
                    }
                    if (saveItemData.projectileData != null)
                    {
                        BaseProjectile projectile = newItem.GetHeldEntity() as BaseProjectile;
                        if (projectile != null)
                        {
                            if (saveItemData.projectileData.ammoShortname != "") projectile.primaryMagazine.ammoType = ItemManager.FindItemDefinition(saveItemData.projectileData.ammoShortname);
                            projectile.primaryMagazine.contents = saveItemData.projectileData.ammoAmount;
                            foreach (string shortname in saveItemData.projectileData.mods)
                            {
                                ItemDefinition itemDefinition = ItemManager.FindItemDefinition(shortname);
                                if (itemDefinition != null) newItem.contents.AddItem(itemDefinition, 1);
                            }
                        }
                    }
                }
            });
        }

        VendingMachineMapMarker CreateMapMarker(Vector3 position)
        {
            VendingMachineMapMarker vendingMarker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", position) as VendingMachineMapMarker;
            vendingMarker.enableSaving = false;
            vendingMarker.Spawn();
            vendingMarker.markerShopName = _config.marker.text;
            return vendingMarker;
        }

        private static void CopySerializableFields<T>(T src, T dst)
        {
            FieldInfo[] srcFields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo field in srcFields)
            {
                object value = field.GetValue(src);
                field.SetValue(dst, value);
            }
        }
        #endregion Methods

        #region Classes
        class BiplaneComponent : FacepunchBehaviour
        {
            internal Coroutine controlCorountine;
            internal string presetName;
            internal BiplaneSetting biplaneSetting;
            internal StorageContainer container;
            internal ModularCar modularCar;
            internal BaseMountable fakeSeat;
            internal List<BaseEntity> subEntities = new List<BaseEntity>();
            List<BaseEntity> rotors = new List<BaseEntity>();
            Rigidbody rigidbody;
            internal BaseMountable pilotSeat;
            List<BaseEntity> rockets = new List<BaseEntity>();
            bool active = true;
            internal bool grounded = true;
            bool engineOn = false;
            int lastFuelAmount = 0;
            float weaonDelay = 1f;
            bool leftRocket = true;

            internal void Init(string presetName, bool firstSpawn = true, bool addModule = true)
            {
                this.presetName = presetName;
                biplaneSetting = ins._config.biplanePresets[presetName];

                modularCar = GetComponent<ModularCar>();
                modularCar.spawnSettings.useSpawnSettings = false;
                modularCar.Inventory.ModuleContainer.capacity = 1;
                modularCar.Inventory.ModuleContainer.SetOnlyAllowedItem(ItemManager.FindItemDefinition("vehicle.1mod.engine"));
                foreach (BaseVehicle.MountPointInfo mountPointInfo in modularCar.mountPoints) mountPointInfo.mountable.Kill();
                modularCar.mountPoints.Clear();

                rigidbody = modularCar.rigidBody;
                rigidbody.drag = 0.1f;


                Invoke(() => Buid(firstSpawn, addModule), 0.1f);
            }

            void Buid(bool firstSpawn, bool addModule)
            {
                if (firstSpawn)
                {
                    Item moduleItem = ItemManager.CreateByName("vehicle.1mod.engine");
                    modularCar.TryAddModule(moduleItem, 0);
                    if (addModule) Invoke(EngineBuild, 0.25f);
                }
                SpawnSubEntity();
                SpawnSeat();
                SpawnRotor();

            }

            void EngineBuild()
            {
                if (biplaneSetting.fuelAmount > 0)
                {
                    StorageContainer fuelContainer = modularCar.GetFuelSystem().GetFuelContainer();
                    fuelContainer.inventory.AddItem(fuelContainer.allowedItem, biplaneSetting.fuelAmount);
                    fuelContainer.isLootable = false;
                }
                if (biplaneSetting.engineComponents)
                {
                    foreach (BaseVehicleModule module in modularCar.AttachedModuleEntities)
                    {
                        VehicleModuleEngine engineModule = module as VehicleModuleEngine;
                        if (engineModule == null) continue;
                        Rust.Modular.EngineStorage engineStorage = engineModule.GetContainer() as Rust.Modular.EngineStorage;
                        if (engineStorage == null) continue;
                        ItemContainer inventory = engineStorage.inventory;

                        for (var i = 0; i < inventory.capacity; i++)
                        {
                            ItemModEngineItem output;
                            if (!engineStorage.allEngineItems.TryGetItem(biplaneSetting.engineComponentsLvl, engineStorage.slotTypes[i], out output)) continue;
                            ItemDefinition component = output.GetComponent<ItemDefinition>();
                            Item item = ItemManager.Create(component);
                            if (item == null) continue;
                            item.conditionNormalized = 100;
                            item.MoveToContainer(engineStorage.inventory, i, allowStack: false);
                        }
                        engineModule.RefreshPerformanceStats(engineStorage);
                        return;
                    }
                }
            }

            void SpawnRotor()
            {
                foreach (BaseEntity rotor in rotors) if (rotor != null && !rotor.IsDestroyed) rotor.Kill();
                rotors.Clear();

                if (rotors.Count == 0)
                {
                    foreach (RotorData rotorData in ins.rotorPositions)
                    {
                        BaseEntity entity = GameManager.server.CreateEntity("assets/prefabs/weapons/sword big/longsword.entity.prefab", Vector3.zero, Quaternion.identity, false);
                        entity.enableSaving = false;
                        entity.SetParent(modularCar);
                        entity.transform.localPosition = rotorData.position;
                        entity.transform.localEulerAngles = rotorData.rotation;
                        entity.SetFlag(BaseEntity.Flags.Reserved8, true);
                        entity.Spawn();
                        rotors.Add(entity);
                        if (rotorData.hide) entity.limitNetworking = true;
                    }
                }
                else
                {
                    for (int i = 0; i < ins.rotorPositions.Count; i++)
                    {
                        if (i >= rotors.Count) return;
                        BaseEntity rotor = rotors[i];
                        if (!rotor.IsExists()) continue;
                        if (ins.rotorPositions[i].hide) rotor.limitNetworking = true;
                        else rotor.limitNetworking = false;
                    }
                }
            }

            void SpawnSubEntity()
            {
                foreach (SubEntityData subEntityData in ins.subEntityDatas)
                {
                    if (subEntityData.type == 4 && !biplaneSetting.rockets) continue; BaseEntity entity = GameManager.server.CreateEntity(subEntityData.name, Vector3.zero, Quaternion.identity, false);
                    entity.enableSaving = false;
                    if (subEntityData.type == 0)
                    {
                        DecorWing newEntity = entity.gameObject.AddComponent<DecorWing>();
                        CopySerializableFields(entity, newEntity);
                        UnityEngine.Object.DestroyImmediate(entity, true);
                        newEntity.gameObject.AwakeFromInstantiate();
                        newEntity.enableSaving = false;
                        newEntity.SetParent(modularCar);
                        newEntity.transform.localPosition = subEntityData.position;
                        newEntity.transform.localEulerAngles = subEntityData.rotation;
                        newEntity.Spawn();
                        newEntity.InitializeHealth(200, 200);
                        newEntity.SetFlag(BaseEntity.Flags.Reserved7, true);
                        newEntity.SetFlag(BaseEntity.Flags.On, true);
                        newEntity.SetFlag(BaseEntity.Flags.Busy, true);
                        Rigidbody entityRigidbody = newEntity.GetComponent<Rigidbody>();
                        if (entityRigidbody != null) Destroy(entityRigidbody);
                        subEntities.Add(newEntity);
                    }
                    else if (subEntityData.type == 1)
                    {
                        Kayak kayak = entity as Kayak;
                        BaseVehicle newEntity = entity.gameObject.AddComponent<BaseVehicle>();
                        CopySerializableFields(kayak, newEntity);

                        UnityEngine.Object.DestroyImmediate(entity, true);
                        newEntity.gameObject.AwakeFromInstantiate();
                        newEntity.enableSaving = false;
                        newEntity.SetParent(modularCar);
                        newEntity.transform.localPosition = subEntityData.position;
                        newEntity.transform.localEulerAngles = subEntityData.rotation;
                        Rigidbody entityRigidbody = newEntity.GetComponent<Rigidbody>();
                        if (entityRigidbody != null) Destroy(entityRigidbody);
                        newEntity.isMobile = false;
                        newEntity.Spawn();
                        subEntities.Add(newEntity);
                        newEntity.SetFlag(BaseEntity.Flags.On, true);

                        foreach (var a in newEntity.mountPoints)
                        {
                            a.mountable.SetParent(modularCar);
                            modularCar.mountPoints.Add(a);

                            a.mountable.transform.localPosition = new Vector3(a.mountable.transform.localPosition.x, a.mountable.transform.localPosition.y + 0.68f, a.mountable.transform.localPosition.z);
                            a.mountable.allowHeadLook = false;
                            newEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
                            if (fakeSeat == null) fakeSeat = a.mountable;
                            else a.isDriver = false;
                        }
                    }
                    else
                    {
                        entity.SetParent(modularCar);
                        entity.transform.localPosition = subEntityData.position;
                        entity.transform.localEulerAngles = subEntityData.rotation;
                        if (subEntityData.type == 2 || subEntityData.type >= 4) entity.SetFlag(BaseEntity.Flags.Reserved8, true);
                        entity.Spawn();
                        Rigidbody entityRigidbody = entity.GetComponent<Rigidbody>();
                        if (entityRigidbody != null) Destroy(entityRigidbody);
                        subEntities.Add(entity);
                        if (subEntityData.type == 4) rockets.Add(entity);

                        if (subEntityData.type == 3)
                        {
                            container = entity as StorageContainer;
                            container.inventory.capacity = biplaneSetting.countSlots;
                        }
                    }
                }
            }

            void SpawnSeat()
            {
                BaseMountable seat = GameManager.server.CreateEntity("assets/prefabs/vehicle/seats/miniheliseat.prefab") as BaseMountable;
                seat.enableSaving = false;
                seat.SetParent(modularCar, true, false);
                seat.transform.localPosition = new Vector3(0f, 0.45f, 0.324f);
                seat.transform.localEulerAngles = Vector3.zero;
                seat.Spawn();

                modularCar.mountPoints.Add(new BaseVehicle.MountPointInfo
                {
                    pos = seat.transform.position,
                    rot = seat.transform.eulerAngles,
                    isDriver = true,
                    mountable = seat,
                    prefab = null
                });
                pilotSeat = seat;
            }

            internal void MountPlayer(BasePlayer player)
            {
                if (pilotSeat._mounted == null) pilotSeat.MountPlayer(player);
            }

            internal void UnSleep()
            {
                if (controlCorountine == null) controlCorountine = ServerMgr.Instance.StartCoroutine(ControlCorountine());
            }

            IEnumerator ControlCorountine()
            {
                lastFuelAmount = 0;
                while (!grounded || pilotSeat._mounted != null)
                {
                    bool checkGroung = modularCar.wheelFR.wheelCollider.isGrounded || modularCar.wheelFL.wheelCollider.isGrounded || modularCar.wheelRR.wheelCollider.isGrounded || modularCar.wheelRL.wheelCollider.isGrounded || Vector3.Angle(modularCar.transform.forward, rigidbody.velocity) > 90;

                    if (checkGroung != grounded)
                    {
                        grounded = checkGroung;
                        if (grounded)
                        {
                            rigidbody.maxAngularVelocity = 0.7f;
                            modularCar.carSettings.maxSteerAngle = 35f;
                        }
                        else
                        {
                            rigidbody.maxAngularVelocity = 0.3f;
                            modularCar.carSettings.maxSteerAngle = 0;
                        }
                    }

                    bool checkEngine = modularCar.engineController.IsOn;

                    if (checkEngine != engineOn)
                    {
                        engineOn = checkEngine;
                        if (!engineOn) SpawnRotor();
                    }

                    if (engineOn)
                    {
                        foreach (BaseEntity rotor in rotors)
                        {
                            if (UnityEngine.Random.Range(0, 2) < 1)
                            {
                                rotor.limitNetworking = true;
                                rotor.limitNetworking = false;
                            }
                        }
                    }

                    BasePlayer player = pilotSeat._mounted;
                    if (player != null)
                    {
                        UpdateGui(player);

                        float leftRight = player.serverInput.MouseDelta().x;
                        if (leftRight > 10) leftRight = 10;
                        if (leftRight < -10) leftRight = -10;
                        if (ins._config.invertX) leftRight = -leftRight;

                        float upDown = player.serverInput.MouseDelta().y;
                        if (upDown > 10) upDown = 10;
                        if (upDown < -10) upDown = -10;
                        if (ins._config.invertY) upDown = -upDown;

                        float speedScale = 1.25f * rigidbody.velocity.magnitude / biplaneSetting.maxVelosity;
                        if (speedScale > 1) speedScale = 1;

                        if (grounded)
                        {
                            if (upDown < 0 || rigidbody.velocity.magnitude < 10) upDown = 0;
                            rigidbody.AddForce(Vector3.up * rigidbody.velocity.magnitude * 1000);
                        }

                        if (player.serverInput.IsDown(BUTTON.FORWARD) && (rigidbody.velocity.magnitude < biplaneSetting.maxVelosity) && engineOn) rigidbody.AddForce(modularCar.transform.forward.normalized * biplaneSetting.force);
                        if (upDown != 0) rigidbody.AddTorque(modularCar.transform.right * upDown * -4000 * speedScale * biplaneSetting.biplaneControlSetting.turningMouseSpeed);

                        if (!grounded)
                        {

                            rigidbody.AddTorque(modularCar.transform.up * leftRight * 4000 * speedScale * biplaneSetting.biplaneControlSetting.turningMouseSpeed);
                            if (player.serverInput.IsDown(BUTTON.LEFT)) rigidbody.AddTorque(modularCar.transform.forward * 4000 * speedScale * biplaneSetting.biplaneControlSetting.turningADSpeed);
                            if (player.serverInput.IsDown(BUTTON.RIGHT)) rigidbody.AddTorque(modularCar.transform.forward * -4000 * speedScale * biplaneSetting.biplaneControlSetting.turningADSpeed);
                        }
                    }

                    Vector2 vector2 = new Vector2(rigidbody.velocity.x, rigidbody.velocity.z);
                    if (!grounded && active)
                    {
                        Vector3 normal = modularCar.transform.forward;
                        float cren = transform.eulerAngles.z;
                        float dive = transform.eulerAngles.x;

                        if (cren > 180) cren = Math.Abs(cren - 360);
                        if (cren > 90) cren = 90;
                        if (normal.y > -1) normal.y -= cren / 360;
                        if (rigidbody.velocity.magnitude < 10f && normal.y > -1 && dive > 180) normal.y = -1 + vector2.magnitude / 10;
                        float magnitude = rigidbody.velocity.magnitude;
                        if (dive < 180)
                        {
                            if (dive > 90) dive -= 90;
                            dive /= 90;
                            magnitude += dive;
                        }
                        if (magnitude > biplaneSetting.maxVelosity) magnitude = biplaneSetting.maxVelosity;
                        if (rigidbody.velocity.magnitude > 2f) rigidbody.velocity = magnitude * normal;
                    }
                    yield return CoroutineEx.waitForSeconds(0.2f);
                }
                SpawnRotor();
                controlCorountine = null;
            }

            void UpdateGui(BasePlayer player)
            {
                if (!ins._config.guiSetting.isGUI) return;
                int fuelAmount = modularCar.GetFuelSystem().GetFuelAmount();
                if (fuelAmount == lastFuelAmount) return;
                lastFuelAmount = fuelAmount;

                CuiHelper.DestroyUi(player, "FuelText");

                CuiElementContainer container = new CuiElementContainer();

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = ins._config.guiSetting.offsetMin, OffsetMax = ins._config.guiSetting.offsetMax },
                    CursorEnabled = false,
                }, "Hud", "FuelText");

                container.Add(new CuiElement
                {
                    Parent = "FuelText",
                    Components =
                    {
                        new CuiTextComponent() { Color = "1 1 1 1", FadeIn = 0f, Text = ins.GetMessage("Fuel", player.UserIDString, fuelAmount), FontSize = ins._config.guiSetting.size, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf" },
                        new CuiOutlineComponent { Distance = "1 1", Color = "0 0 0 1" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });

                CuiHelper.AddUi(player, container);
            }

            void FixedUpdate()
            {
                rigidbody.angularDrag = 1;
                Weapon();
            }

            void Weapon()
            {
                weaonDelay -= 0.2f;
                if (weaonDelay > 0) return;
                BasePlayer player = pilotSeat._mounted;
                if (player == null) return;

                InputState playerInputState = player.serverInput;

                if (!grounded)
                {
                    if (biplaneSetting.rockets && (playerInputState.IsDown(BUTTON.FIRE_PRIMARY) || playerInputState.WasJustPressed(BUTTON.FIRE_PRIMARY))) FireRocker();
                    if (biplaneSetting.bombs && (playerInputState.IsDown(BUTTON.FIRE_SECONDARY) || playerInputState.WasJustPressed(BUTTON.FIRE_SECONDARY))) FireBomb();
                }
            }

            void FireBomb()
            {
                string bombPrefab = null;
                if (biplaneSetting.infinityBombs) bombPrefab = "assets/prefabs/ammo/40mmgrenade/40mm_grenade_he.prefab";
                else
                {
                    foreach (WeaponItem weaponItem in ins._config.bombs)
                    {
                        Item item = container.inventory.itemList.FirstOrDefault(x => x.info.shortname == weaponItem.shortname && x.skin == weaponItem.skin);
                        if (item != null)
                        {
                            bombPrefab = weaponItem.prefab;
                            item.amount -= 1;
                            if (item.amount == 0)
                            {
                                item.Remove();
                                item.RemoveFromContainer();
                            }
                            else item.MarkDirty();
                            break;
                        }
                    }
                }
                if (bombPrefab == null) return;

                weaonDelay = biplaneSetting.rocketTime;
                TimedExplosive grenade = GameManager.server.CreateEntity(bombPrefab, transform.position) as TimedExplosive;
                grenade.creatorEntity = pilotSeat.GetMounted();
                ServerProjectile serverProjectile = grenade.GetComponent<ServerProjectile>();
                serverProjectile.InitializeVelocity(rigidbody.velocity - transform.up * 3);
                grenade.Spawn();
            }

            void FireRocker()
            {
                string rocketPrefab = null;
                if (biplaneSetting.infinityRockets) rocketPrefab = "assets/prefabs/ammo/rocket/rocket_hv.prefab";
                else
                {
                    foreach (WeaponItem weaponItem in ins._config.rockets)
                    {
                        Item item = container.inventory.itemList.FirstOrDefault(x => x.info.shortname == weaponItem.shortname && x.skin == weaponItem.skin);
                        if (item != null)
                        {
                            rocketPrefab = weaponItem.prefab;
                            item.amount -= 1;
                            if (item.amount == 0)
                            {
                                item.Remove();
                                item.RemoveFromContainer();
                            }
                            else item.MarkDirty();
                            break;
                        }
                    }
                }
                if (rocketPrefab == null) return;

                weaonDelay = biplaneSetting.rocketTime;
                leftRocket = !leftRocket;
                Vector3 vector3 = leftRocket ? rockets[0].transform.position + modularCar.transform.forward.normalized * 3 : rockets[1].transform.position + modularCar.transform.forward.normalized * 3;
                TimedExplosive rocket = GameManager.server.CreateEntity(rocketPrefab, vector3) as TimedExplosive;
                rocket.creatorEntity = pilotSeat.GetMounted();
                ServerProjectile serverProjectile = rocket.GetComponent<ServerProjectile>();
                serverProjectile.InitializeVelocity((pilotSeat.transform.forward.normalized - pilotSeat.transform.up.normalized / 20) * serverProjectile.speed * 1.5f);
                rocket.Spawn();
            }

            void OnDestroy()
            {
                foreach (BaseEntity entity in subEntities) if (entity.IsExists()) entity.Kill();
                foreach (BaseEntity entity in rotors) if (entity.IsExists()) entity.Kill();
                if (!ins.unload) ins.biplanes.Remove(this);
            }
        }

        sealed class DecorWing : BaseCombatEntity, SamSite.ISamSiteTarget
        {
            public override float MaxVelocity()
            {
                return 100;
            }

            public SamSite.SamTargetType SAMTargetType => ins._config.samSitePlus ? SamSite.targetTypeMissile : SamSite.targetTypeVehicle;

            public bool IsValidSAMTarget(bool staticRespawn) => ins._config.samSiteMonuments && staticRespawn || ins._config.samSite;
        }

        public class EntityData
        {
            public Vector3 position;
            public Vector3 rotation;
        }

        public class SubEntityData : EntityData
        {
            public string name;
            public int type;


            public SubEntityData(string name, int type, Vector3 position, Vector3 rotation)
            {
                this.position = position;
                this.rotation = rotation;
                this.name = name;
                this.type = type;
            }
        }

        public class RotorData : EntityData
        {
            public bool hide;

            public RotorData(Vector3 position, Vector3 rotation, bool hide)
            {
                this.position = position;
                this.rotation = rotation;
                this.hide = hide;
            }
        }

        public class SellerData
        {
            public VehicleVendor seller;
            public string sellerPrefab;
            public string monumentName;
            public Transform monumentTransform;
            public VendingMachineMapMarker vendingMachineMapMarker;

            public SellerData(VehicleVendor seller, string sellerPrefab, Transform monumentTransform, string monumentName, VendingMachineMapMarker vendingMachineMapMarker)
            {
                this.seller = seller;
                this.sellerPrefab = sellerPrefab;
                this.monumentTransform = monumentTransform;
                this.monumentName = monumentName;
                this.vendingMachineMapMarker = vendingMachineMapMarker;
            }

            public void Destroy()
            {
                if (seller.IsExists()) seller.Kill();
                if (vendingMachineMapMarker.IsExists()) vendingMachineMapMarker.Kill();
            }
        }

        public class SaveBiplaneData
        {
            public ulong biplaneId;
            public string biplanePrefab;
            public int keyIndex;
            public List<SaveItemData> items = new List<SaveItemData>();

            public SaveBiplaneData(ulong biplaneId, string biplanePrefab, int keyIndex)
            {
                this.biplaneId = biplaneId;
                this.biplanePrefab = biplanePrefab;
                this.keyIndex = keyIndex;
            }
        }

        public class SaveItemData
        {
            public string shortname;
            public string customName;
            public int amount;
            public ulong skin;
            public float condition;
            public ProjectileData projectileData = null;

            public SaveItemData(string shortname, string customName, int amount, ulong skin, float condition)
            {
                this.shortname = shortname;
                this.customName = customName;
                this.amount = amount;
                this.skin = skin;
                this.condition = condition;
            }
        }

        public class ProjectileData
        {
            public List<string> mods = new List<string>();
            public string ammoShortname;
            public int ammoAmount;

            public ProjectileData(string ammoShortname, int ammoAmount)
            {
                this.ammoShortname = ammoShortname;
                this.ammoAmount = ammoAmount;
            }
        }

        List<RotorData> rotorPositions = new List<RotorData>
        {
            new RotorData(new Vector3(0.074f, 1.05f, 2.35f), new Vector3(280.531f, 130.317f, 229.105f), false),
            new RotorData(new Vector3(0.041f, 0.879f, 2.35f), new Vector3(8.011f, 96.859f, 269.903f), false),
            new RotorData(new Vector3(-0.092f, 0.914f, 2.35f), new Vector3(79.469f, 229.683f, 49.105f), false),
            new RotorData(new Vector3(-0.061f, 1.041f, 2.35f), new Vector3(351.989f, 263.141f, 89.903f), false),

            new RotorData(new Vector3(0.082f, 0.907f, 2.35f), new Vector3(322.705f, 98.549f, 263.739f), true),
            new RotorData(new Vector3(-0.03f, 0.840f, 2.35f), new Vector3(51.877f, 101.044f, 277.672f), true),
            new RotorData(new Vector3(-0.101f, 0.958f, 2.35f), new Vector3(37.295f, 261.451f, 83.739f), true),
            new RotorData(new Vector3(0.011f, 1.027f, 2.35f), new Vector3(308.123f, 258.956f, 97.672f), true),
        };

        HashSet<SubEntityData> subEntityDatas = new HashSet<SubEntityData>
        {
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 1, new Vector3(0.0f, 0.68f, -0.198f), new Vector3(0, 0, 0)),
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 0, new Vector3(-1.108f, 0.797f, 1.104f), new Vector3(0, 90, 0)),
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 0, new Vector3(1.108f, 0.797f, 1.186f), new Vector3(0, 270, 0)),
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 0, new Vector3(-1.3f, 2.316f, 1.186f), new Vector3(0, 90, 180)),
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 0, new Vector3(1.3f, 2.317f, 1.186f), new Vector3(0, 90, 180)),
            new SubEntityData("assets/content/vehicles/boats/kayak/kayak.prefab", 0, new Vector3(0f, 1.943f, -1.588f), new Vector3(0, 90, 180)),
            new SubEntityData("assets/prefabs/misc/summer_dlc/boogie_board/boogieboard.deployed.prefab", 0, new Vector3(0.465f, 1.424f, -1.714f), new Vector3(270f, 90, 0)),
            new SubEntityData("assets/prefabs/misc/summer_dlc/boogie_board/boogieboard.deployed.prefab", 0, new Vector3(-0.465f, 1.424f, -1.714f), new Vector3(270f, 270, 0)),

            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(1.053f, 1.138f, 1.473f), new Vector3(333.975f, 90, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(1.053f, 1.138f, 0.802f), new Vector3(333.975f, 90, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(2.221f, 1.138f, 1.460f), new Vector3(333.975f, 270, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(2.221f, 1.138f, 0.863f), new Vector3(333.975f, 270, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-1.053f, 1.138f, 1.481f), new Vector3(333.975f, 270, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-1.053f, 1.138f, 0.810f), new Vector3(333.975f, 270, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-2.221f, 1.138f, 1.420f), new Vector3(333.975f, 90, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-2.221f, 1.138f, 0.823f), new Vector3(333.975f, 90, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(0.337f, 0.933f, -0.380f), new Vector3(336.5f, 180, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-0.462f, 0.932f, -0.303f), new Vector3(336.5f, 180, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(0.338f, 0.993f, -0.305f), new Vector3(322.927f, 0, 0)),
            new SubEntityData("assets/prefabs/weapons/wooden spear/spear_wooden.entity.prefab", 2, new Vector3(-0.444f, 0.993f, -0.305f), new Vector3(322.927f, 0, 0)),
            new SubEntityData("assets/prefabs/weapons/mace/mace.entity.prefab", 2, new Vector3(0.078f, 0.991f, 2.28f), new Vector3(353.329f, 7.727f, 358.624f)),

            new SubEntityData("assets/content/vehicles/boats/rhib/subents/rhib_storage.prefab", 3, new Vector3(0, 0.86f, -1.7f), new Vector3(0, 90, 0)),

            new SubEntityData("assets/prefabs/weapons/rocketlauncher/rocket_launcher.entity.prefab", 4, new Vector3(-0.918f, 1.036f, 1.584f), new Vector3(275.535f, 20.481f, 17.278f)),
            new SubEntityData("assets/prefabs/weapons/rocketlauncher/rocket_launcher.entity.prefab", 4, new Vector3(1.078f, 1.036f, 1.584f), new Vector3(275.535f, 20.481f, 17.278f)),
        };
        #endregion Classes

        #region MoveItem
        void MoveItem(BasePlayer player, Item item)
        {
            int spaceCountItem = GetSpaceCountItem(player, item.info.shortname, item.MaxStackable(), item.skin);
            int inventoryItemCount;
            if (spaceCountItem > item.amount) inventoryItemCount = item.amount;
            else inventoryItemCount = spaceCountItem;

            if (inventoryItemCount > 0)
            {
                Item itemInventory = ItemManager.CreateByName(item.info.shortname, inventoryItemCount, item.skin);
                if (item.skin != 0) itemInventory.name = item.name;

                item.amount -= inventoryItemCount;
                MoveInventoryItem(player, itemInventory);
            }

            if (item.amount > 0) MoveOutItem(player, item);
        }

        int GetSpaceCountItem(BasePlayer player, string shortname, int stack, ulong skinID)
        {
            int slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            int taken = player.inventory.containerMain.itemList.Count + player.inventory.containerBelt.itemList.Count;
            int result = (slots - taken) * stack;
            foreach (Item item in player.inventory.AllItems()) if (item.info.shortname == shortname && item.skin == skinID && item.amount < stack) result += stack - item.amount;
            return result;
        }

        void MoveInventoryItem(BasePlayer player, Item item)
        {
            if (item.amount <= item.MaxStackable())
            {
                foreach (Item itemInv in player.inventory.AllItems())
                {
                    if (itemInv.info.shortname == item.info.shortname && itemInv.skin == item.skin && itemInv.amount < itemInv.MaxStackable())
                    {
                        if (itemInv.amount + item.amount <= itemInv.MaxStackable())
                        {
                            itemInv.amount += item.amount;
                            itemInv.MarkDirty();
                            return;
                        }
                        else
                        {
                            item.amount -= itemInv.MaxStackable() - itemInv.amount;
                            itemInv.amount = itemInv.MaxStackable();
                        }
                    }
                }
                if (item.amount > 0) player.inventory.GiveItem(item);
            }
            else
            {
                while (item.amount > item.MaxStackable())
                {
                    Item thisItem = ItemManager.CreateByName(item.info.shortname, item.MaxStackable(), item.skin);
                    if (item.skin != 0) thisItem.name = item.name;
                    player.inventory.GiveItem(thisItem);
                    item.amount -= item.MaxStackable();
                }
                if (item.amount > 0) player.inventory.GiveItem(item);
            }
        }

        void MoveOutItem(BasePlayer player, Item item)
        {
            if (item.amount <= item.MaxStackable()) item.Drop(player.transform.position, Vector3.up);
            else
            {
                while (item.amount > item.MaxStackable())
                {
                    Item thisItem = ItemManager.CreateByName(item.info.shortname, item.MaxStackable(), item.skin);
                    if (item.skin != 0) thisItem.name = item.name;
                    thisItem.Drop(player.transform.position, Vector3.up);
                    item.amount -= item.MaxStackable();
                }
                if (item.amount > 0) item.Drop(player.transform.position, Vector3.up);
            }
        }

        Item CreateItem(NamedItem itemSetting)
        {
            Item item = ItemManager.CreateByName(itemSetting.shortname, 1, itemSetting.skin);
            item.OnBroken();
            if (itemSetting.name != "") item.name = itemSetting.name;
            return item;
        }
        #endregion MoveItem

        #region RemoveItem
        int GetCountItem(BasePlayer player, string shortname, ulong skinID = 0)
        {
            int result = 0;
            foreach (Item item in player.inventory.AllItems())
            {
                if (item.info.shortname == shortname && item.skin == skinID) result += item.amount;
            }
            return result;
        }

        void RemoveItem(BasePlayer player, string shortname, int count, ulong skinID = 0)
        {
            foreach (Item item in player.inventory.AllItems())
            {
                if (item.info.shortname == shortname && item.skin == skinID)
                {
                    if (item.amount == count)
                    {
                        item.Remove();
                        break;
                    }
                    else if (item.amount < count)
                    {
                        count -= item.amount;
                        item.Remove();
                    }
                    else if (item.amount > count)
                    {
                        item.amount -= count;
                        item.MarkDirty();
                        break;
                    }
                }
            }
        }
        #endregion RemoveItem

        #region Gui
        [ConsoleCommand("closebiplanesellergui")] void Cmd_CloseGui(ConsoleSystem.Arg arg) => CuiHelper.DestroyUi(arg.Player(), "BiplaneSellerPanel");
        [ConsoleCommand("biplanesellerguistage")] void Cmd_StageGui(ConsoleSystem.Arg arg) => UpdateGui(arg.Player(), arg.Args[0], Convert.ToInt32(arg.Args[1]), Convert.ToInt32(arg.Args[2]));
        [ConsoleCommand("biplanesellerguibuystage")] void Cmd_StageBuyGui(ConsoleSystem.Arg arg) => UpdateGui(arg.Player(), arg.Args[0], Convert.ToInt32(arg.Args[1]), Convert.ToInt32(arg.Args[2]), arg.Args[3]);
        [ConsoleCommand("biplanesellerguibuy")]
        void Cmd_StageBuy(ConsoleSystem.Arg arg)
        {
            int sellerIndex = Convert.ToInt32(arg.Args[2]);

            BasePlayer player = arg.Player();
            UpdateGui(player, arg.Args[0], Convert.ToInt32(arg.Args[1]), sellerIndex); ;
            ProductSetting productSetting = _config.sellersPresets[arg.Args[0]].FirstOrDefault(x => x.biplaneName == arg.Args[3]);

            SellerData sellerData = sellers[sellerIndex];

            MonumentConfig monumentConfig = _config.spawnSetting.monumentSpawnSettings[sellerData.monumentName];

            if (GetCountItem(player, productSetting.itemSetting.shortname, productSetting.itemSetting.skin) < productSetting.itemSetting.cost) return;

            RemoveItem(player, productSetting.itemSetting.shortname, productSetting.itemSetting.cost, productSetting.itemSetting.skin);

            CreateBiplane(sellerData.monumentTransform.TransformPoint(monumentConfig.sellerBiplaneLocation.position.ToVector3()), sellerData.monumentTransform.rotation * Quaternion.Euler(new Vector3(0, float.Parse(monumentConfig.sellerBiplaneLocation.rotation), 0)), productSetting.biplaneName);
        }

        void CreateGui(BasePlayer player, string sellerPrefab, int sellerIndex)
        {
            CuiHelper.DestroyUi(player, "BiplaneSellerPanel");

            CuiElementContainer container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                CursorEnabled = true,
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Image = { Color = "0 0 0 0" }
            }, "Overlay", "BiplaneSellerPanel");

            container.Add(new CuiElement
            {
                Parent = "BiplaneSellerPanel",
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 1"},
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "0 0", OffsetMax = "0 75.3333"},
                }
            });

            container.Add(new CuiElement
            {
                Parent = "BiplaneSellerPanel",
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 1"},
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -75.33", OffsetMax = "0 0"},
                }
            });

            container.Add(new CuiElement
            {
                Name = "BiplaneSellerBackground",
                Parent = "BiplaneSellerPanel",
                Components =
                {
                    new CuiImageComponent {Color = "0.1 0.12 0.07 0.8", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"},
                    new CuiRectTransformComponent { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "136 238", OffsetMax = "436 463.3333"},
                }
            });

            container.Add(new CuiElement
            {
                Name = "BiplaneSellerCloseButtonBackground",
                Parent = "BiplaneSellerBackground",
                Components =
                {
                    new CuiImageComponent {Color = "0.16 0.16 0.14 0.95", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"},
                    new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-20 -20", OffsetMax = "0 0"},
                }
            });

            container.Add(new CuiElement
            {
                Parent = "BiplaneSellerCloseButtonBackground",
                Components =
                {
                    new CuiImageComponent { Color = "0.67 0.28 0.21 0.95", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-8 -8", OffsetMax = "8 8" },
                }
            });

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Button = { Color = "0 0 0 0", Command = "closebiplanesellergui" },
                Text = { Text = "x", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", FontSize = 16, Font = "robotocondensed-regular.ttf" }
            }, "BiplaneSellerCloseButtonBackground");

            container.Add(new CuiElement
            {
                Name = "BiplaneSellerHeader",
                Parent = "BiplaneSellerBackground",
                Components =
                {
                    new CuiImageComponent { Color = "0.27 0.27 0.25 0.95", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "10 -30", OffsetMax = "110 -10" },
                }
            });

            container.Add(new CuiElement
            {
                Parent = "BiplaneSellerHeader",
                Components =
                {
                    new CuiTextComponent { Text = GetMessage("SellerName", player.UserIDString), Align = TextAnchor.MiddleCenter, Color = "0.79 0.75 0.75 1", FontSize = 11 },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "BiplaneSellerMainText",
                Parent = "BiplaneSellerBackground",
                Components =
                {
                    new CuiImageComponent { Color = "0.27 0.27 0.25 0.95", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "10 110", OffsetMax = "290 190" },
                }
            });

            CuiHelper.AddUi(player, container);

            UpdateGui(player, sellerPrefab, 0, sellerIndex);
        }

        void UpdateGui(BasePlayer player, string sellerPrefab, int stage, int sellerIndex, string biplanePrefab = "")
        {

            CuiHelper.DestroyUi(player, "BiplaneMainText");
            CuiHelper.DestroyUi(player, "BiplaneButtonsBackground");

            CuiElementContainer container = new CuiElementContainer();

            ProductSetting productSetting = null;

            if (biplanePrefab != "")
            {
                productSetting = _config.sellersPresets[sellerPrefab].FirstOrDefault(x => x.biplaneName == biplanePrefab);
                if (productSetting == null) return;
            }

            SellerData sellerData = sellers[sellerIndex];
            MonumentConfig monumentConfig = _config.spawnSetting.monumentSpawnSettings[sellerData.monumentName];
            bool occupied = biplanes.Any(x => x != null && Vector3.Distance(x.modularCar.transform.position, sellerData.monumentTransform.TransformPoint(monumentConfig.sellerBiplaneLocation.position.ToVector3())) < 15f); ;
            string text = productSetting == null ? GetMessage($"MainText{stage}", player.UserIDString) : GetMessage($"MainText{stage}", player.UserIDString, productSetting.itemSetting.cost);
            if (occupied && stage > 0) text = GetMessage("RunwayOccupied", player.UserIDString);
            container.Add(new CuiElement
            {
                Name = "BiplaneMainText",
                Parent = "BiplaneSellerMainText",
                Components =
                {
                    new CuiTextComponent { Text = text, Align = TextAnchor.UpperLeft, Color = "0.79 0.75 0.72 1", FontSize = 11, Font="robotocondensed-regular.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.1", AnchorMax = "0.98 0.9" }
                }
            });
            container.Add(new CuiElement
            {
                Name = "BiplaneButtonsBackground",
                Parent = "BiplaneSellerBackground",
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "10 0", OffsetMax = "-10 105.3333" },
                }
            });
            if (stage == 0)
            {
                CreateButton(container, 0, GetMessage("FirstButtonText", player.UserIDString), $"biplanesellerguistage {sellerPrefab} {stage + 1} {sellerIndex}");
                CreateButton(container, 1, GetMessage("SecondButtonText", player.UserIDString), "closebiplanesellergui");
            }

            else if (stage == 1)
            {
                int index = 0;
                HashSet<ProductSetting> productSettings = _config.sellersPresets[sellerPrefab];

                if (!occupied)
                {
                    foreach (ProductSetting newProductSetting in productSettings)
                    {
                        if (_config.biplanePresets[newProductSetting.biplaneName].permission == "" || permission.UserHasPermission(player.UserIDString, _config.biplanePresets[newProductSetting.biplaneName].permission))
                        {
                            CreateButton(container, index, GetMessage("BuyButtonText", player.UserIDString, newProductSetting.text), $"biplanesellerguibuystage {sellerPrefab} {stage + 1} {sellerIndex} {newProductSetting.biplaneName}");
                            index++;
                        }
                    }
                }
                CreateButton(container, index, GetMessage("SecondButtonText", player.UserIDString), "closebiplanesellergui");
            }

            else if (stage == 2)
            {
                if (productSetting == null) return;
                if (GetCountItem(player, productSetting.itemSetting.shortname, productSetting.itemSetting.skin) >= productSetting.itemSetting.cost)
                {
                    CreateButton(container, 0, GetMessage("CanAfford", player.UserIDString, productSetting.itemSetting.cost), $"biplanesellerguibuy {sellerPrefab} {stage + 1} {sellerIndex} {productSetting.biplaneName}");
                    CreateButton(container, 1, GetMessage("CantAfford", player.UserIDString), "closebiplanesellergui");
                }
                else CreateButton(container, 0, GetMessage("CantAfford", player.UserIDString), "closebiplanesellergui");
            }

            else if (stage == 3) CreateButton(container, 0, GetMessage("Thanks", player.UserIDString), "closebiplanesellergui");

            CuiHelper.AddUi(player, container);
        }

        void CreateButton(CuiElementContainer container, int index, string text, string command)
        {
            float ymax = index * 25;

            container.Add(new CuiElement
            {
                Name = $"ButtonBackground{index}",
                Parent = "BiplaneButtonsBackground",
                Components =
                {
                    new CuiImageComponent { Color = "0.16 0.16 0.14 0.95", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = $"0 {-ymax -  20}", OffsetMax = $"0 {-ymax}" },
                }
            });

            container.Add(new CuiElement
            {
                Name = $"IndexBackground{index}",
                Parent = $"ButtonBackground{index}",
                Components =
                {
                    new CuiImageComponent { Color = "0.36 0.45 0.22 1" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "2 2", OffsetMax = "18 18" },
                }
            });

            container.Add(new CuiElement
            {
                Parent = $"IndexBackground{index}",
                Components =
                {
                    new CuiTextComponent { Text = $"{index + 1}", Align = TextAnchor.MiddleCenter, FontSize = 11},
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "MainText",
                Parent = $"ButtonBackground{index}",
                Components =
                {
                    new CuiTextComponent { Text = text, Align = TextAnchor.MiddleLeft, Color = "0.79 0.75 0.72 1", FontSize = 11, Font="robotocondensed-regular.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0.08 0", AnchorMax = "1 1" }
                }
            });

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Button = { Color = "0 0 0 0", Command = command },
                Text = { Text = " " }
            }, $"ButtonBackground{index}");
        }

        #endregion Gui

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["GetBiplane"] = "{0} You <color=#738d43>got</color> a biplane!",
                ["Lock"] = "{0} <color=#ce3f27>Locked!</color>",
                ["Fuel"] = "Fuel: <color=#738d43>{0}</color>",
                ["EmptyСontainers"] = "<color=#ce3f27>Empty</color> the containers!",
                ["NoPermission"] = "{0} You <color=#b03b1e>do not have permission</color> to use a this biplane!",
                ["RunwayOccupied"] = "The runway is occupied!",
                ["SellerName"] = "Airwolf Vendor",
                ["MainText0"] = "Hello! Welcome to Air Wolf. How can I help you?",
                ["FirstButtonText"] = "I'd like to buy a biplane",
                ["SecondButtonText"] = "I'm just browsing",
                ["MainText1"] = "Well you're come to right place! What are you in the market for?",
                ["BuyButtonText"] = "I'd like to buy a {0}",
                ["MainText2"] = "Sure, that'll be {0} scrap",
                ["CanAfford"] = "[PAY {0} SCRAP]",
                ["CantAfford"] = "I changed my mind. Forget it",
                ["MainText3"] = "Another satisfied customer! You can find your new purchase on the runway",
                ["Thanks"] = "Thanks",
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["GetBiplane"] = "{0} Вы <color=#738d43>получили</color> биплан!",
                ["Lock"] = "{0} <color=#ce3f27>Закрыто!</color>",
                ["Fuel"] = "Топливо: <color=#738d43>{0}</color>",
                ["EmptyСontainers"] = "<color=#ce3f27>Освободите</color> контейнеры самолета!",
                ["NoPermission"] = "{0} У вас <color=#b03b1e>нет разрешения</color> использовать этот биплан!",
                ["RunwayOccupied"] = "Взлетная полоса занята!",
                ["SellerName"] = "Торговец Airwolf",
                ["MainText0"] = "Приветствую! Добро пожаловать в Air Wolf. Чем я могу помочь?",
                ["FirstButtonText"] = "Я хочу купить биплан",
                ["SecondButtonText"] = "Я просто осматриваюсь",
                ["MainText1"] = "Ну, тогда вы пришли по адресу! Что вас интересует?",
                ["BuyButtonText"] = "Я хочу купить {0}",
                ["MainText2"] = "Конечно, это будет стоить {0} металлолома",
                ["CanAfford"] = "[ЗАПЛАТИТЬ {0} МЕТАЛЛОЛОМА]",
                ["CantAfford"] = "Я передумал, забудьте",
                ["MainText3"] = "Еще один довольный клиент! Вы увидите свое приобретение на взлетной полосе.",
                ["Thanks"] = "Спасибо",
            }, this, "ru");
        }

        string GetMessage(string langKey, string userID) => lang.GetMessage(langKey, this, userID);

        string GetMessage(string langKey, string userID, params object[] args) => (args.Length == 0) ? GetMessage(langKey, userID) : string.Format(GetMessage(langKey, userID), args);

        #endregion Lang

        #region Data 

        HashSet<SaveBiplaneData> saveBiplanes = new HashSet<SaveBiplaneData>();

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Title, saveBiplanes);

        private void LoadData() => saveBiplanes = Interface.Oxide.DataFileSystem.ReadObject<HashSet<SaveBiplaneData>>(Title);

        #endregion Data

        #region Config
        PluginConfig _config;

        protected override void LoadDefaultConfig() => _config = PluginConfig.DefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
            Config.WriteObject(_config, true);
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        public class SpawnSetting
        {
            [JsonProperty(en ? "Minimum respawn time" : "Минимальное время респавна")] public float minRespawnTime { get; set; }
            [JsonProperty(en ? "Maximum respawn time" : "Максимальное время респавна")] public float maxRespawnTime { get; set; }
            [JsonProperty(en ? "Settings for Monuments" : "Настройка монументов")] public Dictionary<string, MonumentConfig> monumentSpawnSettings { get; set; }
            [JsonProperty(en ? "Setting up the spawn of biplanes in custom spawn points" : "Настройка кастомных точек спавна для появляения биплана")] public CustomSpawnPointsConfig customSpawnPointsConfig { get; set; }
        }

        public class MonumentConfig
        {
            [JsonProperty(en ? "Enable the spawn of the biplane at this monument" : "Включить спавн самолета на этом монументе")] public bool enableSpawn;
            [JsonProperty(en ? "Preset probability" : "Пресет - вероятность")] public Dictionary<string, float> presets = new Dictionary<string, float>();
            [JsonProperty(en ? "Spawn points" : "Точки спавна")] public List<LocationConfig> points = new List<LocationConfig>();
            [JsonProperty(en ? "Add a seller to a monument? [true/false]" : "Добавить продавца на локацию? [true/false]")] public bool enableSeller;
            [JsonProperty(en ? "Seller's preset" : "Пресет продавца")] public string sellerName = "seller_default";
            [JsonProperty(en ? "The seller's location on the monument" : "Расположение продавца на монументе")] public LocationConfig sellerLocation;
            [JsonProperty(en ? "The place of the plane's spawn after purchase" : "Место спавна самолета после покупки")] public LocationConfig sellerBiplaneLocation;
        }

        public class CustomSpawnPointsConfig
        {
            [JsonProperty(en ? "Enable the spawn of the biplane on custom spawn points" : "Включить спавн самолета на кастомных точках спавна")] public bool enableSpawn { get; set; }
            [JsonProperty(en ? "Preset probability" : "Пресет - вероятность")] public Dictionary<string, float> presets { get; set; }
            [JsonProperty(en ? "Spawn points" : "Точки спавна")] public List<LocationConfig> points { get; set; }
        }

        public class LocationConfig
        {
            [JsonProperty(en ? "Position" : "Позиция")] public string position { get; set; }
            [JsonProperty(en ? "Rotation" : "Вращение")] public string rotation { get; set; }
        }

        public class BiplaneSetting
        {
            [JsonProperty(en ? "Custom item shortname for placement" : "Кастомный предмет для размещения")] public string customItemShortname { get; set; }
            [JsonProperty(en ? "Permission to purchase/use" : "Разрешение для покупки/испольования")] public string permission { get; set; }
            [JsonProperty(en ? "Force" : "Тяга")] public float force { get; set; }
            [JsonProperty(en ? "Maximum speed" : "Максимальная скорость")] public float maxVelosity { get; set; }
            [JsonProperty(en ? "Maximum height" : "Максимальная высота")] public float maxHeight { get; set; }
            [JsonProperty(en ? "Control Settings" : "Настройка управления")] public BiplaneControlSetting biplaneControlSetting { get; set; }
            [JsonProperty(en ? "The amount of fuel in the tank during spawn" : "Количество топлива в баке")] public int fuelAmount { get; set; }
            [JsonProperty(en ? "Add components to the engine? [true/false]" : "Добавить компоненты в двинатель? [true/false]")] public bool engineComponents { get; set; }
            [JsonProperty(en ? "Engine component level (1 - 3)" : "Уровень компонентов в двигателе (1 - 3)")] public int engineComponentsLvl { get; set; }
            [JsonProperty(en ? "Number of slots in the box" : "Количество слотов в ящике")] public int countSlots { get; set; }
            [JsonProperty(en ? "Time between rockets/bombs" : "Время между ракетами/бомбами")] public float rocketTime { get; set; }
            [JsonProperty(en ? "Allow bombing [true/false]" : "Разрешить бомбометание [true/false]")] public bool bombs { get; set; }
            [JsonProperty(en ? "Add rocket launchers [true/false]" : "Добавить ракетницы [true/false]")] public bool rockets { get; set; }
            [JsonProperty(en ? "Infinite rockets [true/false]" : "Бесконечные ракеты [true/false]")] public bool infinityRockets { get; set; }
            [JsonProperty(en ? "Infinite bombs [true/false]" : "Бесконечные бомбы [true/false]")] public bool infinityBombs { get; set; }
        }

        public class BiplaneControlSetting
        {
            [JsonProperty(en ? "Turning speed multiplier [MOUSE]" : "Множитель скорости поворота [MOUSE]")] public float turningMouseSpeed { get; set; }
            [JsonProperty(en ? "Turning speed multiplier [A/D]" : "Множитель скорости поворота [A/D]")] public float turningADSpeed { get; set; }
        }

        public class ItemSetting
        {
            [JsonProperty("Shortname")] public string shortname { get; set; }
            [JsonProperty("Skin")] public ulong skin { get; set; }
        }

        public class GUISetting
        {
            [JsonProperty(en ? "Use the fuel display GUI? [true/false]" : "Использовать ли GUI отображения топлива? [true/false]")] public bool isGUI { get; set; }
            [JsonProperty(en ? "Font Size" : "Размер шрифта")] public int size { get; set; }
            [JsonProperty("OffsetMin")] public string offsetMin { get; set; }
            [JsonProperty("OffsetMax")] public string offsetMax { get; set; }
        }

        public class NamedItem : ItemSetting { [JsonProperty("Name")] public string name { get; set; } }

        public class SellerItem : ItemSetting { [JsonProperty(en ? "Amount" : "Количество")] public int cost { get; set; } }

        public class WeaponItem : ItemSetting { [JsonProperty("Prefab")] public string prefab { get; set; } }

        public class ProductSetting
        {
            [JsonProperty(en ? "The name displayed on the button" : "Отображаемое на кнопке имя")] public string text { get; set; }
            [JsonProperty(en ? "Item for purchase" : "Предмет для покупки")] public SellerItem itemSetting { get; set; }
            [JsonProperty(en ? "Biplane Preset" : "Пресет биплана")] public string biplaneName { get; set; }
        }

        public class MarkerConfig
        {
            [JsonProperty(en ? "Add a seller's marker to the card? [true/false]" : "Добавить маркер продавца на карту? [true/false]")] public bool enable { get; set; }
            [JsonProperty(en ? "Text" : "Текст")] public string text { get; set; }
        }

        class PluginConfig
        {
            [JsonProperty(en ? "Plugin version" : "Версия плагина")] public string version { get; set; }
            [JsonProperty(en ? "Prefix in chat" : "Префикс в чате")] public string prefics { get; set; }
            [JsonProperty(en ? "Permission to give items" : "Разрешение для выдачи предметов")] public string itemPermission { get; set; }
            [JsonProperty(en ? "Deployed SamSite will be attacked by biplanes [true/false]" : "SamSite игроков будут атаковать бипланы [true/false]")] public bool samSite { get; set; }
            [JsonProperty(en ? "SamSite on the monument will attack biplanes [true/false]" : "SamSite на монументах будут атаковать бипланы [true/false]")] public bool samSiteMonuments { get; set; }
            [JsonProperty(en ? "Increase the speed of missiles and the attack radius of SamSite by biplane? [true/false]" : "Увеличить скорость ракет и радиус атаки SamSite по биплану? [true/false]")] public bool samSitePlus { get; set; }
            [JsonProperty(en ? "Allow you to pick up a biplane with a hammer [true/false]" : "Разрешить поднимать биплан киянкой [true/false]")] public bool allowIpckUp { get; set; }
            [JsonProperty(en ? "Invert the X-axis? [true/false]" : "Инвертировать ось X? [true/false]")] public bool invertX { get; set; }
            [JsonProperty(en ? "Invert the Y-axis? [true/false]" : "Инвертировать ось Y? [true/false]")] public bool invertY { get; set; }
            [JsonProperty(en ? "Spawn Setting" : "Настройка спавна")] public SpawnSetting spawnSetting { get; set; }
            [JsonProperty(en ? "List of biplanes" : "Список пресетов")] public Dictionary<string, BiplaneSetting> biplanePresets { get; set; }
            [JsonProperty(en ? "List of items" : "Список предметов")] public Dictionary<string, NamedItem> itemsPresets { get; set; }
            [JsonProperty(en ? "List of sellers" : "Список торговцев")] public Dictionary<string, HashSet<ProductSetting>> sellersPresets { get; set; }
            [JsonProperty(en ? "List of rockets" : "Список ракет")] public HashSet<WeaponItem> rockets { get; set; }
            [JsonProperty(en ? "List of bombs" : "Список бомб")] public HashSet<WeaponItem> bombs { get; set; }
            [JsonProperty("GUI")] public GUISetting guiSetting { get; set; }
            [JsonProperty(en ? "Marker Setting" : "Настройки маркера")] public MarkerConfig marker { get; set; }

            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig()
                {
                    version = "1.0.8",
                    prefics = "[Biplane]",
                    itemPermission = "biplane.items",
                    samSite = true,
                    samSiteMonuments = true,
                    samSitePlus = false,
                    allowIpckUp = true,
                    invertX = false,
                    invertY = true,
                    spawnSetting = new SpawnSetting
                    {
                        minRespawnTime = 7200,
                        maxRespawnTime = 10800,
                        monumentSpawnSettings = new Dictionary<string, MonumentConfig>
                        {
                            ["assets/bundled/prefabs/autospawn/monument/large/airfield_1.prefab"] = new MonumentConfig
                            {
                                enableSpawn = false,
                                presets = new Dictionary<string, float>
                                {
                                    ["biplane_airfield"] = 100
                                },
                                points = new List<LocationConfig>
                                {
                                    new LocationConfig
                                    {
                                        position = "(126.9, 0.3, -44.1)",
                                        rotation = "-90"
                                    },
                                    new LocationConfig
                                    {
                                        position = "(-94.7, 0.3, -10.9)",
                                        rotation = "90"
                                    }
                                },
                                enableSeller = true,
                                sellerName = "seller_default",
                                sellerLocation = new LocationConfig
                                {
                                    position = "(34.6, 0.3, 22.6)",
                                    rotation = "90"
                                },
                                sellerBiplaneLocation = new LocationConfig
                                {
                                    position = "(50.5, 0.3, 15.0)",
                                    rotation = "180"
                                }
                            }
                        },
                        customSpawnPointsConfig = new CustomSpawnPointsConfig
                        {
                            enableSpawn = false,
                            presets = new Dictionary<string, float>
                            {
                                ["biplane_airfield"] = 100
                            },
                            points = new List<LocationConfig>()
                        }
                    },
                    biplanePresets = new Dictionary<string, BiplaneSetting>
                    {
                        ["biplane_default"] = new BiplaneSetting
                        {
                            customItemShortname = "biplaneitem_default",
                            permission = "",
                            force = 10000f,
                            maxVelosity = 35f,
                            maxHeight = 300f,
                            fuelAmount = 100,
                            biplaneControlSetting = new BiplaneControlSetting
                            {
                                turningADSpeed = 1,
                                turningMouseSpeed = 1,
                            },
                            engineComponents = true,
                            engineComponentsLvl = 2,
                            countSlots = 3,
                            rocketTime = 1f,
                            bombs = false,
                            rockets = false,
                            infinityRockets = false,
                            infinityBombs = false
                        },
                        ["biplane_airfield"] = new BiplaneSetting
                        {
                            customItemShortname = "biplaneitem_airfield",
                            permission = "",
                            force = 10000f,
                            maxVelosity = 35f,
                            maxHeight = 300f,
                            fuelAmount = 0,
                            biplaneControlSetting = new BiplaneControlSetting
                            {
                                turningADSpeed = 1,
                                turningMouseSpeed = 1,
                            },
                            engineComponents = false,
                            engineComponentsLvl = 2,
                            countSlots = 3,
                            rocketTime = 1f,
                            bombs = false,
                            rockets = false,
                            infinityRockets = false,
                            infinityBombs = false
                        },
                        ["biplane_bomber"] = new BiplaneSetting
                        {
                            customItemShortname = "biplaneitem_bomber",
                            permission = "biplane.bomber.use",
                            force = 10000f,
                            maxVelosity = 35f,
                            maxHeight = 300f,
                            fuelAmount = 100,
                            biplaneControlSetting = new BiplaneControlSetting
                            {
                                turningADSpeed = 1,
                                turningMouseSpeed = 1,
                            },
                            engineComponents = true,
                            engineComponentsLvl = 2,
                            countSlots = 3,
                            rocketTime = 1f,
                            bombs = true,
                            rockets = false,
                            infinityRockets = false,
                            infinityBombs = false
                        },
                        ["biplane_stormtrooper"] = new BiplaneSetting
                        {
                            customItemShortname = "biplaneitem_stormtrooper",
                            permission = "biplane.stormtrooper.use",
                            force = 10000f,
                            maxVelosity = 35f,
                            maxHeight = 300f,
                            fuelAmount = 100,
                            biplaneControlSetting = new BiplaneControlSetting
                            {
                                turningADSpeed = 1,
                                turningMouseSpeed = 1,
                            },
                            engineComponents = true,
                            engineComponentsLvl = 2,
                            countSlots = 3,
                            rocketTime = 1f,
                            bombs = true,
                            rockets = false,
                            infinityRockets = false,
                            infinityBombs = false
                        }
                    },
                    itemsPresets = new Dictionary<string, NamedItem>
                    {
                        ["biplaneitem_default"] = new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2767743723,
                            name = "Biplane"
                        },
                        ["biplaneitem_airfield"] = new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561072,
                            name = "Biplane Airfield"
                        },
                        ["biplaneitem_bomber"] = new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561506,
                            name = "Bomber"
                        },
                        ["biplaneitem_stormtrooper"] = new NamedItem
                        {
                            shortname = "box.wooden.large",
                            skin = 2776561787,
                            name = "Stormtrooper"
                        }
                    },
                    sellersPresets = new Dictionary<string, HashSet<ProductSetting>>
                    {
                        ["seller_default"] = new HashSet<ProductSetting>
                        {
                            new ProductSetting
                            {
                                text = "Biplane",
                                itemSetting = new SellerItem
                                {
                                    cost = 750,
                                    shortname = "scrap",
                                    skin = 0
                                },
                                biplaneName = "biplane_default"
                            },
                            new ProductSetting
                            {
                                text = "Stormtrooper",
                                itemSetting = new SellerItem
                                {
                                    cost = 1500,
                                    shortname = "scrap",
                                    skin = 0
                                },
                                biplaneName = "biplane_stormtrooper"
                            },
                            new ProductSetting
                            {
                                text = "Bomber",
                                itemSetting = new SellerItem
                                {
                                    cost = 1500,
                                    shortname = "scrap",
                                    skin = 0
                                },
                                biplaneName = "biplane_bomber"
                            }
                        }
                    },
                    rockets = new HashSet<WeaponItem>
                    {
                        new WeaponItem
                        {
                            shortname = "ammo.rocket.hv",
                            skin = 0,
                            prefab = "assets/prefabs/ammo/rocket/rocket_hv.prefab"
                        },
                        new WeaponItem
                        {
                            shortname = "ammo.rocket.basic",
                            skin = 0,
                            prefab = "assets/prefabs/ammo/rocket/rocket_hv.prefab"
                        }
                    },
                    bombs = new HashSet<WeaponItem>
                    {
                        new WeaponItem
                        {
                            shortname = "ammo.grenadelauncher.he",
                            skin = 0,
                            prefab = "assets/prefabs/ammo/40mmgrenade/40mm_grenade_he.prefab"
                        }
                    },
                    guiSetting = new GUISetting
                    {
                        isGUI = true,
                        offsetMin = "-100 70",
                        offsetMax = "100 120",
                        size = 20
                    },
                    marker = new MarkerConfig
                    {
                        enable = true,
                        text = "Biplane seller"
                    }
                };
            }
        }
        #endregion Config
    }
}

namespace Oxide.Plugins.BiplaneExtensionMethods
{
    public static class ExtensionMethods
    {
        #region Any
        public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) return true;
            return false;
        }
        #endregion Any

        #region Where
        public static HashSet<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            HashSet<TSource> result = new HashSet<TSource>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) result.Add(enumerator.Current);
            return result;
        }
        #endregion Where

        #region FirstOrDefault
        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) return enumerator.Current;
            return default(TSource);
        }
        #endregion FirstOrDefault

        public static bool IsExists(this BaseNetworkable entity) => entity != null && !entity.IsDestroyed;
    }
}