﻿using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using UdpKit;
using Bolt;
using Common;
using Core.AuraEffects;

namespace Core
{
    public abstract partial class Unit : WorldEntity
    {
        public new class CreateToken : WorldEntity.CreateToken
        {
            public DeathState DeathState { private get; set; }
            public bool FreeForAll { private get; set; }
            public int FactionId { private get; set; }
            public int ModelId { private get; set; }
            public int OriginalModelId { private get; set; }
            public float Scale { private get; set; } = 1.0f;

            public override void Read(UdpPacket packet)
            {
                base.Read(packet);

                DeathState = (DeathState)packet.ReadInt();
                FactionId = packet.ReadInt();
                ModelId = packet.ReadInt();
                OriginalModelId = packet.ReadInt();
                FreeForAll = packet.ReadBool();
                Scale = packet.ReadFloat();
            }

            public override void Write(UdpPacket packet)
            {
                base.Write(packet);

                packet.WriteInt((int)DeathState);
                packet.WriteInt(FactionId);
                packet.WriteInt(ModelId);
                packet.WriteInt(OriginalModelId);
                packet.WriteBool(FreeForAll);
                packet.WriteFloat(Scale);
            }

            protected void Attached(Unit unit)
            {
                unit.DeathState = DeathState;
                unit.Faction = unit.Balance.FactionsById[FactionId];
                unit.FreeForAll = FreeForAll;
                unit.ModelId = ModelId;
                unit.OriginalModelId = OriginalModelId;
                unit.Scale = Scale;
            }
        }

        [SerializeField, UsedImplicitly, Header(nameof(Unit)), Space(10)]
        private CapsuleCollider unitCollider;
        [SerializeField, UsedImplicitly]
        private WarcraftCharacterController characterController;
        [SerializeField, UsedImplicitly]
        private UnitAttributeDefinition unitAttributeDefinition;
        [SerializeField, UsedImplicitly]
        private UnitMovementDefinition unitMovementDefinition;
        [SerializeField, UsedImplicitly]
        private List<UnitBehaviour> unitBehaviours;

        private SingleReference<Unit> selfReference;
        private CreateToken createToken;
        private UnitControlState controlState;
        private IUnitState entityState;
        private UnitFlags unitFlags;

        private readonly BehaviourController behaviourController = new BehaviourController();

        internal AuraVisibleController VisibleAuras { get; } = new AuraVisibleController();
        internal AuraApplicationController Auras { get; } = new AuraApplicationController();
        internal AttributeController Attributes { get; } = new AttributeController();
        internal ThreatController Threat { get; } = new ThreatController();
        internal SpellController Spells { get; } = new SpellController();
        internal WarcraftCharacterController CharacterController => characterController;

        internal UnitAttributeDefinition AttributeDefinition => unitAttributeDefinition;
        internal SpellInfo TransformSpellInfo { get; private set; }
        internal bool FreeForAll { get => Attributes.FreeForAll; set => Attributes.FreeForAll = value; }
        internal int ModelId { get => Attributes.ModelId; set => Attributes.ModelId = value; }
        internal int OriginalModelId { get => Attributes.OriginalModelId; set => Attributes.OriginalModelId = value; }
        internal FactionDefinition Faction { get => Attributes.Faction; set => Attributes.Faction = value; }
        internal DeathState DeathState { get => Attributes.DeathState; set => Attributes.DeathState = value; }
        internal IReadOnlyDictionary<UnitMoveType, float> SpeedRates => Attributes.SpeedRates;
        internal IReadOnlyList<AuraApplication> AuraApplications => Auras.AuraApplications;

        public IReadOnlyReference<Unit> SelfReference => selfReference;
        public Unit Target => Attributes.Target;
        public SpellCast SpellCast => Spells.Cast;
        public SpellHistory SpellHistory => Spells.SpellHistory;
        public CapsuleCollider UnitCollider => unitCollider;
        public PlayerControllerDefinition ControllerDefinition => characterController.ControllerDefinition;

        public MovementInfo MovementInfo { get; private set; }

        public int Level => Attributes.Level.Value;
        public int Model => Attributes.ModelId;
        public int Health => Attributes.Health.Value;
        public int MaxHealth => Attributes.MaxHealth.Value;
        public int BaseMana => Attributes.Mana.Base;
        public int Mana => Attributes.Mana.Value;
        public int MaxMana => Attributes.MaxMana.Value;
        public int SpellPower => Attributes.SpellPower.Value;
        public int VisibleAuraMaxCount => entityState.VisibleAuras.Length;
        public float ModHaste => Attributes.ModHaste.Value;
        public float ModRegenHaste => Attributes.ModRegenHaste.Value;
        public float CritPercentage => Attributes.CritPercentage.Value;
        public float HealthRatio => MaxHealth > 0 ? (float)Health / MaxHealth : 0.0f;
        public bool IsMovementBlocked => HasState(UnitControlState.Root) || HasState(UnitControlState.Stunned);
        public bool IsAlive => DeathState == DeathState.Alive;
        public bool IsDead => DeathState == DeathState.Dead;
        public bool IsControlledByPlayer => this is Player;
        public bool IsStopped => !HasState(UnitControlState.Moving);
        public float Scale { get => Attributes.Scale; internal set => Attributes.Scale = value; }

        public bool HealthBelowPercent(int percent) => Health < MaxHealth.CalculatePercentage(percent);
        public bool HealthAbovePercent(int percent) => Health > MaxHealth.CalculatePercentage(percent);
        public bool HealthAbovePercentHealed(int percent, int healAmount) => Health + healAmount > MaxHealth.CalculatePercentage(percent);
        public bool HealthBelowPercentDamaged(int percent, int damageAmount) => Health - damageAmount < MaxHealth.CalculatePercentage(percent);
        public float GetSpeed(UnitMoveType type) => SpeedRates[type] * unitMovementDefinition.BaseSpeedByType(type);
        public float GetPowerPercent(SpellResourceType type) => GetMaxPower(type) > 0 ? 100.0f * GetPower(type) / GetMaxPower(type) : 0.0f;
        public int GetPower(SpellResourceType type) => Mana;
        public int GetMaxPower(SpellResourceType type) => MaxMana;
        public VisibleAuraState GetVisibleAura(int index) => entityState.VisibleAuras[index];

        public sealed override void Attached()
        {
            base.Attached();

            HandleAttach();
            
            World.UnitManager.Attach(this);
        }

        public sealed override void Detached()
        {
            // called twice on client (from Detached Photon callback and manual in UnitManager.Dispose)
            // if he needs to instantly destroy current world and avoid any events
            if (IsValid)
            {
                World.UnitManager.Detach(this);

                HandleDetach();

                base.Detached();
            }
        }

        public sealed override void ControlGained()
        {
            base.ControlGained();

            HandleControlGained();
        }

        public sealed override void ControlLost()
        {
            base.ControlLost();

            HandleControlLost();
        }

        protected virtual void HandleAttach()
        {
            selfReference = new SingleReference<Unit>(this);
            createToken = (CreateToken)entity.AttachToken;
            entityState = entity.GetState<IUnitState>();

            MovementInfo = CreateMovementInfo(entityState);
            behaviourController.HandleUnitAttach(this);

            SetMap(World.FindMap(1));
        }

        protected virtual void HandleDetach()
        {
            ResetMap();

            behaviourController.HandleUnitDetach();
            MovementInfo.Dispose();

            selfReference.Invalidate();
            selfReference = null;

            TransformSpellInfo = null;
            controlState = 0;
            unitFlags = 0;
        }

        protected virtual void HandleControlGained()
        {
            UpdateSyncTransform(IsOwner);
            CharacterController.UpdateOwnership();
        }

        protected virtual void HandleControlLost()
        {
            UpdateSyncTransform(true);
            CharacterController.UpdateOwnership();
        }

        protected virtual MovementInfo CreateMovementInfo(IUnitState unitState) => new MovementInfo(this, unitState);

        internal override void DoUpdate(int deltaTime)
        {
            base.DoUpdate(deltaTime);

            behaviourController.DoUpdate(deltaTime);
        }

        public bool IsHostileTo(Unit unit)
        {
            if (unit == this)
                return false;

            if (unit.FreeForAll && FreeForAll)
                return true;

            return Faction.HostileFactions.Contains(unit.Faction);
        }

        public bool IsFriendlyTo(Unit unit)
        {
            if (unit == this)
                return true;

            if (unit.FreeForAll && FreeForAll)
                return false;

            return Faction.FriendlyFactions.Contains(unit.Faction);
        }

        public void AddCallback(string path, PropertyCallback propertyCallback) => entityState.AddCallback(path, propertyCallback);

        public void AddCallback(string path, PropertyCallbackSimple propertyCallback) => entityState.AddCallback(path, propertyCallback);

        public void RemoveCallback(string path, PropertyCallback propertyCallback) => entityState.RemoveCallback(path, propertyCallback);

        public void RemoveCallback(string path, PropertyCallbackSimple propertyCallback) => entityState.RemoveCallback(path, propertyCallback);

        public T FindBehaviour<T>() where T : UnitBehaviour => behaviourController.FindBehaviour<T>();

        internal bool HasAuraType(AuraEffectType auraEffectType) => Auras.HasAuraType(auraEffectType);

        internal bool HasAuraState(AuraStateType auraStateType) => Auras.HasAuraState(auraStateType);

        internal IReadOnlyList<AuraEffect> GetAuraEffects(AuraEffectType auraEffectType) => Auras.GetAuraEffects(auraEffectType);

        internal float TotalAuraModifier(AuraEffectType auraType) => Auras.TotalAuraModifier(auraType);

        internal float TotalAuraMultiplier(AuraEffectType auraType) => Auras.TotalAuraMultiplier(auraType);

        internal float MaxPositiveAuraModifier(AuraEffectType auraType) => Auras.MaxPositiveAuraModifier(auraType);

        internal float MaxNegativeAuraModifier(AuraEffectType auraType) => Auras.MaxNegativeAuraModifier(auraType);

        internal bool IsImmunedToDamage(SpellInfo spellInfo) => Spells.IsImmunedToDamage(spellInfo);

        internal bool IsImmunedToDamage(AuraInfo auraInfo) => Spells.IsImmunedToDamage(auraInfo);

        internal bool IsImmuneToSpell(SpellInfo spellInfo, Unit caster) => Spells.IsImmuneToSpell(spellInfo, caster);

        internal bool IsImmuneToAura(AuraInfo auraInfo, Unit caster) => Spells.IsImmuneToAura(auraInfo, caster);

        internal bool IsImmuneToAuraEffect(AuraEffectInfo auraEffectInfo, Unit caster) => Spells.IsImmuneToAuraEffect(auraEffectInfo, caster);

        internal void AddState(UnitControlState state) { controlState |= state; }

        internal bool HasState(UnitControlState state) { return (controlState & state) != 0; }

        internal void RemoveState(UnitControlState state) { controlState &= ~state; }

        internal void UpdateControlState(UnitControlState state, bool applied)
        {
            if (applied && HasState(state))
                return;

            if (!applied && !HasState(state))
                return;

            if (applied)
            {
                switch (state)
                {
                    case UnitControlState.Stunned:
                        UpdateStunState(true);
                        break;
                    case UnitControlState.Root:
                        if(!HasState(UnitControlState.Stunned))
                            UpdateRootState(true);
                        break;
                    case UnitControlState.Confused:
                        if (!HasState(UnitControlState.Stunned))
                        {
                            SpellCast.Cancel();
                            UpdateConfusionState(true);
                        }
                        break;
                }

                AddState(state);
            }
            else
            {
                switch (state)
                {
                    case UnitControlState.Stunned:
                        if (!HasAuraType(AuraEffectType.StunState))
                        {
                            UpdateStunState(false);
                            RemoveState(state);
                        }
                        break;
                    case UnitControlState.Root:
                        if (!HasAuraType(AuraEffectType.RootState) && !HasState(UnitControlState.Stunned))
                        {
                            UpdateRootState(false);
                            RemoveState(state);
                        }
                        break;
                    case UnitControlState.Confused:
                        if (!HasAuraType(AuraEffectType.ConfusionState))
                        {
                            UpdateConfusionState(false);
                            RemoveState(state);
                        }
                        break;
                    default:
                        RemoveState(state);
                        break;
                }
            }

            if (HasAuraType(AuraEffectType.StunState))
            {
                if (!HasState(UnitControlState.Stunned))
                    UpdateStunState(true);
            }
            else
            {
                if (!HasState(UnitControlState.Root) && HasAuraType(AuraEffectType.RootState))
                    UpdateRootState(true);

                if (!HasState(UnitControlState.Confused) && HasAuraType(AuraEffectType.ConfusionState))
                    UpdateConfusionState(true);
            }
        }

        internal void UpdateTransformSpell(AuraEffectChangeDisplayModel changeDisplayEffect)
        {
            TransformSpellInfo = changeDisplayEffect.Aura.SpellInfo;
            ModelId = changeDisplayEffect.EffectInfo.ModelId;
        }

        internal void ResetTransformSpell()
        {
            TransformSpellInfo = null;
            ModelId = OriginalModelId;
        }

        internal void SetFlag(UnitFlags flag) => unitFlags |= flag;

        internal void RemoveFlag(UnitFlags flag) => unitFlags &= ~flag;

        internal bool HasFlag(UnitFlags flag) => (unitFlags & flag) == flag;

        internal void ModifyDeathState(DeathState newState)
        {
            DeathState = newState;

            if (IsDead && SpellCast.IsCasting)
                SpellCast.Cancel();

            if (newState == DeathState.Dead)
                Auras.RemoveNonDeathPersistentAuras();
        }

        internal int ModifyHealth(int delta)
        {
            return Attributes.SetHealth(Health + delta);
        }

        internal int DealDamage(Unit target, int damageAmount)
        {
            if (damageAmount < 1)
                return 0;

            int healthValue = target.Health;
            if (healthValue <= damageAmount)
            {
                Kill(target);
                return healthValue;
            }

            return target.ModifyHealth(-damageAmount);
        }

        internal int DealHeal(Unit target, int healAmount)
        {
            if (healAmount < 1)
                return 0;

            return target.ModifyHealth(healAmount);
        }

        internal void Kill(Unit victim)
        {
            if (victim.Health <= 0)
                return;

            victim.Attributes.SetHealth(0);
            victim.ModifyDeathState(DeathState.Dead);
        }

        protected void StopMoving()
        {
            MovementInfo.RemoveMovementFlag(MovementFlags.MaskMoving);

            CharacterController.StopMoving();
        }

        private void UpdateStunState(bool applied)
        {
            if (applied)
            {
                SpellCast.Cancel();
                StopMoving();

                SetFlag(UnitFlags.Stunned);

                UpdateRootState(true);
            }
            else
            {
                RemoveFlag(UnitFlags.Stunned);

                if (!HasState(UnitControlState.Root))
                    UpdateRootState(false);
            }
        }

        private void UpdateRootState(bool applied)
        {
            if (applied)
            {
                StopMoving();

                MovementInfo.AddMovementFlag(MovementFlags.Root);
            }
            else
                MovementInfo.RemoveMovementFlag(MovementFlags.Root);

            if (IsOwner && this is Player rootedPlayer)
                EventHandler.ExecuteEvent(EventHandler.GlobalDispatcher, GameEvents.ServerPlayerRootChanged, rootedPlayer, applied);
        }

        private void UpdateConfusionState(bool applied)
        {
            CharacterController.UpdateMovementControl(!applied);
        }
    }
}
