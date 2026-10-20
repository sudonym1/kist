using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ThorsKist
{
    [HarmonyPatch(typeof(Game), "ContinueLogout")]
    internal static class JournalLogout
    {
        internal static bool Pending;
        private static bool continuing;
        [HarmonyPrefix]
        private static bool Prefix(Game __instance, bool save, bool shouldExit, bool changeToStartScene)
        {
            if (continuing || (!save && !shouldExit)) return true;
            if (Pending) return false;
            var session = JournalGameplaySession.Current;
            if (session == null || session.Client.Address == null) return true;
            Pending = true;
            Main.Instance.StartCoroutine(Finish(__instance, session, save, shouldExit, changeToStartScene));
            return false;
        }
        private static IEnumerator Finish(Game game, JournalGameplaySession session, bool save, bool shouldExit, bool changeToStartScene)
        {
            var request = session.Logout();
            float started = Time.realtimeSinceStartup;
            while (!request.IsFaulted && !request.IsCanceled && (!request.IsCompleted || !session.Client.ProcessingStopped)
                && ReferenceEquals(session, JournalGameplaySession.Current) && Time.realtimeSinceStartup - started < 31f)
                yield return null;
            // An uncertain close uses ordinary disconnect/exclusion settlement.
            // No character save participates in the journal acknowledgment.
            if (request.IsFaulted) Main.Log.LogError("Kist logout requires disconnect settlement: " + request.Exception.GetBaseException());
            try
            {
                continuing = true;
                if (game != null) AccessTools.Method(typeof(Game), "ContinueLogout").Invoke(game, new object[] { save, shouldExit, changeToStartScene });
            }
            finally { continuing = false; Pending = false; }
        }
    }
}
