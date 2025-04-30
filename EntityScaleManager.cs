using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Entity Scale Manager", "WhiteThunder", "2.1.6")]
[Description("Utilities for resizing entities.")]
internal class EntityScaleManager : CovalencePlugin
{
    #region Fields

    [PluginReference]
    private readonly Plugin ParentedEntityRenderFix;

    private Configuration _config;
    private StoredData _data;

    private const string PermissionScaleUnrestricted = "entityscalemanager.unrestricted";
    private const string SpherePrefab = "assets/prefabs/visualization/sphere.prefab";

    private readonly object True = true;

    // This could be improved by calculating the time needed for the resize,
    // since the amount of time required seems to depend on the scale.
    private const float ExpectedResizeDuration = 7f;

    private EntitySubscriptionManager _entitySubscriptionManager = new();
    private NetworkSnapshotManager _networkSnapshotManager = new();

    #endregion

    #region Hooks

    private void Init()
    {
        _data = StoredData.Load();

        permission.RegisterPermission(PermissionScaleUnrestricted, this);

        if (!_config.HideSpheresAfterResize)
        {
            Unsubscribe(nameof(OnEntitySnapshot));
            Unsubscribe(nameof(OnPlayerDisconnected));
            Unsubscribe(nameof(OnNetworkGroupLeft));
            Unsubscribe(nameof(CanStartTelekinesis));
        }
    }

    private void Unload()
    {
        _data.Save();

        _entitySubscriptionManager.Clear();
        _networkSnapshotManager.Clear();

        _data = null;
        _config = null;
    }

    private void OnServerInitialized()
    {
        foreach (var networkable in BaseNetworkable.serverEntities)
        {
            var entity = networkable as BaseEntity;
            if (entity == null)
                continue;

            var parentSphere = GetParentSphere(entity);
            if (parentSphere != null && _data.ScaledEntities.Contains(entity.net.ID.Value))
            {
                RefreshScaledEntity(entity, parentSphere);
            }
        }
    }

    private void OnServerSave()
    {
        _data.Save();
    }

    private void OnNewSave()
    {
        _data = StoredData.Clear();
    }

    private void OnEntityKill(BaseEntity entity)
    {
        if (entity == null || entity.net == null)
            return;

        if (!_data.ScaledEntities.Remove(entity.net.ID.Value))
            return;

        _entitySubscriptionManager.RemoveEntity(entity.net.ID);

        var parentSphere = GetParentSphere(entity);
        if (parentSphere == null)
            return;

        if (_config.HideSpheresAfterResize)
        {
            _networkSnapshotManager.InvalidateForEntity(entity.net.ID);
        }

        var parentSphereForClosure = parentSphere;

        // Destroy the sphere that was used to scale the entity.
        // This assumes that only one entity was scaled using this sphere.
        // We could instead check if the sphere still has children, but keeping the sphere
        // might cause it to never be killed after the remaining children are killed.
        // Plugins should generally parent other entities using a separate sphere, or
        // parent to the scaled entity itself.
        parentSphereForClosure.Invoke(() =>
        {
            if (parentSphereForClosure != null && !parentSphereForClosure.IsDestroyed)
            {
                parentSphereForClosure.Kill();
            }
        }, 0);
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
        _entitySubscriptionManager.RemoveSubscriber(player.userID);
    }

    private object OnEntitySnapshot(BaseEntity entity, Connection connection)
    {
        if (entity == null || entity.net == null)
            return null;

        if (!_data.ScaledEntities.Contains(entity.net.ID.Value))
            return null;

        var parentSphere = GetParentSphere(entity);
        if (parentSphere == null)
            return null;

        // Detect when the vanilla network cache has been cleared in order to invalidate the custom cache.
        if (entity._NetworkCache == null)
        {
            _networkSnapshotManager.InvalidateForEntity(entity.net.ID);
        }

        var resizeState = _entitySubscriptionManager.GetResizeState(entity.net.ID, connection.ownerid);
        if (resizeState == ResizeState.Resized)
        {
            // Don't track CPU time of this since it's almost identical to the vanilla behavior being cancelled.
            // Tracking it would give server operators false information that this plugin is spending more CPU time than it is.
            TrackEnd();
            _networkSnapshotManager.SendModifiedSnapshot(entity, connection);
            TrackStart();
            return True;
        }

        if (resizeState == ResizeState.NeedsResize)
        {
            // The entity is now starting to resize for this client.
            // Start a timer to cause this client to start using the custom snapshots after resize.
            timer.Once(ExpectedResizeDuration, () =>
            {
                if (entity != null
                    && parentSphere != null
                    && _entitySubscriptionManager.DoneResizing(entity.net.ID, connection.ownerid))
                {
                    // Send a snapshot to the client indicating that the entity is not parented to the sphere.
                    _networkSnapshotManager.SendModifiedSnapshot(entity, connection);

                    // Terminate the sphere on the client.
                    // Subsequent snapshots to this client will use different logic.
                    NetworkUtils.TerminateOnClient(parentSphere, connection);
                }
            });
        }

        // Send normal snapshot which indicates the entity has a sphere parent.
        return null;
    }

    // Clients destroy entities from a network group when they leave it.
    // This helps determine later on whether the client is creating the entity or simply receiving an update.
    private void OnNetworkGroupLeft(BasePlayer player, Network.Visibility.Group group)
    {
        for (var i = 0; i < group.networkables.Count; i++)
        {
            var networkable = group.networkables.Values.Buffer[i];
            if (networkable == null)
                continue;

            var entity = networkable.handler as BaseNetworkable;
            if (entity == null || entity.net == null)
                continue;

            _entitySubscriptionManager.RemoveEntitySubscription(entity.net.ID, player.userID);
        }
    }

    // This hook is exposed by plugin: Telekinesis.
    private Tuple<BaseEntity, BaseEntity> OnTelekinesisStart(BasePlayer player, BaseEntity entity)
    {
        if (!_data.ScaledEntities.Contains(entity.net.ID.Value))
            return null;

        var parentSphere = GetParentSphere(entity);
        if (parentSphere == null)
            return null;

        // Move the sphere, but rotate the child.
        // This is done because spheres have default rotation to avoid client-side interpolation issues.
        return new Tuple<BaseEntity, BaseEntity>(parentSphere, entity);
    }

    // This hook is exposed by plugin: Telekinesis.
    private string CanStartTelekinesis(BasePlayer player, SphereEntity moveEntity, BaseEntity rotateEntity)
    {
        if (!_data.ScaledEntities.Contains(rotateEntity.net.ID.Value))
            return null;

        return GetMessage(player, "Error.CannotMoveWithHiddenSpheres");
    }

    #endregion

    #region API

    [HookMethod(nameof(API_GetScale))]
    public float API_GetScale(BaseEntity entity)
    {
        if (entity == null || entity.net == null)
            return 1;

        if (!_data.ScaledEntities.Contains(entity.net.ID.Value))
            return 1;

        var parentSphere = GetParentSphere(entity);
        if (parentSphere == null)
            return 1;

        return parentSphere.currentRadius;
    }

    [HookMethod(nameof(API_ScaleEntity))]
    public bool API_ScaleEntity(BaseEntity entity, float scale)
    {
        if (entity == null || entity.net == null)
            return false;

        return TryScaleEntity(entity, scale);
    }

    [HookMethod(nameof(API_ScaleEntity))]
    public bool API_ScaleEntity(BaseEntity entity, int scale)
    {
        return API_ScaleEntity(entity, (float)scale);
    }

    [HookMethod(nameof(API_RegisterScaledEntity))]
    public void API_RegisterScaledEntity(BaseEntity entity)
    {
        if (entity == null || entity.net == null)
            return;

        _data.ScaledEntities.Add(entity.net.ID.Value);
    }

    #endregion

    #region Commands

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

        if (args.Length == 0 || !float.TryParse(args[0], out var scale))
        {
            ReplyToPlayer(player, "Error.Syntax", cmd);
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

        ReplyToPlayer(player, TryScaleEntity(entity, scale) ? "Scale.Success" : "Error.ScaleBlocked", scale);
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

        if (!_data.ScaledEntities.Contains(entity.net.ID.Value))
        {
            ReplyToPlayer(player, "Error.NotTracked");
            return;
        }

        var parentSphere = GetParentSphere(entity);
        if (parentSphere == null)
        {
            ReplyToPlayer(player, "Error.NotScaled");
            return;
        }

        ReplyToPlayer(player, "GetScale.Success", parentSphere.currentRadius);
    }

    #endregion

    #region Helper Methods

    private static bool EntityScaleWasBlocked(BaseEntity entity, float scale)
    {
        return Interface.CallHook("OnEntityScale", entity, scale) is false;
    }

    private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 20)
    {
        return Physics.Raycast(basePlayer.eyes.HeadRay(), out var hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.GetEntity()
            : null;
    }

    private static void EnableGlobalBroadcastFixed(BaseEntity entity, bool wants)
    {
        entity.globalBroadcast = wants;

        if (wants)
        {
            entity.UpdateNetworkGroup();
        }
        else if (entity.net?.group?.ID == 0)
        {
            // Fix vanilla bug that prevents leaving the global network group.
            var group = entity.net.sv.visibility.GetGroup(entity.transform.position);
            entity.net.SwitchGroup(group);
        }
    }

    private static void SetupSphereEntity(SphereEntity sphereEntity, BaseEntity scaledEntity)
    {
        // SphereEntity has enableSaving off by default, so enable it if the child has saving enabled.
        // This fixes an issue where the resized child gets orphaned on restart and spams console errors every 2 seconds.
        sphereEntity.EnableSaving(scaledEntity.enableSaving);

        // SphereEntity has globalBroadcast on by default, but it should generally be off for scaled entities.
        // This fixes an issue where clients who resubscribe do not recreate the sphere or its children.
        EnableGlobalBroadcastFixed(sphereEntity, scaledEntity.globalBroadcast);
    }

    private static void SetSphereSize(SphereEntity sphereEntity, float scale)
    {
        sphereEntity.currentRadius = scale;
        sphereEntity.lerpRadius = scale;
        sphereEntity.transform.localScale = new Vector3(scale, scale, scale);
    }

    private static SphereEntity CreateSphere(Vector3 position, Quaternion rotation, float scale, BaseEntity scaledEntity)
    {
        var sphereEntity = GameManager.server.CreateEntity(SpherePrefab, position, rotation) as SphereEntity;
        if (sphereEntity == null)
            return null;

        SetupSphereEntity(sphereEntity, scaledEntity);
        SetSphereSize(sphereEntity, scale);
        sphereEntity.SetParent(scaledEntity.GetParentEntity());
        sphereEntity.Spawn();

        return sphereEntity;
    }

    private static SphereEntity GetParentSphere(BaseEntity entity)
    {
        return entity.GetParentEntity() as SphereEntity;
    }

    private void RefreshScaledEntity(BaseEntity scaledEntity, SphereEntity parentSphere)
    {
        SetupSphereEntity(parentSphere, scaledEntity);

        if (_config.HideSpheresAfterResize)
        {
            foreach (var subscriber in scaledEntity.net.group.subscribers)
            {
                _entitySubscriptionManager.InitResized(scaledEntity.net.ID, subscriber.ownerid);
            }
        }
    }

    private void UnparentFromSphere(BaseEntity scaledEntity)
    {
        var sphereEntity = GetParentSphere(scaledEntity);
        if (sphereEntity == null)
            return;

        // Un-parenting an entity automatically transfers the local scale of the parent to the child.
        // So we have to invert the local scale of the child to compensate.
        scaledEntity.transform.localScale /= sphereEntity.currentRadius;

        // If the sphere already has a parent, simply move the entity to it.
        // Parent is possibly null but that's ok since that will simply unparent.
        scaledEntity.SetParent(sphereEntity.GetParentEntity(), worldPositionStays: true, sendImmediate: true);

        sphereEntity.Kill();

        _entitySubscriptionManager.RemoveEntity(scaledEntity.net.ID);
        _data.ScaledEntities.Remove(scaledEntity.net.ID.Value);
    }

    private bool TryScaleEntity(BaseEntity entity, float scale)
    {
        if (EntityScaleWasBlocked(entity, scale))
            return false;

        var parentSphere = GetParentSphere(entity);

        // Only resize an existing sphere if it's registered.
        // This allows spheres fully managed by other plugins to remain untouched.
        if (parentSphere != null && _data.ScaledEntities.Contains(entity.net.ID.Value))
        {
            if (scale == parentSphere.currentRadius)
                return true;

            NetworkUtils.TerminateOnClient(entity);
            NetworkUtils.TerminateOnClient(parentSphere);

            // Clear the cache in ParentedEntityRenderFix.
            ParentedEntityRenderFix?.Call("OnEntityKill", entity);

            // Remove the entity from the subscriber manager to allow clients to resize it.
            // This could result in a client who is already resizing it to not resize it fully,
            // but that's not worth the trouble to fix.
            _entitySubscriptionManager.RemoveEntity(entity.net.ID);

            if (scale == 1)
            {
                UnparentFromSphere(entity);
            }
            else
            {
                SetSphereSize(parentSphere, scale);

                // Resend the child as well since it was previously terminated on clients.
                NetworkUtils.SendUpdateImmediateRecursive(parentSphere);
            }

            Interface.CallHook("OnEntityScaled", entity, scale);
            return true;
        }

        if (scale == 1)
            return true;

        _data.ScaledEntities.Add(entity.net.ID.Value);

        var entityTransform = entity.transform;
        parentSphere = CreateSphere(entityTransform.localPosition, Quaternion.identity, scale, entity);
        entityTransform.localPosition = Vector3.zero;
        entity.SetParent(parentSphere, worldPositionStays: false, sendImmediate: true);

        Interface.CallHook("OnEntityScaled", entity, scale);
        return true;
    }

    #endregion

    #region Network Utils

    private static class NetworkUtils
    {
        public static void TerminateOnClient(BaseNetworkable entity, Connection connection = null)
        {
            var write = Net.sv.StartWrite();
            write.PacketID(Message.Type.EntityDestroy);
            write.EntityID(entity.net.ID);
            write.UInt8((byte)BaseNetworkable.DestroyMode.None);
            write.Send(connection != null ? new SendInfo(connection) : new SendInfo(entity.net.group.subscribers));
        }

        public static void SendUpdateImmediateRecursive(BaseEntity entity)
        {
            entity.SendNetworkUpdateImmediate();

            foreach (var child in entity.children)
            {
                SendUpdateImmediateRecursive(child);
            }
        }
    }

    #endregion

    #region Pooling

    private class SimplePool<T> where T : class, new()
    {
        private List<T> _pool = new();

        public virtual T Get()
        {
            var item = _pool.LastOrDefault();
            if (item != null)
            {
                _pool.RemoveAt(_pool.Count - 1);
                return item;
            }

            return new T();
        }

        public virtual void Free(ref T item)
        {
            _pool.Add(item);
            item = null;
        }

        public void Clear()
        {
            _pool.Clear();
        }
    }

    private class SimpleDictionaryPool<TKey, TValue> : SimplePool<Dictionary<TKey, TValue>>
    {
        public override void Free(ref Dictionary<TKey, TValue> dict)
        {
            dict.Clear();
            base.Free(ref dict);
        }
    }

    #endregion

    #region Network Snapshot Manager

    private abstract class BaseNetworkSnapshotManager
    {
        private readonly Dictionary<NetworkableId, MemoryStream> _networkCache = new();

        public void Clear()
        {
            _networkCache.Clear();
        }

        public void InvalidateForEntity(NetworkableId entityId)
        {
            _networkCache.Remove(entityId);
        }

        // Mostly copied from:
        // - `BaseNetworkable.SendAsSnapshot(Connection)`
        // - `BasePlayer.SendEntitySnapshot(BaseNetworkable)`
        public void SendModifiedSnapshot(BaseEntity entity, Connection connection)
        {
            var write = Net.sv.StartWrite();
            connection.validate.entityUpdates++;
            var saveInfo = new BaseNetworkable.SaveInfo
            {
                forConnection = connection,
                forDisk = false,
            };
            write.PacketID(Message.Type.Entities);
            write.UInt32(connection.validate.entityUpdates);
            ToStreamForNetwork(entity, write, saveInfo);
            write.Send(new SendInfo(connection));
        }

        // Mostly copied from `BaseNetworkable.ToStream(Stream, SaveInfo)`.
        private void ToStream(BaseEntity entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
        {
            using (saveInfo.msg = Facepunch.Pool.Get<ProtoBuf.Entity>())
            {
                entity.Save(saveInfo);
                Interface.CallHook("OnEntitySaved", entity, saveInfo);
                HandleOnEntitySaved(entity, saveInfo);
                saveInfo.msg.WriteToStream(stream);
                entity.PostSave(saveInfo);
            }
        }

        // Mostly copied from `BaseNetworkable.ToStreamForNetwork(Stream, SaveInfo)`.
        private Stream ToStreamForNetwork(BaseEntity entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
        {
            if (!_networkCache.TryGetValue(entity.net.ID, out var cachedStream))
            {
                cachedStream = BaseNetworkable.EntityMemoryStreamPool.Count > 0
                    ? BaseNetworkable.EntityMemoryStreamPool.Dequeue()
                    : new MemoryStream(8);

                ToStream(entity, cachedStream, saveInfo);
                _networkCache[entity.net.ID] = cachedStream;
            }

            cachedStream.WriteTo(stream);
            return cachedStream;
        }

        // Handler for modifying save info when building a snapshot.
        protected abstract void HandleOnEntitySaved(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo);
    }

    private class NetworkSnapshotManager : BaseNetworkSnapshotManager
    {
        protected override void HandleOnEntitySaved(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo)
        {
            var parentSphere = GetParentSphere(entity);
            if (parentSphere == null)
                return;

            var transform = entity.transform;

            var grandparent = parentSphere.GetParentEntity();
            if (grandparent == null)
            {
                saveInfo.msg.parent = null;
                saveInfo.msg.baseEntity.pos = transform.position;
                saveInfo.msg.baseEntity.rot = transform.rotation.eulerAngles;
            }
            else
            {
                saveInfo.msg.parent.uid = grandparent.net.ID;
                saveInfo.msg.baseEntity.pos = parentSphere.transform.localPosition;
            }
        }
    }

    #endregion

    #region Entity Subscription Manager

    private enum ResizeState { NeedsResize, Resizing, Resized }

    private class EntitySubscriptionManager
    {
        private SimpleDictionaryPool<ulong, ResizeState> _dictPool = new();

        // This is used to keep track of which clients are aware of each entity
        // When we expect the client to destroy an entity, we update this state
        private readonly Dictionary<NetworkableId, Dictionary<ulong, ResizeState>> _networkResizeState = new();

        public void Clear()
        {
            foreach (var dict in _networkResizeState.Values)
            {
                var d = dict;
                _dictPool.Free(ref d);
            }

            _dictPool.Clear();
            _networkResizeState.Clear();
        }

        public void InitResized(NetworkableId entityId, ulong userId)
        {
            EnsureEntity(entityId).Add(userId, ResizeState.Resized);
        }

        public ResizeState GetResizeState(NetworkableId entityId, ulong userId)
        {
            var clientToResizeState = EnsureEntity(entityId);
            if (clientToResizeState.TryGetValue(userId, out var resizeState))
                return resizeState;

            clientToResizeState[userId] = ResizeState.Resizing;
            return ResizeState.NeedsResize;
        }

        // Returns true if it was still resizing.
        // Absence in the data structure indicates the client deleted it.
        public bool DoneResizing(NetworkableId entityId, ulong userId)
        {
            if (!_networkResizeState.TryGetValue(entityId, out var clientToResizeState))
                return false;

            if (!clientToResizeState.ContainsKey(userId))
                return false;

            clientToResizeState[userId] = ResizeState.Resized;
            return true;
        }

        public void RemoveEntitySubscription(NetworkableId entityId, ulong userId)
        {
            if (!_networkResizeState.TryGetValue(entityId, out var clientToResizeState))
                return;

            clientToResizeState.Remove(userId);
        }

        public void RemoveEntity(NetworkableId entityId)
        {
            if (!_networkResizeState.TryGetValue(entityId, out var clientToResizeState))
                return;

            _networkResizeState.Remove(entityId);
            _dictPool.Free(ref clientToResizeState);
        }

        public void RemoveSubscriber(ulong userId)
        {
            foreach (var entry in _networkResizeState)
            {
                entry.Value.Remove(userId);
            }
        }

        private Dictionary<ulong, ResizeState> EnsureEntity(NetworkableId entityId)
        {
            if (!_networkResizeState.TryGetValue(entityId, out var clientToResizeState))
            {
                clientToResizeState = _dictPool.Get();
                _networkResizeState[entityId] = clientToResizeState;
            }

            return clientToResizeState;
        }
    }

    #endregion

    #region Data

    private class StoredData
    {
        [JsonProperty("ScaledEntities")]
        public HashSet<ulong> ScaledEntities = new();

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
            return this;
        }
    }

    #endregion

    #region Configuration

    private class Configuration : SerializableConfiguration
    {
        [JsonProperty("Hide spheres after resize (performance intensive)")]
        public bool HideSpheresAfterResize = false;
    }

    private Configuration GetDefaultConfig() => new();

    #region Configuration Helpers

    private class SerializableConfiguration
    {
        public string ToJson() => JsonConvert.SerializeObject(this);

        public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
    }

    private static class JsonHelper
    {
        public static object Deserialize(string json) => ToObject(JToken.Parse(json));

        private static object ToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    return token.Children<JProperty>()
                        .ToDictionary(prop => prop.Name,
                            prop => ToObject(prop.Value));

                case JTokenType.Array:
                    return token.Select(ToObject).ToList();

                default:
                    return ((JValue)token).Value;
            }
        }
    }

    private bool MaybeUpdateConfig(SerializableConfiguration config)
    {
        var currentWithDefaults = config.ToDictionary();
        var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
        return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
    }

    private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
    {
        var changed = false;

        foreach (var key in currentWithDefaults.Keys)
        {
            if (currentRaw.TryGetValue(key, out var currentRawValue))
            {
                var currentDictValue = currentRawValue as Dictionary<string, object>;
                if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
                {
                    if (currentDictValue == null)
                    {
                        currentRaw[key] = currentWithDefaults[key];
                        changed = true;
                    }
                    else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                    {
                        changed = true;
                    }
                }
            }
            else
            {
                currentRaw[key] = currentWithDefaults[key];
                changed = true;
            }
        }

        return changed;
    }

    protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

    protected override void LoadConfig()
    {
        base.LoadConfig();
        try
        {
            _config = Config.ReadObject<Configuration>();
            if (_config == null)
            {
                throw new JsonException();
            }

            if (MaybeUpdateConfig(_config))
            {
                LogWarning("Configuration appears to be outdated; updating and saving");
                SaveConfig();
            }
        }
        catch (Exception e)
        {
            LogError(e.Message);
            LogWarning($"Configuration file {Name}.json is invalid; using defaults");
            LoadDefaultConfig();
        }
    }

    protected override void SaveConfig()
    {
        Log($"Configuration changes saved to {Name}.json");
        Config.WriteObject(_config, true);
    }

    #endregion

    #endregion

    #region Localization

    private string GetMessage(string playerId, string messageName, params object[] args)
    {
        var message = lang.GetMessage(messageName, this, playerId);
        return args.Length > 0 ? string.Format(message, args) : message;
    }

    private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
        player.ChatMessage(GetMessage(player.IPlayer, messageName, args));

    private string GetMessage(IPlayer player, string messageName, params object[] args) =>
        GetMessage(player.Id, messageName, args);

    private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
        GetMessage(player.UserIDString, messageName, args);

    private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
        player.Reply(GetMessage(player, messageName, args));

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["Error.NoPermission"] = "You don't have permission to do that.",
            ["Error.Syntax"] = "Syntax: {0} <size>",
            ["Error.NoEntityFound"] = "Error: No entity found.",
            ["Error.EntityNotSafeToScale"] = "Error: That entity cannot be safely scaled.",
            ["Error.NotTracked"] = "Error: That entity is not tracked by Entity Scale Manager.",
            ["Error.NotScaled"] = "Error: That entity is not scaled.",
            ["Error.ScaleBlocked"] = "Error: Another plugin prevented you from scaling that entity to size {0}.",
            ["Error.CannotMoveWithHiddenSpheres"] = "You may not move resized entities while spheres are configured to be hidden.",
            ["GetScale.Success"] = "Entity scale is: {0}",
            ["Scale.Success"] = "Entity was scaled to: {0}",
        }, this, "en");
    }

    #endregion
}