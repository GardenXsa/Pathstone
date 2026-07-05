using System.Text.Json.Serialization;

namespace MyGame.Core.Saves;

/// <summary>
/// Manifest embedded inside a <c>.pathstone-world</c> archive (issue #33 —
/// save sharing). The manifest is written as <c>manifest.json</c> at the
/// root of the zip alongside the four save files
/// (<c>meta.json</c>, <c>world.json</c>/<c>world.json.gz</c>,
/// <c>log.json</c>/<c>log.json.gz</c>,
/// <c>state.json</c>/<c>state.json.gz</c>).
///
/// <para>
/// The manifest carries the export-time metadata that doesn't belong in
/// <see cref="SaveMeta"/> (which is the in-fiction save metadata):
/// who exported it, when, with which engine version, and (on import)
/// when it was imported. The receiving side can show this in the UI
/// («Импортирован: 2024-01-15 из мира Долина Туманов, экспортировал
/// Странник-AB12»).
/// </para>
///
/// <para>
/// <b>Backward compatibility:</b> <c>manifest.json</c> is optional on
/// import — older archives (or hand-zipped saves) without a manifest
/// still import; the importer just won't have the export metadata.
/// </para>
/// </summary>
public sealed record SaveManifest
{
    /// <summary>
    /// Human-readable title of the exported world (copied from
    /// <see cref="SaveMeta.WorldTitle"/> at export time, falling back to
    /// <see cref="SaveMeta.Name"/> when the world title is empty).
    /// </summary>
    public string? WorldTitle { get; init; }

    /// <summary>
    /// Save slot name (copied from <see cref="SaveMeta.Name"/>). Lets the
    /// importer suggest a default name for the new save slot.
    /// </summary>
    public string? SaveName { get; init; }

    /// <summary>
    /// Engine version that wrote the save (e.g. «0.2.0»). Copied from
    /// <see cref="Common.Version.Current"/> at export time so the
    /// importer can warn if the archive was written by an incompatible
    /// engine version.
    /// </summary>
    public string? EngineVersion { get; init; }

    /// <summary>
    /// On-disk save layout version (mirrors
    /// <see cref="SaveMeta.StorageVersion"/>). The importer can use this
    /// to decide whether the save needs migration on load.
    /// </summary>
    public int StorageVersion { get; init; }

    /// <summary>
    /// Profile id of the user who exported the save. Lets the receiver
    /// attribute the save to its original creator (a future "shared by"
    /// UI could show this). <see cref="Guid.Empty"/> when the original
    /// save had no owner (hand-authored test saves).
    /// </summary>
    public Guid OwnerProfileId { get; init; }

    /// <summary>
    /// UTC timestamp when the archive was exported. The receiver can
    /// show this in the import UI («Экспортирован: 2024-01-15 14:32»).
    /// </summary>
    public DateTimeOffset ExportTimestamp { get; init; }

    /// <summary>
    /// UTC timestamp when the archive was imported into this profile.
    /// Null on a freshly-exported archive (the importer sets it on the
    /// way in). Persisted in the save directory as
    /// <c>manifest.json</c> so a future re-export carries the import
    /// history forward.
    /// </summary>
    public DateTimeOffset? ImportTimestamp { get; init; }

    /// <summary>
    /// Schema version of the manifest itself. Bumped if the manifest
    /// shape changes in a way that requires migration. Currently 1.
    /// </summary>
    public int ManifestVersion { get; init; } = 1;
}
