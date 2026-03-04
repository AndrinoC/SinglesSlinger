using System.Collections;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Provides a static entry point for starting Unity coroutines without
    /// requiring a specific MonoBehaviour instance. The host GameObject is
    /// marked DontDestroyOnLoad so it persists across scene transitions.
    /// </summary>
    public static class StaticCoroutine
    {
        private sealed class CoroutineHost : MonoBehaviour { }

        private static CoroutineHost host;

        private static void EnsureHost()
        {
            if (host != null) return;

            var go = new GameObject("SinglesSlinger.StaticCoroutineHost");
            Object.DontDestroyOnLoad(go);
            host = go.AddComponent<CoroutineHost>();
        }

        /// <summary>
        /// Starts a coroutine on the persistent host MonoBehaviour.
        /// </summary>
        /// <param name="routine">The IEnumerator coroutine to run.</param>
        /// <returns>The started Coroutine handle.</returns>
        public static Coroutine Start(IEnumerator routine)
        {
            EnsureHost();
            return host.StartCoroutine(routine);
        }

        /// <summary>
        /// Stops all coroutines currently running on the persistent host.
        /// </summary>
        public static void StopAll()
        {
            if (host == null) return;
            host.StopAllCoroutines();
        }
    }
}