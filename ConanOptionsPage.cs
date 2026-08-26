using conan_vs_extension;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[Guid(GuidList.strConanOptionsPage)]
public class ConanOptionsPage : DialogPage
{
    private string _conanExecutablePath;
    private bool _useSystemConan;

    private PackageRegistrySource _packageSource = PackageRegistrySource.JFrog;
    private string _jFrogPackagesUrl = GlobalSettings.JFrogPackagesUrl;
    private string _gitLabUrl = string.Empty;
    private string _gitLabProjectId = string.Empty;
    private string _gitLabPrivateToken = string.Empty;

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

    [Category("Package Source")]
    [DisplayName("Source")]
    [Description("Where the list of available packages is fetched from: a JFrog Artifactory JSON index, or a GitLab Package Registry (Conan) project.")]
    public PackageRegistrySource PackageSource
    {
        get => _packageSource;
        set
        {
            _packageSource = value;
            GlobalSettings.PackageSource = value;
        }
    }

    [Category("Package Source - JFrog")]
    [DisplayName("Packages JSON URL")]
    [Description("URL of the packages.ref.json index served by JFrog Artifactory. Only used when Source is set to JFrog.")]
    public string JFrogPackagesUrl
    {
        get => _jFrogPackagesUrl;
        set
        {
            _jFrogPackagesUrl = value?.Trim();
            GlobalSettings.JFrogPackagesUrl = value;
        }
    }

    [Category("Package Source - GitLab")]
    [DisplayName("GitLab URL")]
    [Description("Base URL of the GitLab instance hosting the Package Registry, e.g. https://gitlab.com or https://gitlab.mycompany.com. Only used when Source is set to GitLab.")]
    public string GitLabUrl
    {
        get => _gitLabUrl;
        set
        {
            _gitLabUrl = value?.Trim();
            GlobalSettings.GitLabUrl = value;
        }
    }

    [Category("Package Source - GitLab")]
    [DisplayName("Project ID or Path")]
    [Description("Numeric GitLab project ID (e.g. 12345) or its URL-encoded path (e.g. group%2Fsubgroup%2Fproject). Only used when Source is set to GitLab.")]
    public string GitLabProjectId
    {
        get => _gitLabProjectId;
        set
        {
            _gitLabProjectId = value?.Trim();
            GlobalSettings.GitLabProjectId = value;
        }
    }

    [Category("Package Source - GitLab")]
    [DisplayName("Private Token")]
    [Description("GitLab Personal or Project Access Token with at least the 'read_api' scope. Leave empty for a public project.")]
    [PasswordPropertyText(true)]
    public string GitLabPrivateToken
    {
        get => _gitLabPrivateToken;
        set
        {
            _gitLabPrivateToken = value?.Trim();
            GlobalSettings.GitLabPrivateToken = value;
        }
    }

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
