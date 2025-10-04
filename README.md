## Features

- Allows privileged players to resize entities with a command
- Allows other plugins to resize entities via APIs
- Does not allow players to be resized (not possible)

## Permissions

- `entityscalemanager.unrestricted` -- Allows unrestricted usage of the `scale` command. More restricted rulesets may be implemented in the future.

## Commands

- `scale <size>` -- Resizes the entity you are looking at to the specified size in all dimensions. The entity must have a server-side collider for this command to find it. To resize an entity to its original size, use `scale 1`.
- `scale <x> <y> <z>` -- Resizes the entity you are looking at to the specified scale in each dimension.
- `getscale` -- Prints the scale of the entity you are looking at, if the entity has been resized by Entity Scale Manager.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.Syntax": "Syntax: {0} <size>",
  "Error.NoEntityFound": "Error: No entity found.",
  "Error.EntityNotSafeToScale": "Error: That entity cannot be safely scaled.",
  "Error.NotTracked": "Error: That entity is not tracked by Entity Scale Manager.",
  "Error.ScaleBlocked": "Error: Another plugin prevented you from scaling that entity to size {0}.",
  "GetScale.Success": "Entity scale is: {0}",
  "Scale.Success": "Entity was scaled to: {0}"
}
```

## Developer API

#### API_ScaleEntity

```csharp
bool API_ScaleEntity(BaseEntity entity, float scale)
bool API_ScaleEntity(BaseEntity entity, Vector3 scale)
```

- Returns `true` if the entity was successfully scaled, otherwise `false`

#### API_GetScale

```csharp
float API_GetScale(BaseEntity entity)
```

- Returns the scale of the entity, or `1.0` if the entity was not resized by Entity Scale Manager

## Developer Hooks

#### OnEntityScale

- Called when an entity is about to be resized by either the `scale` command or the `API_ScaleEntity` method
- Returning `false` will prevent the entity from being resized
- Returning `null` will result in the default behavior

```csharp
object OnEntityScale(BaseEntity entity, Vector3 scale)
```

#### OnEntityScaled

- Called after a successful use of either the `scale` command or the `API_ScaleEntity` method
- No return behavior

```csharp
void OnEntityScaled(BaseEntity entity, Vector3 scale)
```
