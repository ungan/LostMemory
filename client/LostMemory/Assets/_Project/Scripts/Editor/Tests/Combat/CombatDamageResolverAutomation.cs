using System;
using LostMemory.Combat;
using UnityEditor;
using UnityEngine;

namespace LostMemory.Tests.Combat
{
    public static class CombatDamageResolverAutomation
    {
        [MenuItem("Lost Memory/Tests/Combat Damage Resolver")]
        public static void Run()
        {
            RunCase("CriticalPolicyNever", Resolve_WhenCriticalPolicyNever_ReturnsBaseDamage);
            RunCase("AttackPower", Resolve_WhenAttackPowerEnabled_AppliesAttackPowerMultiplier);
            RunCase("Finisher", Resolve_WhenFinisherEnabled_AppliesFinisherAfterAttackPower);
            RunCase("CriticalSuccess", Resolve_WhenCriticalRollSucceeds_AppliesCriticalDamageBonus);
            RunCase("CriticalFail", Resolve_WhenCriticalRollFails_DoesNotApplyCriticalDamageBonus);
            RunCase("DamageOverTime", Resolve_WhenDamageOverTimeTick_RollsCriticalWithoutAttackPowerAndSuppressesOnHit);

            Debug.Log("[CombatDamageResolverAutomation] Passed 6 combat damage resolver checks.");
        }

        private static void RunCase(string name, Action test)
        {
            try
            {
                test();
                Debug.Log($"[CombatDamageResolverAutomation] PASS {name}");
            }
            catch (Exception ex)
            {
                throw new Exception($"[CombatDamageResolverAutomation] FAIL {name}: {ex.Message}", ex);
            }
        }

        private static void Resolve_WhenCriticalPolicyNever_ReturnsBaseDamage()
        {
            WithStats(stats =>
            {
                CombatDamageRequest request = new CombatDamageRequest(
                    100f,
                    DamageSourceKind.Melee,
                    1UL,
                    criticalPolicy: CriticalPolicy.Never,
                    applyAttackPower: false);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0f);

                AssertClose(100f, result.FinalDamage);
                AssertFalse(result.WasCritical, "Expected non-critical result.");
            });
        }

        private static void Resolve_WhenAttackPowerEnabled_AppliesAttackPowerMultiplier()
        {
            WithStats(stats =>
            {
                stats.AddPermanent(StatId.AttackPower, 0.5f, "test");
                CombatDamageRequest request = new CombatDamageRequest(
                    100f,
                    DamageSourceKind.Projectile,
                    1UL,
                    criticalPolicy: CriticalPolicy.Never,
                    applyAttackPower: true);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0f);

                AssertClose(150f, result.FinalDamage);
                AssertFalse(result.WasCritical, "Expected non-critical result.");
            });
        }

        private static void Resolve_WhenFinisherEnabled_AppliesFinisherAfterAttackPower()
        {
            WithStats(stats =>
            {
                stats.AddPermanent(StatId.AttackPower, 0.5f, "test");
                stats.AddPermanent(StatId.FinisherDamage, 0.25f, "test");
                CombatDamageRequest request = new CombatDamageRequest(
                    100f,
                    DamageSourceKind.Melee,
                    1UL,
                    criticalPolicy: CriticalPolicy.Never,
                    applyAttackPower: true,
                    applyFinisherDamage: true);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0f);

                AssertClose(187.5f, result.FinalDamage);
                AssertFalse(result.WasCritical, "Expected non-critical result.");
            });
        }

        private static void Resolve_WhenCriticalRollSucceeds_AppliesCriticalDamageBonus()
        {
            WithStats(stats =>
            {
                stats.AddPermanent(StatId.Critical, 1f, "test");
                stats.AddPermanent(StatId.CriticalDamage, 0.25f, "test");
                CombatDamageRequest request = new CombatDamageRequest(
                    100f,
                    DamageSourceKind.Area,
                    1UL,
                    applyAttackPower: false);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0f);

                AssertClose(175f, result.FinalDamage);
                AssertTrue(result.WasCritical, "Expected critical result.");
            });
        }

        private static void Resolve_WhenCriticalRollFails_DoesNotApplyCriticalDamageBonus()
        {
            WithStats(stats =>
            {
                stats.AddPermanent(StatId.Critical, 0.5f, "test");
                stats.AddPermanent(StatId.CriticalDamage, 0.25f, "test");
                CombatDamageRequest request = new CombatDamageRequest(
                    100f,
                    DamageSourceKind.Projectile,
                    1UL,
                    applyAttackPower: false);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0.75f);

                AssertClose(100f, result.FinalDamage);
                AssertFalse(result.WasCritical, "Expected non-critical result.");
            });
        }

        private static void Resolve_WhenDamageOverTimeTick_RollsCriticalWithoutAttackPowerAndSuppressesOnHit()
        {
            WithStats(stats =>
            {
                stats.AddPermanent(StatId.AttackPower, 1f, "test");
                stats.AddPermanent(StatId.Critical, 1f, "test");
                CombatDamageRequest request = new CombatDamageRequest(
                    10f,
                    DamageSourceKind.DamageOverTime,
                    2UL,
                    sourceId: 100UL,
                    tickIndex: 3,
                    onHitPolicy: OnHitPolicy.Suppress,
                    applyAttackPower: false);

                CombatDamageResult result = CombatDamageResolver.Resolve(request, stats, 0f);

                AssertClose(15f, result.FinalDamage);
                AssertTrue(result.WasCritical, "Expected DOT tick critical result.");
                AssertTrue(result.OnHitPolicy == OnHitPolicy.Suppress, "Expected DOT OnHit to be suppressed.");
                AssertTrue(result.TickIndex == 3, "Expected DOT tick index to be preserved.");
                AssertTrue(result.SourceId == 100UL, "Expected DOT source id to be preserved.");
            });
        }

        private static void WithStats(Action<PlayerStatModifierContainer> test)
        {
            GameObject statsObject = new GameObject("CombatDamageResolverAutomation_Stats");
            try
            {
                PlayerStatModifierContainer stats = statsObject.AddComponent<PlayerStatModifierContainer>();
                test(stats);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(statsObject);
            }
        }

        private static void AssertClose(float expected, float actual)
        {
            if (Mathf.Abs(expected - actual) > 0.001f)
            {
                throw new Exception($"Expected {expected:F3}, got {actual:F3}.");
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition)
            {
                throw new Exception(message);
            }
        }
    }
}
