using conan_vs_extension;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/**
 * Tools > Options > Conan Package Manager > General - lets the user point the extension at
 * a specific `conan` executable (or the one on the system PATH). The package source itself
 * (remote URLs, authentication...) is configured separately, per Conan remote, via
 * ConanPackageSourcesOptionsPage - the properties on this page whose
 * `[Browsable(false)]`-marked below exist only to reuse DialogPage's built-in
 * persistence for internal state that also needs to survive across Visual Studio sessions.
 */
[Guid(GuidList.strConanOptionsPage)]
public class ConanOptionsPage : DialogPage
{
    // Default configuration: use the Conan found on the system PATH.
    private string _conanExecutablePath = GlobalSettings.SystemConanCommand;
    private bool _useSystemConan = true;

    private string _selectedPackageSourceName = string.Empty;
    private string _gitLabRemoteTokensRaw = string.Empty;

    /**
     * Refreshes every field from GlobalSettings right before the Options dialog is shown, so
     * values entered purely at runtime this session - the selected package source, per-remote
     * tokens typed into the "browse" prompt - show up here and get
     * persisted to disk if the user clicks OK, instead of only living in memory for the
     * current Visual Studio session.
     */
    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);

        _conanExecutablePath = GlobalSettings.GetResolvedConanExecutablePath();
        _useSystemConan = _conanExecutablePath == GlobalSettings.SystemConanCommand;
        _selectedPackageSourceName = GlobalSettings.SelectedPackageSourceName;
        _gitLabRemoteTokensRaw = GlobalSettings.GitLabRemoteTokensRaw;
    }

    [Category("Conan")]
    [DisplayName("Executable Path")]
    [Description("Path to the Conan executable.")]
    [Editor(typeof(ExecutablePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
    public string ConanExecutablePath
    {
        get => _conanExecutablePath;
        set
        {
            _conanExecutablePath = value?.Trim();
            GlobalSettings.ConanExecutablePath = value;

            if (value != GlobalSettings.SystemConanCommand)
            {
                _useSystemConan = false;
            }
        }
    }

    [Category("Conan")]
    [DisplayName("Use System Conan")]
    [Description("Specify whether to use the Conan executable installed in the system.")]
    public bool UseSystemConan
    {
        get => _useSystemConan;
        set
        {
            _useSystemConan = value;
            if (_useSystemConan)
            {
                _conanExecutablePath = GlobalSettings.SystemConanCommand;
                GlobalSettings.ConanExecutablePath = GlobalSettings.SystemConanCommand;
            }

            if (!_useSystemConan && _conanExecutablePath == GlobalSettings.SystemConanCommand)
            {
                _conanExecutablePath = string.Empty;
                GlobalSettings.ConanExecutablePath = "";
            }
        }
    }

    // Not shown in the Options property grid - internal state persisted across sessions the same
    // way as the visible properties (DialogPage persists any public property by default). The
    // package source itself is no longer manually configured here: it comes entirely from
    // `conan remote list` (see ConanRemoteHelper / ConanPackageManagerControl).

    [Browsable(false)]
    public string SelectedPackageSourceName
    {
        get => _selectedPackageSourceName;
        set
        {
            _selectedPackageSourceName = value;
            GlobalSettings.SelectedPackageSourceName = value;
        }
    }

    [Browsable(false)]
    public string GitLabRemoteTokensRaw
    {
        get => _gitLabRemoteTokensRaw;
        set
        {
            _gitLabRemoteTokensRaw = value;
            GlobalSettings.GitLabRemoteTokensRaw = value;
        }
    }

    /**
     * Backs the "..." browse button the property grid renders for ConanExecutablePath, opening a standard "choose an .exe" file picker.
     */
    public class ExecutablePathEditor : System.Drawing.Design.UITypeEditor
    {
        public override System.Drawing.Design.UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
        {
            return System.Drawing.Design.UITypeEditorEditStyle.Modal;
        }

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*";
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    return openFileDialog.FileName;
                }
            }
            return value;
        }
    }
}
