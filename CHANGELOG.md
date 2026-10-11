# Changelog

All notable changes to the Conan Visual Studio Extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.0] - 2026-10-10

### Added

- **NuGet-style Package Manager.** Right-click a project (or use the **Project** menu) and
  choose **Manage Conan Packages...** to open a per-project document window with
  **Browse**/**Installed** tabs, a search box and a package details panel. **Installed** lists
  the project's own dependencies (from `conandata.yml`) even when the selected source does not
  know them (local, editable, other remote), and **Retirer du projet** removes one without
  touching the Conan cache.
- **Search box with history.** Editable combo box with a magnifying-glass button,
  placeholder text and a dropdown of recent searches.
- **Package Sources options page** (Tools > Options > Conan Package Manager > Package
  Sources): add, remove, enable/disable and edit Conan remotes, NuGet-style, and clean the
  local Conan cache (`conan remove "*" -c`, after confirmation).
- **"Add a remote" dialog** with a GitLab mode (server + project ID/path, optional user and
  token) that builds the Conan URL and logs in for you.
- **Remote authentication.** Log in/out of a remote (`conan remote login`/`logout`) from the
  Package Sources page; the password is sent to `conan` through its environment, never as a
  command-line argument. The authenticated user is shown for each remote.
- **Multi-remote support.** The package list now comes from the remotes already configured
  in Conan (`conan remote list`), selectable from a **Source** dropdown that follows remotes
  added or removed from the Options page.
- **GitLab Package Registry** support, including a fallback on GitLab's packages API and a
  per-remote access token for private projects.
- **Compatibility check before installing.** Selecting a package/version resolves it against
  the project's profile (`conan graph info`) and shows whether a prebuilt binary matches, it
  will be built from source (and where the sources come from: included in the package or
  downloaded by the recipe), or the recipe refuses the configuration or has no source at all -
  in which case **Installer** is disabled with the reason. The Windows binaries the source
  publishes (compiler version, build type, runtime, architecture) are listed too.
- **Editable packages.** An installed package has an **Éditable** checkbox in the details
  panel: checking it asks for the folder of the library's sources (`conan editable add`) so
  the project builds against them and the library can be modified and debugged; unchecking
  goes back to the cache. The state is read from Conan each time it is displayed (it is global
  to the Conan home) and marked with a pencil in the package list; **Dossier...** changes the
  folder. Requirements are written as plain `name/version` (never `@_/_`, which Conan 2 does
  not match to an editable package); existing files are normalized and real `@user/channel`
  references are kept.
- **IntelliSense follows the dependencies.** Empty `conandeps.props` files are created before
  the first `conan install`, their imports are made unconditional, and whenever an install
  changes the generated property sheets the extension runs Visual Studio's own
  **Project > Rescan Solution**, so no project unload/reload is needed.
- **On-demand recipe metadata** (description, license, homepage, author, topics); a package
  already in the Conan cache is left untouched. Fetch failures are reported in the UI and the
  Output window.
- **Conan Output pane.** Every `conan` command run by the extension is logged to a
  dedicated "Conan" pane of the Output window, which shows itself automatically. `conan
  install` output (including compilation of missing binaries) is streamed live.
- **Proactive installs** when a solution is opened, a project is added or the active
  profile/configuration changes. Installs are serialized, and a recipe left broken in the
  Conan cache is removed and the install retried once.
- **Offline fallback.** When the selected source is unreachable (including a remote that
  answers with an error entry), the list falls back to the local Conan cache and a banner
  explains why.
- **English and French.** Every text of the extension (package manager, Package Sources page,
  dialogs, messages, Output pane) is shown in the language of the Visual Studio UI: French when
  Visual Studio runs in French, English otherwise.
- **Progress overlay** while installing or uninstalling a package.
- Support for Visual Studio 18 (`v145` toolset) in Conan profile generation. The profile's
  `compiler.version` comes from the MSVC toolset the project really uses
  (`$(VCToolsVersion)`, e.g. 14.44 -> 194); a platform toolset that differs from the one
  Visual Studio provides by default is reported in the Conan Output pane.

### Changed

- **Works out of the box.** "Use System Conan" is now the default: the extension uses `conan` from the system PATH and the Package Manager is only locked when Conan cannot be found. ConanCenter is selected by default as package source.
- The build is automatically postponed and re-triggered once a running Conan
  install/update finishes, instead of racing with it.
- Every `conan` process call is now bounded by a timeout, so a hung command can no longer
  freeze the UI indefinitely.
- `conandata.yml` parsing is hardened and duplicate requirements are prevented.
- The generated `conan_install.bat` no longer fails when `version.txt` is absent.
- Restyled toolbar icons to match Visual Studio's flat look; the dialogs opened by the
  extension use the colors of the current Visual Studio theme.
- The code base is documented with Doxygen-style comments.
- Async-void event handlers were replaced by `JoinableTaskFactory`-based handlers and the
  VSTHRD / NU1603 build warnings were fixed.

### Fixed

- Uninstalling a package could silently do nothing: the installed version is now read
  from `conandata.yml` instead of the version picker.
- Both tabs could get stuck loading forever when a remote was slow or hung.
- The details panel is now cleared after a package is removed.
- The remote selection was not remembered, and the window was empty when re-opened.

### Removed

- The legacy docked tool window (View > Other Windows > Conan C/C++ Package Manager),
  superseded by the per-project **Manage Conan Packages...** window.
- The manual JFrog/GitLab source configuration and its hard-coded defaults, replaced by the Conan remotes list.

## [2.0.10] - upstream

See the [upstream releases](https://github.com/conan-io/conan-vs-extension/releases) for
the history up to 2.0.10.

[2.1.0]: https://github.com/gandybruno/conan-vs-extension/releases/tag/v2.1.0
[2.0.10]: https://github.com/conan-io/conan-vs-extension/releases/tag/v2.0.10
