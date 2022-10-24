//#define debug

using Newtonsoft.Json;
using Rust;
using System;
using System.Collections.Generic;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AntiRoofCamp", "Navio", "1.0")]
    [Description("Anti-Roofcamping plugin")]

    class AntiRoofCamp : RustPlugin
    {
        #region Config
        private static Configuration _config;

        class Configuration
        {
            [JsonProperty(PropertyName = "Combat duration after attack initiation (Milliseconds)")]
            public long combatDuration = 30000;
            
            [JsonProperty(PropertyName = "Warning message for someone that's roofcamping (On Chat)")]
            public string antiRoofCampMessageChat = "<color=#ff0000>You can't roofcamp! Damage was returned to you.</color>";
            [JsonProperty(PropertyName = "Warning message for someone that's roofcamping (On Screen)")]
            public string antiRoofCampMessageScreen = "<color=#ff0000>You can't roofcamp!</color>";
            [JsonProperty(PropertyName = "Show message on text chat")]
            public bool showMessageOnTextChat = true;
            [JsonProperty(PropertyName = "Show message on screen")]
            public bool showMessageOnScreen = true;
            [JsonProperty(PropertyName = "Message fade out effect duration (Seconds)")]
            public float messageFadeOut = 0.3f;
            [JsonProperty(PropertyName = "Message display duration (Seconds)")]
            public float messageDuration = 2f;
            
            [JsonProperty(PropertyName = "Give back damage to player")]
            public bool giveBackDamage = false;
            [JsonProperty(PropertyName = "Damage divider")]
            public float damageDivider = 5f;
            [JsonProperty(PropertyName = "Give back damage till player reaches this amount of hp")]
            public int giveBackDamageMinHp = 5;

            [JsonProperty(PropertyName = "Raid duration after raid initiation (Milliseconds)")]
            public long raidDuration = 120000;

            [JsonProperty(PropertyName = "Roofcamper can shoot to helicopter passenger")]
            public bool heliRoofcamping = true;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                SaveConfig();
            }
            catch
            {
                LoadDefaultConfig();

                if (!Config.Exists())
                {
                    SaveConfig();
                }

                Puts("Your configuration file contains an error. Loading default config...");
            }
        }
        protected override void SaveConfig() => Config.WriteObject(_config, true);
        #endregion

        #region Global variables
        //       Attacker Team  VictimT Duration
        Dictionary<Tuple<ulong, ulong>, long> _combatLog = new Dictionary<Tuple<ulong, ulong>, long>();
        private String[] _raidWeapons = new String[7] {"explosive.satchel.deployed", "grenade.beancan.deployed", "rocket_basic", "rocket_hv", "explosive.timed.deployed", "40mm_grenade_he", "rocket_mlrs"};
        private String[] _raidProjectiles = new String[3] {"riflebullet_explosive", "riflebullet_fire", "pistolbullet_fire"};
        private CuiElementContainer _messageUi = new CuiElementContainer();
        #endregion

        #region Hooks

        void Loaded()
        {
            LoadConfig();
            InitializeUi();

            timer.Every(60f, () =>
            {
                long currentMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                List<Tuple<ulong, ulong>> toRemoveList = new List<Tuple<ulong, ulong>>();

                foreach(KeyValuePair<Tuple<ulong, ulong>, long> pair in _combatLog)
                {
                    if(pair.Value < currentMs)
                    {
                        toRemoveList.Add(pair.Key);
                    }
                }

                for(int i = 0; i < toRemoveList.Count; i++)
                {
                    _combatLog.Remove(toRemoveList[i]);
                }
            });
        }

        object OnEntityTakeDamage(BaseCombatEntity victimEnt, HitInfo info)
        {
            if (!victimEnt.IsValid() || victimEnt.IsDestroyed || victimEnt.IsDead()) //|| victimEnt.IsNpc)
                return null;

            if (!(victimEnt is BasePlayer))
            {
                if (info.WeaponPrefab == null)
                    return null;

                //If c4, rocket or explo ammo etc. are not used return null
                if (!_raidWeapons.Contains(info.WeaponPrefab.name) && (info.ProjectilePrefab == null || !_raidProjectiles.Contains(info.ProjectilePrefab.name)))
                {
                    return null;
                }

#if debug
                Puts("Raiding materials were used...");
#endif
                
                //Getting building privilege at hit position
                BuildingPrivlidge bp = victimEnt.GetBuildingPrivilege(new OBB(info.PointEnd, new Vector3(), new Quaternion()));

                if (bp == null)
                    return null;
                
                if (bp.authorizedPlayers.Count <= 0)
                    return null;

                ulong userId = bp.authorizedPlayers[0].userid;
                
                //Check if building privilage is from player's tc
                if (!userId.IsSteamId())
                    return null;

                BasePlayer bpUser = null;
                
                //Getting BasePlayer from owner id
                foreach (BasePlayer p in BasePlayer.activePlayerList)
                {
                    if (p.userID == userId)
                        bpUser = p;
                }

                if (bpUser == null)
                    return null;

                ulong vtid = bpUser.Team == null ? bpUser.userID : bpUser.Team.teamID;
                ulong atid = info.InitiatorPlayer.Team == null ? info.InitiatorPlayer.userID : info.InitiatorPlayer.Team.teamID;
                
#if debug
                Puts("  Raider team id: " + atid);
                Puts("  Defender team id: " + vtid);
#endif

                if (_combatLog.ContainsKey(Tuple.Create(atid, vtid)))
                    _combatLog[Tuple.Create(atid, vtid)] = DateTimeOffset.Now.ToUnixTimeMilliseconds() + _config.raidDuration;
                else
                    _combatLog.Add(Tuple.Create(atid, vtid), DateTimeOffset.Now.ToUnixTimeMilliseconds() + _config.raidDuration);
                
                if (_combatLog.ContainsKey(Tuple.Create(vtid, atid)))
                    _combatLog[Tuple.Create(vtid, atid)] = DateTimeOffset.Now.ToUnixTimeMilliseconds() + _config.raidDuration;
                else
                    _combatLog.Add(Tuple.Create(vtid, atid), DateTimeOffset.Now.ToUnixTimeMilliseconds() + _config.raidDuration);
                
                return null;

            }

            BaseEntity attackerEnt = info.Initiator;

            if (!(attackerEnt is BasePlayer))
                return null;

            if (attackerEnt.IsNpc || !attackerEnt.IsValid() || attackerEnt.IsDestroyed)
                return null;
            
            if (victimEnt == attackerEnt)
                return null;

            BasePlayer victimPlayer = victimEnt.ToPlayer();
            BasePlayer attackerPlayer = attackerEnt.ToPlayer();
            
            //Checking if both attacker and victim are on foundation or ground
            if (IsOnGroundOrFoundation(attackerPlayer) && IsOnGroundOrFoundation(victimPlayer))
            {
                Puts("Is on ground or foundation");
                return null;
            }
            
            //Checking if attacker is on monument
            if (IsPlayerAtMonument(attackerPlayer))
            {
                Puts("Player is on a monument");
                return null;
            }
            
            long currentMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            ulong victimTeamId = victimPlayer.Team == null ? victimPlayer.userID : victimPlayer.Team.teamID;
            ulong attackerTeamId = attackerPlayer.Team == null ? attackerPlayer.userID : attackerPlayer.Team.teamID;
            
            //Checking if victim is roof camping and attacker is standing on ground or foundation
            if (IsOnGroundOrFoundation(attackerPlayer) && !IsOnGroundOrFoundation(victimPlayer))
            {
                //Adding attacker to combatlog
                if (!_combatLog.TryAdd(Tuple.Create(attackerTeamId, victimTeamId), currentMs + _config.combatDuration))
                {
                    _combatLog[Tuple.Create(attackerTeamId, victimTeamId)] = currentMs + _config.combatDuration;
                }

                return null;
            }
            
            //Checking if victim is on the same tc area as attacker
            if (victimEnt.GetBuildingPrivilege() != null && attackerEnt.GetBuildingPrivilege() != null)
            {
                if (victimEnt.GetBuildingPrivilege().transform.position.Equals(attackerEnt.GetBuildingPrivilege().transform.position))
                {
                    //Checking if victim is near attacker's construction
                    if (IsNearBuilding(victimEnt.transform.position))
                        return null;
                }
            }
            
            //Checking if victim is flying with heli
            if (_config.heliRoofcamping)
            {
                if (IsInHeli(victimPlayer))
                    return null;
            }
            
            //Checking if attacker is flying with heli not on his building privilage
            if (!IsOnGroundOrFoundation(attackerPlayer) && IsInHeli(attackerPlayer))
                if (!attackerEnt.GetBuildingPrivilege().IsAuthed(attackerPlayer))
                    return null;

            //Checking if fight is in water (speargun's etc.)
            if (attackerPlayer.IsSwimming())
                return null;

            //Checking if victim was already attacking attacker
            foreach (KeyValuePair<Tuple<ulong, ulong>, long> pair in _combatLog)
            {
                if (pair.Key.Item1 == victimTeamId && pair.Key.Item2 == attackerTeamId)
                {
                    if (pair.Value > currentMs)
                    {
#if debug
                        Puts($"  Damage dealed because pair.Value > DateTime.Now.Millisecond");
#endif                  
                        return null;
                    }
                }
            }

            ShowAntiRoofCampMessage(attackerPlayer);

            if (_config.giveBackDamage)
            {
                DealDamageBack(attackerPlayer, info);
            }

            return false;
        }

        private void ShowAntiRoofCampMessage(BasePlayer player)
        {
            if (_config.showMessageOnScreen)
            {
                CreateUi(player);
            }

            if (_config.showMessageOnTextChat)
            {
                player.IPlayer.Reply(_config.antiRoofCampMessageChat);
            }
        }

        private void DealDamageBack(BasePlayer player, HitInfo info)
        {
            float damageTotal = info.damageTypes.Total() / _config.damageDivider;
            float hpDiff = player.IPlayer.Health - _config.giveBackDamageMinHp;
            float damage = _config.giveBackDamageMinHp <= 0 ? damageTotal : hpDiff < damageTotal ? hpDiff : damageTotal;
            
            player.Hurt(damage);
        }
        #endregion

        #region Utility functions

        private bool IsInHeli(BasePlayer player)
        {
            if (player.GetMounted() != null)
            {
                if (player.ToPlayer().GetMountedVehicle().ShortPrefabName == "minicopter.entity" ||
                    player.ToPlayer().GetMountedVehicle().ShortPrefabName == "scraptransporthelicopter")
                {
                    return true;
                }
            }

            return false;
        }
        
        private bool IsPlayerAtMonument(BasePlayer player)
        {
            Vector3 playerPos = player.transform.position;
            
            List<Collider> colliders = new List<Collider>();
            Vis.Colliders(playerPos, 0.1f, colliders);

            if (player.GetBuildingPrivilege() != null && player.GetBuildingPrivilege().OwnerID.IsSteamId())
                return false;

            bool hasPreventBuilding = false;

            foreach (Collider collider in colliders)
            {
                if (collider.gameObject.layer == (int)Layer.Player_Server)
                    continue;

                if (collider.gameObject.layer == (int)Layer.Prevent_Building)
                    hasPreventBuilding = true;
                
                BaseEntity entity = collider.gameObject.ToBaseEntity();
                if (entity != null)
                {
                    if (entity.ShortPrefabName == "cargoshiptest")
                        return true;

                    if (entity.OwnerID != 0)
                        return false;
                }
            }

            if (hasPreventBuilding)
                return true;
            
            return false;
        }
        
        private bool IsNearBuilding(Vector3 playerPos)
        {
            RaycastHit hit;
            if(SphereRaycast(playerPos + new Vector3(0.0f, 1f, 0.0f), 6f, out hit, Layers.Mask.Construction))
            {
                if (hit.GetEntity().IsValid())
                    return true;
            }

#if debug
            Puts("  isNearBuilding() Did not found any building");
#endif

            return false;
        }

        private bool IsOnGroundOrFoundation(BasePlayer player)
        {

            Vector3 playerPos = player.transform.position;
#if debug
            Puts($"Checking if entity is on ground or foundation at {playerPos.ToString()} ");
#endif

            RaycastHit hit;
            if (Physics.Raycast(playerPos + new Vector3(0.0f, 0.2f, 0.0f), Vector3.down, out hit, 2f, Rust.Layers.Construction))
            {
                var entity = hit.GetEntity();
                if (entity.IsValid())
                {
                    if (entity.PrefabName.Contains("foundation"))
                    {
#if debug
                        Puts("  Player is on foundation");
#endif
                        return true;
                    }
                }
            }
            
            RaycastHit hitt;
            if (Physics.Raycast(playerPos + new Vector3(0.0f, 0.2f, 0.0f), Vector3.down, out hitt, 2f, LayerMask.GetMask("Terrain", "World")))
            {
                if (hitt.collider.ToBaseEntity() != null)
                {
                    if (hitt.collider.ToBaseEntity().ShortPrefabName == "cursedcauldron.deployed" || hitt.collider.ToBaseEntity().ShortPrefabName == "elevator_lift")
                    {
                        if (Physics.Raycast(hitt.collider.gameObject.transform.position + new Vector3(0, 0, 0), Vector3.down, 0.1f,
                                LayerMask.GetMask("Terrain", "World")))
                            return true;
                        
                        return false;
                    }
                }
                
                return true;
            }
            
            return false;
        }

        private bool SphereRaycast(Vector3 position, float radius, out RaycastHit hit, int layerMask)
        {
            hit = default(RaycastHit);

            Vector3[] vecArr = new Vector3[17] {
                new Vector3(0.0f, -1f, 0.0f),

                Vector3.right,
                Quaternion.Euler(0, 45, 0) * Vector3.right,
                Vector3.forward,
                Quaternion.Euler(0, 45, 0) * Vector3.forward,
                Vector3.left,
                Quaternion.Euler(0, 45, 0) * Vector3.left,
                Vector3.back,
                Quaternion.Euler(0, 45, 0) * Vector3.back,

                Quaternion.Euler(0, 45, -25) * Vector3.right,
                Quaternion.Euler(25, 45, 0) * Vector3.forward ,
                Quaternion.Euler(0, 45, 25) * Vector3.left,
                Quaternion.Euler(-25, 45, 0) * Vector3.back,
                Quaternion.Euler(0, 0, -25) * Vector3.right,
                Quaternion.Euler(25, 0, 0) * Vector3.forward,
                Quaternion.Euler(0, 0, 25) * Vector3.left,
                Quaternion.Euler(-25, 0, 0) * Vector3.back
            };

            for(int i = 0; i < 17; i++)
            {
#if debug
                foreach (var player in BasePlayer.activePlayerList)
                {
                    player.SendConsoleCommand("ddraw.line", 5f, Color.red, position, position + vecArr[i] * radius);
                }
#endif

                RaycastHit hitInfo;
                if (Physics.Raycast(position, vecArr[i], out hitInfo, radius, layerMask, QueryTriggerInteraction.Ignore))
                {
                    if (hitInfo.GetEntity().IsValid())
                    {
                        hit = hitInfo;
#if debug
                        foreach (var player in BasePlayer.activePlayerList)
                        {
                            player.SendConsoleCommand("ddraw.sphere", 5f, Color.green, hit.point, 0.1f);
                        }
#endif
                        return true;
                    }
                }
            }

            return false;

        }
        #endregion
        
        #region UI
        private void InitializeUi()
        {
            _messageUi.Add(new CuiElement()
            {
                Name = "AntiRoofCamp",
                Parent = "Hud",
                
                FadeOut = _config.messageFadeOut,
                
                Components =
                {
                    new CuiOutlineComponent()
                    {
                        Distance = "1 1", Color = "0 0 0 1"
                    },
                    new CuiTextComponent()
                    {
                        Text = _config.antiRoofCampMessageScreen,
                        FontSize = 24,
                        Align = TextAnchor.MiddleCenter
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0.305 0.756",
                        AnchorMax = "0.695 0.894"
                    }
                }
            });
        }
        
        private void CreateUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "AntiRoofCamp");
            CuiHelper.AddUi(player, _messageUi);
            
            timer.Once(_config.messageDuration, () =>
            {
                CuiHelper.DestroyUi(player, "AntiRoofCamp");
            });
        }
        #endregion

    }
}

