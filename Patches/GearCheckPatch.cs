using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using ProjectM.Gameplay.Systems;
using Unity.Collections;
using Unity.Entities;
using BestGearGuard;
using BestGearGuard.Services;
using BestGearGuard.Utils;
using Stunlock.Network;

namespace BestGearGuard.Patches
{
    [HarmonyPatch]
    public static class GearCheckPatch
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _warned = new();

        // ── EntityQuery cache ────────────────────────────────────────────────
        // FIX #1 : on ne recrée plus la query à chaque frame.
        // Elle est initialisée une seule fois au premier appel et réutilisée.
        private static EntityQuery _userQuery;
        private static bool _queryInitialized = false;

        private static EntityQuery GetUserQuery(EntityManager em)
        {
            if (!_queryInitialized)
            {
                _userQuery = em.CreateEntityQuery(ComponentType.ReadOnly<User>());
                _queryInitialized = true;
            }
            return _userQuery;
        }

        // ── Gear equip patches ───────────────────────────────────────────────

        [HarmonyPatch(typeof(ArmorLevelSystem_Spawn), nameof(ArmorLevelSystem_Spawn.OnUpdate))]
        [HarmonyPostfix]
        public static void ArmorPostfix(ArmorLevelSystem_Spawn __instance)
            => CheckAllPlayers(__instance.EntityManager);

        [HarmonyPatch(typeof(WeaponLevelSystem_Spawn), nameof(WeaponLevelSystem_Spawn.OnUpdate))]
        [HarmonyPostfix]
        public static void WeaponPostfix(WeaponLevelSystem_Spawn __instance)
            => CheckAllPlayers(__instance.EntityManager);

        // FIX #2 : SpellLevelSystem_Spawn n'expose pas EntityManager directement.
        // On passe par VWorld.Server (null-checked) plutôt que Core.EntityManager.
        [HarmonyPatch(typeof(SpellLevelSystem_Spawn), nameof(SpellLevelSystem_Spawn.OnUpdate))]
        [HarmonyPostfix]
        public static void SpellPostfix()
        {
            var server = VWorld.Server;
            if (server == null) return;
            CheckAllPlayers(server.EntityManager);
        }

        [HarmonyPatch(typeof(EquipItemFromInventorySystem), nameof(EquipItemFromInventorySystem.OnUpdate))]
        [HarmonyPostfix]
        public static void EquipFromInventoryPostfix(EquipItemFromInventorySystem __instance)
            => CheckAllPlayers(__instance.EntityManager);

        [HarmonyPatch(typeof(EquipItemSystem), nameof(EquipItemSystem.OnUpdate))]
        [HarmonyPostfix]
        public static void EquipPostfix(EquipItemSystem __instance)
            => CheckAllPlayers(__instance.EntityManager);

        [HarmonyPatch(typeof(MoveItemBetweenInventoriesSystem), nameof(MoveItemBetweenInventoriesSystem.OnUpdate))]
        [HarmonyPostfix]
        public static void MoveItemPostfix(MoveItemBetweenInventoriesSystem __instance)
            => CheckAllPlayers(__instance.EntityManager);

        [HarmonyPatch(typeof(UnEquipItemSystem), nameof(UnEquipItemSystem.OnUpdate))]
        [HarmonyPostfix]
        public static void UnEquipPostfix(UnEquipItemSystem __instance)
            => CheckAllPlayers(__instance.EntityManager);

        // FIX #3 : nettoyage du HashSet _warned à la déconnexion d'un joueur,
        // pour qu'il reçoive bien l'avertissement à sa prochaine connexion.
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected))]
        [HarmonyPostfix]
        public static void OnUserDisconnected(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
        {
            try
            {
                var em = __instance.EntityManager;
                var userEntities = GetUserQuery(em).ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var ue in userEntities)
                    {
                        if (!em.HasComponent<User>(ue)) continue;
                        var u = em.GetComponentData<User>(ue);
                        if (!u.IsConnected)
                        {
                            _warned.Remove(u.PlatformId);
                        }
                    }
                }
                finally
                {
                    userEntities.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning($"[GearCheckPatch] OnUserDisconnected cleanup error: {e.Message}");
            }
        }

        // ── Core check ───────────────────────────────────────────────────────

        private static void CheckAllPlayers(EntityManager em)
        {
            if (!GearGuardSettings.Enabled.Value) return;

            // FIX #1 : réutilise la query mise en cache
            var userEntities = GetUserQuery(em).ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var userEntity in userEntities)
                {
                    if (!em.Exists(userEntity) || !em.HasComponent<User>(userEntity)) continue;

                    var user = em.GetComponentData<User>(userEntity);
                    if (!user.IsConnected) continue;

                    var characterEntity = user.LocalCharacter._Entity;
                    if (characterEntity == Entity.Null || !em.Exists(characterEntity)) continue;

                    if (!GearCheckerService.CheckViolation(
                            em, characterEntity,
                            out int armorMaxTier,
                            out int weaponTier,
                            out int amuletTier,
                            out var violation))
                    {
                        _warned.Remove(user.PlatformId);
                        if (GearGuardSettings.DebuffEnabled.Value)
                            GearDebuffService.RemoveDebuff(em, characterEntity);
                        continue;
                    }

                    if (GearGuardSettings.DebuffEnabled.Value)
                        GearDebuffService.ApplyDebuff(em, characterEntity);

                    if (_warned.Contains(user.PlatformId)) continue;
                    _warned.Add(user.PlatformId);

                    SendWarning(em, user, armorMaxTier, weaponTier, amuletTier, violation);
                }
            }
            finally
            {
                userEntities.Dispose();
            }
        }

        private static void SendWarning(
            EntityManager em,
            User user,
            int armorMaxTier,
            int weaponTier,
            int amuletTier,
            GearCheckerService.ViolationType violation)
        {
            int maxAllowed = GearGuardSettings.MaxTierDifference.Value;
            int minAmuletGap = GearGuardSettings.MinAmuletTierBelowArmor.Value;

            var line1 = (FixedString512Bytes)"<color=#ff5555>[GearGuard] WARNING: Your equipment violates the server gear rules!</color>";
            FixedString512Bytes line2;

            switch (violation)
            {
                case GearCheckerService.ViolationType.ArmorSpread:
                    line2 = (FixedString512Bytes)$"<color=#ffaa00>Your armor pieces are too spread out. Max allowed difference: {maxAllowed} tier(s).</color>";
                    break;

                case GearCheckerService.ViolationType.WeaponTooHigh:
                    line2 = (FixedString512Bytes)$"<color=#ffaa00>Your weapon (Tier {weaponTier}) is too high for your armor (Tier {armorMaxTier}). Max allowed: Tier {armorMaxTier + maxAllowed}.</color>";
                    break;

                case GearCheckerService.ViolationType.AmuletTooHigh:
                    line2 = (FixedString512Bytes)$"<color=#ffaa00>Your amulet (Tier {amuletTier}) is too high for your armor (Tier {armorMaxTier}). Max allowed: Tier {armorMaxTier + maxAllowed}.</color>";
                    break;

                case GearCheckerService.ViolationType.AmuletTooLow:
                    line2 = (FixedString512Bytes)$"<color=#ffaa00>Your amulet (Tier {amuletTier}) is too low for your armor (Tier {armorMaxTier}). Min required: Tier {armorMaxTier - minAmuletGap}.</color>";
                    break;

                default:
                    line2 = (FixedString512Bytes)"<color=#ffaa00>Please check your equipped items.</color>";
                    break;
            }

            var line3 = (FixedString512Bytes)"<color=#ffaa00>Please adjust your equipment to comply with the server rules.</color>";

            ServerChatUtils.SendSystemMessageToClient(em, user, ref line1);
            ServerChatUtils.SendSystemMessageToClient(em, user, ref line2);
            ServerChatUtils.SendSystemMessageToClient(em, user, ref line3);
        }
    }
}