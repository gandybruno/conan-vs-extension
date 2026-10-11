using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using VSLangProj;

namespace conan_vs_extension
{
    /**
     * Everything involved in wiring a Visual C++ project up to Conan: detecting whether a
     * project has opted in (conandataFileExists), generating the pre-build script
     * that used to be the only way `conan install` ran (SaveConanPrebuildEventsAllConfigAsync),
     * proactively running `conan install` ahead of time instead (EnsureConanPackagesInstalledAsync),
     * and injecting the resulting `conandeps.props` into the project so MSBuild actually
     * picks up the installed libraries (InjectConanDepsAsync). Also home to several
     * small EnvDTE project-tree helpers (GetStartupProject, GetAllProjectsFlat, ...)
     * used throughout the rest of the extension.
     */
    public class ProjectConfigurationManager
    {

        public ProjectConfigurationManager()
        {
        }

        /**
         * Whether the project has opted into Conan, i.e. has a `conandata.yml` next to its project file.
         */
        public static bool conandataFileExists(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectDirectory = System.IO.Path.GetDirectoryName(project.FullName);
            string path = Path.Combine(projectDirectory, "conandata.yml");
            return File.Exists(path);
        }

        /**
         * Adds the generated `conandeps.props` property sheet to every configuration of the project, if it exists on disk and isn't already referenced.
         */
        public static async Task InjectConanDepsToAllConfigsAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (project.Object is VCProject vcProject)
            {
                string propsFilePath = GetPropsFilePath(project);
                if (File.Exists(propsFilePath))
                {
                    foreach (VCConfiguration vcConfig in (IEnumerable)vcProject.Configurations)
                    {
                        InjectConanDepsToConfig(vcConfig, propsFilePath);
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Properties file '{propsFilePath}' does not exist.");
                }
            }
        }

        /**
         * Adds the generated `conandeps.props` property sheet to a single configuration and saves the project, if the props file exists and isn't already referenced. No-op if the project hasn't opted into Conan.
         */
        public static async Task InjectConanDepsAsync(Project project, VCConfiguration vcConfig)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!conandataFileExists(project))
            {
                System.Diagnostics.Debug.WriteLine("conandata.yml not found. Skipping Conan Deps.");
                return;
            }

            string propsFilePath = GetPropsFilePath(project);
            if (File.Exists(propsFilePath))
            {
                InjectConanDepsToConfig(vcConfig, propsFilePath);
                project.Save();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Properties file '{propsFilePath}' does not exist.");
            }
        }

        /**
         * Adds propsFilePath as a property sheet of vcConfig, unless it's already one.
         */
        private static void InjectConanDepsToConfig(VCConfiguration vcConfig, string propsFilePath)
        {
            bool isAlreadyIncluded = false;
            IVCCollection propertySheets = vcConfig.PropertySheets as IVCCollection;
            foreach (VCPropertySheet sheet in propertySheets)
            {
                if (sheet.PropertySheetFile.Equals(propsFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    isAlreadyIncluded = true;
                    break;
                }
            }
            if (!isAlreadyIncluded)
            {
                vcConfig.AddPropertySheet(propsFilePath);
            }
        }
        
        /**
         * Path to the MSBuild property sheet Conan's MSBuildDeps generator writes (`<project>\conan\conandeps.props`).
         */
        private static string GetPropsFilePath(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectFilePath = project.FullName;
            string projectDirectory = Path.GetDirectoryName(projectFilePath);
            return Path.Combine(projectDirectory, "conan", "conandeps.props");
        }

        /**
         * Writes `.conan\conan_install.bat` - a change-detecting wrapper around `conan
         * install` used by the pre-build event (see SaveConanPrebuildEventAsync).
         * It compares conandata.yml/conanfile.py/version.txt (and a `.runconan` marker
         * written whenever a profile changes) against snapshots taken after the last successful
         * install, and only re-runs `conan install` when something actually changed - deliberately
         * failing the build the first time it does, since Conan needs to run and MSBuild pick up
         * the result before the same build can succeed (see EnsureConanPackagesInstalledAsync
         * for how this extension avoids that "build twice" cycle in the common case).
         */
        private static async Task GenerateConanInstallScriptAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string conan_script_name = "conan_install.bat";
            string projectDirectory = Path.GetDirectoryName(project.FullName);
            string conanScriptDirectory = Path.Combine(projectDirectory, ".conan");
            string scriptPath = Path.Combine(conanScriptDirectory, conan_script_name);
            // GetResolvedConanExecutablePath (not the raw ConanExecutablePath) is required here:
            // this path gets baked as a literal string into the generated conan_install.bat below
            // ("{conanPath}" install ...), and an empty FileName there (the "Use System Conan"
            // case, where ConanExecutablePath is unset) would produce a broken `"" install ...`
            // line instead of letting the OS resolve `conan` from PATH.
            string conanPath = GlobalSettings.GetResolvedConanExecutablePath();

            string conanCommandContents = $@"@echo off
setlocal enabledelayedexpansion

REM Initialize flags

REM Check if the control files exist

set performInstall=0

if not exist "".conan\CONANDATA_%CONAN_BUILD_CONFIG%"" set ""performInstall=1""
if not exist "".conan\CONANFILE_%CONAN_BUILD_CONFIG%"" set ""performInstall=1""
if exist ""version.txt"" if not exist "".conan\VERSION_%CONAN_BUILD_CONFIG%"" set ""performInstall=1""

REM Check for changes in conandata.yml and conanfile.py

if exist "".conan\CONANDATA_%CONAN_BUILD_CONFIG%"" (
    echo Checking changes in conandata.yml
    fc ""conandata.yml"" "".conan\CONANDATA_%CONAN_BUILD_CONFIG%"" > nul
    if errorlevel 1 (
        set performInstall=1
        echo Changes detected in conandata.yml
    )
)

if exist "".conan\CONANFILE_%CONAN_BUILD_CONFIG%"" (
    echo Checking changes in conanfile.py
    fc ""conanfile.py"" "".conan\CONANFILE_%CONAN_BUILD_CONFIG%"" > nul
    if errorlevel 1 (
        set performInstall=1
        echo Changes detected in conanfile.py
    )
)

if exist ""version.txt"" if exist "".conan\VERSION_%CONAN_BUILD_CONFIG%"" (
    echo Checking changes in version.txt
    fc ""version.txt"" "".conan\VERSION_%CONAN_BUILD_CONFIG%"" > nul
    if errorlevel 1 (
        set performInstall=1
        echo Changes detected in version.txt
    )
)

REM Check for the .runconan file that indicates changes in profiles

if exist "".conan\.runconan"" (
    echo Changes in profiles detected
    set performInstall=1
    del "".conan\.runconan""
)

if %performInstall% equ 1 (
    REM Echo changes detected
    echo Changes detected, executing conan install...
    
    set ""args=""

    :args_loop
    if ""%~1""=="""" goto after_args_loop
    set ""args=!args! %1""
    shift
    goto args_loop

    :after_args_loop
    echo Arguments for conan install: !args!

    ""{conanPath}"" install !args!

    if !errorlevel! neq 0 (
        echo ERROR: Conan installation failed. Please check the console output to troubleshoot issues.
        exit /b 1
    )

    REM Update control files to reflect current state
    copy /Y conandata.yml .conan\CONANDATA_%CONAN_BUILD_CONFIG%
    copy /Y conanfile.py .conan\CONANFILE_%CONAN_BUILD_CONFIG%
    if exist ""version.txt"" copy /y version.txt .conan\VERSION_%CONAN_BUILD_CONFIG%

    REM Display the message indicating Conan install finished
    echo ****************************************************************
    echo *                                                              *
    echo *   Conan installation completed successfully.                 *
    echo *   Please relaunch the build to apply the new changes.        *
    echo *                                                              *
    echo ****************************************************************
    echo ERROR: Conan installation completed successfully. Please relaunch the build to apply the new changes.
    exit /b 1
)

REM Echo no changes detected
echo No changes detected, skipping conan install...
";

            Directory.CreateDirectory(conanScriptDirectory);
            File.WriteAllText(scriptPath, conanCommandContents);
        }

        /**
         * Adds a call to `conan_install.bat` to the configuration's pre-build event command line, unless one is already present.
         */
        private static async Task SaveConanPrebuildEventAsync(Project project, VCConfiguration vcConfig)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            VCProject vcProject = project.Object as VCProject;
            IVCCollection tools = vcConfig.Tools as IVCCollection;
            VCPreBuildEventTool preBuildTool = tools.Item("VCPreBuildEventTool") as VCPreBuildEventTool;

            if (preBuildTool != null)
            {
                string conan_script_name = "conan_install.bat";
                string commandLine = $"set CONAN_BUILD_CONFIG=\"$(Configuration)_$(Platform)\" && \"$(ProjectDir).conan\\{conan_script_name}\" . -pr:h=.conan/$(Configuration)_$(Platform) -pr:b=default --build=missing";
                if (!preBuildTool.CommandLine.Contains(conan_script_name))
                {
                    preBuildTool.CommandLine += Environment.NewLine + commandLine;
                    vcProject.Save();
                }
            }
        }

        /**
         * (Re)generates `conan_install.bat` and wires its pre-build event into every configuration of the project. No-op if the project hasn't opted into Conan.
         */
        public static async Task SaveConanPrebuildEventsAllConfigAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!conandataFileExists(project))
            {
                System.Diagnostics.Debug.WriteLine("conandata.yml not found. Skipping Conan PreBuildEvent.");
                return;
            }

            await GenerateConanInstallScriptAsync(project); // all the config share the same script

            VCProject vcProject = project.Object as VCProject;
            foreach (VCConfiguration vcConfig in (IEnumerable)vcProject.Configurations)
            {
                await SaveConanPrebuildEventAsync(project, vcConfig);
            }
        }

        /**
         * Runs `conan install` for one project/configuration right away (add a package, open a
         * project, switch the active configuration/profile - see BuildEventsHandler and
         * ConanPackageManagerControl for the call sites) instead of only doing it lazily from the
         * pre-build event script during an actual Build. That pre-build script (see
         * GenerateConanInstallScriptAsync) intentionally fails the build with "please relaunch"
         * the first time it detects a change, precisely because Conan hadn't run yet and the
         * generated conandeps.props wasn't wired into the project - forcing a second build. Doing
         * the same work here, ahead of time, means that by the time the user actually clicks
         * Build, the pre-build script finds nothing new to do and the build succeeds on the first
         * try. The pre-build script is left in place as a safety net for configurations that
         * weren't proactively handled (e.g. building a configuration that was never made active).
         *
         * @param project The project to install Conan packages for.
         * @param vcConfig The specific build configuration to generate/install for (its Conan profile - see ConanProfilesManager.getProfileName).
         * @param showErrors Reserved for callers that want to react to failures beyond reading LastConanInstallError - not currently used to change this method's own behavior (it never shows UI itself either way).
         * @return True if `conan install` succeeded (or there was nothing to do); false if it failed - see LastConanInstallError for why.
         */
        public static async Task<bool> EnsureConanPackagesInstalledAsync(Project project, VCConfiguration vcConfig, bool showErrors)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (project == null || vcConfig == null || !conandataFileExists(project))
            {
                return true;
            }

            string projectDirectory = Path.GetDirectoryName(project.FullName);
            string profileName = ConanProfilesManager.getProfileName(vcConfig);

            // Older versions wrote "name/version@_/_", which Conan never matches to an editable package.
            ConanFileManager.NormalizeConandataFile(projectDirectory);

            // Make sure the property sheets exist (empty) BEFORE conan install, and are imported
            // unconditionally, so IntelliSense follows them without unloading/reloading the project.
            string propsSignatureBefore = ComputePropsSignature(projectDirectory);
            bool propsPrepared = PrepareConanPropsFiles(project);
            await InjectConanDepsAsync(project, vcConfig);

            bool success;
            string error;

            // One `conan install` at a time, whatever triggered it: two concurrent installs (e.g. a
            // profile/configuration event firing while the package manager runs its own) race on
            // the same Conan cache, and the loser can leave a recipe registered without its files.
            await _installGate.WaitAsync();
            try
            {
                (success, error) = await RunConanInstallProcessAsync(projectDirectory, profileName);

                // A recipe left half-written in the cache ("seems broken" / "conanfile.py not found")
                // fails every later install: drop just that reference from the cache and retry once.
                if (!success && await TryRepairBrokenRecipeAsync(error))
                {
                    (success, error) = await RunConanInstallProcessAsync(projectDirectory, profileName);
                }
            }
            finally
            {
                _installGate.Release();
            }

            if (!success)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ProjectConfigurationManager] Proactive 'conan install' failed for '{project.Name}' ({profileName}): {error}");
                LastConanInstallError = error;
                return false;
            }

            try
            {
                UpdateConanControlFiles(projectDirectory, profileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectConfigurationManager] Failed to update Conan control files: {ex}");
            }

            await InjectConanDepsAsync(project, vcConfig);

            // The generated property sheets changed (a dependency added/removed/made editable, first-time
            // wiring...): IntelliSense does not follow on its own, so trigger the same thing as
            // Project > Rescan Solution - without unloading/reloading the project.
            if (propsPrepared || ComputePropsSignature(projectDirectory) != propsSignatureBefore)
            {
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => RescanIntelliSenseAsync(project));
            }

            return true;
        }

        /**
         * A fingerprint of every `.props` file under the project's `conan` folder, to know whether
         * `conan install` changed the include/lib paths the project sees.
         */
        private static string ComputePropsSignature(string projectDirectory)
        {
            try
            {
                string folder = Path.Combine(projectDirectory, "conan");
                if (!Directory.Exists(folder))
                {
                    return string.Empty;
                }

                var content = new System.Text.StringBuilder();
                string[] files = Directory.GetFiles(folder, "*.props", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    content.Append(file).Append('\n').Append(File.ReadAllText(file)).Append('\n');
                }

                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content.ToString())));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectConfigurationManager] ComputePropsSignature failed: {ex.Message}");
                return string.Empty;
            }
        }

        /**
         * Runs Visual Studio's own "Project > Rescan Solution" (`Project.RescanSolution`) so
         * IntelliSense re-reads the include directories. Waits a moment first: VS re-evaluates the
         * project asynchronously after its property sheets change and a rescan issued before that
         * still sees the old paths.
         */
        private static async Task RescanIntelliSenseAsync(Project project)
        {
            await Task.Delay(1500);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                project.DTE.ExecuteCommand("Project.RescanSolution");
                ConanOutputPane.Log(Loc.T("IntelliSense: solution rescan requested (Project > Rescan Solution).", "IntelliSense : réanalyse de la solution demandée (Projet > Réanalyser la solution)."));
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"IntelliSense: the solution rescan failed: {ex.Message}", $"IntelliSense : la réanalyse de la solution a échoué : {ex.Message}"));
            }
        }

        private const string EmptyPropsFile =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />\r\n";

        /**
         * Writes an empty `conandeps.props` (never overwriting an existing one) for the extension's
         * own sheet (`conan\conandeps.props`) and for every `conan\<Config>\conandeps.props` the
         * .vcxproj already imports, then turns those imports unconditional (drops the
         * `Condition="Exists(...)"`, evaluated only once when VS loads the project, which left
         * IntelliSense without the Conan include paths until a reload). `conan install` rewrites
         * the files afterwards as usual.
         *
         * @return True if something was created or the project file was changed (first-time setup).
         */
        private static bool PrepareConanPropsFiles(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool changed = false;

            try
            {
                string projectFile = project.FullName;
                string projectDirectory = Path.GetDirectoryName(projectFile);
                var propsFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.Combine(projectDirectory, "conan", "conandeps.props")
                };

                string text = File.Exists(projectFile) ? File.ReadAllText(projectFile) : string.Empty;
                var importRegex = new System.Text.RegularExpressions.Regex(
                    @"(?<import><Import\s+Project=""conan[\x5C/](?<config>[^\x5C/""']+)[\x5C/]conandeps\.props"")\s+Condition=""[^""]*""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in importRegex.Matches(text))
                {
                    propsFiles.Add(Path.Combine(projectDirectory, "conan", match.Groups["config"].Value, "conandeps.props"));
                }

                foreach (string propsFile in propsFiles)
                {
                    if (!File.Exists(propsFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(propsFile));
                        File.WriteAllText(propsFile, EmptyPropsFile, new System.Text.UTF8Encoding(false));
                        changed = true;
                    }
                }

                // Per-configuration imports written by hand: make them unconditional now that the files exist.
                string migrated = importRegex.Replace(text, "${import}");
                if (migrated != text)
                {
                    bool bom = false;
                    using (var stream = File.OpenRead(projectFile))
                    {
                        bom = stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF;
                    }

                    File.WriteAllText(projectFile, migrated, new System.Text.UTF8Encoding(bom));
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectConfigurationManager] PrepareConanPropsFiles failed: {ex}");
            }

            return changed;
        }

        private static readonly System.Threading.SemaphoreSlim _installGate = new System.Threading.SemaphoreSlim(1, 1);

        /**
         * If a failed `conan install` reports a recipe that cannot be loaded from the cache (its
         * folder is missing/incomplete), removes that one reference from the cache
         * (`conan remove "<name>/<version>" -c`) so the next attempt downloads it again.
         *
         * @return True if a broken reference was removed and the install is worth retrying.
         */
        private static async Task<bool> TryRepairBrokenRecipeAsync(string error)
        {
            if (string.IsNullOrEmpty(error) ||
                !(error.Contains("Cannot load recipe") || error.Contains("seems broken")))
            {
                return false;
            }

            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                error, @"Package '(?<ref>[^']+)' not resolved");
            if (!match.Success)
            {
                return false;
            }

            string reference = match.Groups["ref"].Value;
            ConanOutputPane.Log(Loc.T($"Recipe '{reference}' is corrupted in the Conan cache: removing this reference and retrying.", $"Recette '{reference}' corrompue dans le cache Conan : suppression de cette référence puis nouvel essai."));

            try
            {
                (int exitCode, string _, string _) = await ConanRemoteHelper.RunConanAsync($"remove \"{reference}\" -c", 60000);
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"  -> exception: {ex.Message}", $"  -> exception : {ex.Message}"));
                return false;
            }
        }

        /**
         * Set by EnsureConanPackagesInstalledAsync right before it returns false, so
         * a caller that wants to surface the error (e.g. after an explicit "Install" click) can
         * read it without needing conan install's raw process plumbing.
         */
        public static string LastConanInstallError { get; private set; }

        /**
         * Runs `conan install . -pr:h=<profile> -pr:b=default --build=missing` for one project
         * directory/profile, streaming its stdout/stderr line-by-line into ConanOutputPane
         * as they arrive (rather than only showing the full output once the process exits - a
         * `--build=missing` compiling a large binary from source can otherwise look frozen for
         * several minutes). Bounded by a 5-minute timeout, after which the process is killed and
         * treated as a failure so the UI never gets stuck waiting indefinitely.
         *
         * @return (true, null) on success; (false, an error message built from stderr/stdout or the timeout/exception) otherwise.
         */
        private static async Task<(bool success, string error)> RunConanInstallProcessAsync(string projectDirectory, string profileName)
        {
            string conanPath = GlobalSettings.GetResolvedConanExecutablePath();

            string arguments = $"install . -pr:h=.conan/{profileName} -pr:b=default --build=missing";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = conanPath,
                Arguments = arguments,
                WorkingDirectory = projectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ConanOutputPane.Log($"[{System.IO.Path.GetFileName(projectDirectory)}] conan {arguments}");

            var stdoutBuilder = new System.Text.StringBuilder();
            var stderrBuilder = new System.Text.StringBuilder();
            var processExited = new TaskCompletionSource<bool>();

            try
            {
                using (var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    // Stream conan's output line-by-line into the "Conan" Output pane as it happens
                    // (compiler output while building a missing binary included), instead of only
                    // showing the final result once the whole command has finished - that's what
                    // used to make a multi-minute `--build=missing` look completely frozen.
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        stdoutBuilder.AppendLine(e.Data);
                        ConanOutputPane.Log(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        stderrBuilder.AppendLine(e.Data);
                        ConanOutputPane.Log(e.Data);
                    };
                    process.Exited += (s, e) => processExited.TrySetResult(true);

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // conan install can compile missing binaries (--build=missing) - give it
                    // considerably more time than a simple metadata/list command. But still bound
                    // it: if the process hangs (waiting on input, a stalled download...) instead of
                    // failing outright, this would never resolve and the UI would be stuck on
                    // "chargement" indefinitely.
                    const int timeoutMs = 300000;
                    Task completed = await Task.WhenAny(processExited.Task, Task.Delay(timeoutMs));

                    if (completed != processExited.Task)
                    {
                        try { process.Kill(); } catch { /* already exited on its own */ }
                        string timeoutError = Loc.T($"'conan install' did not answer within the allotted time ({timeoutMs / 1000}s).", $"'conan install' n'a pas répondu dans le délai imparti ({timeoutMs / 1000}s).");
                        ConanOutputPane.Log(Loc.T($"  -> timed out ({timeoutMs / 1000}s), process killed.", $"  -> délai dépassé ({timeoutMs / 1000}s), processus arrêté."));
                        return (false, timeoutError);
                    }

                    // The parameterless WaitForExit() guarantees the async output/error readers
                    // have finished delivering their last buffered lines before we read the
                    // builders below (a timed WaitForExit(ms) does not give that guarantee) - the
                    // process has already exited at this point so this returns immediately.
                    await Task.Run(() => process.WaitForExit());

                    if (process.ExitCode != 0)
                    {
                        string error = stderrBuilder.Length > 0 ? stderrBuilder.ToString() : stdoutBuilder.ToString();
                        error = string.IsNullOrWhiteSpace(error) ? Loc.T($"conan returned code {process.ExitCode}.", $"conan a retourné le code {process.ExitCode}.") : error.Trim();
                        ConanOutputPane.Log(Loc.T($"  -> failed (code {process.ExitCode})", $"  -> échec (code {process.ExitCode})"));
                        return (false, error);
                    }

                    ConanOutputPane.Log("  -> OK");
                    return (true, null);
                }
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"  -> exception: {ex.Message}", $"  -> exception : {ex.Message}"));
                return (false, ex.Message);
            }
        }

        /**
         * Mirrors the bookkeeping the pre-build script (conan_install.bat) does at the end of a
         * successful run: snapshot conandata.yml/conanfile.py/version.txt into the ".conan"
         * folder under this configuration's key, and clear the ".runconan" marker. Doing this
         * here too means the script's own change-detection correctly finds nothing to do the next
         * time an actual Build triggers it for this same configuration.
         */
        private static void UpdateConanControlFiles(string projectDirectory, string configKey)
        {
            string conanFolder = Path.Combine(projectDirectory, ".conan");
            Directory.CreateDirectory(conanFolder);

            string conandataPath = Path.Combine(projectDirectory, "conandata.yml");
            string conanfilePath = Path.Combine(projectDirectory, "conanfile.py");
            string versionPath = Path.Combine(projectDirectory, "version.txt");

            if (File.Exists(conandataPath))
            {
                File.Copy(conandataPath, Path.Combine(conanFolder, $"CONANDATA_{configKey}"), true);
            }
            if (File.Exists(conanfilePath))
            {
                File.Copy(conanfilePath, Path.Combine(conanFolder, $"CONANFILE_{configKey}"), true);
            }
            if (File.Exists(versionPath))
            {
                File.Copy(versionPath, Path.Combine(conanFolder, $"VERSION_{configKey}"), true);
            }

            string runConanMarker = Path.Combine(conanFolder, ".runconan");
            if (File.Exists(runConanMarker))
            {
                File.Delete(runConanMarker);
            }
        }

        /**
         * Flattens every project in the solution (recursing into solution folders), used to
         * proactively run Conan installs for every Conan-enabled project when the solution opens.
         */
        public static List<Project> GetAllProjectsFlat(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new List<Project>();
            if (dte?.Solution?.Projects == null)
            {
                return result;
            }

            foreach (Project project in dte.Solution.Projects)
            {
                CollectProjectsRecursive(project, result);
            }

            return result;
        }

        /**
         * Recursion helper for GetAllProjectsFlat: adds project to result, or recurses into its children if it's a solution folder.
         */
        private static void CollectProjectsRecursive(Project project, List<Project> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null)
            {
                return;
            }

            if (project.Kind == EnvDTE.Constants.vsProjectKindSolutionItems)
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item.SubProject != null)
                        {
                            CollectProjectsRecursive(item.SubProject, result);
                        }
                    }
                }
            }
            else
            {
                result.Add(project);
            }
        }

        /**
         * Finds the VCConfiguration matching the project's currently active configuration/platform (the toolbar dropdowns), if any.
         */
        public static VCConfiguration GetActiveVCConfiguration(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project.Object is VCProject vcProject)
            {
                IVCCollection configurations = vcProject.Configurations as IVCCollection;
                VCConfiguration activeConfiguration = null;

                foreach (VCConfiguration config in configurations)
                {
                    string configName = config.ConfigurationName;
                    VCPlatform vcPlatform = config.Platform as VCPlatform;
                    if (config.ConfigurationName == project.ConfigurationManager.ActiveConfiguration.ConfigurationName &&
                        vcPlatform.Name == project.ConfigurationManager.ActiveConfiguration.PlatformName)
                    {
                        activeConfiguration = config;
                        break;
                    }
                }

                return activeConfiguration;
            }

            return null;
        }

        /**
         * Resolves the solution's configured startup project (Solution Properties > Startup Project), searching recursively through solution folders.
         *
         * @return The startup project, or null if none is set or it can't be found.
         */
        public static Project GetStartupProject(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Access startup projects from SolutionBuild
            var startupProjects = dte.Solution.SolutionBuild.StartupProjects as object[];

            if (startupProjects != null && startupProjects.Length > 0)
            {
                // Get the name of the first startup project
                string startupProjectName = (string)startupProjects[0];

                // Iterate through all projects in the solution to find a matching project
                foreach (Project project in dte.Solution.Projects)
                {
                    Project result = FindProjectByNameRecursive(project, startupProjectName);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null; // No startup project found
        }

        /**
         * Recursion helper: returns project (or one of its children, if it's a solution folder) whose `UniqueName` matches projectName.
         */
        private static Project FindProjectByNameRecursive(Project project, string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project.UniqueName.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }

            // Handle solution folders
            if (project.Kind == EnvDTE.Constants.vsProjectKindSolutionItems)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        Project foundProject = FindProjectByNameRecursive(item.SubProject, projectName);
                        if (foundProject != null)
                        {
                            return foundProject;
                        }
                    }
                }
            }

            return null;
        }

        /**
         * Finds a project anywhere in the solution (including inside solution folders) by its `UniqueName`.
         */
        public static Project GetProjectByName(DTE dte, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project project in dte.Solution.Projects)
            {
                Project result = FindProjectByNameRecursive(project, name);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /**
         * Finds the VCConfiguration matching a given configuration name/platform pair (as reported by EnvDTE build events, e.g. "Debug"/"x64").
         */
        public static VCConfiguration GetVCConfig(Project project, string ProjectConfig, string Platform)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project.Object is VCProject vcProject)
            {
                foreach (VCConfiguration vcConfig in (IEnumerable)vcProject.Configurations)
                {
                    VCPlatform vcPlatform = vcConfig.Platform as VCPlatform;
                    if (vcConfig.ConfigurationName.Equals(ProjectConfig) 
                        && vcPlatform.Name.Equals(Platform)) { 
                        return vcConfig; 
                    }  
                }
            }
            return null;
        }
    }
}
