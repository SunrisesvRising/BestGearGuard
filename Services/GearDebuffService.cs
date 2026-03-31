using ProjectM;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;
using BestGearGuard.Utils;
using ProjectM.Network;

namespace BestGearGuard.Services
{
    public static class GearDebuffService
    {
        public static readonly PrefabGUID DebuffPrefab = new PrefabGUID(-1315531444); // SunDamageDebuff

        public static void ApplyDebuff(EntityManager em, Entity characterEntity)
        {
            try
            {
                if (characterEntity == Entity.Null || !em.Exists(characterEntity)) return;
                if (BuffUtility.TryGetBuff(em, characterEntity, DebuffPrefab, out _)) return;

                var userEntity = GetUserEntity(em, characterEntity);
                if (userEntity == Entity.Null) return;

                var debugEvents = VWorld.Server.GetExistingSystemManaged<DebugEventsSystem>();
                var applyEvent = new ApplyBuffDebugEvent { BuffPrefabGUID = DebuffPrefab };
                var fromChar = new FromCharacter { Character = characterEntity, User = userEntity };

                debugEvents.ApplyBuff(fromChar, applyEvent);

                // FIX #3 : on re-vérifie que le buff existe et que l'entité est valide
                // avant chaque modification de composant. Chaque RemoveComponent est
                // isolé dans son propre try/catch pour ne pas bloquer les suivants
                // en cas de structural change conflict.
                if (!BuffUtility.TryGetBuff(em, characterEntity, DebuffPrefab, out var buffEntity)) return;
                if (!em.Exists(buffEntity)) return;

                // Réduire les dégâts
                try
                {
                    if (em.HasComponent<SunDamageDebuff>(buffEntity))
                    {
                        var sunDebuff = em.GetComponentData<SunDamageDebuff>(buffEntity);
                        sunDebuff.DamageFactorPerTick = 0.01f;
                        em.SetComponentData(buffEntity, sunDebuff);
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogWarning($"[GearDebuffService] Could not set SunDamageDebuff data: {e.Message}");
                }

                // Rendre permanent : supprimer LifeTime
                try
                {
                    if (em.Exists(buffEntity) && em.HasComponent<LifeTime>(buffEntity))
                        em.RemoveComponent<LifeTime>(buffEntity);
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogWarning($"[GearDebuffService] Could not remove LifeTime: {e.Message}");
                }

                // Supprimer RemoveBuffOnGameplayEvent
                try
                {
                    if (em.Exists(buffEntity) && em.HasComponent<RemoveBuffOnGameplayEvent>(buffEntity))
                        em.RemoveComponent<RemoveBuffOnGameplayEvent>(buffEntity);
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogWarning($"[GearDebuffService] Could not remove RemoveBuffOnGameplayEvent: {e.Message}");
                }

                // FIX #3b : RemoveBuffOnGameplayEventEntry est un type rare dans ProjectM.
                // On le tente uniquement si HasComponent réussit, avec son propre catch.
                try
                {
                    if (em.Exists(buffEntity) && em.HasComponent<RemoveBuffOnGameplayEventEntry>(buffEntity))
                        em.RemoveComponent<RemoveBuffOnGameplayEventEntry>(buffEntity);
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogWarning($"[GearDebuffService] Could not remove RemoveBuffOnGameplayEventEntry: {e.Message}");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError($"[GearDebuffService] ApplyDebuff error: {e.Message}");
            }
        }

        public static void RemoveDebuff(EntityManager em, Entity characterEntity)
        {
            try
            {
                if (characterEntity == Entity.Null || !em.Exists(characterEntity)) return;
                if (!BuffUtility.TryGetBuff(em, characterEntity, DebuffPrefab, out var buffEntity)) return;

                // FIX #3 : vérification supplémentaire de l'existence de l'entité buff
                // avant de tenter de la détruire (elle peut avoir été détruite entre-temps)
                if (!em.Exists(buffEntity)) return;

                DestroyUtility.Destroy(em, buffEntity, DestroyDebugReason.None);
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogError($"[GearDebuffService] RemoveDebuff error: {e.Message}");
            }
        }

        private static Entity GetUserEntity(EntityManager em, Entity characterEntity)
        {
            if (em.HasComponent<PlayerCharacter>(characterEntity))
                return em.GetComponentData<PlayerCharacter>(characterEntity).UserEntity;
            return Entity.Null;
        }
    }
}