using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;

namespace conan_vs_extension.Packaging
{
    /**
     * Outcome of PackageCreator.CreateAsync.
     */
    public class PackageCreationResult
    {
        public bool Success { get; set; }

        public bool Cancelled { get; set; }

        /** Why it failed. */
        public string Error { get; set; }

        /** "name/version". */
        public string Reference { get; set; }

        /** The configurations whose binary is now in the Conan cache, e.g. "Debug|x64". */
        public List<string> Packaged { get; } = new List<string>();
    }

    /**
     * "Create package": turns a library project into a Conan package in the local cache. Each
     * selected configuration is built by Visual Studio, then exported with `conan export-pkg`
     * (the binaries Visual Studio just produced are packaged as they are - Conan does not
     * rebuild anything). All the binaries end up under the same `name/version`.
     */
    public static class PackageCreator
    {
        private static int _running;

        /**
         * Creates the package described by the project's `conandata.yml` (`package` section) and
         * `version.txt`.
         *
         * @param progress Told what is being done, one short sentence at a time.
         * @param cancellationToken Stops the conan command in progress; a Visual Studio build already started is left to finish.
         */
        public static async Task<PackageCreationResult> CreateAsync(Project project, IProgress<string> progress, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return Failure(Loc.T("A package is already being created.", "Un paquet est déjà en cours de création."));
            }

            try
            {
                return await CreateCoreAsync(project, progress ?? new Progress<string>(), cancellationToken);
            }
            catch (Exception ex)
            {
                ConanOutputPane.Log(Loc.T($"Package creation: unexpected exception: {ex}", $"Création du paquet : exception inattendue : {ex}"));
                return Failure(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static async Task<PackageCreationResult> CreateCoreAsync(Project project, IProgress<string> progress, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string projectDirectory = Path.GetDirectoryName(project.FullName);
            PackageSettings settings = ConanDataFile.Load(projectDirectory).Package;
            string version = VersionFile.Read(projectDirectory);

            if (settings == null)
            {
                return Failure(Loc.T("This project has no package settings yet: fill in the Package tab first.",
                                     "Ce projet n'a pas encore de réglages de paquet : remplissez d'abord l'onglet Paquet."));
            }

            List<string> problems = PackagePlanner.Validate(settings, version);
            if (problems.Count > 0)
            {
                return Failure(string.Join("\n", problems));
            }

            // The profiles give the Conan settings of each configuration; they are (re)written
            // from the project, like before every build.
            new ConanProfilesManager().GenerateProfilesForProject(project);

            (List<PackagePlanner.Step> steps, string planError) = PackagePlanner.Plan(settings, VcProjectInspector.GetConfigurations(project), projectDirectory);
            if (planError != null)
            {
                return Failure(planError);
            }

            if (!ConanFileManager.WritePackageSettings(projectDirectory, settings))
            {
                return Failure(Loc.T("conandata.yml was modified by hand: the extension cannot update it. Restore its two comment lines at the top, or create the package from a terminal (conan export-pkg).",
                                     "conandata.yml a été modifié à la main : l'extension ne peut pas le mettre à jour. Rétablissez ses deux lignes de commentaire en tête, ou créez le paquet depuis un terminal (conan export-pkg)."));
            }

            await ProjectConfigurationManager.SaveConanPrebuildEventsAllConfigAsync(project);

            var result = new PackageCreationResult { Reference = $"{settings.Name}/{version}" };
            ConanOutputPane.Log(Loc.T($"Creating package {result.Reference} ({string.Join(", ", steps.Select(s => s.Configuration.Name))})...",
                                      $"Création du paquet {result.Reference} ({string.Join(", ", steps.Select(s => s.Configuration.Name))})..."));

            foreach (PackagePlanner.Step step in steps)
            {
                string error = await PackageConfigurationAsync(project, projectDirectory, step, progress, cancellationToken);
                if (error != null)
                {
                    result.Cancelled = cancellationToken.IsCancellationRequested;
                    result.Error = error;
                    return result;
                }

                result.Packaged.Add(step.Configuration.Name);
            }

            result.Success = true;
            ConanOutputPane.Log(Loc.T($"Package {result.Reference} is in the local Conan cache. To publish it: conan upload {result.Reference} -r=<remote>",
                                      $"Le paquet {result.Reference} est dans le cache Conan local. Pour le publier : conan upload {result.Reference} -r=<remote>"));
            return result;
        }

        /**
         * Installs the dependencies of one configuration, builds it and exports its binaries.
         *
         * @return Null on success, the error otherwise.
         */
        private static async Task<string> PackageConfigurationAsync(Project project, string projectDirectory, PackagePlanner.Step step, IProgress<string> progress, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            LibraryConfiguration configuration = step.Configuration;
            VCConfiguration vcConfig = ProjectConfigurationManager.GetVCConfig(project, configuration.ConfigurationName, configuration.PlatformName);

            // The build would be postponed if an install were still running (BuildEventsHandler),
            // and its pre-build step fails when the dependencies are not installed yet.
            progress.Report(Loc.T($"{configuration.Name}: installing the dependencies...", $"{configuration.Name} : installation des dépendances..."));
            await Task.WhenAll(ConanInstallTracker.GetPending().Select(pending => pending.JoinAsync()));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!await ProjectConfigurationManager.EnsureConanPackagesInstalledAsync(project, vcConfig, force: false))
            {
                return ProjectConfigurationManager.LastConanInstallError;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Loc.T("Cancelled.", "Annulé.");
            }

            progress.Report(Loc.T($"{configuration.Name}: building...", $"{configuration.Name} : compilation..."));
            (bool built, string buildError) = await VsProjectBuilder.BuildAsync(project, configuration);
            if (!built)
            {
                return buildError;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Loc.T("Cancelled.", "Annulé.");
            }

            progress.Report(Loc.T($"{configuration.Name}: creating the Conan package...", $"{configuration.Name} : création du paquet Conan..."));
            await ProjectConfigurationManager.ConanCacheGate.WaitAsync();
            try
            {
                (bool exported, string exportError) = await ConanProcessRunner.RunStreamingAsync(
                    $"export-pkg . -pr:h=.conan/{configuration.ProfileName} -pr:b=default", projectDirectory, cancellationToken);
                return exported ? null : exportError;
            }
            finally
            {
                ProjectConfigurationManager.ConanCacheGate.Release();
            }
        }

        private static PackageCreationResult Failure(string error)
        {
            return new PackageCreationResult { Error = error };
        }
    }
}
