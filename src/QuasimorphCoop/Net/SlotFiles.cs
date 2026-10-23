using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace QuasimorphCoop.Net
{
    // A save slot is a set of files named slot_<n>_* in persistentDataPath (session, header, space,
    // dung_<location>, decals, the co-op party sidecar, thumbnail). Autosaves use an autosave_ prefix
    // and are not part of the transfer.
    internal static class SlotFiles
    {
        // The main menu only lists slots 0-2, so the client's copy of the host's world can't
        // overwrite one of the client's own saves.
        public const int ClientSlot = 9;

        private static string Dir => Application.persistentDataPath;

        public static Dictionary<string, byte[]> Read(int slot)
        {
            string prefix = $"slot_{slot}_";
            return Directory.GetFiles(Dir, prefix + "*")
                .Where(p => !p.Contains(".temp."))
                .ToDictionary(p => Path.GetFileName(p).Substring(prefix.Length), File.ReadAllBytes);
        }

        // Replaces everything in the target slot (including its autosave) with the given files.
        public static void Write(int slot, Dictionary<string, byte[]> files)
        {
            foreach (string old in Directory.GetFiles(Dir, $"slot_{slot}_*").Concat(Directory.GetFiles(Dir, $"autosave_slot_{slot}_*")))
            {
                File.Delete(old);
            }
            foreach (KeyValuePair<string, byte[]> file in files)
            {
                File.WriteAllBytes(Path.Combine(Dir, $"slot_{slot}_{file.Key}"), file.Value);
            }
        }
    }
}
