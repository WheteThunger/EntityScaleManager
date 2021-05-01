## Features

This plugin is primarily intended to be used by developers of other plugins.

- Allows other plugins to resize entities
- Allows privileged players to resize entities with a command
- Optional feature for hiding sphere entities after resize (performance intensive)
- Supports entities that are parented

## Use cases

### Players can resize entities for fun

Players can be granted the `entityscalemanager.unrestricted` permission to allow them to use the `scale <size>` command. Recommended only for players you trust since this can get out of hand and does not work well with all types of entities.

### Other plugins can depend on this plugin to scale entities

This is highly recommended, as opposed to each plugin implementing its own resize logic. Resizing entities has several gotchas that this plugin solves for you, listed below.
- Instances of `SphereEntity` do not have saving enabled by default. This causes children that do have saving enabled to get orphaned on server restart and spam console errors. Saving also has to be re-enabled on each server restart since the `enableSaving` property itself is not saved.
- Instances of `SphereEntity` have global broadcast enabled by default. If not desired, you have to re-disable it on each server restart since the `enableGlobalBroadcast` property itself is not saved.
  - Additionally, disabling global broadcast on an entity does not remove it from the global network group (Rust bug), so even if you spawn it in a positional network group, you have to do some hacks to remove it from the global network group on server restart.
- The `SphereEntity` needs to be destroyed after the scaled entity is killed.

### Other plugins can register their scaled entities with this plugin to hide the black sphere after resize

If your plugin already implements its own resize logic, you can still integrate with this plugin to hide the black sphere after resize, if this plugin has been configured to do that (it's performance intensive so it's optional). For details, see the `API_RegisterScaledEntity` method later in the documentation.

## Known issues

Some types of entities have no issues with resizing. Others may have a multitude of issues. Here are some issues to keep an eye out for.

- Resizing vehicles will break their physics -- Don't do it. It's possible to fix, but out of scope for this plugin for now.
- Resizing auto turrets adjusts their targeting range. If you enlarge a turret, it may get stuck unable to shoot if it selects a target outside its normal range.
- Various entities may appear invisible (not necessarily right when you resize it), due to a rendering bug with parented entities.
  - This can be mitigated with the Parented Entity Render Fix plugin but that has a significant performance cost.

More specific plugins can be built on top of Entity Scale Manager to address entities that need special treatment. See the API section for methods you can utilize so you don't have to re-implement everything.

## Recommended compatible plugins

- [Parented Entity Render Fix](https://umod.org/plugins/parented-entity-render-fix) -- Fixes a client-side bug where certain types of entities are often invisible when they are parented to another entity.
  - This is an important counterpart to Entity Scale Manager since entities can only be resized by parenting them to transparent black spheres, which can cause the rendering bug.
  - That plugin has a significant performance cost. If you cannot afford to install it, avoid resizing entities that have this rendering issue since they will usually appear invisible when resized.
    - Note: To determine if an entity is affected by the rendering bug, resize it, then leave the area and return, which will destroy and recreate the entity on your client.

## Commands

- `scale <size>` -- Resizes the entity you are looking at.
  - The entity must have a collider for this command to find it.
  - If the entity is already resized, this will visually reset it to default scale (1.0) and then resize it to the desired scale. This is intentional because it avoids various edge cases that would otherwise take significant complexity for this plugin to mitigate, particularly when the config option is enabled to hide spheres after resize.
  - When resizing back to scale `1.0`, the sphere will be removed.
  - Max recommended scale is `7.0`. If you go higher, you may notice the colliders aren't as large as the entity.
- `getscale` -- Prints the scale of the entity you are looking at.

## Permissions

- `entityscalemanager.unrestricted` -- Allows unrestricted usage of the `scale` command. More restricted rulesets may be implemented in the future.

## Configuration

Default configuration:

```json
{
  "Hide spheres after resize (performance intensive)": false
}
```

- `Hide spheres after resize (performance intensive)` (`true` or `false`) -- While `true`, the transparent black spheres used for resizing entities will be hidden after the resize is complete. This effect is per client.
  - **WARNING:** This is implemented by subscribing to hooks that Oxide calls very frequently. Oxide has a significant overhead for these calls, which can cause a significant performance drop for servers with high population and high entity count, especially if players frequently spawn in or teleport to areas with many entities. **DO NOT ENABLE** this feature if your server already has performance problems.
  - You may test out the performance cost of this feature by reloading the plugin with this option enabled, while monitoring your server FPS before and after. You can also get a sense from the total hook time that Oxide reports next to the plugin (in seconds) when you run `o.plugins`. Note: A plugin's total hook time is expected to start at 0 when a plugin loads, and it goes up over time as the plugin gradually uses hooks. If that number climbs quickly (e.g., 1 second per minute) then it means the plugin may be using a significant percentage of your overall performance budget.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.Syntax": "Syntax: {0} <size>",
  "Error.NoEntityFound": "Error: No entity found.",
  "Error.NotTracked": "Error: That entity is not tracked by Entity Scale Manager.",
  "Error.NotScaled": "Error: That entity is not scaled.",
  "Error.ScaleBlocked": "Error: Another plugin prevented you from scaling that entity to size {0}.",
  "GetScale.Success": "Entity scale is: {0}",
  "Scale.Success": "Entity was scaled to: {0}"
}
```

## Developer API

#### API_ScaleEntity

Plugins can call this API to resize an entity. This will create a transparent black sphere, parent the entity to that sphere, and resize the sphere to produce the resizing effect on clients. This will also resize the entity colliders to match. See the documentaiton for the `scale` command for additional details about how resizing works.

```csharp
bool API_ScaleEntity(BaseEntity entity, float scale)
```

#### API_RegisterScaledEntity

Plugins can call this API to register an already scaled entity with Entity Scale Manager. This is useful for plugins that want to manage scaling entities themselves, taking only an optional dependency on Entity Scale Manager, allowing this plugin to hide the sphere after resize, if configured to do so.

```csharp
void API_RegisterScaledEntity(BaseEntity entity)
```

#### API_GetScale

Plugins can call this API to get the current scale of an entity that is tracked by Entity Scale Manager.

```csharp
float API_GetScale(BaseEntity entity)
```

The return value will be `-1` if any of the following are true.
- The entity is invalid
- The entity is not tracked by Entity Scale Manager
- The entity is not parented to a sphere (for some reason, despite being tracked by this plugin)

## Developer Hooks

#### OnEntityScale

- Called when an entity is about to be resized by either the `scale` command or the `API_ScaleEntity` method
- Returning `false` will prevent the entity from being resized
- Returning `null` will result in the default behavior

```csharp
bool? OnEntityScale(BaseEntity entity, float scale)
```

#### OnEntityScaled

- Called after a successful use of either the `scale` command or the `API_ScaleEntity` method
- No return behavior

```csharp
void OnEntityScaled(BaseEntity entity, float scale)
```
