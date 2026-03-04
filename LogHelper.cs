using System.Collections.Generic;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Centralized logging utilities with throttle support.
    /// Repeated messages sharing the same key are suppressed for a configurable cooldown.
    /// </summary>
    internal static class LogHelper
    {
        private static readonly Dictionary<string, float> _logNextAllowed =
            new Dictionary<string, float>();

        /// <summary>
        /// Logs an error, throttled so the same key only fires once per cooldown period.
        /// </summary>
        internal static void LogErrorThrottled(string key, string message, float cooldownSeconds = 10f)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_logNextAllowed.TryGetValue(key, out float next) && now < next)
                    return;
                _logNextAllowed[key] = now + cooldownSeconds;
                Plugin.Log.LogError(message);
            }
            catch { }
        }

        /// <summary>
        /// Logs a warning, throttled so the same key only fires once per cooldown period.
        /// </summary>
        internal static void LogWarningThrottled(string key, string message, float cooldownSeconds = 10f)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_logNextAllowed.TryGetValue(key, out float next) && now < next)
                    return;
                _logNextAllowed[key] = now + cooldownSeconds;
                Plugin.Log.LogWarning(message);
            }
            catch { }
        }

        /// <summary>
        /// Whether debug-level logging is enabled via <see cref="Plugin.DebugLogging"/>.
        /// </summary>
        internal static bool DebugEnabled
        {
            get
            {
                try
                {
                    return Plugin.DebugLogging != null && Plugin.DebugLogging.Value;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Logs an info-level message only when <see cref="DebugEnabled"/> is true.
        /// </summary>
        internal static void LogDebug(string msg)
        {
            if (!DebugEnabled) return;
            try { Plugin.Log.LogInfo(msg); } catch { }
        }
    }
}