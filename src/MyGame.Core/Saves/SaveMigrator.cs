using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to GameWorld to disambiguate, matching
// the convention used by SaveManager.cs.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Core.Saves;

/// <summary>
/// Save-file migration pipeline. Runs on load to transform old saves to
/// the current schema. Each step (v1→v2, v2→v3, …) is a separate method;
/// <see cref="MigrateWorld"/> loops from the save's
/// <see cref="SaveMeta.StorageVersion"/> up to
/// <see cref="CurrentStorageVersion"/>, applying each step in order.
/// </summary>
///
/// <remarks>
/// <b>Why a migrator?</b> The desktop port's save schema is intentionally
/// minimal (4 JSON files per save). When a field is added to an entity
/// (e.g. <see cref="Item.Weight"/>), old saves load with the default
/// value (0) — which is functionally wrong (the inventory panel shows
/// zero carried weight). Rather than silently leave the bad data in
/// place, the migrator backfills the missing field from the content
/// registry on load. The migrated world is returned to the caller; the
/// caller can optionally re-save it so the next load is fast.
///
/// <para>
/// <b>Versioning:</b> the storage version lives in
/// <see cref="SaveMeta.StorageVersion"/>. Bump
/// <see cref="CurrentStorageVersion"/> whenever a migration step is
/// added. The migrator is forward-only: it only knows how to upgrade
/// old saves to the current schema, not downgrade. Old engines loading
/// new saves will see <c>StorageVersion &gt; expected</c> and should
/// refuse to load (the loader currently doesn't check this — a future
/// task).
/// </para>
///
/// <para>
/// <b>Idempotence:</b> every migration step MUST be idempotent — running
/// it on an already-migrated save should be a no-op. This lets the
/// migrator run unconditionally on load (defensive: if the meta's
/// version is missing or wrong, the migrator still produces correct
/// data).
/// </para>
///
/// <para>
/// <b>Backward compatibility:</b> the v1→v2 step (Item.Weight backfill)
/// is the only step currently defined. The Item.Weight field was added
/// after the initial release; old saves have <c>Weight = 0</c> on every
/// item instance. The migrator looks up the item's template via the
/// world's content registry and copies the template's Weight onto the
/// instance. Items without a template, or whose template doesn't exist
/// in the registry, are left at 0.
/// </para>
/// </remarks>
public static class SaveMigrator
{
    /// <summary>
    /// Current on-disk save layout version. Bump this when adding a new
    /// migration step (and add the corresponding
    /// <c>MigrateWorldV&lt;n-1&gt;ToV&lt;n&gt;</c> method +
    /// <c>switch</c> case below). Mirrored by
    /// <see cref="SaveManager.CurrentStorageVersion"/> (kept in sync).
    /// </summary>
    public const int CurrentStorageVersion = 2;

    // ─── World migration ────────────────────────────────────────────────

    /// <summary>
    /// Run every applicable world migration step from
    /// <paramref name="fromVersion"/> up to
    /// <see cref="CurrentStorageVersion"/>. The world is mutated
    /// in place AND returned (for fluent chaining). No-op if
    /// <paramref name="fromVersion"/> is already at or above the current
    /// version.
    /// </summary>
    /// <param name="world">
    /// The freshly-deserialized world. Its
    /// <see cref="World.Registries"/> must be wired up (the
    /// <see cref="SaveManager"/> does this before calling the migrator)
    /// — template lookups use it.
    /// </param>
    /// <param name="fromVersion">
    /// The save's <see cref="SaveMeta.StorageVersion"/> at load time.
    /// Clamped to &gt;= 1. If &gt;= <see cref="CurrentStorageVersion"/>,
    /// the method returns the world unchanged.
    /// </param>
    public static GameWorld MigrateWorld(GameWorld world, int fromVersion)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (fromVersion < 1) fromVersion = 1;
        if (fromVersion >= CurrentStorageVersion) return world;

        for (int v = fromVersion; v < CurrentStorageVersion; v++)
        {
            switch (v)
            {
                case 1:
                    MigrateWorldV1ToV2(world);
                    break;
                // Future migrations go here:
                // case 2: MigrateWorldV2ToV3(world); break;
                // case 3: MigrateWorldV3ToV4(world); break;
                default:
                    // Unknown step — shouldn't happen (the loop bound is
                    // CurrentStorageVersion, and every version between 1
                    // and CurrentStorageVersion must have a case). Be
                    // defensive: stop migrating rather than throwing, so
                    // a missing step doesn't brick the save.
                    return world;
            }
        }
        return world;
    }

    /// <summary>
    /// v1 → v2: backfill <see cref="Item.Weight"/> from the item's
    /// template. Before v2, item instances didn't carry a Weight field;
    /// they loaded with <c>Weight = 0</c> (the default), which made the
    /// inventory panel's carried-weight display show 0 for every item.
    /// After v2, <see cref="EntityFactory.InstantiateItem"/> copies the
    /// template's Weight onto the instance at spawn time — but old
    /// saves need a one-time backfill.
    ///
    /// <para>
    /// Targets every item in the world: loose ground items, every
    /// character's carried inventory, and every character's equipped
    /// slots (players + NPCs). Items with Weight &gt; 0 are skipped
    /// (idempotence: already-migrated items don't get re-touched).
    /// Items without a TemplateId, or whose TemplateId isn't in the
    /// registry, are left at 0 (we can't backfill what we can't look
    /// up).
    /// </para>
    /// </summary>
    private static void MigrateWorldV1ToV2(GameWorld world)
    {
        // Loose ground items.
        BackfillItemWeights(world.Items, world);

        // Every character's carried inventory + equipped slots.
        foreach (var player in world.Players)
        {
            BackfillItemWeights(player.Inventory.Items, world);
            foreach (var kv in player.Equipped)
                BackfillItemWeight(kv.Value, world);
        }
        foreach (var npc in world.Npcs)
        {
            BackfillItemWeights(npc.Inventory.Items, world);
            foreach (var kv in npc.Equipped)
                BackfillItemWeight(kv.Value, world);
        }
    }

    /// <summary>
    /// Backfill Weight on a collection of items. Defensive against null
    /// collections (a malformed save could have null Inventory.Items).
    /// </summary>
    private static void BackfillItemWeights(System.Collections.Generic.IEnumerable<Item>? items, GameWorld world)
    {
        if (items is null) return;
        foreach (var item in items) BackfillItemWeight(item, world);
    }

    /// <summary>
    /// Backfill Weight on a single item. Skipped if:
    /// <list type="bullet">
    ///   <item>The item is null.</item>
    ///   <item>The item already has a non-zero Weight (idempotence).</item>
    ///   <item>The item has no TemplateId (hand-spawned, no template to
    ///     backfill from).</item>
    ///   <item>The TemplateId isn't in the registry (unknown template —
    ///     leave at 0).</item>
    /// </list>
    /// </summary>
    private static void BackfillItemWeight(Item? item, GameWorld world)
    {
        if (item is null) return;
        if (item.Weight != 0) return;
        if (string.IsNullOrEmpty(item.TemplateId)) return;
        var tpl = world.Registries.Items.Get(item.TemplateId);
        if (tpl is null) return;
        item.Weight = tpl.Weight;
    }

    // ─── Meta migration ─────────────────────────────────────────────────

    /// <summary>
    /// Update a <see cref="SaveMeta"/> to the current schema version.
    /// Sets <see cref="SaveMeta.StorageVersion"/> to
    /// <see cref="CurrentStorageVersion"/> and refreshes
    /// <see cref="SaveMeta.EngineVersion"/> to
    /// <see cref="Common.Version.Current"/>. The caller should use the
    /// returned meta on subsequent saves so the on-disk version is
    /// bumped (the next load is then a no-op).
    ///
    /// <para>
    /// Returns a new meta via <c>with</c> (the EngineVersion property
    /// is still init-only — only StorageVersion was changed to set for
    /// the migrator's in-place bump). The caller should use the
    /// returned reference, not the original.
    /// </para>
    /// </summary>
    /// <param name="meta">
    /// The meta to update. If <paramref name="fromVersion"/> is already
    /// at or above <see cref="CurrentStorageVersion"/> and the
    /// EngineVersion already matches, the meta is still returned
    /// unchanged (the version-bump is harmless and the caller can rely
    /// on the post-condition StorageVersion == CurrentStorageVersion).
    /// </param>
    /// <param name="fromVersion">
    /// The save's StorageVersion at load time. Clamped to &gt;= 1.
    /// Currently unused (the meta is always bumped to current
    /// regardless) — kept for future migrations that may need to
    /// inspect the source version.
    /// </param>
    public static SaveMeta MigrateMeta(SaveMeta meta, int fromVersion)
    {
        if (meta is null) throw new ArgumentNullException(nameof(meta));
        if (fromVersion < 1) fromVersion = 1;

        // Always bump to current. (Idempotent: a meta already at v2
        // stays at v2; the EngineVersion refresh is also idempotent.)
        // Use `with` because EngineVersion is still init-only (only
        // StorageVersion was made settable for the migrator).
        return meta with
        {
            StorageVersion = CurrentStorageVersion,
            EngineVersion = Common.Version.Current,
        };
    }
}
