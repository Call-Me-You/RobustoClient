using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Client.Weapons.Melee;
using Content.Client.MouseRotator;
using Content.Shared.MouseRotator;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee;
using HarmonyLib;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using RobustoClient.Systems;

namespace RobustoClient.Patches;

/// <summary>
/// United melee patch: combines light-attack aimbot, heavy-attack aimbot,
/// and mouse-rotator rotation/triggerbot into a single consolidated file
/// so the melee code path is maintained in one place.
/// </summary>
public static class MeleePatch
{
    private static IEntityManager? _entMan;
    private static RobustaAimSystem? _aim;

    private static readonly MethodInfo? ClientLightAttackMethod =
        AccessTools.Method(typeof(MeleeWeaponSystem), "ClientLightAttack");
    private static readonly MethodInfo? PlayMeleeWeaponAnimationMethod =
        AccessTools.Method(typeof(MeleeWeaponSystem), "PlayMeleeWeaponAnimation");

    private static RobustaAimSystem Aim
    {
        get
        {
            _entMan ??= IoCManager.Resolve<IEntityManager>();
            _aim ??= _entMan.System<RobustaAimSystem>();
            return _aim;
        }
    }

    [HarmonyPatch(typeof(MeleeWeaponSystem), "ClientLightAttack")]
    public class ClientLightAttack
    {
        [HarmonyPrefix]
        private static void Prefix(
            EntityUid attacker,
            ref MapCoordinates mousePos,
            ref EntityCoordinates coordinates,
            MeleeWeaponComponent meleeComponent)
        {
            if (!RobustaConfig.MeleeAimbotEnabled) return;

            var aim = Aim;
            var output = aim.LockedTarget.HasValue && aim.IsInRange(attacker, aim.LockedTarget.Value, meleeComponent.Range)
                ? aim.GetAimOutputFromEnt(aim.LockedTarget.Value)
                : aim.GetClosestToEntInRange(attacker, meleeComponent.Range, [ attacker ]);

            if (output == null) return;

            mousePos = output.Value.Position;
            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MeleeWeaponSystem), "ClientHeavyAttack")]
    public class ClientHeavyAttack
    {
        [HarmonyPrefix]
        private static void Prefix(
            ref EntityUid user,
            ref EntityCoordinates coordinates,
            ref MeleeWeaponComponent component)
        {
            if (!RobustaConfig.MeleeAimbotEnabled) return;

            var aim = Aim;
            var output = aim.LockedTarget.HasValue && aim.IsInRange(user, aim.LockedTarget.Value, component.Range)
                ? aim.GetAimOutputFromEnt(aim.LockedTarget.Value)
                : aim.GetClosestToEntInRange(user, component.Range, [ user ]);

            if (output == null) return;

            coordinates = new EntityCoordinates(output.Value.Entity, Vector2.Zero);
        }
    }

    [HarmonyPatch(typeof(MouseRotatorSystem), nameof(MouseRotatorSystem.Update))]
    public class MouseRotatorUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!RobustaConfig.MeleeAimbotEnabled && !RobustaConfig.MeleeTriggerbotEnabled)
                return true;

            var playerManager = IoCManager.Resolve<IPlayerManager>();
            var entityManager = IoCManager.Resolve<IEntityManager>();
            var timing = IoCManager.Resolve<IGameTiming>();

            var localPlayer = playerManager.LocalEntity;
            if (localPlayer == null) return true;

            if (!entityManager.HasComponent<MouseRotatorComponent>(localPlayer.Value))
                return true;

            var aimSystem = Aim;
            var xformSystem = entityManager.System<SharedTransformSystem>();
            var handsSystem = entityManager.System<SharedHandsSystem>();

            EntityUid? weapon = null;
            MeleeWeaponComponent? melee = null;
            if (handsSystem.TryGetActiveItem(localPlayer.Value, out var activeWeapon) &&
                entityManager.TryGetComponent(activeWeapon.Value, out MeleeWeaponComponent? meleeComp))
            {
                weapon = activeWeapon;
                melee = meleeComp;
            }

            float searchRange = melee != null ? melee.Range : RobustaConfig.RangedAimbotRadius;

            var exclude = new HashSet<EntityUid> { localPlayer.Value };
            var target = aimSystem.LockedTarget.HasValue && aimSystem.IsInRange(localPlayer.Value, aimSystem.LockedTarget.Value, searchRange)
                ? aimSystem.GetAimOutputFromEnt(aimSystem.LockedTarget.Value)
                : aimSystem.GetClosestToEntInRange(localPlayer.Value, searchRange, exclude);

            if (target == null) return true;

            if (!entityManager.TryGetComponent<TransformComponent>(localPlayer.Value, out _))
                return true;

            var playerWorldPos = xformSystem.GetWorldPosition(localPlayer.Value);
            var targetWorldPos = target.Value.Position.Position;
            var diff = targetWorldPos - playerWorldPos;

            if (diff.LengthSquared() <= 0.001f) return true;

            var baseAngle = diff.ToAngle();
            var correctedAngle = new Angle(baseAngle.Theta + MathHelper.PiOver2);
            xformSystem.SetWorldRotation(localPlayer.Value, correctedAngle);

            if (RobustaConfig.MeleeTriggerbotEnabled && weapon.HasValue && melee != null)
            {
                if (diff.Length() <= melee.Range && timing.CurTime >= melee.NextAttack)
                {
                    var meleeSystem = entityManager.System<MeleeWeaponSystem>();
                    var targetCoords = new EntityCoordinates(target.Value.Entity, Vector2.Zero);

                    ClientLightAttackMethod?.Invoke(meleeSystem, [localPlayer.Value, target.Value.Position, targetCoords, weapon.Value, melee]);
                    PlayMeleeWeaponAnimationMethod?.Invoke(meleeSystem, [weapon.Value, melee, diff.ToAngle(), target.Value.Entity, false]);

                    melee.NextAttack = timing.CurTime + TimeSpan.FromSeconds(1.1 / melee.AttackRate);
                }
            }

            return false;
        }
    }
}