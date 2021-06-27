using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Entity Scale Manager", "WhiteThunder", "2.0.2")]
    [Description("Utilities for resizing entities.")]
    internal class EntityScaleManager : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        Plugin ParentedEntityRenderFix;

        private static EntityScaleManager _pluginInstance;
        private static Configuration _pluginConfig;
        private static StoredData _pluginData;

        private const string PermissionScaleUnrestricted = "entityscalemanager.unrestricted";
        private const string SpherePrefab = "assets/prefabs/visualization/sphere.prefab";

        // This could be improved by calculating the time needed for the resize,
        // since the amount of time required seems to depend on the scale.
        private const float ExpectedResizeDuration = 7f;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredData.Load();

            permission.RegisterPermission(PermissionScaleUnrestricted, this);

            if (!_pluginConfig.HideSpheresAfterResize)
            {
                Unsubscribe(nameof(OnEntitySnapshot));
                Unsubscribe(nameof(OnPlayerDisconnected));
                Unsubscribe(nameof(OnNetworkGroupLeft));
            }
        }

        private void Unload()
        {
            _pluginData.Save();

            EntitySubscriptionManager.Instance.Clear();
            NetworkSnapshotManager.Instance.Clear();

            _pluginData = null;
            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var entity = networkable as BaseEntity;
                if (entity == null)
                    continue;

                var parentSphere = GetParentSphere(entity);
                if (parentSphere != null && _pluginData.ScaledEntities.Contains(entity.net.ID))
                    RefreshScaledEntity(entity, parentSphere);
            }
        }

        private void OnServerSave()
        {
            _pluginData.Save();
        }

        private void OnNewSave()
        {
            _pluginData = StoredData.Clear();
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
                return;

            if (!_pluginData.ScaledEntities.Remove(entity.net.ID))
                return;

            EntitySubscriptionManager.Instance.RemoveEntity(entity.net.ID);

            var parentSphere = GetParentSphere(entity);
            if (parentSphere != null)
            {
                // Destroy the sphere that was used to scale the entity.
                // This assumes that only one entity was scaled using this sphere.
                // We could instead check if the sphere still has children, but keeping the sphere
                // might cause it to never be killed after the remaining children are killed.
                // Plugins should generally parent other entities using a separate sphere, or
                // parent to the scaled entity itself.
                parentSphere.Invoke(() =>
                {
                    if (!parentSphere.IsDestroyed)
                        parentSphere.Kill();
                }, 0);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            EntitySubscriptionManager.Instance.RemoveSubscriber(player.userID);
        }

        private bool? OnEntitySnapshot(BaseEntity entity, Connection connection)
        {
            if (entity == null || entity.net == null)
                return null;

            if (!_pluginData.ScaledEntities.Contains(entity.net.ID))
                return null;

            var parentSphere = GetParentSphere(entity);
            if (parentSphere == null)
                return null;

            // Detect when the vanilla network cache has been cleared in order to invalidate the custom cache.
            if (entity._NetworkCache == null)
                NetworkSnapshotManager.Instance.InvalidateForEntity(entity.net.ID);

            var resizeState = EntitySubscriptionManager.Instance.GetResizeState(entity.net.ID, connection.ownerid);
            if (resizeState == ResizeState.Resized)
            {
                // Don't track CPU time of this since it's almost identidcal to the vanilla behavior being cancelled.
                // Tracking it would give server operators false information that this plugin is spending more CPU time than it is.
                TrackEnd();
                NetworkSnapshotManager.Instance.SendModifiedSnapshot(entity, connection);
                TrackStart();
                return false;
            }

            if (resizeState == ResizeState.NeedsResize)
            {
                // The entity is now starting to resize for this client.
                // Start a timer to cause this client to start using the custom snapshots after resize.
                timer.Once(ExpectedResizeDuration, () =>
                {
                    if (entity != null
                        && parentSphere != null
                        && EntitySubscriptionManager.Instance.DoneResizing(entity.net.ID, connection.ownerid))
                    {
                        // Send a snapshot to the client indicating that the entity is not parented to the sphere.
                        NetworkSnapshotManager.Instance.SendModifiedSnapshot(entity, connection);

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

                EntitySubscriptionManager.Instance.RemoveEntitySubscription(entity.net.ID, player.userID);
            }
        }

        #endregion

        #region API

        private float API_GetScale(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
                return 1;

            if (!_pluginData.ScaledEntities.Contains(entity.net.ID))
                return 1;

            var parentSphere = GetParentSphere(entity);
            if (parentSphere == null)
                return 1;

            return parentSphere.currentRadius;
        }

        private bool API_ScaleEntity(BaseEntity entity, float scale)
        {
            if (entity == null || entity.net == null)
                return false;

            return TryScaleEntity(entity, scale);
        }

        private bool API_ScaleEntity(BaseEntity entity, int scale) =>
            API_ScaleEntity(entity, (float)scale);

        private void API_RegisterScaledEntity(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
                return;

            _pluginData.ScaledEntities.Add(entity.net.ID);
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

            float scale;
            if (args.Length == 0 || !float.TryParse(args[0], out scale))
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

            if (TryScaleEntity(entity, scale))
                ReplyToPlayer(player, "Scale.Success", scale);
            else
                ReplyToPlayer(player, "Error.ScaleBlocked", scale);
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

            if (!_pluginData.ScaledEntities.Contains(entity.net.ID))
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
            object hookResult = Interface.CallHook("OnEntityScale", entity, scale);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 20)
        {
            RaycastHit hit;
            return Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
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

        private static void RefreshScaledEntity(BaseEntity scaledEntity, SphereEntity parentSphere)
        {
            SetupSphereEntity(parentSphere, scaledEntity);

            if (_pluginConfig.HideSpheresAfterResize)
            {
                foreach (var subscriber in scaledEntity.net.group.subscribers)
                    EntitySubscriptionManager.Instance.InitResized(scaledEntity.net.ID, subscriber.ownerid);
            }
        }

        private static SphereEntity GetParentSphere(BaseEntity entity) =>
            entity.GetParentEntity() as SphereEntity;

        private static void UnparentFromSphere(BaseEntity scaledEntity)
        {
            var sphereEntity = GetParentSphere(scaledEntity);
            if (sphereEntity == null)
                return;

            // Unparenting an entity automatically transfers the local scale of the parent to the child.
            // So we have to invert the local scale of the child to compensate.
            scaledEntity.transform.localScale /= sphereEntity.currentRadius;

            // If the sphere already has a parent, simply move the entity to it.
            // Parent is possibly null but that's ok since that will simply unparent.
            scaledEntity.SetParent(sphereEntity.GetParentEntity(), worldPositionStays: true, sendImmediate: true);

            sphereEntity.Kill();

            EntitySubscriptionManager.Instance.RemoveEntity(scaledEntity.net.ID);
            _pluginData.ScaledEntities.Remove(scaledEntity.net.ID);
        }

        private static bool TryScaleEntity(BaseEntity entity, float scale)
        {
            if (EntityScaleWasBlocked(entity, scale))
                return false;

            var parentSphere = GetParentSphere(entity);

            // Only resize an existing sphere if it's registered.
            // This allows spheres fully managed by other plugins to remain untouched.
            if (parentSphere != null && _pluginData.ScaledEntities.Contains(entity.net.ID))
            {
                if (scale == parentSphere.currentRadius)
                    return true;

                NetworkUtils.TerminateOnClient(entity);
                NetworkUtils.TerminateOnClient(parentSphere);

                // Clear the cache in ParentedEntityRenderFix.
                _pluginInstance.ParentedEntityRenderFix?.Call("OnEntityKill", entity);

                // Remove the entity from the subscriber manager to allow clients to resize it.
                // This could result in a client who is already resizing it to not resize it fully,
                // but that's not worth the trouble to fix.
                EntitySubscriptionManager.Instance.RemoveEntity(entity.net.ID);

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

            _pluginData.ScaledEntities.Add(entity.net.ID);

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
                if (Net.sv.write.Start())
                {
                    Net.sv.write.PacketID(Message.Type.EntityDestroy);
                    Net.sv.write.EntityID(entity.net.ID);
                    Net.sv.write.UInt8((byte)BaseNetworkable.DestroyMode.None);
                    Net.sv.write.Send(connection != null ? new SendInfo(connection) : new SendInfo(entity.net.group.subscribers));
                }
            }

            public static void SendUpdateImmediateRecursive(BaseEntity entity)
            {
                entity.SendNetworkUpdateImmediate();
                foreach (var child in entity.children)
                    SendUpdateImmediateRecursive(child);
            }
        }

        #endregion

        #region Network Snapshot Manager

        private abstract class BaseNetworkSnapshotManager
        {
            private readonly Dictionary<uint, MemoryStream> _networkCache = new Dictionary<uint, MemoryStream>();

            public void Clear()
            {
                _networkCache.Clear();
            }

            public void InvalidateForEntity(uint entityId)
            {
                _networkCache.Remove(entityId);
            }

            // Mostly copied from:
            // - `BaseNetworkable.SendAsSnapshot(Connection)`
            // - `BasePlayer.SendEntitySnapshot(BaseNetworkable)`
            public void SendModifiedSnapshot(BaseEntity entity, Connection connection)
            {
                if (Net.sv.write.Start())
                {
                    connection.validate.entityUpdates++;
                    var saveInfo = new BaseNetworkable.SaveInfo()
                    {
                        forConnection = connection,
                        forDisk = false
                    };
                    Net.sv.write.PacketID(Message.Type.Entities);
                    Net.sv.write.UInt32(connection.validate.entityUpdates);
                    ToStreamForNetwork(entity, Net.sv.write, saveInfo);
                    Net.sv.write.Send(new SendInfo(connection));
                }
            }

            // Mostly copied from `BaseNetworkable.ToStream(Stream, SaveInfo)`.
            private void ToStream(BaseEntity entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
            {
                using (saveInfo.msg = Facepunch.Pool.Get<ProtoBuf.Entity>())
                {
                    entity.Save(saveInfo);
                    Interface.CallHook("OnEntitySaved", entity, saveInfo);
                    HandleOnEntitySaved(entity, saveInfo);
                    saveInfo.msg.ToProto(stream);
                    entity.PostSave(saveInfo);
                }
            }

            // Mostly copied from `BaseNetworkable.ToStreamForNetwork(Stream, SaveInfo)`.
            private Stream ToStreamForNetwork(BaseEntity entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
            {
                MemoryStream cachedStream;
                if (!_networkCache.TryGetValue(entity.net.ID, out cachedStream))
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
            private static NetworkSnapshotManager _instance = new NetworkSnapshotManager();
            public static NetworkSnapshotManager Instance => _instance;

            protected override void HandleOnEntitySaved(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo)
            {
                var parentSphere = GetParentSphere(entity);
                if (parentSphere == null)
                    return;

                var scale = parentSphere.currentRadius;
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
            private static EntitySubscriptionManager _instance = new EntitySubscriptionManager();
            public static EntitySubscriptionManager Instance => _instance;

            // This is used to keep track of which clients are aware of each entity
            // When we expect the client to destroy an entity, we update this state
            private readonly Dictionary<uint, Dictionary<ulong, ResizeState>> _networkResizeState = new Dictionary<uint, Dictionary<ulong, ResizeState>>();

            public void Clear()
            {
                _networkResizeState.Clear();
            }

            private Dictionary<ulong, ResizeState> EnsureEntity(uint entityId)
            {
                Dictionary<ulong, ResizeState> clientToResizeState;
                if (!_networkResizeState.TryGetValue(entityId, out clientToResizeState))
                {
                    clientToResizeState = new Dictionary<ulong, ResizeState>();
                    _networkResizeState[entityId] = clientToResizeState;
                }
                return clientToResizeState;
            }

            public void InitResized(uint entityId, ulong userId)
            {
                EnsureEntity(entityId).Add(userId, ResizeState.Resized);
            }

            public ResizeState GetResizeState(uint entityId, ulong userId)
            {
                var clientToResizeState = EnsureEntity(entityId);

                ResizeState resizeState;
                if (clientToResizeState.TryGetValue(userId, out resizeState))
                    return resizeState;

                clientToResizeState[userId] = ResizeState.Resizing;
                return ResizeState.NeedsResize;
            }

            // Returns true if it was still resizing.
            // Absence in the data structure indicates the client deleted it.
            public bool DoneResizing(uint entityId, ulong userId)
            {
                Dictionary<ulong, ResizeState> clientToResizeState;
                if (!_networkResizeState.TryGetValue(entityId, out clientToResizeState))
                    return false;

                if (!clientToResizeState.ContainsKey(userId))
                    return false;

                clientToResizeState[userId] = ResizeState.Resized;
                return true;
            }

            public void RemoveEntitySubscription(uint entityId, ulong userId)
            {
                Dictionary<ulong, ResizeState> clientToResizeState;
                if (!_networkResizeState.TryGetValue(entityId, out clientToResizeState))
                    return;

                clientToResizeState.Remove(userId);
            }

            public void RemoveEntity(uint entityId)
            {
                _networkResizeState.Remove(entityId);
            }

            public void RemoveSubscriber(ulong userId)
            {
                foreach (var entry in _networkResizeState)
                    entry.Value.Remove(userId);
            }
        }

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty("ScaledEntities")]
            public HashSet<uint> ScaledEntities = new HashSet<uint>();

            public static StoredData Load() =>
                Interface.Oxide.DataFileSystem.ReadObject<StoredData>(_pluginInstance.Name) ?? new StoredData();

            public static StoredData Clear() => new StoredData().Save();

            public StoredData Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(_pluginInstance.Name, this);
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

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

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
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
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

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error.NoPermission"] = "You don't have permission to do that.",
                ["Error.Syntax"] = "Syntax: {0} <size>",
                ["Error.NoEntityFound"] = "Error: No entity found.",
                ["Error.NotTracked"] = "Error: That entity is not tracked by Entity Scale Manager.",
                ["Error.NotScaled"] = "Error: That entity is not scaled.",
                ["Error.ScaleBlocked"] = "Error: Another plugin prevented you from scaling that entity to size {0}.",
                ["GetScale.Success"] = "Entity scale is: {0}",
                ["Scale.Success"] = "Entity was scaled to: {0}",
            }, this, "en");
        }

        #endregion
    }
}
