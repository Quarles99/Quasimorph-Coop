using UnityEngine;

namespace QuasimorphCoop
{
    // Everything goes to Unity's Player.log with a fixed prefix so playtests can be grepped.
    internal static class Log
    {
        private const string Prefix = "[QMCoop] ";

        public static void Info(string message) => Debug.Log(Prefix + message);

        public static void Warn(string message) => Debug.LogWarning(Prefix + message);

        public static void Error(string message) => Debug.LogError(Prefix + message);
    }
}
