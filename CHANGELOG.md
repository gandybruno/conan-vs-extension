# Changelog

All notable changes to the Conan Visual Studio Extension are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.0] - 2026-10-10

### Added

- **NuGet-style Package Manager.** Right-click a project (or use the **Project** menu) and
  choose **Manage Conan Packages...** to open a per-project document window with
  **Browse**/**Installed** tabs, a search box and a package details panel.
- **Search box with history.** Editable combo box with a magnifying-glass button,
  placeholder text and a dropdown of recent searches.
- **Package Sources options page** (Tools > Options > Conan Package Manager > Package
  Sources): add, remove, enable/disable and edit Conan remotes, NuGet-style.
- **Remote authentication.** Log in/out of a remote (`conan remote login`/`logout`) from the
  Package Sources page; the password is sent to `conan` through its environment, never as a
  command-line argument. The authenticated user is shown for each remote.
- **Multi-remote support.** The package list now comes from the remotes already configured
  in Conan (`conan remote list`), selectable from a **Source** dropdown.
- **GitLab Package Registry** support, including a fallback on GitLab's packages API and a
  per-remote access token for private projects.
- **"Add a remote" dialog** with a GitLab mode (server + project ID/path, optional user and
  token) that builds the Conan URL and logs in for you.
- **On-demand recipe metadata** (description, license, homepage, author, topics) with cache
  cleanup; fetch failures are reported in the UI and the Output window.
- **Conan Output pane.** Every `conan` command run by the extension is logged to a
  dedicated "Conan" pane of the Output window, which shows itself automatically. `conan
  install` output (including compilation of missing binaries) is streamed live.
- **Proactive installs** when a solution is opened, a project is added or the active
  profile/configuration changes.
- **Offline fallback.** When the selected source is unreachable, the list falls back to the
  local Conan cache and a banner explains why.
- **Progress overlay** while installing or uninstalling a package.
- Support for Visual Studio 18 (`v145` toolset) in Conan profile generation.

### Changed

- **Works out of the box.** "Use System Conan" is now the default: the extension uses `conan` from the system PATH and the Package Manager is only locked when Conan cannot be found. ConanCenter is selected by default as package source.
- The build is automatically postponed and re-triggered once a running Conan
  install/update finishes, instead of racing with it.
- Every `conan` process call is now bounded by a timeout, so a hung command can no longer
  freeze the UI indefinitely.
- `conandata.yml` parsing is hardened and duplicate requirements are prevented.
- The generated `conan_install.bat` no longer fails when `version.txt` is absent.
- Restyled toolbar icons to match Visual Studio's flat look.
- The code base is documented with Doxygen-style comments.
- Async-void event handlers were replaced by `JoinableTaskFactory`-based handlers and the
  VSTHRD / NU1603 build warnings were fixed.

### Fixed

- Uninstalling a package could silently do nothing: the installed version is now read
  from `conandata.yml` instead of the version picker.
- Both tabs could get stuck loading forever when a remote was slow or hung.
- The details panel is now cleared after a package is removed.
- The fields to add a remote on the Package Sources page could not be typed into (keyboard
  input is not delivered to WPF text boxes nested in the Options dialog); remotes are now
  added through a dialog.
- The remote selection was not remembered, and the window was empty when re-opened.

### Removed

- The legacy docked tool window (View > Other Windows > Conan C/C++ Package Manager),
  superseded by the per-project **Manage Conan Packages...** window.
- The manual JFrog/GitLab source configuration and its hard-coded defaults, replaced by the Conan remotes list.
- The hard-coded recipe link in the package details panel.

## [2.0.10] - upstream

See the [upstream releases](https://github.com/conan-io/conan-vs-extension/releases) for
the history up to 2.0.10.

[2.1.0]: https://github.com/gandybruno/conan-vs-extension/releases/tag/v2.1.0
[2.0.10]: https://github.com/conan-io/conan-vs-extension/releases/tag/v2.0.10
