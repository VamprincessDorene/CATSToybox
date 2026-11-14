using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using SignSpeak.Rig;
using SignSpeak.Signs;

namespace SignSpeak;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = SettingsMigration.CurrentVersion;

    /// <summary>
    /// Brings an older saved config up to date. Returns true if anything
    /// changed, so the caller knows to save. The upgrade steps themselves live
    /// in <see cref="SettingsMigration"/> so they can be tested headlessly.
    /// </summary>
    public bool Migrate()
    {
        var translations = UseImportedTranslations;
        var changed = SettingsMigration.Migrate(Version, Posture, ref translations, out var version);
        UseImportedTranslations = translations;
        Version = version;
        return changed;
    }

    /// <summary>
    /// Publish poses to other players through Simple Heels, so anyone synced to
    /// you with Mare / PSync / Snowcloak sees the signing. Requires the LivePose
    /// backend, since that is the format remote viewers understand.
    /// </summary>
    public bool SyncToOtherPlayers { get; set; } = true;

    /// <summary>
    /// Prefer LivePose (ships with Simple Heels) over Customize+ when both are
    /// present. LivePose authored the pose files, so it applies them with no
    /// format conversion and no loss.
    /// </summary>
    public bool PreferLivePose { get; set; } = true;

    /// <summary>Which hand does the spelling. Most signers use their dominant hand.</summary>
    public bool RightHanded { get; set; } = true;

    /// <summary>Playback rate multiplier.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>
    /// How often the temporary profile is pushed to Customize+. Every push
    /// re-parses JSON on the far side, so there is no point running this at
    /// full frame rate; 30 is smooth and cheap.
    /// </summary>
    public int UpdateHz { get; set; } = 30;

    /// <summary>Show the letter currently being spelled in the plugin window.</summary>
    public bool ShowLetterReadout { get; set; } = true;

    /// <summary>
    /// Flat calibration storage. Newtonsoft handles a list far more predictably
    /// than a rectangular array, so the 5x2 grid is flattened as
    /// index = finger * 2 + (distal ? 1 : 0).
    /// </summary>
    public List<JointAxis> Joints { get; set; } = Flatten(HandRig.DefaultCalibration());

    public SigningPosture Posture { get; set; } = new();

    /// <summary>
    /// Hand-authored handshapes imported from pose files, keyed by the
    /// character they were mapped to. Stored as one-character strings because
    /// Newtonsoft handles string keys far more predictably than char keys.
    /// </summary>
    public Dictionary<string, ImportedSign> ImportedSigns { get; set; } = new();

    /// <summary>Reference capture used to convert absolute .pose files into offsets.</summary>
    public string ReferencePoseFile { get; set; } = string.Empty;

    /// <summary>
    /// Send the small positional offsets imported poses carry. Off by default;
    /// see SignLibrary.UseImportedTranslations for why.
    /// </summary>
    public bool UseImportedTranslations { get; set; } = true;

    /// <summary>Folder the import browser opens in, so it remembers where the poses live.</summary>
    public string LastImportFolder { get; set; } = string.Empty;

    /// <summary>Copies the stored imports into a library instance.</summary>
    public void LoadImportsInto(SignLibrary library)
    {
        library.Imported.Clear();

        foreach (var (key, sign) in ImportedSigns)
        {
            if (key.Length == 0 || sign is null)
                continue;

            library.Imported[char.ToUpperInvariant(key[0])] = sign;
        }
    }

    /// <summary>Writes the library's imports back for saving.</summary>
    public void StoreImportsFrom(SignLibrary library)
    {
        ImportedSigns = new Dictionary<string, ImportedSign>();

        foreach (var (character, sign) in library.Imported)
            ImportedSigns[character.ToString()] = sign;
    }

    /// <summary>Rebuilds the 5x2 calibration grid the rig expects.</summary>
    public JointAxis[,] GetCalibration()
    {
        // Tolerate a config written by an older or corrupted version.
        if (Joints.Count != 10)
            Joints = Flatten(HandRig.DefaultCalibration());

        var grid = new JointAxis[5, 2];
        for (var finger = 0; finger < 5; finger++)
        {
            grid[finger, 0] = Joints[finger * 2];
            grid[finger, 1] = Joints[finger * 2 + 1];
        }

        return grid;
    }

    public void ResetCalibration() => Joints = Flatten(HandRig.DefaultCalibration());

    public void ResetPosture() => Posture = new SigningPosture();

    private static List<JointAxis> Flatten(JointAxis[,] grid)
    {
        var list = new List<JointAxis>(10);
        for (var finger = 0; finger < 5; finger++)
        {
            list.Add(grid[finger, 0]);
            list.Add(grid[finger, 1]);
        }

        return list;
    }
}
