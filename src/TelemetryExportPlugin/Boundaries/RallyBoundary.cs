using System;

namespace TelemetryExportPlugin.Boundaries
{
    /// <summary>
    /// Rally recording boundary: one file per stage, start/end driven by the
    /// sim's own stage-start/stage-end signal.
    ///
    /// NOT YET WIRED to a real event source - per PLUGIN_IMPLEMENTATION_PLAN.md
    /// build order this needs a live rally sim (RBR/DiRT/etc.) to find which
    /// SimHub adapter property or event actually reports stage-start/stage-end
    /// for that sim. Call NotifyStageStart/NotifyStageEnd from wherever that
    /// turns out to be (a StatusDataBase field, a pluginManager custom property,
    /// or a game-specific event) once identified.
    /// </summary>
    public delegate void StageStartedHandler(string context, string car, string driver);

    public class RallyBoundary
    {
        private bool _inStage;

        public event StageStartedHandler StageStarted;
        public event Action StageEnded;

        public void NotifyStageStart(string context, string car, string driver)
        {
            if (_inStage) return;
            _inStage = true;
            StageStarted?.Invoke(context, car, driver);
        }

        public void NotifyStageEnd()
        {
            if (!_inStage) return;
            _inStage = false;
            StageEnded?.Invoke();
        }
    }
}
