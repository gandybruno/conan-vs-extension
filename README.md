# Conan Visual Studio Extension for Conan 2

![Build](https://github.com/gandybruno/conan-vs-extension/actions/workflows/main.yml/badge.svg?branch=develop)

Conan, the C and C++ package manager, is now directly integrated into your IDE. List
available libraries, install, and use them without leaving Visual Studio.

- 🔧 **Compatibility:** Designed exclusively for Conan 2.0 and above.
- 💻 **Platforms supported:** It is developed for Visual Studio 2022 and beyond.
- ⏫ **Looking Ahead:** We're committed to supporting Conan 2.0 and future versions.
- 📢 **Early stage release:** Your feedback is crucial as we continue to improve 🚀

## What's new in 2.1.0

> The full history is in [CHANGELOG.md](CHANGELOG.md).

This release is a large pass of bug fixes and quality-of-life improvements on top of the
NuGet-style Package Manager introduced previously:

- **Conan Output pane.** Every `conan` command the extension runs (install, list, remote
  management, recipe lookups...) is now logged to a dedicated "Conan" pane in Visual
  Studio's Output window, which surfaces itself automatically as soon as something is
  logged - the same way CMake Tools' own pane does. `conan install` output (including
  compiler output while building a missing binary) streams in line-by-line as it happens
  instead of only appearing once the command finishes.
- **The build now waits for Conan.** Starting a Build while a Conan install/update is
  still running used to race with it and could crash Visual Studio or fail the link step.
  The build is now automatically postponed and re-triggered once Conan finishes, for both
  automatic installs (opening a solution, adding a project, switching configuration) and
  manual ones (the Install/Uninstall buttons).
- **Visible progress during Install/Uninstall.** Installing or removing a package now
  shows a full progress overlay instead of leaving the window looking frozen - useful
  since Conan can take several minutes to compile a missing binary from source.
- **Offline fallback.** If the selected package source can't be reached, the package list
  now falls back to what Conan's local cache genuinely has available, with a visible
  banner explaining why - instead of silently showing a stale list left over from a
  previous session.
- **Fixed: uninstalling a package could silently do nothing.** The Uninstall button now
  looks up the actually-installed version from `conandata.yml` instead of trusting the
  version picker (which lists remote-available versions, not the installed one).
- **Fixed: both tabs could get stuck loading forever.** Every `conan` process call is now
  bounded by a timeout, and the "Installé" tab (which only reads local files) no longer
  waits on a slow/hung fetch for the "Parcourir" tab.
- **NuGet-style search box.** The plain search field is now an editable combo box with a
  magnifying-glass button, placeholder text, and a dropdown of your most recent searches
  (Enter or the magnifying glass commits a search to that history); the refresh button now
  sits right next to it.
- **Package Sources page can manage authentication.** Tools > Options > Conan Package
  Manager > Package Sources can now log in/out of a remote (`conan remote login`/`logout`)
  directly, showing which user (if any) is currently authenticated on each one. The
  password is sent to `conan` through the environment of the `conan` process, never as a command-line
  argument.
- Restyled icons (Actualiser/Configurer) to sit flush with the toolbar, matching NuGet's
  own flat look.
- **Removed the legacy docked tool window.** The original single-source package browser
  (View > Other Windows > Conan C/C++ Package Manager) has been deleted: it duplicated the
  per-project window with none of the fixes above (including the uninstall bug), and had
  no reason to keep existing once every project got its own **Manage Conan Packages...**
  window.

## Using the Extension

### Managing packages for a project

The primary way to use the extension is per-project, the same way NuGet's own "Manage
NuGet Packages..." works: right-click a project in Solution Explorer (or use the
**Project** menu) and choose **Manage Conan Packages...**. This opens a document window
(an editor tab) with **Parcourir**/**Installé** tabs, a search box, and package details -
scoped to that one project.

> The older docked tool window (**View > Other Windows > Conan C/C++ Package Manager**)
> has been removed in 2.1.0 - it predated the per-project window above, duplicated its
> functionality without most of its fixes (including a known uninstall bug), and is no
> longer needed now that every project has its own **Manage Conan Packages...** window.

#### Initial Configuration

There is nothing to configure if `conan` is on your system PATH: **Use System Conan** is
checked by default and **conancenter** is selected as the package source. If Conan cannot
be found the window is locked with a message; open **Tools > Options > Conan Package
Manager > General**, uncheck "Use System Conan" and point it at a specific `conan.exe`.
Clicking the ⚙️ (gear) icon in the package manager window opens the same page directly.

![Select Conan executable](.github/readme/select-conan-executable.png)

#### Searching and Adding Libraries

Once configured, search for a package by typing in the search box - the list filters as
you type, and pressing Enter (or clicking the magnifying glass) adds the term to a
"recent searches" dropdown for later. Selecting a library shows its available versions
and details (description, license, author, topics, when the recipe exposes them).

Clicking **Installer** adds the selected package/version as a requirement (writing
`conanfile.py` and `conandata.yml` if this is the project's first Conan package) and runs
`conan install` right away, so the very next Build already has everything it needs -
progress is shown in a full-window overlay, and the detailed command output appears
automatically in the **Conan** pane of the Output window. **Désinstaller** removes a
package the same way.

At any point, use the refresh (🔄) button next to the search box to re-fetch the package
list from the currently selected source.

#### Removing and editing libraries

The **Installed** tab lists the dependencies of the current project (its `conandata.yml`),
whether or not the selected source knows them. **Retirer du projet** removes one from the
project and re-runs `conan install`; the package stays in the Conan cache, so other projects
are not affected.

To work on the sources of a library your project uses, select it in **Installed** and check
**Éditable**, then pick the folder holding the library's `conanfile.py`. This runs
`conan editable add` and re-runs `conan install`, so the project is built against the local
sources instead of the package from the Conan cache. The folder is shown under the package
name, a pencil marks editable packages in the list, **Dossier...** changes the folder and
unchecking the box goes back to the regular package.

Requirements (Conan's, not the extension's): the recipe must define `layout()`, and the
library must have been built once in its own folder (`conan build .`) so its generated files
exist. Editable mode is stored in the Conan home, so it applies to every project on the
machine that uses that `name/version`.

The Conan cache itself can be emptied from **Tools > Options > Conan Package Manager >
Package Sources** (**Nettoyer le cache...**).

#### Configuring Package Sources

The list of available packages comes entirely from the Conan remotes already configured
on your machine (`conan remote list`) - there is no separate, plugin-specific source
configuration to maintain. The **Source** dropdown in the package manager window lets you
switch between them, and **Tools > Options > Conan Package Manager > Package Sources**
gives you a NuGet-style grid to add, remove, enable/disable, or edit a remote's URL,
equivalent to running `conan remote add/remove/enable/disable/update` yourself.

That same page can also authenticate you on a remote: select it and click **Se
connecter...** to run `conan remote login` (your password is handed to `conan` through its environment,
never passed as a command-line argument), or **Se déconnecter** to log out
(`conan remote logout`). The **Utilisateur** column shows who, if anyone, is currently
authenticated on each remote.

**Adding a remote:** click **Ajouter un remote...** and fill in the dialog.

- *Remote Conan (URL)*: a name and the URL of any Conan remote (Artifactory, a Conan
  server, ...).
- *GitLab Package Registry*: a name, the GitLab server (default `https://gitlab.com`) and
  the project's numeric ID (Settings > General) or its path (`group/project`). The dialog
  builds the Conan URL `<server>/api/v4/projects/<id>/packages/conan` for you. For a private
  project also give your username and an access token (Personal, Project or Deploy token)
  with the `read_api` and `read_package_registry` scopes; the extension logs you in on the
  remote (`conan remote login`) and keeps the token for the package listing. Leave both empty
  for a public project.

The same remote from a terminal:

```
conan remote add my-gitlab https://gitlab.com/api/v4/projects/<project-id>/packages/conan
conan remote login my-gitlab <username> -p <token>
```

For a remote pointing at a GitLab Package Registry project, the extension additionally
queries GitLab's own [packages
API](https://docs.gitlab.com/ee/api/packages.html#for-a-project) as a fallback, since
GitLab's Conan v2 endpoint does not support a bare wildcard search. If that project is
private, you'll be prompted once for the token (`read_api` scope) if it was not given when
adding the remote; it is remembered per remote afterwards.

#### Working offline

If the selected source can't be reached, the package list automatically falls back to
what's already in your local Conan cache (packages you've downloaded before), with a
banner making clear you're looking at cached data rather than a live view of the remote.

### Tracking what the extension is doing

Every `conan` command the extension runs - proactive installs when a solution opens or a
project's configuration changes, manual installs/removals, remote list refreshes, recipe
metadata lookups - is logged to a dedicated **Conan** pane of the Output window
(**View > Output**, then pick "Conan" from the "Show output from" dropdown). That pane
pops into view on its own as soon as something is logged, the same way the Build or CMake
panes do, so you don't need to go looking for it.

## Installation

The Conan Visual Studio Extension can be installed directly from within Visual Studio:

- Open the Extensions menu.
- Select Manage Extensions.
- Search for "Conan" in the Online marketplace.
- Download and install the extension.
