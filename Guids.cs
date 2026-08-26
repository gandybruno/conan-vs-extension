namespace conan_vs_extension
{
	static class GuidList
	{
		public const string strConanOptionsPage = "188e5905-b7fe-4f68-a738-46e4a698caac";

		// "Conan Package Manager > Package Sources" options page - the NuGet-style remote
		// management grid (add/remove/update/enable/disable via `conan remote ...`).
		public const string strConanPackageSourcesOptionsPage = "6f2a0b3d-9e4c-4a1f-8b7d-2c5e6a9f1d34";

		// Opaque "document kind" tag passed to IVsUIShell.CreateDocumentWindow when opening the
		// NuGet-style Conan Package Manager as a document window. This does NOT require a
		// registered IVsEditorFactory: NuGet itself uses this same pattern (guidNuGetEditorType)
		// purely as a tag, hosting its WPF control directly via the punkDocView/punkDocData COM
		// pointers instead of going through the editor-factory mechanism.
		public const string strConanPackageManagerEditorType = "5b9a1f3e-6b1a-4dfb-9c2e-8f6a2d9e5a11";
	};
}
