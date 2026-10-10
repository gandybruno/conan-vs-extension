using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /**
     * NuGet-style "Package Sources" grid, hosted inside Tools > Options > Conan Package
     * Manager > Package Sources (see ConanPackageSourcesOptionsPage). Unlike
     * NuGet's sources - which NuGet itself stores in NuGet.Config - Conan's remotes are entirely
     * owned and persisted by Conan itself (`conan remote add/remove/update/enable/disable`), so
     * every action here just shells out to the same CLI a terminal would use, then re-reads
     * `conan remote list` to refresh the grid. There is no separate plugin-side storage to keep in
     * sync - Conan's own remotes.json is the single source of truth.
     */
    public partial class ConanPackageSourcesControl : UserControl
    {
        private readonly ObservableCollection<ConanRemoteHelper.ConanRemote> _remotes =
            new ObservableCollection<ConanRemoteHelper.ConanRemote>();

        private ConanRemoteHelper.ConanRemote _selected;

        public ConanPackageSourcesControl()
        {
            InitializeComponent();
            RemotesDataGrid.ItemsSource = _remotes;

            // This control is hosted inside the Tools > Options dialog via an ElementHost/WinForms
            // bridge - a separate WPF tree that the shell's merged theme resource dictionaries
            // never reach, so every DynamicResource lookup in this control's XAML (VsBrushes.WindowKey
            // and friends) silently fails to resolve. Trying to track Visual Studio's actual current
            // theme here (via VSColorTheme.GetThemedColor) turned out unreliable in this specific
            // hosting context - it kept coming back dark regardless of the user's theme. Since every
            // other page of this plugin (the main tool window, etc.) already renders in light mode
            // for this user, this page is simply forced to the same fixed light palette instead of
            // trying (and failing) to dynamically match the shell's theme.
            ApplyLightTheme();

            Loaded += (s, e) => _ = RefreshRemotesAsync();
        }

        /**
         * Injects a fixed light color palette as local resources under the same VsBrushes keys the XAML's DynamicResource bindings reference (see the constructor's remarks for why).
         */
        private void ApplyLightTheme()
        {
            var windowBrush = new SolidColorBrush(Colors.White);
            var textBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            var borderBrush = new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3));
            var highlightBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            var highlightTextBrush = new SolidColorBrush(Colors.White);

            windowBrush.Freeze();
            textBrush.Freeze();
            borderBrush.Freeze();
            highlightBrush.Freeze();
            highlightTextBrush.Freeze();

            // Overriding the resource under the SAME key VsBrushes.XxxKey the XAML's
            // {DynamicResource {x:Static vsshell:VsBrushes.XxxKey}} bindings use lets those
            // lookups resolve locally against this control's own Resources dictionary, instead of
            // needing to reach the (unreachable, from this ElementHost-hosted tree) shell tree.
            Resources[VsBrushes.WindowKey] = windowBrush;
            Resources[VsBrushes.WindowTextKey] = textBrush;
            Resources[VsBrushes.ActiveBorderKey] = borderBrush;
            Resources[VsBrushes.HighlightKey] = highlightBrush;
            Resources[VsBrushes.HighlightTextKey] = highlightTextBrush;

            Background = windowBrush;
            Foreground = textBrush;
        }

        /**
         * Reloads the grid from `conan remote list` + `conan remote list-users`, preserving the current selection by name if it still exists afterwards.
         */
        private async Task RefreshRemotesAsync()
        {
            var remotes = await ConanRemoteHelper.ListRemotesAsync();
            var users = await ConanRemoteHelper.ListRemoteUsersAsync();

            foreach (var remote in remotes)
            {
                remote.User = users.TryGetValue(remote.Name, out string user) ? user : null;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string selectedName = _selected?.Name;

            _remotes.Clear();
            foreach (var remote in remotes)
            {
                _remotes.Add(remote);
            }

            RemotesDataGrid.SelectedItem = string.IsNullOrEmpty(selectedName)
                ? null
                : _remotes.FirstOrDefaultByName(selectedName);
        }

        /**
         * Enables/disables the action buttons and updates their labels ("Activer"/"Désactiver", "Se connecter..."/"Se déconnecter") based on the newly-selected remote.
         */
        private void RemotesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = RemotesDataGrid.SelectedItem as ConanRemoteHelper.ConanRemote;

            bool hasSelection = _selected != null;
            LoginButton.IsEnabled = hasSelection;
            EnableDisableButton.IsEnabled = hasSelection;
            EditSourceButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
            EnableDisableButton.Content = hasSelection && !_selected.Enabled ? "Activer" : "Désactiver";
            LoginButton.Content = hasSelection && !string.IsNullOrEmpty(_selected.User) ? "Se déconnecter" : "Se connecter...";
        }

        /**
         * The "Se connecter..."/"Se déconnecter" button: if the selected remote already has a
         * logged-in user, confirms and runs `conan remote logout`; otherwise prompts for
         * credentials (CredentialsDialog) and runs `conan remote login` (the
         * password is handed to the child process through its environment, never passed as a command-line
         * argument - see ConanRemoteHelper.LoginRemoteAsync).
         */
        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => LoginButtonClickAsync(sender, e));
        }

        private async Task LoginButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_selected.User))
            {
                MessageBoxResult confirmLogout = MessageBox.Show(
                    $"Se déconnecter du remote '{_selected.Name}' (utilisateur '{_selected.User}') ?",
                    "Conan C/C++ Package Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirmLogout != MessageBoxResult.Yes)
                {
                    return;
                }

                bool logoutSuccess;
            string logoutError;
            using (BusyIndicator.Show(this, "Op\u00e9ration Conan en cours..."))
            {
                (logoutSuccess, logoutError) = await ConanRemoteHelper.LogoutRemoteAsync(_selected.Name);
            }
                if (!logoutSuccess)
                {
                    MessageBox.Show($"Impossible de se déconnecter de '{_selected.Name}' :\n{logoutError}", "Conan C/C++ Package Manager",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await RefreshRemotesAsync();
                return;
            }

            (string username, string password)? credentials = CredentialsDialog.Show(
                $"Authentification sur le remote '{_selected.Name}' :", _selected.Name);

            if (credentials == null)
            {
                return;
            }

            bool loginSuccess;
            string loginError;
            using (BusyIndicator.Show(this, "Authentification en cours (peut prendre quelques secondes)..."))
            {
                (loginSuccess, loginError) = await ConanRemoteHelper.LoginRemoteAsync(_selected.Name, credentials.Value.username, credentials.Value.password);
            }
            if (!loginSuccess)
            {
                MessageBox.Show($"Authentification impossible sur '{_selected.Name}' :\n{loginError}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }

        /**
         * The "Ajouter un remote..." button: prompts for the remote (AddRemoteDialog), runs `conan remote add` and, for GitLab, stores the token and logs in.
         */
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => AddButtonClickAsync(sender, e));
        }

        private async Task AddButtonClickAsync(object sender, RoutedEventArgs e)
        {
            NewRemoteInfo info = AddRemoteDialog.Show();
            if (info == null)
            {
                return;
            }

            bool success;
            string error;
            using (BusyIndicator.Show(this, "Ajout du remote en cours..."))
            {
                (success, error) = await ConanRemoteHelper.AddRemoteAsync(info.Name, info.Url, info.VerifySsl);
            }

            if (!success)
            {
                MessageBox.Show($"Impossible d'ajouter le remote '{info.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (info.IsGitLab && !string.IsNullOrEmpty(info.Token))
            {
                // The same token serves the GitLab Packages API (listing) and the Conan login (download/upload).
                GlobalSettings.SetGitLabTokenForRemote(info.Name, info.Token);

                if (!string.IsNullOrEmpty(info.Username))
                {
                    bool loginSuccess;
            string loginError;
            using (BusyIndicator.Show(this, "Authentification en cours (peut prendre quelques secondes)..."))
            {
                (loginSuccess, loginError) = await ConanRemoteHelper.LoginRemoteAsync(info.Name, info.Username, info.Token);
            }
                    if (!loginSuccess)
                    {
                        MessageBox.Show($"Le remote '{info.Name}' a été ajouté mais l'authentification a échoué :\n{loginError}",
                            "Conan C/C++ Package Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            await RefreshRemotesAsync();
        }
        /**
         * The "Modifier la source..." button: prompts for a new URL (SSL verification unchanged) and runs `conan remote update`.
         */
        private void EditSourceButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => EditSourceButtonClickAsync(sender, e));
        }

        private async Task EditSourceButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            string input = MessageBoxWithInput.Show(
                $"Nouvelle source (URL) pour le remote '{_selected.Name}' :",
                "Conan C/C++ Package Manager",
                _selected.Url);

            if (string.IsNullOrWhiteSpace(input) || string.Equals(input.Trim(), _selected.Url, StringComparison.Ordinal))
            {
                return;
            }

            bool success;
            string error;
            using (BusyIndicator.Show(this, "Op\u00e9ration Conan en cours..."))
            {
                (success, error) = await ConanRemoteHelper.UpdateRemoteUrlAsync(_selected.Name, input.Trim(), _selected.VerifySsl);
            }

            if (!success)
            {
                MessageBox.Show($"Impossible de mettre à jour le remote '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }

        /**
         * The "Supprimer" button: confirms, then runs `conan remote remove` (this only removes the remote configuration, never any already-downloaded packages).
         */
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => RemoveButtonClickAsync(sender, e));
        }

        private async Task RemoveButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                $"Supprimer le remote '{_selected.Name}' ? Cette action ne supprime que la configuration du remote, pas les packages déjà installés localement.",
                "Conan C/C++ Package Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            bool success;
            string error;
            using (BusyIndicator.Show(this, "Op\u00e9ration Conan en cours..."))
            {
                (success, error) = await ConanRemoteHelper.RemoveRemoteAsync(_selected.Name);
            }

            if (!success)
            {
                MessageBox.Show($"Impossible de supprimer le remote '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _selected = null;
            await RefreshRemotesAsync();
        }

        /**
         * The "Activer"/"Désactiver" button: toggles the remote via `conan remote enable`/`disable` without removing its configuration.
         */
        private void EnableDisableButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => EnableDisableButtonClickAsync(sender, e));
        }

        private async Task EnableDisableButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            bool success;
            string error;
            using (BusyIndicator.Show(this, "Op\u00e9ration Conan en cours..."))
            {
                (success, error) = _selected.Enabled
                ? await ConanRemoteHelper.DisableRemoteAsync(_selected.Name)
                : await ConanRemoteHelper.EnableRemoteAsync(_selected.Name);
            }

            if (!success)
            {
                MessageBox.Show($"Opération impossible sur '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }
    }

    /**
     * Shows a small always-on-top "please wait" window with an animated bar and disables the page
     * for the duration of a slow `conan` call (login in particular can take several seconds), so
     * the user can tell that something is happening. Dispose to hide it again.
     */
    internal sealed class BusyIndicator : IDisposable
    {
        private readonly UIElement _owner;
        private readonly System.Windows.Forms.Form _form;

        public static BusyIndicator Show(UIElement owner, string message)
        {
            return new BusyIndicator(owner, message);
        }

        private BusyIndicator(UIElement owner, string message)
        {
            _owner = owner;
            _owner.IsEnabled = false;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            _form = new System.Windows.Forms.Form
            {
                Text = "Conan C/C++ Package Manager",
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow,
                ControlBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                ClientSize = new System.Drawing.Size(380, 74)
            };
            _form.Controls.Add(new System.Windows.Forms.Label { Text = message, Left = 12, Top = 12, Width = 356, AutoSize = false });
            _form.Controls.Add(new System.Windows.Forms.ProgressBar
            {
                Left = 12,
                Top = 40,
                Width = 356,
                Height = 18,
                Style = System.Windows.Forms.ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30
            });
            _form.Show();
            _form.Refresh();
        }

        public void Dispose()
        {
            _form.Close();
            _form.Dispose();
            _owner.IsEnabled = true;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }
    /**
     * Result of AddRemoteDialog: a plain Conan remote (Name + Url) or a GitLab Package Registry
     * (Url already built from the server and project, plus the optional Username/Token).
     */
    internal sealed class NewRemoteInfo
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public bool VerifySsl { get; set; }
        public bool IsGitLab { get; set; }
        public string Username { get; set; }
        public string Token { get; set; }
    }

    /**
     * Modal WinForms prompt used to add a remote. It is a real dialog rather than inline text
     * boxes because WPF text boxes hosted in the Tools > Options page (ElementHost nested in the
     * shell's own WPF dialog) do not receive keyboard input.
     * Two kinds of remote are offered: any Conan URL, or a GitLab project whose Conan v2 URL
     * (`<server>/api/v4/projects/<id>/packages/conan`) is built from the server and project ID/path.
     */
    internal static class AddRemoteDialog
    {
        public static NewRemoteInfo Show()
        {
            var form = new System.Windows.Forms.Form
            {
                Text = "Ajouter un remote Conan",
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false
            };

            var typeLabel = new System.Windows.Forms.Label { Text = "Type :", Left = 12, Top = 15, AutoSize = true };
            var typeCombo = new System.Windows.Forms.ComboBox { Left = 190, Top = 12, Width = 340, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            typeCombo.Items.Add("Remote Conan (URL)");
            typeCombo.Items.Add("GitLab Package Registry");
            typeCombo.SelectedIndex = 0;

            var nameLabel = new System.Windows.Forms.Label { Text = "Nom :", Left = 12, Top = 48, AutoSize = true };
            var nameBox = new System.Windows.Forms.TextBox { Left = 190, Top = 45, Width = 340 };

            var urlLabel = new System.Windows.Forms.Label { Text = "Source (URL) :", Left = 12, Top = 81, AutoSize = true };
            var urlBox = new System.Windows.Forms.TextBox { Left = 190, Top = 78, Width = 340 };

            var projectLabel = new System.Windows.Forms.Label { Text = "ID ou chemin du projet :", Left = 12, Top = 114, AutoSize = true };
            var projectBox = new System.Windows.Forms.TextBox { Left = 190, Top = 111, Width = 340 };

            var userLabel = new System.Windows.Forms.Label { Text = "Utilisateur :", Left = 12, Top = 147, AutoSize = true };
            var userBox = new System.Windows.Forms.TextBox { Left = 190, Top = 144, Width = 340 };

            var tokenLabel = new System.Windows.Forms.Label { Text = "Token d'accès :", Left = 12, Top = 180, AutoSize = true };
            var tokenBox = new System.Windows.Forms.TextBox { Left = 190, Top = 177, Width = 340, UseSystemPasswordChar = true };

            var hintLabel = new System.Windows.Forms.Label
            {
                Left = 12,
                Top = 210,
                Width = 518,
                Height = 46,
                ForeColor = System.Drawing.SystemColors.GrayText,
                Text = "Token GitLab (personnel, de projet ou de déploiement) avec les scopes read_api et read_package_registry. " +
                       "Laissez l'utilisateur et le token vides pour un projet public."
            };

            var sslBox = new System.Windows.Forms.CheckBox { Text = "Vérifier SSL", Left = 190, Checked = true, AutoSize = true };
            var okButton = new System.Windows.Forms.Button { Text = "Ajouter", Width = 90, DialogResult = System.Windows.Forms.DialogResult.OK };
            var cancelButton = new System.Windows.Forms.Button { Text = "Annuler", Width = 90, DialogResult = System.Windows.Forms.DialogResult.Cancel };

            void UpdateLayout()
            {
                bool gitlab = typeCombo.SelectedIndex == 1;

                urlLabel.Text = gitlab ? "Serveur GitLab :" : "Source (URL) :";
                if (gitlab && string.IsNullOrWhiteSpace(urlBox.Text))
                {
                    urlBox.Text = "https://gitlab.com";
                }
                else if (!gitlab && urlBox.Text == "https://gitlab.com")
                {
                    urlBox.Text = string.Empty;
                }

                foreach (System.Windows.Forms.Control c in new System.Windows.Forms.Control[] { projectLabel, projectBox, userLabel, userBox, tokenLabel, tokenBox, hintLabel })
                {
                    c.Visible = gitlab;
                }

                int bottom = gitlab ? 262 : 118;
                sslBox.Top = bottom + 4;
                okButton.Top = bottom;
                okButton.Left = 345;
                cancelButton.Top = bottom;
                cancelButton.Left = 440;
                form.ClientSize = new System.Drawing.Size(542, bottom + 42);
            }

            typeCombo.SelectedIndexChanged += (s, e) => UpdateLayout();

            form.Controls.AddRange(new System.Windows.Forms.Control[]
            {
                typeLabel, typeCombo, nameLabel, nameBox, urlLabel, urlBox, projectLabel, projectBox,
                userLabel, userBox, tokenLabel, tokenBox, hintLabel, sslBox, okButton, cancelButton
            });
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;
            UpdateLayout();

            form.FormClosing += (s, e) =>
            {
                if (form.DialogResult != System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }

                bool gitlab = typeCombo.SelectedIndex == 1;
                bool missing = string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text)
                               || (gitlab && string.IsNullOrWhiteSpace(projectBox.Text));
                if (missing)
                {
                    System.Windows.Forms.MessageBox.Show(
                        gitlab ? "Le nom, le serveur et le projet GitLab sont obligatoires." : "Le nom et la source (URL) sont obligatoires.",
                        "Conan C/C++ Package Manager", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            };

            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return null;
            }

            bool isGitLab = typeCombo.SelectedIndex == 1;
            string url = urlBox.Text.Trim();

            if (isGitLab)
            {
                string project = projectBox.Text.Trim().Trim('/');
                bool numeric = project.Length > 0 && project.All(char.IsDigit);
                url = $"{url.TrimEnd('/')}/api/v4/projects/{(numeric ? project : Uri.EscapeDataString(project))}/packages/conan";
            }

            return new NewRemoteInfo
            {
                Name = nameBox.Text.Trim(),
                Url = url,
                VerifySsl = sslBox.Checked,
                IsGitLab = isGitLab,
                Username = isGitLab ? userBox.Text.Trim() : null,
                Token = isGitLab ? tokenBox.Text.Trim() : null
            };
        }
    }
    /**
     * Small WinForms prompt (username + masked password) for `conan remote login`, in the same
     * spirit as MessageBoxWithInput (ConanSharedModels.cs) but with a
     * second, masked field - a plain WPF dialog would need its own Window/theme wiring for very
     * little benefit here, since this only ever pops up modally from Tools > Options.
     */
    internal static class CredentialsDialog
    {
        public static (string username, string password)? Show(string message, string remoteName, string defaultUsername = null)
        {
            var form = new System.Windows.Forms.Form
            {
                Text = "Conan C/C++ Package Manager",
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new System.Drawing.Size(340, 170)
            };

            var messageLabel = new System.Windows.Forms.Label
            {
                Text = message,
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(310, 0),
                Left = 12,
                Top = 10
            };
            var usernameLabel = new System.Windows.Forms.Label { Text = "Utilisateur :", Left = 12, Top = 48, AutoSize = true };
            var usernameTextBox = new System.Windows.Forms.TextBox { Left = 110, Top = 45, Width = 210, Text = defaultUsername ?? string.Empty };
            var passwordLabel = new System.Windows.Forms.Label { Text = "Mot de passe :", Left = 12, Top = 78, AutoSize = true };
            var passwordTextBox = new System.Windows.Forms.TextBox { Left = 110, Top = 75, Width = 210, UseSystemPasswordChar = true };
            var okButton = new System.Windows.Forms.Button { Text = "Se connecter", Left = 145, Width = 90, Top = 115, DialogResult = System.Windows.Forms.DialogResult.OK };
            var cancelButton = new System.Windows.Forms.Button { Text = "Annuler", Left = 240, Width = 80, Top = 115, DialogResult = System.Windows.Forms.DialogResult.Cancel };

            form.Controls.Add(messageLabel);
            form.Controls.Add(usernameLabel);
            form.Controls.Add(usernameTextBox);
            form.Controls.Add(passwordLabel);
            form.Controls.Add(passwordTextBox);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            System.Windows.Forms.DialogResult result = form.ShowDialog();
            if (result != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(usernameTextBox.Text))
            {
                return null;
            }

            return (usernameTextBox.Text.Trim(), passwordTextBox.Text);
        }
    }

    /**
     * Small lookup helper used by ConanPackageSourcesControl.RefreshRemotesAsync to re-find the previously-selected remote by name after reloading the grid.
     */
    internal static class ConanRemoteCollectionExtensions
    {
        /**
         * Case-insensitive lookup by ConanRemoteHelper.ConanRemote.Name.
         */
        public static ConanRemoteHelper.ConanRemote FirstOrDefaultByName(
            this ObservableCollection<ConanRemoteHelper.ConanRemote> remotes, string name)
        {
            foreach (var remote in remotes)
            {
                if (string.Equals(remote.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return remote;
                }
            }

            return null;
        }
    }
}
