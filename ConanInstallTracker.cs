using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace conan_vs_extension
{
    /**
     * Central registry of every `conan install` currently running in the background, no matter
     * what triggered it - a proactive install from BuildEventsHandler (solution
     * opened, project added, active configuration/profile switched), or a user-initiated one from
     * the "Installer"/"Supprimer" buttons in ConanPackageManagerControl.
     *
     * Without a SHARED registry, a Build could still race with (and fail alongside) a Conan
     * install that the user kicked off by hand from the package manager window - only proactive
     * installs were being tracked/blocked-on before, which is why the build could still slip
     * through while Conan was mid-install/mid-update. BuildEventsHandler blocks a
     * Build (in its `UpdateSolution_Begin` handler) until every entry here has finished.
     */
    public static class ConanInstallTracker
    {
        private static readonly ConcurrentDictionary<string, JoinableTask> _pending =
            new ConcurrentDictionary<string, JoinableTask>(StringComparer.OrdinalIgnoreCase);

        /**
         * Runs work in the background (via the JoinableTaskFactory, so it can
         * later be Join()'d/JoinAsync()'d without deadlocking) and records it under
         * key (typically the project's full path) until it completes. Returns
         * the JoinableTask so the caller can await it too (via JoinAsync()) if it needs the result
         * or wants to know when it's done, instead of purely firing-and-forgetting it.
         */
        public static JoinableTask Track(string key, Func<Task> work)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            JoinableTask joinableTask = ThreadHelper.JoinableTaskFactory.RunAsync(work);
            _pending[key] = joinableTask;

            joinableTask.Task.ContinueWith(completedTask =>
            {
                // Only remove our own entry - a newer install for the same key may have started
                // (and replaced it in the dictionary) while this one was still running.
                if (_pending.TryGetValue(key, out JoinableTask current) && current == joinableTask)
                {
                    _pending.TryRemove(key, out JoinableTask _);
                }
            }, TaskScheduler.Default);

            return joinableTask;
        }

        /**
         * Snapshot of every install currently tracked as still running.
         */
        public static JoinableTask[] GetPending() => _pending.Values.ToArray();
    }
}
