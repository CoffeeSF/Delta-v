using System.Diagnostics.CodeAnalysis;
using Content.Shared.Actions;
using Content.Shared.Administration.Logs;
using Content.Shared.CombatMode;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Gravity;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Weapons.Ranged.Systems;

public abstract partial class SharedGunSystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] protected readonly IMapManager MapManager = default!;
    [Dependency] private   readonly INetManager _netManager = default!;
    [Dependency] protected readonly IPrototypeManager ProtoManager = default!;
    [Dependency] protected readonly IRobustRandom Random = default!;
    [Dependency] protected readonly ISharedAdminLogManager Logs = default!;
    [Dependency] protected readonly DamageableSystem Damageable = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private   readonly ItemSlotsSystem _slots = default!;
    [Dependency] private   readonly RechargeBasicEntityAmmoSystem _recharge = default!;
    [Dependency] protected readonly SharedActionsSystem Actions = default!;
    [Dependency] protected readonly SharedAppearanceSystem Appearance = default!;
    [Dependency] private   readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] protected readonly SharedContainerSystem Containers = default!;
    [Dependency] protected readonly ExamineSystemShared Examine = default!;
    [Dependency] protected readonly SharedPhysicsSystem Physics = default!;
    [Dependency] protected readonly SharedPopupSystem PopupSystem = default!;
    [Dependency] protected readonly ThrowingSystem ThrowingSystem = default!;
    [Dependency] protected readonly TagSystem TagSystem = default!;
    [Dependency] protected readonly SharedAudioSystem Audio = default!;
    [Dependency] protected readonly SharedProjectileSystem Projectiles = default!;
    [Dependency] protected readonly SharedTransformSystem TransformSystem = default!;

    protected ISawmill Sawmill = default!;

    private const float InteractNextFire = 0.3f;
    private const double SafetyNextFire = 0.5;
    private const float EjectOffset = 0.4f;
    protected const string AmmoExamineColor = "yellow";
    protected const string FireRateExamineColor = "yellow";
    protected const string ModeExamineColor = "cyan";

    public override void Initialize()
    {
        Sawmill = Logger.GetSawmill("gun");
        Sawmill.Level = LogLevel.Info;
        SubscribeAllEvent<RequestShootEvent>(OnShootRequest);
        SubscribeAllEvent<RequestStopShootEvent>(OnStopShootRequest);
        SubscribeLocalEvent<GunComponent, MeleeHitEvent>(OnGunMelee);

        // Ammo providers
        InitializeBallistic();
        InitializeBattery();
        InitializeCartridge();
        InitializeChamberMagazine();
        InitializeMagazine();
        InitializeRevolver();
        InitializeBasicEntity();
        InitializeClothing();
        InitializeContainer();
        InitializeSolution();

        // Interactions
        SubscribeLocalEvent<GunComponent, GetVerbsEvent<AlternativeVerb>>(OnAltVerb);
        SubscribeLocalEvent<GunComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<GunComponent, CycleModeEvent>(OnCycleMode);
        SubscribeLocalEvent<GunComponent, EntityUnpausedEvent>(OnGunUnpaused);

#if DEBUG
        SubscribeLocalEvent<GunComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, GunComponent component, MapInitEvent args)
    {
        if (component.NextFire > Timing.CurTime)
            Logger.Warning($"Initializing a map that contains an entity that is on cooldown. Entity: {ToPrettyString(uid)}");

        DebugTools.Assert((component.AvailableModes & component.SelectedMode) != 0x0);
#endif
    }

    private void OnGunMelee(EntityUid uid, GunComponent component, MeleeHitEvent args)
    {
        if (!TryComp<MeleeWeaponComponent>(uid, out var melee))
            return;

        if (melee.NextAttack > component.NextFire)
        {
            component.NextFire = melee.NextAttack;
            Dirty(component);
        }
    }

    private void OnGunUnpaused(EntityUid uid, GunComponent component, ref EntityUnpausedEvent args)
    {
        component.NextFire += args.PausedTime;
    }

    private void OnShootRequest(RequestShootEvent msg, EntitySessionEventArgs args)
    {
        var user = args.SenderSession.AttachedEntity;

        if (user == null ||
            !TryGetGun(user.Value, out var ent, out var gun))
            return;

        if (ent != msg.Gun)
            return;

        gun.ShootCoordinates = msg.Coordinates;
        Sawmill.Debug($"Set shoot coordinates to {gun.ShootCoordinates}");
        AttemptShoot(user.Value, ent, gun);
    }

    private void OnStopShootRequest(RequestStopShootEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity == null ||
            !TryComp<GunComponent>(ev.Gun, out var gun) ||
            !TryGetGun(args.SenderSession.AttachedEntity.Value, out _, out var userGun))
        {
            return;
        }

        if (userGun != gun)
            return;

        StopShooting(ev.Gun, gun);
    }

    public bool CanShoot(GunComponent component)
    {
        if (component.NextFire > Timing.CurTime)
            return false;

        return true;
    }

    public bool TryGetGun(EntityUid entity, out EntityUid gunEntity, [NotNullWhen(true)] out GunComponent? gunComp)
    {
        gunEntity = default;
        gunComp = null;

        if (!_combatMode.IsInCombatMode(entity))
            return false;

        if (EntityManager.TryGetComponent(entity, out HandsComponent? hands) &&
            hands.ActiveHandEntity is { } held &&
            TryComp(held, out GunComponent? gun))
        {
            gunEntity = held;
            gunComp = gun;
            return true;
        }

        // Last resort is check if the entity itself is a gun.
        if (TryComp(entity, out gun))
        {
            gunEntity = entity;
            gunComp = gun;
            return true;
        }

        return false;
    }

    private void StopShooting(EntityUid uid, GunComponent gun)
    {
        if (gun.ShotCounter == 0)
            return;

        Sawmill.Debug($"Stopped shooting {ToPrettyString(uid)}");
        gun.ShotCounter = 0;
        gun.ShootCoordinates = null;
        Dirty(gun);
    }

    /// <summary>
    /// Attempts to shoot at the target coordinates. Resets the shot counter after every shot.
    /// </summary>
    public void AttemptShoot(EntityUid user, EntityUid gunUid, GunComponent gun, EntityCoordinates toCoordinates)
    {
        gun.ShootCoordinates = toCoordinates;
        AttemptShoot(user, gunUid, gun);
        gun.ShotCounter = 0;
    }

    private void AttemptShoot(EntityUid user, EntityUid gunUid, GunComponent gun)
    {
        if (gun.FireRate <= 0f)
            return;

        var toCoordinates = gun.ShootCoordinates;

        if (toCoordinates == null)
            return;

        var curTime = Timing.CurTime;

        // Maybe Raise an event for this? CanAttack doesn't seem appropriate.
        if (TryComp<MeleeWeaponComponent>(gunUid, out var melee) && melee.NextAttack > curTime)
            return;

        // Need to do this to play the clicking sound for empty automatic weapons
        // but not play anything for burst fire.
        if (gun.NextFire > curTime)
            return;

        var fireRate = TimeSpan.FromSeconds(1f / gun.FireRate);

        // First shot
        // Previously we checked shotcounter but in some cases all the bullets got dumped at once
        // curTime - fireRate is insufficient because if you time it just right you can get a 3rd shot out slightly quicker.
        if (gun.NextFire < curTime - fireRate || gun.ShotCounter == 0 && gun.NextFire < curTime)
            gun.NextFire = curTime;

        var shots = 0;
        var lastFire = gun.NextFire;

        while (gun.NextFire <= curTime)
        {
            gun.NextFire += fireRate;
            shots++;
        }

        // NextFire has been touched regardless so need to dirty the gun.
        Dirty(gun);

        // Get how many shots we're actually allowed to make, due to clip size or otherwise.
        // Don't do this in the loop so we still reset NextFire.
        switch (gun.SelectedMode)
        {
            case SelectiveFire.SemiAuto:
                shots = Math.Min(shots, 1 - gun.ShotCounter);
                break;
            case SelectiveFire.Burst:
                shots = Math.Min(shots, 3 - gun.ShotCounter);
                break;
            case SelectiveFire.FullAuto:
                break;
            default:
                throw new ArgumentOutOfRangeException($"No implemented shooting behavior for {gun.SelectedMode}!");
        }

        var attemptEv = new AttemptShootEvent(user, null);
        RaiseLocalEvent(gunUid, ref attemptEv);

        if (attemptEv.Cancelled)
        {
            if (attemptEv.Message != null)
            {
                PopupSystem.PopupClient(attemptEv.Message, gunUid, user);
            }

            gun.NextFire = TimeSpan.FromSeconds(Math.Max(lastFire.TotalSeconds + SafetyNextFire, gun.NextFire.TotalSeconds));
            return;
        }

        var fromCoordinates = Transform(user).Coordinates;
        // Remove ammo
        var ev = new TakeAmmoEvent(shots, new List<(EntityUid? Entity, IShootable Shootable)>(), fromCoordinates, user);

        // Listen it just makes the other code around it easier if shots == 0 to do this.
        if (shots > 0)
            RaiseLocalEvent(gunUid, ev);

        DebugTools.Assert(ev.Ammo.Count <= shots);
        DebugTools.Assert(shots >= 0);
        UpdateAmmoCount(gunUid);

        // Even if we don't actually shoot update the ShotCounter. This is to avoid spamming empty sounds
        // where the gun may be SemiAuto or Burst.
        gun.ShotCounter += shots;

        if (ev.Ammo.Count <= 0)
        {
            // Play empty gun sounds if relevant
            // If they're firing an existing clip then don't play anything.
            if (shots > 0)
            {
                // Don't spam safety sounds at gun fire rate, play it at a reduced rate.
                // May cause prediction issues? Needs more tweaking
                gun.NextFire = TimeSpan.FromSeconds(Math.Max(lastFire.TotalSeconds + SafetyNextFire, gun.NextFire.TotalSeconds));
                Audio.PlayPredicted(gun.SoundEmpty, gunUid, user);
                return;
            }

            return;
        }

        // Shoot confirmed - sounds also played here in case it's invalid (e.g. cartridge already spent).
        Shoot(gunUid, gun, ev.Ammo, fromCoordinates, toCoordinates.Value, user, throwItems: attemptEv.ThrowItems);
        var shotEv = new GunShotEvent(user);
        RaiseLocalEvent(gunUid, ref shotEv);
        // Projectiles cause impulses especially important in non gravity environments
        if (TryComp<PhysicsComponent>(user, out var userPhysics))
        {
            if (_gravity.IsWeightless(user, userPhysics))
                CauseImpulse(fromCoordinates, toCoordinates.Value, user, userPhysics);
        }
        Dirty(gun);
    }

    public void Shoot(
        EntityUid gunUid,
        GunComponent gun,
        EntityUid ammo,
        EntityCoordinates fromCoordinates,
        EntityCoordinates toCoordinates,
        EntityUid? user = null,
        bool throwItems = false)
    {
        var shootable = EnsureComp<AmmoComponent>(ammo);
        Shoot(gunUid, gun, new List<(EntityUid? Entity, IShootable Shootable)>(1) { (ammo, shootable) }, fromCoordinates, toCoordinates, user, throwItems);
    }

    public abstract void Shoot(
        EntityUid gunUid,
        GunComponent gun,
        List<(EntityUid? Entity, IShootable Shootable)> ammo,
        EntityCoordinates fromCoordinates,
        EntityCoordinates toCoordinates,
        EntityUid? user = null,
        bool throwItems = false);

    protected abstract void Popup(string message, EntityUid? uid, EntityUid? user);

    /// <summary>
    /// Call this whenever the ammo count for a gun changes.
    /// </summary>
    protected virtual void UpdateAmmoCount(EntityUid uid) {}

    protected void SetCartridgeSpent(EntityUid uid, CartridgeAmmoComponent cartridge, bool spent)
    {
        if (cartridge.Spent != spent)
            Dirty(cartridge);

        cartridge.Spent = spent;
        Appearance.SetData(uid, AmmoVisuals.Spent, spent);
    }

    /// <summary>
    /// Drops a single cartridge / shell
    /// </summary>
    protected void EjectCartridge(
        EntityUid entity,
        bool playSound = true)
    {
        // TODO: Sound limit version.
        var offsetPos = (Random.NextVector2(EjectOffset));
        var xform = Transform(entity);

        var coordinates = xform.Coordinates;
        coordinates = coordinates.Offset(offsetPos);

        xform.LocalRotation = Random.NextAngle();
        xform.Coordinates = coordinates;

        if (playSound && TryComp<CartridgeAmmoComponent>(entity, out var cartridge))
        {
            Audio.PlayPvs(cartridge.EjectSound, entity, AudioParams.Default.WithVariation(0.05f).WithVolume(-1f));
        }
    }

    protected void MuzzleFlash(EntityUid gun, AmmoComponent component, EntityUid? user = null)
    {
        var sprite = component.MuzzleFlash;

        if (sprite == null)
            return;

        var ev = new MuzzleFlashEvent(gun, sprite, user == gun);
        CreateEffect(gun, ev, user);
    }

    public void CauseImpulse(EntityCoordinates fromCoordinates, EntityCoordinates toCoordinates, EntityUid user, PhysicsComponent userPhysics)
    {
        var fromMap = fromCoordinates.ToMapPos(EntityManager, TransformSystem);
        var toMap = toCoordinates.ToMapPos(EntityManager, TransformSystem);
        var shotDirection = (toMap - fromMap).Normalized;

        const float impulseStrength = 85.0f; //The bullet impulse strength, TODO: In the future we might want to make it more projectile dependent
        var impulseVector =  shotDirection * impulseStrength;
        Physics.ApplyLinearImpulse(user, -impulseVector, body: userPhysics);
    }
    protected abstract void CreateEffect(EntityUid uid, MuzzleFlashEvent message, EntityUid? user = null);

    /// <summary>
    /// Used for animated effects on the client.
    /// </summary>
    [Serializable, NetSerializable]
    public sealed class HitscanEvent : EntityEventArgs
    {
        public List<(EntityCoordinates coordinates, Angle angle, SpriteSpecifier Sprite, float Distance)> Sprites = new();
    }
}

/// <summary>
///     Raised directed on the gun before firing to see if the shot should go through.
/// </summary>
/// <remarks>
///     Handling this in server exclusively will lead to mispredicts.
/// </remarks>
/// <param name="User">The user that attempted to fire this gun.</param>
/// <param name="Cancelled">Set this to true if the shot should be cancelled.</param>
/// <param name="ThrowItems">Set this to true if the ammo shouldn't actually be fired, just thrown.</param>
[ByRefEvent]
public record struct AttemptShootEvent(EntityUid User, string? Message, bool Cancelled = false, bool ThrowItems = false);

/// <summary>
///     Raised directed on the gun after firing.
/// </summary>
/// <param name="User">The user that fired this gun.</param>
[ByRefEvent]
public record struct GunShotEvent(EntityUid User);

public enum EffectLayers : byte
{
    Unshaded,
}

[Serializable, NetSerializable]
public enum AmmoVisuals : byte
{
    Spent,
    AmmoCount,
    AmmoMax,
    HasAmmo, // used for generic visualizers. c# stuff can just check ammocount != 0
    MagLoaded,
}
