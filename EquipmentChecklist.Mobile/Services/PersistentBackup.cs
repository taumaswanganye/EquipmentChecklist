namespace EquipmentChecklist.Mobile.Services;

// PersistentBackup was an opt-in feature that mirrored the SQLite cache
// (eqcache.db) to the public Documents folder so the cache survived
// Android's "Settings → Apps → Storage → Clear data" action.
//
// It was disabled because it caused a silent startup crash on the target
// device. The class is kept as an empty stub so any straggling reference
// elsewhere still compiles; nothing in the DI graph references it.
//
// To bring the feature back later:
//   1. Restore the class body from git history.
//   2. AddSingleton<PersistentBackup>() in MauiProgram.cs (above LocalCache).
//   3. Re-add TryEager<PersistentBackup>(app) at the top of the eager block.
//   4. Re-add the manifest pieces: fullBackupContent / dataExtractionRules /
//      requestLegacyExternalStorage and the WRITE_/READ_EXTERNAL_STORAGE
//      permissions. Make sure the XML rule files under
//      Platforms/Android/Resources/xml/ end up in the build output before
//      flipping the switch — that was the suspected root cause.
public class PersistentBackup
{
}
