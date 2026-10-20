using System.Linq;
using HarmonyLib;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void ProfilingConfiguration()
        {
            int start = checks; bool enabled = Main.EnableProfiling.Value;
            var patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(Main), "Update"));
            bool hooks = patches != null && patches.Prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(MainThreadProfiler));
            Check(hooks == enabled, "Profiling hooks are installed only when explicitly configured");
            Check((MainThreadProfiler.TargetCount > 0) == enabled, "Disabled profiling resolves no per-method timing targets");
            Check((AccessTools.Field(typeof(MainThreadProfiler), "preparedJournalTrace").GetValue(null) != null) == enabled,
                "Disabled profiling starts no worker trace-buffer allocation");
            CutoverLog("M7 PROFILING CONFIG PASS enabled=" + enabled + "; " + (checks - start) + " native assertions");
        }
    }
}
