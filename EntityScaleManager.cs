using System;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Entity Scale Manager", "WhiteThunder", "3.0.0")]
[Description("Utilities for resizing entities.")]
internal class EntityScaleManager : CovalencePlugin
{
    #region Fields

    private StoredData _data;

    private const string PermissionScaleUnrestricted = "entityscalemanager.unrestricted";

    #endregion

    #region Hooks

    private void Init()
    {
        _data = StoredData.Load();
        permission.RegisterPermission(PermissionScaleUnrestricted, this);
    }

    private void Unload()
    {
        _data.SaveIfDirty();
    }

    private void OnServerInitialized()
    {
        _data.MigrateLegacySpheres();

        foreach (var (entityId, scale) in _data.EntityScale)
        {
            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as BaseEntity;
            if (entity == null)
                continue;

            SetEntityScale(entity, scale);
        }
    }

    private void OnServerSave()
    {
        _data.SaveIfDirty();
    }

    private void OnNewSave()
    {
        _data = StoredData.Clear();
    }

    private void OnEntityKill(BaseEntity entity)
    {
        if (entity == null || entity.net == null)
            return;

        _data.ForgetScale(entity);
    }

    #endregion

    #region API

    [HookMethod(nameof(API_GetScale))]
    public Vector3 API_GetScale(BaseEntity entity)
    {
        if (entity == null || entity.net == null)
            return Vector3.one;

        return _data.TryGetScale(entity, out var scale) ? scale : Vector3.one;
    }

    [HookMethod(nameof(API_ScaleEntity))]
    public bool API_ScaleEntity(BaseEntity entity, Vector3 scale)
    {
        if (entity == null || entity.net == null)
            return false;

        return TryScaleEntity(entity, scale);
    }

    [HookMethod(nameof(API_ScaleEntity))]
    public bool API_ScaleEntity(BaseEntity entity, float scale)
    {
        if (entity == null || entity.net == null)
            return false;

        return TryScaleEntity(entity, Vector3.one * scale);
    }

    [HookMethod(nameof(API_ScaleEntity))]
    public bool API_ScaleEntity(BaseEntity entity, int scale)
    {
        return API_ScaleEntity(entity, Vector3.one * scale);
    }

    #endregion

    #region Commands

    private static bool TryParseScaleArgs(string[] args, out Vector3 scale)
    {
        scale = Vector3.one;

        if (args.Length == 0 || !float.TryParse(args[0], out var uniformScale))
            return false;

        if (args.Length == 1)
        {
            scale = Vector3.one * uniformScale;
            return true;
        }

        if (args.Length < 3
            || !float.TryParse(args[0], out var x)
            || !float.TryParse(args[1], out var y)
            || !float.TryParse(args[2], out var z))
            return false;

        scale = new Vector3(x, y, z);
        return true;
    }

    [Command("scale")]
    private void CommandScale(IPlayer player, string cmd, string[] args)
    {
        if (player.IsServer)
            return;

        if (!player.HasPermission(PermissionScaleUnrestricted))
        {
            ReplyToPlayer(player, "Error.NoPermission");
            return;
        }

        if (!TryParseScaleArgs(args, out var scale))
        {
            ReplyToPlayer(player, "Error.SyntaxV3", cmd);
            return;
        }

        var basePlayer = player.Object as BasePlayer;
        var entity = GetLookEntity(basePlayer);
        if (entity == null)
        {
            ReplyToPlayer(player, "Error.NoEntityFound");
            return;
        }

        if (entity is BasePlayer)
        {
            ReplyToPlayer(player, "Error.EntityNotSafeToScale");
            return;
        }

        ReplyToPlayer(player, TryScaleEntity(entity, scale) ? "Scale.Success" : "Error.ScaleBlocked", FormatScale(scale));
    }

    [Command("getscale")]
    private void CommandGetScale(IPlayer player)
    {
        if (player.IsServer)
            return;

        if (!player.HasPermission(PermissionScaleUnrestricted))
        {
            ReplyToPlayer(player, "Error.NoPermission");
            return;
        }

        var basePlayer = player.Object as BasePlayer;
        var entity = GetLookEntity(basePlayer);
        if (entity == null)
        {
            ReplyToPlayer(player, "Error.NoEntityFound");
            return;
        }

        if (!_data.TryGetScale(entity, out var scale))
        {
            ReplyToPlayer(player, "Error.NotTracked");
            return;
        }

        ReplyToPlayer(player, "GetScale.Success", FormatScale(scale));
    }

    #endregion

    #region Helper Methods

    private static bool EntityScaleWasBlocked(BaseEntity entity, Vector3 scale)
    {
        return Interface.CallHook("OnEntityScale", entity, scale) is false;
    }

    private static string FormatScale(Vector3 scale)
    {
        if (Mathf.Approximately(scale.x, scale.y) && Mathf.Approximately(scale.x, scale.z))
            return $"{scale.x:0.###}";

        return $"{scale.x:0.###} {scale.y:0.###} {scale.z:0.###}";
    }

    private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 20)
    {
        return Physics.Raycast(basePlayer.eyes.HeadRay(), out var hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.GetEntity()
            : null;
    }

    private static SphereEntity GetParentSphere(BaseEntity entity)
    {
        return entity.GetParentEntity() as SphereEntity;
    }

    private static bool TryRemoveSphereEntity(BaseEntity scaledEntity, out Vector3 scale)
    {
        scale = Vector3.one;

        var sphereEntity = GetParentSphere(scaledEntity);
        if (sphereEntity == null)
            return false;

        scale = Vector3.one * sphereEntity.currentRadius;

        // If the sphere already has a parent, simply move the entity to it.
        // Parent is possibly null but that's ok since that will simply unparent.
        scaledEntity.SetParent(sphereEntity.GetParentEntity(), worldPositionStays: true, sendImmediate: true);

        sphereEntity.Kill();
        return true;
    }

    private static bool IsScaledTo(BaseEntity entity, Vector3 scale)
    {
        if (scale == Vector3.one)
            return !entity.networkEntityScale;

        return entity.networkEntityScale && entity.transform.localScale == scale;
    }

    private static bool SetEntityScale(BaseEntity entity, Vector3 scale)
    {
        if (IsScaledTo(entity, scale))
            return false;

        entity.transform.localScale = scale;

        if (scale == Vector3.one)
        {
            entity.networkEntityScale = true;
            entity.SendNetworkUpdateImmediate();
            entity.networkEntityScale = false;
        }
        else
        {
            entity.networkEntityScale = true;
            entity.SendNetworkUpdate();
        }

        return true;
    }

    private bool TryScaleEntity(BaseEntity entity, Vector3 scale)
    {
        if (EntityScaleWasBlocked(entity, scale))
            return false;

        if (!SetEntityScale(entity, scale))
            return true;

        if (scale == Vector3.one)
        {
            _data.ForgetScale(entity);
        }
        else
        {
            _data.RememberScale(entity, scale);
        }

        Interface.CallHook("OnEntityScaled", entity, scale);
        return true;
    }

    #endregion

    #region Data

    private class StoredData
    {
        [JsonProperty("ScaledEntities", DefaultValueHandling = DefaultValueHandling.Ignore)]
        private HashSet<ulong> _deprecatedScaledEntities;

        [JsonProperty("EntityScale")]
        public Dictionary<ulong, Vector3> EntityScale = new();

        [JsonIgnore]
        private bool _isDirty;

        public static StoredData Load()
        {
            return Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(EntityScaleManager)) ?? new StoredData();
        }

        public static StoredData Clear()
        {
            return new StoredData().Save();
        }

        public StoredData Save()
        {
            Interface.Oxide.DataFileSystem.WriteObject(nameof(EntityScaleManager), this);
            _isDirty = false;
            return this;
        }

        public void SaveIfDirty()
        {
            if (_isDirty)
            {
                Save();
            }
        }

        public bool TryGetScale(BaseEntity entity, out Vector3 scale)
        {
            scale = Vector3.one;
            return entity?.net != null
                && EntityScale.TryGetValue(entity.net.ID.Value, out scale);
        }

        public void RememberScale(BaseEntity entity, Vector3 scale)
        {
            if (entity?.net == null)
                return;

            EntityScale[entity.net.ID.Value] = scale;
            _isDirty = true;
        }

        public void ForgetScale(BaseEntity entity)
        {
            if (entity?.net == null)
                return;

            if (!EntityScale.Remove(entity.net.ID.Value))
                return;

            _isDirty = true;
        }

        public void MigrateLegacySpheres()
        {
            if (_deprecatedScaledEntities == null)
                return;

            var migrated = 0;

            foreach (var entityId in _deprecatedScaledEntities)
            {
                var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as BaseEntity;
                if (entity == null)
                    continue;

                if (!TryRemoveSphereEntity(entity, out var scale))
                    continue;

                Interface.Oxide.LogWarning($"Removed sphere entity from scaled entity of type {entity.GetType()} ({scale}).");
                RememberScale(entity, scale);
                migrated++;
            }

            if (migrated > 0)
            {
                Interface.Oxide.LogWarning($"Migrated {_deprecatedScaledEntities.Count} scaled entities from spheres to native scaling.");
            }

            _deprecatedScaledEntities = null;
            Save();
        }
    }

    #endregion

    #region Localization

    private string GetMessage(string playerId, string messageName, params object[] args)
    {
        var message = lang.GetMessage(messageName, this, playerId);
        return args.Length > 0 ? string.Format(message, args) : message;
    }

    private string GetMessage(IPlayer player, string messageName, params object[] args) =>
        GetMessage(player.Id, messageName, args);

    private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
        player.Reply(GetMessage(player, messageName, args));

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["Error.NoPermission"] = "You don't have permission to do that.",
            ["Error.SyntaxV3"] = "Syntax: {0} <x> <y> <z>",
            ["Error.NoEntityFound"] = "Error: No entity found.",
            ["Error.EntityNotSafeToScale"] = "Error: That entity cannot be safely scaled.",
            ["Error.NotTracked"] = "Error: That entity is not tracked by Entity Scale Manager.",
            ["Error.ScaleBlocked"] = "Error: Another plugin prevented you from scaling that entity to size {0}.",
            ["GetScale.Success"] = "Entity scale is: {0}",
            ["Scale.Success"] = "Entity was scaled to: {0}",
        }, this, "en");
    }

    #endregion
}