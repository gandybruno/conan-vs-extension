# Conan Visual Studio Extension for Conan 2

![Build](https://github.com/conan-io/conan-vs-extension/actions/workflows/main.yml/badge.svg?branch=develop2)

Conan, the C and C++ package manager, is now directly integrated into your IDE. List
available libraries, install, and use them without leaving Visual Studio.

- 🔧 **Compatibility:** Designed exclusively for Conan 2.0 and above.
- 🆕 **Completely revamped:** This is a fresh start with no ties to previous versions.
- ❌ **End of support for older versions:** Previous extensions are no longer supported.
- 💻 **Platforms supported:** It is developed for Visual Studio 2022 and beyond.
- ⏫ **Looking Ahead:** We're committed to supporting Conan 2.0 and future versions.
- 📢 **Early stage release:** Your feedback is crucial as we continue to improve 🚀 

## Using the Extension

After installing the Conan extension, you can access it from the “Conan” tool window in
Visual Studio. To do so, go to **View > Other Windows > Conan C/C++ Package Manager**.

#### Initial Configuration

![Initial configuration](https://raw.githubusercontent.com/conan-io/conan-vs-extension/develop2/.github/readme/tool-window-extension.png)

Initially, you will need to configure the Conan executable to be used by the extension. By
clicking on the ⚙️ (wheel) symbol from the extension's window, you can set up the path to
the Conan client executable. You can either specify a custom path or choose to use the
Conan client installed at the system level.

![Select Conan executable](https://raw.githubusercontent.com/conan-io/conan-vs-extension/develop2/.github/readme/select-conan-executable.png)

Once you have configured the Conan client, the extension is ready to use, and you can
start adding libraries to your project.

#### Searching and Adding Libraries

Once configured, the library list in the Conan tool window becomes active, and you can
search for Conan packages using the search bar. Selecting a library will allow you to view
its details, including available versions and integration options.

![Select Conan executable](https://raw.githubusercontent.com/conan-io/conan-vs-extension/develop2/.github/readme/search-packages.png)

If you now click the `Add requirement` button, the extension will add a `conanfile.py` and
a `conandata.yml` to your project with the necessary information to install the selected
Conan packages. It will also add a prebuild event to the project to install those
libraries on the next compilation of the project.

At any point, you can also use the refresh button (🔄) to update the list of available
packages in [Conan Center](https://conan.io/center).

#### Configuring the Package Source

By default, the extension fetches the list of available packages from a JFrog Artifactory
JSON index. It can also be configured to fetch that list from a **GitLab Package Registry**
(Conan) instead. Open **Tools > Options > Conan C/C++ Package Manager** and, under
**Package Source**, choose `GitLab`, then fill in the **Package Source - GitLab** section:

- **GitLab URL**: base URL of the GitLab instance, e.g. `https://gitlab.com` or
  `https://gitlab.mycompany.com`.
- **Project ID or Path**: the numeric GitLab project ID (e.g. `12345678`) or its
  URL-encoded path (e.g. `group%2Fsubgroup%2Fproject`).
- **Private Token**: a Personal or Project Access Token with at least the `read_api`
  scope. Leave empty if the project's package registry is public.

The extension queries the GitLab [packages
API](https://docs.gitlab.com/ee/api/packages.html#for-a-project)
(`GET /api/v4/projects/:id/packages?package_type=conan`, paginated) to list all
recipes and versions published in that project's package registry.

Now, if you initiate the build process for your project, the pre-build event will trigger
Conan to install the packages and inject the necessary properties into the project,
enabling Visual Studio to compile and link against those libraries.

![Build project](https://raw.githubusercontent.com/conan-io/conan-vs-extension/develop2/.github/readme/build-project.png)

> ⚠️ **Warning**: The initial compilation might fail if Visual Studio does not have
> sufficient time to process the injected properties. If this happens, simply rebuild the
> project, and it should build successfully.

## Installation

The Conan Visual Studio Extension can be installed directly from within Visual Studio:

- Open the Extensions menu.
- Select Manage Extensions.
- Search for "Conan" in the Online marketplace.
- Download and install the extension.

Alternatively, you can download the latest release from our [releases
page](https://github.com/conan-io/conan-vs-extension/releases/latest) and install it
manually.

If you have any questions or issues, please refer to our
[documentation](https://docs.conan.io/) or open an issue on our [GitHub
repository](https://github.com/conan-io/conan-vs-extension).
