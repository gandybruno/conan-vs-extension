
import os

from conan import ConanFile
from conan.errors import ConanException
from conan.tools.build import supported_cppstd
from conan.tools.files import copy, load
from conan.tools.microsoft import vs_layout, MSBuildDeps

# Everything specific to the library lives in conandata.yml ("package" section, written by the
# Package tab of the extension) and version.txt: this recipe is the same for every project.

HEADER_PATTERNS = ("*.h", "*.hh", "*.hpp", "*.hxx", "*.h++", "*.inl", "*.ipp", "*.tpp")

# MSVC toolsets that are binary compatible with each other (Visual Studio 2015 and later).
MSVC_COMPATIBLE_VERSIONS = ("190", "191", "192", "193", "194", "195")


class ConanLibrary(ConanFile):
    settings = "os", "compiler", "build_type", "arch"
    exports = "version.txt"

    @property
    def _package(self):
        return (self.conan_data or {}).get("package") or {}

    @property
    def _is_shared(self):
        return self._package.get("type") == "shared-library"

    @property
    def _output_dir(self):
        """Folder (relative to the project) where Visual Studio writes the binaries of the
        configuration being packaged."""
        key = "%s_%s" % (self.settings.get_safe("build_type"), self.settings.get_safe("arch"))
        return (self._package.get("output_dirs") or {}).get(key)

    def init(self):
        package = self._package
        self.package_type = package.get("type") or "static-library"
        self.license = package.get("license")
        self.description = package.get("description")

    def set_name(self):
        self.name = self.name or self._package.get("name")

    def set_version(self):
        self.version = self.version or load(self, os.path.join(self.recipe_folder, "version.txt")).strip()

    def requirements(self):
        for requirement in self.conan_data.get("requirements") or []:
            self.requires(requirement.replace("@_/_", ""))

    def layout(self):
        vs_layout(self)
        # Editable mode: consumers read the headers and the binaries straight from the project.
        self.cpp.source.includedirs = [self._package.get("headers") or "include"]
        if self._output_dir:
            self.cpp.build.libdirs = [self._output_dir]
            self.cpp.build.bindirs = [self._output_dir]

    def generate(self):
        deps = MSBuildDeps(self)
        deps.generate()

    def compatibility(self):
        # A binary built with an older MSVC toolset can be linked by a newer one, never the
        # other way round, and not when it was compiled with /GL (whole program optimization).
        if not self._package.get("toolset_compatibility"):
            return []
        if self.settings.get_safe("compiler") != "msvc":
            return []
        if str(self.settings.build_type) in (self._package.get("ltcg_build_types") or []):
            return []

        version = str(self.settings.compiler.version)
        if version not in MSVC_COMPATIBLE_VERSIONS:
            return []
        older = MSVC_COMPATIBLE_VERSIONS[:MSVC_COMPATIBLE_VERSIONS.index(version)]

        cppstd = self.settings.get_safe("compiler.cppstd")
        compatibles = []
        for candidate in reversed(older):
            if cppstd is None:
                compatibles.append({"settings": [("compiler.version", candidate)]})
                continue
            standards = supported_cppstd(self, compiler_version=candidate) or [cppstd]
            for standard in [cppstd] + [s for s in standards if s != cppstd]:
                compatibles.append({"settings": [("compiler.version", candidate),
                                                 ("compiler.cppstd", standard)]})
        return compatibles

    def package(self):
        package = self._package
        headers = os.path.join(self.source_folder, package.get("headers") or "include")
        for pattern in HEADER_PATTERNS:
            copy(self, pattern, src=headers, dst=os.path.join(self.package_folder, "include"))

        output_dir = os.path.join(self.source_folder, self._output_dir or "")
        libs = package.get("libs") or [self.name]
        binaries = os.path.join(self.package_folder, "bin" if self._is_shared else "lib")
        for lib in libs:
            copied = copy(self, lib + ".lib", src=output_dir,
                          dst=os.path.join(self.package_folder, "lib"), keep_path=False)
            if not copied:
                raise ConanException(
                    "%s.lib not found in '%s': build the %s configuration of the project first."
                    % (lib, output_dir, self.settings.build_type))
            if self._is_shared:
                copy(self, lib + ".dll", src=output_dir, dst=binaries, keep_path=False)
            if package.get("pdb", True):
                copy(self, lib + ".pdb", src=output_dir, dst=binaries, keep_path=False)

    def package_info(self):
        self.cpp_info.libs = self._package.get("libs") or [self.name]
