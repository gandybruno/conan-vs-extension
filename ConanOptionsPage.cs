using conan_vs_extension;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/**
 * Tools > Options > Conan Package Manager > General - lets the user point the extension at
 * a specific `conan` executable (or the one on the system PATH). The package source itself
 * (JFrog/GitLab URL, tokens...) is configured separately, per Conan remote, via
 * ConanPackageSourcesOptionsPage - the properties on this page whose
 * `[Browsable(false)]`-marked below exist only to reuse DialogPage's built-in
 * persistence for internal state that also needs to survive across Visual Studio sessions.
 */
[Guid(GuidList.strConanOptionsPage)]
public class ConanOptionsPage : DialogPage
{
    private string _conanExecutablePath;
    private bool _useSystemConan;

    private string _selectedPackageSourceName = string.Empty;
    private string _gitLabRemoteTokensRaw = string.Empty;

    /**
     * Refreshes every field from GlobalSettings right before the Options dialog is shown, so
     * values discovered/entered purely at runtime this session - the auto-detected GitLab
     * URL/project ID, per-remote tokens typed into the "browse" prompt - show up here and get
     * persisted to disk if the user clicks OK, instead of only living in memory for the
     * current Visual Studio session.
     */
    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);

        _conanExecutablePath = GlobalSettings.ConanExecutablePath;
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

            if (value != "conan")
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
                _conanExecutablePath = "conan";
                GlobalSettings.ConanExecutablePath = "conan";
            }

            if (!_useSystemConan && _conanExecutablePath == "conan")
            {
                _conanExecutablePath = "";
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
